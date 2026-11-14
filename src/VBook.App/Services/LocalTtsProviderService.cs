using System.ComponentModel;
using System.Text.Json;
using VBook.Core.Providers;

namespace VBook.App.Services;

public sealed class LocalTtsProviderService : ITtsProvider, INotifyPropertyChanged, IDisposable
{
    public const string KokoroProviderId = "Kokoro";
    public const string PiperProviderId = "Piper";
    public const string QwenProviderId = "Qwen3-TTS";

    private readonly KokoroModelAssetManager _kokoroAssets;
    private readonly IKokoroSpeechEngineFactory _kokoroEngineFactory;
    private readonly string? _piperExecutable;
    private readonly string? _piperVoicesDirectory;
    private readonly string _settingsPath;
    private readonly QwenTtsSetupService? _qwenSetup;
    private ITtsProvider _currentProvider;

    public LocalTtsProviderService(
        KokoroModelAssetManager kokoroAssets,
        IKokoroSpeechEngineFactory kokoroEngineFactory,
        string? piperExecutable,
        string? piperVoicesDirectory,
        string? settingsPath = null,
        QwenTtsSetupService? qwenSetup = null)
    {
        _kokoroAssets = kokoroAssets ?? throw new ArgumentNullException(nameof(kokoroAssets));
        _kokoroEngineFactory = kokoroEngineFactory ?? throw new ArgumentNullException(nameof(kokoroEngineFactory));
        _piperExecutable = piperExecutable;
        _piperVoicesDirectory = piperVoicesDirectory;
        _qwenSetup = qwenSetup;
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAudiobookStudio",
            "tts-settings.json");
        SelectedProviderId = LoadSelectedProvider();
        _currentProvider = CreateProvider(SelectedProviderId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SelectedProviderId { get; private set; }

    public IReadOnlyList<string> ProviderIds { get; } = [QwenProviderId, KokoroProviderId, PiperProviderId];

    public ProviderCapabilities Capabilities => _currentProvider.Capabilities;

    public IReadOnlyList<string> VoiceIds => _currentProvider.VoiceIds;

    public IReadOnlyList<string> Languages => _currentProvider.Languages;

    public IReadOnlyList<string> GetLanguagesForVoice(string voiceId) =>
        _currentProvider.GetLanguagesForVoice(voiceId);

    public string? GetVoiceNativeLanguage(string voiceId) =>
        _currentProvider.GetVoiceNativeLanguage(voiceId);

    public bool SupportsVoiceInstructions => _currentProvider.SupportsVoiceInstructions;

    public bool IsKokoroInstalled => _kokoroAssets.IsInstalled;

    public bool IsQwenInstalled => _qwenSetup?.IsInstalled == true;

    public string? InitializationError { get; private set; }

    public string CurrentProviderDescription
    {
        get
        {
            var description = SelectedProviderId switch
            {
                QwenProviderId when _qwenSetup is null =>
                    "Qwen3-TTS local CUDA setup is unavailable in this app configuration.",
                QwenProviderId =>
                    _qwenSetup.CurrentDescription,
                KokoroProviderId when !IsKokoroInstalled =>
                    "Download the Kokoro speech model (about 126 MiB) to enable local narration. Audio is synthesized on this device.",
                KokoroProviderId =>
                    "Kokoro synthesizes speech locally. The model was downloaded from the sherpa-onnx release and verified before installation.",
                PiperProviderId when !IsPiperConfigured =>
                    "Piper is an optional local alternative. Configure VBOOK_PIPER_EXECUTABLE and VBOOK_PIPER_VOICES_DIRECTORY with a valid Piper executable and voice folder, then restart VBook.",
                PiperProviderId =>
                    "Piper synthesizes speech locally using the configured executable and voice models.",
                _ => throw new InvalidOperationException($"Unknown TTS provider '{SelectedProviderId}'.")
            };
            return InitializationError is null ? description : $"{InitializationError} {description}";
        }
    }

    public async Task InstallAndSelectKokoroAsync(
        IProgress<KokoroModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = await _kokoroAssets.InstallAsync(progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SelectProvider(KokoroProviderId, files);
    }

    public async Task InstallAndSelectQwenAsync(
        IProgress<QwenTtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_qwenSetup is null)
        {
            throw new InvalidOperationException("The guided Qwen local setup service is not configured.");
        }

        await _qwenSetup.InstallAsync(progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SelectProvider(QwenProviderId, _kokoroAssets.TryGetInstalledFiles());
    }

    public void SelectProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!ProviderIds.Contains(providerId, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown TTS provider '{providerId}'.", nameof(providerId));
        }

        if (string.Equals(providerId, SelectedProviderId, StringComparison.Ordinal) &&
            (providerId != KokoroProviderId || VoiceIds.Count > 0))
        {
            return;
        }

        SelectProvider(providerId, _kokoroAssets.TryGetInstalledFiles());
    }

    public Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken) =>
        _currentProvider.SynthesizeAsync(request, cancellationToken);

