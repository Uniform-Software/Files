namespace UniFiles.Core.Tools.Pdf.Fonts;

/// <summary>
/// Figtree (SIL Open Font License), the app's text font, shipped inside UniFiles.Core to be embedded
/// in PDFs: form values outside Helvetica's characters, signature appearances.
/// </summary>
public static class Figtree
{
    private static readonly Lazy<TrueTypeFont> RegularFont = new(() => Load("Figtree-Regular.ttf", "Figtree-Regular"));
    private static readonly Lazy<TrueTypeFont> SemiBoldFont = new(() => Load("Figtree-SemiBold.ttf", "Figtree-SemiBold"));

    public static TrueTypeFont Regular => RegularFont.Value;

    public static TrueTypeFont SemiBold => SemiBoldFont.Value;

    private static TrueTypeFont Load(string file, string name)
    {
        using var stream = typeof(Figtree).Assembly.GetManifestResourceStream("UniFiles.Core.Fonts." + file)
            ?? throw new InvalidOperationException($"Missing font resource {file}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new TrueTypeFont(memory.ToArray(), name);
    }
}
