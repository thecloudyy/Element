using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElementGui.Models;
using ElementGui.Services;
using ElementGui.Services.Downloads;

namespace ElementGui.ViewModels;

/// <summary>A game card in the Fixes grid (from thecloudyy/OnlineFixes).</summary>
public partial class FixGameCardVm(OnlineFixGame g) : ObservableObject
{
    public string AppId { get; } = g.AppId.ToString();
    public long AppIdLong { get; } = g.AppId;
    public string Name { get; } = g.Name;
    public string? HeaderImage { get; } = g.HeaderImage;
    public int FixCount { get; } = g.Fixes.Count;
    public IReadOnlyList<string> TagIds { get; } = [];
    public string FixCountLabel => string.Format(Resources.Strings.Fixes_Count, FixCount);

    /// <summary>Only show the "x fixes" label when there is more than one fix to count.</summary>
    public bool ShowFixCount => FixCount > 1;

    /// <summary>Local cached cover path (set after CoverCache resolves it); bound via ImagePathToSource.</summary>
    [ObservableProperty] private string? _cover;
    private int _resolving;

    /// <summary>True when at least one fix for this game has a revert record on disk.</summary>
    [ObservableProperty] private bool _hasAppliedFix;

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || AppId.Contains(q);

    /// <summary>Cache the header image to disk once (CoverCache, keyed by appid), then expose its path.</summary>
    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null || string.IsNullOrWhiteSpace(HeaderImage)) return;
        if (!long.TryParse(AppId, out long appid)) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(appid) ?? await covers.EnsureAsync(appid, HeaderImage!);
            if (local is not null) Cover = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }
}

/// <summary>A tag filter pill; IsSelected drives its active highlight.</summary>
public partial class TagPillVm : ObservableObject
{
    public TagPillVm(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; } = "";
    public string Name { get; } = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One fix zip in the per-game flyout (from OnlineFixes repo, fix slot only).</summary>
public partial class FixItemVm(OnlineFixEntry e) : ObservableObject
{
    public OnlineFixEntry Entry { get; } = e;
    public string Id { get; } = e.FileName;
    public string Title { get; } = e.FileName;
    public string? Description { get; }
    public IReadOnlyList<string> Tags { get; } = [];
    public bool HasManifest { get; }
    public bool HasFix { get; } = true;
    public string? ManifestFilename { get; }
    public string? FixFilename { get; } = e.FileName;
    public string DateLabel { get; } = "";

    /// <summary>In-flight queue item for this fix. The button and progress bar bind straight through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix))]
    private DownloadItem? _fixItem;

    // Kept for XAML compat (Manifest button binds to these, HasManifest=false hides it).
    [ObservableProperty] private DownloadItem? _manifestItem;
    public bool CanDownloadManifest => false;

    /// <summary>
    /// Whether the game folder is known on disk. The Fix button stays enabled even when false:
    /// clicking it opens the folder picker popup to locate the game.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]
    private bool _gameInstalled;

    /// <summary>True when the fix has been applied (its revert record exists on disk). Drives Revert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied), nameof(CanDownloadFix), nameof(FixHint))]
    private bool _isApplied;

    public bool HasApplied => IsApplied;

    public bool CanDownloadFix => HasFix && !IsApplied && FixItem?.IsActive != true;

    /// <summary>Tooltip hint. Shows install hint when folder unknown, even though button stays clickable.</summary>
    public string? FixHint =>
        IsApplied ? Resources.Strings.Fixes_Applied_Hint
        : GameInstalled ? null
        : Resources.Strings.Fixes_NotInstalled_Hint;
}

/// <summary>
/// "Fixes" page: browse games with online fixes from thecloudyy/OnlineFixes (grid + search),
/// open a game to see its fix zips, and download a fix zip (extract into the game folder,
/// auto-detected or user-picked via popup). No lua.tools APIs are used for fixes.
/// </summary>
public partial class FixesViewModel : PagedListViewModel<FixGameCardVm>
{
    private readonly OnlineFixesService online;
    private readonly CoverCache covers;
    private readonly ToastService toast;
    private readonly SettingsService settings;
    private readonly DownloadQueue queue;
    private readonly ManifestJobFactory jobs;
    private readonly SteamLibraryService library;
    private readonly SteamService steam;
    private readonly AppliedFixIndexService fixIndex;
    private readonly LuaInstaller installer;

