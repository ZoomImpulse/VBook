using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VBook.App.Services;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.App.ViewModels;

public sealed record SegmentItem(int Index, string Speaker, bool IsNarration, string Text);

public sealed record CharacterItem(string Name, string Detail);

public sealed partial class MainWindowViewModel
{
    private ChapterAnalysis? _analysis;
    private SpeechEditReview? _speechReview;
    private GeneratedBookAudio? _generatedBookAudio;
    private int _analysisLoadVersion;
    private int _chapterQueueRefreshVersion;
    private byte[]? _generatedWavBytes;
    private int _voiceAssignmentUpdateDepth;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly BookAudioGenerationService _bookAudioGeneration;
    private readonly IM4bExporter _m4bExporter;
    private readonly DispatcherTimer _audioPlaybackTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveAudioPlayback))]
    [NotifyCanExecuteChangedFor(nameof(PlayGeneratedAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseGeneratedAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGeneratedAudioCommand))]
    private bool _isAudioPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveAudioPlayback))]
    [NotifyCanExecuteChangedFor(nameof(PlayGeneratedAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGeneratedAudioCommand))]
    private bool _isAudioPaused;

    [ObservableProperty]
    private double _audioPlaybackProgress;

    [ObservableProperty]
    private string _audioPlaybackTime = "0:00 / 0:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateVoiceDesignCommand))]
    private bool _isPreviewingVoice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportEpubCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateVoiceDesignCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectChapter))]
    [NotifyPropertyChangedFor(nameof(CanChangeNarrationMode))]
    [NotifyPropertyChangedFor(nameof(ChapterAnalysisGuidance))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    private bool _isAnalyzing;

    [ObservableProperty]
    private double _analysisProgress;

    [ObservableProperty]
    private string _analysisStatus = "Select a chapter to analyze.";

    [ObservableProperty]
    private string _speechReviewStatus = "Review the chapter to suggest changes for spoken delivery.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    private bool _hasAnalysis;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    private bool _isAnalysisStale;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    private bool _useMultipleVoices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBookAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStructureSuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportEpubCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecentProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelectChapter))]
    [NotifyPropertyChangedFor(nameof(CanChangeNarrationMode))]
    [NotifyCanExecuteChangedFor(nameof(DownloadKokoroCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetupQwenCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeTtsProvider))]
    [NotifyPropertyChangedFor(nameof(ChapterAnalysisGuidance))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuggestVoiceInstructionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateVoiceDesignCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private double _generationProgress;

    [ObservableProperty]
    private string _generationStatus = "Analyze a chapter before generating audio.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeTtsProvider))]
    [NotifyCanExecuteChangedFor(nameof(DownloadKokoroCommand))]
    private bool _isDownloadingKokoro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeTtsProvider))]
    [NotifyCanExecuteChangedFor(nameof(SetupQwenCommand))]
    private bool _isSettingUpQwen;

    [ObservableProperty]
    private double _ttsModelDownloadProgress;

    [ObservableProperty]
    private string _ttsModelDownloadStatus = string.Empty;

    [ObservableProperty]
    private double _qwenSetupProgress;

    [ObservableProperty]
    private string _qwenSetupStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeBookStructureCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStructureSuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuggestVoiceInstructionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateVoiceDesignCommand))]
    private bool _isAnalyzingStructure;

    [ObservableProperty]
    private bool _isGeneratingBook;

    [ObservableProperty]
    private double _structureProgress;

    [ObservableProperty]
    private string _structureStatus = "Analyze the book to review its structure.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedGenre))]
    [NotifyCanExecuteChangedFor(nameof(SuggestVoiceInstructionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    private string _detectedGenre = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChapterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBookCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceProposalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewVoiceProposalCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateVoiceDesignCommand))]
    private string _bookLanguage = string.Empty;

    [ObservableProperty]
    private string _voiceDesignerStatus = "Choose a book language and propose profiles. Analyze book structure for genre-aware directions.";

    public ObservableCollection<SegmentItem> Segments { get; } = [];

    public ObservableCollection<CharacterItem> Characters { get; } = [];

    public ObservableCollection<VoiceAssignmentItem> VoiceAssignments { get; } = [];

    public ObservableCollection<StructureSuggestionItem> StructureSuggestions { get; } = [];

    public ObservableCollection<SpeechEditItem> SpeechEdits { get; } = [];

    public bool HasStructureSuggestions => StructureSuggestions.Count > 0;

    public bool HasNoStructureSuggestions => !HasStructureSuggestions;

    public bool HasSpeechEdits => SpeechEdits.Count > 0;

    public bool HasNoSpeechEdits => !HasSpeechEdits;

    public bool HasNarrativeSegments => Segments.Count > 0;

    public bool HasCharacters => Characters.Count > 0;

    public bool IsSpeechReviewStale =>
        _speechReview is not null &&
        SelectedChapter is { } chapter &&
        !string.Equals(
            _speechReview.SourceHash,
            TextFingerprint.Compute(GetChapterText(chapter)),
            StringComparison.Ordinal);

    public bool HasActiveAudioPlayback => IsAudioPlaying || IsAudioPaused;

    public bool HasGeneratedBookAudio => _generatedBookAudio is not null;

    public bool IsGeneratingChapter => IsGenerating && !IsGeneratingBook;

    public bool HasDetectedGenre => !string.IsNullOrWhiteSpace(DetectedGenre);

    public string AnalyzerName => _narrativeWorker.Capabilities.Name;

    public string ChapterAnalysisGuidance =>
        Book is null
            ? "Import a book, then select a chapter in the production queue to analyze it."
            : SelectedChapter is null
                ? "Select a chapter in the production queue on the left to analyze it."
                : IsBusy || IsAnalyzing || IsGenerating
                    ? "Wait for the current operation to finish before analyzing a chapter."
                    : SelectedChapter.Role == SectionRole.Skip
                        ? "This section is marked Skip. Select a narratable chapter in the production queue, or change its role in Structure and apply the reviewed roles."
                        : $"Analysis applies to “{SelectedChapter.Title}”. Select another chapter in the production queue to analyze it instead.";

    public string TtsProviderName => _ttsProvider.Capabilities.Name;

    public IReadOnlyList<string> TtsLanguages => _ttsProvider.Languages;

    public bool SupportsVoiceDesign =>
        _ttsProvider is IVoiceDesignProvider { SupportsVoiceDesign: true };

    public string VoiceDesignerDescription => SupportsVoiceDesign
        ? "Design a voice from an editable description and a sample spoken in the effective profile language. Suggested descriptions are in English and are not auto-translated; the reference stays staged until applied, and preview uses Qwen Base voice cloning."
        : "Voice matches use verified native-language metadata. Every provider voice remains available for manual selection. Analyzed book genre adds tailored direction; otherwise a general direction is staged.";

    partial void OnBookLanguageChanged(string value)
    {
        ClearGeneratedBookAudio();
        ClearGeneratedAudioForChapterChange();
        GenerateChapterCommand.NotifyCanExecuteChanged();
        GenerateBookCommand.NotifyCanExecuteChanged();
        PreviewVoiceCommand.NotifyCanExecuteChanged();
        CreateVoiceProposalsCommand.NotifyCanExecuteChanged();
        ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
        PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
        GenerateVoiceDesignCommand.NotifyCanExecuteChanged();
        if (SupportsVoiceDesign)
        {
            foreach (var assignment in VoiceAssignments.Where(item =>
                         item.HasProposal && string.IsNullOrWhiteSpace(item.ProposedLanguage)))
            {
                assignment.ProposedSampleText = GetVoicePreviewText(value);
            }
        }

        if (!_loadingProjectSettings && _projectId is not null)
        {
            ScheduleAutosave();
            _ = RefreshChapterQueueForLanguageChangeAsync();
        }
    }

    partial void OnDetectedGenreChanged(string value) =>
        CreateVoiceProposalsCommand.NotifyCanExecuteChanged();

    private async Task RefreshChapterQueueForLanguageChangeAsync()
    {
        try
        {
            await RefreshChapterQueueAsync();
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not refresh the production queue for the selected language: {exception.Message}";
        }
    }

    public bool SupportsVoiceInstructions => _ttsProvider.SupportsVoiceInstructions;

    public string CurrentTtsProviderId =>
        _ttsProvider is LocalTtsProviderService localTtsProvider
            ? localTtsProvider.SelectedProviderId
            : _ttsProvider.Capabilities.Name;

    public IReadOnlyList<string> TtsProviderOptions =>
        _ttsProvider is LocalTtsProviderService localTtsProvider
            ? localTtsProvider.ProviderIds
            : [];

    public string SelectedTtsProvider
    {
        get => _ttsProvider is LocalTtsProviderService localTtsProvider
            ? localTtsProvider.SelectedProviderId
            : string.Empty;
        set
        {
            if (_ttsProvider is not LocalTtsProviderService localTtsProvider ||
                string.Equals(value, localTtsProvider.SelectedProviderId, StringComparison.Ordinal) ||
                !CanChangeTtsProvider)
            {
                return;
            }

            try
            {
                localTtsProvider.SelectProvider(value);
            }
            catch (Exception exception)
            {
                GenerationStatus = $"Could not select {value} speech: {exception.Message}";
                OnPropertyChanged();
            }
        }
    }

    public bool CanChangeTtsProvider => !IsBusy && !IsGenerating && !IsDownloadingKokoro && !IsSettingUpQwen;

    public bool CanSetupQwen =>
        _ttsProvider is LocalTtsProviderService localTtsProvider &&
        !localTtsProvider.IsQwenInstalled &&
        !IsBusy &&
        !IsGenerating &&
        !IsSettingUpQwen;

    public bool IsQwenSetupRequired =>
        _ttsProvider is LocalTtsProviderService localTtsProvider &&
        localTtsProvider.SelectedProviderId == LocalTtsProviderService.QwenProviderId &&
        !localTtsProvider.IsQwenInstalled;

    public bool IsKokoroSetupRequired =>
        _ttsProvider is LocalTtsProviderService localTtsProvider &&
        localTtsProvider.SelectedProviderId == LocalTtsProviderService.KokoroProviderId &&
        !localTtsProvider.IsKokoroInstalled;

    public bool CanDownloadKokoro =>
        _ttsProvider is LocalTtsProviderService localTtsProvider &&
        !localTtsProvider.IsKokoroInstalled &&
        !IsBusy &&
        !IsGenerating &&
        !IsDownloadingKokoro;

    public string TtsProviderDescription =>
        _ttsProvider is LocalTtsProviderService localTtsProvider
            ? localTtsProvider.CurrentProviderDescription
            : _ttsProvider.Capabilities.IsMock
                ? "The mock TTS generates tones, not speech."
                : $"Audio is synthesized locally with {_ttsProvider.Capabilities.Name}.";

    public bool CanChangeNarrationMode => !IsBusy && !IsAnalyzing && !IsGenerating && !IsAnalyzingStructure;

    private void OnLocalTtsProviderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedTtsProvider));
        OnPropertyChanged(nameof(TtsProviderName));
        OnPropertyChanged(nameof(TtsProviderDescription));
        OnPropertyChanged(nameof(TtsLanguages));
        OnPropertyChanged(nameof(SupportsVoiceInstructions));
        OnPropertyChanged(nameof(SupportsVoiceDesign));
        OnPropertyChanged(nameof(VoiceDesignerDescription));
        OnPropertyChanged(nameof(CurrentTtsProviderId));
        OnPropertyChanged(nameof(IsQwenSetupRequired));
        OnPropertyChanged(nameof(CanSetupQwen));
        OnPropertyChanged(nameof(IsKokoroSetupRequired));
        OnPropertyChanged(nameof(CanDownloadKokoro));
        OnPropertyChanged(nameof(CanChangeTtsProvider));
        GenerateChapterCommand.NotifyCanExecuteChanged();
        GenerateBookCommand.NotifyCanExecuteChanged();
        DownloadKokoroCommand.NotifyCanExecuteChanged();
        SetupQwenCommand.NotifyCanExecuteChanged();
        PreviewVoiceCommand.NotifyCanExecuteChanged();
        CreateVoiceProposalsCommand.NotifyCanExecuteChanged();
        ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
        PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
        GenerateVoiceDesignCommand.NotifyCanExecuteChanged();

        if (e.PropertyName == nameof(LocalTtsProviderService.SelectedProviderId) &&
            !IsBusy &&
            _projectId is { } projectId)
        {
            _ = RefreshVoiceAssignmentsSafelyAsync(projectId, IsAnalysisStale ? null : _analysis);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetupQwen), IncludeCancelCommand = true)]
    private async Task SetupQwenAsync(CancellationToken cancellationToken)
        {
            if (_ttsProvider is not LocalTtsProviderService localTtsProvider)
            {
                return;
            }

            IsBusy = true;
            IsSettingUpQwen = true;
            QwenSetupProgress = 0;
            QwenSetupStatus = "Preparing the local Qwen CUDA setup. No downloads start until you choose Set up Qwen.";
            GenerationStatus = QwenSetupStatus;
            try
            {
                var progress = new Progress<QwenTtsSetupProgress>(report =>
                {
                    if (!IsSettingUpQwen)
                    {
                        return;
                    }

                    QwenSetupStatus = report.TotalBytes > 0
                        ? $"{report.Phase}: {report.BytesReceived / (1024d * 1024d * 1024d):0.00} of {report.TotalBytes / (1024d * 1024d * 1024d):0.00} GiB"
                        : report.Phase;
                    QwenSetupProgress = report.TotalBytes > 0
                        ? Math.Clamp(report.BytesReceived / (double)report.TotalBytes, 0, 1)
                        : QwenSetupProgress;
                    GenerationStatus = QwenSetupStatus;
                });
                await localTtsProvider.InstallAndSelectQwenAsync(progress, cancellationToken);
                QwenSetupProgress = 1;
                QwenSetupStatus = "Qwen3-TTS is ready. Generation runs on the local CUDA GPU.";
                GenerationStatus = QwenSetupStatus;
                if (_projectId is { } projectId)
                {
                    await RefreshVoiceAssignmentsSafelyAsync(projectId, IsAnalysisStale ? null : _analysis);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                QwenSetupStatus = "Qwen setup canceled. Incomplete downloads and setup files were cleaned up.";
                GenerationStatus = QwenSetupStatus;
            }
            catch (Exception exception)
            {
                QwenSetupStatus = $"Could not set up Qwen3-TTS: {exception.Message}";
                GenerationStatus = QwenSetupStatus;
            }
            finally
            {
                IsSettingUpQwen = false;
                IsBusy = false;
            }
        }

    [RelayCommand(CanExecute = nameof(CanDownloadKokoro), IncludeCancelCommand = true)]
    private async Task DownloadKokoroAsync(CancellationToken cancellationToken)
    {
        if (_ttsProvider is not LocalTtsProviderService localTtsProvider)
        {
            return;
        }

        IsBusy = true;
        IsDownloadingKokoro = true;
        TtsModelDownloadProgress = 0;
        TtsModelDownloadStatus = "Preparing the Kokoro model download…";
        GenerationStatus = TtsModelDownloadStatus;
        try
        {
            var progress = new Progress<KokoroModelDownloadProgress>(report =>
            {
                TtsModelDownloadProgress = report.TotalBytes > 0
                    ? Math.Clamp(report.BytesReceived / (double)report.TotalBytes, 0, 1)
                    : 0;
                TtsModelDownloadStatus =
                    $"Downloading Kokoro model: {TtsModelDownloadProgress:P0} ({FormatBytes(report.BytesReceived)} of {FormatBytes(report.TotalBytes)}).";
                GenerationStatus = TtsModelDownloadStatus;
            });
            await localTtsProvider.InstallAndSelectKokoroAsync(progress, cancellationToken);
            TtsModelDownloadProgress = 1;
            TtsModelDownloadStatus = "Kokoro is ready. Model inference stays local.";
            GenerationStatus = TtsModelDownloadStatus;
            if (_projectId is { } projectId)
            {
                await RefreshVoiceAssignmentsSafelyAsync(projectId, IsAnalysisStale ? null : _analysis);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TtsModelDownloadStatus = "Kokoro download canceled. No partial model was activated.";
            GenerationStatus = TtsModelDownloadStatus;
        }
        catch (Exception exception)
        {
            TtsModelDownloadStatus = $"Could not install Kokoro: {exception.Message}";
            GenerationStatus = TtsModelDownloadStatus;
        }
        finally
        {
            IsDownloadingKokoro = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeStructure), IncludeCancelCommand = true)]
    private async Task AnalyzeBookStructureAsync(CancellationToken cancellationToken)
    {
        if (Book is not { } book)
        {
            return;
        }

        var chapters = Chapters.ToArray();
        IsAnalyzingStructure = true;
        IsBusy = true;
        StructureProgress = 0;
        StructureStatus = "Preparing local book-structure analysis…";
        try
        {
            var progress = new Progress<BookStructureProgress>(report =>
            {
                if (!IsAnalyzingStructure)
                {
                    return;
                }

                StructureProgress = report.Fraction;
                StructureStatus = report.Message;
            });
            var result = await _bookStructureAnalyzer.AnalyzeAsync(
                book.Title,
                chapters,
                progress,
                cancellationToken);
            if (!ReferenceEquals(Book, book))
            {
                return;
            }

            var titles = chapters.ToDictionary(chapter => chapter.Id, chapter => chapter.Title, StringComparer.Ordinal);
            ClearStructureSuggestions();
            foreach (var suggestion in result.Chapters)
            {
                AddStructureSuggestion(new StructureSuggestionItem(
                    suggestion.ChapterId,
                    titles[suggestion.ChapterId],
                    suggestion.Role,
                    suggestion.Confidence,
                    suggestion.Rationale));
            }

            ApplyStructureSuggestionsCommand.NotifyCanExecuteChanged();
            DetectedGenre = result.Genre;
            StructureStatus = "Review the suggested section roles, then apply them to the project.";
            ScheduleAutosave();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StructureStatus = "Book-structure analysis cancelled.";
        }
        catch (Exception exception)
        {
            StructureStatus = $"Book-structure analysis failed: {exception.Message}";
        }
        finally
        {
            IsAnalyzingStructure = false;
            IsBusy = false;
        }
    }

    private bool CanAnalyzeStructure() =>
        !IsBusy && !IsAnalyzing && !IsGenerating && Book is not null;

    partial void OnIsGeneratingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGeneratingChapter));
        OnPropertyChanged(nameof(CanEditNarrationContent));
    }

    partial void OnIsGeneratingBookChanged(bool value) => OnPropertyChanged(nameof(IsGeneratingChapter));

    [RelayCommand(CanExecute = nameof(CanGenerateBook), IncludeCancelCommand = true)]
    private async Task GenerateBookAsync(CancellationToken cancellationToken)
    {
        if (_projectId is not { } projectId || Book is not { } book)
        {
            return;
        }

        if (!HasValidVoiceLanguages())
        {
            GenerationStatus = "Select a book language and ensure every assigned voice supports it before generating.";
            return;
        }

        var chapters = Chapters.Select(chapter => chapter with { Text = GetChapterText(chapter) }).ToArray();
        if (!chapters.Any(chapter => chapter.Role != SectionRole.Skip))
        {
            GenerationStatus = "The book has no chapters selected for narration.";
            return;
        }

        IsGenerating = true;
        IsGeneratingBook = true;
        GenerationProgress = 0;
        GenerationStatus = "Preparing whole-book audio generation…";
        try
        {
            await FlushPendingSaveAsync();
            var (assignments, profiles) = await SaveAndLoadCurrentVoiceProfilesAsync(
                projectId,
                cancellationToken);
            var progress = new Progress<GenerationProgress>(report =>
            {
                if (!IsGenerating || !IsGeneratingBook)
                {
                    return;
                }

                GenerationProgress = report.Fraction;
                GenerationStatus = report.Message;
            });
            var generated = await _bookAudioGeneration.GenerateAsync(
                projectId,
                book.Title,
                chapters,
                UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                assignments,
                progress,
                cancellationToken,
                profiles,
                BookLanguage);
            if (_projectId == projectId && ReferenceEquals(Book, book))
            {
                _generatedBookAudio = generated;
                OnPropertyChanged(nameof(HasGeneratedBookAudio));
                ExportBookAudioCommand.NotifyCanExecuteChanged();
                GenerationStatus =
                    $"Generated {generated.Chapters.Count} chapters for {generated.Title}; completed chapter audio can be resumed if interrupted.";
                await RefreshChapterQueueAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GenerationStatus = "Whole-book generation paused. Run it again to resume completed chapter segments.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Whole-book generation failed: {exception.Message}";
        }
        finally
        {
            IsGeneratingBook = false;
            IsGenerating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportBookAudio))]
    private async Task ExportBookAudioAsync(CancellationToken cancellationToken)
    {
        if (_generatedBookAudio is not { } book)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var path = await _filePicker.PickM4bSavePathAsync(book.Title);
            if (path is null)
            {
                return;
            }

            await _m4bExporter.ExportAsync(book, path, cancellationToken);
            GenerationStatus = $"Exported {book.Chapters.Count} chapters to M4B: {path}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GenerationStatus = "M4B export cancelled.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not export M4B audiobook: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanGenerateBook() =>
        !IsBusy && !IsAnalyzing && !IsGenerating && !IsAnalyzingStructure && Book is not null &&
        _ttsProvider.VoiceIds.Count > 0 && HasValidVoiceLanguages() && HasReadyVoiceDesigns() &&
        Chapters.Any(chapter => chapter.Role != SectionRole.Skip);

    private bool CanExportBookAudio() => !IsBusy && !IsGenerating && _generatedBookAudio is not null;

    private void ClearGeneratedBookAudio()
    {
        var bookAudio = _generatedBookAudio;
        if (bookAudio is null)
        {
            return;
        }

        _generatedBookAudio = null;
        OnPropertyChanged(nameof(HasGeneratedBookAudio));
        ExportBookAudioCommand.NotifyCanExecuteChanged();
        try
        {
            bookAudio.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GenerationStatus = $"Could not remove temporary whole-book audio: {exception.Message}";
        }
    }

    private Task AcceptSpeechEditAsync(SpeechEditItem item) =>
        SetSpeechEditStatusAsync(item, SpeechEditStatus.Accepted);

    private Task RejectSpeechEditAsync(SpeechEditItem item) =>
        SetSpeechEditStatusAsync(item, SpeechEditStatus.Rejected);

    private async Task SetSpeechEditStatusAsync(SpeechEditItem? item, SpeechEditStatus status)
    {
        if (IsBusy || IsGenerating)
        {
            SpeechReviewStatus = "Wait for the current operation to finish before changing speech-review decisions.";
            return;
        }

        if (item is null || _projectId is not { } projectId || _speechReview is not { } review)
        {
            return;
        }

        if (IsSpeechReviewStale)
        {
            SpeechReviewStatus = "The source changed; run the combined chapter analysis before reviewing suggestions.";
            return;
        }

        if (item.Status == status)
        {
            return;
        }

        var updated = review with
        {
            Suggestions = review.Suggestions.Select(suggestion =>
                suggestion.Id == item.Id ? suggestion with { Status = status } : suggestion).ToArray()
        };
        try
        {
            await _narrativeStore.SaveSpeechReviewAsync(projectId, updated);
            ClearGeneratedBookAudio();
            ApplySpeechReview(updated);
            await RefreshChapterQueueAsync();
            SpeechReviewStatus = status == SpeechEditStatus.Accepted
                ? "Accepted change saved to the speech script; the source text is unchanged."
                : "Suggestion rejected; the original passage remains in the speech script.";
        }
        catch (Exception exception)
        {
            SpeechReviewStatus = $"Could not save the review decision: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyStructureSuggestions))]
    private async Task ApplyStructureSuggestionsAsync()
    {
        if (StructureSuggestions.Count == 0)
        {
            return;
        }

        ClearGeneratedBookAudio();
        var suggestions = StructureSuggestions.ToDictionary(
            suggestion => suggestion.ChapterId,
            suggestion => suggestion.SuggestedRole,
            StringComparer.Ordinal);
        var selectedId = SelectedChapter?.Id;
        for (var index = 0; index < Chapters.Count; index++)
        {
            var chapter = Chapters[index];
            if (suggestions.TryGetValue(chapter.Id, out var role) && chapter.Role != role)
            {
                Chapters[index] = chapter with { Role = role };
            }
        }

        if (selectedId is not null)
        {
            SelectedChapter = Chapters.FirstOrDefault(chapter => chapter.Id == selectedId);
        }

        ScheduleAutosave();
        StructureStatus = "Applied the reviewed section roles. Skipped sections remain in the project but cannot be narrated.";
        GenerateBookCommand.NotifyCanExecuteChanged();
        try
        {
            await RefreshChapterQueueAsync();
        }
        catch (Exception exception)
        {
            StructureStatus = $"Applied roles, but could not refresh the production queue: {exception.Message}";
        }
    }

    private bool CanApplyStructureSuggestions() =>
        !IsBusy && !IsGenerating && !IsAnalyzingStructure && StructureSuggestions.Count > 0;

    private void RestoreStructureAnalysis(
        BookStructureResult? structureAnalysis,
        IReadOnlyList<ImportedChapter> chapters)
    {
        ClearStructureSuggestions();
        if (structureAnalysis is null)
        {
            DetectedGenre = string.Empty;
            StructureStatus = "Analyze the book to review its structure.";
            return;
        }

        if (string.IsNullOrWhiteSpace(structureAnalysis.Genre))
        {
            throw new InvalidDataException("The saved book-structure analysis has no genre.");
        }

        var titles = chapters.ToDictionary(chapter => chapter.Id, chapter => chapter.Title, StringComparer.Ordinal);
        foreach (var suggestion in structureAnalysis.Chapters)
        {
            if (!titles.TryGetValue(suggestion.ChapterId, out var title))
            {
                throw new InvalidDataException(
                    $"The saved book-structure analysis references missing chapter '{suggestion.ChapterId}'.");
            }

            AddStructureSuggestion(new StructureSuggestionItem(
                suggestion.ChapterId,
                title,
                suggestion.Role,
                suggestion.Confidence,
                suggestion.Rationale));
        }

        DetectedGenre = structureAnalysis.Genre;
        StructureStatus = "Loaded saved book-structure analysis. Review the suggested section roles, then apply them to the project.";
        ApplyStructureSuggestionsCommand.NotifyCanExecuteChanged();
    }

    private void ClearStructureSuggestions()
    {
        foreach (var suggestion in StructureSuggestions)
        {
            suggestion.PropertyChanged -= OnStructureSuggestionChanged;
        }

        StructureSuggestions.Clear();
        OnPropertyChanged(nameof(HasStructureSuggestions));
        OnPropertyChanged(nameof(HasNoStructureSuggestions));
        ApplyStructureSuggestionsCommand.NotifyCanExecuteChanged();
    }

    private void AddStructureSuggestion(StructureSuggestionItem suggestion)
    {
        suggestion.PropertyChanged += OnStructureSuggestionChanged;
        StructureSuggestions.Add(suggestion);
        OnPropertyChanged(nameof(HasStructureSuggestions));
        OnPropertyChanged(nameof(HasNoStructureSuggestions));
    }

    private void OnStructureSuggestionChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StructureSuggestionItem.SuggestedRole))
        {
            ScheduleAutosave();
        }
    }

    private BookStructureResult? CreateCurrentStructureAnalysis() =>
        HasDetectedGenre
            ? new BookStructureResult(
                DetectedGenre,
                StructureSuggestions.Select(suggestion => new ChapterStructureSuggestion(
                    suggestion.ChapterId,
                    suggestion.SuggestedRole,
                    suggestion.Confidence,
                    suggestion.Rationale)).ToArray())
            : null;

    partial void OnUseMultipleVoicesChanged(bool value)
    {
        ClearGeneratedBookAudio();
        if (!_loadingProjectSettings && Book is not null && _projectId is not null)
        {
            foreach (var item in ChapterQueue.Where(item => item.Chapter.Role != SectionRole.Skip))
            {
                item.Status = "Needs analysis";
            }

            RefreshVisibleChapterQueue();
            OnPropertyChanged(nameof(AudioReadyChapterCount));
            OnPropertyChanged(nameof(BookReadinessSummary));
            OnPropertyChanged(nameof(BookAudioReadiness));
            ScheduleAutosave();
            if (SelectedChapter is { } chapter)
            {
                RefreshAnalysisStaleness(chapter);
                _ = RefreshVoiceAssignmentsSafelyAsync(_projectId.Value, IsAnalysisStale ? null : _analysis);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private async Task AnalyzeChapterAsync(CancellationToken cancellationToken)
    {
        if (_projectId is not { } projectId || SelectedChapter is not { } chapter)
        {
            return;
        }

        var text = GetChapterText(chapter);
        IsAnalyzing = true;
        AnalysisProgress = 0;
        AnalysisStatus = "Starting analysis…";
        var progressSync = new object();
        var acceptProgress = true;
        try
        {
            // The project row must exist before narrative data can reference it.
            await FlushPendingSaveAsync();
            var known = await _narrativeStore.LoadCharactersAsync(projectId, cancellationToken);
            var progress = new Progress<AnalysisProgress>(report =>
            {
                lock (progressSync)
                {
                    if (!acceptProgress || !IsAnalyzing)
                    {
                        return;
                    }

                    AnalysisProgress = report.Fraction;
                    AnalysisStatus = report.Message;
                }
            });
            var analysis = await _narrativeWorker.AnalyzeChapterAsync(
                new AnalysisRequest(
                    chapter.Id,
                    text,
                    known,
                    UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                    chapter.Role),
                progress,
                cancellationToken);
            lock (progressSync)
            {
                acceptProgress = false;
            }
            await _narrativeStore.SaveAnalysisAsync(projectId, analysis, cancellationToken);
            var review = new SpeechEditReview(
                chapter.Id,
                analysis.SourceHash,
                MergeSpeechSuggestions(
                    analysis.SpeechSuggestions ?? [],
                    _speechReview is { } currentReview &&
                    currentReview.ChapterId == chapter.Id &&
                    currentReview.SourceHash == analysis.SourceHash
                        ? currentReview.Suggestions
                        : []));
            await _narrativeStore.SaveSpeechReviewAsync(projectId, review, cancellationToken);

            _analysisLoadVersion++;
            if (_projectId == projectId && SelectedChapter?.Id == chapter.Id)
            {
                ApplySpeechReview(review);
                ApplyAnalysis(analysis);
                var warningCount = analysis.Warnings?.Count ?? 0;
                AnalysisStatus = warningCount == 0
                    ? $"Found {analysis.Segments.Count} segments and {analysis.Characters.Count} characters."
                    : $"Found {analysis.Segments.Count} segments and {analysis.Characters.Count} characters; " +
                      $"{warningCount} model suggestion(s) failed validation and were ignored.";
                SpeechReviewStatus = review.Suggestions.Count == 0
                    ? "Combined analysis found no wording changes; the source text will be read as written."
                    : $"Combined analysis found {review.Suggestions.Count} wording suggestion(s). Review each before it affects narration.";
                if (warningCount > 0)
                {
                    SpeechReviewStatus += " Invalid model suggestions were ignored; unclassified text remains narration.";
                }
                await RefreshVoiceAssignmentsAsync(projectId, analysis);
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (progressSync)
            {
                acceptProgress = false;
                AnalysisStatus = "Analysis cancelled.";
                SpeechReviewStatus = "Combined analysis cancelled; no new review suggestions were applied.";
            }
        }
        catch (Exception exception)
        {
            lock (progressSync)
            {
                acceptProgress = false;
                AnalysisStatus = $"Analysis failed: {exception.Message}";
                SpeechReviewStatus = "Combined analysis did not complete; previous review decisions were kept.";
            }
        }
        finally
        {
            lock (progressSync)
            {
                acceptProgress = false;
            }

            IsAnalyzing = false;
        }

        try
        {
            await RefreshChapterQueueAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            AnalysisStatus += $" Production queue refresh failed: {exception.Message}";
        }
    }

    private bool CanAnalyze() =>
        !IsBusy && !IsAnalyzing && !IsGenerating && Book is not null &&
        SelectedChapter is { Role: not SectionRole.Skip };

    private static IReadOnlyList<SpeechEditSuggestion> MergeSpeechSuggestions(
        IReadOnlyList<SpeechEditSuggestion> generated,
        IReadOnlyList<SpeechEditSuggestion> previous)
    {
        var merged = new List<SpeechEditSuggestion>();
        var exactMatches = previous.ToDictionary(
            suggestion => (suggestion.Start, suggestion.Length, suggestion.OriginalText, suggestion.ReplacementText));
        foreach (var suggestion in generated)
        {
            var exactKey = (suggestion.Start, suggestion.Length, suggestion.OriginalText, suggestion.ReplacementText);
            if (exactMatches.TryGetValue(exactKey, out var reviewed))
            {
                merged.Add(suggestion with { Id = reviewed.Id, Status = reviewed.Status });
                continue;
            }

            var acceptedAlternative = previous.FirstOrDefault(existing =>
                existing.Status == SpeechEditStatus.Accepted &&
                existing.Start == suggestion.Start &&
                existing.Length == suggestion.Length &&
                string.Equals(existing.OriginalText, suggestion.OriginalText, StringComparison.Ordinal));
            if (acceptedAlternative is not null)
            {
                if (merged.All(existing => existing.Id != acceptedAlternative.Id))
                {
                    merged.Add(acceptedAlternative);
                }

                continue;
            }

            merged.Add(suggestion);
        }

        foreach (var reviewed in previous.Where(suggestion => suggestion.Status != SpeechEditStatus.Pending))
        {
            if (merged.All(existing => existing.Id != reviewed.Id))
            {
                merged.Add(reviewed);
            }
        }

        return merged.OrderBy(suggestion => suggestion.Start).ToArray();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateChapter), IncludeCancelCommand = true)]
    private async Task GenerateChapterAsync(CancellationToken cancellationToken)
    {
        if (_projectId is not { } projectId || SelectedChapter is not { } chapter || _analysis is not { } analysis)
        {
            return;
        }

        if (!HasValidVoiceLanguages())
        {
            GenerationStatus = "Select a book language and ensure every assigned voice supports it before generating.";
            return;
        }

        var text = GetChapterText(chapter);
        if (!analysis.Matches(
                text,
                UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                chapter.Role))
        {
            GenerationStatus = "Analyze the current chapter text before generating audio.";
            return;
        }

        IsGenerating = true;
        GenerationProgress = 0;
        GenerationStatus = "Preparing audio generation…";
        try
        {
            await FlushPendingSaveAsync();
            var (assignments, profiles) = await SaveAndLoadCurrentVoiceProfilesAsync(
                projectId,
                cancellationToken);
            var progress = new Progress<GenerationProgress>(report =>
            {
                if (!IsGenerating)
                {
                    return;
                }

                GenerationProgress = report.Fraction;
                GenerationStatus = report.Message;
            });
            var generated = await _audioGeneration.GenerateChapterAsync(
                projectId,
                chapter.Id,
                text,
                analysis,
                UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                assignments,
                progress,
                cancellationToken,
                chapter.Role,
                _speechReview?.ChapterId == chapter.Id ? _speechReview : null,
                profiles,
                BookLanguage);
            if (_projectId == projectId && SelectedChapter?.Id == chapter.Id && ReferenceEquals(_analysis, analysis))
            {
                ClearGeneratedBookAudio();
                _audioPlayback.Stop();
                ResetAudioPlayback();
                _generatedWavBytes = generated.WavBytes;
                PlayGeneratedAudioCommand.NotifyCanExecuteChanged();
                ExportChapterAudioCommand.NotifyCanExecuteChanged();
                GenerationStatus = $"Generated {generated.Job.TotalSegments} segments ({generated.Duration:g}).";
            }
            else
            {
                GenerationStatus = $"Audio generation completed for {chapter.Title}. Select it and generate again to load the saved audio.";
            }

            await RefreshChapterQueueAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Generation paused. Run it again to resume.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Audio generation failed: {exception.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportAudio))]
    private async Task ExportChapterAudioAsync(CancellationToken cancellationToken)
    {
        if (_generatedWavBytes is null || SelectedChapter is not { } chapter)
        {
            return;
        }

        var path = await _filePicker.PickWavSavePathAsync(chapter.Title);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(path, _generatedWavBytes, cancellationToken);
            GenerationStatus = $"Exported chapter audio to {path}.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not export chapter audio: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlayAudio))]
    private void PlayGeneratedAudio()
    {
        if (_generatedWavBytes is null)
        {
            return;
        }

        try
        {
            if (_audioPlayback.IsPaused)
            {
                _audioPlayback.Resume();
            }
            else
            {
                _audioPlayback.Play(_generatedWavBytes);
            }

            IsAudioPlaying = _audioPlayback.IsPlaying;
            IsAudioPaused = _audioPlayback.IsPaused;
            UpdateAudioPlaybackTime();
            _audioPlaybackTimer.Start();
            GenerationStatus = IsAudioPaused ? "Audio playback paused." : "Playing generated chapter audio.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not play generated audio: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewVoice), IncludeCancelCommand = true)]
    private Task PreviewVoiceAsync(
        VoiceAssignmentItem assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return PreviewVoiceProfileAsync(assignment.SpeakerName, assignment.Profile, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewVoiceProposal), IncludeCancelCommand = true)]
    private Task PreviewVoiceProposalAsync(
        VoiceAssignmentItem assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return PreviewVoiceProfileAsync(assignment.SpeakerName, assignment.ProposedProfile, cancellationToken);
    }

    private async Task PreviewVoiceProfileAsync(
        string speakerName,
        SpeechVoiceProfile profile,
        CancellationToken cancellationToken)
    {
        if (!_ttsProvider.VoiceIds.Contains(profile.VoiceId, StringComparer.Ordinal))
        {
            GenerationStatus = $"Voice '{profile.VoiceId}' is unavailable from {_ttsProvider.Capabilities.Name}.";
            return;
        }

        if (SupportsVoiceDesign &&
            (profile.VoiceDesignReferenceAudio is null || string.IsNullOrWhiteSpace(profile.VoiceDesignReferenceText)))
        {
            GenerationStatus = $"Generate and apply a VoiceDesign reference for {speakerName} before previewing it.";
            return;
        }

        IsBusy = true;
        IsPreviewingVoice = true;
        try
        {
            if (_audioPlayback.IsPlaying || _audioPlayback.IsPaused)
            {
                _audioPlayback.Stop();
                ResetAudioPlayback();
            }

            var language = ResolveEffectiveVoiceLanguage(profile.VoiceId, profile.Language);
            if (language is null)
            {
                GenerationStatus =
                    $"Select a book language or a supported language override for {profile.VoiceId}.";
                return;
            }

            var instruction = _ttsProvider.SupportsVoiceInstructions ? profile.Instruction : string.Empty;
            var text = GetVoicePreviewText(language);
            GenerationStatus = $"Generating a {language} voice preview for {speakerName}…";
            var result = await _ttsProvider.SynthesizeAsync(
                new SpeechRequest(
                    text,
                    profile.VoiceId,
                    language,
                    instruction,
                    profile.VoiceDesignReferenceAudio,
                    profile.VoiceDesignReferenceText),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _audioPlayback.Play(result.WavBytes);
            IsAudioPlaying = _audioPlayback.IsPlaying;
            IsAudioPaused = _audioPlayback.IsPaused;
            UpdateAudioPlaybackTime();
            _audioPlaybackTimer.Start();
            PlayGeneratedAudioCommand.NotifyCanExecuteChanged();
            GenerationStatus = $"Playing the {language} voice preview for {speakerName}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GenerationStatus = $"Voice preview for {speakerName} was canceled.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not generate or play the voice preview: {exception.Message}";
        }
        finally
        {
            IsPreviewingVoice = false;
            IsBusy = false;
        }
    }

    private static string GetVoicePreviewText(string language) =>
        language.ToLowerInvariant() switch
        {
            "chinese" => "你好，这是语音试听。",
            "french" => "Bonjour, ceci est un court aperçu vocal.",
            "german" => "Hallo, dies ist eine kurze Hörprobe.",
            "italian" => "Ciao, questa è una breve anteprima vocale.",
            "japanese" => "こんにちは。これは音声プレビューです。",
            "korean" => "안녕하세요. 음성 미리 듣기입니다.",
            "portuguese" => "Olá, esta é uma breve amostra de voz.",
            "russian" => "Здравствуйте, это короткий голосовой образец.",
            "spanish" => "Hola, esta es una breve muestra de voz.",
            _ => "Hello, this is a short voice preview."
        };

    [RelayCommand(CanExecute = nameof(CanCreateVoiceProposals))]
    private void CreateVoiceProposals()
    {
        foreach (var assignment in VoiceAssignments)
        {
            var effectiveLanguage = GetProfileEffectiveLanguage(assignment.Language);
            var isVoiceDesign = SupportsVoiceDesign;
            var recommendedVoice = isVoiceDesign ? null : FindRecommendedVoice(effectiveLanguage);
            var voiceId = isVoiceDesign
                ? _ttsProvider.VoiceIds[0]
                : recommendedVoice ?? assignment.VoiceId;
            var recommendation = isVoiceDesign
                ? "Qwen VoiceDesign creates a custom voice from this description; no preset voice recommendation applies."
                : BuildVoiceRecommendationMessage(effectiveLanguage, recommendedVoice);
            var instruction = _ttsProvider.SupportsVoiceInstructions
                ? CreateSuggestedVoiceInstruction(assignment, DetectedGenre)
                : string.Empty;
            var sampleText = GetVoicePreviewText(effectiveLanguage ?? BookLanguage);

            assignment.StageProposal(
                new SpeechVoiceProfile(voiceId, assignment.Language, instruction),
                recommendation,
                sampleText);
        }

        VoiceDesignerStatus = SupportsVoiceDesign
            ? $"Prepared staged VoiceDesign descriptions and sample text for {VoiceAssignments.Count} speakers. Generate each reference, preview the Base-model clone, then apply it explicitly."
            : HasDetectedGenre
                ? $"Prepared staged voice profiles for {VoiceAssignments.Count} speakers. Review each profile and apply it explicitly."
                : $"Prepared staged voice profiles for {VoiceAssignments.Count} speakers using a general direction. Analyze book structure for genre-aware directions.";
        ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
        PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
        GenerateVoiceDesignCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateVoiceDesign), IncludeCancelCommand = true)]
    private async Task GenerateVoiceDesignAsync(
        VoiceAssignmentItem assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (_ttsProvider is not IVoiceDesignProvider { SupportsVoiceDesign: true } voiceDesigner ||
            !CanGenerateVoiceDesign(assignment))
        {
            return;
        }

        var language = ResolveEffectiveVoiceLanguage(assignment.ProposedVoiceId, assignment.ProposedLanguage);
        if (language is null)
        {
            VoiceDesignerStatus = $"Select a supported spoken language for {assignment.SpeakerName} first.";
            return;
        }

        IsBusy = true;
        IsPreviewingVoice = true;
        assignment.IsDesigningVoice = true;
        try
        {
            if (_audioPlayback.IsPlaying || _audioPlayback.IsPaused)
            {
                _audioPlayback.Stop();
                ResetAudioPlayback();
            }

            var sampleText = assignment.ProposedSampleText.Trim();
            GenerationStatus = $"Generating a {language} Qwen VoiceDesign sample for {assignment.SpeakerName}…";
            var result = await voiceDesigner.GenerateVoiceDesignAsync(
                new VoiceDesignRequest(sampleText, language, assignment.ProposedInstruction),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            assignment.SetProposedReference(result.WavBytes, sampleText);

            _audioPlayback.Play(result.WavBytes);
            IsAudioPlaying = _audioPlayback.IsPlaying;
            IsAudioPaused = _audioPlayback.IsPaused;
            UpdateAudioPlaybackTime();
            _audioPlaybackTimer.Start();
            PlayGeneratedAudioCommand.NotifyCanExecuteChanged();
            GenerationStatus = $"Playing the generated VoiceDesign sample for {assignment.SpeakerName}.";
            VoiceDesignerStatus =
                $"Generated a staged VoiceDesign reference for {assignment.SpeakerName}. Preview the Base-model clone, then apply it to save.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VoiceDesignerStatus = $"VoiceDesign generation for {assignment.SpeakerName} was canceled.";
        }
        catch (Exception exception)
        {
            VoiceDesignerStatus = $"Could not generate the VoiceDesign reference: {exception.Message}";
        }
        finally
        {
            assignment.IsDesigningVoice = false;
            IsPreviewingVoice = false;
            IsBusy = false;
            ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
            PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
            GenerateVoiceDesignCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyVoiceProposal))]
    private async Task ApplyVoiceProposalAsync(VoiceAssignmentItem assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!CanApplyVoiceProposal(assignment) || _projectId is not { } projectId)
        {
            return;
        }

        var proposedProfile = assignment.ProposedProfile;
        var voiceChanged = !string.Equals(assignment.VoiceId, proposedProfile.VoiceId, StringComparison.Ordinal);
        _voiceAssignmentUpdateDepth++;
        try
        {
            assignment.VoiceId = proposedProfile.VoiceId;
            assignment.SetSupportedLanguages(_ttsProvider.GetLanguagesForVoice(proposedProfile.VoiceId));
            assignment.Language = proposedProfile.Language;
            assignment.Instruction = proposedProfile.Instruction;
            assignment.SetVoiceDesignReference(
                proposedProfile.VoiceDesignReferenceAudio,
                proposedProfile.VoiceDesignReferenceText);
        }
        finally
        {
            _voiceAssignmentUpdateDepth--;
        }

        assignment.MarkProposalApplied();
        ClearGeneratedBookAudio();
        ClearGeneratedAudioForChapterChange();
        GenerateChapterCommand.NotifyCanExecuteChanged();
        GenerateBookCommand.NotifyCanExecuteChanged();
        PreviewVoiceCommand.NotifyCanExecuteChanged();
        VoiceDesignerStatus = $"Applied the proposed profile for {assignment.SpeakerName}.";
        await PersistVoiceAssignmentAsync(projectId, assignment, voiceChanged);
    }

    private bool CanCreateVoiceProposals() =>
        CanEditNarrationContent &&
        !IsAnalyzing &&
        !IsAnalyzingStructure &&
        VoiceAssignments.Count > 0;

    private bool CanApplyVoiceProposal(VoiceAssignmentItem assignment) =>
        assignment is not null &&
        assignment.HasProposal &&
        VoiceAssignments.Contains(assignment) &&
        CanEditNarrationContent &&
        !IsAnalyzing &&
        !IsAnalyzingStructure &&
        _ttsProvider.VoiceIds.Contains(assignment.ProposedVoiceId, StringComparer.Ordinal) &&
        assignment.IsProposedProfileReady;

    private bool CanGenerateVoiceDesign(VoiceAssignmentItem assignment) =>
        assignment is not null &&
        SupportsVoiceDesign &&
        assignment.SupportsVoiceDesign &&
        assignment.HasProposal &&
        VoiceAssignments.Contains(assignment) &&
        !IsBusy &&
        !IsAnalyzing &&
        !IsGenerating &&
        !IsAnalyzingStructure &&
        !IsPreviewingVoice &&
        !string.IsNullOrWhiteSpace(assignment.ProposedSampleText) &&
        assignment.ProposedSampleText.Length <= _ttsProvider.Capabilities.MaxContextCharacters &&
        ResolveEffectiveVoiceLanguage(assignment.ProposedVoiceId, assignment.ProposedLanguage) is not null;

    private bool CanPreviewVoiceProposal(VoiceAssignmentItem assignment) =>
        assignment is not null &&
        assignment.HasProposal &&
        !IsBusy &&
        !IsAnalyzing &&
        !IsGenerating &&
        !IsAnalyzingStructure &&
        !IsPreviewingVoice &&
        VoiceAssignments.Contains(assignment) &&
        _ttsProvider.VoiceIds.Contains(assignment.ProposedVoiceId, StringComparer.Ordinal) &&
        ResolveEffectiveVoiceLanguage(assignment.ProposedVoiceId, assignment.ProposedLanguage) is not null &&
        assignment.IsProposedProfileReady;

    private string? FindRecommendedVoice(string? effectiveLanguage)
    {
        if (string.IsNullOrWhiteSpace(effectiveLanguage))
        {
            return null;
        }

        return _ttsProvider.VoiceIds.FirstOrDefault(voiceId =>
        {
            var nativeLanguage = _ttsProvider.GetVoiceNativeLanguage(voiceId);
            return !string.IsNullOrWhiteSpace(nativeLanguage) &&
                MatchesNativeLanguage(nativeLanguage, effectiveLanguage) &&
                _ttsProvider.GetLanguagesForVoice(voiceId).Contains(
                    effectiveLanguage,
                    StringComparer.OrdinalIgnoreCase);
        });
    }

    private string BuildVoiceRecommendationMessage(string? effectiveLanguage, string? recommendedVoice)
    {
        if (!string.IsNullOrWhiteSpace(recommendedVoice) && !string.IsNullOrWhiteSpace(effectiveLanguage))
        {
            return $"Recommended {recommendedVoice}: verified native-language metadata matches {effectiveLanguage}.";
        }

        if (_ttsProvider.VoiceIds.All(voiceId =>
                string.IsNullOrWhiteSpace(_ttsProvider.GetVoiceNativeLanguage(voiceId))))
        {
            return "No voice recommendation is available because this provider supplies no verified native-language metadata. All voices remain manually selectable.";
        }

        return string.IsNullOrWhiteSpace(effectiveLanguage)
            ? "Select a book language or per-profile language override for a native-language voice recommendation. All voices remain manually selectable."
            : $"No voice recommendation is available for {effectiveLanguage}; no voice with matching verified native-language metadata supports that language. All voices remain manually selectable.";
    }

    private string? GetProfileEffectiveLanguage(string profileLanguage) =>
        string.IsNullOrWhiteSpace(profileLanguage) ? BookLanguage : profileLanguage;

    private static bool MatchesNativeLanguage(string nativeLanguage, string effectiveLanguage)
    {
        var native = nativeLanguage.Trim();
        var effective = effectiveLanguage.Trim();
        return string.Equals(native, effective, StringComparison.OrdinalIgnoreCase) ||
            native.StartsWith($"{effective} (", StringComparison.OrdinalIgnoreCase) ||
            native.StartsWith($"{effective}-", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanSuggestVoiceInstruction))]
    private void SuggestVoiceInstruction(VoiceAssignmentItem assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Instruction = CreateSuggestedVoiceInstruction(assignment, DetectedGenre);
        GenerationStatus = $"Suggested an expressive direction for {assignment.SpeakerName}. Review or edit it before generating.";
    }

    private bool CanSuggestVoiceInstruction(VoiceAssignmentItem assignment) =>
        assignment is not null &&
        assignment.SupportsInstructions &&
        VoiceAssignments.Contains(assignment) &&
        HasDetectedGenre &&
        CanEditNarrationContent;

    private string CreateSuggestedVoiceInstruction(
        VoiceAssignmentItem assignment,
        string genre)
    {
        var normalizedGenre = genre.Trim().ToLowerInvariant();
        var (tone, direction) = normalizedGenre switch
        {
            var value when value.Contains("poetry", StringComparison.Ordinal) =>
                ("lyrical and expressive", "Respect rhythm and line breaks; keep emphasis controlled."),
            var value when value.Contains("fantasy", StringComparison.Ordinal) ||
                          value.Contains("science fiction", StringComparison.Ordinal) ||
                          value.Contains("sci-fi", StringComparison.Ordinal) =>
                ("immersive and vivid", "Use measured pacing and suggest subtle wonder."),
            var value when value.Contains("mystery", StringComparison.Ordinal) ||
                          value.Contains("thriller", StringComparison.Ordinal) ||
                          value.Contains("crime", StringComparison.Ordinal) =>
                ("restrained and quietly tense", "Keep pacing controlled and emphasize clues subtly; avoid melodrama."),
            var value when value.Contains("romance", StringComparison.Ordinal) =>
                ("warm and intimate", "Convey gentle emotional nuance with natural pacing."),
            var value when value.Contains("horror", StringComparison.Ordinal) =>
                ("atmospheric and restrained", "Use deliberate pacing and subtle tension; avoid theatrical scares."),
            var value when value.Contains("nonfiction", StringComparison.Ordinal) ||
                          value.Contains("non-fiction", StringComparison.Ordinal) ||
                          value.Contains("textbook", StringComparison.Ordinal) ||
                          value.Contains("academic", StringComparison.Ordinal) ||
                          value.Contains("history", StringComparison.Ordinal) =>
                ("clear, measured, and informative", "Keep a steady pace, articulate carefully, and use restrained emphasis on key ideas."),
            var value when value.Contains("memoir", StringComparison.Ordinal) ||
                          value.Contains("biography", StringComparison.Ordinal) =>
                ("warm and reflective", "Use natural pacing and gentle emphasis; keep the delivery personal but restrained."),
            _ => ("natural and engaging", "Keep a steady pace, articulate clearly, and use subtle emphasis.")
        };
        var article = "aeiou".Contains(char.ToLowerInvariant(tone[0])) ? "an" : "a";

        if (SupportsVoiceDesign)
        {
            var audiobookContext = string.IsNullOrWhiteSpace(genre)
                ? "an audiobook"
                : $"a {genre.Trim()} audiobook";
            return string.Equals(assignment.SpeakerId, "narrator", StringComparison.Ordinal)
                ? $"Design a natural, clear audiobook narrator voice for {audiobookContext}. Give it {tone} vocal character with distinct, believable timbre. {direction} Keep it expressive but not theatrical."
                : $"Design a distinct, believable character voice for {assignment.SpeakerName} in {audiobookContext}. Give it {tone} vocal character with a clear, natural timbre. {direction} Keep it conversational and not theatrical.";
        }

        return string.Equals(assignment.SpeakerId, "narrator", StringComparison.Ordinal)
            ? $"Narrate in {article} {tone} tone. {direction} Sound natural, not theatrical."
            : $"Portray {assignment.SpeakerName} as a distinct, believable character. Use {article} {tone} tone. {direction} Keep the delivery conversational and natural.";
    }

    [RelayCommand(CanExecute = nameof(CanPauseAudio))]
    private void PauseGeneratedAudio()
    {
        try
        {
            _audioPlayback.Pause();
            IsAudioPlaying = _audioPlayback.IsPlaying;
            IsAudioPaused = _audioPlayback.IsPaused;
            GenerationStatus = "Audio playback paused.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not pause audio: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopAudio))]
    private void StopGeneratedAudio()
    {
        try
        {
            _audioPlayback.Stop();
            ResetAudioPlayback();
            GenerationStatus = "Audio playback stopped.";
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not stop audio: {exception.Message}";
        }
    }

    public void Dispose()
    {
        _audioPlaybackTimer.Stop();
        _audioPlaybackTimer.Tick -= OnAudioPlaybackTimerTick;
        if (_ttsProvider is LocalTtsProviderService localTtsProvider)
        {
            localTtsProvider.PropertyChanged -= OnLocalTtsProviderPropertyChanged;
        }

        ClearGeneratedBookAudio();
    }

    private bool CanGenerateChapter() =>
        !IsBusy && !IsAnalyzing && !IsGenerating && HasAnalysis && !IsAnalysisStale &&
        _ttsProvider.VoiceIds.Count > 0 && HasValidVoiceLanguages() && HasReadyVoiceDesigns() &&
        SelectedChapter is { Role: not SectionRole.Skip };

    private bool CanPreviewVoice(VoiceAssignmentItem assignment) =>
        !IsBusy && !IsAnalyzing && !IsGenerating && !IsAnalyzingStructure &&
        !string.IsNullOrWhiteSpace(BookLanguage) &&
        _ttsProvider.Languages.Contains(BookLanguage, StringComparer.OrdinalIgnoreCase) &&
        !IsPreviewingVoice && VoiceAssignments.Contains(assignment) &&
        _ttsProvider.VoiceIds.Contains(assignment.VoiceId, StringComparer.Ordinal) &&
        ResolveEffectiveVoiceLanguage(assignment) is not null &&
        (!SupportsVoiceDesign ||
         assignment.Profile is { VoiceDesignReferenceAudio: not null, VoiceDesignReferenceText: not null });

    private bool HasValidVoiceLanguages()
    {
        if (string.IsNullOrWhiteSpace(BookLanguage) ||
            !_ttsProvider.Languages.Contains(BookLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return VoiceAssignments.All(assignment => ResolveEffectiveVoiceLanguage(assignment) is not null);
    }

    private bool HasReadyVoiceDesigns() =>
        !SupportsVoiceDesign ||
        VoiceAssignments.All(assignment =>
            assignment.Profile.VoiceDesignReferenceAudio is { Length: >= 44 } &&
            !string.IsNullOrWhiteSpace(assignment.Profile.VoiceDesignReferenceText));

    private string? ResolveEffectiveVoiceLanguage(VoiceAssignmentItem assignment)
    {
        return ResolveEffectiveVoiceLanguage(assignment.VoiceId, assignment.Language);
    }

    private string? ResolveEffectiveVoiceLanguage(string voiceId, string profileLanguage)
    {
        var selectedLanguage = GetProfileEffectiveLanguage(profileLanguage);
        return _ttsProvider.GetLanguagesForVoice(voiceId)
            .FirstOrDefault(language =>
                string.Equals(language, selectedLanguage, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanExportAudio() => _generatedWavBytes is not null && SelectedChapter is not null;

    private bool CanPlayAudio() => _generatedWavBytes is not null && !IsAudioPlaying;

    private bool CanPauseAudio() => IsAudioPlaying;

    private bool CanStopAudio() => IsAudioPlaying || IsAudioPaused;

    private void ClearGeneratedAudioForChapterChange()
    {
        if (_generatedWavBytes is null && !IsAudioPlaying && !IsAudioPaused)
        {
            return;
        }

        try
        {
            _audioPlayback.Stop();
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not stop audio for the selected chapter: {exception.Message}";
        }

        ResetAudioPlayback();
        _generatedWavBytes = null;
        PlayGeneratedAudioCommand.NotifyCanExecuteChanged();
        ExportChapterAudioCommand.NotifyCanExecuteChanged();
    }

    private void ResetAudioPlayback()
    {
        _audioPlaybackTimer.Stop();
        IsAudioPlaying = false;
        IsAudioPaused = false;
        AudioPlaybackProgress = 0;
        AudioPlaybackTime = "0:00 / 0:00";
    }

    private void OnAudioPlaybackTimerTick(object? sender, EventArgs e)
    {
        try
        {
            IsAudioPlaying = _audioPlayback.IsPlaying;
            IsAudioPaused = _audioPlayback.IsPaused;
            UpdateAudioPlaybackTime();
            if (!IsAudioPlaying && !IsAudioPaused)
            {
                _audioPlaybackTimer.Stop();
                if (_audioPlayback.Duration > TimeSpan.Zero)
                {
                    AudioPlaybackProgress = 1;
                    GenerationStatus = "Audio playback finished.";
                }
            }
        }
        catch (Exception exception)
        {
            _audioPlaybackTimer.Stop();
            IsAudioPlaying = false;
            IsAudioPaused = false;
            GenerationStatus = $"Audio playback failed: {exception.Message}";
        }
    }

    private void UpdateAudioPlaybackTime()
    {
        var position = _audioPlayback.Position;
        var duration = _audioPlayback.Duration;
        AudioPlaybackProgress = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;
        AudioPlaybackTime = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.0} MiB"
            : $"{bytes / 1024d:0} KiB";

    private async Task<(IReadOnlyDictionary<string, string> VoiceAssignments,
        IReadOnlyDictionary<string, SpeechVoiceProfile> VoiceProfiles)> SaveAndLoadCurrentVoiceProfilesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var providerId = CurrentTtsProviderId;
        var profiles = new Dictionary<string, SpeechVoiceProfile>(
            await _generationStore.LoadVoiceProfilesAsync(projectId, providerId, cancellationToken),
            StringComparer.Ordinal);
        var voiceAssignments = new Dictionary<string, string>(
            await _generationStore.LoadVoiceAssignmentsAsync(projectId, cancellationToken),
            StringComparer.Ordinal);
        foreach (var assignment in VoiceAssignments)
        {
            var profile = assignment.Profile;
            await _generationStore.SaveVoiceProfileAsync(
                projectId,
                providerId,
                assignment.SpeakerId,
                profile,
                cancellationToken);
            profiles[assignment.SpeakerId] = profile;
            if (providerId != LocalTtsProviderService.QwenProviderId)
            {
                await _generationStore.SaveVoiceAssignmentAsync(
                    projectId,
                    assignment.SpeakerId,
                    profile.VoiceId,
                    cancellationToken);
            }

            voiceAssignments[assignment.SpeakerId] = profile.VoiceId;
        }

        foreach (var key in voiceAssignments.Keys.ToArray())
        {
            if (!_ttsProvider.VoiceIds.Contains(voiceAssignments[key], StringComparer.Ordinal))
            {
                voiceAssignments.Remove(key);
            }
        }

        return (voiceAssignments, profiles);
    }

    private IReadOnlyDictionary<string, SpeechVoiceProfile> BuildVoiceProfiles(
        ChapterAnalysis? analysis,
        IReadOnlyDictionary<string, SpeechVoiceProfile> savedProfiles,
        IReadOnlyDictionary<string, string> legacyAssignments) =>
        _audioGeneration.ResolveVoiceProfiles(UseMultipleVoices ? analysis : null, legacyAssignments, savedProfiles);

    private async Task RefreshVoiceAssignmentsAsync(Guid projectId, ChapterAnalysis? analysis)
    {
        if (_ttsProvider.VoiceIds.Count == 0)
        {
            VoiceAssignments.Clear();
            GenerationStatus = TtsProviderDescription;
            return;
        }

        _voiceAssignmentUpdateDepth++;
        try
        {
            var providerId = CurrentTtsProviderId;
            var saved = await _generationStore.LoadVoiceAssignmentsAsync(projectId);
            var savedProfiles = await _generationStore.LoadVoiceProfilesAsync(projectId, providerId);
            if (_projectId != projectId ||
                CurrentTtsProviderId != providerId ||
                (analysis is not null && !ReferenceEquals(_analysis, analysis)))
            {
                return;
            }

            var profiles = BuildVoiceProfiles(analysis, savedProfiles, saved);
            var desired = new List<(string Id, string Name)> { ("narrator", "Narrator") };
            if (UseMultipleVoices && analysis is not null)
            {
                desired.AddRange(analysis.Characters.Select(character => (character.Id, character.Name)));
            }

            VoiceAssignments.Clear();
            for (var index = 0; index < desired.Count; index++)
            {
                var (id, name) = desired[index];
                var profile = profiles[id];
                if (!savedProfiles.ContainsKey(id) &&
                    saved.TryGetValue(id, out var savedVoice) &&
                    !_ttsProvider.VoiceIds.Contains(savedVoice, StringComparer.Ordinal))
                {
                    GenerationStatus =
                        $"Saved voice '{savedVoice}' for {name} is unavailable in {TtsProviderName}; using {profile.VoiceId}.";
                }
                else if (savedProfiles.TryGetValue(id, out var savedProfile) &&
                         !VoiceProfilesEqual(savedProfile, profile))
                {
                    GenerationStatus =
                        $"Saved voice profile for {name} is unavailable in {TtsProviderName}; using a supported profile.";
                }

                VoiceAssignments.Add(new VoiceAssignmentItem(
                    id,
                    name,
                    _ttsProvider.VoiceIds,
                    _ttsProvider.GetVoiceNativeLanguage,
                    _ttsProvider.GetLanguagesForVoice(profile.VoiceId),
                    _ttsProvider.SupportsVoiceInstructions,
                    profile,
                    _ttsProvider.GetLanguagesForVoice,
                    SupportsVoiceDesign));
                VoiceAssignments[^1].PropertyChanged += OnVoiceAssignmentChanged;
                if (!savedProfiles.TryGetValue(id, out var existingProfile) ||
                    !VoiceProfilesEqual(existingProfile, profile))
                {
                    await _generationStore.SaveVoiceProfileAsync(projectId, providerId, id, profile);
                    if (providerId != LocalTtsProviderService.QwenProviderId &&
                        !saved.ContainsKey(id))
                    {
                        await _generationStore.SaveVoiceAssignmentAsync(projectId, id, profile.VoiceId);
                    }
                }
            }
        }
        finally
        {
            _voiceAssignmentUpdateDepth--;
            GenerateChapterCommand.NotifyCanExecuteChanged();
            GenerateBookCommand.NotifyCanExecuteChanged();
            PreviewVoiceCommand.NotifyCanExecuteChanged();
            CreateVoiceProposalsCommand.NotifyCanExecuteChanged();
            ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
            PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
            GenerateVoiceDesignCommand.NotifyCanExecuteChanged();
        }
    }

    private static bool VoiceProfilesEqual(SpeechVoiceProfile left, SpeechVoiceProfile right)
    {
        if (!string.Equals(left.VoiceId, right.VoiceId, StringComparison.Ordinal) ||
            !string.Equals(left.Language, right.Language, StringComparison.Ordinal) ||
            !string.Equals(left.Instruction, right.Instruction, StringComparison.Ordinal) ||
            !string.Equals(
                left.VoiceDesignReferenceText,
                right.VoiceDesignReferenceText,
                StringComparison.Ordinal))
        {
            return false;
        }

        return left.VoiceDesignReferenceAudio is null
            ? right.VoiceDesignReferenceAudio is null
            : right.VoiceDesignReferenceAudio is not null &&
              left.VoiceDesignReferenceAudio.AsSpan().SequenceEqual(right.VoiceDesignReferenceAudio);
    }

    private async Task RefreshVoiceAssignmentsSafelyAsync(Guid projectId, ChapterAnalysis? analysis)
    {
        try
        {
            await RefreshVoiceAssignmentsAsync(projectId, analysis);
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not load voice assignments: {exception.Message}";
        }
    }

    private void OnVoiceAssignmentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not VoiceAssignmentItem assignment)
        {
            return;
        }

        if (e.PropertyName == nameof(VoiceAssignmentItem.Profile))
        {
            GenerateChapterCommand.NotifyCanExecuteChanged();
            GenerateBookCommand.NotifyCanExecuteChanged();
            PreviewVoiceCommand.NotifyCanExecuteChanged();
            return;
        }

        if (e.PropertyName is nameof(VoiceAssignmentItem.HasProposal) or
            nameof(VoiceAssignmentItem.ProposedVoiceId) or
            nameof(VoiceAssignmentItem.ProposedLanguage) or
            nameof(VoiceAssignmentItem.ProposedInstruction) or
            nameof(VoiceAssignmentItem.ProposedSampleText) or
            nameof(VoiceAssignmentItem.ProposedReferenceAudio) or
            nameof(VoiceAssignmentItem.ProposedReferenceText))
        {
            if (e.PropertyName == nameof(VoiceAssignmentItem.ProposedLanguage) && assignment.SupportsVoiceDesign)
            {
                var language = GetProfileEffectiveLanguage(assignment.ProposedLanguage);
                assignment.ProposedSampleText = GetVoicePreviewText(language ?? BookLanguage);
            }

            ApplyVoiceProposalCommand.NotifyCanExecuteChanged();
            PreviewVoiceProposalCommand.NotifyCanExecuteChanged();
            GenerateVoiceDesignCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_voiceAssignmentUpdateDepth > 0 || IsBusy || IsGenerating ||
            e.PropertyName is not (nameof(VoiceAssignmentItem.VoiceId) or
                nameof(VoiceAssignmentItem.Language) or
                nameof(VoiceAssignmentItem.Instruction)) ||
            _projectId is not { } projectId)
        {
            return;
        }

        if (e.PropertyName == nameof(VoiceAssignmentItem.VoiceId))
        {
            _voiceAssignmentUpdateDepth++;
            try
            {
                assignment.SetSupportedLanguages(_ttsProvider.GetLanguagesForVoice(assignment.VoiceId));
            }
            finally
            {
                _voiceAssignmentUpdateDepth--;
            }
        }

        ClearGeneratedBookAudio();
        ClearGeneratedAudioForChapterChange();
        GenerateChapterCommand.NotifyCanExecuteChanged();
        GenerateBookCommand.NotifyCanExecuteChanged();
        PreviewVoiceCommand.NotifyCanExecuteChanged();
        _ = PersistVoiceAssignmentAsync(projectId, assignment, e.PropertyName == nameof(VoiceAssignmentItem.VoiceId));
    }

    private async Task PersistVoiceAssignmentAsync(
        Guid projectId,
        VoiceAssignmentItem assignment,
        bool persistLegacyVoice = false)
    {
        var providerId = CurrentTtsProviderId;
        var applyingProposal = assignment.IsProposalApplied;
        try
        {
            var profile = assignment.Profile;
            await _generationStore.SaveVoiceProfileAsync(
                projectId,
                providerId,
                assignment.SpeakerId,
                profile);
            if (persistLegacyVoice && providerId != LocalTtsProviderService.QwenProviderId)
            {
                await _generationStore.SaveVoiceAssignmentAsync(projectId, assignment.SpeakerId, profile.VoiceId);
            }

            if (_projectId == projectId && CurrentTtsProviderId == providerId)
            {
                await RefreshChapterQueueAsync();
                GenerationStatus = $"Saved the voice profile for {assignment.SpeakerName}.";
                if (applyingProposal)
                {
                    VoiceDesignerStatus = $"Applied and saved the profile for {assignment.SpeakerName}.";
                }
            }
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Could not save the voice profile: {exception.Message}";
            if (applyingProposal)
            {
                VoiceDesignerStatus = $"Could not save the applied profile for {assignment.SpeakerName}: {exception.Message}";
            }
        }
    }

    private async Task LoadAnalysisForSelectionAsync()
    {
        var version = ++_analysisLoadVersion;
        var chapter = SelectedChapter;
        var projectId = _projectId;
        ApplySpeechReview(null);
        ApplyAnalysis(null);
        if (chapter is null || projectId is null)
        {
            return;
        }

        try
        {
            var review = await _narrativeStore.LoadSpeechReviewAsync(projectId.Value, chapter.Id);
            if (version != _analysisLoadVersion)
            {
                return;
            }

            ApplySpeechReview(review);
            var analysis = await _narrativeStore.LoadAnalysisAsync(projectId.Value, chapter.Id);
            if (version != _analysisLoadVersion)
            {
                return;
            }

            ApplyAnalysis(analysis);
            await RefreshVoiceAssignmentsAsync(projectId.Value, IsAnalysisStale ? null : analysis);
            if (version != _analysisLoadVersion)
            {
                return;
            }

            if (!IsAnalyzing)
            {
                AnalysisStatus = analysis is null
                    ? "This chapter has not been analyzed yet."
                    : IsAnalysisStale
                        ? "The chapter text changed since it was analyzed."
                        : $"{analysis.Segments.Count} segments, {analysis.Characters.Count} characters.";
            }
        }
        catch (Exception exception) when (version == _analysisLoadVersion)
        {
            AnalysisStatus = $"Could not load analysis: {exception.Message}";
        }
    }

    private void ApplyAnalysis(ChapterAnalysis? analysis)
    {
        _analysis = analysis;
        Segments.Clear();
        Characters.Clear();
        OnPropertyChanged(nameof(HasNarrativeSegments));
        OnPropertyChanged(nameof(HasCharacters));
        VoiceAssignments.Clear();
        _generatedWavBytes = null;
        PlayGeneratedAudioCommand.NotifyCanExecuteChanged();
        ExportChapterAudioCommand.NotifyCanExecuteChanged();
        GenerateChapterCommand.NotifyCanExecuteChanged();
        IsAnalysisStale = false;
        HasAnalysis = false;
        if (analysis is null || SelectedChapter is null)
        {
            return;
        }

        var text = GetChapterText(SelectedChapter);
        if (!analysis.Matches(
                text,
                UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
                SelectedChapter.Role))
        {
            IsAnalysisStale = true;
            return;
        }

        var names = analysis.Characters.ToDictionary(character => character.Id, character => character.Name);
        foreach (var segment in analysis.Segments)
        {
            var isNarration = segment.Kind == SegmentKind.Narration;
            var speaker = isNarration
                ? "Narrator"
                : names.GetValueOrDefault(segment.SpeakerId ?? string.Empty, "Unknown speaker");
            var review = _speechReview?.ChapterId == SelectedChapter.Id ? _speechReview : null;
            Segments.Add(new SegmentItem(
                segment.Index,
                speaker,
                isNarration,
                SpeechScriptBuilder.ApplyToSegment(text, segment, review).Trim()));
        }

        foreach (var character in analysis.Characters)
        {
            var lines = analysis.Segments.Count(segment => segment.SpeakerId == character.Id);
            Characters.Add(new CharacterItem(character.Name, lines == 1 ? "1 line" : $"{lines} lines"));
        }

        OnPropertyChanged(nameof(HasNarrativeSegments));
        OnPropertyChanged(nameof(HasCharacters));
        HasAnalysis = true;
        UpdateSelectedQueueStatus();
    }

    private void ApplySpeechReview(SpeechEditReview? review)
    {
        _speechReview = review;
        SpeechEdits.Clear();
        OnPropertyChanged(nameof(HasSpeechEdits));
        OnPropertyChanged(nameof(HasNoSpeechEdits));
        if (review is not null)
        {
            foreach (var suggestion in review.Suggestions)
            {
                SpeechEdits.Add(new SpeechEditItem(suggestion, AcceptSpeechEditAsync, RejectSpeechEditAsync));
            }
        }

        OnPropertyChanged(nameof(HasSpeechEdits));
        OnPropertyChanged(nameof(HasNoSpeechEdits));
        OnPropertyChanged(nameof(IsSpeechReviewStale));
        if (_analysis is not null)
        {
            ApplyAnalysis(_analysis);
        }
        else if (SelectedChapter is { } chapter)
        {
            RefreshAnalysisStaleness(chapter);
        }

        UpdateSelectedQueueStatus();
    }

    private void RefreshAnalysisStaleness(ImportedChapter chapter)
    {
        if (_analysis is null)
        {
            return;
        }

        var stale = !_analysis.Matches(
            GetChapterText(chapter),
            UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator,
            chapter.Role);
        if (stale != IsAnalysisStale)
        {
            ApplyAnalysis(_analysis);
        }
    }
}
