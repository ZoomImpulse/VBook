using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.Core.Projects;

public interface IGenerationStore
{
    Task<IReadOnlyDictionary<string, SpeechVoiceProfile>> LoadVoiceProfilesAsync(
        Guid projectId,
        string providerId,
        CancellationToken cancellationToken = default);

    Task SaveVoiceProfileAsync(
        Guid projectId,
        string providerId,
        string speakerId,
        SpeechVoiceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> LoadVoiceAssignmentsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task SaveVoiceAssignmentAsync(
        Guid projectId,
        string speakerId,
        string voiceId,
        CancellationToken cancellationToken = default);

    Task<GenerationJob> GetOrCreateGenerationJobAsync(
        Guid projectId,
        string chapterId,
        string sourceHash,
        NarrationMode mode,
        int totalSegments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeneratedAudioSegment>> LoadGeneratedSegmentsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task SaveGeneratedSegmentAsync(
        Guid jobId,
        GeneratedAudioSegment segment,
        CancellationToken cancellationToken = default);

    Task UpdateGenerationJobAsync(
        Guid jobId,
        GenerationJobStatus status,
        string? error = null,
        CancellationToken cancellationToken = default);
}