    public void Dispose()
    {
        if (_currentProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private bool IsPiperConfigured =>
        !string.IsNullOrWhiteSpace(_piperExecutable) &&
        File.Exists(_piperExecutable) &&
        !string.IsNullOrWhiteSpace(_piperVoicesDirectory) &&
        Directory.Exists(_piperVoicesDirectory) &&
        Directory.EnumerateFiles(_piperVoicesDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
            .Any(modelPath => File.Exists(modelPath + ".json"));

    private string LoadSelectedProvider()
    {
        if (!File.Exists(_settingsPath))
        {
            return QwenProviderId;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TtsSettings>(File.ReadAllText(_settingsPath));
            if (settings is null)
            {
                throw new InvalidDataException("The saved speech provider preference is not valid.");
            }

            if ((settings.SettingsVersion ?? 1) < 2)
            {
                SaveSelectedProvider(QwenProviderId);
                return QwenProviderId;
            }

            if (!ProviderIds.Contains(settings.SelectedProviderId, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The saved speech provider preference is not valid.");
            }

            return settings.SelectedProviderId;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            InitializationError =
                $"Could not load the saved speech provider preference from '{_settingsPath}': {exception.Message}";
            return QwenProviderId;
        }
    }

    private void SelectProvider(string providerId, KokoroModelFiles? kokoroFiles)
    {
        var provider = CreateProvider(providerId, kokoroFiles);
        var saved = false;
        try
        {
            SaveSelectedProvider(providerId);
            saved = true;
        }
        finally
        {
            if (!saved && provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        var previousProvider = _currentProvider;
        _currentProvider = provider;
        SelectedProviderId = providerId;
        InitializationError = null;
        if (previousProvider is IDisposable previousDisposable)
        {
            previousDisposable.Dispose();
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProviderId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Capabilities)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoiceIds)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentProviderDescription)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKokoroInstalled)));
    }

    private ITtsProvider CreateProvider(string providerId, KokoroModelFiles? kokoroFiles = null)
    {
        switch (providerId)
        {
            case QwenProviderId when _qwenSetup?.IsInstalled == true:
                return _qwenSetup.CreateProvider();
            case QwenProviderId:
                return new UnavailableTtsProvider(
                    "Qwen3-TTS (setup required)",
                    "Set up the Qwen3-TTS model and local CUDA runtime before generating speech.");
            case KokoroProviderId:
                var files = kokoroFiles ?? _kokoroAssets.TryGetInstalledFiles();
                return files is not null
                    ? new KokoroTtsProvider(files, _kokoroEngineFactory)
                    : new UnavailableTtsProvider(
                        "Kokoro-82M (setup required)",
                        "Download the Kokoro model before generating spoken audio.");
            case PiperProviderId when IsPiperConfigured:
                return new PiperTtsProvider(_piperExecutable!, _piperVoicesDirectory!);
            case PiperProviderId:
                return new UnavailableTtsProvider(
                    "Piper (not configured)",
                    "Configure both VBOOK_PIPER_EXECUTABLE and VBOOK_PIPER_VOICES_DIRECTORY to use Piper.");
            default:
                throw new ArgumentException($"Unknown TTS provider '{providerId}'.", nameof(providerId));
        }
    }

    private void SaveSelectedProvider(string providerId)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The TTS settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new TtsSettings(providerId, 2)));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record TtsSettings(string SelectedProviderId, int? SettingsVersion = null);

    private sealed class UnavailableTtsProvider(string name, string message) : ITtsProvider
    {
        public ProviderCapabilities Capabilities { get; } = new(name, IsMock: false, MaxContextCharacters: 900);

        public IReadOnlyList<string> VoiceIds { get; } = [];

        public Task<SpeechResult> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
