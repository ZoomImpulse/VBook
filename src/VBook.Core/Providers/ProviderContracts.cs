using VBook.Core.Narrative;

namespace VBook.Core.Providers;

/// <summary>
/// What a provider can honestly do. The UI shows this instead of assuming features.
/// </summary>
public sealed record ProviderCapabilities(string Name, bool IsMock, int MaxContextCharacters);

public sealed record AnalysisRequest(
    string ChapterId,
    string Text,
    IReadOnlyList<Character> KnownCharacters,
    NarrationMode Mode = NarrationMode.MultiVoice);

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

public sealed record SpeechRequest(string Text, string VoiceId);

public sealed record SpeechResult(byte[] WavBytes, TimeSpan Duration);

public interface ITtsProvider
{
    ProviderCapabilities Capabilities { get; }

    Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}
