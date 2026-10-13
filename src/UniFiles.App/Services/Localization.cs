using System.Globalization;

namespace UniFiles.App.Services;

public static class Localization
{
    public static readonly string[] Supported = ["fr", "en"];

    /// <summary>Applies "fr", "en", or the system language when empty or unsupported (English fallback).</summary>
    public static void Apply(string language)
    {
        var code = string.IsNullOrEmpty(language) ? CultureInfo.InstalledUICulture.TwoLetterISOLanguageName : language;
        var culture = CultureInfo.GetCultureInfo(Supported.Contains(code) ? code : "en");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
