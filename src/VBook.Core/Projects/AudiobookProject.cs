using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.Core.Projects;

public sealed record AudiobookProject(
    Guid Id,
    string Title,
    string SourcePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ImportedChapter> Chapters,
    NarrationMode? NarrationMode = null,
    BookStructureResult? StructureAnalysis = null,
    string? DefaultLanguage = null);

public sealed record RecentProject(Guid Id, string Title, DateTimeOffset UpdatedAt);

public interface IProjectStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecentProject>> GetRecentAsync(
        CancellationToken cancellationToken = default);

    Task<AudiobookProject?> LoadAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(AudiobookProject project, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
