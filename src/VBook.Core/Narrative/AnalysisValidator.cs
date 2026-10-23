namespace VBook.Core.Narrative;

public static class AnalysisValidator
{
    private const int MaxReportedErrors = 5;

    /// <summary>
    /// Returns problems that would make the segments an unfaithful, incomplete,
    /// or unattributable representation of <paramref name="text"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string text,
        IReadOnlyCollection<string> knownSpeakerIds,
        IReadOnlyList<NarrativeSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(knownSpeakerIds);
        ArgumentNullException.ThrowIfNull(segments);

        var errors = new List<string>();
        var cursor = 0;
        for (var position = 0; position < segments.Count && errors.Count < MaxReportedErrors; position++)
        {
            var segment = segments[position];
            if (segment.Index != position)
            {
                errors.Add($"Segment at position {position} has index {segment.Index}.");
            }

            if (segment.Length <= 0)
            {
                errors.Add($"Segment {position} is empty.");
            }

            if (segment.Start != cursor)
            {
                errors.Add($"Segment {position} starts at {segment.Start} but {cursor} was expected (gap or overlap).");
            }

            if (segment.Kind == SegmentKind.Narration && segment.SpeakerId is not null)
            {
                errors.Add($"Narration segment {position} must not have a speaker.");
            }

            if (segment.Kind == SegmentKind.Dialogue
                && (segment.SpeakerId is null || !knownSpeakerIds.Contains(segment.SpeakerId)))
            {
                errors.Add($"Dialogue segment {position} has unknown speaker '{segment.SpeakerId}'.");
            }

            cursor = segment.Start + segment.Length;
        }

        if (errors.Count == 0 && cursor != text.Length)
        {
            errors.Add($"Segments cover {cursor} of {text.Length} characters.");
        }

        return errors;
    }

    public static void EnsureValid(
        string text,
        IReadOnlyCollection<string> knownSpeakerIds,
        IReadOnlyList<NarrativeSegment> segments)
    {
        var errors = Validate(text, knownSpeakerIds, segments);
        if (errors.Count > 0)
        {
            throw new InvalidDataException("Invalid chapter analysis: " + string.Join(" ", errors));
        }
    }
}
