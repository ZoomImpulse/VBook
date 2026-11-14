using VBook.Core.Narrative;
using VBook.Core.Books;

namespace VBook.Core.Providers;

/// <summary>
/// What a provider can honestly do. The UI shows this instead of assuming features.
/// </summary>
public sealed record ProviderCapabilities(string Name, bool IsMock, int MaxContextCharacters);

public sealed record AnalysisRequest(
    string ChapterId,
    string Text,
    IReadOnlyList<Character> KnownCharacters,
    NarrationMode Mode = NarrationMode.MultiVoice,
    SectionRole SectionRole = SectionRole.Body);

public sealed record AnalysisProgress(double Fraction, string Message);

public interface ILlmProvider
{
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Segments <see cref="AnalysisRequest.Text"/> (never longer than
    /// <see cref="ProviderCapabilities.MaxContextCharacters"/>) into narration and
    /// attributed dialogue. Offsets are relative to the supplied text, and segments
    /// must cover it completely and in order.
    /// </summary>
    Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken);
}

public sealed record BookStructureRequest(
    string BookTitle,
    IReadOnlyList<BookStructureChapter> Chapters);

public sealed record BookStructureChapter(
    string Id,
    string Title,
    string Excerpt,
    SectionRole CurrentRole);

public sealed record BookStructureProgress(double Fraction, string Message);

public sealed record ChapterStructureSuggestion(
    string ChapterId,
    SectionRole Role,
    double Confidence,
    string Rationale);

public sealed record BookStructureResult(
    string Genre,
    IReadOnlyList<ChapterStructureSuggestion> Chapters);

public interface IBookStructureProvider
{
    ProviderCapabilities Capabilities { get; }

    Task<BookStructureResult> AnalyzeAsync(
        BookStructureRequest request,
        CancellationToken cancellationToken);
}

public sealed record SpeechRequest(
    string Text,
    string VoiceId,
    string Language = "Auto",
    string Instruction = "");

public sealed record SpeechVoiceProfile(string VoiceId, string Language, string Instruction);

public sealed record SpeechResult(byte[] WavBytes, TimeSpan Duration);

public interface ITtsProvider
{
    ProviderCapabilities Capabilities { get; }
    IReadOnlyList<string> VoiceIds { get; }
    IReadOnlyList<string> Languages => ["Auto"];
    bool SupportsVoiceInstructions => false;

    Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}
