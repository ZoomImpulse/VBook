namespace VBook.Core.Narrative;

public sealed record TextChunk(int Start, string Text);

public static class TextChunker
{
    /// <summary>
    /// Splits text into ordered, contiguous chunks no longer than <paramref name="maxLength"/>,
    /// preferring line breaks, then spaces, so chunk offsets map exactly onto the source.
    /// </summary>
    public static IReadOnlyList<TextChunk> Split(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= maxLength)
            {
                chunks.Add(new TextChunk(start, text[start..]));
                break;
            }

            var end = start + maxLength;
            var window = maxLength - maxLength / 2;
            var cut = text.LastIndexOf('\n', end - 1, window);
            if (cut < 0)
            {
                cut = text.LastIndexOf(' ', end - 1, window);
            }

            cut = cut < 0 ? end : cut + 1;
            if (cut < text.Length && char.IsLowSurrogate(text[cut]))
            {
                cut--;
            }

            chunks.Add(new TextChunk(start, text[start..cut]));
            start = cut;
        }

        return chunks;
    }
}
