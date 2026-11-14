using CommunityToolkit.Mvvm.ComponentModel;
using VBook.Core.Books;

namespace VBook.App.ViewModels;

public sealed partial class StructureSuggestionItem : ObservableObject
{
    public StructureSuggestionItem(
        string chapterId,
        string title,
        SectionRole suggestedRole,
        double confidence,
        string rationale)
    {
        ChapterId = chapterId;
        Title = title;
        _suggestedRole = suggestedRole;
        Confidence = confidence;
        Rationale = rationale;
    }

    public static IReadOnlyList<SectionRole> RoleChoices { get; } = Enum.GetValues<SectionRole>();

    public string ChapterId { get; }

    public string Title { get; }

    public double Confidence { get; }

    public string ConfidenceLabel => $"{Confidence:P0} confidence";

    public string Rationale { get; }

    [ObservableProperty]
    private SectionRole _suggestedRole;
}
