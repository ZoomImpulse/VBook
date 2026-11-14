using Microsoft.Data.Sqlite;
using VBook.Core.Generation;
using VBook.Core.Narrative;

namespace VBook.Core.Projects;

public interface IGenerationQueueStore
{
    Task<IReadOnlyList<GenerationJob>> LoadGenerationJobsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

public sealed partial class SqliteProjectStore : IGenerationQueueStore
{
    public async Task<IReadOnlyList<GenerationJob>> LoadGenerationJobsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ChapterId, SourceHash, NarrationMode, Status, TotalSegments, Error, UpdatedAt
            FROM GenerationJobs
            WHERE ProjectId = $project
            ORDER BY UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        var jobs = new List<GenerationJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new GenerationJob(
                Guid.Parse(reader.GetString(0)),
                projectId,
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<NarrationMode>(reader.GetString(3)),
                Enum.Parse<GenerationJobStatus>(reader.GetString(4)),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return jobs;
    }
}
