using VBook.Core.Generation;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class KokoroTtsProviderTests
{
    [Fact]
    public async Task SynthesizeAsync_UsesSpeakerAndReturnsValidatedPcmWav()
    {
        var engine = new RecordingEngine(new KokoroAudioSamples([-1f, -0.5f, 0f, 0.5f, 1f], 24_000));
        var factory = new RecordingEngineFactory(engine);
        using var provider = new KokoroTtsProvider(
            new KokoroModelFiles("model.onnx", "voices.bin", "tokens.txt", "lexicon.txt"),
            factory);

        var result = await provider.SynthesizeAsync(
            new SpeechRequest("Hello world.", "af_heart", "English"),
            CancellationToken.None);
        var audio = WavPcm.ReadPcmAudio(result.WavBytes);

        Assert.Equal(53, provider.VoiceIds.Count);
        Assert.Equal(3, engine.SpeakerId);
        Assert.Equal("Hello world.", engine.Text);
        Assert.Equal(["English"], factory.Languages);
        Assert.Equal(["Spanish"], provider.GetLanguagesForVoice("ef_dora"));
        Assert.DoesNotContain("Auto", provider.Languages);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(24_000, audio.SampleRate);
        Assert.Equal(16, audio.BitsPerSample);
        Assert.Equal(10, audio.Data.Length);
        Assert.Equal(TimeSpan.FromSeconds(5d / 24_000), result.Duration);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsUnknownVoiceWithoutCallingEngine()
    {
        var engine = new RecordingEngine(new KokoroAudioSamples([0.2f], 24_000));
        using var provider = new KokoroTtsProvider(
            new KokoroModelFiles("model.onnx", "voices.bin", "tokens.txt", "lexicon.txt"),
            new RecordingEngineFactory(engine));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Hello.", "missing-voice", "English"),
            CancellationToken.None));

        Assert.Null(engine.Text);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsTextOverProviderLimit()
    {
        var engine = new RecordingEngine(new KokoroAudioSamples([0.2f], 24_000));
        using var provider = new KokoroTtsProvider(
            new KokoroModelFiles("model.onnx", "voices.bin", "tokens.txt", "lexicon.txt"),
            new RecordingEngineFactory(engine));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(
            new SpeechRequest(new string('a', provider.Capabilities.MaxContextCharacters + 1), "af_heart", "English"),
            CancellationToken.None));

        Assert.Null(engine.Text);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsNonFiniteSamples()
    {
        using var provider = new KokoroTtsProvider(
            new KokoroModelFiles("model.onnx", "voices.bin", "tokens.txt", "lexicon.txt"),
            new RecordingEngineFactory(new RecordingEngine(
                new KokoroAudioSamples([float.NaN], 24_000))));

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SynthesizeAsync(
            new SpeechRequest("Hello.", "af_heart", "English"),
            CancellationToken.None));
    }

    private sealed class RecordingEngineFactory(RecordingEngine engine) : IKokoroSpeechEngineFactory
    {
        public List<string> Languages { get; } = [];

        public IKokoroSpeechEngine Create(KokoroModelFiles modelFiles, string language)
        {
            Languages.Add(language);
            return engine;
        }
    }

    private sealed class RecordingEngine(KokoroAudioSamples audio) : IKokoroSpeechEngine
    {
        public string? Text { get; private set; }
        public int? SpeakerId { get; private set; }

        public KokoroAudioSamples Synthesize(string text, int speakerId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            SpeakerId = speakerId;
            return audio;
        }

        public void Dispose()
        {
        }
    }
}
