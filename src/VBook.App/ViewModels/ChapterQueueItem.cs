using CommunityToolkit.Mvvm.ComponentModel;
using VBook.Core.Books;

namespace VBook.App.ViewModels;

public sealed partial class ChapterQueueItem(ImportedChapter chapter) : ObservableObject
{
    public ImportedChapter Chapter { get; private set; } = chapter;

    public string Id => Chapter.Id;

    public string Title => Chapter.Title;

    public string Role => Chapter.Role.ToString();

    [ObservableProperty]
    private string _status = "Needs analysis";

    public void UpdateChapter(ImportedChapter chapter)
    {
        Chapter = chapter;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Role));
    }
}
