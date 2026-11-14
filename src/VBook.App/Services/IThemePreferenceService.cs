namespace VBook.App.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public interface IThemePreferenceService
{
    AppThemePreference Preference { get; }

    string? InitializationError { get; }

    void SetPreference(AppThemePreference preference);
}

public sealed class NullThemePreferenceService : IThemePreferenceService
{
    public AppThemePreference Preference { get; private set; } = AppThemePreference.System;

    public string? InitializationError => null;

    public void SetPreference(AppThemePreference preference) => Preference = preference;
}

public sealed class AvaloniaThemePreferenceService : IThemePreferenceService
{
    private readonly Action<AppThemePreference> _applyTheme;
    private readonly string _settingsPath;

    public AvaloniaThemePreferenceService(Action<AppThemePreference> applyTheme, string? settingsPath = null)
    {
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAudiobookStudio",
            "settings.json");
        Preference = LoadPreference();
        _applyTheme(Preference);
    }

    public AppThemePreference Preference { get; private set; }

    public string? InitializationError { get; private set; }

    public void SetPreference(AppThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The theme settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(new ThemeSettings(preference)));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _applyTheme(preference);
            Preference = preference;
            InitializationError = null;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private AppThemePreference LoadPreference()
    {
        if (!File.Exists(_settingsPath))
        {
            return AppThemePreference.System;
        }

        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(
                File.ReadAllText(_settingsPath));
            if (settings is null || !Enum.IsDefined(settings.Theme))
            {
                throw new InvalidDataException("The saved theme preference is not valid.");
            }

            return settings.Theme;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            InitializationError = $"Could not load theme preference from '{_settingsPath}': {exception.Message}";
            return AppThemePreference.System;
        }
    }

    private sealed record ThemeSettings(AppThemePreference Theme);
}
