using Microsoft.Data.Sqlite;
using VBook.Core.Books;
using VBook.Core.Projects;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class SqliteProjectStoreTests
{
    [Fact]
    public async Task VoiceProfiles_AreScopedByProjectProviderAndSpeaker()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var project = new AudiobookProject(
                Guid.NewGuid(), "Book", "book.epub", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new ImportedChapter("c1", "Chapter", "Text.")]);
            await store.SaveAsync(project);
            await store.SaveVoiceAssignmentAsync(project.Id, "narrator", "af_heart");
            var qwen = new SpeechVoiceProfile("Ryan", "English", "Speak calmly.");
            await store.SaveVoiceProfileAsync(project.Id, "Qwen3-TTS", "narrator", qwen);
            await store.SaveVoiceProfileAsync(project.Id, "Kokoro", "narrator", new("af_heart", "Auto", ""));

            var reopened = new SqliteProjectStore(databasePath);
            await reopened.InitializeAsync();
            Assert.Equal(qwen, (await reopened.LoadVoiceProfilesAsync(project.Id, "Qwen3-TTS"))["narrator"]);
            Assert.Equal("af_heart", (await reopened.LoadVoiceProfilesAsync(project.Id, "Kokoro"))["narrator"].VoiceId);
            Assert.Equal("af_heart", (await reopened.LoadVoiceAssignmentsAsync(project.Id))["narrator"]);
            Assert.Empty(await reopened.LoadVoiceProfilesAsync(project.Id, "Piper"));
            await reopened.DeleteAsync(project.Id);
            Assert.Empty(await reopened.LoadVoiceProfilesAsync(project.Id, "Qwen3-TTS"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

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
            var structureAnalysis = new BookStructureResult(
                "fantasy",
                [
                    new ChapterStructureSuggestion(
                        "chapter-1",
                        SectionRole.FrontMatter,
                        0.91,
                        "Introduces the story.")
                ]);
            var project = new AudiobookProject(
                projectId,
                "Sample book",
                @"C:\Books\sample.epub",
                createdAt,
                DateTimeOffset.UtcNow,
                [
                    new ImportedChapter("chapter-1", "Opening", "Edited opening text.", SectionRole.FrontMatter),
                    new ImportedChapter("chapter-2", "Next", "Second chapter.", SectionRole.BackMatter)
                ],
                VBook.Core.Narrative.NarrationMode.SingleNarrator,
                structureAnalysis);

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
            Assert.Equal(SectionRole.FrontMatter, loaded.Chapters[0].Role);
            Assert.Equal(SectionRole.BackMatter, loaded.Chapters[1].Role);
            Assert.Equal(VBook.Core.Narrative.NarrationMode.SingleNarrator, loaded.NarrationMode);
            Assert.NotNull(loaded.StructureAnalysis);
            Assert.Equal("fantasy", loaded.StructureAnalysis.Genre);
            var savedSuggestion = Assert.Single(loaded.StructureAnalysis.Chapters);
            Assert.Equal("chapter-1", savedSuggestion.ChapterId);
            Assert.Equal(SectionRole.FrontMatter, savedSuggestion.Role);
            Assert.Equal(0.91, savedSuggestion.Confidence);
            Assert.Equal("Introduces the story.", savedSuggestion.Rationale);
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

    [Fact]
    public async Task Initialize_MigratesExistingProjectAndAnalysisTables()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var projectId = Guid.NewGuid();
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE Projects (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Title TEXT NOT NULL,
                        SourcePath TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    CREATE TABLE ChapterAnalyses (
                        ProjectId TEXT NOT NULL,
                        ChapterId TEXT NOT NULL,
                        SourceHash TEXT NOT NULL,
                        AnalyzedAt TEXT NOT NULL,
                        PRIMARY KEY (ProjectId, ChapterId)
                    );
                    INSERT INTO Projects (Id, Title, SourcePath, CreatedAt, UpdatedAt)
                    VALUES ($id, 'Legacy', 'legacy.epub', $created, $updated);
                    INSERT INTO ChapterAnalyses (ProjectId, ChapterId, SourceHash, AnalyzedAt)
                    VALUES ($id, 'c1', 'hash', $updated);
                    """;
                command.Parameters.AddWithValue("$id", projectId.ToString("D"));
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();

            var project = await store.LoadAsync(projectId);
            var analysis = await store.LoadAnalysisAsync(projectId, "c1");

            Assert.NotNull(project);
            Assert.Null(project.NarrationMode);
            Assert.NotNull(analysis);
            Assert.Equal(VBook.Core.Narrative.NarrationMode.MultiVoice, analysis.Mode);
            Assert.Equal(SectionRole.Body, analysis.SectionRole);
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
