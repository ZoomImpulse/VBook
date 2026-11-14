using Microsoft.Data.Sqlite;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.Core.Projects;

public sealed partial class SqliteProjectStore
{
    public async Task<IReadOnlyDictionary<string, SpeechVoiceProfile>> LoadVoiceProfilesAsync(
        Guid projectId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SpeakerId, VoiceId, Language, Instruction, VoiceDesignReferenceAudio, VoiceDesignReferenceText
            FROM VoiceProfiles
            WHERE ProjectId = $project AND ProviderId = $provider;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$provider", providerId);
        var profiles = new Dictionary<string, SpeechVoiceProfile>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var referenceAudio = reader.IsDBNull(4) ? null : (byte[])reader[4];
            var referenceText = reader.IsDBNull(5) ? null : reader.GetString(5);
            if ((referenceAudio is null) != (referenceText is null) ||
                referenceAudio is { Length: < 44 } ||
                string.IsNullOrWhiteSpace(referenceText) && referenceAudio is not null)
            {
                throw new InvalidDataException(
                    $"The saved voice-design reference for '{reader.GetString(0)}' is incomplete.");
            }

            profiles.Add(reader.GetString(0), new SpeechVoiceProfile(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                referenceAudio,
                referenceText));
        }

        return profiles;
    }

    public async Task SaveVoiceProfileAsync(
        Guid projectId,
        string providerId,
        string speakerId,
        SpeechVoiceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.VoiceId);
        ArgumentNullException.ThrowIfNull(profile.Language);
        ArgumentNullException.ThrowIfNull(profile.Instruction);
        if ((profile.VoiceDesignReferenceAudio is null) != (profile.VoiceDesignReferenceText is null) ||
            profile.VoiceDesignReferenceAudio is { Length: < 44 } ||
            profile.VoiceDesignReferenceAudio is not null &&
            string.IsNullOrWhiteSpace(profile.VoiceDesignReferenceText))
        {
            throw new ArgumentException("Voice-design reference audio and text must be complete and paired.", nameof(profile));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO VoiceProfiles
                (ProjectId, ProviderId, SpeakerId, VoiceId, Language, Instruction,
                 VoiceDesignReferenceAudio, VoiceDesignReferenceText)
            VALUES
                ($project, $provider, $speaker, $voice, $language, $instruction,
                 $voiceDesignReferenceAudio, $voiceDesignReferenceText)
            ON CONFLICT(ProjectId, ProviderId, SpeakerId) DO UPDATE SET
                VoiceId = excluded.VoiceId,
                Language = excluded.Language,
                Instruction = excluded.Instruction,
                VoiceDesignReferenceAudio = excluded.VoiceDesignReferenceAudio,
                VoiceDesignReferenceText = excluded.VoiceDesignReferenceText;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$voice", profile.VoiceId);
        command.Parameters.AddWithValue("$language", profile.Language);
        command.Parameters.AddWithValue("$instruction", profile.Instruction);
        command.Parameters.AddWithValue(
            "$voiceDesignReferenceAudio",
            (object?)profile.VoiceDesignReferenceAudio ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$voiceDesignReferenceText",
            (object?)profile.VoiceDesignReferenceText ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadVoiceAssignmentsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SpeakerId, VoiceId FROM VoiceAssignments WHERE ProjectId = $project;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assignments.Add(reader.GetString(0), reader.GetString(1));
        }

        return assignments;
    }

    public async Task SaveVoiceAssignmentAsync(
        Guid projectId,
        string speakerId,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO VoiceAssignments (ProjectId, SpeakerId, VoiceId)
            VALUES ($project, $speaker, $voice)
            ON CONFLICT(ProjectId, SpeakerId) DO UPDATE SET VoiceId = excluded.VoiceId;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$voice", voiceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GenerationJob> GetOrCreateGenerationJobAsync(
        Guid projectId,
        string chapterId,
        string sourceHash,
        NarrationMode mode,
        int totalSegments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentOutOfRangeException.ThrowIfNegative(totalSegments);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO GenerationJobs
                    (Id, ProjectId, ChapterId, SourceHash, NarrationMode, Status, TotalSegments, UpdatedAt)
                VALUES
                    ($id, $project, $chapter, $hash, $mode, $status, $total, $updatedAt)
                ON CONFLICT(ProjectId, ChapterId, SourceHash, NarrationMode) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            command.Parameters.AddWithValue("$chapter", chapterId);
            command.Parameters.AddWithValue("$hash", sourceHash);
            command.Parameters.AddWithValue("$mode", mode.ToString());
            command.Parameters.AddWithValue("$status", GenerationJobStatus.Pending.ToString());
            command.Parameters.AddWithValue("$total", totalSegments);
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT Id, Status, TotalSegments, Error, UpdatedAt
            FROM GenerationJobs
            WHERE ProjectId = $project AND ChapterId = $chapter
                AND SourceHash = $hash AND NarrationMode = $mode;
            """;
        read.Parameters.AddWithValue("$project", projectId.ToString("D"));
        read.Parameters.AddWithValue("$chapter", chapterId);
        read.Parameters.AddWithValue("$hash", sourceHash);
        read.Parameters.AddWithValue("$mode", mode.ToString());
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The generation job could not be created or loaded.");
        }

        return new GenerationJob(
            Guid.Parse(reader.GetString(0)),
            projectId,
            chapterId,
            sourceHash,
            mode,
            Enum.Parse<GenerationJobStatus>(reader.GetString(1)),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<GeneratedAudioSegment>> LoadGeneratedSegmentsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Idx, VoiceId, WavBytes, DurationTicks, ProfileHash
            FROM GeneratedAudioSegments
            WHERE JobId = $job
            ORDER BY Idx;
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D"));
        var segments = new List<GeneratedAudioSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            segments.Add(new GeneratedAudioSegment(
                reader.GetInt32(0),
                reader.GetString(1),
                (byte[])reader[2],
                TimeSpan.FromTicks(reader.GetInt64(3)),
                reader.GetString(4)));
        }

        return segments;
    }

    public async Task SaveGeneratedSegmentAsync(
        Guid jobId,
        GeneratedAudioSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentOutOfRangeException.ThrowIfNegative(segment.Index);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.VoiceId);
        ArgumentNullException.ThrowIfNull(segment.WavBytes);
        if (segment.WavBytes.Length < 44 || segment.Duration < TimeSpan.Zero)
        {
            throw new ArgumentException("Generated audio must contain a WAV file and a non-negative duration.", nameof(segment));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GeneratedAudioSegments (JobId, Idx, VoiceId, WavBytes, DurationTicks, ProfileHash)
            VALUES ($job, $idx, $voice, $wav, $duration, $profileHash)
            ON CONFLICT(JobId, Idx) DO UPDATE SET
                VoiceId = excluded.VoiceId,
                WavBytes = excluded.WavBytes,
                DurationTicks = excluded.DurationTicks,
                ProfileHash = excluded.ProfileHash;
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D"));
        command.Parameters.AddWithValue("$idx", segment.Index);
        command.Parameters.AddWithValue("$voice", segment.VoiceId);
        command.Parameters.AddWithValue("$wav", segment.WavBytes);
        command.Parameters.AddWithValue("$duration", segment.Duration.Ticks);
        command.Parameters.AddWithValue("$profileHash", segment.ProfileHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateGenerationJobAsync(
        Guid jobId,
        GenerationJobStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE GenerationJobs
            SET Status = $status, Error = $error, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException($"Generation job '{jobId}' was not found.");
        }
    }
}
