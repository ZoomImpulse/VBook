using VBook.App.Services;
using VBook.Core.Generation;
using Xunit;

namespace VBook.App.Tests;

public sealed class OpenAlAudioPlaybackServiceTests
{
    [Fact]
    public void PlaybackControls_WorkWithOpenAlSoftNullOutput()
    {
        var originalDriver = Environment.GetEnvironmentVariable("ALSOFT_DRIVERS");
        Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", "null");

        try
        {
            using var playback = new OpenAlAudioPlaybackService();
            playback.Play(WavPcm.CreateSilence(TimeSpan.FromSeconds(5)));

            Assert.True(playback.IsPlaying);
            playback.Pause();
            Assert.True(playback.IsPaused);

            playback.Resume();
            Assert.True(playback.IsPlaying);

            playback.Stop();
            Assert.False(playback.IsPlaying);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", originalDriver);
        }
    }
}
