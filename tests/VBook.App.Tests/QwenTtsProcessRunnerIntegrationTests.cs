using VBook.App.Services;
using VBook.Core.Generation;
using VBook.Core.Providers;
using Xunit;

namespace VBook.App.Tests;

public sealed class QwenTtsProcessRunnerIntegrationTests
{
    [QwenRuntimeFact]
    public async Task ProcessRunner_ConsumesWorkerProtocolAndReturnsCudaAudio()
    {
        var applicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAudiobookStudio");
        var python = Path.Combine(applicationDataDirectory, "qwen-runtime", "Scripts", "python.exe");
        var models = Path.Combine(
            applicationDataDirectory,
            "models",
            "Qwen3-TTS-12Hz-1.7B-VoiceDesign");
        var voiceDesignModel = Path.Combine(models, "VoiceDesign");
        var baseModel = Path.Combine(models, "Base");
        var worker = Path.Combine(FindRepositoryRoot(), "src", "VBook.App", "Services", "qwen_tts_worker.py");
        using var runner = new QwenTtsProcessRunner(python, worker, voiceDesignModel, baseModel);
        using var provider = new QwenTtsProvider(runner);

        const string referenceText = "This is a short voice identity sample.";
        var reference = await provider.GenerateVoiceDesignAsync(
            new VoiceDesignRequest(
                referenceText,
                "English",
                "A clear, natural audiobook narrator voice with measured pacing."),
            CancellationToken.None);
        var result = await provider.SynthesizeAsync(
            new SpeechRequest(
                "This test exercises Qwen through the same C sharp worker process runner used by VBook.",
                QwenTtsProvider.DesignedVoiceId,
                "English",
                "A clear, natural audiobook narrator voice with measured pacing.",
                reference.WavBytes,
                referenceText),
            CancellationToken.None);
        var audio = WavPcm.ReadPcmAudio(result.WavBytes);

        Assert.Equal(24_000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.True(audio.Data.Length > 0);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VBook.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the VBook repository root.");
    }

    private sealed class QwenRuntimeFactAttribute : FactAttribute
    {
        public QwenRuntimeFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("VBOOK_RUN_QWEN_LIVE_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set VBOOK_RUN_QWEN_LIVE_TESTS=1 to run real Qwen CUDA inference.";
            }
        }
    }
}
