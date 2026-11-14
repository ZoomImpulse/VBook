using VBook.Core.Providers;

namespace VBook.Core.Narrative;

/// <summary>
/// Runs one combined narrative and speech-edit analysis per bounded chapter chunk,
/// then validates and merges the results against the original source text.
/// </summary>
public sealed class ChapterAnalyzer
{
    private readonly ILlmProvider _provider;

    public ChapterAnalyzer(ILlmProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<ChapterAnalysis> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var budget = _provider.Capabilities.MaxContextCharacters;
        if (budget < 2)
        {
            throw new InvalidOperationException("The provider reports an unusable context budget.");
        }

        var chunks = TextChunker.Split(request.Text, budget);
        var cast = new Dictionary<string, Character>(StringComparer.Ordinal);
        foreach (var known in request.KnownCharacters)
        {
            cast[known.Id] = known;
        }

        var segments = new List<NarrativeSegment>();
        var suggestions = new List<SpeechEditSuggestion>();
        var warnings = new List<string>();
        var usedSpeakerIds = new HashSet<string>(StringComparer.Ordinal);
        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[chunkIndex];
            progress?.Report(new AnalysisProgress(
                (double)chunkIndex / chunks.Count,
                $"Analyzing narrative and spoken wording, part {chunkIndex + 1} of {chunks.Count}…"));

            var result = await _provider.AnalyzeAsync(
                new AnalysisRequest(request.ChapterId, chunk.Text, [.. cast.Values], request.Mode, request.SectionRole),
                cancellationToken);
            warnings.AddRange(result.Warnings ?? []);

            foreach (var character in result.Characters)
            {
                cast[character.Id] = cast.TryGetValue(character.Id, out var existing)
                    ? MergeCharacter(existing, character)
                    : character;
            }

            AnalysisValidator.EnsureValid(chunk.Text, cast.Keys.ToHashSet(StringComparer.Ordinal), result.Segments);
            foreach (var segment in result.Segments)
            {
                segments.Add(segment with { Index = segments.Count, Start = segment.Start + chunk.Start });
                if (segment.SpeakerId is not null)
                {
                    usedSpeakerIds.Add(segment.SpeakerId);
                }
            }

            var editCursor = 0;
            foreach (var edit in result.SpeechEdits ?? [])
            {
                if (string.IsNullOrEmpty(edit.OriginalText) ||
                    edit.ReplacementText is null ||
                    string.IsNullOrWhiteSpace(edit.Rationale))
                {
                    throw new InvalidDataException("The model returned an incomplete spoken-wording suggestion.");
                }

                var localStart = chunk.Text.IndexOf(edit.OriginalText, editCursor, StringComparison.Ordinal);
                if (localStart < 0)
                {
                    throw new InvalidDataException(
                        "A spoken-wording suggestion did not match the source text in order.");
                }

                suggestions.Add(new SpeechEditSuggestion(
                    Guid.NewGuid(),
                    chunk.Start + localStart,
                    edit.OriginalText.Length,
                    edit.OriginalText,
                    edit.ReplacementText,
                    edit.Rationale));
                editCursor = localStart + edit.OriginalText.Length;
            }
        }

        AnalysisValidator.EnsureValid(request.Text, cast.Keys.ToHashSet(StringComparer.Ordinal), segments);
        if (request.Mode == NarrationMode.SingleNarrator)
        {
            segments = [.. SingleNarratorSegmenter.Segment(request.Text)];
        }

        suggestions = [.. ValidateSpeechSuggestions(request.Text, segments, suggestions, warnings)];
        progress?.Report(new AnalysisProgress(1, "Narrative and spoken-wording analysis complete."));
        return new ChapterAnalysis(
            request.ChapterId,
            TextFingerprint.Compute(request.Text),
            request.Mode == NarrationMode.SingleNarrator
                ? []
                : cast.Values.Where(character => usedSpeakerIds.Contains(character.Id)).ToArray(),
            segments,
            request.Mode,
            request.SectionRole,
            suggestions,
            warnings);
    }

    private static IReadOnlyList<SpeechEditSuggestion> ValidateSpeechSuggestions(
        string source,
        IReadOnlyList<NarrativeSegment> segments,
        IReadOnlyList<SpeechEditSuggestion> suggestions,
        ICollection<string> warnings)
    {
        var ordered = suggestions.OrderBy(suggestion => suggestion.Start).ToArray();
        var valid = new List<SpeechEditSuggestion>();
        var cursor = 0;
        foreach (var suggestion in ordered)
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
                warnings.Add("A spoken-wording suggestion overlapping or not matching the source was ignored.");
                continue;
            }

            var containingSegments = segments.Where(segment =>
                suggestion.Start >= segment.Start &&
                suggestion.Start + suggestion.Length <= segment.Start + segment.Length).ToArray();
            if (containingSegments.Length != 1 || containingSegments[0].Kind != SegmentKind.Narration)
            {
                warnings.Add("A spoken-wording suggestion outside a single narration segment was ignored.");
                continue;
            }

            cursor = suggestion.Start + suggestion.Length;
            valid.Add(suggestion);
        }

        return valid;
    }

    private static Character MergeCharacter(Character existing, Character incoming)
    {
        var aliases = existing.Aliases.Union(incoming.Aliases, StringComparer.OrdinalIgnoreCase).ToArray();
        return existing with { Aliases = aliases };
    }
}
