namespace VBook.App.Services;

public interface IFilePickerService
{
    /// <summary>Returns the local path of the chosen EPUB, or null if cancelled.</summary>
    Task<string?> PickEpubAsync();

    /// <summary>Returns a WAV export path, or null if the save dialog was cancelled.</summary>
    Task<string?> PickWavSavePathAsync(string chapterTitle);

    /// <summary>Returns an M4B export path, or null if the save dialog was cancelled.</summary>
    Task<string?> PickM4bSavePathAsync(string bookTitle);
}
