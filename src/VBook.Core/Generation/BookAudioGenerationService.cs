using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;

namespace VBook.Core.Generation;

public sealed record GeneratedBookChapter(string Id, string Title, string WavPath, TimeSpan Duration);

public sealed class GeneratedBookAudio : IDisposable
{
    private readonly string? _ownedDirectory;
    private bool _disposed;

    public GeneratedBookAudio(string title, IReadOnlyList<GeneratedBookChapter> chapters)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Chapters = chapters ?? throw new ArgumentNullException(nameof(chapters));
    }

    internal GeneratedBookAudio(
        string title,
        IReadOnlyList<GeneratedBookChapter> chapters,
        string ownedDirectory)
        : this(title, chapters) =>
        _ownedDirectory = ownedDirectory;

    public string Title { get; }

    public IReadOnlyList<GeneratedBookChapter> Chapters { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
        {
            Directory.Delete(_ownedDirectory, recursive: true);
        }
    }
}

public sealed class BookAudioGenerationService
{
    private readonly INarrativeWorker _narrativeWorker;
    private readonly INarrativeStore _narrativeStore;
    private readonly AudioGenerationService _chapterGeneration;

    public BookAudioGenerationService(
        INarrativeWorker narrativeWorker,
        INarrativeStore narrativeStore,
        AudioGenerationService chapterGeneration)
    {
        _narrativeWorker = narrativeWorker ?? throw new ArgumentNullException(nameof(narrativeWorker));
        _narrativeStore = narrativeStore ?? throw new ArgumentNullException(nameof(narrativeStore));
        _chapterGeneration = chapterGeneration ?? throw new ArgumentNullException(nameof(chapterGeneration));
    }

    public async Task<GeneratedBookAudio> GenerateAsync(
        Guid projectId,
        string title,
        IReadOnlyList<ImportedChapter> chapters,
        NarrationMode mode,
        IReadOnlyDictionary<string, string> voiceAssignments,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, SpeechVoiceProfile>? voiceProfiles = null,
        string defaultLanguage = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(voiceAssignments);
        var narratable = chapters.Where(chapter => chapter.Role != SectionRole.Skip).ToArray();
        if (narratable.Length == 0)
        {
            throw new InvalidOperationException("The book has no chapters selected for narration.");
        }

        if (narratable.Select(chapter => chapter.Id).Distinct(StringComparer.Ordinal).Count() != narratable.Length)
        {
            throw new InvalidDataException("The book contains duplicate chapter identifiers.");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"vbook-book-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var generatedChapters = new List<GeneratedBookChapter>(narratable.Length);
            for (var index = 0; index < narratable.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = narratable[index];
                var analysis = await GetCurrentAnalysisAsync(projectId, chapter, mode, cancellationToken);
                var review = await GetCurrentSpeechReviewAsync(projectId, chapter, analysis, cancellationToken);
                var chapterProgress = new InlineProgress(report =>
                    progress?.Report(new GenerationProgress(
                        index + report.Fraction,
                        narratable.Length,
                        $"{chapter.Title}: {report.Message}")));
                var generated = await _chapterGeneration.GenerateChapterAsync(
                    projectId,
                    chapter.Id,
                    chapter.Text,
                    analysis,
                    mode,
                    voiceAssignments,
                    chapterProgress,
                    cancellationToken,
                    chapter.Role,
                    review,
                    voiceProfiles,
                    defaultLanguage);
                var wavPath = Path.Combine(workingDirectory, $"{index:D6}.wav");
                await File.WriteAllBytesAsync(wavPath, generated.WavBytes, cancellationToken);
                generatedChapters.Add(new GeneratedBookChapter(
                    chapter.Id,
                    chapter.Title,
                    wavPath,
                    generated.Duration));
                progress?.Report(new GenerationProgress(
                    index + 1,
                    narratable.Length,
                    $"Completed chapter {index + 1} of {narratable.Length}: {chapter.Title}."));
            }

            return new GeneratedBookAudio(title, generatedChapters, workingDirectory);
        }
        catch
        {
            Directory.Delete(workingDirectory, recursive: true);
            throw;
        }
    }

    private async Task<ChapterAnalysis> GetCurrentAnalysisAsync(
        Guid projectId,
        ImportedChapter chapter,
        NarrationMode mode,
        CancellationToken cancellationToken)
    {
        var analysis = await _narrativeStore.LoadAnalysisAsync(projectId, chapter.Id, cancellationToken);
        if (analysis is not null && analysis.Matches(chapter.Text, mode, chapter.Role))
        {
            return analysis;
        }

        var characters = await _narrativeStore.LoadCharactersAsync(projectId, cancellationToken);
        analysis = await _narrativeWorker.AnalyzeChapterAsync(
            new AnalysisRequest(chapter.Id, chapter.Text, characters, mode, chapter.Role),
            null,
            cancellationToken);
        if (!string.Equals(analysis.ChapterId, chapter.Id, StringComparison.Ordinal) ||
            !analysis.Matches(chapter.Text, mode, chapter.Role))
        {
            throw new InvalidDataException($"Narrative analysis for '{chapter.Title}' did not match its current source.");
        }

        AnalysisValidator.EnsureValid(
            chapter.Text,
            analysis.Characters.Select(character => character.Id).ToHashSet(StringComparer.Ordinal),
            analysis.Segments);
        await _narrativeStore.SaveAnalysisAsync(projectId, analysis, cancellationToken);
        return analysis;
    }

    private async Task<SpeechEditReview> GetCurrentSpeechReviewAsync(
        Guid projectId,
        ImportedChapter chapter,
        ChapterAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var review = await _narrativeStore.LoadSpeechReviewAsync(projectId, chapter.Id, cancellationToken);
        if (review is not null &&
            string.Equals(review.SourceHash, analysis.SourceHash, StringComparison.Ordinal))
        {
            return review;
        }

        review = new SpeechEditReview(
            chapter.Id,
            analysis.SourceHash,
            analysis.SpeechSuggestions ?? []);
        await _narrativeStore.SaveSpeechReviewAsync(projectId, review, cancellationToken);
        return review;
    }

    private sealed class InlineProgress(Action<GenerationProgress> report) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) => report(value);
    }
}
