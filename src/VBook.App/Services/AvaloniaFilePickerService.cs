using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VBook.App.Services;

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private static readonly FilePickerFileType EpubType = new("EPUB books")
    {
        Patterns = ["*.epub"],
        MimeTypes = ["application/epub+zip"],
    };

    private static readonly FilePickerFileType WavType = new("WAV audio")
    {
        Patterns = ["*.wav"],
        MimeTypes = ["audio/wav"],
    };

    private static readonly FilePickerFileType M4bType = new("M4B audiobook")
    {
        Patterns = ["*.m4b"],
        MimeTypes = ["audio/mp4"],
    };

    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickEpubAsync()
    {
        if (_topLevel is null)
        {
            return null;
        }

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import EPUB",
            AllowMultiple = false,
            FileTypeFilter = [EpubType],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickWavSavePathAsync(string chapterTitle)
    {
        if (_topLevel is null)
        {
            return null;
        }

        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chapter audio",
            SuggestedFileName = $"{SanitizeFileName(chapterTitle)}.wav",
            DefaultExtension = "wav",
            FileTypeChoices = [WavType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickM4bSavePathAsync(string bookTitle)
    {
        if (_topLevel is null)
        {
            return null;
        }

        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export M4B audiobook",
            SuggestedFileName = $"{SanitizeFileName(bookTitle)}.m4b",
            DefaultExtension = "m4b",
            FileTypeChoices = [M4bType],
        });
        return file?.TryGetLocalPath();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var name = string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(name) ? "chapter" : name;
    }
}
