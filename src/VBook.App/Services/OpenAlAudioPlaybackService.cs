using Silk.NET.OpenAL;
using VBook.Core.Generation;

namespace VBook.App.Services;

public sealed unsafe class OpenAlAudioPlaybackService : IAudioPlaybackService
{
    private AL? _audio;
    private ALContext? _contextApi;
    private Device* _device;
    private Context* _context;
    private uint _buffer;
    private uint _source;
    private int _sampleRate;
    private int? _contextThreadId;
    private TimeSpan _duration;
    private bool _disposed;

    public bool IsPlaying => GetState() == SourceState.Playing;

    public bool IsPaused => GetState() == SourceState.Paused;

    public TimeSpan Position
    {
        get
        {
            VerifyContextThread();
            if (_audio is null || _source == 0 || _sampleRate == 0)
            {
                return TimeSpan.Zero;
            }

            _audio.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out var sampleOffset);
            return TimeSpan.FromSeconds(Math.Max(sampleOffset, 0) / (double)_sampleRate);
        }
    }

    public TimeSpan Duration => _duration;

    public void Play(byte[] wavBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavBytes);

        var pcm = WavPcm.ReadPcmAudio(wavBytes);
        var format = GetBufferFormat(pcm);
        EnsureContext();
        ClearPlayback();

        try
        {
            _buffer = _audio!.GenBuffers(1)[0];
            _source = _audio.GenSources(1)[0];
            _audio.BufferData(_buffer, format, pcm.Data, pcm.SampleRate);
            _audio.SetSourceProperty(_source, SourceInteger.Buffer, _buffer);
            ThrowIfAudioError();

            _sampleRate = pcm.SampleRate;
            _duration = TimeSpan.FromSeconds(
                pcm.Data.Length / (double)(pcm.BlockAlign * pcm.SampleRate));
            _audio.SourcePlay(_source);
            ThrowIfAudioError();
        }
        catch
        {
            ClearPlayback();
            throw;
        }
    }

    public void Pause()
    {
        VerifyContextThread();
        if (IsPlaying)
        {
            _audio!.SourcePause(_source);
            ThrowIfAudioError();
        }
    }

    public void Resume()
    {
        VerifyContextThread();
        if (IsPaused)
        {
            _audio!.SourcePlay(_source);
            ThrowIfAudioError();
        }
    }

    public void Stop()
    {
        VerifyContextThread();
        if (_audio is not null && _source != 0)
        {
            _audio.SourceStop(_source);
            ThrowIfAudioError();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        VerifyContextThread();
        _disposed = true;
        if (_contextApi is null)
        {
            return;
        }

        if (!_contextApi.MakeContextCurrent(_context))
        {
            throw new InvalidOperationException("Could not activate the audio context during shutdown.");
        }

        ClearPlayback();
        _contextApi.MakeContextCurrent((Context*)null);
        if (_context is not null)
        {
            _contextApi.DestroyContext(_context);
            _context = null;
        }

        if (_device is not null)
        {
            _contextApi.CloseDevice(_device);
            _device = null;
        }

        _audio?.Dispose();
        _contextApi.Dispose();
    }

    private void EnsureContext()
    {
        if (_audio is not null)
        {
            VerifyContextThread();
            return;
        }

        var contextApi = ALContext.GetApi();
        Device* device = null;
        Context* context = null;
        try
        {
            device = contextApi.OpenDevice(null);
            if (device is null)
            {
                throw new InvalidOperationException("No audio output device is available.");
            }

            context = contextApi.CreateContext(device, null);
            if (context is null)
            {
                throw new InvalidOperationException("Could not create an audio playback context.");
            }

            if (!contextApi.MakeContextCurrent(context))
            {
                throw new InvalidOperationException("Could not activate the audio playback context.");
            }

            var audio = AL.GetApi();
            _contextApi = contextApi;
            _device = device;
            _context = context;
            _audio = audio;
            _contextThreadId = Environment.CurrentManagedThreadId;
        }
        catch
        {
            contextApi.MakeContextCurrent((Context*)null);
            if (context is not null)
            {
                contextApi.DestroyContext(context);
            }

            if (device is not null)
            {
                contextApi.CloseDevice(device);
            }

            contextApi.Dispose();
            throw;
        }
    }

    private SourceState GetState()
    {
        VerifyContextThread();
        if (_audio is null || _source == 0)
        {
            return SourceState.Stopped;
        }

        _audio.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
        return (SourceState)state;
    }

    private void ClearPlayback()
    {
        if (_audio is null)
        {
            return;
        }

        if (_source != 0)
        {
            _audio.SourceStop(_source);
            ThrowIfAudioError();
            _audio.DeleteSources([_source]);
            ThrowIfAudioError();
            _source = 0;
        }

        if (_buffer != 0)
        {
            _audio.DeleteBuffers([_buffer]);
            ThrowIfAudioError();
            _buffer = 0;
        }

        _sampleRate = 0;
        _duration = TimeSpan.Zero;
    }

    private void ThrowIfAudioError()
    {
        var error = _audio!.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL audio playback failed: {error}.");
        }
    }

    private void VerifyContextThread()
    {
        if (_contextThreadId is { } ownerThreadId &&
            ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "Audio playback must be controlled from the Avalonia UI thread.");
        }
    }

    private static BufferFormat GetBufferFormat(PcmAudioData pcm) =>
        (pcm.Channels, pcm.BitsPerSample) switch
        {
            (1, 8) => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8) => BufferFormat.Stereo8,
            (2, 16) => BufferFormat.Stereo16,
            _ => throw new InvalidDataException(
                $"Playback does not support {pcm.Channels}-channel, {pcm.BitsPerSample}-bit PCM audio.")
        };
}
