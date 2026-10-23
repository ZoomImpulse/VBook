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
