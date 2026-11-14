using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using VBook.Core.Narrative;

namespace VBook.App.ViewModels;

public sealed class SpeechEditItem
{
    public SpeechEditItem(
        SpeechEditSuggestion suggestion,
        Func<SpeechEditItem, Task> accept,
        Func<SpeechEditItem, Task> reject)
    {
        Id = suggestion.Id;
        OriginalText = suggestion.OriginalText;
        ReplacementText = string.IsNullOrEmpty(suggestion.ReplacementText)
            ? "(omit this passage)"
            : suggestion.ReplacementText;
        Rationale = suggestion.Rationale;
        Status = suggestion.Status;
        AcceptCommand = new AsyncRelayCommand(() => accept(this));
        RejectCommand = new AsyncRelayCommand(() => reject(this));
    }

    public Guid Id { get; }

    public string OriginalText { get; }

    public string ReplacementText { get; }

    public string Rationale { get; }

    public SpeechEditStatus Status { get; }

    public ICommand AcceptCommand { get; }

    public ICommand RejectCommand { get; }

    public string StatusLabel => Status switch
    {
        SpeechEditStatus.Accepted => "Accepted",
        SpeechEditStatus.Rejected => "Rejected",
        _ => "Pending review"
    };
}
