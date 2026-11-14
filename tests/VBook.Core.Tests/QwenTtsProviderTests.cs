using VBook.Core.Generation;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class QwenTtsProviderTests
{
    [Fact]
    public async Task Synthesize_UsesDesignedVoiceAndReferenceForBaseClone()
    {
        var wavBytes = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250));
        using var runner = new RecordingQwenRunner(wavBytes);
        using var provider = new QwenTtsProvider(runner);

        var result = await provider.SynthesizeAsync(
            new SpeechRequest(
                "Hello there.",
                "designed",
                "english",
                "A clear audiobook voice.",
                wavBytes,
                "Hello, this is a voice sample."),
            CancellationToken.None);

        Assert.Equal(QwenTtsProvider.DesignedVoiceId, runner.Request!.VoiceId);
        Assert.Equal("English", runner.Request.Language);
        Assert.Equal("A clear audiobook voice.", runner.Request.Instruction);
        Assert.Same(wavBytes, runner.Request.VoiceDesignReferenceAudio);
        Assert.Equal("Hello, this is a voice sample.", runner.Request.VoiceDesignReferenceText);
        Assert.Equal(TimeSpan.FromMilliseconds(250), result.Duration);
        Assert.True(provider.SupportsVoiceInstructions);
        Assert.Contains("German", provider.Languages);
        Assert.DoesNotContain("Auto", provider.Languages);
        Assert.Null(provider.GetVoiceNativeLanguage(QwenTtsProvider.DesignedVoiceId));
        Assert.Equal([QwenTtsProvider.DesignedVoiceId], provider.VoiceIds);
    }

    [Fact]
    public async Task GenerateVoiceDesign_NormalizesLanguageAndForwardsDescription()
    {
        using var runner = new RecordingQwenRunner(WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250)));
        using var provider = new QwenTtsProvider(runner);

        var result = await provider.GenerateVoiceDesignAsync(
            new VoiceDesignRequest("Hello there.", "english", "A warm, clear narrator voice."),
            CancellationToken.None);

        Assert.Equal("Hello there.", runner.DesignRequest!.Text);
        Assert.Equal("English", runner.DesignRequest.Language);
        Assert.Equal("A warm, clear narrator voice.", runner.DesignRequest.Instruction);
        Assert.Equal(TimeSpan.FromMilliseconds(250), result.Duration);
    }

    [Fact]
    public async Task Synthesize_RequiresVoiceDesignReferenceBeforeStartingWorker()
    {
        using var runner = new RecordingQwenRunner(WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250)));
        using var provider = new QwenTtsProvider(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Hello.", QwenTtsProvider.DesignedVoiceId, "English"),
            CancellationToken.None));

        Assert.Null(runner.Request);
    }

    private sealed class RecordingQwenRunner(byte[] wavBytes) : IQwenTtsProcessRunner
    {
        public SpeechRequest? Request { get; private set; }

        public VoiceDesignRequest? DesignRequest { get; private set; }

        public Task<byte[]> GenerateVoiceDesignAsync(
            VoiceDesignRequest request,
            CancellationToken cancellationToken)
        {
            DesignRequest = request;
            return Task.FromResult(wavBytes);
        }

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
