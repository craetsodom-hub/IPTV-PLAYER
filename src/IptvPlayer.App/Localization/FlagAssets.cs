using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IptvPlayer.App.Localization;

public sealed class FlagCodeToImageSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => FlagAssets.Get(value as string ?? "GB");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

internal static class FlagAssets
{
    private const double Width = 30d;
    private const double Height = 20d;
    private static readonly IReadOnlyDictionary<string, ImageSource> Images = BuildImages();

    public static ImageSource Get(string code)
        => Images.TryGetValue(code, out var image) ? image : Images["GB"];

    private static IReadOnlyDictionary<string, ImageSource> BuildImages()
        => new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["GB"] = UnitedKingdom(),
            ["ES"] = Horizontal("#AA151B", "#F1BF00", "#AA151B", 1d, 2d, 1d),
            ["FR"] = Vertical("#0055A4", "#FFFFFF", "#EF4135"),
            ["DE"] = Horizontal("#000000", "#DD0000", "#FFCE00"),
            ["IT"] = Vertical("#009246", "#FFFFFF", "#CE2B37"),
            ["PT"] = Portugal(),
            ["NL"] = Horizontal("#AE1C28", "#FFFFFF", "#21468B"),
            ["PL"] = Horizontal("#FFFFFF", "#DC143C"),
            ["RO"] = Vertical("#002B7F", "#FCD116", "#CE1126"),
            ["CZ"] = Czechia(),
            ["HU"] = Horizontal("#CE2939", "#FFFFFF", "#477050"),
            ["GR"] = Greece(),
            ["TR"] = Turkey(),
            ["UA"] = Horizontal("#0057B7", "#FFD700"),
            ["RU"] = Horizontal("#FFFFFF", "#0039A6", "#D52B1E"),
            ["SE"] = Nordic("#006AA7", "#FECC00", 10d),
            ["DK"] = Nordic("#C8102E", "#FFFFFF", 10d),
            ["NO"] = Norway(),
            ["FI"] = Nordic("#FFFFFF", "#003580", 11d),
            ["SA"] = SaudiArabia(),
            ["CN"] = China(),
            ["TW"] = Taiwan(),
            ["JP"] = Japan(),
            ["KR"] = SouthKorea(),
            ["IN"] = India(),
            ["ID"] = Horizontal("#FF0000", "#FFFFFF"),
        };

    private static ImageSource UnitedKingdom()
    {
        var group = Group("#012169");
        group.Children.Add(Line("#FFFFFF", 5.2, 0, 0, Width, Height));
        group.Children.Add(Line("#FFFFFF", 5.2, Width, 0, 0, Height));
        group.Children.Add(Line("#C8102E", 2.2, 0, 0, Width, Height));
        group.Children.Add(Line("#C8102E", 2.2, Width, 0, 0, Height));
        group.Children.Add(Rect("#FFFFFF", 0, 7, Width, 6));
        group.Children.Add(Rect("#FFFFFF", 12, 0, 6, Height));
        group.Children.Add(Rect("#C8102E", 0, 8.5, Width, 3));
        group.Children.Add(Rect("#C8102E", 13.5, 0, 3, Height));
        return Image(group);
    }

    private static ImageSource Portugal()
    {
        var group = Group("#FF0000");
        group.Children.Add(Rect("#046A38", 0, 0, 12, Height));
        group.Children.Add(new GeometryDrawing(
            Brush("#FFCC00"),
            new Pen(Brush("#8B1A1A"), 0.6),
            new EllipseGeometry(new Point(12, 10), 3.2, 3.2)));
        group.Children.Add(Rect("#FFFFFF", 10.4, 7.3, 3.2, 4.4));
        group.Children.Add(Rect("#C8102E", 11.2, 8.1, 1.6, 2.8));
        return Image(group);
    }

    private static ImageSource Czechia()
    {
        var group = Group("#D7141A");
        group.Children.Add(Rect("#FFFFFF", 0, 0, Width, 10));
        group.Children.Add(Path("#11457E", "M0,0 L13,10 L0,20 Z"));
        return Image(group);
    }

    private static ImageSource Greece()
    {
        var group = Group("#0D5EAF");
        for (var row = 1; row < 9; row += 2)
        {
            group.Children.Add(Rect("#FFFFFF", 0, row * Height / 9d, Width, Height / 9d));
        }

        group.Children.Add(Rect("#0D5EAF", 0, 0, 12, 11.2));
        group.Children.Add(Rect("#FFFFFF", 4.8, 0, 2.4, 11.2));
        group.Children.Add(Rect("#FFFFFF", 0, 4.4, 12, 2.4));
        return Image(group);
    }

    private static ImageSource Turkey()
    {
        var group = Group("#E30A17");
        group.Children.Add(Ellipse("#FFFFFF", 7, 4, 12, 12));
        group.Children.Add(Ellipse("#E30A17", 10, 5.5, 9, 9));
        group.Children.Add(Path("#FFFFFF", Star(20.5, 10, 3.1, 1.25, -90)));
        return Image(group);
    }

    private static ImageSource Nordic(string background, string cross, double crossCenter)
    {
        var group = Group(background);
        group.Children.Add(Rect(cross, crossCenter - 1.4, 0, 2.8, Height));
        group.Children.Add(Rect(cross, 0, 8.6, Width, 2.8));
        return Image(group);
    }

    private static ImageSource Norway()
    {
        var group = Group("#BA0C2F");
        group.Children.Add(Rect("#FFFFFF", 8.3, 0, 5.2, Height));
        group.Children.Add(Rect("#FFFFFF", 0, 7.4, Width, 5.2));
        group.Children.Add(Rect("#00205B", 9.7, 0, 2.4, Height));
        group.Children.Add(Rect("#00205B", 0, 8.8, Width, 2.4));
        return Image(group);
    }

    private static ImageSource SaudiArabia()
    {
        var group = Group("#006C35");
        group.Children.Add(Path("#FFFFFF", "M6,7 C9,5 12,8 15,6 C18,5 20,7 24,6 M8,9 C11,8 14,10 17,8 C20,7 22,9 24,8"));
        group.Children.Add(Line("#FFFFFF", 1.2, 7, 14.3, 23, 14.3));
        group.Children.Add(Line("#FFFFFF", 0.8, 21, 14.3, 23.5, 13.2));
        return Image(group);
    }

    private static ImageSource China()
    {
        var group = Group("#DE2910");
        group.Children.Add(Path("#FFDE00", Star(6.2, 6.1, 3.2, 1.3, -90)));
        group.Children.Add(Path("#FFDE00", Star(11.8, 2.9, 1.1, 0.45, -55)));
        group.Children.Add(Path("#FFDE00", Star(14, 5.4, 1.1, 0.45, -25)));
        group.Children.Add(Path("#FFDE00", Star(14, 8.6, 1.1, 0.45, 5)));
        group.Children.Add(Path("#FFDE00", Star(11.8, 11.1, 1.1, 0.45, 35)));
        return Image(group);
    }

    private static ImageSource Taiwan()
    {
        var group = Group("#FE0000");
        group.Children.Add(Rect("#000095", 0, 0, 15, 10.8));
        group.Children.Add(Path("#FFFFFF", Star(7.5, 5.4, 4.3, 2.8, -90, points: 12)));
        group.Children.Add(Ellipse("#FFFFFF", 5.2, 3.1, 4.6, 4.6));
        return Image(group);
    }

    private static ImageSource Japan()
    {
        var group = Group("#FFFFFF");
        group.Children.Add(Ellipse("#BC002D", 10, 5, 10, 10));
        return Image(group);
    }

    private static ImageSource SouthKorea()
    {
        var group = Group("#FFFFFF");
        group.Children.Add(Path("#CD2E3A", "M10,10 A5,5 0 0 1 20,10 A2.5,2.5 0 0 0 15,10 A2.5,2.5 0 0 1 10,10 Z"));
        group.Children.Add(Path("#0047A0", "M10,10 A5,5 0 0 0 20,10 A2.5,2.5 0 0 1 15,10 A2.5,2.5 0 0 0 10,10 Z"));
        group.Children.Add(Line("#000000", 1.1, 4, 4, 9, 2));
        group.Children.Add(Line("#000000", 1.1, 5, 7, 10, 5));
        group.Children.Add(Line("#000000", 1.1, 20, 15, 25, 13));
        group.Children.Add(Line("#000000", 1.1, 21, 18, 26, 16));
        return Image(group);
    }

    private static ImageSource India()
    {
        var group = Group("#138808");
        group.Children.Add(Rect("#FF9933", 0, 0, Width, Height / 3d));
        group.Children.Add(Rect("#FFFFFF", 0, Height / 3d, Width, Height / 3d));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#000080"), 0.8), new EllipseGeometry(new Point(15, 10), 2.7, 2.7)));
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.PI / 6d;
            group.Children.Add(Line("#000080", 0.35, 15, 10, 15 + Math.Cos(angle) * 2.5, 10 + Math.Sin(angle) * 2.5));
        }
        return Image(group);
    }

    private static ImageSource Horizontal(params string[] colors)
        => Horizontal(colors, Enumerable.Repeat(1d, colors.Length).ToArray());

    private static ImageSource Horizontal(string first, string second, string third, double firstWeight, double secondWeight, double thirdWeight)
        => Horizontal([first, second, third], [firstWeight, secondWeight, thirdWeight]);

    private static ImageSource Horizontal(string[] colors, double[] weights)
    {
        var group = Group(colors[^1]);
        var total = weights.Sum();
        var y = 0d;
        for (var i = 0; i < colors.Length; i++)
        {
            var height = Height * weights[i] / total;
            group.Children.Add(Rect(colors[i], 0, y, Width, height));
            y += height;
        }
        return Image(group);
    }

    private static ImageSource Vertical(params string[] colors)
    {
        var group = Group(colors[^1]);
        var width = Width / colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            group.Children.Add(Rect(colors[i], i * width, 0, width, Height));
        }
        return Image(group);
    }

    private static DrawingGroup Group(string background)
    {
        var group = new DrawingGroup
        {
            ClipGeometry = new RectangleGeometry(new Rect(0, 0, Width, Height), 1.2, 1.2),
        };
        group.Children.Add(Rect(background, 0, 0, Width, Height));
        return group;
    }

    private static GeometryDrawing Rect(string color, double x, double y, double width, double height)
        => new(Brush(color), null, new RectangleGeometry(new Rect(x, y, width, height)));

    private static GeometryDrawing Ellipse(string color, double x, double y, double width, double height)
        => new(Brush(color), null, new EllipseGeometry(new Rect(x, y, width, height)));

    private static GeometryDrawing Line(string color, double thickness, double x1, double y1, double x2, double y2)
        => new(null, new Pen(Brush(color), thickness), new LineGeometry(new Point(x1, y1), new Point(x2, y2)));

    private static GeometryDrawing Path(string color, string data)
        => new(Brush(color), null, Geometry.Parse(data));

    private static string Star(double centerX, double centerY, double outerRadius, double innerRadius, double rotationDegrees, int points = 5)
    {
        var values = new List<string>(points * 2);
        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            var angle = (rotationDegrees + (i * 180d / points)) * Math.PI / 180d;
            values.Add(FormattableString.Invariant($"{centerX + Math.Cos(angle) * radius:0.###},{centerY + Math.Sin(angle) * radius:0.###}"));
        }
        return $"M{string.Join(" L", values)} Z";
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static ImageSource Image(DrawingGroup group)
    {
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
