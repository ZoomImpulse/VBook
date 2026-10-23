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
}
