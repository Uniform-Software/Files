using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;

namespace UniFiles.App.ViewModels;

public sealed record ThemeChoice(AppTheme Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record LanguageChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

public partial class PreferencesViewModel : PageViewModel
{
    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;
    private readonly string _startupLanguage;

    public PreferencesViewModel(MainWindowViewModel main, AppServices services)
    {
        _main = main;
        _services = services;
        var settings = services.Settings;
        _startupLanguage = settings.Language;

        _outputDirectory = settings.OutputDirectory;
        _selectedTheme = Themes.First(t => t.Value == settings.Theme);
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == settings.Language) ?? Languages[0];
        _reduceMotion = settings.ReduceMotion;
        _keepInTray = settings.KeepInTray;
        // The registry is the truth: the menu may have been removed by an uninstaller.
        _contextMenuEnabled = ExplorerMenu.Installer.IsSupported && ExplorerMenu.Installer.IsInstalled;
    }

    public IReadOnlyList<ThemeChoice> Themes { get; } =
    [
        new(AppTheme.Dark, Strings.Prefs_ThemeDark),
        new(AppTheme.Light, Strings.Prefs_ThemeLight),
    ];

    public IReadOnlyList<LanguageChoice> Languages { get; } =
    [
        new("", Strings.Prefs_LanguageSystem),
        new("fr", "Français"),
        new("en", "English"),
    ];

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    private LanguageChoice _selectedLanguage;

    [ObservableProperty]
    private bool _reduceMotion;

    [ObservableProperty]
    private bool _keepInTray;

    [ObservableProperty]
    private bool _contextMenuEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextMenuError))]
    private string? _contextMenuError;

    public bool IsContextMenuSupported => ExplorerMenu.Installer.IsSupported;

    public bool HasContextMenuError => ContextMenuError is not null;

    public bool NeedsRestart => SelectedLanguage.Value != _startupLanguage;

    public string Version { get; } = string.Format(Strings.Prefs_Version,
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0");

    partial void OnOutputDirectoryChanged(string value)
    {
        _services.Settings.OutputDirectory = value;
        _services.SaveSettings();
    }

    partial void OnSelectedThemeChanged(ThemeChoice value)
    {
        _services.Settings.Theme = value.Value;
        _services.SaveSettings();
        App.ApplyTheme(value.Value);
    }

    partial void OnSelectedLanguageChanged(LanguageChoice value)
    {
        _services.Settings.Language = value.Value;
        _services.SaveSettings();
    }

    partial void OnReduceMotionChanged(bool value)
    {
        _services.Settings.ReduceMotion = value;
        _services.SaveSettings();
        _main.ReducedMotion = value || Motion.SystemPrefersReducedMotion();
    }

    partial void OnKeepInTrayChanged(bool value)
    {
        _services.Settings.KeepInTray = value;
        _services.SaveSettings();
    }

    partial void OnContextMenuEnabledChanged(bool value)
    {
        ContextMenuError = null;
        try
        {
            if (value && Environment.ProcessPath is { } exe)
                ExplorerMenu.Installer.Install(exe, _services.Catalog);
            else
                ExplorerMenu.Installer.Uninstall();
            _services.Settings.ContextMenuEnabled = value;
            _services.SaveSettings();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            ContextMenuError = string.Format(Strings.Prefs_ContextMenuError, ex.Message);
        }
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        if (await DialogService.PickFolderAsync(Strings.Prefs_OutputFolder) is { } folder)
            OutputDirectory = folder;
    }

    [RelayCommand]
    private void ResetOutput() => OutputDirectory = AppSettings.DefaultOutputDirectory;

    [RelayCommand]
    private Task OpenOutput() => DialogService.OpenFolderAsync(OutputDirectory);
}
