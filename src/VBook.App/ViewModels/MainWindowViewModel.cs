using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VBook.App.Services;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;

namespace VBook.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const string AppTitle = "Local Audiobook Studio";
    private static readonly string[] WorkflowStepTitles =
    [
        "Prepare",
        "Structure",
        "Produce",
        "Publish"
    ];
    private static readonly string[] WorkflowStepDescriptions =
    [
        "Inspect your source text and confirm the imported chapters before preparing the book.",
        "Review the book outline and chapter roles. Nothing changes until you apply them.",
        "Work through chapters that need attention, review narration, then generate audio.",
        "Check readiness, resume or generate the full book, then export your audiobook."
    ];

    private readonly IBookImporter _bookImporter;
    private readonly IProjectStore _projectStore;
    private readonly IFilePickerService _filePicker;
    private readonly IThemePreferenceService _themePreferences;
    private readonly INarrativeWorker _narrativeWorker;
    private readonly INarrativeStore _narrativeStore;
    private readonly IGenerationStore _generationStore;
    private readonly ITtsProvider _ttsProvider;
    private readonly AudioGenerationService _audioGeneration;
    private readonly BookStructureAnalyzer _bookStructureAnalyzer;
    private readonly Dictionary<string, string> _editedTextByChapter = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _autosaveCancellation;
    private Task? _autosaveTask;
    private Guid? _projectId;
    private DateTimeOffset _projectCreatedAt;
    private string _sourcePath = string.Empty;
    private bool _loadingChapterText;
    private bool _loadingProjectSettings;
    private bool _updatingThemeMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    private ImportedBook? _book;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuggestVoiceInstructionCommand))]
    private ImportedChapter? _selectedChapter;

    [ObservableProperty]
    private string _chapterText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecentProjectCommand))]
    private RecentProject? _selectedRecentProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteProjectCommand))]
    private RecentProject? _projectPendingDelete;

    [ObservableProperty]
    private string _statusMessage = "Import an EPUB to begin.";

    [ObservableProperty]
    private string _themeMode = AppThemePreference.System.ToString();

    [ObservableProperty]
    private int _selectedWorkflowStepIndex;

    [ObservableProperty]
    private ChapterQueueItem? _selectedQueueItem;

    [ObservableProperty]
    private string _queueSearchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportEpubCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeBookStructureCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStructureSuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBookAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadKokoroCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetupQwenCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuggestVoiceInstructionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeTtsProvider))]
    [NotifyPropertyChangedFor(nameof(CanSetupQwen))]
    private bool _isBusy;

    public MainWindowViewModel(
        IBookImporter bookImporter,
        IProjectStore projectStore,
        IFilePickerService filePicker,
        INarrativeWorker narrativeWorker,
        INarrativeStore narrativeStore,
        IGenerationStore generationStore,
        ITtsProvider ttsProvider,
        AudioGenerationService audioGeneration,
        BookAudioGenerationService bookAudioGeneration,
        IM4bExporter m4bExporter,
        BookStructureAnalyzer bookStructureAnalyzer,
        IAudioPlaybackService audioPlayback,
        IThemePreferenceService? themePreferences = null)
    {
        _bookImporter = bookImporter;
        _projectStore = projectStore;
        _filePicker = filePicker;
        _themePreferences = themePreferences ?? new NullThemePreferenceService();
        _narrativeWorker = narrativeWorker;
        _narrativeStore = narrativeStore;
        _generationStore = generationStore;
        _ttsProvider = ttsProvider;
        if (_ttsProvider is LocalTtsProviderService localTtsProvider)
        {
            localTtsProvider.PropertyChanged += OnLocalTtsProviderPropertyChanged;
        }

        _audioGeneration = audioGeneration;
        _bookAudioGeneration = bookAudioGeneration;
        _m4bExporter = m4bExporter;
        _bookStructureAnalyzer = bookStructureAnalyzer;
        _audioPlayback = audioPlayback;
        _audioPlaybackTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _audioPlaybackTimer.Tick += OnAudioPlaybackTimerTick;
        GenerateChapterCommand.CanExecuteChanged += (_, _) => GenerateSegmentCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<ImportedChapter> Chapters { get; } = [];

    public ObservableCollection<ChapterQueueItem> ChapterQueue { get; } = [];

    public ObservableCollection<ChapterQueueItem> VisibleChapterQueue { get; } = [];

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public IReadOnlyList<string> ThemeModes { get; } =
        Enum.GetNames<AppThemePreference>();

    public string? ThemePreferenceWarning => _themePreferences.InitializationError;

    public bool HasThemePreferenceWarning => ThemePreferenceWarning is not null;

    public bool IsDeleteConfirmationVisible => ProjectPendingDelete is not null;

    public string WindowTitle => Book is null ? AppTitle : $"{Book.Title} — {AppTitle}";

    public bool HasChapter => SelectedChapter is not null;

    public bool HasNoChapter => !HasChapter;

    public bool HasBookWithoutSelectedChapter => Book is not null && !HasChapter;

    public bool HasNoBook => Book is null;

    public bool CanEditNarrationContent => HasChapter && !IsBusy && !IsGenerating;

    public string ChapterHeading => SelectedChapter?.Title ?? "No chapter selected";

    public int WorkflowStepNumber => SelectedWorkflowStepIndex + 1;

    public int WorkflowStepCount => WorkflowStepTitles.Length;

    public string WorkflowStepTitle => WorkflowStepTitles[SelectedWorkflowStepIndex];

    public string WorkflowStepDescription => WorkflowStepDescriptions[SelectedWorkflowStepIndex];

    public string WorkflowProgressLabel => $"STEP {WorkflowStepNumber} OF {WorkflowStepCount}";

    public double WorkflowProgress => (double)WorkflowStepNumber / WorkflowStepCount;

    public string ContinueWorkflowLabel => SelectedWorkflowStepIndex < WorkflowStepCount - 1
        ? $"Continue to {WorkflowStepTitles[SelectedWorkflowStepIndex + 1]}"
        : "Final step";

    public bool CanContinueWorkflow => HasChapter && SelectedWorkflowStepIndex < WorkflowStepCount - 1;

    public int NarratableChapterCount => ChapterQueue.Count(item => item.Chapter.Role != SectionRole.Skip);

    public int AudioReadyChapterCount => ChapterQueue.Count(item => item.Status == "Audio ready");

    public bool IsQueueSearchEmpty => ChapterQueue.Count > 0 && VisibleChapterQueue.Count == 0;

    public string BookReadinessSummary => NarratableChapterCount == 0
        ? "Import a book to build your production queue."
        : $"{AudioReadyChapterCount} of {NarratableChapterCount} narratable chapters have audio.";

    public double BookAudioReadiness => NarratableChapterCount == 0
        ? 0
        : (double)AudioReadyChapterCount / NarratableChapterCount;

    partial void OnBookChanged(ImportedBook? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HasNoBook));
        OnPropertyChanged(nameof(HasBookWithoutSelectedChapter));
        OnPropertyChanged(nameof(ChapterAnalysisGuidance));
    }

    partial void OnSelectedQueueItemChanged(ChapterQueueItem? value)
    {
        if (value is not null && SelectedChapter?.Id != value.Id)
        {
            SelectedChapter = Chapters.FirstOrDefault(chapter => chapter.Id == value.Id);
        }
    }

    partial void OnQueueSearchTextChanged(string value) => RefreshVisibleChapterQueue();

    partial void OnSelectedWorkflowStepIndexChanged(int value)
    {
        var index = Math.Clamp(value, 0, WorkflowStepCount - 1);
        if (index != value)
        {
            SelectedWorkflowStepIndex = index;
            return;
        }

        OnPropertyChanged(nameof(WorkflowStepNumber));
        OnPropertyChanged(nameof(WorkflowStepTitle));
        OnPropertyChanged(nameof(WorkflowStepDescription));
        OnPropertyChanged(nameof(WorkflowProgressLabel));
        OnPropertyChanged(nameof(WorkflowProgress));
        OnPropertyChanged(nameof(ContinueWorkflowLabel));
        OnPropertyChanged(nameof(CanContinueWorkflow));
        ContinueWorkflowCommand.NotifyCanExecuteChanged();
    }

    partial void OnThemeModeChanged(string value)
    {
        if (_updatingThemeMode ||
            !Enum.TryParse<AppThemePreference>(value, ignoreCase: true, out var preference) ||
            !Enum.IsDefined(preference))
        {
            return;
        }

        try
        {
            _themePreferences.SetPreference(preference);
            OnPropertyChanged(nameof(ThemePreferenceWarning));
            OnPropertyChanged(nameof(HasThemePreferenceWarning));
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not save theme preference: {exception.Message}";
            _updatingThemeMode = true;
            ThemeMode = _themePreferences.Preference.ToString();
            _updatingThemeMode = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectChapter));
        OnPropertyChanged(nameof(CanChangeNarrationMode));
        OnPropertyChanged(nameof(CanEditNarrationContent));
        OnPropertyChanged(nameof(ChapterAnalysisGuidance));
        CreateVoiceProposalsCommand.NotifyCanExecuteChanged();
        ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
        PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAnalyzingStructureChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeNarrationMode));
    }

    partial void OnProjectPendingDeleteChanged(RecentProject? value) =>
        OnPropertyChanged(nameof(IsDeleteConfirmationVisible));

    partial void OnSelectedChapterChanged(ImportedChapter? value)
    {
        ClearGeneratedAudioForChapterChange();
        OnPropertyChanged(nameof(HasChapter));
        OnPropertyChanged(nameof(HasNoChapter));
        OnPropertyChanged(nameof(HasBookWithoutSelectedChapter));
        OnPropertyChanged(nameof(ChapterHeading));
        OnPropertyChanged(nameof(ChapterAnalysisGuidance));
        OnPropertyChanged(nameof(CanContinueWorkflow));
        ContinueWorkflowCommand.NotifyCanExecuteChanged();
        if (SelectedQueueItem?.Id != value?.Id)
        {
            SelectedQueueItem = value is null
                ? null
                : ChapterQueue.FirstOrDefault(item => item.Id == value.Id);
        }

        _loadingChapterText = true;
        try
        {
            ChapterText = value is null ? string.Empty : GetChapterText(value);
        }
        finally
        {
            _loadingChapterText = false;
        }

        _ = LoadAnalysisForSelectionAsync();
        UpdateSelectedQueueStatus();
    }

    partial void OnChapterTextChanged(string value)
    {
        if (_loadingChapterText || SelectedChapter is null)
        {
            return;
        }

        _editedTextByChapter[SelectedChapter.Id] = value;
        ClearGeneratedBookAudio();
        OnPropertyChanged(nameof(IsSpeechReviewStale));
        RefreshAnalysisStaleness(SelectedChapter);
        UpdateSelectedQueueStatus();
        ScheduleAutosave();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThemeMode = _themePreferences.Preference.ToString();
        if (_themePreferences.InitializationError is { } themeError)
        {
            StatusMessage = themeError;
        }

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
            await RefreshChapterQueueAsync(cancellationToken);
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

    [RelayCommand(CanExecute = nameof(CanDeleteRecentProject))]
    private void DeleteRecentProject() => ProjectPendingDelete = SelectedRecentProject;

    [RelayCommand]
    private void CancelDeleteProject() => ProjectPendingDelete = null;

    [RelayCommand(CanExecute = nameof(CanConfirmDeleteProject))]
    private async Task ConfirmDeleteProjectAsync(CancellationToken cancellationToken)
    {
        if (ProjectPendingDelete is not { } project)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var isOpenProject = _projectId == project.Id;
            if (isOpenProject)
            {
                await FlushPendingSaveAsync();
            }

            if (!await _projectStore.DeleteAsync(project.Id, cancellationToken))
            {
                StatusMessage = $"Project '{project.Title}' was already deleted or could not be found.";
                return;
            }

            if (isOpenProject)
            {
                ClearOpenProject();
            }

            ProjectPendingDelete = null;
            await RefreshRecentProjectsAsync(cancellationToken);
            StatusMessage = $"Deleted project '{project.Title}'. The source EPUB was not deleted.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not delete project '{project.Title}': {exception.Message}";
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

    private bool CanRun() => !IsBusy && !IsAnalyzing && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanContinueWorkflow))]
    private void ContinueWorkflow()
    {
        if (CanContinueWorkflow)
        {
            SelectedWorkflowStepIndex++;
        }
    }

    private bool CanOpenRecent() => !IsBusy && !IsAnalyzing && !IsGenerating && SelectedRecentProject is not null;

    private bool CanDeleteRecentProject() => !IsBusy && !IsAnalyzing && !IsGenerating && SelectedRecentProject is not null;

    private bool CanConfirmDeleteProject() => !IsBusy && !IsAnalyzing && !IsGenerating && ProjectPendingDelete is not null;

    private bool CanSave() => !IsBusy && !IsGenerating && Book is not null;

    public bool CanSelectChapter => !IsBusy && !IsAnalyzing && !IsGenerating;

    public async Task RefreshChapterQueueAsync(CancellationToken cancellationToken = default)
    {
        var refreshVersion = Interlocked.Increment(ref _chapterQueueRefreshVersion);
        var projectId = _projectId;
        var chapters = Chapters.ToArray();
        if (projectId is null)
        {
            if (refreshVersion != Volatile.Read(ref _chapterQueueRefreshVersion))
            {
                return;
            }

            ChapterQueue.Clear();
            VisibleChapterQueue.Clear();
            OnPropertyChanged(nameof(NarratableChapterCount));
            OnPropertyChanged(nameof(AudioReadyChapterCount));
            OnPropertyChanged(nameof(BookReadinessSummary));
            OnPropertyChanged(nameof(BookAudioReadiness));
            OnPropertyChanged(nameof(IsQueueSearchEmpty));
            return;
        }

        if (_generationStore is not IGenerationQueueStore queueStore)
        {
            throw new InvalidOperationException(
                "The configured generation store does not expose saved job status for the production queue.");
        }

        var jobs = await queueStore.LoadGenerationJobsAsync(projectId.Value, cancellationToken);
        var savedProfiles = await _generationStore.LoadVoiceProfilesAsync(
            projectId.Value,
            CurrentTtsProviderId,
            cancellationToken);
        var legacyAssignments = await _generationStore.LoadVoiceAssignmentsAsync(projectId.Value, cancellationToken);
        var defaultLanguage = BookLanguage;
        var jobsByChapter = jobs
            .GroupBy(job => job.ChapterId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        using var concurrencyGate = new SemaphoreSlim(4);
        var states = await Task.WhenAll(chapters.Select(async chapter =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                var analysis = await _narrativeStore.LoadAnalysisAsync(projectId.Value, chapter.Id, cancellationToken);
                var review = await _narrativeStore.LoadSpeechReviewAsync(projectId.Value, chapter.Id, cancellationToken);
                var text = GetChapterText(chapter);
                var mode = UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator;
                var voiceProfiles = BuildVoiceProfiles(analysis, savedProfiles, legacyAssignments);
                var languageReady = _audioGeneration.TryResolveEffectiveVoiceProfiles(
                    voiceProfiles,
                    defaultLanguage,
                    out var effectiveVoiceProfiles,
                    out _);
                var generationFingerprint = analysis is null || !languageReady
                    ? null
                    : AudioGenerationService.ComputeGenerationFingerprint(
                        _ttsProvider.Capabilities.Name,
                        analysis,
                        review,
                        effectiveVoiceProfiles);
                var status = GetChapterQueueStatus(
                    chapter,
                    analysis,
                    review,
                    jobsByChapter.GetValueOrDefault(chapter.Id) ?? [],
                    text,
                    generationFingerprint,
                    mode,
                    _ttsProvider.VoiceIds.Count > 0,
                    languageReady);
                return (chapter, status);
            }
            finally
            {
                concurrencyGate.Release();
            }
        }));

        if (refreshVersion != Volatile.Read(ref _chapterQueueRefreshVersion) ||
            _projectId != projectId)
        {
            return;
        }

        ChapterQueue.Clear();
        foreach (var (chapter, status) in states)
        {
            var item = new ChapterQueueItem(chapter) { Status = status };
            ChapterQueue.Add(item);
        }

        RefreshVisibleChapterQueue();
        SelectedQueueItem = SelectedChapter is null
            ? null
            : VisibleChapterQueue.FirstOrDefault(item => item.Id == SelectedChapter.Id);
        OnPropertyChanged(nameof(NarratableChapterCount));
        OnPropertyChanged(nameof(AudioReadyChapterCount));
        OnPropertyChanged(nameof(BookReadinessSummary));
        OnPropertyChanged(nameof(BookAudioReadiness));
        OnPropertyChanged(nameof(IsQueueSearchEmpty));
    }

    private void RefreshVisibleChapterQueue()
    {
        var search = QueueSearchText.Trim();
        var filtered = ChapterQueue.Where(item =>
            search.Length == 0 ||
            item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Status.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleChapterQueue.Clear();
        foreach (var item in filtered)
        {
            VisibleChapterQueue.Add(item);
        }

        if (SelectedChapter is not null)
        {
            SelectedQueueItem = VisibleChapterQueue.FirstOrDefault(item => item.Id == SelectedChapter.Id);
        }

        OnPropertyChanged(nameof(IsQueueSearchEmpty));
    }

    private static string GetChapterQueueStatus(
        ImportedChapter chapter,
        ChapterAnalysis? analysis,
        SpeechEditReview? review,
        IReadOnlyList<GenerationJob> jobs,
        string text,
        string? generationFingerprint,
        NarrationMode mode,
        bool speechEngineReady,
        bool languageReady)
    {
        if (chapter.Role == SectionRole.Skip)
        {
            return "Skipped";
        }

        if (!speechEngineReady)
        {
            return "Speech engine setup required";
        }

        if (analysis is null || !analysis.Matches(text, mode, chapter.Role))
        {
            return "Needs analysis";
        }

        if (!languageReady)
        {
            return "Select voice language";
        }

        var job = generationFingerprint is null
            ? null
            : jobs.FirstOrDefault(candidate =>
                candidate.SourceHash == generationFingerprint &&
                candidate.NarrationMode == mode);
        if (job is not null)
        {
            switch (job.Status)
            {
                case GenerationJobStatus.Completed:
                    return "Audio ready";
                case GenerationJobStatus.Failed:
                    return "Failed";
                case GenerationJobStatus.Paused:
                    return "Paused · resumable";
                case GenerationJobStatus.Running:
                    return "Generating";
            }
        }

        return review?.Suggestions.Any(suggestion => suggestion.Status == SpeechEditStatus.Pending) == true
            ? "Needs narration review"
            : "Ready to generate";
    }

    private void UpdateSelectedQueueStatus()
    {
        var chapter = SelectedChapter;
        var item = chapter is null
            ? null
            : ChapterQueue.FirstOrDefault(queueItem => queueItem.Id == chapter.Id);
        if (chapter is null || item is null)
        {
            return;
        }

        if (item.Status == "Audio ready" && !IsAnalysisStale && HasAnalysis)
        {
            return;
        }

        item.Status = chapter.Role == SectionRole.Skip
            ? "Skipped"
            : IsAnalysisStale || !HasAnalysis
                ? "Needs analysis"
                : SpeechEdits.Any(edit => edit.Status == SpeechEditStatus.Pending)
                    ? "Needs narration review"
                    : "Ready to generate";
        RefreshVisibleChapterQueue();
        OnPropertyChanged(nameof(AudioReadyChapterCount));
        OnPropertyChanged(nameof(BookReadinessSummary));
        OnPropertyChanged(nameof(BookAudioReadiness));
    }

    private string GetChapterText(ImportedChapter chapter) =>
        _editedTextByChapter.TryGetValue(chapter.Id, out var edited) ? edited : chapter.Text;

    private void ShowBook(
        string title,
        IReadOnlyList<ImportedChapter> chapters,
        NarrationMode? narrationMode = null,
        BookStructureResult? structureAnalysis = null,
        string? defaultLanguage = null)
    {
        ClearGeneratedBookAudio();
        Book = new ImportedBook(title, chapters);
        SelectedWorkflowStepIndex = 0;
        ChapterQueue.Clear();
        RestoreStructureAnalysis(structureAnalysis, chapters);
        _editedTextByChapter.Clear();
        Chapters.Clear();
        foreach (var chapter in chapters)
        {
            Chapters.Add(chapter);
            ChapterQueue.Add(new ChapterQueueItem(chapter));
        }

        RefreshVisibleChapterQueue();
        _loadingProjectSettings = true;
        try
        {
            BookLanguage = defaultLanguage ?? string.Empty;
            UseMultipleVoices = (narrationMode ??
                NarrationModeDetector.Suggest(string.Join("\n\n", chapters.Select(c => c.Text))))
                == NarrationMode.MultiVoice;
        }
        finally
        {
            _loadingProjectSettings = false;
        }

        SelectedChapter = Chapters.FirstOrDefault();
        OnPropertyChanged(nameof(NarratableChapterCount));
        OnPropertyChanged(nameof(AudioReadyChapterCount));
        OnPropertyChanged(nameof(BookReadinessSummary));
        OnPropertyChanged(nameof(BookAudioReadiness));
        OnPropertyChanged(nameof(IsQueueSearchEmpty));
    }

    private void ClearOpenProject()
    {
        Interlocked.Increment(ref _chapterQueueRefreshVersion);
        CancelPendingAutosave();
        ClearGeneratedBookAudio();
        _projectId = null;
        _projectCreatedAt = default;
        _sourcePath = string.Empty;
        _editedTextByChapter.Clear();
        Book = null;
        Chapters.Clear();
        ChapterQueue.Clear();
        VisibleChapterQueue.Clear();
        OnPropertyChanged(nameof(NarratableChapterCount));
        OnPropertyChanged(nameof(AudioReadyChapterCount));
        OnPropertyChanged(nameof(BookReadinessSummary));
        OnPropertyChanged(nameof(BookAudioReadiness));
        OnPropertyChanged(nameof(IsQueueSearchEmpty));
        ClearStructureSuggestions();
        ApplyStructureSuggestionsCommand.NotifyCanExecuteChanged();
        DetectedGenre = string.Empty;
        BookLanguage = string.Empty;
        SelectedChapter = null;
        ChapterText = string.Empty;
        ApplyAnalysis(null);
        VoiceAssignments.Clear();
        OnPropertyChanged(nameof(HasChapter));
        OnPropertyChanged(nameof(ChapterHeading));
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
        ShowBook(
            project.Title,
            project.Chapters,
            project.NarrationMode,
            project.StructureAnalysis,
            project.DefaultLanguage);
        await RefreshChapterQueueAsync(cancellationToken);
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
                Chapters.Select(chapter => chapter with { Text = GetChapterText(chapter) }).ToArray(),
                UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                CreateCurrentStructureAnalysis(),
                BookLanguage);
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
