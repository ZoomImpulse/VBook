namespace VBook.Core.Narrative;

/// <summary>
/// Deterministic, model-free segmentation for single-narrator books: keeps paragraph
/// and explicit line boundaries (such as verse), and splits oversized lines at
/// sentence ends so each segment stays a sensible size for speech synthesis.
/// </summary>
public static class SingleNarratorSegmenter
{
    public const int DefaultMaxSegmentLength = 1200;

    public static IReadOnlyList<NarrativeSegment> Segment(string text, int maxSegmentLength = DefaultMaxSegmentLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSegmentLength, 2);

        var segments = new List<NarrativeSegment>();
        var start = 0;
        while (start < text.Length)
        {
            var end = LineEnd(text, start);
            while (start < end)
            {
                var cut = end - start <= maxSegmentLength ? end : SentenceCut(text, start, start + maxSegmentLength);
                segments.Add(new NarrativeSegment(segments.Count, SegmentKind.Narration, null, start, cut - start));
                start = cut;
            }
        }

        return segments;
    }

    // Keep line endings attached to the preceding segment; this preserves verse and source offsets.
    private static int LineEnd(string text, int start)
    {
        var index = text.IndexOf('\n', start);
        if (index < 0)
        {
            return text.Length;
        }

        index++;
        while (index < text.Length && text[index] == '\n')
        {
            index++;
        }

        return index;
    }

    private static int SentenceCut(string text, int start, int limit)
    {
        var floor = start + (limit - start) / 2;
        for (var i = limit - 1; i >= floor; i--)
        {
            if (text[i] is '.' or '!' or '?' or '…' or '\n' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 1 < limit && text[i] != '\n' ? i + 2 : i + 1;
            }
        }

        var space = text.LastIndexOf(' ', limit - 1, limit - floor);
        var cut = space >= floor ? space + 1 : limit;
        return cut > start && cut < text.Length && char.IsLowSurrogate(text[cut]) ? cut - 1 : cut;
    }
}
