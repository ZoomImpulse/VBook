namespace VBook.App.Services;

public interface IAudioPlaybackService : IDisposable
{
    bool IsPlaying { get; }

    bool IsPaused { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    void Play(byte[] wavBytes);

    void Pause();

    void Resume();

    void Stop();
}
