using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.App.ViewModels;

public sealed record SegmentItem(int Index, string Speaker, bool IsNarration, string Text);

public sealed record CharacterItem(string Name, string Detail);

public sealed partial class MainWindowViewModel
{
    private ChapterAnalysis? _analysis;
    private int _analysisLoadVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeChapterCommand))]
    private bool _isAnalyzing;

    [ObservableProperty]
    private double _analysisProgress;

    [ObservableProperty]
    private string _analysisStatus = "Select a chapter to analyze.";

    [ObservableProperty]
    private bool _hasAnalysis;

    [ObservableProperty]
    private bool _isAnalysisStale;

    [ObservableProperty]
    private bool _useMultipleVoices;

    public ObservableCollection<SegmentItem> Segments { get; } = [];

    public ObservableCollection<CharacterItem> Characters { get; } = [];

    public string AnalyzerName => _narrativeWorker.Capabilities.Name;

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
        try
        {
            // The project row must exist before narrative data can reference it.
            await FlushPendingSaveAsync();
            var known = await _narrativeStore.LoadCharactersAsync(projectId, cancellationToken);
            var progress = new Progress<AnalysisProgress>(report =>
            {
                AnalysisProgress = report.Fraction;
                AnalysisStatus = report.Message;
            });
            var analysis = await _narrativeWorker.AnalyzeChapterAsync(
                new AnalysisRequest(
                    chapter.Id,
                    text,
                    known,
                    UseMultipleVoices ? NarrationMode.MultiVoice : NarrationMode.SingleNarrator),
                progress,
                cancellationToken);
            await _narrativeStore.SaveAnalysisAsync(projectId, analysis, cancellationToken);

            _analysisLoadVersion++;
            if (_projectId == projectId && SelectedChapter?.Id == chapter.Id)
            {
                ApplyAnalysis(analysis);
            }

            AnalysisStatus = $"Found {analysis.Segments.Count} segments and {analysis.Characters.Count} characters.";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatus = "Analysis cancelled.";
        }
        catch (Exception exception)
        {
            AnalysisStatus = $"Analysis failed: {exception.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => !IsBusy && !IsAnalyzing && Book is not null && SelectedChapter is not null;

    private async Task LoadAnalysisForSelectionAsync()
    {
        var version = ++_analysisLoadVersion;
        var chapter = SelectedChapter;
        var projectId = _projectId;
        ApplyAnalysis(null);
        if (chapter is null || projectId is null)
        {
            return;
        }

        try
        {
            var analysis = await _narrativeStore.LoadAnalysisAsync(projectId.Value, chapter.Id);
            if (version != _analysisLoadVersion)
            {
                return;
            }

            ApplyAnalysis(analysis);
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
        IsAnalysisStale = false;
        HasAnalysis = false;
        if (analysis is null || SelectedChapter is null)
        {
            return;
        }

        var text = GetChapterText(SelectedChapter);
        if (!analysis.Matches(text))
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
            Segments.Add(new SegmentItem(segment.Index, speaker, isNarration, segment.TextFrom(text).Trim()));
        }

        foreach (var character in analysis.Characters)
        {
            var lines = analysis.Segments.Count(segment => segment.SpeakerId == character.Id);
            Characters.Add(new CharacterItem(character.Name, lines == 1 ? "1 line" : $"{lines} lines"));
        }

        HasAnalysis = true;
    }

    private void RefreshAnalysisStaleness(ImportedChapter chapter)
    {
        if (_analysis is null)
        {
            return;
        }

        var stale = !_analysis.Matches(GetChapterText(chapter));
        if (stale != IsAnalysisStale)
        {
            ApplyAnalysis(_analysis);
        }
    }
}
