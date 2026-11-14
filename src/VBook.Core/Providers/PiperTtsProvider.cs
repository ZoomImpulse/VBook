using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    private readonly IReadOnlyDictionary<string, string> _languagesByVoice;
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
        _languagesByVoice = _models.ToDictionary(
            model => model.Key,
            model => ReadLanguageName(model.Value),
            StringComparer.OrdinalIgnoreCase);
        Languages = _languagesByVoice.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> VoiceIds { get; }

    public IReadOnlyList<string> Languages { get; }

    public IReadOnlyList<string> GetLanguagesForVoice(string voiceId) =>
        _languagesByVoice.TryGetValue(voiceId, out var language) ? [language] : [];

    public ProviderCapabilities Capabilities { get; } =
        new("Piper local neural speech", IsMock: false, MaxContextCharacters: 2000);

    public async Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds Piper's request limit.", nameof(request));
        }

        if (!_models.TryGetValue(request.VoiceId, out var modelPath))
        {
            throw new ArgumentException($"Piper voice '{request.VoiceId}' is not available.", nameof(request));
        }

        if (!string.Equals(
                request.Language,
                _languagesByVoice[request.VoiceId],
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Piper voice '{request.VoiceId}' requires the language '{_languagesByVoice[request.VoiceId]}'.",
                nameof(request));
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

    private static string ReadLanguageName(string modelPath)
    {
        var configPath = modelPath + ".json";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("language", out var language) &&
                language.ValueKind == JsonValueKind.Object &&
                language.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                var languageName = name.GetString();
                if (!string.IsNullOrWhiteSpace(languageName))
                {
                    return languageName;
                }
            }

            throw new InvalidDataException(
                $"Piper voice configuration '{configPath}' does not declare a language name.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Piper voice configuration '{configPath}' is not valid JSON.",
                exception);
        }
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
