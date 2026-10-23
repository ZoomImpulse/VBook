using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VBook.Core.Books;

public enum SectionRole
{
    Body,
    FrontMatter,
    BackMatter,
    Skip
}

public sealed record SectionClassification(SectionRole Role, string? Reason);

/// <summary>
/// Decides whether an EPUB spine item belongs in an audiobook. Structural hints
/// (epub:type, guide, nav property) win over title and link-density heuristics.
/// </summary>
public static class SectionClassifier
{
    private static readonly Dictionary<string, SectionRole> TypeRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["toc"] = SectionRole.Skip,
        ["landmarks"] = SectionRole.Skip,
        ["loi"] = SectionRole.Skip,
        ["lot"] = SectionRole.Skip,
        ["cover"] = SectionRole.Skip,
        ["titlepage"] = SectionRole.Skip,
        ["title-page"] = SectionRole.Skip,
        ["halftitlepage"] = SectionRole.Skip,
        ["copyright-page"] = SectionRole.Skip,
        ["colophon"] = SectionRole.Skip,
        ["index"] = SectionRole.Skip,
        ["imprint"] = SectionRole.Skip,
        ["dedication"] = SectionRole.FrontMatter,
        ["epigraph"] = SectionRole.FrontMatter,
        ["foreword"] = SectionRole.FrontMatter,
        ["preface"] = SectionRole.FrontMatter,
        ["prologue"] = SectionRole.Body,
        ["epilogue"] = SectionRole.Body,
        ["afterword"] = SectionRole.BackMatter,
        ["acknowledgments"] = SectionRole.BackMatter,
        ["acknowledgements"] = SectionRole.BackMatter,
        ["appendix"] = SectionRole.BackMatter,
        ["glossary"] = SectionRole.Skip,
        ["bibliography"] = SectionRole.Skip,
        ["notes"] = SectionRole.Skip,
        ["endnotes"] = SectionRole.Skip,
        ["chapter"] = SectionRole.Body,
        ["part"] = SectionRole.Body,
        ["bodymatter"] = SectionRole.Body
    };

    private static readonly (Regex Pattern, SectionRole Role, string Reason)[] TitleRules =
    {
        (Rx(@"^(table of )?contents?$"), SectionRole.Skip, "table of contents"),
        (Rx(@"^(title page|half[- ]?title|cover|front cover|back cover)$"), SectionRole.Skip, "title or cover page"),
        (Rx(@"^(copyright|copyright (page|notice)|legal notice|imprint|colophon|credits)$"), SectionRole.Skip, "copyright page"),
        (Rx(@"^(also by|other books|books by|more books|you may also like|by the same author).*"), SectionRole.Skip, "promotional page"),
        (Rx(@".*(newsletter|sign up|mailing list|reading group).*"), SectionRole.Skip, "promotional page"),
        (Rx(@"^(index|bibliography|glossary|endnotes|notes|list of (illustrations|figures|tables))$"), SectionRole.Skip, "reference matter"),
        (Rx(@"^(dedication|epigraph|foreword|preface|introduction)$"), SectionRole.FrontMatter, "front matter"),
        (Rx(@"^(acknowledg(e)?ments?|about the authors?|afterword|author'?s note|appendix.*)$"), SectionRole.BackMatter, "back matter")
    };

    public static SectionClassification Classify(
        XDocument document,
        string? title,
        string? manifestProperties,
        string? guideType,
        string text)
    {
        if (manifestProperties?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav") == true)
        {
            return new(SectionRole.Skip, "navigation document");
        }

        foreach (var type in EpubTypes(document))
        {
            if (TypeRoles.TryGetValue(type, out var role))
            {
                return new(role, role == SectionRole.Skip ? $"epub:type '{type}'" : null);
            }
        }

        if (guideType is not null && TypeRoles.TryGetValue(guideType, out var guideRole) && guideRole == SectionRole.Skip)
        {
            return new(SectionRole.Skip, $"guide type '{guideType}'");
        }

        var normalizedTitle = Regex.Replace((title ?? string.Empty).Trim(), @"\s+", " ");
        foreach (var (pattern, role, reason) in TitleRules)
        {
            if (pattern.IsMatch(normalizedTitle))
            {
                return new(role, role == SectionRole.Skip ? reason : null);
            }
        }

        if (LooksLikeLinkList(document, text))
        {
            return new(SectionRole.Skip, "mostly links (table of contents)");
        }

        return new(SectionRole.Body, null);
    }

    private static IEnumerable<string> EpubTypes(XDocument document)
    {
        return document.Descendants()
            .Where(element => element.Name.LocalName is "body" or "section" or "nav" or "div")
            .Take(8)
            .SelectMany(element => element.Attributes().Where(attribute => attribute.Name.LocalName == "type"))
            .SelectMany(attribute => attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool LooksLikeLinkList(XDocument document, string text)
    {
        var links = document.Descendants().Where(element => element.Name.LocalName == "a" && element.Attribute("href") is not null).ToArray();
        if (links.Length < 5 || text.Length == 0)
        {
            return false;
        }

        var linkChars = links.Sum(link => link.Value.Trim().Length);
        return linkChars > text.Length * 0.4;
    }

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
