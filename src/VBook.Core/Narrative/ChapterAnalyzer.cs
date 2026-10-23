using VBook.Core.Providers;

namespace VBook.Core.Narrative;

/// <summary>
/// Enforces the provider context budget by chunking the chapter, carries known
/// characters forward between chunks, and validates the merged result before it
/// can be stored or voiced.
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
        var usedSpeakerIds = new HashSet<string>(StringComparer.Ordinal);
        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[chunkIndex];
            progress?.Report(new AnalysisProgress(
                (double)chunkIndex / chunks.Count,
                $"Analyzing part {chunkIndex + 1} of {chunks.Count}…"));

            var result = await _provider.AnalyzeAsync(
                new AnalysisRequest(request.ChapterId, chunk.Text, [.. cast.Values]),
                cancellationToken);

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
        }

        AnalysisValidator.EnsureValid(request.Text, cast.Keys.ToHashSet(StringComparer.Ordinal), segments);
        progress?.Report(new AnalysisProgress(1, "Analysis complete."));
        return new ChapterAnalysis(
            request.ChapterId,
            TextFingerprint.Compute(request.Text),
            cast.Values.Where(character => usedSpeakerIds.Contains(character.Id)).ToArray(),
            segments);
    }

    private static Character MergeCharacter(Character existing, Character incoming)
    {
        var aliases = existing.Aliases.Union(incoming.Aliases, StringComparer.OrdinalIgnoreCase).ToArray();
        return existing with { Aliases = aliases };
    }
}
