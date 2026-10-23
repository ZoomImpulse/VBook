using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using Xunit;

namespace VBook.Core.Tests;

public sealed class NarrativeStoreTests
{
    [Fact]
    public async Task SaveAndLoadAnalysis_RoundTripsAndKeepsProjectWideCharacters()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = await SaveProjectAsync(store);
            var first = new ChapterAnalysis(
                "c1",
                "HASH1",
                [new Character("mary", "Mary", ["Mar"])],
                [
                    new NarrativeSegment(0, SegmentKind.Narration, null, 0, 5),
                    new NarrativeSegment(1, SegmentKind.Dialogue, "mary", 5, 7),
                ]);
            var second = new ChapterAnalysis(
                "c2",
                "HASH2",
                [new Character("mary", "Mary", ["Mary J."]), new Character("tom", "Tom", [])],
                [
                    new NarrativeSegment(0, SegmentKind.Dialogue, "tom", 0, 4),
                    new NarrativeSegment(1, SegmentKind.Dialogue, "mary", 4, 4),
                ]);

            await store.SaveAnalysisAsync(projectId, first);
            await store.SaveAnalysisAsync(projectId, second);
            await store.SaveAnalysisAsync(projectId, first with { SourceHash = "HASH1B" });

            var reopened = new SqliteProjectStore(databasePath);
            await reopened.InitializeAsync();
            var loaded = await reopened.LoadAnalysisAsync(projectId, "c1");
            var characters = await reopened.LoadCharactersAsync(projectId);

            Assert.NotNull(loaded);
            Assert.Equal("HASH1B", loaded.SourceHash);
            Assert.Equal(
                [(0, SegmentKind.Narration, (string?)null, 0, 5), (1, SegmentKind.Dialogue, "mary", 5, 7)],
                loaded.Segments.Select(s => (s.Index, s.Kind, s.SpeakerId, s.Start, s.Length)));
            Assert.Equal("mary", Assert.Single(loaded.Characters).Id);
            Assert.Equal(["mary", "tom"], characters.Select(c => c.Id));
            Assert.Equal(["Mar", "Mary J."], characters[0].Aliases.Order());
            Assert.Null(await reopened.LoadAnalysisAsync(projectId, "missing"));
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task ResavingProject_KeepsStoredAnalyses()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = await SaveProjectAsync(store);
            await store.SaveAnalysisAsync(projectId, new ChapterAnalysis(
                "c1", "H", [], [new NarrativeSegment(0, SegmentKind.Narration, null, 0, 3)]));

            var project = await store.LoadAsync(projectId);
            await store.SaveAsync(project! with { UpdatedAt = DateTimeOffset.UtcNow });

            Assert.Single((await store.LoadAnalysisAsync(projectId, "c1"))!.Segments);
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task SaveAnalysis_ForUnknownProject_FailsWithoutPartialData()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var orphan = Guid.NewGuid();

            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAnalysisAsync(
                orphan,
                new ChapterAnalysis("c1", "H", [new Character("a", "A", [])], [])));

            Assert.Empty(await store.LoadCharactersAsync(orphan));
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    private static async Task<Guid> SaveProjectAsync(SqliteProjectStore store)
    {
        var projectId = Guid.NewGuid();
        await store.SaveAsync(new AudiobookProject(
            projectId,
            "Book",
            "book.epub",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [new ImportedChapter("c1", "One", "Text one."), new ImportedChapter("c2", "Two", "Text two.")]));
        return projectId;
    }

    private static void DeleteDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
