#:package SkiaSharp@2.88.9
#:package SkiaSharp.NativeAssets.macOS@2.88.9

// Draws every UniFiles icon from vectors (the "u" and the wordmark are real Bricolage Grotesque
// ExtraBold glyphs), so each size is sharp. Run from the repository root after changing the design:
//   dotnet run build/icons/make-icons.cs
// Writes to src/UniFiles.App/Assets:
//   unifiles-icon.png / .svg / unifiles.ico   app icon (window, installer, Windows)
//   unifiles-macos.png                        app icon on Apple's 1024 grid (macOS .icns)
//   unifiles-tray-mac.png                     menu bar template image (white mark)
//   unifiles-folder.png / unifiles-folder.ico output folder icon (macOS, Windows)
using SkiaSharp;

var assets = Path.Combine("src", "UniFiles.App", "Assets");
var font = SKTypeface.FromFile(Path.Combine(assets, "Fonts", "BricolageGrotesque-ExtraBold.ttf"));
var lemon = SKColor.Parse("#D7FF3D");
var lemonDeep = SKColor.Parse("#AEE33D");
var ink = SKColor.Parse("#16130F");

// ---- App icon: designed on a 146 grid (the reference artwork) ------------------------------

// The "u", 59 wide, from x 43 to 102, sitting on y 123.5; the tilted square above it.
SKPath MarkPath()
{
    using var paint = new SKPaint { Typeface = font, TextSize = 100 };
    var u = paint.GetTextPath("u", 0, 0);
    var bounds = u.TightBounds;
    var scale = 59 / bounds.Width;
    u.Transform(SKMatrix.CreateTranslation(-bounds.Left, -bounds.Bottom).PostConcat(SKMatrix.CreateScale(scale, scale))
        .PostConcat(SKMatrix.CreateTranslation(43, 123.5f)));

    var dot = new SKPath();
    dot.AddRoundRect(new SKRect(-9.9f, -9.9f, 9.9f, 9.9f), 4.2f, 4.2f);
    dot.Transform(SKMatrix.CreateRotationDegrees(14).PostConcat(SKMatrix.CreateTranslation(72.5f, 35.8f)));
    u.AddPath(dot);
    return u;
}

// Full-bleed artwork: rounded lemon square, ink mark. `inset` leaves Apple's margin (100/1024 each side).
SKBitmap AppIcon(int size, bool appleGrid = false)
{
    var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    if (appleGrid)
    {
        canvas.Translate(size * 100f / 1024, size * 100f / 1024);
        canvas.Scale(824f / 1024);
    }
    canvas.Scale(size / 146f);
    using var tile = new SKPaint { Color = lemon, IsAntialias = true };
    canvas.DrawRoundRect(new SKRect(0, 0, 146, 146), 31, 31, tile);
    using var mark = new SKPaint { Color = ink, IsAntialias = true };
    canvas.DrawPath(MarkPath(), mark);
    return bitmap;
}

