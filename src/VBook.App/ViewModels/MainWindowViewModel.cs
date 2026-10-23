using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VBook.App.Services;
using VBook.Core.Books;
using VBook.Core.Projects;

namespace VBook.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const string AppTitle = "Local Audiobook Studio";

    private readonly IBookImporter _bookImporter;
    private readonly IProjectStore _projectStore;
    private readonly IFilePickerService _filePicker;
    private readonly Dictionary<string, string> _editedTextByChapter = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _autosaveCancellation;
    private Task? _autosaveTask;
    private Guid? _projectId;
    private DateTimeOffset _projectCreatedAt;
    private string _sourcePath = string.Empty;
    private bool _loadingChapterText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    private ImportedBook? _book;

    [ObservableProperty]
    private ImportedChapter? _selectedChapter;

    [ObservableProperty]
    private string _chapterText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    private RecentProject? _selectedRecentProject;

    [ObservableProperty]
    private string _statusMessage = "Import an EPUB to begin.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportEpubCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    private bool _isBusy;

    public MainWindowViewModel(
        IBookImporter bookImporter,
        IProjectStore projectStore,
        IFilePickerService filePicker)
    {
        _bookImporter = bookImporter;
        _projectStore = projectStore;
        _filePicker = filePicker;
    }

    public ObservableCollection<ImportedChapter> Chapters { get; } = [];

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public string WindowTitle => Book is null ? AppTitle : $"{Book.Title} — {AppTitle}";

    public bool HasChapter => SelectedChapter is not null;

    public string ChapterHeading => SelectedChapter?.Title ?? "No chapter selected";

    partial void OnBookChanged(ImportedBook? value) => OnPropertyChanged(nameof(WindowTitle));

    partial void OnSelectedChapterChanged(ImportedChapter? value)
    {
        OnPropertyChanged(nameof(HasChapter));
        OnPropertyChanged(nameof(ChapterHeading));
        _loadingChapterText = true;
        try
        {
            ChapterText = value is null ? string.Empty : GetChapterText(value);
        }
        finally
        {
            _loadingChapterText = false;
        }
    }

    partial void OnChapterTextChanged(string value)
    {
        if (_loadingChapterText || SelectedChapter is null)
        {
            return;
        }

        _editedTextByChapter[SelectedChapter.Id] = value;
        ScheduleAutosave();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _projectStore.InitializeAsync(cancellationToken);
            await RefreshRecentProjectsAsync(cancellationToken);
            var mostRecent = RecentProjects.FirstOrDefault();
            if (mostRecent is not null)
            {
                await LoadProjectAsync(mostRecent.Id, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not open saved projects: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportEpubAsync(CancellationToken cancellationToken)
    {
        var filePath = await _filePicker.PickEpubAsync();
        if (filePath is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reading EPUB…";
        try
        {
            await FlushPendingSaveAsync();
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            var importedBook = await Task.Run(() => _bookImporter.Import(stream), cancellationToken);

            _projectId = Guid.NewGuid();
            _projectCreatedAt = DateTimeOffset.UtcNow;
            _sourcePath = filePath;
            ShowBook(importedBook.Title, importedBook.Chapters);
            await SaveCurrentProjectCoreAsync(cancellationToken);
            await RefreshRecentProjectsAsync(cancellationToken);
            StatusMessage = $"Imported and saved {Chapters.Count} chapters from {importedBook.Title}.";
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
        catch (Exception exception)
        {
            StatusMessage = $"The book was imported, but the project could not be saved: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenRecent))]
    private async Task OpenRecentProjectAsync(CancellationToken cancellationToken)
    {
        if (SelectedRecentProject is not { } recent)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await FlushPendingSaveAsync();
            await LoadProjectAsync(recent.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not open saved project: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveProjectAsync(CancellationToken cancellationToken)
    {
        CancelPendingAutosave();
        try
        {
            await SaveCurrentProjectCoreAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not save project: {exception.Message}";
        }
    }

    public Task FlushAsync() => FlushPendingSaveAsync();

    private bool CanRun() => !IsBusy;

    private bool CanOpenRecent() => !IsBusy && SelectedRecentProject is not null;

    private bool CanSave() => !IsBusy && Book is not null;

    private string GetChapterText(ImportedChapter chapter) =>
        _editedTextByChapter.TryGetValue(chapter.Id, out var edited) ? edited : chapter.Text;

    private void ShowBook(string title, IReadOnlyList<ImportedChapter> chapters)
    {
        Book = new ImportedBook(title, chapters);
        _editedTextByChapter.Clear();
        Chapters.Clear();
        foreach (var chapter in chapters)
        {
            Chapters.Add(chapter);
        }

        SelectedChapter = Chapters.FirstOrDefault();
    }

    private async Task LoadProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _projectStore.LoadAsync(projectId, cancellationToken);
        if (project is null)
        {
            StatusMessage = "That saved project could not be found.";
            await RefreshRecentProjectsAsync(cancellationToken);
            return;
        }

        _projectId = project.Id;
        _projectCreatedAt = project.CreatedAt;
        _sourcePath = project.SourcePath;
        ShowBook(project.Title, project.Chapters);
        SelectedRecentProject = RecentProjects.FirstOrDefault(p => p.Id == project.Id);
        StatusMessage = $"Opened {project.Title}.";
    }

    private async Task SaveCurrentProjectCoreAsync(CancellationToken cancellationToken)
    {
        if (Book is null || _projectId is null)
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var project = new AudiobookProject(
                _projectId.Value,
                Book.Title,
                _sourcePath,
                _projectCreatedAt,
                DateTimeOffset.UtcNow,
                Chapters.Select(chapter => chapter with { Text = GetChapterText(chapter) }).ToArray());
            await _projectStore.SaveAsync(project, cancellationToken);
            StatusMessage = $"Saved {project.Title}.";
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ScheduleAutosave()
    {
        CancelPendingAutosave();
        var cancellation = new CancellationTokenSource();
        _autosaveCancellation = cancellation;
        StatusMessage = "Changes will be saved automatically…";
        _autosaveTask = AutosaveAfterDelayAsync(cancellation);
    }

    private async Task AutosaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token);
            await SaveCurrentProjectCoreAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"Autosave failed ({exception.GetType().Name}): {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_autosaveCancellation, cancellation))
            {
                _autosaveCancellation = null;
                _autosaveTask = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task FlushPendingSaveAsync()
    {
        var pendingTask = _autosaveTask;
        CancelPendingAutosave();
        if (pendingTask is not null)
        {
            await pendingTask;
        }

        await SaveCurrentProjectCoreAsync(CancellationToken.None);
    }

    private void CancelPendingAutosave() => _autosaveCancellation?.Cancel();

    private async Task RefreshRecentProjectsAsync(CancellationToken cancellationToken)
    {
        var recent = await _projectStore.GetRecentAsync(cancellationToken);
        var selectedId = _projectId;
        RecentProjects.Clear();
        foreach (var project in recent)
        {
            RecentProjects.Add(project);
        }

        SelectedRecentProject = RecentProjects.FirstOrDefault(p => p.Id == selectedId);
    }
}
