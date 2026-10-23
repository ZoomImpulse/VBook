using System.Text.RegularExpressions;
using VBook.Core.Narrative;

namespace VBook.Core.Providers;

/// <summary>
/// Deterministic, model-free stand-in for an LLM. It splits on quotation marks and
/// guesses speakers from "said Mary" style attributions, so the full pipeline and
/// UI can be exercised without a local model.
/// </summary>
public sealed partial class MockLlmProvider : ILlmProvider
{
    private const string Verbs =
        "said|asked|replied|whispered|shouted|cried|answered|murmured|called|exclaimed|muttered|added|continued";

    private const string Name = @"[\p{Lu}][\p{L}'’-]*";

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "he", "she", "they", "i", "we", "it", "you", "the", "a", "and", "but", "then", "who", "one",
    };

    public ProviderCapabilities Capabilities { get; } =
        new("Mock heuristic analyzer (no model)", IsMock: true, MaxContextCharacters: 8000);

    public Task<SegmentationResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds this provider's context budget.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var text = request.Text;
        var cast = new Dictionary<string, Character>(StringComparer.Ordinal);
        var segments = new List<NarrativeSegment>();
        var cursor = 0;
        var index = 0;
        while (index < text.Length)
        {
            var closer = text[index] switch { '“' => '”', '"' => '"', _ => '\0' };
            if (closer == '\0')
            {
                index++;
                continue;
            }

            var close = text.IndexOf(closer, index + 1);
            if (close < 0)
            {
                break;
            }

            if (close == index + 1)
            {
                index = close + 1;
                continue;
            }

            if (index > cursor)
            {
                segments.Add(new NarrativeSegment(segments.Count, SegmentKind.Narration, null, cursor, index - cursor));
            }

            var before = text.Substring(Math.Max(cursor, index - 60), index - Math.Max(cursor, index - 60));
            var after = AttributionWindow(text, close + 1);
            var speaker = ResolveSpeaker(before, after, request.KnownCharacters);
            cast.TryAdd(speaker.Id, speaker);
            segments.Add(new NarrativeSegment(
                segments.Count, SegmentKind.Dialogue, speaker.Id, index, close + 1 - index));
            cursor = close + 1;
            index = cursor;
        }

        if (cursor < text.Length)
        {
            segments.Add(new NarrativeSegment(segments.Count, SegmentKind.Narration, null, cursor, text.Length - cursor));
        }

        return Task.FromResult(new SegmentationResult([.. cast.Values], segments));
    }

    private static string AttributionWindow(string text, int start)
    {
        var end = Math.Min(text.Length, start + 80);
        for (var position = start; position < end; position++)
        {
            if (text[position] is '"' or '“')
            {
                end = position;
                break;
            }
        }

        return text[start..end];
    }

    private static Character ResolveSpeaker(string before, string after, IReadOnlyList<Character> known)
    {
        var name = Match(BeforePattern(), before) ?? Match(AfterPattern(), after);
        if (name is null)
        {
            return Character.Unknown;
        }

        var existing = known.FirstOrDefault(character =>
            string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)
            || character.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var id = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return new Character(id.Length == 0 ? Character.UnknownId : id, name, []);
    }

    private static string? Match(Regex pattern, string input)
    {
        var match = pattern.Match(input);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        return NotNames.Contains(name) ? null : name;
    }

    [GeneratedRegex($@"(?<name>{Name})\s+(?i:{Verbs})[,:]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BeforePattern();

    [GeneratedRegex(
        $@"^[\s,.]*(?:(?<name>{Name})\s+(?i:{Verbs})\b|(?i:{Verbs})\s+(?<name>{Name}))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AfterPattern();
}