    public FixesViewModel(
        OnlineFixesService online, CoverCache covers, ToastService toast,
        SettingsService settings, DownloadQueue queue, ManifestJobFactory jobs,
        SteamLibraryService library, SteamService steam, AppliedFixIndexService fixIndex,
        LuaInstaller installer)
    {
        this.online = online;
        this.covers = covers;
        this.toast = toast;
        this.settings = settings;
        this.queue = queue;
        this.jobs = jobs;
        this.library = library;
        this.steam = steam;
        this.fixIndex = fixIndex;
        this.installer = installer;
        InitPageSize(settings.FixesPageSize);
    }

    // The master list; the displayed page slice lives in the base's Items collection.
    private List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = [];

    protected override void SavePageSizeSetting(int size) => settings.FixesPageSize = size;

    /// <summary>Warm the cover images for just the freshly-shown page (idempotent, off-UI).</summary>
    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(covers);
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty] private string? _selectedTagId;

    // Appids with a known install folder (Steam auto-detect + manual popup picks).
    private HashSet<long> _installedAppIds = [];

    /// <summary>True once the listing has been fetched (set at the end of LoadAsync).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilter))]
    private bool _loaded;

    /// <summary>Gates the filter pills ("my games") behind a finished, non-empty listing.</summary>
    public bool CanFilter => Loaded && _allGames.Count > 0;

    /// <summary>Only show fix games installed on disk (auto-detected or manually picked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyGamesHint))]
    private bool _myGamesOnly;

