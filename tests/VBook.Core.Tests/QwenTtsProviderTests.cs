using VBook.Core.Generation;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class QwenTtsProviderTests
{
    [Fact]
    public async Task Synthesize_ForwardsCanonicalVoiceLanguageAndExpressiveInstruction()
    {
        using var runner = new RecordingQwenRunner(WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250)));
        using var provider = new QwenTtsProvider(runner);

        var result = await provider.SynthesizeAsync(
            new SpeechRequest("Hello there.", "ryan", "english", "Speak warmly."),
            CancellationToken.None);

        Assert.Equal("Ryan", runner.Request!.VoiceId);
        Assert.Equal("English", runner.Request.Language);
        Assert.Equal("Speak warmly.", runner.Request.Instruction);
        Assert.Equal(TimeSpan.FromMilliseconds(250), result.Duration);
        Assert.True(provider.SupportsVoiceInstructions);
        Assert.Contains("German", provider.Languages);
        Assert.DoesNotContain("Auto", provider.Languages);
        Assert.Equal("English", provider.GetVoiceNativeLanguage("Aiden"));
        Assert.Equal("Chinese", provider.GetVoiceNativeLanguage("vivian"));
    }

    [Fact]
    public async Task Synthesize_RejectsUnsupportedVoiceBeforeStartingWorker()
    {
        using var runner = new RecordingQwenRunner(WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250)));
        using var provider = new QwenTtsProvider(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Hello.", "not-a-qwen-voice", "English"),
            CancellationToken.None));

        Assert.Null(runner.Request);
    }

    private sealed class RecordingQwenRunner(byte[] wavBytes) : IQwenTtsProcessRunner
    {
        public SpeechRequest? Request { get; private set; }

        public Task<byte[]> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(wavBytes);
        }

        public void Dispose()
        {
        }
    }
}
