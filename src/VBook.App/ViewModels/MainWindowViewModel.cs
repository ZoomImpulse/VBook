using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VBook.Core.Books;

namespace VBook.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IBookImporter _bookImporter;
    private readonly Dictionary<string, string> _editedTextByChapter = new(StringComparer.Ordinal);
    private ImportedBook? _book;
    private ImportedChapter? _selectedChapter;
    private string _statusMessage = "Import an EPUB to begin.";
    private bool _isBusy;

    public MainWindowViewModel(IBookImporter bookImporter)
    {
        _bookImporter = bookImporter;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImportedChapter> Chapters { get; } = [];

    public ImportedBook? Book
    {
        get => _book;
        private set => SetProperty(ref _book, value);
    }

    public ImportedChapter? SelectedChapter
    {
        get => _selectedChapter;
        set => SetProperty(ref _selectedChapter, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public async Task ImportEpubAsync(string filePath, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Reading EPUB…";
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            var importedBook = await Task.Run(
                () => _bookImporter.Import(stream),
                cancellationToken);

            Book = importedBook;
            Chapters.Clear();
            _editedTextByChapter.Clear();
            foreach (var chapter in importedBook.Chapters)
            {
                Chapters.Add(chapter);
            }

            SelectedChapter = Chapters.FirstOrDefault();
            StatusMessage = $"Imported {Chapters.Count} chapters from {importedBook.Title}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Import cancelled.";
        }
        catch (InvalidDataException exception)
        {
            StatusMessage = $"Could not import EPUB: {exception.Message}";
        }
        catch (IOException exception)
        {
            StatusMessage = $"Could not read EPUB: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusMessage = $"Could not access EPUB: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void UpdateSelectedText(string text)
    {
        if (SelectedChapter is null)
        {
            return;
        }

        _editedTextByChapter[SelectedChapter.Id] = text;
        var index = -1;
        for (var chapterIndex = 0; chapterIndex < Chapters.Count; chapterIndex++)
        {
            if (string.Equals(Chapters[chapterIndex].Id, SelectedChapter.Id, StringComparison.Ordinal))
            {
                index = chapterIndex;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var updated = SelectedChapter with { Text = text };
        if (Book is not null)
        {
            var chapters = Chapters.ToArray();
            chapters[index] = updated;
            Book = Book with { Chapters = chapters };
        }
    }

    public string GetChapterText(ImportedChapter chapter)
    {
        return _editedTextByChapter.TryGetValue(chapter.Id, out var editedText)
            ? editedText
            : chapter.Text;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
