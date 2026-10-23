using VBook.App.Services;
using VBook.App.ViewModels;
using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;
using Xunit;

namespace VBook.App.Tests;

public sealed class NarrativeViewModelTests : IAsyncLifetime
{
    private const string Chapter1 = "“Hello,” said Mary.\n\nTom nodded. “Hi,” said Tom.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vbook-app-test-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Analyze_ShowsSegmentsAndCharacters_AndSurvivesReopen()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasAnalysis);
        Assert.Equal(
            ["Mary", "Narrator", "Tom"],
            viewModel.Segments.Select(segment => segment.Speaker).Distinct().Order());
        Assert.Equal(["Mary", "Tom"], viewModel.Characters.Select(c => c.Name).Order());
        Assert.Contains("segments", viewModel.AnalysisStatus);

        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.HasAnalysis);
        Assert.Equal(viewModel.Segments.Count, reopened.Segments.Count);
    }

    [Fact]
    public async Task EditingText_MarksAnalysisStale_AndRestoringItRecovers()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        viewModel.ChapterText += " Extra.";
        Assert.True(viewModel.IsAnalysisStale);
        Assert.False(viewModel.HasAnalysis);
        Assert.Empty(viewModel.Segments);

        viewModel.ChapterText = Chapter1;
        Assert.False(viewModel.IsAnalysisStale);
        Assert.True(viewModel.HasAnalysis);
        Assert.NotEmpty(viewModel.Segments);
    }

    [Fact]
    public async Task Cancel_StopsAnalysis_AndKeepsPreviousStateUsable()
    {
        var provider = new BlockingProvider();
        var (viewModel, _) = await CreateImportedViewModelAsync(provider);

        var running = viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        await provider.Started.Task;
        Assert.True(viewModel.IsAnalyzing);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));
        viewModel.AnalyzeChapterCancelCommand.Execute(null);
        await running;

        Assert.False(viewModel.IsAnalyzing);
        Assert.Equal("Analysis cancelled.", viewModel.AnalysisStatus);
        Assert.False(viewModel.HasAnalysis);
        Assert.True(viewModel.AnalyzeChapterCommand.CanExecute(null));
    }

    [Fact]
    public async Task SingleNarratorMode_ProducesNarrationOnlyWithoutCharacters()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        viewModel.UseMultipleVoices = false;

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasAnalysis);
        Assert.Empty(viewModel.Characters);
        Assert.All(viewModel.Segments, segment => Assert.True(segment.IsNarration));
    }

    [Fact]
    public async Task DeleteProject_RequiresConfirmationAndClearsOpenWorkspace()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());
        var projectId = Assert.Single(viewModel.RecentProjects).Id;
        var sourcePath = Path.Combine(_directory, "book.epub");

        viewModel.DeleteRecentProjectCommand.Execute(null);

        Assert.True(viewModel.IsDeleteConfirmationVisible);
        Assert.NotNull(viewModel.Book);
        viewModel.CancelDeleteProjectCommand.Execute(null);
        Assert.False(viewModel.IsDeleteConfirmationVisible);

        viewModel.DeleteRecentProjectCommand.Execute(null);
        await viewModel.ConfirmDeleteProjectCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleteConfirmationVisible);
        Assert.Null(viewModel.Book);
        Assert.Empty(viewModel.Chapters);
        Assert.Empty(viewModel.RecentProjects);
        Assert.Null(await store.LoadAsync(projectId));
        Assert.True(File.Exists(sourcePath));
        Assert.Contains("Deleted project", viewModel.StatusMessage);
    }

    [Fact]
    public async Task WorkerFailure_IsReportedInStatus()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new FailingProvider());

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.StartsWith("Analysis failed:", viewModel.AnalysisStatus);
        Assert.False(viewModel.IsAnalyzing);
    }

    private async Task<(MainWindowViewModel ViewModel, SqliteProjectStore Store)> CreateImportedViewModelAsync(
        ILlmProvider provider)
    {
        var store = new SqliteProjectStore(Path.Combine(_directory, "projects.sqlite3"));
        var viewModel = CreateViewModel(store, provider);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.SelectedChapter);
        viewModel.UseMultipleVoices = true;
        return (viewModel, store);
    }

    private MainWindowViewModel CreateViewModel(SqliteProjectStore store, ILlmProvider provider)
    {
        var epubPath = Path.Combine(_directory, "book.epub");
        File.WriteAllBytes(epubPath, [0]);
        var worker = new ProtocolNarrativeWorker(
            new LocalWorkerHost(new ChapterAnalyzer(provider)),
            provider.Capabilities);
        return new MainWindowViewModel(new FakeImporter(), store, new FakePicker(epubPath), worker, store);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class FakePicker(string path) : IFilePickerService
    {
        public Task<string?> PickEpubAsync() => Task.FromResult<string?>(path);
    }

    private sealed class FakeImporter : IBookImporter
    {
        public ImportedBook Import(Stream stream) =>
            new("Test Book", [new ImportedChapter("c1", "Chapter One", Chapter1)]);
    }

    private sealed class BlockingProvider : ILlmProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities Capabilities { get; } = new("blocking", true, 1000);

        public async Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FailingProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("failing", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("model crashed");
    }
}
