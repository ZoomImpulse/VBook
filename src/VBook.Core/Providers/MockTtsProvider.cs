using System.Text;

namespace VBook.Core.Providers;

/// <summary>
/// Model-free TTS stand-in that emits a quiet tone (pitch derived from the voice ID)
/// whose length scales with the text, as a valid mono 16-bit PCM WAV.
/// </summary>
public sealed class MockTtsProvider : ITtsProvider
{
    private const int SampleRate = 16_000;
    private const double CharactersPerSecond = 15;

    public ProviderCapabilities Capabilities { get; } =
        new("Mock tone synthesizer (no model)", IsMock: true, MaxContextCharacters: 2000);

    public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds this provider's request limit.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var seconds = Math.Clamp(request.Text.Length / CharactersPerSecond, 0.2, 120);
        var sampleCount = (int)(seconds * SampleRate);
        var frequency = 180 + request.VoiceId.Sum(character => character) % 120;
        var fadeSamples = SampleRate / 100;
        var samples = new short[sampleCount];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var fade = Math.Min(1.0, Math.Min(sample, sampleCount - 1 - sample) / (double)fadeSamples);
            samples[sample] = (short)(Math.Sin(2 * Math.PI * frequency * sample / SampleRate) * 3000 * fade);
        }

        return Task.FromResult(new SpeechResult(
            EncodeWav(samples),
            TimeSpan.FromSeconds((double)sampleCount / SampleRate)));
    }

    private static byte[] EncodeWav(short[] samples)
    {
        var dataLength = samples.Length * sizeof(short);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
