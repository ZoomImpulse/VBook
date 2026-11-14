using VBook.Core.Books;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class BookStructureAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_BatchesWithinBudgetAndRestoresOriginalOrder()
    {
        var provider = new RecordingStructureProvider();
        var chapters = Enumerable.Range(1, 4)
            .Select(index => new ImportedChapter($"c{index}", $"Chapter {index}", new string('x', 2000)))
            .ToArray();
        var analyzer = new BookStructureAnalyzer(provider);

        var result = await analyzer.AnalyzeAsync("A Book", chapters, null, CancellationToken.None);

        Assert.Equal(4, result.Chapters.Count);
        Assert.Equal(["c1", "c2", "c3", "c4"], result.Chapters.Select(item => item.ChapterId));
        Assert.True(provider.Requests.Count > 1);
        Assert.All(provider.Requests.SelectMany(request => request.Chapters), chapter =>
            Assert.True(chapter.Excerpt.Length <= 480));
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsMissingChapterSuggestions()
    {
        var analyzer = new BookStructureAnalyzer(new MissingSuggestionProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => analyzer.AnalyzeAsync(
            "A Book",
            [
                new ImportedChapter("c1", "One", "Text."),
                new ImportedChapter("c2", "Two", "Text.")
            ],
            null,
            CancellationToken.None));
    }

    private sealed class RecordingStructureProvider : IBookStructureProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("test structure provider", true, 1000);

        public List<BookStructureRequest> Requests { get; } = [];

        public Task<BookStructureResult> AnalyzeAsync(
            BookStructureRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new BookStructureResult(
                "fiction",
                request.Chapters.Select(chapter => new ChapterStructureSuggestion(
                    chapter.Id,
                    SectionRole.Body,
                    0.9,
                    "Chapter content indicates main text.")).ToArray()));
        }
    }

    private sealed class MissingSuggestionProvider : IBookStructureProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("incomplete provider", true, 1000);

        public Task<BookStructureResult> AnalyzeAsync(
            BookStructureRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookStructureResult(
                "fiction",
                [new ChapterStructureSuggestion("c1", SectionRole.Body, 0.8, "First chapter.")]));
    }
}
