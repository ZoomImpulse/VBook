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
        SpeechVoiceProfile profile,
        Func<string, IReadOnlyList<string>>? getVoiceLanguages = null,
        bool supportsVoiceDesign = false)
    {
        SpeakerId = speakerId;
        SpeakerName = speakerName;
        VoiceIds = voiceIds;
        _getVoiceNativeLanguage = getVoiceNativeLanguage;
        _getVoiceLanguages = getVoiceLanguages ?? (_ => languages);
        Languages = languages;
        SupportsInstructions = supportsInstructions;
        SupportsVoiceDesign = supportsVoiceDesign;
        _voiceId = profile.VoiceId;
        _language = profile.Language;
        _instruction = profile.Instruction;
        VoiceDesignReferenceAudio = profile.VoiceDesignReferenceAudio;
        VoiceDesignReferenceText = profile.VoiceDesignReferenceText;
    }

    public string SpeakerId { get; }

    public string SpeakerName { get; }

    public IReadOnlyList<string> VoiceIds { get; }

    public IReadOnlyList<string> VoiceOptions => VoiceIds.Select(FormatVoiceOption).ToArray();

    public IReadOnlyList<string> Languages { get; private set; }

    public IReadOnlyList<string> LanguageOptions => [BookDefaultLanguageOption, .. Languages];

    public bool SupportsInstructions { get; }

    public bool SupportsVoiceDesign { get; }

    public string InstructionPlaceholder => SupportsVoiceDesign
        ? "Voice description (editable; not auto-translated)"
        : "Provider instruction";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    [NotifyPropertyChangedFor(nameof(CurrentProfileSummary))]
    [NotifyPropertyChangedFor(nameof(VoiceSelection))]
    private string _voiceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    [NotifyPropertyChangedFor(nameof(CurrentProfileSummary))]
    [NotifyPropertyChangedFor(nameof(LanguageSelection))]
    private string _language;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    private string _instruction;

    private readonly Func<string, string?> _getVoiceNativeLanguage;
    private readonly Func<string, IReadOnlyList<string>> _getVoiceLanguages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    [NotifyPropertyChangedFor(nameof(ProposedVoiceSelection))]
    [NotifyPropertyChangedFor(nameof(ProposedLanguages))]
    [NotifyPropertyChangedFor(nameof(ProposedLanguageOptions))]
    private string _proposedVoiceId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    [NotifyPropertyChangedFor(nameof(ProposedLanguageSelection))]
    private string _proposedLanguage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    [NotifyPropertyChangedFor(nameof(HasProposedReference))]
    [NotifyPropertyChangedFor(nameof(IsProposedProfileReady))]
    [NotifyPropertyChangedFor(nameof(CanUseProposal))]
    private byte[]? _proposedReferenceAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    [NotifyPropertyChangedFor(nameof(HasProposedReference))]
    [NotifyPropertyChangedFor(nameof(IsProposedProfileReady))]
    [NotifyPropertyChangedFor(nameof(CanUseProposal))]
    private string? _proposedReferenceText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    private string _proposedSampleText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProposedProfile))]
    private string _proposedInstruction = string.Empty;

    [ObservableProperty]
    private bool _isDesigningVoice;

    [ObservableProperty]
    private string _recommendationMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseProposal))]
    private bool _hasProposal;

    [ObservableProperty]
    private bool _isProposalApplied;

    public IReadOnlyList<string> ProposedVoiceOptions => VoiceOptions;

    public IReadOnlyList<string> ProposedLanguages =>
        string.IsNullOrWhiteSpace(ProposedVoiceId) ? [] : _getVoiceLanguages(ProposedVoiceId);

    public IReadOnlyList<string> ProposedLanguageOptions => [BookDefaultLanguageOption, .. ProposedLanguages];

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

    public byte[]? VoiceDesignReferenceAudio { get; private set; }

    public string? VoiceDesignReferenceText { get; private set; }

    public SpeechVoiceProfile Profile =>
        new(VoiceId, Language, Instruction, VoiceDesignReferenceAudio, VoiceDesignReferenceText);

    public string ProposedVoiceSelection
    {
        get => FormatVoiceOption(ProposedVoiceId);
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var selectedVoiceId = VoiceIds.FirstOrDefault(
                voiceId => string.Equals(FormatVoiceOption(voiceId), value, StringComparison.Ordinal));
            if (selectedVoiceId is null)
            {
                throw new ArgumentException($"Voice option '{value}' is not available.", nameof(value));
            }

            ProposedVoiceId = selectedVoiceId;
        }
    }

    public string ProposedLanguageSelection
    {
        get => string.IsNullOrWhiteSpace(ProposedLanguage) ? BookDefaultLanguageOption : ProposedLanguage;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (string.Equals(value, BookDefaultLanguageOption, StringComparison.Ordinal))
            {
                ProposedLanguage = string.Empty;
                return;
            }

            var supportedLanguage = ProposedLanguages.FirstOrDefault(
                language => string.Equals(language, value, StringComparison.OrdinalIgnoreCase));
            if (supportedLanguage is null)
            {
                throw new ArgumentException($"Language '{value}' is not supported by this voice.", nameof(value));
            }

            ProposedLanguage = supportedLanguage;
        }
    }

    public bool HasProposedReference =>
        ProposedReferenceAudio is { Length: >= 44 } &&
        !string.IsNullOrWhiteSpace(ProposedReferenceText);

    public bool IsProposedProfileReady => !SupportsVoiceDesign || HasProposedReference;

    public bool CanUseProposal => HasProposal && IsProposedProfileReady;

    public SpeechVoiceProfile ProposedProfile =>
        new(ProposedVoiceId, ProposedLanguage, ProposedInstruction, ProposedReferenceAudio, ProposedReferenceText);

    public string CurrentProfileSummary => $"{VoiceId} · {LanguageSelection}";

    public void StageProposal(SpeechVoiceProfile profile, string recommendationMessage)
        => StageProposal(profile, recommendationMessage, profile.VoiceDesignReferenceText ?? string.Empty);

    public void StageProposal(
        SpeechVoiceProfile profile,
        string recommendationMessage,
        string sampleText)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recommendationMessage);
        ArgumentNullException.ThrowIfNull(sampleText);
        if (!VoiceIds.Contains(profile.VoiceId, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Voice '{profile.VoiceId}' is not available.", nameof(profile));
        }

        ClearProposedReference();
        ProposedVoiceId = profile.VoiceId;
        ProposedLanguage = ProposedLanguages.Contains(profile.Language, StringComparer.OrdinalIgnoreCase)
            ? profile.Language
            : string.Empty;
        ProposedInstruction = profile.Instruction;
        ProposedSampleText = sampleText;
        ProposedReferenceText = profile.VoiceDesignReferenceText;
        ProposedReferenceAudio = profile.VoiceDesignReferenceAudio;
        RecommendationMessage = recommendationMessage;
        HasProposal = true;
        IsProposalApplied = false;
    }

    public void SetProposedReference(byte[] wavBytes, string referenceText)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceText);
        if (wavBytes.Length < 44)
        {
            throw new ArgumentException("The voice-design reference must contain a WAV recording.", nameof(wavBytes));
        }

        ProposedSampleText = referenceText;
        ProposedReferenceText = referenceText;
        ProposedReferenceAudio = wavBytes;
    }

    public void SetVoiceDesignReference(byte[]? wavBytes, string? referenceText)
    {
        if ((wavBytes is null) != (referenceText is null))
        {
            throw new ArgumentException("Voice-design reference audio and text must be set or cleared together.");
        }

        VoiceDesignReferenceAudio = wavBytes;
        VoiceDesignReferenceText = referenceText;
        OnPropertyChanged(nameof(VoiceDesignReferenceAudio));
        OnPropertyChanged(nameof(VoiceDesignReferenceText));
        OnPropertyChanged(nameof(Profile));
    }

    public void MarkProposalApplied() => IsProposalApplied = true;

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

    partial void OnProposedVoiceIdChanged(string value)
    {
        ClearProposedReference();
        if (!string.IsNullOrWhiteSpace(ProposedLanguage) &&
            !ProposedLanguages.Contains(ProposedLanguage, StringComparer.OrdinalIgnoreCase))
        {
            ProposedLanguage = string.Empty;
        }

        IsProposalApplied = false;
    }

    partial void OnProposedLanguageChanged(string value)
    {
        ClearProposedReference();
        IsProposalApplied = false;
    }

    partial void OnProposedInstructionChanged(string value)
    {
        ClearProposedReference();
        IsProposalApplied = false;
    }

    partial void OnProposedSampleTextChanged(string value)
    {
        ClearProposedReference();
        IsProposalApplied = false;
    }

    private void ClearProposedReference()
    {
        ProposedReferenceAudio = null;
        ProposedReferenceText = null;
    }
}
