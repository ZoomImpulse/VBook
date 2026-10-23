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
                Title = (string?)item.Attribute("title"),
                Properties = (string?)item.Attribute("properties")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToArray();

        if (manifestItems is null || manifestItems.Length == 0)
        {
            throw new InvalidDataException("The EPUB package has no readable manifest.");
        }

        var manifest = new Dictionary<string, (string Href, string? MediaType, string? Title, string? Properties)>(StringComparer.Ordinal);
        foreach (var item in manifestItems)
        {
            if (!manifest.TryAdd(item.Id!, (item.Href!, item.MediaType, item.Title, item.Properties)))
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
        var guideTypes = ReadGuide(package, packageDirectory);
        var tocTitles = ReadTocTitles(archive, package, manifest, packageDirectory);
        var chapters = new List<ImportedChapter>();
        var skipped = new List<SkippedSection>();
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
            tocTitles.TryGetValue(contentPath, out var tocTitle);
            guideTypes.TryGetValue(contentPath, out var guideType);
            var chapterTitle = FirstNonEmpty(tocTitle, heading, item.Title, $"Chapter {chapters.Count + 1}");

            var classification = SectionClassifier.Classify(
                document, FirstNonEmpty(tocTitle, heading, item.Title, string.Empty), item.Properties, guideType, text);
            if (classification.Role == SectionRole.Skip)
            {
                skipped.Add(new SkippedSection(id!, chapterTitle, classification.Reason ?? "not audiobook content"));
                continue;
            }

            chapters.Add(new ImportedChapter(id!, chapterTitle, text, classification.Role));
        }

        if (chapters.Count == 0)
        {
            throw new InvalidDataException("The EPUB contains no readable chapter text.");
        }

        return new ImportedBook(FirstNonEmpty(title, "Untitled book"), chapters, skipped);
    }

    private static Dictionary<string, string> ReadGuide(XDocument package, string packageDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = package.Root?.Element(OpfNamespace + "guide")?.Elements(OpfNamespace + "reference");
        foreach (var reference in references ?? Enumerable.Empty<XElement>())
        {
            var type = (string?)reference.Attribute("type");
            var href = (string?)reference.Attribute("href");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            try
            {
                result.TryAdd(ResolveArchivePath(packageDirectory, href), type);
            }
            catch (InvalidDataException)
            {
            }
        }

        return result;
    }

    // Titles come from the EPUB's own table of contents; the first entry per file wins.
    private static Dictionary<string, string> ReadTocTitles(
        ZipArchive archive,
        XDocument package,
        Dictionary<string, (string Href, string? MediaType, string? Title, string? Properties)> manifest,
        string packageDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var tocId = (string?)package.Root?.Element(OpfNamespace + "spine")?.Attribute("toc");
        foreach (var (id, item) in manifest)
        {
            var isNav = item.Properties?.Split(' ').Contains("nav") == true;
            var isNcx = id == tocId || item.MediaType == "application/x-dtbncx+xml";
            if (!isNav && !isNcx)
            {
                continue;
            }

            try
            {
                var path = ResolveArchivePath(packageDirectory, item.Href);
                var document = ReadXml(archive, path);
                var directory = GetDirectory(path);
                var entries = isNav
                    ? document.Descendants().Where(e => e.Name.LocalName == "a")
                        .Select(a => (Href: (string?)a.Attribute("href"), Title: a.Value))
                    : document.Descendants().Where(e => e.Name.LocalName == "navPoint")
                        .Select(point => (
                            Href: (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src"),
                            Title: point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value ?? string.Empty));

                foreach (var (href, label) in entries)
                {
                    var cleaned = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (string.IsNullOrWhiteSpace(href) || cleaned.Length == 0)
                    {
                        continue;
                    }

                    result.TryAdd(ResolveArchivePath(directory, href), cleaned);
                }
            }
            catch (InvalidDataException)
            {
                // A broken table of contents must not block the import.
            }
        }

        return result;
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
        return NormalizeText(builder.ToString());
    }

    private static string NormalizeText(string text)
    {
        var normalizedLines = text
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

        if (element.Name.LocalName == "pre")
        {
            if (!string.IsNullOrWhiteSpace(element.Value))
            {
                AppendBlockBoundary(builder);
                builder.Append("Code block omitted.");
                AppendBlockBoundary(builder);
            }

            return;
        }

        if (element.Name.LocalName == "table")
        {
            var spokenTable = FormatTable(element);
            if (spokenTable.Length > 0)
            {
                AppendBlockBoundary(builder);
                builder.Append(spokenTable);
                AppendBlockBoundary(builder);
            }

            return;
        }

        if (element.Name.LocalName is "ol" or "ul")
        {
            AppendList(element, builder);
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

    private static void AppendList(XElement list, StringBuilder builder)
    {
        var ordered = list.Name.LocalName == "ol";
        var index = 0;
        foreach (var item in list.Elements().Where(child => child.Name.LocalName == "li"))
        {
            index++;
            var itemContent = new StringBuilder();
            foreach (var node in item.Nodes())
            {
                if (node is XText text)
                {
                    itemContent.Append(text.Value);
                }
                else if (node is XElement child)
                {
                    AppendNodeText(child, itemContent);
                }
            }

            var itemText = NormalizeText(itemContent.ToString());
            if (itemText.Length == 0)
            {
                continue;
            }

            AppendBlockBoundary(builder);
            builder.Append(ordered ? $"{index}. " : $"Item {index}: ");
            builder.Append(itemText);
            AppendBlockBoundary(builder);
        }
    }

    private static string FormatTable(XElement table)
    {
        var rows = table.Descendants()
            .Where(element => element.Name.LocalName == "tr"
                && element.Ancestors().FirstOrDefault(parent => parent.Name.LocalName == "table") == table)
            .Select(row => row.Elements()
                .Where(cell => cell.Name.LocalName is "th" or "td")
                .Select(cell =>
                {
                    var cellText = new StringBuilder();
                    AppendNodeText(cell, cellText);
                    return NormalizeText(cellText.ToString());
                })
                .ToArray())
            .Where(cells => cells.Length > 0)
            .ToArray();
        if (rows.Length == 0)
        {
            return string.Empty;
        }

        var headers = rows[0];
        var hasHeaders = table.Descendants()
            .Any(element => element.Name.LocalName == "th");
        var columnCount = rows.Max(row => row.Length);
        var builder = new StringBuilder($"Table with {rows.Length} rows and {columnCount} columns.");
        var firstDataRow = 0;
        if (hasHeaders)
        {
            builder.Append(" Column headings: ");
            builder.Append(string.Join("; ", headers.Select((header, index) =>
                $"Column {index + 1}: {NonEmptyCell(header)}")));
            builder.Append('.');
            firstDataRow = 1;
        }

        for (var rowIndex = firstDataRow; rowIndex < rows.Length; rowIndex++)
        {
            builder.Append($" Row {rowIndex - firstDataRow + 1}: ");
            builder.Append(string.Join("; ", rows[rowIndex].Select((cell, cellIndex) =>
                hasHeaders && cellIndex < headers.Length
                    ? $"{NonEmptyCell(headers[cellIndex])}: {NonEmptyCell(cell)}"
                    : $"Column {cellIndex + 1}: {NonEmptyCell(cell)}")));
            builder.Append('.');
        }

        return builder.ToString();
    }

    private static string NonEmptyCell(string value) =>
        string.IsNullOrWhiteSpace(value) ? "empty" : value;

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
