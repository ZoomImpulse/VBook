using System.Text.Json;
using System.Text.Json.Serialization;
using VBook.Core.Narrative;

namespace VBook.Core.Worker;

/// <summary>
/// Typed local protocol between the app and a narrative worker (in-process today,
/// a separate Python process later). Messages are JSON with a "type" discriminator;
/// out-of-process transports frame them as one JSON document per line.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnalyzeChapterRequest), "analyzeChapter")]
[JsonDerivedType(typeof(CancelRequest), "cancel")]
public abstract record WorkerRequest(Guid RequestId);

public sealed record AnalyzeChapterRequest(
    Guid RequestId,
    string ChapterId,
    string Text,
    IReadOnlyList<Character> KnownCharacters,
    NarrationMode Mode = NarrationMode.MultiVoice) : WorkerRequest(RequestId);

/// <summary>Asks an out-of-process worker to stop the request with the given ID.</summary>
public sealed record CancelRequest(Guid RequestId) : WorkerRequest(RequestId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProgressEvent), "progress")]
[JsonDerivedType(typeof(AnalysisCompletedEvent), "analysisCompleted")]
[JsonDerivedType(typeof(FailedEvent), "failed")]
[JsonDerivedType(typeof(CancelledEvent), "cancelled")]
public abstract record WorkerEvent(Guid RequestId);

public sealed record ProgressEvent(Guid RequestId, double Fraction, string Message) : WorkerEvent(RequestId);

public sealed record AnalysisCompletedEvent(Guid RequestId, ChapterAnalysis Analysis) : WorkerEvent(RequestId);

public sealed record FailedEvent(Guid RequestId, string Code, string Message) : WorkerEvent(RequestId);

public sealed record CancelledEvent(Guid RequestId) : WorkerEvent(RequestId);

public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(WorkerRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static string Serialize(WorkerEvent workerEvent) =>
        JsonSerializer.Serialize(workerEvent, Options);

    public static WorkerRequest DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<WorkerRequest>(json, Options)
        ?? throw new InvalidDataException("Empty worker request.");

    public static WorkerEvent DeserializeEvent(string json) =>
        JsonSerializer.Deserialize<WorkerEvent>(json, Options)
        ?? throw new InvalidDataException("Empty worker event.");
}
