using VBook.App.Services;
using Xunit;

namespace VBook.App.Tests;

public sealed class ThemePreferenceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vbook-theme-test-{Guid.NewGuid():N}");

    [Fact]
    public void ThemePreference_PersistsAndAppliesLightDarkAndSystemChoices()
    {
        var applied = new List<AppThemePreference>();
        var settingsPath = Path.Combine(_directory, "settings.json");
        var service = new AvaloniaThemePreferenceService(applied.Add, settingsPath);

        Assert.Equal(AppThemePreference.System, service.Preference);
        Assert.Equal(AppThemePreference.System, Assert.Single(applied));

        service.SetPreference(AppThemePreference.Dark);
        service.SetPreference(AppThemePreference.Light);
        service.SetPreference(AppThemePreference.System);
        var reopened = new AvaloniaThemePreferenceService(applied.Add, settingsPath);

        Assert.Equal(AppThemePreference.System, reopened.Preference);
        Assert.Null(reopened.InitializationError);
        Assert.Equal(
            [AppThemePreference.System, AppThemePreference.Dark, AppThemePreference.Light, AppThemePreference.System, AppThemePreference.System],
            applied);
    }

    [Fact]
    public void InvalidSavedPreference_FallsBackToSystemAndExposesWarning()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, "{ broken json");
        var applied = new List<AppThemePreference>();

        var service = new AvaloniaThemePreferenceService(applied.Add, settingsPath);

        Assert.Equal(AppThemePreference.System, service.Preference);
        Assert.Contains("Could not load theme preference", service.InitializationError);
        Assert.Equal(AppThemePreference.System, Assert.Single(applied));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
