using VBook.Core.Narrative;
using VBook.Core.Books;
using VBook.Core.Projects;
using VBook.Core.Providers;
using System.Text.Json;
using System.Security.Cryptography;

namespace VBook.Core.Generation;

public sealed record GeneratedChapter(GenerationJob Job, byte[] WavBytes, TimeSpan Duration);

public sealed class AudioGenerationService
{
    private static readonly TimeSpan InterSegmentSilence = TimeSpan.FromMilliseconds(250);

    private readonly IGenerationStore _store;
    private readonly ITtsProvider _provider;

    public AudioGenerationService(IGenerationStore store, ITtsProvider provider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<GeneratedChapter> GenerateChapterAsync(
        Guid projectId,
        string chapterId,
        string text,
        ChapterAnalysis analysis,
        NarrationMode mode,
        IReadOnlyDictionary<string, string> voiceAssignments,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken,
        SectionRole sectionRole = SectionRole.Body,
        SpeechEditReview? speechReview = null,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? voiceProfiles = null,
        string defaultLanguage = "",
        IProgress<GeneratedAudioSegment>? segmentProgress = null)
        => (await GenerateAsync(projectId, chapterId, text, analysis, mode, voiceAssignments, progress,
            cancellationToken, sectionRole, speechReview, voiceProfiles, defaultLanguage, null, segmentProgress))!;

    public Task<GeneratedChapter?> GenerateSegmentAsync(
        Guid projectId, string chapterId, string text, ChapterAnalysis analysis, NarrationMode mode,
        IReadOnlyDictionary<string, string> voiceAssignments, int segmentIndex,
        CancellationToken cancellationToken, SectionRole sectionRole = SectionRole.Body,
        SpeechEditReview? speechReview = null,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? voiceProfiles = null,
        string defaultLanguage = "", IProgress<GeneratedAudioSegment>? segmentProgress = null)
        => GenerateAsync(projectId, chapterId, text, analysis, mode, voiceAssignments, null,
            cancellationToken, sectionRole, speechReview, voiceProfiles, defaultLanguage, segmentIndex, segmentProgress);

    private async Task<GeneratedChapter?> GenerateAsync(
        Guid projectId, string chapterId, string text, ChapterAnalysis analysis, NarrationMode mode,
        IReadOnlyDictionary<string, string> voiceAssignments, IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken, SectionRole sectionRole, SpeechEditReview? speechReview,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? voiceProfiles, string defaultLanguage,
        int? segmentIndex, IProgress<GeneratedAudioSegment>? segmentProgress)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(voiceAssignments);
        if (!analysis.Matches(text, mode, sectionRole))
        {
            throw new InvalidOperationException("Analyze the current chapter text and narration mode before generating audio.");
        }

        if (speechReview is not null &&
            !string.Equals(speechReview.SourceHash, TextFingerprint.Compute(text), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review the spoken wording again after editing the chapter text.");
        }

        if (analysis.Segments.Count == 0)
        {
            throw new InvalidOperationException("The chapter has no text segments to generate.");
        }
        if (segmentIndex is < 0 || segmentIndex >= analysis.Segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        if (_provider.VoiceIds.Count == 0)
        {
            throw new InvalidOperationException("The configured TTS provider does not expose any voices.");
        }

        voiceProfiles = ResolveEffectiveVoiceProfiles(
            ResolveVoiceProfiles(analysis, voiceAssignments, voiceProfiles),
            defaultLanguage);
        var knownSpeakerIds = analysis.Characters.Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);
        AnalysisValidator.EnsureValid(text, knownSpeakerIds, analysis.Segments);

        var generationFingerprint = ComputeGenerationFingerprint(
            _provider.Capabilities.Name,
            analysis,
            speechReview,
            voiceProfiles);
        var job = await _store.GetOrCreateGenerationJobAsync(
            projectId,
            chapterId,
            generationFingerprint,
            mode,
            analysis.Segments.Count,
            cancellationToken);
        var existing = (await _store.LoadGeneratedSegmentsAsync(job.Id, cancellationToken))
            .ToDictionary(segment => segment.Index);

        await _store.UpdateGenerationJobAsync(job.Id, GenerationJobStatus.Running, cancellationToken: cancellationToken);
        try
        {
            var completed = 0;
            for (var index = 0; index < analysis.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segmentIndex is not null && index != segmentIndex)
                {
                    if (existing.TryGetValue(index, out var saved))
                    {
                        segmentProgress?.Report(saved);
                    }
                    continue;
                }
                var segment = analysis.Segments[index];
                var assignmentKey = segment.SpeakerId ?? "narrator";
                var legacyVoiceId = voiceAssignments.GetValueOrDefault(assignmentKey, _provider.VoiceIds[0]);
                var profile = voiceProfiles?.GetValueOrDefault(assignmentKey)
                    ?? new SpeechVoiceProfile(legacyVoiceId, string.Empty, string.Empty);
                var voiceId = profile.VoiceId;
                if (!_provider.VoiceIds.Contains(voiceId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Voice '{voiceId}' assigned to '{assignmentKey}' is not available from {_provider.Capabilities.Name}.");
                }

                if (segmentIndex is null && existing.TryGetValue(index, out var cached) &&
                    string.Equals(cached.VoiceId, voiceId, StringComparison.Ordinal) &&
                    string.Equals(cached.ProfileHash, ComputeProfileHash(profile), StringComparison.Ordinal))
                {
                    segmentProgress?.Report(cached);
                    completed++;
                    progress?.Report(new GenerationProgress(completed, analysis.Segments.Count,
                        $"Resuming: reused audio segment {index + 1} of {analysis.Segments.Count}."));
                    continue;
                }

                var segmentText = SpeechScriptBuilder.ApplyToSegment(text, segment, speechReview).Trim();
                var generated = await SynthesizeTextAsync(segmentText, profile, cancellationToken);
                generated = generated with { Index = index, ProfileHash = ComputeProfileHash(profile) };
                await _store.SaveGeneratedSegmentAsync(job.Id, generated, cancellationToken);
                segmentProgress?.Report(generated);
                completed++;
                progress?.Report(new GenerationProgress(completed, analysis.Segments.Count,
                    $"Generated audio segment {completed} of {analysis.Segments.Count}."));
            }

            var result = await _store.LoadGeneratedSegmentsAsync(job.Id, cancellationToken);
            if (result.Count != analysis.Segments.Count ||
                result.Where((segment, index) => segment.Index != index).Any())
            {
                if (segmentIndex is not null)
                {
                    await _store.UpdateGenerationJobAsync(job.Id, GenerationJobStatus.Paused, cancellationToken: cancellationToken);
                    return null;
                }
                throw new InvalidDataException("Generated chapter audio is incomplete or out of order.");
            }

            var wavBytes = WavPcm.Concatenate(
                result.Select(segment => segment.WavBytes),
                InterSegmentSilence);
            var duration = result.Aggregate(TimeSpan.Zero, (sum, segment) => sum + segment.Duration)
                + TimeSpan.FromTicks(InterSegmentSilence.Ticks * Math.Max(0, result.Count - 1));
            await _store.UpdateGenerationJobAsync(job.Id, GenerationJobStatus.Completed, cancellationToken: cancellationToken);
            progress?.Report(new GenerationProgress(result.Count, result.Count, "Audio generation complete."));
            return new GeneratedChapter(job with { Status = GenerationJobStatus.Completed }, wavBytes, duration);
        }
        catch (OperationCanceledException)
        {
            await _store.UpdateGenerationJobAsync(job.Id, GenerationJobStatus.Paused, cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _store.UpdateGenerationJobAsync(
                job.Id,
                GenerationJobStatus.Failed,
                exception.Message,
                CancellationToken.None);
            throw;
        }
    }

    private async Task<GeneratedAudioSegment> SynthesizeTextAsync(
        string text,
        SpeechVoiceProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            var silence = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(150));
            return new GeneratedAudioSegment(0, profile.VoiceId, silence, TimeSpan.FromMilliseconds(150));
        }

        var parts = SplitText(text, _provider.Capabilities.MaxContextCharacters);
        var audioParts = new List<byte[]>(parts.Count);
        var duration = TimeSpan.Zero;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _provider.SynthesizeAsync(
                new SpeechRequest(
                    part,
                    profile.VoiceId,
                    profile.Language,
                    profile.Instruction,
                    profile.VoiceDesignReferenceAudio,
                    profile.VoiceDesignReferenceText),
                cancellationToken);
            audioParts.Add(result.WavBytes);
            duration += result.Duration;
        }

