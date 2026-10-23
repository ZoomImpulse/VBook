namespace VBook.Core.Books;

public sealed record ImportedBook(string Title, IReadOnlyList<ImportedChapter> Chapters);

public sealed record ImportedChapter(string Id, string Title, string Text);

public interface IBookImporter
{
    ImportedBook Import(Stream epubStream);
}
