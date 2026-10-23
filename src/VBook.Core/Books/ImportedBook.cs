namespace VBook.Core.Books;

public sealed record ImportedBook(
    string Title,
    IReadOnlyList<ImportedChapter> Chapters,
    IReadOnlyList<SkippedSection>? Skipped = null)
{
    public IReadOnlyList<SkippedSection> SkippedSections { get; } = Skipped ?? Array.Empty<SkippedSection>();
}

public sealed record ImportedChapter(string Id, string Title, string Text, SectionRole Role = SectionRole.Body);

public sealed record SkippedSection(string Id, string Title, string Reason);

public interface IBookImporter
{
    ImportedBook Import(Stream epubStream);
}