    public string MyGamesHint
    {
        get
        {
            if (_installedAppIds.Count == 0) return Resources.Strings.Fixes_MyGames_NotInstalled;

            int withFixes = _allGames.Count(g =>
                long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
            return string.Format(Resources.Strings.Fixes_MyGames_Count, withFixes);
        }
    }

    partial void OnMyGamesOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null;
            foreach (var pill in Tags) pill.IsSelected = false;
            if (AppliedOnly) AppliedOnly = false;
        }
        ApplyFilter();
    }

    /// <summary>Only show games with at least one applied fix (revertable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedHint))]
    private bool _appliedOnly;

    /// <summary>Appids with a revert record on disk (from the applied-fix index).</summary>
    private HashSet<long> _appliedAppIds = [];

    public string AppliedHint
    {
        get
        {
            if (_appliedAppIds.Count == 0) return Resources.Strings.Fixes_Applied_Empty;
            return string.Format(Resources.Strings.Fixes_Applied_Count, _appliedAppIds.Count);
        }
    }

    partial void OnAppliedOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null;
            foreach (var pill in Tags) pill.IsSelected = false;
            if (MyGamesOnly) MyGamesOnly = false;
            _ = RefreshAppliedSetAsync();
        }
        ApplyFilter();
    }

    /// <summary>Reload applied appids from the index and mark cards. Records are confirmed on read.</summary>
    public async Task RefreshAppliedSetAsync(CancellationToken ct = default)
    {
        try
        {
            var entries = await fixIndex.ListAsync(ct);
            var indexed = new HashSet<long>(entries.Select(e => e.AppId));
            // Disk confirmation OFF the UI thread: ~1200 GetInstallDir probes (VDF + ACF reads each)
            // would otherwise jank the page for seconds. Cards are updated back on the UI thread.
            var confirmed = await Task.Run(() =>
            {
                var set = new HashSet<long>(indexed);
                foreach (var g in _allGames)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!long.TryParse(g.AppId, out long id) || set.Contains(id)) continue;
                    try
                    {
                        string? dir = library.GetInstallDir(id);
                        string? fixDir = dir is not null
                            ? Path.Combine(dir, ManifestJobFactory.FixRecordDir)
                            : null;
                        if (fixDir is not null && Directory.Exists(fixDir)
                            && Directory.GetFiles(fixDir, "*.json").Length > 0)
                            set.Add(id);
                    }
                    catch { }
                }
                return set;
            }, ct);
            _appliedAppIds = confirmed;
            foreach (var g in _allGames)
                if (long.TryParse(g.AppId, out long id))
                    g.HasAppliedFix = confirmed.Contains(id);
            OnPropertyChanged(nameof(AppliedHint));
            ApplyFilter();
        }
        catch { }
    }

    /// <summary>Set by App: navigate to Add and load this appid (the "Add game" button).</summary>
    public Action<long>? NavigateToAdd { get; set; }

    // -- Detail flyout -----------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private FixGameCardVm? _selectedGame;

    public bool IsDetailOpen => SelectedGame is not null;

    /// <summary>
    /// "Add game" button visibility: hidden when the game's lua is already in the library
    /// (same rule as Manage — a lua in Steam's config/stplug-in).
    /// </summary>
    [ObservableProperty] private bool _showAddGame;

    public ObservableCollection<FixItemVm> Fixes { get; } = [];
    [ObservableProperty] private bool _isLoadingFixes;

    // Per-game tag filter kept for XAML compat (OnlineFixes has no tags, stays empty).
    private List<FixItemVm> _allFixes = [];
    public ObservableCollection<TagPillVm> FixTags { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixTags))]
    private string? _selectedFixTagId;
    public bool HasFixTags => FixTags.Count > 0;

    // -- Load ---------------------------------------------------------

    /// <param name="force">True to re-fetch even if already loaded (the Refresh button).</param>
    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return;
        if (force) online.InvalidateCache();
        IsLoading = true;
        try
        {
            var games = await online.GetGamesAsync();
            _allGames = games.Select(g => new FixGameCardVm(g)).ToList();
            Tags.Clear();
            RefreshInstalledSet();
            await RefreshAppliedSetAsync();
            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = Resources.Strings.Fixes_Empty_None;
        }
        catch
        {
            EmptyMessage = Resources.Strings.Fixes_Err_Load;
        }
        finally
        {
            IsLoading = false;
            Loaded = true;
        }
    }

    private void RefreshInstalledSet()
    {
        try
        {
            var set = new HashSet<long>(library.EnumerateInstalled().Select(g => g.AppId));
            foreach (var (id, _) in settings.GetManualInstallDirs()) set.Add(id);
            // A manual pick counts even when the game has no appmanifest.
            foreach (var g in _allGames)
            {
                if (long.TryParse(g.AppId, out long id) && library.GetInstallDir(id) is not null)
                    set.Add(id);
            }
            _installedAppIds = set;
            OnPropertyChanged(nameof(MyGamesHint));
        }
        catch { _installedAppIds = []; }
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = "";
        if (SelectedTagId is not null) SelectTag(SelectedTagId);
        if (MyGamesOnly) MyGamesOnly = false;
        if (AppliedOnly) AppliedOnly = false;
        await LoadAsync(force: true);
        toast.Show(Resources.Strings.Fixes_Toast_Refreshed_Title,
            string.Format(Resources.Strings.Fixes_Toast_Refreshed_Body, _allGames.Count));
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId;
        if (MyGamesOnly) MyGamesOnly = false;
        if (AppliedOnly) AppliedOnly = false;
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.TagIds.Contains(tag));
        if (MyGamesOnly) shown = shown.Where(g => long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
        if (AppliedOnly) shown = shown.Where(g => g.HasAppliedFix
            || (long.TryParse(g.AppId, out long id) && _appliedAppIds.Contains(id)));
        if (q.Length > 0) shown = shown.Where(g => g.Matches(q));

        var list = shown.ToList();
        if (AppliedOnly && list.Count == 0)
            EmptyMessage = Resources.Strings.Fixes_Applied_Empty;
        else if (_allGames.Count == 0)
            EmptyMessage = Resources.Strings.Fixes_Empty_None;
        else if (list.Count == 0)
            EmptyMessage = Resources.Strings.Manage_Empty_NoMatch;
        else
            EmptyMessage = "";

        SetFiltered(list);
    }

    // -- Detail flyout -----------------------------------------------

    /// <summary>
    /// Open the detail flyout for a specific game by its Steam AppId.
    /// Used by the element://fix/ protocol handler.
    /// </summary>
    public async Task OpenForAppIdAsync(long appId)
    {
        if (_allGames.Count == 0)
            await LoadAsync();

        var game = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        if (MyGamesOnly) MyGamesOnly = false;
        if (AppliedOnly) AppliedOnly = false;
        ApplyFilter();

        await OpenGame(game);
    }

    [RelayCommand]
    private void CopyAppId(FixGameCardVm game)
    {
        if (!SteamService.CopyToClipboard(game.AppId))
            toast.Show(Resources.Strings.Common_CopyAppId, Resources.Strings.Err_ClipboardBusy, error: true);
    }

    /// <summary>Open the game's install folder (auto or manually picked).</summary>
    [RelayCommand]
    private void ShowInFolder(FixGameCardVm game)
    {
        string? dir = long.TryParse(game.AppId, out long appId) ? library.GetInstallDir(appId) : null;
        if (SteamService.ShowInExplorer(dir)) return;

        toast.Show(Resources.Strings.Fixes_Toast_GameNotFound,
            string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, game.Name), error: true);
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        SelectedGame = game;
        try { ShowAddGame = installer.ReadInstalledLua(game.AppIdLong) is null; }
        catch { ShowAddGame = true; }
        _ = game.EnsureCoverAsync(covers);
        Fixes.Clear();
        _allFixes = [];
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var entries = await online.GetFixesAsync(game.AppIdLong);
            _allFixes = entries.Select(e => new FixItemVm(e)).ToList();
            // One probe for the whole game: GetInstallDir walks VDFs/ACFs per call.
            bool installed = library.GetInstallDir(game.AppIdLong) is not null;
            foreach (var f in _allFixes)
            {
                f.GameInstalled = installed;
                try { f.IsApplied = jobs.IsFixApplied(game.AppIdLong, f.Id); } catch { }
            }
            game.HasAppliedFix = _allFixes.Any(f => f.IsApplied);
            if (game.HasAppliedFix) { _appliedAppIds.Add(game.AppIdLong); OnPropertyChanged(nameof(AppliedHint)); }
            ApplyFixFilter();
        }
        catch { /* leave empty. Flyout shows "no fixes" */ }
        finally { IsLoadingFixes = false; }
    }

    [RelayCommand]
    private void SelectFixTag(string? tagId)
    {
        SelectedFixTagId = SelectedFixTagId == tagId ? null : tagId;
        foreach (var pill in FixTags) pill.IsSelected = pill.Id == SelectedFixTagId;
        ApplyFixFilter();
    }

    private void ApplyFixFilter()
    {
        IEnumerable<FixItemVm> shown = _allFixes;
        if (SelectedFixTagId is { } tag) shown = shown.Where(f => f.Tags.Contains(tag));
        Fixes.Clear();
        foreach (var f in shown) Fixes.Add(f);
    }

    /// <summary>"Add game": jump to the Add tab with this game loaded (Fetch + download a source).</summary>
    [RelayCommand]
    private void AddGame()
    {
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;
        NavigateToAdd?.Invoke(appId);
    }

    [RelayCommand]
    private void CloseDetail()
    {
        // Sync this game's badge from disk before closing: apply/revert handlers already do,
        // but this covers records changed behind our back (manual delete, another copy of the app).
        if (SelectedGame is { } closing && long.TryParse(closing.AppId, out long closingId))
        {
            try
            {
                string? dir = library.GetInstallDir(closingId);
                string? fixDir = dir is not null
                    ? Path.Combine(dir, ManifestJobFactory.FixRecordDir)
                    : null;
                bool applied = fixDir is not null && Directory.Exists(fixDir)
                    && Directory.GetFiles(fixDir, "*.json").Length > 0;
                closing.HasAppliedFix = applied;
                if (applied) _appliedAppIds.Add(closingId);
                else _appliedAppIds.Remove(closingId);
                OnPropertyChanged(nameof(AppliedHint));
            }
            catch { }
        }
        SelectedGame = null;
        ShowAddGame = false;
        ApplyFilter();
    }

    // -- Downloads ----------------------------------------------------

    [RelayCommand]
    private Task DownloadManifest(FixItemVm fix)
    {
        // Manifest slot removed: OnlineFixes ships fix zips only, no lua.tools.
        toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed,
            Resources.Strings.Fixes_Empty_None, error: true);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task DownloadFix(FixItemVm fix) => RunDownload(fix);

    /// <summary>Confirm, then revert an applied fix back to its original files.</summary>
    [RelayCommand]
    private void RevertFix(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        _pendingRevert = (fix, game);
        ConfirmRevertTitle = string.Format(Resources.Strings.Fixes_Revert_Confirm_Title, game.Name);
        ConfirmRevertBody = string.Format(Resources.Strings.Fixes_Revert_Confirm_Body, game.Name);
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevertConfirm()
    {
        IsConfirmingRevert = false;
        _pendingRevert = null;
    }

    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        var pending = _pendingRevert;
        _pendingRevert = null;
        if (pending is not (var fix, var game)) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        var result = await Task.Run(() => jobs.RevertDenuvoFix(appId, fix.Id, game.Name));
        if (!result.Ok) return;

        fix.IsApplied = false;
        // Recompute from disk, never from stale flags: the record file is the truth.
        // A failed cleanup keeps the record (and the method reports failure), so a success
        // here means THIS fix's record is gone — but sibling fixes may still hold records.
        bool anyLeft = false;
        try
        {
            foreach (var f in _allFixes)
            {
                if (f == fix) continue;
                try { f.IsApplied = jobs.IsFixApplied(appId, f.Id); }
                catch { f.IsApplied = false; }
                if (f.IsApplied) anyLeft = true;
            }

            string? dir = library.GetInstallDir(appId);
            if (dir is not null)
            {
                string fixDir = Path.Combine(dir, ManifestJobFactory.FixRecordDir);
                if (Directory.Exists(fixDir) && Directory.GetFiles(fixDir, "*.json").Length > 0)
                    anyLeft = true;
            }
        }
        catch { }
        game.HasAppliedFix = anyLeft;
        if (anyLeft) _appliedAppIds.Add(appId);
        else _appliedAppIds.Remove(appId);
        OnPropertyChanged(nameof(AppliedHint));
        ApplyFilter();
    }

    private (FixItemVm Fix, FixGameCardVm Game)? _pendingRevert;
    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private string _confirmRevertTitle = "";
    [ObservableProperty] private string _confirmRevertBody = "";

    /// <summary>
    /// Queue a fix: auto-find the install folder, else popup for the user to pick it.
    /// Download + install run in the shared queue; this returns once enqueued.
    /// </summary>
    private async Task RunDownload(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        // Auto-find first; popup only when missing. Persisted for next time.
        string? dir = library.GetInstallDir(appId)
            ?? library.ResolveInstallDirWithPrompt(appId, game.Name);
        if (dir is null || !Directory.Exists(dir))
        {
            toast.Show(Resources.Strings.Fixes_Toast_FolderCancelled,
                Resources.Strings.Fixes_Toast_FolderCancelled_Body, error: true);
            return;
        }

        fix.GameInstalled = true;
        RefreshInstalledSet();
        ApplyFilter();

        var job = jobs.CreateOnlineFixJob(fix.Entry, appId, game.Name,
            onFinished: (item, result) =>
            {
                if (result is null && item.Status == DownloadStatus.Failed)
                {
                    toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed,
                        item.Message ?? Resources.Strings.Fixes_Toast_DownloadFailed_Body, error: true);
                    return;
                }
                if (result?.Ok == true)
                {
                    fix.IsApplied = true;
                    game.HasAppliedFix = true;
                    _appliedAppIds.Add(appId);
                    OnPropertyChanged(nameof(AppliedHint));
                    ApplyFilter();
                }
            });

        fix.FixItem = queue.Enqueue(job);
    }
}
