using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElementGui.Services;

namespace ElementGui.ViewModels;

public partial class CloudViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly ToastService _toast;
    private readonly CacheService _cache;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string CloudRedirectOwner = "Selectively11";
    private const string CloudRedirectRepo = "CloudRedirect";
    private const string DllName = "cloud_redirect.dll";

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string _installStatus = "";
    [ObservableProperty] private string _installStatusColor = "#6b7280";
    [ObservableProperty] private string _syncEngineStatus = "";
    [ObservableProperty] private string _syncEngineStatusColor = "#6b7280";
    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "…";
    [ObservableProperty] private string _installLog = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _autoSync;
    [ObservableProperty] private bool _achievementsSync;
    [ObservableProperty] private bool _playtimeSync;
    [ObservableProperty] private bool _luaSync;
    [ObservableProperty] private bool _schemaFetch;
    [ObservableProperty] private string _localPath = "";
    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private string _connectionStatusColor = "#6b7280";
    [ObservableProperty] private bool _isChecking;

    partial void OnIsEnabledChanged(bool value)
    {
        _settings.CloudEnabled = value;
        RefreshEngineStatus();
    }
    partial void OnAutoSyncChanged(bool value) => _settings.CloudAutoSync = value;
    partial void OnAchievementsSyncChanged(bool value) => _settings.CloudAchievementsSync = value;
    partial void OnPlaytimeSyncChanged(bool value) => _settings.CloudPlaytimeSync = value;
    partial void OnLuaSyncChanged(bool value) => _settings.CloudLuaSync = value;
    partial void OnSchemaFetchChanged(bool value) => _settings.CloudSchemaFetch = value;
    partial void OnLocalPathChanged(string value) => _settings.CloudLocalPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public CloudViewModel(SettingsService settings, SteamService steam, ToastService toast, CacheService cache)
    {
        _settings = settings;
        _steam = steam;
        _toast = toast;
        _cache = cache;

        _isEnabled = settings.CloudEnabled;
        _autoSync = settings.CloudAutoSync;
        _achievementsSync = settings.CloudAchievementsSync; // default ON. Init without triggering Save
        _playtimeSync = settings.CloudPlaytimeSync;
        _luaSync = settings.CloudLuaSync;
        _schemaFetch = settings.CloudSchemaFetch;
        _localPath = settings.CloudLocalPath ?? DefaultLocalCloudPath();
        if (settings.CloudLocalPath is null)
        {
            // First run with the new default: actually make the folder.
            try { Directory.CreateDirectory(_localPath); } catch { }
        }

        CheckInstallStatus();
    }

    private void CheckInstallStatus()
    {
        string steamPath = _steam.EffectivePath;
        if (string.IsNullOrEmpty(steamPath))
        {
            IsInstalled = false;
            InstallStatus = Resources.Strings.Cloud_SteamNotFound;
            InstallStatusColor = "#f87171";
            InstalledVersion = "—";
            RefreshEngineStatus();
            return;
        }

        string dllPath = Path.Combine(steamPath, DllName);
        if (File.Exists(dllPath))
        {
            IsInstalled = true;
            InstallStatus = Resources.Strings.Cloud_Installed;
            InstallStatusColor = "#34d399";
            InstalledVersion = DisplayVersion(_cache.CloudRedirectVersion ?? ReadFileVersion(dllPath));
        }
        else
        {
            IsInstalled = false;
            InstallStatus = Resources.Strings.Cloud_NotInstalled;
            InstallStatusColor = "#6b7280";
            InstalledVersion = "—";
        }
        RefreshEngineStatus();
        _ = RefreshLatestVersionAsync();
    }

    private static string DisplayVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag == "—") return "—";
        tag = tag.Trim();
        return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag : "v" + tag;
    }

    private static string ReadFileVersion(string dllPath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
            return info.FileVersion ?? info.ProductVersion ?? "—";
        }
        catch { return "—"; }
    }

    private void RefreshEngineStatus()
    {
        if (!IsInstalled)
        {
            SyncEngineStatus = Resources.Strings.Cloud_NotInstalled;
            SyncEngineStatusColor = "#6b7280";
        }
        else if (!IsEnabled)
        {
            SyncEngineStatus = Resources.Strings.Cloud_SyncPaused;
            SyncEngineStatusColor = "#fbbf24";
        }
        else
        {
            SyncEngineStatus = Resources.Strings.Cloud_SyncActive;
            SyncEngineStatusColor = "#34d399";
        }
    }

    private async Task RefreshLatestVersionAsync()
    {
        try
        {
            var release = await GetLatestRelease();
            LatestVersion = release is { Tag.Length: > 0 } ? release.Tag : "—";
        }
        catch { LatestVersion = "—"; }
    }

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        IsBusy = true;
        InstallLog = "";
        try
        {
            string steamPath = _steam.EffectivePath;
            if (string.IsNullOrEmpty(steamPath))
            {
                AppendLog(Resources.Strings.Cloud_Log_SteamNotFound);
                InstallStatus = Resources.Strings.Cloud_SteamNotFound;
                InstallStatusColor = "#f87171";
                return;
            }

            string dllPath = Path.Combine(steamPath, DllName);
            AppendLog(string.Format(Resources.Strings.Cloud_Log_SteamPath, steamPath));
            AppendLog(Resources.Strings.Cloud_Log_CheckingRelease);

            var release = await GetLatestRelease();
            if (release is null)
            {
                AppendLog(Resources.Strings.Cloud_Log_ReleaseFailed);
                InstallStatus = Resources.Strings.Cloud_Log_ReleaseFailed;
                InstallStatusColor = "#f87171";
                return;
            }

            AppendLog(string.Format(Resources.Strings.Cloud_Log_LatestVersion, release.Tag));

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.Contains("cloud_redirect", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                AppendLog(Resources.Strings.Cloud_Log_DllNotFound);
                InstallStatus = Resources.Strings.Cloud_Log_DllNotFound;
                InstallStatusColor = "#f87171";
                return;
            }

            AppendLog(string.Format(Resources.Strings.Cloud_Log_Downloading, asset.Name));

            byte[] data = await Http.GetByteArrayAsync(asset.BrowserDownloadUrl);
            AppendLog(string.Format(Resources.Strings.Cloud_Log_Downloaded, data.Length));

            AppendLog(Resources.Strings.Cloud_Log_StoppingSteam);
            _steam.StopSteam();
            await Task.Delay(2000);

            File.WriteAllBytes(dllPath, data);
            AppendLog(string.Format(Resources.Strings.Cloud_Log_Deployed, dllPath));
            _cache.CloudRedirectVersion = release.Tag;

            AppendLog(Resources.Strings.Cloud_Log_RestartingSteam);
            _steam.StartSteam();

            InstallStatus = Resources.Strings.Cloud_Installed;
            InstallStatusColor = "#34d399";
            IsInstalled = true;
            InstalledVersion = DisplayVersion(_cache.CloudRedirectVersion);
            RefreshEngineStatus();
            AppendLog(Resources.Strings.Cloud_Log_Done);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            InstallStatus = Resources.Strings.Cloud_Log_Error;
            InstallStatusColor = "#f87171";
            try { _steam.StartSteam(); } catch { }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy) return;
        IsBusy = true;
        InstallLog = "";
        try
        {
            string steamPath = _steam.EffectivePath;
            if (string.IsNullOrEmpty(steamPath))
            {
                AppendLog(Resources.Strings.Cloud_Log_SteamNotFound);
                return;
            }

            string dllPath = Path.Combine(steamPath, DllName);
            if (!File.Exists(dllPath))
            {
                AppendLog(Resources.Strings.Cloud_Log_DllAlreadyRemoved);
            IsInstalled = false;
            InstallStatus = Resources.Strings.Cloud_NotInstalled;
            InstallStatusColor = "#6b7280";
            InstalledVersion = "—";
            _cache.CloudRedirectVersion = null;
            RefreshEngineStatus();
            return;
        }

            AppendLog(Resources.Strings.Cloud_Log_StoppingSteam);
            _steam.StopSteam();
            await Task.Delay(2000);

            File.Delete(dllPath);
            AppendLog(string.Format(Resources.Strings.Cloud_Log_Removed, dllPath));

            AppendLog(Resources.Strings.Cloud_Log_RestartingSteam);
            _steam.StartSteam();

            IsInstalled = false;
            InstallStatus = Resources.Strings.Cloud_NotInstalled;
            InstallStatusColor = "#6b7280";
            InstalledVersion = "—";
            _cache.CloudRedirectVersion = null;
            RefreshEngineStatus();
            AppendLog(Resources.Strings.Cloud_Log_UninstallDone);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            try { _steam.StartSteam(); } catch { }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        try
        {
            string? steamPath = _steam.EffectivePath;
            if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steamPath) { UseShellExecute = true });
        }
        catch { }
    }

    private void AppendLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        InstallLog += $"[{timestamp}] {message}\n";
    }

    private sealed class GitHubRelease
    {
        public string Tag { get; init; } = "";
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; init; } = "";
        public string BrowserDownloadUrl { get; init; } = "";
    }

    private async Task<GitHubRelease?> GetLatestRelease()
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{CloudRedirectOwner}/{CloudRedirectRepo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("ElementGui/1.0");
        var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                assets.Add(new GitHubAsset
                {
                    Name = a.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = a.GetProperty("browser_download_url").GetString() ?? "",
                });
            }
        }
        return new GitHubRelease { Tag = tag, Assets = assets };
    }

    [RelayCommand]
    private async Task CheckConnection()
    {
        if (IsChecking) return;
        IsChecking = true;
        ConnectionStatus = Resources.Strings.Cloud_Status_Checking;
        ConnectionStatusColor = "#6b7280";
        try
        {
            if (string.IsNullOrWhiteSpace(LocalPath) || !Directory.Exists(LocalPath))
            {
                ConnectionStatus = Resources.Strings.Cloud_Status_PathNotFound;
                ConnectionStatusColor = "#f87171";
                return;
            }
            ConnectionStatus = Resources.Strings.Cloud_Status_LocalOk;
            ConnectionStatusColor = "#34d399";
        }
        catch
        {
            ConnectionStatus = Resources.Strings.Cloud_Status_Unreachable;
            ConnectionStatusColor = "#f87171";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Default local-cloud folder: &lt;Steam&gt;\localcloud
    /// (C:\Program Files (x86)\Steam\localcloud for standard installs).</summary>
    private string DefaultLocalCloudPath()
    {
        var steam = _steam.EffectivePath;
        var baseDir = !string.IsNullOrEmpty(steam) ? steam : @"C:\Program Files (x86)\Steam";
        return Path.Combine(baseDir, "localcloud");
    }

    [RelayCommand]
    private void BrowseLocalFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Resources.Strings.Cloud_SelectFolder,
            InitialDirectory = LocalPath,
        };
        if (dialog.ShowDialog() == true)
            LocalPath = dialog.FolderName;
    }
}
