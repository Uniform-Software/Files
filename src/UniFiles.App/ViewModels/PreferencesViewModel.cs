using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools.Video;

namespace UniFiles.App.ViewModels;

public sealed record ThemeChoice(AppTheme Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record LanguageChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The parts of the Preferences, one per sub-entry of the sidebar.</summary>
public enum PreferencesSection
{
    General,
    Appearance,
    System,
    Updates,
    About,
}

public partial class PreferencesViewModel : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsAppearance), nameof(IsSystem), nameof(IsUpdates), nameof(IsAbout), nameof(SectionTitle))]
    private PreferencesSection _section;

    public bool IsGeneral => Section == PreferencesSection.General;

    public bool IsAppearance => Section == PreferencesSection.Appearance;

    public bool IsSystem => Section == PreferencesSection.System;

    public bool IsUpdates => Section == PreferencesSection.Updates;

    public bool IsAbout => Section == PreferencesSection.About;

    public string SectionTitle => Section switch
    {
        PreferencesSection.Appearance => Strings.Prefs_Appearance,
        PreferencesSection.System => Strings.Prefs_System,
        PreferencesSection.Updates => Strings.Update_Title,
        PreferencesSection.About => Strings.Prefs_AboutTitle,
        _ => Strings.Prefs_General,
    };

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
        _checkForUpdates = settings.CheckForUpdates;
        _hardwareAcceleration = settings.HardwareAcceleration;
        _ = DetectEncoderAsync();
        Updater = services.Updater;
        Updater.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(IsUpdateReady));
        };
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
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _hardwareAcceleration;

    /// <summary>Which video chip was found, once the (background) test is over.</summary>
    [ObservableProperty]
    private string? _encoderStatus;

    private async Task DetectEncoderAsync()
    {
        var hardware = await VideoEncoder.DetectHardwareAsync();
        EncoderStatus = hardware is null
            ? Strings.Prefs_HardwareNone
            : string.Format(Strings.Prefs_HardwareFound, hardware.DisplayName);
    }

    partial void OnHardwareAccelerationChanged(bool value)
    {
        _services.Settings.HardwareAcceleration = value;
        VideoEncoder.HardwareEnabled = value;
        _services.SaveSettings();
    }

    public Updater Updater { get; }

    public bool IsUpdateReady => Updater.State == UpdateState.Ready;

    public string UpdateStatus => Updater.State switch
    {
        UpdateState.Checking => Strings.Update_Checking,
        UpdateState.UpToDate => Strings.Update_UpToDate,
        UpdateState.Downloading => string.Format(Strings.Update_Downloading, Updater.AvailableVersion, Updater.Progress),
        UpdateState.Ready => string.Format(Strings.Update_Ready, Updater.AvailableVersion),
        UpdateState.Failed => Strings.Update_Failed,
        _ => "",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextMenuError))]
    private string? _contextMenuError;

    public bool IsContextMenuSupported => ExplorerMenu.Installer.IsSupported;

    public string ContextMenuLabel => OperatingSystem.IsMacOS() ? Strings.Prefs_ContextMenuMac : Strings.Prefs_ContextMenu;

    public string ContextMenuHint => OperatingSystem.IsMacOS() ? Strings.Prefs_ContextMenuHintMac : Strings.Prefs_ContextMenuHint;

    public bool HasContextMenuError => ContextMenuError is not null;

    public bool NeedsRestart => SelectedLanguage.Value != _startupLanguage;

    public string Version { get; } = string.Format(Strings.Prefs_Version,
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0");

    partial void OnOutputDirectoryChanged(string value)
    {
        _services.Settings.OutputDirectory = value;
        _services.SaveSettings();
        FolderIcon.Apply(value);
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

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _services.Settings.CheckForUpdates = value;
        _services.SaveSettings();
    }

    [RelayCommand]
    private Task CheckUpdates() => Updater.CheckAsync();

    [RelayCommand]
    private void InstallUpdate() => _main.InstallUpdate();

    partial void OnContextMenuEnabledChanged(bool value)
    {
        ContextMenuError = null;
        try
        {
            if (value && Environment.ProcessPath is { } exe)
                ExplorerMenu.Installer.Install(exe, _services.Catalog, _services.Settings.Presets);
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
    private Task OpenLogs() => DialogService.OpenFolderAsync(CrashLog.Folder);

    [RelayCommand]
    private void ResetOutput() => OutputDirectory = AppSettings.DefaultOutputDirectory;

    [RelayCommand]
    private Task OpenOutput() => DialogService.OpenFolderAsync(OutputDirectory);
}
