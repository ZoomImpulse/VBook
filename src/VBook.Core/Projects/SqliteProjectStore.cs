using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.Core.Projects;

public sealed partial class SqliteProjectStore : IProjectStore, INarrativeStore, IGenerationStore
{
    private static readonly JsonSerializerOptions ProjectJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _databasePath;

    public SqliteProjectStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT NOT NULL PRIMARY KEY,
                Title TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                NarrationMode TEXT NULL,
                StructureAnalysis TEXT NULL,
                DefaultLanguage TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Chapters (
                ProjectId TEXT NOT NULL,
                Id TEXT NOT NULL,
                Position INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Text TEXT NOT NULL,
                Role TEXT NOT NULL DEFAULT 'Body',
                PRIMARY KEY (ProjectId, Id),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Projects_UpdatedAt
                ON Projects(UpdatedAt DESC);

            CREATE TABLE IF NOT EXISTS Characters (
                ProjectId TEXT NOT NULL,
                Id TEXT NOT NULL,
                Name TEXT NOT NULL,
                Aliases TEXT NOT NULL,
                PRIMARY KEY (ProjectId, Id),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ChapterAnalyses (
                ProjectId TEXT NOT NULL,
                ChapterId TEXT NOT NULL,
                SourceHash TEXT NOT NULL,
                NarrationMode TEXT NOT NULL DEFAULT 'MultiVoice',
                SectionRole TEXT NOT NULL DEFAULT 'Body',
                AnalyzedAt TEXT NOT NULL,
                PRIMARY KEY (ProjectId, ChapterId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Segments (
                ProjectId TEXT NOT NULL,
                ChapterId TEXT NOT NULL,
                Idx INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                SpeakerId TEXT NULL,
                Start INTEGER NOT NULL,
                Length INTEGER NOT NULL,
                PRIMARY KEY (ProjectId, ChapterId, Idx),
                FOREIGN KEY (ProjectId, ChapterId)
                    REFERENCES ChapterAnalyses(ProjectId, ChapterId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SpeechReviews (
                ProjectId TEXT NOT NULL,
                ChapterId TEXT NOT NULL,
                SourceHash TEXT NOT NULL,
                Suggestions TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProjectId, ChapterId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS VoiceAssignments (
                ProjectId TEXT NOT NULL,
                SpeakerId TEXT NOT NULL,
                VoiceId TEXT NOT NULL,
                PRIMARY KEY (ProjectId, SpeakerId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS VoiceProfiles (
                ProjectId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                SpeakerId TEXT NOT NULL,
                VoiceId TEXT NOT NULL,
                Language TEXT NOT NULL,
                Instruction TEXT NOT NULL,
                VoiceDesignReferenceAudio BLOB NULL,
                VoiceDesignReferenceText TEXT NULL,
                PRIMARY KEY (ProjectId, ProviderId, SpeakerId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS GenerationJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                ChapterId TEXT NOT NULL,
                SourceHash TEXT NOT NULL,
                NarrationMode TEXT NOT NULL,
                Status TEXT NOT NULL,
                TotalSegments INTEGER NOT NULL,
                Error TEXT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE (ProjectId, ChapterId, SourceHash, NarrationMode),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS GeneratedAudioSegments (
                JobId TEXT NOT NULL,
                Idx INTEGER NOT NULL,
                VoiceId TEXT NOT NULL,
                WavBytes BLOB NOT NULL,
                DurationTicks INTEGER NOT NULL,
                ProfileHash TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (JobId, Idx),
                FOREIGN KEY (JobId) REFERENCES GenerationJobs(Id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureNarrationModeColumnAsync(connection, cancellationToken);
        await EnsureStructureAnalysisColumnAsync(connection, cancellationToken);
        await EnsureDefaultLanguageColumnAsync(connection, cancellationToken);
        await EnsureAnalysisModeColumnAsync(connection, cancellationToken);
        await EnsureChapterRoleColumnAsync(connection, cancellationToken);
        await EnsureAnalysisSectionRoleColumnAsync(connection, cancellationToken);
        await EnsureGeneratedSegmentProfileHashColumnAsync(connection, cancellationToken);
        await EnsureVoiceDesignProfileColumnsAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<RecentProject>> GetRecentAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Title, UpdatedAt
            FROM Projects
            ORDER BY UpdatedAt DESC
            LIMIT 20;
            """;

        var projects = new List<RecentProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new RecentProject(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return projects;
    }

    public async Task<AudiobookProject?> LoadAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var projectCommand = connection.CreateCommand();
        projectCommand.CommandText =
            """
            SELECT Title, SourcePath, CreatedAt, UpdatedAt, NarrationMode, StructureAnalysis, DefaultLanguage
            FROM Projects
            WHERE Id = $id;
            """;
        projectCommand.Parameters.AddWithValue("$id", id.ToString("D"));

        string title;
        string sourcePath;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        NarrationMode? narrationMode;
        BookStructureResult? structureAnalysis;
        string? defaultLanguage;
        await using (var reader = await projectCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            title = reader.GetString(0);
            sourcePath = reader.GetString(1);
            createdAt = DateTimeOffset.Parse(
                reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture);
            updatedAt = DateTimeOffset.Parse(
                reader.GetString(3),
                System.Globalization.CultureInfo.InvariantCulture);
            narrationMode = reader.IsDBNull(4) ? null : Enum.Parse<NarrationMode>(reader.GetString(4));
            structureAnalysis = reader.IsDBNull(5)
                ? null
                : JsonSerializer.Deserialize<BookStructureResult>(
                    reader.GetString(5),
                    ProjectJsonOptions)
                    ?? throw new InvalidDataException("The saved book-structure analysis is empty.");
            defaultLanguage = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        await using var chapterCommand = connection.CreateCommand();
        chapterCommand.CommandText =
            """
            SELECT Id, Title, Text, Role
            FROM Chapters
            WHERE ProjectId = $id
            ORDER BY Position;
            """;
        chapterCommand.Parameters.AddWithValue("$id", id.ToString("D"));

        var chapters = new List<ImportedChapter>();
        await using (var reader = await chapterCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                chapters.Add(new ImportedChapter(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.Parse<SectionRole>(reader.GetString(3))));
            }
        }

        return new AudiobookProject(
            id,
            title,
            sourcePath,
            createdAt,
            updatedAt,
            chapters,
            narrationMode,
            structureAnalysis,
            defaultLanguage);
    }

    public async Task SaveAsync(
        AudiobookProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Id == Guid.Empty)
        {
            throw new ArgumentException("A project must have a stable, non-empty ID.", nameof(project));
        }

        if (project.Chapters.Count == 0)
        {
            throw new ArgumentException("A project must contain at least one chapter.", nameof(project));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var projectCommand = connection.CreateCommand())
            {
                projectCommand.Transaction = (SqliteTransaction)transaction;
                projectCommand.CommandText =
                    """
                    INSERT INTO Projects (
                        Id, Title, SourcePath, CreatedAt, UpdatedAt, NarrationMode, StructureAnalysis, DefaultLanguage)
                    VALUES (
                        $id, $title, $sourcePath, $createdAt, $updatedAt, $narrationMode, $structureAnalysis, $defaultLanguage)
                    ON CONFLICT(Id) DO UPDATE SET
                        Title = excluded.Title,
                        SourcePath = excluded.SourcePath,
                        UpdatedAt = excluded.UpdatedAt,
                        NarrationMode = excluded.NarrationMode,
                        StructureAnalysis = excluded.StructureAnalysis,
                        DefaultLanguage = excluded.DefaultLanguage;
                    """;
                projectCommand.Parameters.AddWithValue("$id", project.Id.ToString("D"));
                projectCommand.Parameters.AddWithValue("$title", project.Title);
                projectCommand.Parameters.AddWithValue("$sourcePath", project.SourcePath);
                projectCommand.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O"));
                projectCommand.Parameters.AddWithValue("$updatedAt", project.UpdatedAt.ToString("O"));
                projectCommand.Parameters.AddWithValue(
                    "$narrationMode",
                    (object?)project.NarrationMode?.ToString() ?? DBNull.Value);
                projectCommand.Parameters.AddWithValue(
                    "$structureAnalysis",
                    (object?)(
                        project.StructureAnalysis is null
                            ? null
                            : JsonSerializer.Serialize(project.StructureAnalysis, ProjectJsonOptions))
                    ?? DBNull.Value);
                projectCommand.Parameters.AddWithValue(
                    "$defaultLanguage",
                    (object?)project.DefaultLanguage ?? DBNull.Value);
                await projectCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = (SqliteTransaction)transaction;
                deleteCommand.CommandText = "DELETE FROM Chapters WHERE ProjectId = $id;";
                deleteCommand.Parameters.AddWithValue("$id", project.Id.ToString("D"));
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var position = 0; position < project.Chapters.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = project.Chapters[position];
                await using var chapterCommand = connection.CreateCommand();
                chapterCommand.Transaction = (SqliteTransaction)transaction;
                chapterCommand.CommandText =
                    """
                    INSERT INTO Chapters (ProjectId, Id, Position, Title, Text, Role)
                    VALUES ($projectId, $id, $position, $title, $text, $role);
                    """;
                chapterCommand.Parameters.AddWithValue("$projectId", project.Id.ToString("D"));
                chapterCommand.Parameters.AddWithValue("$id", chapter.Id);
                chapterCommand.Parameters.AddWithValue("$position", position);
                chapterCommand.Parameters.AddWithValue("$title", chapter.Title);
                chapterCommand.Parameters.AddWithValue("$text", chapter.Text);
                chapterCommand.Parameters.AddWithValue("$role", chapter.Role.ToString());
                await chapterCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var cleanupReviewsCommand = connection.CreateCommand())
            {
                cleanupReviewsCommand.Transaction = (SqliteTransaction)transaction;
                cleanupReviewsCommand.CommandText =
                    """
                    DELETE FROM SpeechReviews
                    WHERE ProjectId = $id
                      AND ChapterId NOT IN (SELECT Id FROM Chapters WHERE ProjectId = $id);
                    """;
                cleanupReviewsCommand.Parameters.AddWithValue("$id", project.Id.ToString("D"));
                await cleanupReviewsCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A project ID must not be empty.", nameof(id));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task EnsureNarrationModeColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Projects);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "NarrationMode", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Projects ADD COLUMN NarrationMode TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureStructureAnalysisColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "Projects", "StructureAnalysis", cancellationToken))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Projects ADD COLUMN StructureAnalysis TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDefaultLanguageColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "Projects", "DefaultLanguage", cancellationToken))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Projects ADD COLUMN DefaultLanguage TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAnalysisModeColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(ChapterAnalyses);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hasColumn |= string.Equals(reader.GetString(1), "NarrationMode", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!hasColumn)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE ChapterAnalyses ADD COLUMN NarrationMode TEXT NOT NULL DEFAULT 'MultiVoice';";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureChapterRoleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "Chapters", "Role", cancellationToken))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Chapters ADD COLUMN Role TEXT NOT NULL DEFAULT 'Body';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAnalysisSectionRoleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "ChapterAnalyses", "SectionRole", cancellationToken))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ChapterAnalyses ADD COLUMN SectionRole TEXT NOT NULL DEFAULT 'Body';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureGeneratedSegmentProfileHashColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "GeneratedAudioSegments", "ProfileHash", cancellationToken))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE GeneratedAudioSegments ADD COLUMN ProfileHash TEXT NOT NULL DEFAULT '';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureVoiceDesignProfileColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(connection, "VoiceProfiles", "VoiceDesignReferenceAudio", cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE VoiceProfiles ADD COLUMN VoiceDesignReferenceAudio BLOB NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await HasColumnAsync(connection, "VoiceProfiles", "VoiceDesignReferenceText", cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE VoiceProfiles ADD COLUMN VoiceDesignReferenceText TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
