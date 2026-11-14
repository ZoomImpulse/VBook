using System.Security.Cryptography;
using System.Text;
using VBook.Core.Books;

namespace VBook.Core.Narrative;

/// <summary>
/// How a book is voiced. Fiction with dialogue benefits from a cast; most other
/// genres (non-fiction, textbooks, memoir, essays, reference) are read by one narrator.
/// </summary>
public enum NarrationMode
{
    SingleNarrator,
    MultiVoice,
}

public enum SegmentKind
{
    Narration,
    Dialogue,
}

public sealed record Character(string Id, string Name, IReadOnlyList<string> Aliases)
{
    public const string UnknownId = "unknown";

    public static Character Unknown { get; } = new(UnknownId, "Unknown speaker", []);
}

/// <summary>
/// A contiguous slice of chapter source text. Only offsets are stored so the
/// source text remains the single authoritative copy.
/// </summary>
public sealed record NarrativeSegment(
    int Index,
    SegmentKind Kind,
    string? SpeakerId,
    int Start,
    int Length)
{
    public string TextFrom(string source) => source.Substring(Start, Length);
}

public sealed record SegmentationResult(
    IReadOnlyList<Character> Characters,
    IReadOnlyList<NarrativeSegment> Segments,
    IReadOnlyList<SpeechEditProposal>? SpeechEdits = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record ChapterAnalysis(
    string ChapterId,
    string SourceHash,
    IReadOnlyList<Character> Characters,
    IReadOnlyList<NarrativeSegment> Segments,
    NarrationMode Mode = NarrationMode.MultiVoice,
    SectionRole SectionRole = SectionRole.Body,
    IReadOnlyList<SpeechEditSuggestion>? SpeechSuggestions = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool Matches(string chapterText) =>
        string.Equals(SourceHash, TextFingerprint.Compute(chapterText), StringComparison.Ordinal);

    public bool Matches(string chapterText, NarrationMode mode) =>
        Mode == mode && Matches(chapterText);

    public bool Matches(string chapterText, NarrationMode mode, SectionRole sectionRole) =>
        SectionRole == sectionRole && Matches(chapterText, mode);
}

public enum SpeechEditStatus
{
    Pending,
    Accepted,
    Rejected
}

public sealed record SpeechEditProposal(
    string OriginalText,
    string ReplacementText,
    string Rationale);

public sealed record SpeechEditSuggestion(
    Guid Id,
    int Start,
    int Length,
    string OriginalText,
    string ReplacementText,
    string Rationale,
    SpeechEditStatus Status = SpeechEditStatus.Pending);

public sealed record SpeechEditReview(
    string ChapterId,
    string SourceHash,
    IReadOnlyList<SpeechEditSuggestion> Suggestions);

public static class SpeechScriptBuilder
{
    public static string Apply(string source, SpeechEditReview? review)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (review is null ||
            !string.Equals(review.SourceHash, TextFingerprint.Compute(source), StringComparison.Ordinal))
        {
            return source;
        }

        var accepted = review.Suggestions
            .Where(suggestion => suggestion.Status == SpeechEditStatus.Accepted)
            .OrderBy(suggestion => suggestion.Start)
            .ToArray();
        var result = new StringBuilder(source.Length);
        var cursor = 0;
        foreach (var suggestion in accepted)
        {
            if (suggestion.Start < cursor ||
                suggestion.Start < 0 ||
                suggestion.Length <= 0 ||
                suggestion.Start > source.Length - suggestion.Length ||
                !string.Equals(
                    source.Substring(suggestion.Start, suggestion.Length),
                    suggestion.OriginalText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Accepted speech edits overlap or no longer match the source text.");
            }

            result.Append(source, cursor, suggestion.Start - cursor);
            result.Append(suggestion.ReplacementText);
            cursor = suggestion.Start + suggestion.Length;
        }

        result.Append(source, cursor, source.Length - cursor);
        return result.ToString();
    }

    public static string ApplyToSegment(
        string source,
        NarrativeSegment segment,
        SpeechEditReview? review)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(segment);
        if (review is null ||
            !string.Equals(review.SourceHash, TextFingerprint.Compute(source), StringComparison.Ordinal))
        {
            return segment.TextFrom(source);
        }

        var segmentEnd = segment.Start + segment.Length;
        var applicable = review.Suggestions
            .Where(suggestion => suggestion.Status == SpeechEditStatus.Accepted)
            .Where(suggestion => suggestion.Start < segmentEnd &&
                                 suggestion.Start + suggestion.Length > segment.Start)
            .OrderBy(suggestion => suggestion.Start)
            .ToArray();
        if (applicable.Any(suggestion =>
                suggestion.Start < segment.Start || suggestion.Start + suggestion.Length > segmentEnd))
        {
            throw new InvalidDataException(
                "An accepted speech edit crosses a narration segment boundary; review the chapter analysis again.");
        }

        var result = new StringBuilder(segment.Length);
        var cursor = segment.Start;
        foreach (var suggestion in applicable)
        {
            if (suggestion.Start < cursor ||
                suggestion.Length <= 0 ||
                suggestion.Start > source.Length - suggestion.Length ||
                !string.Equals(
                    source.Substring(suggestion.Start, suggestion.Length),
                    suggestion.OriginalText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Accepted speech edits overlap or no longer match the source text.");
            }

            result.Append(source, cursor, suggestion.Start - cursor);
            result.Append(suggestion.ReplacementText);
            cursor = suggestion.Start + suggestion.Length;
        }

        result.Append(source, cursor, segmentEnd - cursor);
        return result.ToString();
    }
}

public static class TextFingerprint
{
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
