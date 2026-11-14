using VBook.Core.Generation;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class KokoroRealModelIntegrationTests
{
    [KokoroModelFact]
    public async Task InstallAndSynthesizeAsync_DownloadsVerifiedModelAndProducesPcm()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var assets = new KokoroModelAssetManager(client, directory.Path);
        var files = await assets.InstallAsync(progress: null, CancellationToken.None);
        using var provider = new KokoroTtsProvider(files);

        var result = await provider.SynthesizeAsync(
            new SpeechRequest("This is a local Kokoro integration test.", "af_heart", "English"),
            CancellationToken.None);
        var audio = WavPcm.ReadPcmAudio(result.WavBytes);

        Assert.Equal(24_000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.True(audio.Data.Length > 0);
        Assert.True(assets.IsInstalled);
    }

    private sealed class KokoroModelFactAttribute : FactAttribute
    {
        public KokoroModelFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("VBOOK_RUN_KOKORO_LIVE_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set VBOOK_RUN_KOKORO_LIVE_TESTS=1 to download the 126 MiB model and run native inference.";
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"vbook-kokoro-live-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
