namespace ElementGui;

/// <summary>
/// Compiled-in client configuration for the Element app using the Ryuu API.
/// </summary>
public static class AppConfig
{
    public const string HubcapApiBaseUrl = "https://hubcapmanifest.com";
    public const string ApiBaseUrl = "https://generator.ryuu.lol";
    public const string LuaToolsApiBaseUrl = "https://lua.tools";
    public const string AuthKey = "hqGqlo1bw6aWFl08";
    public const string HubcapKey = "smm_da41ecae4378061052ce32dc357c9ae118c5f64cf6aab072c08c606c57d7558afe894d27e1eb14f90c521752894eebed";

    // Public upstream APIs the app calls directly (no proxy needed for guest browsing).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets).
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Steamless (atom0s): strips SteamStub DRM from a game's .exe.
    public const string SteamlessRepo = "atom0s/Steamless";

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
        "https://github.com/thecloudyy/OpenSteamTool",
        "https://github.com/thecloudyy/ManifestDeXCore",
    ];

    public static string GithubReleasesRepo => GithubReleasesRepos[0];

    // Plugin releases
    public const string PluginReleasesOwner = "thecloudyy";
    public const string PluginReleasesRepo = "ManifestDeXCore";

    // GitHub proxy mirrors
    public static readonly string[] GithubApiMirrors =
    [
        "https://gh-proxy.com/",
    ];
    public static readonly string[] GithubDownloadMirrors =
    [
        "https://gh-proxy.com/",
        "https://gh.ddlc.top/",
    ];
}
