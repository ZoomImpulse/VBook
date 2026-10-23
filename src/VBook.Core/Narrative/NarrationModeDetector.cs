namespace VBook.Core.Narrative;

/// <summary>
/// Suggests a narration mode from how much of the text is quoted speech. It is only a
/// default; the user's choice always wins.
/// </summary>
public static class NarrationModeDetector
{
    private const double DialogueShareThreshold = 0.12;
    private const int MinimumQuotedSpans = 8;

    public static NarrationMode Suggest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return NarrationMode.SingleNarrator;
        }

        var quotedChars = 0;
        var spans = 0;
        var open = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (open < 0 && c is '“' or '"')
            {
                open = i;
            }
            else if (open >= 0 && c is '”' or '"')
            {
                // Long spans are block quotes or stray marks, not conversation.
                var length = i - open + 1;
                if (length <= 400)
                {
                    quotedChars += length;
                    spans++;
                }

                open = -1;
            }
        }

        return spans >= MinimumQuotedSpans && (double)quotedChars / text.Length >= DialogueShareThreshold
            ? NarrationMode.MultiVoice
            : NarrationMode.SingleNarrator;
    }
}
