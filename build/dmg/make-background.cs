#:package SkiaSharp@2.88.9
#:package SkiaSharp.NativeAssets.macOS@2.88.9

// Draws the background of the macOS disk image (build/dmg/background.png and background@2x.png).
// Run from the repository root after changing the layout: dotnet run build/dmg/make-background.cs
// The icon positions here must match build/dmg/settings.py.
using SkiaSharp;

const float Width = 640, Height = 420;
var fonts = Path.Combine("src", "UniFiles.App", "Assets", "Fonts");
var title = SKTypeface.FromFile(Path.Combine(fonts, "BricolageGrotesque-Bold.ttf"));
var text = SKTypeface.FromFile(Path.Combine(fonts, "Figtree-Medium.ttf"));

// Brand tokens (src/UniFiles.App/Themes/Tokens.axaml).
var ink = SKColor.Parse("#16130F");
var surface = SKColor.Parse("#221E19");
var border = SKColor.Parse("#3A342C");
var cream = SKColor.Parse("#F6F1E7");
var secondary = SKColor.Parse("#B8AE9F");
var lemon = SKColor.Parse("#D7FF3D");
SKColor[] modules = [SKColor.Parse("#FF6A42"), SKColor.Parse("#6E8BFF"), SKColor.Parse("#B08CFF"), SKColor.Parse("#3FD8C8")];
// Finder draws icon names black in light mode and white in dark mode: a mid tone behind them keeps
// both readable (contrast 4.5:1 or more with either).
var label = SKColor.Parse("#7A746B");

foreach (var scale in new[] { 1, 2 })
{
    using var bitmap = new SKBitmap((int)(Width * scale), (int)(Height * scale));
    using var canvas = new SKCanvas(bitmap);
    canvas.Scale(scale);
    canvas.Clear(ink);

    // Mosaic in the corners, in the module colors, on the same 14° grid as the logo.
    Mosaic(canvas, 44, 40, 1, [(0, 0, 18, 0.9f, 0), (22, -4, 9, 0.5f, 1), (34, 6, 6, 0.35f, 2), (-8, 22, 9, 0.4f, 3), (16, 24, 6, 0.25f, 1)]);
    Mosaic(canvas, Width - 60, 34, 1, [(0, 0, 9, 0.45f, 2), (13, 8, 14, 0.7f, 3), (-12, 14, 6, 0.3f, 0), (4, 26, 6, 0.25f, 1)]);

    Centered(canvas, "Glisse UniFiles dans Applications", title, 25, cream, 70);
    Centered(canvas, "Drag UniFiles to your Applications folder", text, 13.5f, secondary, 96);

    // Panel holding the two icons (centered at x 190 and 450, y 228), a pill behind each name.
    using (var panel = new SKPaint { Color = surface, IsAntialias = true })
    using (var panelBorder = new SKPaint { Color = border, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
    {
        var rect = new SKRect(48, 126, Width - 48, 346);
        canvas.DrawRoundRect(rect, 28, 28, panel);
        canvas.DrawRoundRect(rect, 28, 28, panelBorder);
    }
    using (var pill = new SKPaint { Color = label, IsAntialias = true })
    {
        foreach (var x in new[] { 190f, 450f })
            canvas.DrawRoundRect(new SKRect(x - 62, 283, x + 62, 311), 14, 14, pill);
    }

    // Arrow from the app (x 190) to Applications (x 450), icons centered at y 228.
    using (var arrow = new SKPaint { Color = lemon, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
    {
        canvas.DrawLine(272, 228, 362, 228, arrow);
        using var head = new SKPath();
        head.MoveTo(346, 212);
        head.LineTo(364, 228);
        head.LineTo(346, 244);
        canvas.DrawPath(head, arrow);
    }

    // Footer: what to do next, and the unsigned-build tip.
    const string tip = "Bloquée au premier lancement ?  Réglages › Confidentialité et sécurité › Ouvrir quand même";
    using (var measure = new SKPaint { Typeface = text, TextSize = 11 })
    using (var chip = new SKPaint { Color = lemon, IsAntialias = true })
    {
        var half = measure.MeasureText(tip) / 2 + 16;
        canvas.DrawRoundRect(new SKRect(Width / 2 - half, 366, Width / 2 + half, 394), 14, 14, chip);
    }
    Centered(canvas, tip, text, 11, ink, 384);

    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    var name = scale == 1 ? "background.png" : $"background@{scale}x.png";
    File.WriteAllBytes(Path.Combine("build", "dmg", name), data.ToArray());
    Console.WriteLine(name);
}

void Centered(SKCanvas canvas, string value, SKTypeface typeface, float size, SKColor color, float baseline)
{
    using var paint = new SKPaint { Typeface = typeface, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };
    canvas.DrawText(value, (Width - paint.MeasureText(value)) / 2, baseline, paint);
}

void Mosaic(SKCanvas canvas, float x, float y, float unit, (float X, float Y, float Side, float Opacity, int Color)[] tiles)
{
    canvas.Save();
    canvas.Translate(x, y);
    canvas.RotateDegrees(14);
    foreach (var tile in tiles)
    {
        var side = tile.Side * unit;
        using var paint = new SKPaint { Color = modules[tile.Color].WithAlpha((byte)(255 * tile.Opacity)), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(tile.X - side / 2, tile.Y - side / 2, tile.X + side / 2, tile.Y + side / 2), side * 0.26f, side * 0.26f, paint);
    }
    canvas.Restore();
}
