using Microsoft.Data.Sqlite;
using VBook.Core.Books;
using VBook.Core.Projects;
using Xunit;

namespace VBook.Core.Tests;

public sealed class SqliteProjectStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesEditedTextAndChapterOrder()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var project = new AudiobookProject(
                projectId,
                "Sample book",
                @"C:\Books\sample.epub",
                createdAt,
                DateTimeOffset.UtcNow,
                [
                    new ImportedChapter("chapter-1", "Opening", "Edited opening text."),
                    new ImportedChapter("chapter-2", "Next", "Second chapter.")
                ]);

            await store.SaveAsync(project);

            var restartedStore = new SqliteProjectStore(databasePath);
            await restartedStore.InitializeAsync();
            var loaded = await restartedStore.LoadAsync(projectId);
            var recent = await restartedStore.GetRecentAsync();

            Assert.NotNull(loaded);
            Assert.Equal(project.Title, loaded.Title);
            Assert.Equal(project.SourcePath, loaded.SourcePath);
            Assert.Equal(createdAt, loaded.CreatedAt);
            Assert.Equal(
                new[] { "chapter-1", "chapter-2" },
                loaded.Chapters.Select(chapter => chapter.Id));
            Assert.Equal("Edited opening text.", loaded.Chapters[0].Text);
            Assert.Equal(projectId, Assert.Single(recent).Id);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Save_WhenChapterInsertFails_RollsBackPreviousProject()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var original = new AudiobookProject(
                Guid.NewGuid(),
                "Sample book",
                "sample.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ImportedChapter("chapter-1", "Opening", "Original text.")]);
            await store.SaveAsync(original);
            var invalidReplacement = original with
            {
                Title = "Changed title",
                Chapters =
                [
                    new ImportedChapter("duplicate", "One", "First."),
                    new ImportedChapter("duplicate", "Two", "Second.")
                ]
            };

            await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(invalidReplacement));

            var loaded = await store.LoadAsync(original.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Sample book", loaded.Title);
            Assert.Equal("Original text.", Assert.Single(loaded.Chapters).Text);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Delete_RemovesProjectAndItsDependentData()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Delete me",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ImportedChapter("chapter-1", "Opening", "Text.")]));
            await store.SaveAnalysisAsync(projectId, new VBook.Core.Narrative.ChapterAnalysis(
                "chapter-1",
                VBook.Core.Narrative.TextFingerprint.Compute("Text."),
                [],
                [new VBook.Core.Narrative.NarrativeSegment(0, VBook.Core.Narrative.SegmentKind.Narration, null, 0, 5)]));

            Assert.True(await store.DeleteAsync(projectId));
            Assert.False(await store.DeleteAsync(projectId));
            Assert.Null(await store.LoadAsync(projectId));
            Assert.Empty(await store.GetRecentAsync());
            Assert.Empty(await store.LoadCharactersAsync(projectId));
            Assert.Null(await store.LoadAnalysisAsync(projectId, "chapter-1"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
