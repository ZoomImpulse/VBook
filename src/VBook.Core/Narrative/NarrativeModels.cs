using System.Security.Cryptography;
using System.Text;

namespace VBook.Core.Narrative;

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
    IReadOnlyList<NarrativeSegment> Segments);

public sealed record ChapterAnalysis(
    string ChapterId,
    string SourceHash,
    IReadOnlyList<Character> Characters,
    IReadOnlyList<NarrativeSegment> Segments)
{
    public bool Matches(string chapterText) =>
        string.Equals(SourceHash, TextFingerprint.Compute(chapterText), StringComparison.Ordinal);
}

public static class TextFingerprint
{
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
