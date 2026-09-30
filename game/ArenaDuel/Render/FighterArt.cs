using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ArenaDuel.Data;

namespace ArenaDuel.Render;

/// <summary>Procedural arcade-fighter bust &amp; body art (original cast only).</summary>
public static class FighterArt
{
    private static readonly Dictionary<string, ImageSource> PortraitCache = new();

    public static ImageSource GetPortrait(CharacterDef def, int size = 128)
    {
        var key = "v2:" + def.Id + ":" + size;
        if (PortraitCache.TryGetValue(key, out var cached)) return cached;
        var bmp = RenderPortraitBitmap(def, size);
        PortraitCache[key] = bmp;
        return bmp;
    }

    public static void DrawFighter(Canvas canvas, CharacterDef def, double cx, double cy, double scale,
        bool faceRight, bool casting, bool hurt, double bob)
    {
        var accent = Parse(def.Color);
        var skin = Color.FromRgb(0xF2, 0xD0, 0xB0);
        var shade = Darken(accent, 0.45);
        var glow = casting ? Color.FromArgb(160, 255, 240, 120) : Colors.Transparent;
        var dir = faceRight ? 1.0 : -1.0;
        var unit = 18 * scale;

        // ground shadow
        AddOval(canvas, cx - unit * 0.9, cy + unit * 1.55, unit * 1.8, unit * 0.45, Color.FromArgb(90, 0, 0, 0));

        if (hurt)
            AddOval(canvas, cx - unit * 1.2, cy - unit * 1.8, unit * 2.4, unit * 3.6, Color.FromArgb(70, 255, 40, 40));

        if (casting)
            AddOval(canvas, cx - unit * 1.4, cy - unit * 0.2, unit * 2.8, unit * 2.8, glow);

        // legs
        var legY = cy + unit * 0.55 + bob * 0.3;
        AddLimb(canvas, cx - unit * 0.28 * dir, legY, unit * 0.28, unit * 1.05, shade, 12 * scale);
        AddLimb(canvas, cx + unit * 0.32 * dir, legY, unit * 0.28, unit * 1.05, Darken(shade, 0.15), 12 * scale);

        // torso cloak / jacket
        var torso = new Polygon
        {
            Fill = new SolidColorBrush(accent),
            Points = new PointCollection
            {
                new(cx - unit * 0.55 * dir, cy - unit * 0.15 + bob),
                new(cx + unit * 0.7 * dir, cy - unit * 0.25 + bob),
                new(cx + unit * 0.55 * dir, cy + unit * 0.95 + bob),
                new(cx - unit * 0.45 * dir, cy + unit * 1.05 + bob)
            },
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.2 * scale
        };
        canvas.Children.Add(torso);

        // sash
        var sash = new Rectangle
        {
            Width = unit * 1.1,
            Height = unit * 0.18,
            Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x42))
        };
        Canvas.SetLeft(sash, cx - unit * 0.55);
        Canvas.SetTop(sash, cy + unit * 0.35 + bob);
        canvas.Children.Add(sash);

        // arms
        var armY = cy + unit * 0.05 + bob;
        AddLimb(canvas, cx - unit * 0.75 * dir, armY, unit * 0.22, unit * 0.85, skin, 10 * scale);
        AddLimb(canvas, cx + unit * 0.85 * dir, armY - (casting ? unit * 0.35 : 0), unit * 0.22, unit * 0.85, skin, 10 * scale);

        // weapon by archetype
        DrawWeapon(canvas, def.Id, cx, cy + bob, unit, dir, accent, casting);

        // head
        var headY = cy - unit * 0.85 + bob;
        AddOval(canvas, cx - unit * 0.42, headY - unit * 0.42, unit * 0.84, unit * 0.84, skin);
        DrawHair(canvas, def.Id, cx, headY, unit, dir, accent, shade);

        // eyes
        var eyeColor = Color.FromRgb(0x1A, 0x1A, 0x22);
        AddOval(canvas, cx - unit * 0.18 * dir - unit * 0.08, headY - unit * 0.05, unit * 0.14, unit * 0.16, eyeColor);
        AddOval(canvas, cx + unit * 0.12 * dir - unit * 0.08, headY - unit * 0.05, unit * 0.14, unit * 0.16, eyeColor);

        // name plate
        var name = new TextBlock
        {
            Text = def.Name,
            Foreground = Brushes.White,
            FontSize = Math.Max(10, 11 * scale),
            FontWeight = FontWeights.Bold,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9
            }
        };
        Canvas.SetLeft(name, cx - 18 * scale);
        Canvas.SetTop(name, cy - unit * 2.15 + bob);
        canvas.Children.Add(name);
    }

    public static UIElement BuildSelectCard(CharacterDef def, bool selected, Action onClick)
    {
        var root = new Border
        {
            Width = 156,
            Height = 236,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(0xC4, 0x1E, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Cursor = Cursors.Hand,
            Tag = def.Id
        };
        root.MouseLeftButtonDown += (_, _) => onClick();

        var stack = new StackPanel { Margin = new Thickness(8) };
        var portraitFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = GetPortrait(def, 140),
                Width = 132,
                Height = 132,
                Stretch = Stretch.Uniform
            }
        };
        stack.Children.Add(portraitFrame);
        stack.Children.Add(new TextBlock
        {
            Text = def.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xE8, 0xE0)),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("KaiTi, STKaiti, Microsoft YaHei UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 2)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{def.Role}  HP {def.MaxHp:0}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x86, 0x80)),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{def.Skill1.Name} / {def.Ultimate.Name}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x66, 0x60)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });
        root.Child = stack;
        return root;
    }

    private static ImageSource RenderPortraitBitmap(CharacterDef def, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var accent = Parse(def.Color);
            var skin = Color.FromRgb(0xF2, 0xD0, 0xB0);
            var bg = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
            dc.DrawRectangle(bg, null, new Rect(0, 0, size, size));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 2),
                new Rect(2, 2, size - 4, size - 4));
            // left accent bar instead of gold ring
            dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, 0, size * 0.06, size));

            var cx = size * 0.5;
            var cy = size * 0.55;
            var u = size * 0.28;

            // shoulders
            dc.DrawEllipse(new SolidColorBrush(accent), null, new Point(cx, cy + u * 0.9), u * 1.15, u * 0.7);
            // head
            dc.DrawEllipse(new SolidColorBrush(skin), null, new Point(cx, cy - u * 0.15), u * 0.72, u * 0.72);
            DrawHairDc(dc, def.Id, cx, cy - u * 0.15, u, accent);
            // eyes
            dc.DrawEllipse(Brushes.Black, null, new Point(cx - u * 0.22, cy - u * 0.1), u * 0.1, u * 0.12);
            dc.DrawEllipse(Brushes.Black, null, new Point(cx + u * 0.22, cy - u * 0.1), u * 0.1, u * 0.12);
            dc.DrawEllipse(Brushes.White, null, new Point(cx - u * 0.18, cy - u * 0.14), u * 0.035, u * 0.035);
            dc.DrawEllipse(Brushes.White, null, new Point(cx + u * 0.26, cy - u * 0.14), u * 0.035, u * 0.035);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawHairDc(DrawingContext dc, string id, double cx, double cy, double u, Color accent)
    {
        var brush = new SolidColorBrush(accent);
        var dark = new SolidColorBrush(Darken(accent, 0.35));
        switch (id)
        {
            case "akishun":
                dc.DrawEllipse(brush, null, new Point(cx, cy - u * 0.35), u * 0.85, u * 0.55);
                dc.DrawRectangle(brush, null, new Rect(cx + u * 0.35, cy - u * 0.2, u * 0.55, u * 1.1));
                break;
            case "tiezhang":
                dc.DrawEllipse(dark, null, new Point(cx, cy - u * 0.25), u * 0.8, u * 0.45);
                break;
            case "qinglan":
                for (var i = -2; i <= 2; i++)
                    dc.DrawEllipse(brush, null, new Point(cx + i * u * 0.22, cy - u * 0.5), u * 0.2, u * 0.55);
                break;
            case "shazhi":
                dc.DrawEllipse(brush, null, new Point(cx, cy - u * 0.2), u * 0.9, u * 0.5);
                dc.DrawRectangle(brush, null, new Rect(cx - u * 0.9, cy, u * 0.35, u * 1.2));
                dc.DrawRectangle(brush, null, new Rect(cx + u * 0.55, cy, u * 0.35, u * 1.2));
                break;
            case "baizang":
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)), null,
                    new Point(cx, cy - u * 0.4), u * 0.9, u * 0.6);
                break;
            case "yanya":
                dc.DrawEllipse(brush, null, new Point(cx, cy - u * 0.35), u * 0.8, u * 0.5);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x20)), null,
                    new Point(cx + u * 0.5, cy - u * 0.1), u * 0.25, u * 0.7);
                break;
            case "wuyin":
                dc.DrawEllipse(dark, null, new Point(cx, cy - u * 0.3), u * 1.0, u * 0.55);
                dc.DrawRectangle(dark, null, new Rect(cx - u * 0.2, cy - u * 0.9, u * 0.4, u * 0.5));
                break;
            default:
                dc.DrawEllipse(brush, null, new Point(cx, cy - u * 0.35), u * 0.85, u * 0.5);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0x60)), null,
                    new Point(cx - u * 0.55, cy), u * 0.2, u * 0.55);
                break;
        }
    }

    private static void DrawHair(Canvas canvas, string id, double cx, double headY, double unit, double dir, Color accent, Color shade)
    {
        switch (id)
        {
            case "akishun":
                AddOval(canvas, cx - unit * 0.55, headY - unit * 0.75, unit * 1.1, unit * 0.7, accent);
                AddOval(canvas, cx + dir * unit * 0.25, headY - unit * 0.2, unit * 0.45, unit * 1.0, accent);
                break;
            case "shazhi":
                AddOval(canvas, cx - unit * 0.6, headY - unit * 0.55, unit * 1.2, unit * 0.6, accent);
                AddOval(canvas, cx - unit * 0.95, headY, unit * 0.35, unit * 1.1, accent);
                AddOval(canvas, cx + unit * 0.6, headY, unit * 0.35, unit * 1.1, accent);
                break;
            case "baizang":
                AddOval(canvas, cx - unit * 0.55, headY - unit * 0.8, unit * 1.1, unit * 0.75, Color.FromRgb(0xEE, 0xEE, 0xF2));
                break;
            case "wuyin":
                AddOval(canvas, cx - unit * 0.6, headY - unit * 0.7, unit * 1.2, unit * 0.65, shade);
                break;
            default:
                AddOval(canvas, cx - unit * 0.5, headY - unit * 0.7, unit * 1.0, unit * 0.65, accent);
                break;
        }
    }

    private static void DrawWeapon(Canvas canvas, string id, double cx, double cy, double unit, double dir, Color accent, bool casting)
    {
        var tip = casting ? Color.FromRgb(0xFF, 0xF0, 0x80) : accent;
        switch (id)
        {
            case "akishun":
            case "leichan":
            case "baizang":
                var blade = new Rectangle
                {
                    Width = unit * 0.14,
                    Height = unit * 1.6,
                    Fill = new LinearGradientBrush(Colors.WhiteSmoke, tip, 90),
                    RenderTransformOrigin = new Point(0.5, 0),
                    RenderTransform = new RotateTransform(dir > 0 ? -25 : 25)
                };
                Canvas.SetLeft(blade, cx + dir * unit * 0.85);
                Canvas.SetTop(blade, cy - unit * 0.9);
                canvas.Children.Add(blade);
                break;
            case "qinglan":
            case "wuyin":
                AddOval(canvas, cx + dir * unit * 0.7, cy - unit * 0.3, unit * 0.55, unit * 0.55,
                    Color.FromArgb(casting ? (byte)200 : (byte)120, tip.R, tip.G, tip.B));
                break;
            case "shazhi":
                for (var i = 0; i < 3; i++)
                    AddOval(canvas, cx + dir * (unit * 0.8 + i * unit * 0.15), cy - unit * 0.2 + i * 4,
                        unit * 0.12, unit * 0.35, tip);
                break;
            default:
                AddOval(canvas, cx + dir * unit * 0.85, cy + unit * 0.1, unit * 0.35, unit * 0.35, tip);
                break;
        }
    }

    private static void AddOval(Canvas canvas, double x, double y, double w, double h, Color c)
    {
        var el = new Ellipse { Width = w, Height = h, Fill = new SolidColorBrush(c) };
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        canvas.Children.Add(el);
    }

    private static void AddLimb(Canvas canvas, double x, double y, double w, double h, Color c, double radius)
    {
        var b = new Border
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(c),
            CornerRadius = new CornerRadius(radius)
        };
        Canvas.SetLeft(b, x - w / 2);
        Canvas.SetTop(b, y);
        canvas.Children.Add(b);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}
