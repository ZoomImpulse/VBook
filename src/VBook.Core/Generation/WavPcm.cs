using System.Buffers.Binary;
using System.Text;

namespace VBook.Core.Generation;

public static class WavPcm
{
    public static PcmAudioData ReadPcmAudio(byte[] bytes)
    {
        var wave = Parse(bytes);
        return new PcmAudioData(
            wave.Format.Channels,
            wave.Format.SampleRate,
            wave.Format.BlockAlign,
            wave.Format.BitsPerSample,
            wave.Data);
    }

    public static byte[] Concatenate(IEnumerable<byte[]> waveFiles, TimeSpan silenceBetweenFiles)
    {
        ArgumentNullException.ThrowIfNull(waveFiles);
        if (silenceBetweenFiles < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceBetweenFiles));
        }

        var waves = waveFiles.Select(Parse).ToArray();
        if (waves.Length == 0)
        {
            throw new ArgumentException("At least one WAV file is required.", nameof(waveFiles));
        }

        var format = waves[0].Format;
        if (waves.Any(wave => wave.Format != format))
        {
            throw new InvalidDataException("WAV segments use incompatible PCM formats.");
        }

        var silenceBytes = checked((int)Math.Ceiling(
            silenceBetweenFiles.TotalSeconds * format.SampleRate * format.BlockAlign));
        silenceBytes -= silenceBytes % format.BlockAlign;
        var dataLength = checked(waves.Sum(wave => wave.Data.Length) + silenceBytes * (waves.Length - 1));
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write(format.AudioFormat);
        writer.Write(format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.ByteRate);
        writer.Write(format.BlockAlign);
        writer.Write(format.BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (var index = 0; index < waves.Length; index++)
        {
            writer.Write(waves[index].Data);
            if (index < waves.Length - 1 && silenceBytes > 0)
            {
                writer.Write(new byte[silenceBytes]);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] CreateSilence(TimeSpan duration, int sampleRate = 16_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var samples = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        var data = new byte[samples * sizeof(short)];
        return Concatenate([CreateWave(data, sampleRate)], TimeSpan.Zero);
    }

    private static byte[] CreateWave(byte[] data, int sampleRate)
    {
        using var stream = new MemoryStream(44 + data.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
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
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        return stream.ToArray();
    }

    private static ParsedWave Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Audio segment is not a valid RIFF/WAVE file.");
        }

        short? audioFormat = null;
        short? channels = null;
        int? sampleRate = null;
        int? byteRate = null;
        short? blockAlign = null;
        short? bitsPerSample = null;
        byte[]? data = null;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || offset + 8L + chunkSize > bytes.Length)
            {
                throw new InvalidDataException("WAV chunk length is invalid.");
            }

            var chunk = bytes.AsSpan(offset + 8, chunkSize);
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("WAV format chunk is incomplete.");
                }

                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[8..]);
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(chunk[12..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
            }
            else if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                data = chunk.ToArray();
            }

            offset += 8 + chunkSize + (chunkSize & 1);
        }

        if (audioFormat != 1 || channels is null or <= 0 || sampleRate is null or <= 0 ||
            byteRate is null or <= 0 || blockAlign is null or <= 0 ||
            bitsPerSample is null or <= 0 || data is null)
        {
            throw new InvalidDataException("Only complete PCM WAV segments can be concatenated.");
        }

        return new ParsedWave(
            new PcmFormat(audioFormat.Value, channels.Value, sampleRate.Value, byteRate.Value,
                blockAlign.Value, bitsPerSample.Value),
            data);
    }

    private sealed record ParsedWave(PcmFormat Format, byte[] Data);

    private sealed record PcmFormat(
        short AudioFormat,
        short Channels,
        int SampleRate,
        int ByteRate,
        short BlockAlign,
        short BitsPerSample);
}

public sealed record PcmAudioData(
    short Channels,
    int SampleRate,
    short BlockAlign,
    short BitsPerSample,
    byte[] Data);
