using System.IO.Compression;
using System.Text;
using VBook.Core.Books;
using Xunit;

namespace VBook.Core.Tests;

public sealed class EpubImporterTests
{
    [Fact]
    public void Import_PreservesSpineOrderAndParagraphBoundaries()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Test Book</dc:title></metadata>
              <manifest>
                <item id="first" href="text/first.xhtml" media-type="application/xhtml+xml"/>
                <item id="second" href="text/second.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="second"/><itemref idref="first"/></spine>
            </package>
            """,
            ("text/first.xhtml", "<html><body><h1>First</h1><p>First paragraph.</p><p>Second paragraph!</p></body></html>"),
            ("text/second.xhtml", "<html><body><h1>Second</h1><p>Opening line.</p></body></html>"));

        var book = new EpubImporter().Import(stream);

        Assert.Equal("Test Book", book.Title);
        Assert.Equal(new[] { "Second", "First" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("Second\n\nOpening line.", book.Chapters[0].Text);
        Assert.Equal("First\n\nFirst paragraph.\n\nSecond paragraph!", book.Chapters[1].Text);
    }

    [Fact]
    public void Import_SkipsFrontMatterAndUsesTocTitles()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Book</dc:title></metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="copy" href="copy.xhtml" media-type="application/xhtml+xml"/>
                <item id="toc" href="contents.xhtml" media-type="application/xhtml+xml"/>
                <item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/>
                <item id="ack" href="ack.xhtml" media-type="application/xhtml+xml"/>
                <item id="promo" href="promo.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="copy"/><itemref idref="toc"/><itemref idref="c1"/><itemref idref="ack"/><itemref idref="promo"/></spine>
            </package>
            """,
            ("nav.xhtml", "<html xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='c1.xhtml'>The Storm</a></li></ol></nav></body></html>"),
            ("copy.xhtml", "<html><body><h1>Copyright</h1><p>All rights reserved.</p></body></html>"),
            ("contents.xhtml", "<html><body><h1>Chapters</h1><p><a href='c1.xhtml'>One</a></p><p><a href='c1.xhtml'>Two</a></p><p><a href='c1.xhtml'>Three</a></p><p><a href='c1.xhtml'>Four</a></p><p><a href='c1.xhtml'>Five</a></p></body></html>"),
            ("c1.xhtml", "<html><body><h1>1</h1><p>It rained.</p></body></html>"),
            ("ack.xhtml", "<html><body><h1>Acknowledgements</h1><p>Thanks.</p></body></html>"),
            ("promo.xhtml", "<html><body><h1>Also by the author</h1><p>Other things.</p></body></html>"));

        var book = new EpubImporter().Import(stream);

        Assert.Equal(new[] { "The Storm", "Acknowledgements" }, book.Chapters.Select(c => c.Title));
        Assert.Equal(SectionRole.Body, book.Chapters[0].Role);
        Assert.Equal(SectionRole.BackMatter, book.Chapters[1].Role);
        Assert.Equal(new[] { "copy", "toc", "promo" }, book.SkippedSections.Select(s => s.Id));
    }

    [Fact]
    public void Import_SkipsSectionsByEpubType()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <manifest>
                <item id="t" href="t.xhtml" media-type="application/xhtml+xml"/>
                <item id="c" href="c.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="t"/><itemref idref="c"/></spine>
            </package>
            """,
            ("t.xhtml", "<html xmlns:epub='http://www.idpf.org/2007/ops'><body epub:type='titlepage'><h1>My Story</h1><p>by Someone</p></body></html>"),
            ("c.xhtml", "<html xmlns:epub='http://www.idpf.org/2007/ops'><body epub:type='bodymatter chapter'><h1>Start</h1><p>Hello.</p></body></html>"));

        var book = new EpubImporter().Import(stream);

        Assert.Single(book.Chapters);
        Assert.Equal("Start", book.Chapters[0].Title);
        Assert.Contains("titlepage", book.SkippedSections.Single().Reason);
    }

    [Fact]
    public void Import_PreparesStructuredContentForSpeech()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
              <spine><itemref idref="chapter"/></spine>
            </package>
            """,
            ("chapter.xhtml",
                """
                <html><body>
                  <h1>Examples</h1>
                  <p>Opening paragraph.</p>
                  <ol><li>First step</li><li>Second step</li></ol>
                  <ul><li>Apples</li><li>Oranges</li></ul>
                  <pre>print("not spoken")</pre>
                  <table><tr><th>Term</th><th>Meaning</th></tr><tr><td>local</td><td>on this device</td></tr></table>
                  <p>First verse line.<br/>Second verse line.</p>
                </body></html>
                """));

        var chapter = Assert.Single(new EpubImporter().Import(stream).Chapters);

        Assert.Contains("Examples\n\nOpening paragraph.", chapter.Text);
        Assert.Contains("1. First step", chapter.Text);
        Assert.Contains("2. Second step", chapter.Text);
        Assert.Contains("Item 1: Apples", chapter.Text);
        Assert.Contains("Item 2: Oranges", chapter.Text);
        Assert.Contains("Code block omitted.", chapter.Text);
        Assert.DoesNotContain("print(", chapter.Text);
        Assert.Contains("Table with 2 rows and 2 columns.", chapter.Text);
        Assert.Contains("Term: local; Meaning: on this device.", chapter.Text);
        Assert.Contains("First verse line.\nSecond verse line.", chapter.Text);
    }

    [Fact]
    public void Import_RejectsContainerWithoutPackagePath()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "META-INF/container.xml", "<container/>");
        }

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => new EpubImporter().Import(stream));
    }

    [Fact]
    public void Import_RejectsPathEscapingArchiveRoot()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <manifest><item id="chapter" href="../../chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
              <spine><itemref idref="chapter"/></spine>
            </package>
            """);

        Assert.Throws<InvalidDataException>(() => new EpubImporter().Import(stream));
    }

    [Fact]
    public void Import_RejectsMalformedChapterXml()
    {
        using var stream = CreateEpub(
            """
            <package xmlns="http://www.idpf.org/2007/opf">
              <manifest><item id="chapter" href="text/chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
              <spine><itemref idref="chapter"/></spine>
            </package>
            """,
            ("text/chapter.xhtml", "<html><body><p>unclosed"));

        var exception = Assert.Throws<InvalidDataException>(() => new EpubImporter().Import(stream));

        Assert.Contains("malformed XML", exception.Message);
    }

    private static MemoryStream CreateEpub(string package, params (string Path, string Content)[] chapters)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "META-INF/container.xml",
                """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles>
                </container>
                """);
            WriteEntry(archive, "OEBPS/content.opf", package);
            foreach (var (path, content) in chapters)
            {
                WriteEntry(archive, $"OEBPS/{path}", content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(contents);
    }
}
