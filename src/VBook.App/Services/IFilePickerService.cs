namespace VBook.App.Services;

public interface IFilePickerService
{
    /// <summary>Returns the local path of the chosen EPUB, or null if cancelled.</summary>
    Task<string?> PickEpubAsync();
}
