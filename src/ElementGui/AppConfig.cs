namespace ElementGui;

/// <summary>
/// Compiled-in client configuration for the Element app using the Ryuu API.
/// </summary>
public static class AppConfig
{
    public const string ApiBaseUrl = "https://generator.ryuu.lol";
    public const string AuthKey = "waauFN2MpVidOhKX";

    // Public upstream APIs the app calls directly (no proxy needed for guest browsing).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets).
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Steamless (atom0s): strips SteamStub DRM from a game's .exe.
    public const string SteamlessRepo = "atom0s/Steamless";

    // CloudRedirect (Selectively11): the Mode page "Manage" button downloads the latest CloudRedirect.exe.
    public const string CloudRedirectRepo = "Selectively11/CloudRedirect";

    // SteamAutoCrack: the Downloads page button fetches this release and launches its GUI.
    public const string SteamAutoCrackRepo = "SteamAutoCracks/Steam-auto-crack";

    // DepotDownloaderMod: downloads raw depot content from Steam's CDN using depot keys + a local manifest.
    public const string DepotDownloaderRepo = "mendy-tools/DepotDownloaderMod";

    // ── Umami analytics (anonymous app-launch counting) ──────────────
    public const string UmamiHost = "https://analytics.lua.tools";
    public const string UmamiWebsiteId = "820d782c-a434-424f-9f90-dee83dc6032e";
    public const string UmamiHostname = "desktop.lua.tools";

    /// <summary>
    /// Public GitHub repos hosting Velopack release assets, in priority order.
    /// </summary>
    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/thecloudyy/Element",
    ];

    public static string GithubReleasesRepo => GithubReleasesRepos[0];

    // Plugin releases
    public const string PluginReleasesOwner = "thecloudyy";
    public const string PluginReleasesRepo = "Element";

    // GitHub proxy mirrors
    public static readonly string[] GithubApiMirrors =
    [
    ];
    public static readonly string[] GithubDownloadMirrors =
    [
        "https://ghproxy.net/",
        "https://ghfast.top/",
        "https://gh.ddlc.top/",
    ];
}
