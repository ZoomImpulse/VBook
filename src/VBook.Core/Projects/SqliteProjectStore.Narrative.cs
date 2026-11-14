using System.Text.Json;
using Microsoft.Data.Sqlite;
using VBook.Core.Books;
using VBook.Core.Narrative;

namespace VBook.Core.Projects;

public sealed partial class SqliteProjectStore
{
    public async Task SaveSpeechReviewAsync(
        Guid projectId,
        SpeechEditReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.ChapterId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SpeechReviews (ProjectId, ChapterId, SourceHash, Suggestions, UpdatedAt)
            VALUES ($project, $chapter, $sourceHash, $suggestions, $updatedAt)
            ON CONFLICT(ProjectId, ChapterId) DO UPDATE SET
                SourceHash = excluded.SourceHash,
                Suggestions = excluded.Suggestions,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$chapter", review.ChapterId);
        command.Parameters.AddWithValue("$sourceHash", review.SourceHash);
        command.Parameters.AddWithValue("$suggestions", JsonSerializer.Serialize(review.Suggestions));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SpeechEditReview?> LoadSpeechReviewAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourceHash, Suggestions FROM SpeechReviews WHERE ProjectId = $project AND ChapterId = $chapter;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$chapter", chapterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var suggestions = JsonSerializer.Deserialize<IReadOnlyList<SpeechEditSuggestion>>(reader.GetString(1))
            ?? throw new InvalidDataException("The saved spoken-content review is invalid.");
        return new SpeechEditReview(chapterId, reader.GetString(0), suggestions);
    }

    public async Task<IReadOnlyList<Character>> LoadCharactersAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadCharactersAsync(connection, null, projectId, cancellationToken);
    }

    public async Task SaveAnalysisAsync(
        Guid projectId,
        ChapterAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var project = projectId.ToString("D");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = (await ReadCharactersAsync(connection, transaction, projectId, cancellationToken))
                .ToDictionary(character => character.Id, StringComparer.Ordinal);
            foreach (var character in analysis.Characters)
            {
                var aliases = existing.TryGetValue(character.Id, out var stored)
                    ? stored.Aliases.Union(character.Aliases, StringComparer.OrdinalIgnoreCase).ToArray()
                    : character.Aliases.ToArray();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO Characters (ProjectId, Id, Name, Aliases)
                    VALUES ($project, $id, $name, $aliases)
                    ON CONFLICT(ProjectId, Id) DO UPDATE SET Aliases = excluded.Aliases;
                    """;
                command.Parameters.AddWithValue("$project", project);
                command.Parameters.AddWithValue("$id", character.Id);
                command.Parameters.AddWithValue("$name", character.Name);
                command.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM ChapterAnalyses WHERE ProjectId = $project AND ChapterId = $chapter;
                    INSERT INTO ChapterAnalyses (ProjectId, ChapterId, SourceHash, NarrationMode, SectionRole, AnalyzedAt)
                    VALUES ($project, $chapter, $hash, $mode, $sectionRole, $analyzedAt);
                    """;
                command.Parameters.AddWithValue("$project", project);
                command.Parameters.AddWithValue("$chapter", analysis.ChapterId);
                command.Parameters.AddWithValue("$hash", analysis.SourceHash);
                command.Parameters.AddWithValue("$mode", analysis.Mode.ToString());
                command.Parameters.AddWithValue("$sectionRole", analysis.SectionRole.ToString());
                command.Parameters.AddWithValue("$analyzedAt", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var segment in analysis.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO Segments (ProjectId, ChapterId, Idx, Kind, SpeakerId, Start, Length)
                    VALUES ($project, $chapter, $idx, $kind, $speaker, $start, $length);
                    """;
                command.Parameters.AddWithValue("$project", project);
                command.Parameters.AddWithValue("$chapter", analysis.ChapterId);
                command.Parameters.AddWithValue("$idx", segment.Index);
                command.Parameters.AddWithValue("$kind", segment.Kind.ToString());
                command.Parameters.AddWithValue("$speaker", (object?)segment.SpeakerId ?? DBNull.Value);
                command.Parameters.AddWithValue("$start", segment.Start);
                command.Parameters.AddWithValue("$length", segment.Length);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ChapterAnalysis?> LoadAnalysisAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken = default)
    {
        var project = projectId.ToString("D");
        await using var connection = await OpenConnectionAsync(cancellationToken);

        string hash;
        NarrationMode mode;
        SectionRole sectionRole;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT SourceHash, NarrationMode, SectionRole FROM ChapterAnalyses WHERE ProjectId = $project AND ChapterId = $chapter;";
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$chapter", chapterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            hash = reader.GetString(0);
            mode = Enum.Parse<NarrationMode>(reader.GetString(1));
            sectionRole = Enum.Parse<SectionRole>(reader.GetString(2));
        }

        var segments = new List<NarrativeSegment>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT Idx, Kind, SpeakerId, Start, Length
                FROM Segments
                WHERE ProjectId = $project AND ChapterId = $chapter
                ORDER BY Idx;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$chapter", chapterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                segments.Add(new NarrativeSegment(
                    reader.GetInt32(0),
                    Enum.Parse<SegmentKind>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)));
            }
        }

        var speakerIds = segments.Select(segment => segment.SpeakerId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var characters = (await ReadCharactersAsync(connection, null, projectId, cancellationToken))
            .Where(character => speakerIds.Contains(character.Id))
            .ToArray();
        return new ChapterAnalysis(chapterId, hash, characters, segments, mode, sectionRole);
    }

    private static async Task<IReadOnlyList<Character>> ReadCharactersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, Name, Aliases FROM Characters WHERE ProjectId = $project ORDER BY Name;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));

        var characters = new List<Character>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var aliases = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
            characters.Add(new Character(reader.GetString(0), reader.GetString(1), aliases));
        }

        return characters;
    }
}
