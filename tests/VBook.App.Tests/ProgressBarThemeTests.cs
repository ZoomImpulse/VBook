using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace VBook.App.Tests;

public sealed class ProgressBarThemeTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Default")]
    public void ProgressBar_UsesContrastingThemeBrushes(string theme)
    {
        using var stream = typeof(ProgressBarThemeTests).Assembly
            .GetManifestResourceStream("VBook.App.Tests.App.axaml");
        Assert.NotNull(stream);
        var document = XDocument.Load(stream);
        XNamespace ui = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = Assert.Single(document.Descendants(ui + "Style"),
            element => (string?)element.Attribute("Selector") == "ProgressBar");
        var setters = style.Elements(ui + "Setter").ToDictionary(
            element => (string)element.Attribute("Property")!,
            element => (string)element.Attribute("Value")!);

        Assert.Equal("{DynamicResource AppAccentBrush}", setters["Foreground"]);
        Assert.Equal("{DynamicResource AppStrokeBrush}", setters["Background"]);
        Assert.True(double.Parse(setters["MinHeight"], CultureInfo.InvariantCulture) >= 8);

        var dictionary = Assert.Single(document.Descendants(ui + "ResourceDictionary"),
            element => (string?)element.Attribute(x + "Key") == theme);
        var brushes = dictionary.Elements(ui + "SolidColorBrush").ToDictionary(
            element => (string)element.Attribute(x + "Key")!,
            element => (string)element.Attribute("Color")!);
        var indicator = Luminance(brushes["AppAccentBrush"]);
        var track = Luminance(brushes["AppStrokeBrush"]);
        var contrast = (Math.Max(indicator, track) + 0.05) / (Math.Min(indicator, track) + 0.05);

        Assert.True(contrast >= 3, $"{theme} progress indicator/track contrast was {contrast:F2}:1.");
    }

    private static double Luminance(string color)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", color);
        var channels = Enumerable.Range(0, 3).Select(index =>
        {
            var value = int.Parse(color.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }).ToArray();
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }
}
