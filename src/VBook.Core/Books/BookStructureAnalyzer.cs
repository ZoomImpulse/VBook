using VBook.Core.Providers;

namespace VBook.Core.Books;

public sealed class BookStructureAnalyzer
{
    private readonly IBookStructureProvider _provider;

    public BookStructureAnalyzer(IBookStructureProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<BookStructureResult> AnalyzeAsync(
        string title,
        IReadOnlyList<ImportedChapter> chapters,
        IProgress<BookStructureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(chapters);
        if (chapters.Count == 0)
        {
            throw new ArgumentException("At least one chapter is required.", nameof(chapters));
        }

        if (chapters.Select(chapter => chapter.Id).Distinct(StringComparer.Ordinal).Count() != chapters.Count)
        {
            throw new InvalidDataException("Chapter IDs must be unique for structure analysis.");
        }

        var budget = _provider.Capabilities.MaxContextCharacters;
        if (budget < 512)
        {
            throw new InvalidOperationException("The structure provider reports an unusable context budget.");
        }

        var batches = CreateBatches(title, chapters, budget);
        var analyzed = new Dictionary<string, ChapterStructureSuggestion>(StringComparer.Ordinal);
        var genres = new List<string>();
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new BookStructureProgress(
                (double)batchIndex / batches.Count,
                $"Analyzing book structure, part {batchIndex + 1} of {batches.Count}…"));
            var result = await _provider.AnalyzeAsync(
                new BookStructureRequest(title, batches[batchIndex]),
                cancellationToken);
            ValidateBatch(batches[batchIndex], result);
            genres.Add(result.Genre.Trim());
            foreach (var suggestion in result.Chapters)
            {
                analyzed.Add(suggestion.ChapterId, suggestion);
            }
        }

        progress?.Report(new BookStructureProgress(1, "Book structure analysis complete."));
        var genre = genres.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
        return new BookStructureResult(
            genre,
            chapters.Select(chapter => analyzed[chapter.Id]).ToArray());
    }

    private static IReadOnlyList<IReadOnlyList<BookStructureChapter>> CreateBatches(
        string title,
        IReadOnlyList<ImportedChapter> chapters,
        int budget)
    {
        var batches = new List<IReadOnlyList<BookStructureChapter>>();
        var current = new List<BookStructureChapter>();
        var currentLength = title.Length + 256;
        foreach (var chapter in chapters)
        {
            var excerptLimit = Math.Min(
                1400,
                budget - title.Length - chapter.Id.Length - chapter.Title.Length - 512);
            if (excerptLimit < 1)
            {
                throw new InvalidOperationException(
                    $"The structure provider context budget is too small for chapter '{chapter.Title}'.");
            }

            var excerpt = CreateExcerpt(chapter.Text, excerptLimit);
            var item = new BookStructureChapter(chapter.Id, chapter.Title, excerpt, chapter.Role);
            var estimatedLength = chapter.Id.Length + chapter.Title.Length + excerpt.Length + 180;
            if (current.Count > 0 && currentLength + estimatedLength > budget)
            {
                batches.Add(current);
                current = [];
                currentLength = title.Length + 256;
            }

            current.Add(item);
            currentLength += estimatedLength;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static string CreateExcerpt(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        const string separator = "\n[…]\n";
        var firstLength = (maxLength - separator.Length) * 3 / 4;
        var lastLength = maxLength - separator.Length - firstLength;
        return string.Concat(text.AsSpan(0, firstLength), separator, text.AsSpan(text.Length - lastLength));
    }

    private static void ValidateBatch(
        IReadOnlyList<BookStructureChapter> requested,
        BookStructureResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Genre))
        {
            throw new InvalidDataException("The structure provider returned no book genre.");
        }

        var expected = requested.Select(chapter => chapter.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var suggestion in result.Chapters)
        {
            if (!expected.Remove(suggestion.ChapterId))
            {
                throw new InvalidDataException(
                    $"The structure provider returned an unexpected or duplicate chapter ID '{suggestion.ChapterId}'.");
            }

            if (!Enum.IsDefined(suggestion.Role) ||
                suggestion.Confidence is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(suggestion.Rationale))
            {
                throw new InvalidDataException(
                    $"The structure provider returned an invalid suggestion for chapter '{suggestion.ChapterId}'.");
            }
        }

        if (expected.Count > 0)
        {
            throw new InvalidDataException(
                $"The structure provider omitted chapter IDs: {string.Join(", ", expected)}.");
        }
    }
}