        return new GeneratedAudioSegment(
            0,
            profile.VoiceId,
            WavPcm.Concatenate(audioParts, TimeSpan.Zero),
            duration);
    }

    public static string ComputeGenerationFingerprint(
        string providerName,
        ChapterAnalysis analysis,
        SpeechEditReview? speechReview,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? voiceProfiles = null)
    {
        var relevantSpeakers = analysis.Segments
            .Select(segment => segment.SpeakerId ?? "narrator")
            .Append("narrator")
            .ToHashSet(StringComparer.Ordinal);
        var relevantProfiles = voiceProfiles?
            .Where(profile => relevantSpeakers.Contains(profile.Key))
            .OrderBy(profile => profile.Key, StringComparer.Ordinal)
            .Select(profile => new
            {
                speakerId = profile.Key,
                profile.Value.VoiceId,
                profile.Value.Language,
                profile.Value.Instruction,
                profile.Value.VoiceDesignReferenceText,
                voiceDesignReferenceHash = ComputeVoiceDesignReferenceHash(
                    profile.Value.VoiceDesignReferenceAudio)
            });
        return TextFingerprint.Compute(JsonSerializer.Serialize(new
        {
            providerName,
            analysis.SourceHash,
            analysis.Mode,
            analysis.SectionRole,
            segments = analysis.Segments.Select(segment => new
            {
                segment.Index,
                segment.Kind,
                segment.SpeakerId,
                segment.Start,
                segment.Length
            }),
            acceptedSpeechEdits = speechReview?.Suggestions
                .Where(suggestion => suggestion.Status == SpeechEditStatus.Accepted)
                .Select(suggestion => new
                {
                    suggestion.Start,
                    suggestion.Length,
                    suggestion.OriginalText,
                    suggestion.ReplacementText
                }),
            voiceProfiles = relevantProfiles
        }));
    }

    public IReadOnlyDictionary<string, SpeechVoiceProfile> ResolveVoiceProfiles(
        ChapterAnalysis? analysis,
        IReadOnlyDictionary<string, string> voiceAssignments,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? savedProfiles)
    {
        ArgumentNullException.ThrowIfNull(voiceAssignments);
        if (_provider.VoiceIds.Count == 0)
        {
            return new Dictionary<string, SpeechVoiceProfile>(StringComparer.Ordinal);
        }

        var speakerIds = new List<string> { "narrator" };
        if (analysis is not null)
        {
            speakerIds.AddRange(analysis.Characters.Select(character => character.Id));
        }

        var profiles = new Dictionary<string, SpeechVoiceProfile>(StringComparer.Ordinal);
        for (var index = 0; index < speakerIds.Count; index++)
        {
            var speakerId = speakerIds[index];
            var defaultVoice = _provider.VoiceIds[index % _provider.VoiceIds.Count];
            var legacyVoice = voiceAssignments.GetValueOrDefault(speakerId, defaultVoice);
            var profile = savedProfiles?.GetValueOrDefault(speakerId)
                ?? new SpeechVoiceProfile(
                    _provider.VoiceIds.Contains(legacyVoice, StringComparer.Ordinal)
                        ? legacyVoice
                        : defaultVoice,
                    string.Empty,
                    string.Empty);
            if (!_provider.VoiceIds.Contains(profile.VoiceId, StringComparer.Ordinal))
            {
                profile = profile with { VoiceId = defaultVoice };
            }

            if (!_provider.GetLanguagesForVoice(profile.VoiceId)
                    .Contains(profile.Language, StringComparer.OrdinalIgnoreCase))
            {
                profile = profile with { Language = string.Empty };
            }

            profiles[speakerId] = profile;
        }

        return profiles;
    }

    public IReadOnlyDictionary<string, SpeechVoiceProfile> ResolveEffectiveVoiceProfiles(
        IReadOnlyDictionary<string, SpeechVoiceProfile> voiceProfiles,
        string defaultLanguage)
    {
        if (!TryResolveEffectiveVoiceProfiles(voiceProfiles, defaultLanguage, out var profiles, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return profiles;
    }

    public bool TryResolveEffectiveVoiceProfiles(
        IReadOnlyDictionary<string, SpeechVoiceProfile> voiceProfiles,
        string defaultLanguage,
        out IReadOnlyDictionary<string, SpeechVoiceProfile> effectiveProfiles,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(voiceProfiles);
        if (string.IsNullOrWhiteSpace(defaultLanguage) ||
            !_provider.Languages.Contains(defaultLanguage, StringComparer.OrdinalIgnoreCase))
        {
            effectiveProfiles = new Dictionary<string, SpeechVoiceProfile>(StringComparer.Ordinal);
            error = "Select a supported book language before generating audio.";
            return false;
        }

        var profiles = new Dictionary<string, SpeechVoiceProfile>(voiceProfiles, StringComparer.Ordinal);
        foreach (var speakerId in profiles.Keys.ToArray())
        {
            var profile = profiles[speakerId];
            var language = string.IsNullOrWhiteSpace(profile.Language)
                ? defaultLanguage
                : profile.Language;
            if (string.IsNullOrWhiteSpace(language))
            {
                effectiveProfiles = profiles;
                error = $"Select a language for the '{speakerId}' voice before generating audio.";
                return false;
            }

            var supportedLanguage = _provider.GetLanguagesForVoice(profile.VoiceId)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, language, StringComparison.OrdinalIgnoreCase));
            if (supportedLanguage is null)
            {
                effectiveProfiles = profiles;
                error =
                    $"Voice '{profile.VoiceId}' does not support language '{language}'. Select a supported language for this voice or change the voice.";
                return false;
            }

            profiles[speakerId] = profile with { Language = supportedLanguage };
        }

        effectiveProfiles = profiles;
        error = null;
        return true;
    }

    private static string ComputeProfileHash(SpeechVoiceProfile profile) =>
        TextFingerprint.Compute(JsonSerializer.Serialize(new
        {
            profile.VoiceId,
            profile.Language,
            profile.Instruction,
            profile.VoiceDesignReferenceText,
            voiceDesignReferenceHash = ComputeVoiceDesignReferenceHash(
                profile.VoiceDesignReferenceAudio)
        }));

    private static string? ComputeVoiceDesignReferenceHash(byte[]? referenceAudio) =>
        referenceAudio is null
            ? null
            : Convert.ToHexString(SHA256.HashData(referenceAudio));

    private static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        if (maxLength < 2)
        {
            throw new InvalidOperationException("The TTS provider reports an unusable context budget.");
        }

        var parts = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + maxLength, text.Length);
            if (end < text.Length)
            {
                var minimumBoundary = start + (end - start) / 2;
                var sentenceBoundary = FindSentenceBoundary(text, minimumBoundary, end);
                if (sentenceBoundary > start)
                {
                    end = sentenceBoundary;
                }
                else
                {
                    var boundary = text.LastIndexOfAny([' ', '\t', '\r', '\n'], end - 1, end - start);
                    if (boundary > minimumBoundary)
                    {
                        end = boundary + 1;
                    }
                }
            }

            if (end < text.Length && char.IsLowSurrogate(text[end]))
            {
                end--;
            }

            parts.Add(text[start..end]);
            start = end;
        }

        return parts;
    }

    private static int FindSentenceBoundary(string text, int start, int end)
    {
        for (var index = end - 1; index >= start; index--)
        {
            if (text[index] is not ('.' or '?' or '!'))
            {
                continue;
            }

            var afterSentence = index + 1;
            while (afterSentence < end && text[afterSentence] is '"' or '\'' or ')' or ']' or '}' or '”' or '’')
            {
                afterSentence++;
            }

            if (afterSentence < end && char.IsWhiteSpace(text[afterSentence]))
            {
                return afterSentence + 1;
            }
        }

        return -1;
    }
}
