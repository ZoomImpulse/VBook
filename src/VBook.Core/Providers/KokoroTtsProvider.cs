using System.Buffers.Binary;
using System.Text;
using SherpaOnnx;
using VBook.Core.Generation;

namespace VBook.Core.Providers;

public sealed record KokoroAudioSamples(float[] Samples, int SampleRate);

public interface IKokoroSpeechEngine : IDisposable
{
    KokoroAudioSamples Synthesize(string text, int speakerId, CancellationToken cancellationToken);
}

public interface IKokoroSpeechEngineFactory
{
    IKokoroSpeechEngine Create(KokoroModelFiles modelFiles);
}

public sealed class KokoroSpeechEngineFactory : IKokoroSpeechEngineFactory
{
    public IKokoroSpeechEngine Create(KokoroModelFiles modelFiles)
    {
        ArgumentNullException.ThrowIfNull(modelFiles);
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = modelFiles.ModelPath;
        config.Model.Kokoro.Voices = modelFiles.VoicesPath;
        config.Model.Kokoro.Tokens = modelFiles.TokensPath;
        config.Model.Kokoro.Lexicon = modelFiles.LexiconPath;
        config.Model.Kokoro.Lang = "en-us";
        config.Model.Provider = "cpu";
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        config.Model.Debug = 0;
        return new SherpaKokoroSpeechEngine(new OfflineTts(config));
    }

    private sealed class SherpaKokoroSpeechEngine(OfflineTts engine) : IKokoroSpeechEngine
    {
        public KokoroAudioSamples Synthesize(
            string text,
            int speakerId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OfflineTtsCallbackProgress progress = (_, _, _) => cancellationToken.IsCancellationRequested ? 0 : 1;
            var audio = engine.GenerateWithCallbackProgress(text, 1.0f, speakerId, progress);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (audio.NumSamples <= 0 || audio.SampleRate <= 0)
                {
                    throw new InvalidDataException("Kokoro returned an empty audio recording.");
                }

                return new KokoroAudioSamples(audio.Samples, audio.SampleRate);
            }
            finally
            {
                audio.Dispose();
            }
        }

        public void Dispose() => engine.Dispose();
    }
}

public sealed class KokoroTtsProvider : ITtsProvider, IDisposable
{
    private static readonly IReadOnlyDictionary<string, int> Speakers =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["af_alloy"] = 0,
            ["af_aoede"] = 1,
            ["af_bella"] = 2,
            ["af_heart"] = 3,
            ["af_jessica"] = 4,
            ["af_kore"] = 5,
            ["af_nicole"] = 6,
            ["af_nova"] = 7,
            ["af_river"] = 8,
            ["af_sarah"] = 9,
            ["af_sky"] = 10,
            ["am_adam"] = 11,
            ["am_echo"] = 12,
            ["am_eric"] = 13,
            ["am_fenrir"] = 14,
            ["am_liam"] = 15,
            ["am_michael"] = 16,
            ["am_onyx"] = 17,
            ["am_puck"] = 18,
            ["am_santa"] = 19,
            ["bf_alice"] = 20,
            ["bf_emma"] = 21,
            ["bf_isabella"] = 22,
            ["bf_lily"] = 23,
            ["bm_daniel"] = 24,
            ["bm_fable"] = 25,
            ["bm_george"] = 26,
            ["bm_lewis"] = 27,
            ["ef_dora"] = 28,
            ["em_alex"] = 29,
            ["ff_siwis"] = 30,
            ["hf_alpha"] = 31,
            ["hf_beta"] = 32,
            ["hm_omega"] = 33,
            ["hm_psi"] = 34,
            ["if_sara"] = 35,
            ["im_nicola"] = 36,
            ["jf_alpha"] = 37,
            ["jf_gongitsune"] = 38,
            ["jf_nezumi"] = 39,
            ["jf_tebukuro"] = 40,
            ["jm_kumo"] = 41,
            ["pf_dora"] = 42,
            ["pm_alex"] = 43,
            ["pm_santa"] = 44,
            ["zf_xiaobei"] = 45,
            ["zf_xiaoni"] = 46,
            ["zf_xiaoxiao"] = 47,
            ["zf_xiaoyi"] = 48,
            ["zm_yunjian"] = 49,
            ["zm_yunxi"] = 50,
            ["zm_yunxia"] = 51,
            ["zm_yunyang"] = 52
        };

    private readonly IKokoroSpeechEngine _engine;
    private readonly SemaphoreSlim _generationLock = new(1, 1);

    public KokoroTtsProvider(
        KokoroModelFiles modelFiles,
        IKokoroSpeechEngineFactory? engineFactory = null)
    {
        ArgumentNullException.ThrowIfNull(modelFiles);
        _engine = (engineFactory ?? new KokoroSpeechEngineFactory()).Create(modelFiles);
        VoiceIds = Speakers.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> VoiceIds { get; }

    public ProviderCapabilities Capabilities { get; } =
        new("Kokoro-82M v1.0 int8 (local CPU)", IsMock: false, MaxContextCharacters: 900);

    public async Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds Kokoro's request limit.", nameof(request));
        }

        if (!Speakers.TryGetValue(request.VoiceId, out var speakerId))
        {
            throw new ArgumentException($"Kokoro voice '{request.VoiceId}' is not available.", nameof(request));
        }

        await _generationLock.WaitAsync(cancellationToken);
        try
        {
            var generated = await Task.Run(
                () => _engine.Synthesize(request.Text, speakerId, cancellationToken),
                cancellationToken);
            var wavBytes = CreatePcmWave(generated.Samples, generated.SampleRate);
            var audio = WavPcm.ReadPcmAudio(wavBytes);
            var durationSeconds = audio.Data.Length / (double)(audio.SampleRate * audio.BlockAlign);
            if (durationSeconds <= 0)
            {
                throw new InvalidDataException("Kokoro returned an empty WAV recording.");
            }

            return new SpeechResult(wavBytes, TimeSpan.FromSeconds(durationSeconds));
        }
        finally
        {
            _generationLock.Release();
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _generationLock.Dispose();
    }

    private static byte[] CreatePcmWave(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (samples.Length == 0)
        {
            throw new InvalidDataException("Kokoro returned an empty audio recording.");
        }

        var dataLength = checked(samples.Length * sizeof(short));
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                throw new InvalidDataException("Kokoro returned an invalid audio sample.");
            }

            var scaled = sample < 0
                ? (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32768)
                : (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767);
            writer.Write((short)scaled);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
