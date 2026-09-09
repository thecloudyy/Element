using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElementGui.Services;
using ElementGui.Models;
using Microsoft.Win32;

namespace ElementGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    // ── Startup behavior ────────────────────────────────────────────
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Element";

    /// <summary>Launch the app on Windows sign-in (writes HKCU …\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (value)
                // --minimized: start silently in the tray on sign-in so it just serves the
                // local backend (127.0.0.1:6767) for the Steam plugin without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked. Setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>Available UI languages (native endonyms, matching Steam's list). "System default"
    /// (null tag) follows Windows. Languages whose .resx isn't present fall back to English.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
        new("Español (España)", "es"),
        new("Polski", "pl"),
        new("Türkçe", "tr"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, SteamService steam)
    {
        _settings = settings;
        _steam = steam;
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    public void RefreshAccount()
    {
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void SignOut() { }

    public void OnViewLoaded()
    {
    }
}
