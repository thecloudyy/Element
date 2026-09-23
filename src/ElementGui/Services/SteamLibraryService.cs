using System.IO;
using System.Text.RegularExpressions;

namespace ElementGui.Services;

/// <summary>
/// Resolves where a Steam game is installed on disk by walking Valve's KeyValues files:
/// registry Steam root → steamapps\libraryfolders.vdf (every library, possibly across drives) →
/// per-library steamapps\appmanifest_&lt;appid&gt;.acf (the game's installdir) → common\&lt;installdir&gt;.
/// Falls back to a user-picked manual folder (Fixes popup, persisted in settings).
/// Best-effort: returns null if Steam/the game can't be located. Used to apply online-fix zips.
/// </summary>
public partial class SteamLibraryService(SteamService steam, SettingsService settings)
{
    // "path"        "D:\\SteamLibrary"     → the library root (libraryfolders.vdf)
    // "installdir"  "Elden Ring"           → folder under steamapps\common (appmanifest_*.acf)
    // Values are quoted; backslashes are escaped (\\). One key per line.
    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex(@"""appid""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>An installed game, as its appmanifest describes it.</summary>
    public record InstalledGame(long AppId, string Name, string InstallDir);

    /// <summary>
    /// Full path to the game's install folder: auto-detected via Steam libraries first, then a
    /// user-picked manual override. Null when neither finds it on disk.
    /// </summary>
    public string? GetInstallDir(long appId)
    {
        return GetAutoInstallDir(appId) ?? settings.GetManualInstallDir(appId);
    }

    /// <summary>Auto-detected install folder via Steam libraries only (no manual override).</summary>
    public string? GetAutoInstallDir(long appId)
    {
        try
        {
            string? steamRoot = steam.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                var m = InstallDirRegex().Match(File.ReadAllText(acf));
                if (!m.Success) continue;

                string installDir = Unescape(m.Groups[1].Value);
                string full = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(full)) return full;
            }
        }
        catch { /* unreadable VDF/ACF or odd path. Treat as not found */ }
        return null;
    }

    /// <summary>
    /// Every installed game across every library, from the appmanifests. Lazy, and skips anything whose
    /// folder isn't actually on disk.
    /// </summary>
    /// <remarks>
    /// Enumerating the .acf files is the cheap part (~39 ms across three drives here); what costs is
    /// whatever the caller then does per game. Yields rather than returning a list so a caller looking
    /// for one thing can stop early.
    /// </remarks>
    public IEnumerable<InstalledGame> EnumerateInstalled()
    {
        string? steamRoot = steam.EffectivePath;
        if (steamRoot is null) yield break;

        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string steamapps = Path.Combine(library, "steamapps");
            string[] acfs;
            try
            {
                if (!Directory.Exists(steamapps)) continue;
                acfs = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            }
            catch { continue; }

            foreach (string acf in acfs)
            {
                InstalledGame? game = null;
                try
                {
                    string text = File.ReadAllText(acf);

                    var idm = AppIdRegex().Match(text);
                    var dirm = InstallDirRegex().Match(text);
                    if (!idm.Success || !dirm.Success) continue;
                    if (!long.TryParse(idm.Groups[1].Value, out long appId)) continue;

                    string full = Path.Combine(steamapps, "common", Unescape(dirm.Groups[1].Value));
                    if (!Directory.Exists(full)) continue;

                    var nm = NameRegex().Match(text);
                    game = new InstalledGame(appId, nm.Success ? nm.Groups[1].Value : appId.ToString(), full);
                }
                catch { /* unreadable or malformed acf: skip this one, not the whole library */ }

                if (game is not null) yield return game;
            }
        }
    }

    /// <summary>Every Steam library root (the main install plus any added libraries).</summary>
    private static IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        // The main install is always a library.
        yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (Match m in PathRegex().Matches(text))
        {
            string path = Unescape(m.Groups[1].Value);
            // The main root often appears here too; harmless duplicate (we just probe each).
            if (!string.Equals(path, steamRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>VDF strings escape backslashes as "\\"; collapse to a real path.</summary>
    private static string Unescape(string s) => s.Replace(@"\\", @"\");

    /// <summary>
    /// Resolve the install folder, asking the user to pick it with a folder popup when auto-detect
    /// (and any saved manual override) misses. The picked folder is persisted for next time.
    /// Returns null when the user cancels. Must be called on the UI thread (shows a dialog).
    /// </summary>
    public string? ResolveInstallDirWithPrompt(long appId, string gameName)
    {
        string? dir = GetInstallDir(appId);
        if (dir is not null && Directory.Exists(dir)) return dir;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = string.Format(Resources.Strings.Fixes_Select_Game_Folder_Title, gameName),
            InitialDirectory = settings.SteamPathOverride ?? steam.EffectivePath,
        };
        if (dialog.ShowDialog() != true) return null;

        string picked = dialog.FolderName;
        if (!Directory.Exists(picked)) return null;

        // Sanity check: a game folder almost always has executables at its root. Persisting a
        // wrong pick (Documents, another game) would redirect every future fix extract there,
        // so confirm before saving it. Must run on the UI thread (callers are UI commands).
        if (!LooksLikeGameFolder(picked)
            && System.Windows.MessageBox.Show(
                string.Format(Resources.Strings.Fixes_Select_Game_Folder_WarnBody, picked),
                Resources.Strings.Fixes_Select_Game_Folder_WarnTitle,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return null;

        settings.SetManualInstallDir(appId, picked);
        return picked;
    }

    /// <summary>True when the folder has executables at its root (best-effort game-folder sniff).</summary>
    private static bool LooksLikeGameFolder(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe").Any(); }
        catch { return true; } // unreadable: don't block, the write surfaces the real error
    }

    /// <summary>Forget a user-picked manual folder (e.g. game moved).</summary>
    public void ClearManualDir(long appId) => settings.ClearManualInstallDir(appId);
}
