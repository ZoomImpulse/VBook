using VBook.Core.Narrative;

namespace VBook.Core.Generation;

public enum GenerationJobStatus
{
    Pending,
    Running,
    Paused,
    Failed,
    Completed
}

public sealed record GenerationJob(
    Guid Id,
    Guid ProjectId,
    string ChapterId,
    string SourceHash,
    NarrationMode NarrationMode,
    GenerationJobStatus Status,
    int TotalSegments,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record GeneratedAudioSegment(
    int Index,
    string VoiceId,
    byte[] WavBytes,
    TimeSpan Duration,
    string ProfileHash = "");

public sealed record GenerationProgress(double CompletedSegments, int TotalSegments, string Message)
{
    public double Fraction => TotalSegments == 0 ? 1 : (double)CompletedSegments / TotalSegments;
}
