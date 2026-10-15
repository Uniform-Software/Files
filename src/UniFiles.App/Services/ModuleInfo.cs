using Avalonia;
using Avalonia.Media;
using UniFiles.Core;
using UniFiles.Core.Resources;

namespace UniFiles.App.Services;

/// <summary>Display data of a module: name, color and icon, read from the theme resources.</summary>
public sealed class ModuleInfo
{
    private ModuleInfo(ToolModule module, string name, string summary, string resourcePrefix, string iconKey)
    {
        Module = module;
        Name = name;
        Summary = summary;
        Brush = Resource<IBrush>(resourcePrefix + "Brush") ?? Brushes.Gray;
        Icon = Resource<Geometry>(iconKey);
    }

    public ToolModule Module { get; }

    public string Name { get; }

    public string Summary { get; }

    public IBrush Brush { get; }

    public Geometry? Icon { get; }

    public static IReadOnlyList<ModuleInfo> All { get; } =
    [
        new(ToolModule.Images, Strings.Module_Images, Strings.Module_Images_Summary, "Images", "IconImage"),
        new(ToolModule.Rename, Strings.Module_Rename, Strings.Module_Rename_Summary, "Rename", "IconRename"),
        new(ToolModule.Pdf, Strings.Module_Pdf, Strings.Module_Pdf_Summary, "Pdf", "IconPdf"),
        new(ToolModule.Video, Strings.Module_Video, Strings.Module_Video_Summary, "Video", "IconVideo"),
        new(ToolModule.Files, Strings.Module_Files, Strings.Module_Files_Summary, "Files", "IconArchive"),
    ];

    public static ModuleInfo For(ToolModule module) => All.First(m => m.Module == module);

    public static T? Resource<T>(string key) where T : class =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as T : null;
}
