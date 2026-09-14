using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElementGui.Models;

namespace ElementGui.Services;

/// <summary>
/// Manages the mutually-exclusive Steam unlockers (Element / Custom). Only
/// one is active at a time. Each managed mode resolves its own build, verifies files by sha256, and
/// installs into the Steam root; Custom downloads and verifies nothing, since the user owns those
/// files. Switching overwrites shared files but doesn't delete the previous mode's leftovers. The
/// active mode persists in settings.
/// </summary>
public class UnlockerService(SteamService steam, SettingsService settings, CacheService cache, GithubProxy gh)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Per-mode cache of the GitHub release so re-opening the page doesn't hammer the API
    // (unauthenticated GitHub allows only 60 req/hr per IP). The "Check for updates" button forces a
    // fresh fetch (30s cooldown) for anyone who wants certainty sooner.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<UnlockerMode, (GithubRelease release, DateTime fetchedAt)> _releaseCache = new();

    public IReadOnlyList<ModeDefinition> Modes { get; } =
    [
        // ManifestDeXCore: DLLs from GitHub releases, index starts at 1.4.8.
        new(UnlockerMode.Element, "ManifestDeXCore",
            Description: "ManifestDeXCore mode from GitHub releases",
            Kind: ModeKind.Zip,
            Owner: "thecloudyy", Repo: "ManifestDeXCore",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "manifestdexcore.dll"],
            ZipAssetPattern: "ManifestDeXCore-{version}-Release.zip"),

        // Opt-out: the user installs and updates their own unlocker, and we place/verify nothing.
        new(UnlockerMode.Custom, Resources.Strings.Mode_Name_Custom,
            Description: Resources.Strings.Mode_Desc_Custom,
            Kind: ModeKind.Manual,
            Owner: "", Repo: "",
            FixedTag: null,
            PlaceFiles: [],
            ZipAssetPattern: null),
    ];

    private ModeDefinition Def(UnlockerMode mode) => Modes.First(m => m.Mode == mode);

    /// <summary>The currently-active mode (the last one installed/selected), or null if none yet.</summary>
    public UnlockerMode? SelectedMode =>
        Enum.TryParse(settings.SelectedMode, out UnlockerMode m) ? m : null;

    /// <summary>Short display name of the active mode for status UI; null if none selected/detected yet.</summary>
    public string? SelectedModeDisplayName =>
        SelectedMode is { } m ? Def(m).DisplayName : null;

    /// <summary>
    /// Make sure the active OST/BST install is watching <c>config/stplug-in</c>, so luas written there
    /// hot-reload instead of needing a Steam restart.
    /// </summary>
    /// <remarks>
    /// The app no longer tells users to restart Steam after a lua change, because OST/BST re-read any
    /// directory listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c>. That makes this registration
    /// load-bearing rather than a nicety: previously it ran only inside <see cref="InstallAsync"/>, so a
    /// user who set their unlocker up outside this app got neither hot-reload nor restart advice.
    ///
    /// Safe to call repeatedly — the underlying edit is targeted, comment-preserving and append-only, and
    /// no-ops when the path is already present. Skipped for <c>Custom</c>, whose unlocker we know nothing
    /// about, and when no mode is selected.
    /// </remarks>
    public void EnsureLuaPathRegistered()
    {
        if (SelectedMode is not UnlockerMode.Element) return;
        if (steam.EffectivePath is not { } root) return;
        try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }
    }

    // ── State query ─────────────────────────────────────────────────

    /// <summary>Query GitHub + local files → this mode's status. Returns Unknown on any failure/offline.
    /// Cached briefly unless <paramref name="forceRefresh"/>.</summary>
    public async Task<ModeState> GetStateAsync(UnlockerMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var def = Def(mode);
        bool active = SelectedMode == mode;

        // Custom: the user owns their files. Nothing to fetch, nothing to compare.
        if (def.Kind == ModeKind.Manual)
            return new ModeState(mode, ModeStatus.UserManaged, active, null);

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new ModeState(mode, ModeStatus.Unknown, active, null);

        // Check if the main payload DLL exists on disk.
        string payloadDll = def.PlaceFiles.Last(); // ElementSteam.dll
        string localPath = Path.Combine(root, payloadDll);
        bool installed = File.Exists(localPath);

        // Fetch the latest release from GitHub.
        var release = await FetchReleaseAsync(def, forceRefresh, ct);
        if (release is null)
        {
            // Can't reach GitHub: if installed locally we at least know that much.
            return new ModeState(mode, installed ? ModeStatus.UpToDate : ModeStatus.NotInstalled, active, null);
        }

        if (!installed)
            return new ModeState(mode, ModeStatus.NotInstalled, active, release.TagName);

        // Compare the payload DLL hash against the release asset digest.
        string? remoteDigest = AssetDigest(release, payloadDll);
        if (remoteDigest is null)
        {
            // No per-file digest in the release; fall back to zip-level check (already installed → assume ok).
            return new ModeState(mode, ModeStatus.UpToDate, active, release.TagName);
        }

        string localHash = AssetHash.OfFile(localPath);
        bool upToDate = localHash.Equals(remoteDigest, StringComparison.OrdinalIgnoreCase);
        return new ModeState(mode, upToDate ? ModeStatus.UpToDate : ModeStatus.UpdateAvailable, active, release.TagName);
    }

    // ── Install / switch ─────────────────────────────────────────────

    /// <summary>Download + verify a mode's files, place them in the Steam root, remove the other mode's
    /// unique files, and persist the selection. Best-effort per file (locked files land in Failed).</summary>
    public async Task<ModeInstallResult> InstallAsync(
        UnlockerMode mode, IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        var def = Def(mode);

        // Custom: selecting it is the whole operation. Nothing is downloaded, nothing is written to
        // the Steam root, and whatever the user has installed is left exactly as it is.
        if (def.Kind == ModeKind.Manual)
        {
            settings.SelectedMode = mode.ToString();
            return ModeInstallResult.Ok();
        }

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        // Resolve the build to install from the same (cached) release the status was based
        // on, so what installs matches what was shown.
        var release = await FetchReleaseAsync(def, forceRefresh: false, ct);
        if (release is null)
            return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
        string version = release.TagName;

        string staging = Path.Combine(Path.GetTempPath(), "ElementGui", "mode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            // 1. Stage + verify into temp.
            Dictionary<string, string> staged; // filename → staged path
            string? zipDigest = null;
            {
                // Read the zip off the release (same "<name>-<version>-Release.zip" shape).
                var asset = FindZipAsset(def, release);
                if (asset is null) return ModeInstallResult.Fail(Resources.Strings.Err_ReleaseMissingDownload);
                string zipName = asset.Name;
                string zipUrl = asset.DownloadUrl;
                string? wantedZipDigest = AssetHash.ParseDigest(asset.Digest);

                string zipPath = Path.Combine(staging, zipName);
                await DownloadToFileAsync(zipUrl, zipPath, progress, ct);

                zipDigest = AssetHash.OfFile(zipPath);
                if (wantedZipDigest is { } want && !zipDigest.Equals(want, StringComparison.OrdinalIgnoreCase))
                    return ModeInstallResult.Fail(Resources.Strings.Err_VerifyFailed);

                staged = ExtractWanted(zipPath, def.PlaceFiles, staging);
                var missing = def.PlaceFiles.Where(f => !staged.ContainsKey(f)).ToList();
                if (missing.Count > 0)
                    return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_DownloadMissingFiles, string.Join(", ", missing)));

            }

            // 2. Copy verified files into the Steam root (overwrite). Locked files → Failed (Steam running).
            var failed = new List<string>();
            foreach (string file in def.PlaceFiles)
            {
                try
                {
                    string dest = Path.Combine(root, file);
                    File.Copy(staged[file], dest, overwrite: true);
                    StampNow(dest);
                }
                catch
                {
                    failed.Add(file);
                }
            }

            // 3. This mode is now the active one. (No cleanup of other modes' files. Just overwrite.)
            settings.SelectedMode = mode.ToString();

            // Record the installed zip digest/version for reference (the up-to-date check uses per-DLL
            // hashes, not this). Both remaining install modes are OpenSteamTool-derived, so both want
            // their config pointed at stplug-in.
            cache.OpenSteamToolsInstalledZipDigest = zipDigest;
            cache.OpenSteamToolsInstalledVersion = version;
            try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }

            return failed.Count > 0
                ? new ModeInstallResult(false, string.Format(Resources.Strings.Err_WriteFailedCount, failed.Count), failed)
                : ModeInstallResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled);
        }
        catch (Exception ex)
        {
            return ModeInstallResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── First-run auto-detect ────────────────────────────────────────

    /// <summary>
    /// One-time detection of an already-installed mode when none is selected yet. Hashes the on-disk
    /// DLLs against published digests, in priority order:
    ///   1. Bst: OpenSteamTool.dll vs the BST update manifest's sha256.
    ///   2. Ost (nightly): OpenSteamTool.dll vs any madoiscool/OST-Nightly release asset.
    ///   3. Ost (stable): dwmapi.dll AND xinput1_4.dll vs mendy-tools tag "ost-" (loose-DLL mirror;
    ///      OST ships a zip whose API digest isn't per-DLL, so we mirror the DLLs for hash-matching).
    ///      Still OST, just the other channel: GetStateAsync will offer the move to nightly.
    ///
    /// EVERY branch requires an EXACT hash match. Do not relax this to "the file exists": SteamTools
    /// shipped the same dwmapi.dll / xinput1_4.dll filenames, so a presence check would silently claim
    /// ex-SteamTools users as OST. Exactly the users ModeMigration deliberately routes to onboarding.
    /// Never auto-selects <see cref="UnlockerMode.Custom"/>; that's an explicit user choice.
    ///
    /// Persists the match as the active mode. Returns the detected mode, or null if nothing matched.
    /// </summary>
    public async Task<UnlockerMode?> DetectActiveModeAsync(CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid) return null;

        UnlockerMode? detected = null;

        // Check for ElementSteam.dll in the Steam root
        string elementDll = Path.Combine(root, "ElementSteam.dll");
        if (File.Exists(elementDll))
        {
            detected = UnlockerMode.Element;
        }

        if (detected is { } m) settings.SelectedMode = m.ToString();
        return detected;
    }

    /// <summary>Digest (hex, no prefix) of a release's same-named asset, or null if absent.</summary>
    private static string? AssetDigest(GithubRelease r, string assetName) =>
        AssetHash.ParseDigest(r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))?.Digest);

    /// <summary>The same-named asset, or null if this release doesn't have it.</summary>
    private static GithubAsset? FindAsset(GithubRelease r, string assetName) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

    // ── OpenSteamTool config ─────────────────────────────────────────

    private const string OstLuaPath = "config/stplug-in";

    /// <summary>
    /// Ensure &lt;Steam&gt;/opensteamtool.toml's [lua] paths array contains "config/stplug-in" so our luas
    /// are loaded. Creates the file/section/array if missing; appends without removing existing paths.
    /// Targeted text edit (preserves comments and other sections). Commented-out lines are ignored.
    /// </summary>
    private static void EnsureOpenSteamToolLuaPath(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");

        // No file → create a minimal one.
        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[lua]\npaths = [\"{OstLuaPath}\"]\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        // Find the active (uncommented) [lua] section header and the bounds of that section.
        int luaHeader = lines.FindIndex(l => IsActiveTableHeader(l, "lua"));
        if (luaHeader < 0)
        {
            // No active [lua] section → append one.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[lua]");
            lines.Add($"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Section runs until the next active table header (or EOF).
        int sectionEnd = lines.FindIndex(luaHeader + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        // Look for an active `paths` key within the section. The array may span multiple lines.
        int pathsStart = -1;
        for (int i = luaHeader + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^paths\s*=")) { pathsStart = i; break; }
        }

        if (pathsStart < 0)
        {
            // [lua] exists but no active paths key → insert one right under the header.
            lines.Insert(luaHeader + 1, $"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Find where the array closes (']'), scanning from pathsStart (handles multi-line arrays).
        int pathsEnd = pathsStart;
        while (pathsEnd < sectionEnd && !lines[pathsEnd].Contains(']')) pathsEnd++;
        if (pathsEnd >= sectionEnd) pathsEnd = sectionEnd - 1; // malformed/unclosed. Best effort

        string block = string.Join("\n", lines.GetRange(pathsStart, pathsEnd - pathsStart + 1));

        // Already present (compare the path token, slashes normalized)? Nothing to do.
        if (Regex.IsMatch(block, @"[""']\s*" + Regex.Escape(OstLuaPath).Replace("/", @"[/\\]+") + @"\s*[""']",
                RegexOptions.IgnoreCase))
            return;

        // Insert our entry just before the closing ']' on the line that has it.
        int closeLine = pathsEnd;
        string line = lines[closeLine];
        int bracket = line.LastIndexOf(']');

        // Insert our entry just before the ']'. Add a comma after existing content unless the array
        // is empty (text before ']' ends right after the opening '[').
        string before = line[..bracket].TrimEnd();
        bool arrayEmpty = Regex.IsMatch(before, @"\[\s*$");
        string newBefore = arrayEmpty
            ? before + $" \"{OstLuaPath}\""
            : before + $", \"{OstLuaPath}\"";
        lines[closeLine] = newBefore + line[bracket..];

        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>True if the line is an active (uncommented) [name] table header.</summary>
    private static bool IsActiveTableHeader(string line, string name)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    /// <summary>True if the line is any active (uncommented) [..] table header.</summary>
    private static bool IsActiveAnyTableHeader(string line)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, @"^\[[^\[].*\]");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<GithubRelease?> FetchReleaseAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        // Serve from cache within the TTL unless a forced refresh is requested.
        if (!forceRefresh
            && _releaseCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.release;

        string url = def.FixedTag is not null
            ? $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/tags/{def.FixedTag}"
            : $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/latest";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _releaseCache[def.Mode] = (release, DateTime.UtcNow);
            return release;
        }
        catch
        {
            return null; // offline / rate-limited / parse error → caller maps to Unknown
        }
    }


    /// <summary>Find the small Release zip (matches the pattern, excludes any Debug build).</summary>
    private static GithubAsset? FindZipAsset(ModeDefinition def, GithubRelease release)
    {
        string wanted = (def.ZipAssetPattern ?? "").Replace("{version}", release.TagName);
        return release.Assets.FirstOrDefault(a =>
                   a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extract just the wanted files from a zip into <paramref name="destDir"/> (flattened).</summary>
    private static Dictionary<string, string> ExtractWanted(string zipPath, string[] wanted, string destDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            string? match = wanted.FirstOrDefault(w => w.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null || result.ContainsKey(match)) continue;
            string dest = Path.Combine(destDir, match);
            entry.ExtractToFile(dest, overwrite: true);
            result[match] = dest;
        }
        return result;
    }

    // Asset download routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
    private Task DownloadToFileAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct) =>
        gh.DownloadAsync(url, destPath, progress, ct);

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }
}
