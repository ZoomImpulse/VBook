using System.ComponentModel;
using System.Diagnostics;
using VBook.Core.Generation;

namespace VBook.Core.Providers;

public interface IPiperProcessRunner
{
    Task<byte[]> SynthesizeAsync(
        string executablePath,
        string modelPath,
        string text,
        CancellationToken cancellationToken);
}

public sealed class PiperTtsProvider : ITtsProvider
{
    private readonly string _executablePath;
    private readonly IReadOnlyDictionary<string, string> _models;
    private readonly IPiperProcessRunner _processRunner;

    public PiperTtsProvider(
        string executablePath,
        string voicesDirectory,
        IPiperProcessRunner? processRunner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(voicesDirectory);
        if (!Directory.Exists(voicesDirectory))
        {
            throw new DirectoryNotFoundException($"Piper voices directory was not found: {voicesDirectory}");
        }

        _executablePath = executablePath;
        _models = Directory.EnumerateFiles(voicesDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
            .Where(modelPath => File.Exists(modelPath + ".json"))
            .ToDictionary(
                modelPath => Path.GetFileNameWithoutExtension(modelPath),
                modelPath => Path.GetFullPath(modelPath),
                StringComparer.OrdinalIgnoreCase);
        if (_models.Count == 0)
        {
            throw new InvalidOperationException(
                $"No Piper voices were found in '{voicesDirectory}'. Each voice requires matching .onnx and .onnx.json files.");
        }

        _processRunner = processRunner ?? new PiperProcessRunner();
        VoiceIds = _models.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> VoiceIds { get; }

    public ProviderCapabilities Capabilities { get; } =
        new("Piper local neural speech", IsMock: false, MaxContextCharacters: 2000);

    public async Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds Piper's request limit.", nameof(request));
        }

        if (!_models.TryGetValue(request.VoiceId, out var modelPath))
        {
            throw new ArgumentException($"Piper voice '{request.VoiceId}' is not available.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var wavBytes = await _processRunner.SynthesizeAsync(
            _executablePath,
            modelPath,
            request.Text,
            cancellationToken);
        var audio = WavPcm.ReadPcmAudio(wavBytes);
        var durationSeconds = audio.Data.Length / (double)(audio.SampleRate * audio.BlockAlign);
        if (durationSeconds <= 0)
        {
            throw new InvalidDataException("Piper returned an empty WAV recording.");
        }

        return new SpeechResult(wavBytes, TimeSpan.FromSeconds(durationSeconds));
    }
}

public sealed class PiperProcessRunner : IPiperProcessRunner
{
    public async Task<byte[]> SynthesizeAsync(
        string executablePath,
        string modelPath,
        string text,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"vbook-piper-{Guid.NewGuid():N}.wav");
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelPath);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(outputPath);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Could not start Piper executable '{executablePath}'.");
                }
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    $"Could not start Piper executable '{executablePath}'. Verify its path and installation.",
                    exception);
            }

            var errorOutput = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await errorOutput;
                throw;
            }

            var error = await errorOutput;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Piper exited with code {process.ExitCode}: {error.Trim()}");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidDataException("Piper completed without creating a WAV file.");
            }

            return await File.ReadAllBytesAsync(outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
