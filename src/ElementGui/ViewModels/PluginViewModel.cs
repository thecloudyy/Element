using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElementGui.Services;

namespace ElementGui.ViewModels;

/// <summary>
/// "Plugin" page: the store-page plugin MANAGER. The app no longer bundles the frontend. It installs,
/// updates, and removes the LuaTools plugin (the "Add via LuaTools" button on Steam store pages) by
/// downloading it from GitHub releases via <see cref="PluginInstallerService"/>. One install path
/// (LuaLoader); if the Millennium mod is present it just coexists (and install disables Millennium's own
/// redundant luatools plugin).
/// </summary>
public partial class PluginViewModel : ObservableObject
{
    private readonly PluginInstallerService _installer;
    private readonly ToastService _toast;

    public PluginViewModel(PluginInstallerService installer, ToastService toast)
    {
        _installer = installer;
        _toast = toast;
    }

    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _manifestdexcoreStatus = Resources.Strings.Plugin_Checking;
    [ObservableProperty] private string _dwmapiStatus = Resources.Strings.Plugin_Checking;
    [ObservableProperty] private string _xinputStatus = Resources.Strings.Plugin_Checking;

    // Per-component status flags: drive the colored status icons in the view (green check when
    // present, grey dismiss when missing). The *Status strings above stay the row label text.
    [ObservableProperty] private bool _manifestdexcoreInstalled;
    [ObservableProperty] private bool _dwmapiInstalled;
    [ObservableProperty] private bool _xinputInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _updateAvailable;

    /// <summary>Online hash proof that the present components match the release. False offline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _upToDate;

    /// <summary>Whether the last check reached GitHub. Offline keeps the local-only view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _offline;

    /// <summary>True when the install button should be the loud green primary CTA. Only when there's an
    /// actionable state (fresh install or an update). A healthy up-to-date "Reinstall" stays secondary.</summary>
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;

    /// <summary>Green "Up to date" pill on the version line. Installed, nothing to update, and either
    /// offline (local-only view) or hash-proven current.</summary>
    public bool ShowUpToDate => IsInstalled && !UpdateAvailable && (Offline || UpToDate);

    /// <summary>True when the Millennium mod is detected. Shown as a "coexisting" info line, not a card.</summary>
    [ObservableProperty] private bool _millenniumCoexisting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    public bool CanUninstall => IsInstalled && !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Non-null shows a small info/error line under the buttons (e.g. offline).</summary>
    [ObservableProperty] private string? _statusLine;

    public string InstallButtonText => !IsInstalled
        ? Resources.Strings.Plugin_Btn_Install
        : UpdateAvailable ? Resources.Strings.Plugin_Btn_Update : Resources.Strings.Plugin_Btn_Reinstall;

    public async Task LoadAsync() => await RefreshAsync(force: false);

    private async Task RefreshAsync(bool force)
    {
        var st = await _installer.GetStatusAsync(force);
        // Headline truth is local: all three Core DLLs on disk = installed, even offline.
        IsInstalled = st.CoreInstalled;
        // Version: Element-made installs read their manifest tag; otherwise, when online and the
        // on-disk hashes prove the files ARE the release, the release tag itself is the version.
        InstalledVersion = st.InstalledTag
            ?? ((st.UpToDate ? st.LatestTag : null) ?? (IsInstalled ? Resources.Strings.Plugin_Version_Unknown : "—"));
        UpToDate = st.UpToDate;
        Offline = st.Offline;
        LatestVersion = st.Offline ? Resources.Strings.Plugin_Version_Offline : (st.LatestTag ?? "—");
        ManifestdexcoreInstalled = st.ManifestdexcoreInstalled;
        ManifestdexcoreStatus = st.ManifestdexcoreInstalled ? Resources.Strings.Plugin_Status_Installed : Resources.Strings.Plugin_Status_NotInstalled;
        DwmapiInstalled = st.DwmapiInstalled;
        DwmapiStatus = st.DwmapiInstalled ? Resources.Strings.Plugin_Status_Installed : Resources.Strings.Plugin_Status_NotInstalled;
        XinputInstalled = st.XinputInstalled;
        XinputStatus = st.XinputInstalled ? Resources.Strings.Plugin_Status_Installed : Resources.Strings.Plugin_Status_NotInstalled;
        UpdateAvailable = st.UpdateAvailable;
        MillenniumCoexisting = st.MillenniumPresent;
        // Offline is only worth surfacing when there's nothing installed to show for it; a present
        // Core reads as a clean "Installed" with no warning. The port warning stays online-only.
        StatusLine = st.Offline && !st.CoreInstalled ? Resources.Strings.Plugin_Status_OfflineCheck
            : !st.Offline && st.Port8080Busy ? Resources.Strings.Plugin_Status_Port8080Busy
            : null;
    }

    private bool ConfirmSteamRestart()
    {
        var result = System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.OK;
    }

    private IProgress<double?> MakeProgress() => new Progress<double?>(p =>
    {
        if (p is null) { IsProgressIndeterminate = true; }
        else { IsProgressIndeterminate = false; Progress = p.Value * 100; }
    });

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        if (!ConfirmSteamRestart()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await _installer.InstallAsync(MakeProgress());
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Installed
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy || !IsInstalled) return;
        if (!ConfirmSteamRestart()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (ok, error) = await _installer.UninstallAsync();
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Removed
                : string.Format(Resources.Strings.Plugin_Toast_UninstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await RefreshAsync(force: true);
            if (StatusLine is null)
                _toast.Show(Resources.Strings.Plugin_Toast_Title,
                    UpdateAvailable ? Resources.Strings.Plugin_Toast_UpdateAvailable : Resources.Strings.Plugin_Toast_UpToDate);
        }
        finally { IsBusy = false; }
    }
}
