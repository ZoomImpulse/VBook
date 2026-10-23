using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VBook.Core.Books;

public sealed class EpubImporter : IBookImporter
{
    private static readonly XNamespace ContainerNamespace =
        "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNamespace =
        "http://www.idpf.org/2007/opf";
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "blockquote", "div", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "ol", "p", "pre", "section", "ul"
    };

    public ImportedBook Import(Stream epubStream)
    {
        ArgumentNullException.ThrowIfNull(epubStream);
        if (!epubStream.CanRead)
        {
            throw new ArgumentException("The EPUB stream must be readable.", nameof(epubStream));
        }

        using var archive = new ZipArchive(epubStream, ZipArchiveMode.Read, leaveOpen: true);
        var container = ReadXml(archive, "META-INF/container.xml");
        var packagePath = container
            .Root?
            .Element(ContainerNamespace + "rootfiles")?
            .Elements(ContainerNamespace + "rootfile")
            .Select(element => (string?)element.Attribute("full-path"))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        if (packagePath is null)
        {
            throw new InvalidDataException("The EPUB container does not identify a package document.");
        }

        packagePath = NormalizeArchivePath(packagePath);
        var package = ReadXml(archive, packagePath);
        var metadata = package.Root?.Element(OpfNamespace + "metadata");
        var title = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "title")?.Value
            ?.Trim();
        var manifestItems = package.Root?.Element(OpfNamespace + "manifest")?
            .Elements(OpfNamespace + "item")
            .Select(item => new
            {
                Id = (string?)item.Attribute("id"),
                Href = (string?)item.Attribute("href"),
                MediaType = (string?)item.Attribute("media-type"),
                Title = (string?)item.Attribute("title")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToArray();

        if (manifestItems is null || manifestItems.Length == 0)
        {
            throw new InvalidDataException("The EPUB package has no readable manifest.");
        }

        var manifest = new Dictionary<string, (string Href, string? MediaType, string? Title)>(StringComparer.Ordinal);
        foreach (var item in manifestItems)
        {
            if (!manifest.TryAdd(item.Id!, (item.Href!, item.MediaType, item.Title)))
            {
                throw new InvalidDataException($"The EPUB manifest contains duplicate item ID '{item.Id}'.");
            }
        }

        var spine = package.Root?.Element(OpfNamespace + "spine")?
            .Elements(OpfNamespace + "itemref")
            .Where(item => !string.Equals((string?)item.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase))
            .Select(item => (string?)item.Attribute("idref"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();

        if (spine is null || spine.Length == 0)
        {
            throw new InvalidDataException("The EPUB package has no readable spine.");
        }

        var packageDirectory = GetDirectory(packagePath);
        var chapters = new List<ImportedChapter>();
        foreach (var id in spine)
        {
            if (!manifest.TryGetValue(id!, out var item))
            {
                throw new InvalidDataException($"The EPUB spine references unknown manifest ID '{id}'.");
            }

            if (!IsXhtml(item.MediaType, item.Href))
            {
                continue;
            }

            var contentPath = ResolveArchivePath(packageDirectory, item.Href);
            var document = ReadXml(archive, contentPath);
            var text = ExtractText(document);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var heading = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName is "h1" or "h2" or "h3")
                ?.Value;
            var chapterTitle = FirstNonEmpty(heading, item.Title, $"Chapter {chapters.Count + 1}");
            chapters.Add(new ImportedChapter(id!, chapterTitle, text));
        }

        if (chapters.Count == 0)
        {
            throw new InvalidDataException("The EPUB contains no readable chapter text.");
        }

        return new ImportedBook(FirstNonEmpty(title, "Untitled book"), chapters);
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Required EPUB entry '{path}' is missing.");
        if (entry.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"EPUB entry '{path}' exceeds the 64 MiB safety limit.");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024
        });
        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"EPUB entry '{path}' contains malformed XML.", exception);
        }
    }

    private static bool IsXhtml(string? mediaType, string href)
    {
        return string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
               href.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               href.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractText(XDocument document)
    {
        var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body")
            ?? document.Root;
        if (body is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendNodeText(body, builder);
        var normalizedLines = builder.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();

        var result = new StringBuilder();
        var previousBlank = true;
        foreach (var line in normalizedLines)
        {
            var blank = line.Length == 0;
            if (blank && previousBlank)
            {
                continue;
            }

            if (result.Length > 0)
            {
                result.Append('\n');
            }

            result.Append(line);
            previousBlank = blank;
        }

        return result.ToString().Trim();
    }

    private static void AppendNodeText(XElement element, StringBuilder builder)
    {
        if (element.Name.LocalName is "script" or "style" or "head")
        {
            return;
        }

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
            }
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "br")
                {
                    builder.Append('\n');
                }
                else
                {
                    var isBlock = BlockElements.Contains(child.Name.LocalName);
                    if (isBlock)
                    {
                        AppendBlockBoundary(builder);
                    }

                    AppendNodeText(child, builder);
                    if (isBlock)
                    {
                        AppendBlockBoundary(builder);
                    }
                }
            }
        }
    }

    private static void AppendBlockBoundary(StringBuilder builder)
    {
        if (builder.Length == 0 || builder[^1] == '\n')
        {
            if (builder.Length < 2 || builder[^2] != '\n')
            {
                builder.Append('\n');
            }

            return;
        }

        builder.Append("\n\n");
    }

    private static string ResolveArchivePath(string baseDirectory, string href)
    {
        var reference = Uri.UnescapeDataString(href.Split('#', '?')[0]).Replace('\\', '/');
        if (reference.StartsWith("/", StringComparison.Ordinal) ||
            Uri.TryCreate(reference, UriKind.Absolute, out _))
        {
            throw new InvalidDataException("EPUB manifest references must be relative archive paths.");
        }

        return NormalizeArchivePath($"{baseDirectory}/{reference}");
    }

    private static string NormalizeArchivePath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new InvalidDataException("An EPUB path attempts to escape the archive root.");
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static string GetDirectory(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : path[..separatorIndex];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
