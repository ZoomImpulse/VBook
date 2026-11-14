using VBook.Core.Narrative;

namespace VBook.Core.Projects;

public interface INarrativeStore
{
    /// <summary>Project-wide characters, the start of cross-chapter narrative memory.</summary>
    Task<IReadOnlyList<Character>> LoadCharactersAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task SaveAnalysisAsync(Guid projectId, ChapterAnalysis analysis, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored analysis, which may be stale; callers compare
    /// <see cref="ChapterAnalysis.SourceHash"/> with the current chapter text.
    /// </summary>
    Task<ChapterAnalysis?> LoadAnalysisAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken = default);

    Task SaveSpeechReviewAsync(
        Guid projectId,
        SpeechEditReview review,
        CancellationToken cancellationToken = default);

    Task<SpeechEditReview?> LoadSpeechReviewAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken = default);
}
