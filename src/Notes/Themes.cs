using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Notes;

public static class Themes
{
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Honey",    "#FF9F0A"),
        ("Ocean",    "#0A84FF"),
        ("Mint",     "#30D158"),
        ("Rose",     "#FF375F"),
        ("Lavender", "#BF5AF2"),
        ("Teal",     "#40C8E0"),
        ("Graphite", "#8E8E93"),
    };

    public static readonly string[] Fonts = { "Inter", "Segoe UI Variable", "Georgia", "Cascadia Code", "Consolas" };

    public static Color AccentColor(Settings s)
    {
        var hex = s.Accent == "Custom" ? s.CustomAccent : Accents.FirstOrDefault(a => a.Name == s.Accent).Hex;
        return TryParse(hex, out var c) ? c : Color.Parse(Accents[0].Hex);
    }

    public static bool TryParse(string? hex, out Color c)
    {
        c = default;
        return !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out c);
    }

    public static FontFamily FontFor(string name) => name == "Inter"
        ? new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter")
        : new FontFamily(name);

    public static void Apply(Settings s)
    {
        var app = Application.Current!;
        bool dark = s.Mode == "Dark";
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        var accent = AccentColor(s);
        var bg = TryParse(s.CustomBackground, out var cb) ? cb : Color.Parse(dark ? "#1C1C1E" : "#F9F9FB");
        var text = TryParse(s.CustomText, out var ct) ? ct : Color.Parse(dark ? "#F5F5F7" : "#1C1C1E");
        byte alpha = (byte)Math.Clamp(s.Opacity * 255, 90, 255);

        void Set(string key, Color c) => app.Resources[key] = new SolidColorBrush(c);

        Set("BgBrush", Color.FromArgb(alpha, bg.R, bg.G, bg.B));
        var panel = Mix(bg, text, 0.04);
        Set("PanelBrush", Color.FromArgb(alpha, panel.R, panel.G, panel.B));
        Set("FieldBrush", Mix(bg, text, 0.07));
        Set("HoverBrush", Color.FromArgb(40, text.R, text.G, text.B));
        Set("LineBrush", Color.FromArgb(30, text.R, text.G, text.B));
        Set("TextBrush", text);
        Set("SubtleBrush", Color.FromArgb(150, text.R, text.G, text.B));
        Set("AccentBrush", accent);
        Set("AccentSoftBrush", Color.FromArgb(48, accent.R, accent.G, accent.B));
        Set("OnAccentBrush", Luma(accent) > 0.7 ? Colors.Black : Colors.White);

        // Feed the accent into the Fluent theme so selection, carets and sliders match.
        app.Resources["SystemAccentColor"] = accent;
        app.Resources["SystemAccentColorLight1"] = accent;
        app.Resources["SystemAccentColorLight2"] = accent;
        app.Resources["SystemAccentColorDark1"] = accent;
        app.Resources["SystemAccentColorDark2"] = accent;
        app.Resources["AppFont"] = FontFor(s.Font);
        app.Resources["EditorFontSize"] = s.FontSize;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
}

/// The app logo, drawn as vectors: a warm rounded tile holding a white sheet with three lines.
public static class Logo
{
    public static Control Create(double size)
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        var tile = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(15),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#FFD60A"), 0), new GradientStop(Color.Parse("#FF9500"), 1) },
            },
        };
        var sheet = new Border
        {
            Width = 38, Height = 42, CornerRadius = new CornerRadius(7),
            Background = Brushes.White,
            BoxShadow = BoxShadows.Parse("0 2 4 0 #30000000"),
        };
        Canvas.SetLeft(sheet, 13); Canvas.SetTop(sheet, 11);
        canvas.Children.Add(tile);
        canvas.Children.Add(sheet);

        var lineBrush = new SolidColorBrush(Color.Parse("#FFB340"));
        double[] widths = { 24, 24, 15 };
        for (int i = 0; i < 3; i++)
        {
            var line = new Rectangle { Width = widths[i], Height = 4, RadiusX = 2, RadiusY = 2, Fill = lineBrush };
            Canvas.SetLeft(line, 20); Canvas.SetTop(line, 22 + i * 8);
            canvas.Children.Add(line);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    public static byte[] RenderPng(int px)
    {
        var control = Create(px);
        control.Measure(new Size(px, px));
        control.Arrange(new Rect(0, 0, px, px));
        using var bmp = new RenderTargetBitmap(new PixelSize(px, px));
        bmp.Render(control);
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    /// Writes a multi-size .ico (PNG-compressed entries) for the executable icon.
    public static void ExportIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = sizes.Select(RenderPng).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    public static WindowIcon CreateIcon()
    {
        const int px = 128;
        var control = Create(px);
        control.Measure(new Size(px, px));
        control.Arrange(new Rect(0, 0, px, px));
        var bmp = new RenderTargetBitmap(new PixelSize(px, px));
        bmp.Render(control);
        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
