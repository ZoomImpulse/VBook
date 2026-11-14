using CommunityToolkit.Mvvm.ComponentModel;
using VBook.Core.Providers;

namespace VBook.App.ViewModels;

public sealed partial class VoiceAssignmentItem : ObservableObject
{
    public const string BookDefaultLanguageOption = "Book default";

    public VoiceAssignmentItem(
        string speakerId,
        string speakerName,
        IReadOnlyList<string> voiceIds,
        Func<string, string?> getVoiceNativeLanguage,
        IReadOnlyList<string> languages,
        bool supportsInstructions,
        SpeechVoiceProfile profile)
    {
        SpeakerId = speakerId;
        SpeakerName = speakerName;
        VoiceIds = voiceIds;
        _getVoiceNativeLanguage = getVoiceNativeLanguage;
        Languages = languages;
        SupportsInstructions = supportsInstructions;
        _voiceId = profile.VoiceId;
        _language = profile.Language;
        _instruction = profile.Instruction;
    }

    public string SpeakerId { get; }

    public string SpeakerName { get; }

    public IReadOnlyList<string> VoiceIds { get; }

    public IReadOnlyList<string> VoiceOptions => VoiceIds.Select(FormatVoiceOption).ToArray();

    public IReadOnlyList<string> Languages { get; private set; }

    public IReadOnlyList<string> LanguageOptions => [BookDefaultLanguageOption, .. Languages];

    public bool SupportsInstructions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    [NotifyPropertyChangedFor(nameof(VoiceSelection))]
    private string _voiceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    [NotifyPropertyChangedFor(nameof(LanguageSelection))]
    private string _language;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    private string _instruction;

    private readonly Func<string, string?> _getVoiceNativeLanguage;

    public string VoiceSelection
    {
        get => FormatVoiceOption(VoiceId);
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var selectedVoiceId = VoiceIds.FirstOrDefault(
                voiceId => string.Equals(FormatVoiceOption(voiceId), value, StringComparison.Ordinal));
            if (selectedVoiceId is null)
            {
                throw new ArgumentException($"Voice option '{value}' is not available.", nameof(value));
            }

            VoiceId = selectedVoiceId;
        }
    }

    public string LanguageSelection
    {
        get => string.IsNullOrWhiteSpace(Language) ? BookDefaultLanguageOption : Language;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (string.Equals(value, BookDefaultLanguageOption, StringComparison.Ordinal))
            {
                Language = string.Empty;
                return;
            }

            var supportedLanguage = Languages.FirstOrDefault(
                language => string.Equals(language, value, StringComparison.OrdinalIgnoreCase));
            if (supportedLanguage is null)
            {
                throw new ArgumentException($"Language '{value}' is not supported by this voice.", nameof(value));
            }

            Language = supportedLanguage;
        }
    }

    public SpeechVoiceProfile Profile => new(VoiceId, Language, Instruction);

    public void SetSupportedLanguages(IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        Languages = languages;
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(LanguageOptions));
        if (!string.IsNullOrWhiteSpace(Language) &&
            !languages.Contains(Language, StringComparer.OrdinalIgnoreCase))
        {
            Language = string.Empty;
        }
    }

    private string FormatVoiceOption(string voiceId)
    {
        var nativeLanguage = _getVoiceNativeLanguage(voiceId);
        return string.IsNullOrWhiteSpace(nativeLanguage)
            ? voiceId
            : $"{voiceId} (native: {nativeLanguage})";
    }
}
