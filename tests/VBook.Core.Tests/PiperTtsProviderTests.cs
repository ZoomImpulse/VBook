using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class PiperTtsProviderTests
{
    [Fact]
    public async Task SynthesizeAsync_UsesSelectedVoiceAndReturnsValidatedPcmDuration()
    {
        using var directory = new TemporaryDirectory();
        var modelPath = directory.AddVoice("en_US-test-medium");
        var audio = await new MockTtsProvider().SynthesizeAsync(
            new SpeechRequest("Spoken text.", "mock-warm", "English"),
            CancellationToken.None);
        var runner = new RecordingRunner(audio.WavBytes);
        var provider = new PiperTtsProvider("piper", directory.Path, runner);

        var result = await provider.SynthesizeAsync(
            new SpeechRequest("Spoken text.", "en_US-test-medium", "English"),
            CancellationToken.None);

        Assert.Equal([Path.GetFileNameWithoutExtension(modelPath)], provider.VoiceIds);
        Assert.Equal("piper", runner.ExecutablePath);
        Assert.Equal(modelPath, runner.ModelPath);
        Assert.Equal("Spoken text.", runner.Text);
        Assert.Equal(audio.WavBytes, result.WavBytes);
        Assert.Equal(audio.Duration, result.Duration);
        Assert.False(provider.Capabilities.IsMock);
    }

    [Fact]
    public void Constructor_RequiresVoiceModelAndMatchingConfiguration()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "incomplete.onnx"), string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new PiperTtsProvider("piper", directory.Path));

        Assert.Contains("matching .onnx and .onnx.json", exception.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsUnavailableVoice()
    {
        using var directory = new TemporaryDirectory();
        directory.AddVoice("en_US-test-medium");
        var runner = new RecordingRunner([]);
        var provider = new PiperTtsProvider("piper", directory.Path, runner);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Text.", "missing-voice", "English"),
            CancellationToken.None));
        Assert.Null(runner.ModelPath);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsLanguageThatDoesNotMatchVoice()
    {
        using var directory = new TemporaryDirectory();
        directory.AddVoice("en_US-test-medium");
        var runner = new RecordingRunner([]);
        var provider = new PiperTtsProvider("piper", directory.Path, runner);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Text.", "en_US-test-medium", "Spanish"),
            CancellationToken.None));

        Assert.Null(runner.ModelPath);
        Assert.Equal(["English"], provider.GetLanguagesForVoice("en_US-test-medium"));
        Assert.DoesNotContain("Auto", provider.Languages);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsInvalidPiperAudio()
    {
        using var directory = new TemporaryDirectory();
        directory.AddVoice("en_US-test-medium");
        var provider = new PiperTtsProvider("piper", directory.Path, new RecordingRunner([1, 2, 3]));

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Text.", "en_US-test-medium", "English"),
            CancellationToken.None));
    }

    private sealed class RecordingRunner(byte[] wavBytes) : IPiperProcessRunner
    {
        public string? ExecutablePath { get; private set; }
        public string? ModelPath { get; private set; }
        public string? Text { get; private set; }

        public Task<byte[]> SynthesizeAsync(
            string executablePath,
            string modelPath,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutablePath = executablePath;
            ModelPath = modelPath;
            Text = text;
            return Task.FromResult(wavBytes);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"vbook-piper-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string AddVoice(string voiceId)
        {
            var modelPath = System.IO.Path.Combine(Path, $"{voiceId}.onnx");
            File.WriteAllText(modelPath, string.Empty);
            File.WriteAllText(modelPath + ".json", """{"language":{"name":"English"}}""");
            return modelPath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