// Menu bar: the mark alone, white on transparent (macOS tints it), 18 pt @2x.
SKBitmap TrayIcon()
{
    var bitmap = new SKBitmap(36, 36, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    var path = MarkPath();
    var bounds = path.TightBounds;
    var scale = 34 / Math.Max(bounds.Width, bounds.Height);
    canvas.Translate(18, 18);
    canvas.Scale(scale);
    canvas.Translate(-bounds.MidX, -bounds.MidY);
    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
    canvas.DrawPath(path, white);
    return bitmap;
}

// ---- Folder icon: designed on a 246 x 183 grid ---------------------------------------------

SKPath TabPath()
{
    var tab = new SKPath();
    tab.MoveTo(0, 50);
    tab.LineTo(0, 10);
    tab.QuadTo(0, 0, 10, 0);
    tab.LineTo(86, 0);
    tab.CubicTo(95, 0, 99, 6, 103, 14);
    tab.CubicTo(106, 20, 108, 25, 116, 25);
    tab.LineTo(116, 50);
    tab.Close();
    return tab;
}

SKPath BodyPath()
{
    var body = new SKPath();
    body.MoveTo(0, 47);
    body.QuadTo(0, 37, 10, 37);
    body.LineTo(80, 37);
    body.CubicTo(90, 37, 94, 25, 106, 25);
    body.LineTo(235, 25);
    body.QuadTo(245, 25, 245, 35);
    body.LineTo(245, 170);
    body.QuadTo(245, 180, 235, 180);
    body.LineTo(10, 180);
    body.QuadTo(0, 180, 0, 170);
    body.Close();
    return body;
}

// "unifiles" in ink, tight tracking like the logo, from x 15 to 152, on baseline 167.
SKPath WordmarkPath()
{
    using var paint = new SKPaint { Typeface = font, TextSize = 100 };
    var path = new SKPath();
    var x = 0f;
    foreach (var letter in "unifiles")
    {
        var glyph = paint.GetTextPath(letter.ToString(), x, 0);
        path.AddPath(glyph);
        x += paint.MeasureText(letter.ToString()) - 3; // -0.03 em, as the in-app logo
    }
    var bounds = path.TightBounds;
    var scale = 137 / bounds.Width;
    path.Transform(SKMatrix.CreateTranslation(-bounds.Left, 0).PostConcat(SKMatrix.CreateScale(scale, scale))
        .PostConcat(SKMatrix.CreateTranslation(15, 167)));
    return path;
}

// Tab, body casting a soft shadow on the tab only, wordmark.
void DrawFolder(SKCanvas canvas)
{
    var tabPath = TabPath();
    var bodyPath = BodyPath();
    using var tab = new SKPaint { Color = lemonDeep, IsAntialias = true };
    using var body = new SKPaint { Color = lemon, IsAntialias = true };
    using var text = new SKPaint { Color = ink, IsAntialias = true };
    using var shadow = new SKPaint
    {
        Color = ink.WithAlpha(46),
        IsAntialias = true,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3),
    };
    canvas.DrawPath(tabPath, tab);
    canvas.Save();
    canvas.ClipPath(tabPath, antialias: true);
    canvas.Translate(0, -1.5f);
    canvas.DrawPath(bodyPath, shadow);
    canvas.Restore();
    canvas.DrawPath(bodyPath, body);
    canvas.DrawPath(WordmarkPath(), text);
}

// Square canvas (icons are square), the folder centered with a small margin.
SKBitmap FolderIcon(int size)
{
    var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    var scale = size * 0.94f / 246;
    canvas.Translate(size / 2f, size / 2f);
    canvas.Scale(scale);
    canvas.Translate(-123, -90.5f);
    DrawFolder(canvas);
    return bitmap;
}

// ---- Writers ---------------------------------------------------------------------------------

void Png(SKBitmap bitmap, string name)
{
    using (bitmap)
    using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        File.WriteAllBytes(Path.Combine(assets, name), data.ToArray());
    Console.WriteLine(name);
}

// ICO with PNG-compressed entries (Windows Vista and later), one per size.
void Ico(Func<int, SKBitmap> draw, string name)
{
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var images = sizes.Select(s =>
    {
        using var bitmap = draw(s);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }).ToList();

    using var stream = File.Create(Path.Combine(assets, name));
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        writer.Write(image);
    Console.WriteLine(name);
}

void Svg()
{
    var mark = MarkPath().ToSvgPathData();
    File.WriteAllText(Path.Combine(assets, "unifiles-icon.svg"), $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 146 146">
          <rect width="146" height="146" rx="31" fill="#D7FF3D"/>
          <path d="{mark}" fill="#16130F"/>
        </svg>

        """);
    Console.WriteLine("unifiles-icon.svg");
}

Png(AppIcon(1024), "unifiles-icon.png");
Png(AppIcon(1024, appleGrid: true), "unifiles-macos.png");
Png(TrayIcon(), "unifiles-tray-mac.png");
Png(FolderIcon(1024), "unifiles-folder.png");
Ico(s => AppIcon(s), "unifiles.ico");
Ico(FolderIcon, "unifiles-folder.ico");
Svg();

// Reference-size renders, to compare with the original artwork (not shipped).
if (args.FirstOrDefault() is { } previewFolder)
{
    Directory.CreateDirectory(previewFolder);
    using (var icon = AppIcon(146))
    using (var data = icon.Encode(SKEncodedImageFormat.Png, 100))
        File.WriteAllBytes(Path.Combine(previewFolder, "icon-146.png"), data.ToArray());
    var folder = new SKBitmap(246, 183);
    using (var canvas = new SKCanvas(folder))
    {
        canvas.Clear(SKColors.Transparent);
        DrawFolder(canvas);
    }
    using (folder)
    using (var data = folder.Encode(SKEncodedImageFormat.Png, 100))
        File.WriteAllBytes(Path.Combine(previewFolder, "folder-246.png"), data.ToArray());
}
