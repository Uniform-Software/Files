using NetVips;

namespace UniFiles.Core.Tools.Images;

/// <summary>
/// libvips keeps recent operations, and the files they read, in a cache. Under Windows an open
/// file can be neither deleted nor renamed: the user's originals stayed locked after processing,
/// and a chain's temporary folder could not be removed. UniFiles never reuses an operation, so the
/// cache is turned off before the first file is opened.
/// </summary>
public static class VipsSetup
{
    private static readonly Lazy<bool> Done = new(() =>
    {
        Cache.Max = 0;
        Cache.MaxFiles = 0;
        return true;
    });

    public static void Ensure() => _ = Done.Value;
}
