using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VBook.Core.Providers;

namespace VBook.App.Services;

public sealed class QwenTtsProcessRunner : IQwenTtsProcessRunner
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromMinutes(3);
    private const int MaximumProtocolLineLength = 16_000_000;

    private readonly string _pythonPath;
    private readonly string _workerScript;
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private Process? _process;
    private Task? _stderrDrain;
    private bool _disposed;

    public QwenTtsProcessRunner(string pythonPath, string workerScript, string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _pythonPath = Path.GetFullPath(pythonPath);
        _workerScript = Path.GetFullPath(workerScript);
        _modelDirectory = Path.GetFullPath(modelDirectory);
    }

    public async Task<byte[]> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SynthesisTimeout);
            await EnsureWorkerReadyAsync(timeout.Token);
            var process = _process
                ?? throw new InvalidOperationException("The Qwen worker is not running.");
            var requestId = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new
            {
                command = "synthesize",
                requestId,
                text = request.Text,
                voiceId = request.VoiceId,
                language = request.Language,
                instruction = request.Instruction
            });
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token);
            var response = await ReadProtocolMessageAsync(process, timeout.Token);
            var type = response.TryGetProperty("type", out var responseTypeElement)
                ? responseTypeElement.GetString()
                : null;
            if (type == "error")
            {
                var message = response.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                var trace = response.TryGetProperty("traceback", out var traceElement)
                    ? traceElement.GetString()
                    : null;
                throw new InvalidOperationException(
                    $"Qwen local inference failed: {message}{Environment.NewLine}{trace}");
            }

            var responseId = response.TryGetProperty("requestId", out var idElement)
                ? idElement.GetString()
                : null;
            if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
            {
                var responseType = response.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : "<missing>";
                throw new InvalidDataException(
                    $"The Qwen worker returned request '{responseId ?? "<missing>"}' of type '{responseType}', " +
                    $"expected request '{requestId}'.");
            }

            if (type != "audio" || !response.TryGetProperty("wavBase64", out var audioElement))
            {
                throw new InvalidDataException("The Qwen worker returned an invalid audio response.");
            }

            try
            {
                return Convert.FromBase64String(audioElement.GetString()!);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The Qwen worker returned malformed WAV data.", exception);
            }
        }
        catch (OperationCanceledException)
        {
            await StopWorkerAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Qwen inference exceeded the three-minute request timeout.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            await StopWorkerAsync();
            throw new InvalidOperationException(
                $"The local Qwen worker stopped or returned an invalid response: {exception.Message} {GetDiagnostics()}",
                exception);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _requestLock.Wait();
        try
        {
            _disposed = true;
            StopWorkerAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _requestLock.Release();
            _requestLock.Dispose();
        }
    }

    private async Task EnsureWorkerReadyAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        if (!File.Exists(_pythonPath) || !File.Exists(_workerScript) || !Directory.Exists(_modelDirectory))
        {
            throw new InvalidOperationException("The installed Qwen Python runtime, worker, or model is missing.");
        }

        var startInfo = new ProcessStartInfo(_pythonPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_workerScript)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.ArgumentList.Add(_workerScript);
        startInfo.ArgumentList.Add(_modelDirectory);
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the local Qwen Python worker.");
        }

        _process = process;
        _stderrDrain = DrainStderrAsync(process);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            var ready = await ReadProtocolMessageAsync(process, timeout.Token);
            var type = ready.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "startupError")
            {
                throw new InvalidOperationException(
                    $"Qwen could not load its CUDA model: {ready.GetProperty("message").GetString()}. " +
                    GetDiagnostics());
            }

            if (type != "ready")
            {
                throw new InvalidDataException("The Qwen worker did not report readiness.");
            }

            var gpu = ready.TryGetProperty("gpu", out var gpuElement) ? gpuElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(gpu))
            {
                throw new InvalidDataException("The Qwen worker did not report an available CUDA GPU.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopWorkerAsync();
            throw new TimeoutException("The Qwen model did not load within three minutes.");
        }
        catch
        {
            await StopWorkerAsync();
            throw;
        }
    }

    private async Task<JsonElement> ReadProtocolMessageAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > MaximumProtocolLineLength)
            {
                throw new InvalidDataException("The Qwen worker exceeded the protocol size limit.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                RecordDiagnostic($"Ignored non-protocol worker output: {line}");
            }
        }

        throw new InvalidDataException(
            $"The Qwen worker exited with code {(process.HasExited ? process.ExitCode : -1)}.");
    }

    private async Task DrainStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            RecordDiagnostic(line);
        }
    }

    private void RecordDiagnostic(string line)
    {
        _diagnostics.Enqueue(line.Length > 1000 ? line[..1000] : line);
        while (_diagnostics.Count > 20)
        {
            _diagnostics.TryDequeue(out _);
        }
    }

    private async Task StopWorkerAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.StandardInput.WriteLineAsync(
                        """{"command":"close"}""".AsMemory(),
                        timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or IOException or InvalidOperationException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }

            if (_stderrDrain is not null)
            {
                await _stderrDrain;
            }
        }
        finally
        {
            process.Dispose();
            _stderrDrain = null;
        }
    }

    private string GetDiagnostics()
    {
        var lines = _diagnostics.ToArray();
        return lines.Length == 0
            ? string.Empty
            : $"Worker diagnostics: {string.Join(" | ", lines)}";
    }
}
