using CommunityToolkit.Mvvm.ComponentModel;
using VBook.Core.Providers;

namespace VBook.App.ViewModels;

public sealed partial class VoiceAssignmentItem : ObservableObject
{
    public VoiceAssignmentItem(
        string speakerId,
        string speakerName,
        IReadOnlyList<string> voiceIds,
        IReadOnlyList<string> languages,
        bool supportsInstructions,
        SpeechVoiceProfile profile)
    {
        SpeakerId = speakerId;
        SpeakerName = speakerName;
        VoiceIds = voiceIds;
        Languages = languages;
        SupportsInstructions = supportsInstructions;
        _voiceId = profile.VoiceId;
        _language = profile.Language;
        _instruction = profile.Instruction;
    }

    public string SpeakerId { get; }

    public string SpeakerName { get; }

    public IReadOnlyList<string> VoiceIds { get; }

    public IReadOnlyList<string> Languages { get; }

    public bool SupportsInstructions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    private string _voiceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    private string _language;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Profile))]
    private string _instruction;

    public SpeechVoiceProfile Profile => new(VoiceId, Language, Instruction);
}
