using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElementGui.Services;
using ElementGui.Models;
using Microsoft.Win32;

namespace ElementGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

/// <summary>Which API the "Add with Element" store button uses. Hubcap first (default).</summary>
public record ButtonModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly ToastService _toast;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    // ── Built-in button mode ────────────────────────────────────────
    /// <summary>Which API the "Add with Element" store button uses. Hubcap first (default).</summary>
    public ObservableCollection<ButtonModeOption> ButtonModeOptions { get; } =
    [
        new("Hubcap", "Hubcap"),
        new("Ryuu", "Ryuu"),
    ];

    [ObservableProperty] private ButtonModeOption _selectedButtonMode = null!;

    partial void OnSelectedButtonModeChanged(ButtonModeOption value)
    {
        if (value is null) return;
        _settings.BuiltInButtonMode = value.Tag;
        // The button mode applies on next launch: changing it requires a restart,
        // same flow as a language change.
        RequestButtonModeRestartPrompt?.Invoke();
    }

    /// <summary>Show the "button mode changed. Restart Steam now?" toast. Wired in App like the language prompt.</summary>
    public Action? RequestButtonModeRestartPrompt { get; set; }

    /// <summary>
    /// Restart Steam so the new Built-In Button Mode takes effect where it matters
    /// (the module + Steam side). Runs off the UI thread: the kill waits on exit.
    /// </summary>
    [RelayCommand]
    private async Task RestartSteamForButtonMode()
    {
        await Task.Run(() => _steam.RestartSteam());
    }

    // ── Block Steam updates (steam.cfg) ────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _blockSteamUpdates;

    /// <summary>Show the "steam.cfg changed. Restart Steam now?" toast. Wired in App like the button mode prompt.</summary>
    public Action<bool>? RequestSteamCfgRestartPrompt { get; set; }

    partial void OnBlockSteamUpdatesChanged(bool value)
    {
        _settings.BlockSteamUpdates = value;
        try
        {
            string? steamDir = _steam.EffectivePath;
            if (steamDir is null) return;
            string cfgPath = Path.Combine(steamDir, "steam.cfg");
            if (value)
                File.WriteAllText(cfgPath, "BootStrapperInhibitAll=Enable" + Environment.NewLine);
            else if (File.Exists(cfgPath))
                File.Delete(cfgPath);
            RequestSteamCfgRestartPrompt?.Invoke(value);
        }
        catch { /* write failed — setting is still saved */ }
    }

    [RelayCommand]
    private async Task CleanSteamCache()
    {
        if (IsBusy) return;

        var confirm = System.Windows.MessageBox.Show(
            Resources.Strings.Settings_CleanSteamCache_Confirm,
            Resources.Strings.Settings_CleanSteamCache,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        string? steamDir = _steam.EffectivePath;
        if (steamDir is null) return;

        IsBusy = true;
        try
        {
            _toast.Show(Resources.Strings.Settings_CleanSteamCache, Resources.Strings.SteamCache_Cleaning_Body);

            bool wasRunning = Process.GetProcessesByName("steam").Length > 0;
            await Task.Run(() => _steam.StopSteam());
            await Task.Delay(1500);

            string[] cacheFolders = ["appcache", "depotcache", "htmlcache", "librarycache", "logs", "shadercache", "downloads"];
            foreach (string folder in cacheFolders)
            {
                string path = Path.Combine(steamDir, folder);
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
            }
            // userdata/*/local
            string userdataPath = Path.Combine(steamDir, "userdata");
            if (Directory.Exists(userdataPath))
            {
                foreach (string userDir in Directory.GetDirectories(userdataPath))
                {
                    string localPath = Path.Combine(userDir, "local");
                    try { if (Directory.Exists(localPath)) Directory.Delete(localPath, recursive: true); } catch { }
                }
            }

            if (wasRunning) await Task.Run(() => _steam.StartSteam());

            _toast.Show(Resources.Strings.SteamCache_Cleaned_Title, Resources.Strings.SteamCache_Cleaned_Body);
        }
        finally
        {
            IsBusy = false;
        }
    }

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

    public SettingsViewModel(SettingsService settings, SteamService steam, ToastService toast)
    {
        _settings = settings;
        _steam = steam;
        _toast = toast;
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _blockSteamUpdates = settings.BlockSteamUpdates; // default OFF. Init without triggering the cfg write

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        // Select the saved button mode (Hubcap default). Field init: the change handler
        // only writes settings, so firing it here would be harmless, but direct init is exact.
        _selectedButtonMode = ButtonModeOptions.FirstOrDefault(o =>
            string.Equals(o.Tag, settings.BuiltInButtonMode, StringComparison.OrdinalIgnoreCase))
            ?? ButtonModeOptions[0];
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

    public void OnViewLoaded()
    {
    }
}
