using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElementGui.Services.Downloads;

namespace ElementGui.Services;

/// <summary>One fix zip from the OnlineFixes GitHub repo.</summary>
public sealed record OnlineFixEntry(
    long AppId,
    string GameName,
    string FileName,
    string DownloadUrl,
    long Size);

/// <summary>One game in the OnlineFixes listing (one folder per appid).</summary>
public sealed record OnlineFixGame(
    long AppId,
    string Name,
    IReadOnlyList<OnlineFixEntry> Fixes)
{
    public string HeaderImage => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
}

/// <summary>
/// Fixes source backed ONLY by the GitHub repo thecloudyy/OnlineFixes (branch main).
/// Layout: /&lt;appid&gt;/&lt;Game Name&gt; - fix.zip (one or more zips per appid).
/// No lua.tools APIs are used for fixes.
/// </summary>
public class OnlineFixesService
{
    private readonly HttpClient _api = new()
    {
        BaseAddress = new Uri("https://api.github.com/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient _dl = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Cached tree (5 min) so listing + per-game detail share one GitHub call.
    private IReadOnlyList<OnlineFixEntry>? _cache;
    private DateTimeOffset _cacheAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OnlineFixesService()
    {
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("ElementGui/1.0");
        _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _dl.DefaultRequestHeaders.UserAgent.ParseAdd("ElementGui/1.0");
    }

    private sealed class GitTreeResponse
    {
        [JsonPropertyName("tree")] public List<GitTreeItem> Tree { get; set; } = [];
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    }

    private sealed class GitTreeItem
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("size")] public long? Size { get; set; }
    }

    /// <summary>Every fix zip in the repo (cached). Empty on offline / API error.</summary>
    public async Task<IReadOnlyList<OnlineFixEntry>> GetAllFixesAsync(CancellationToken ct = default)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cacheAt < CacheTtl)
            return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheAt < CacheTtl)
                return _cache;

            string url = $"repos/{AppConfig.OnlineFixesOwner}/{AppConfig.OnlineFixesRepo}/git/trees/{AppConfig.OnlineFixesBranch}?recursive=1";
            using var res = await _api.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return _cache ?? [];
            var data = JsonSerializer.Deserialize<GitTreeResponse>(
                await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (data?.Tree is null) return _cache ?? [];

            var list = new List<OnlineFixEntry>();
            foreach (var item in data.Tree)
            {
                if (item.Type != "blob") continue;
                if (TryParseEntry(item.Path, item.Size ?? 0, out var entry) && entry is not null)
                    list.Add(entry);
            }

            _cache = list;
            _cacheAt = DateTimeOffset.UtcNow;
            return _cache;
        }
        catch
        {
            return _cache ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Games with at least one fix, ordered by name.</summary>
    public async Task<IReadOnlyList<OnlineFixGame>> GetGamesAsync(CancellationToken ct = default)
    {
        var all = await GetAllFixesAsync(ct);
        return all.GroupBy(f => f.AppId)
            .Select(g => new OnlineFixGame(
                g.Key,
                g.Select(f => f.GameName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? g.Key.ToString(),
                g.OrderBy(f => f.FileName).ToList()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fix zips for one appid (empty when none).</summary>
    public async Task<IReadOnlyList<OnlineFixEntry>> GetFixesAsync(long appId, CancellationToken ct = default)
    {
        var all = await GetAllFixesAsync(ct);
        return all.Where(f => f.AppId == appId).OrderBy(f => f.FileName).ToList();
    }

    public void InvalidateCache() => _cache = null;

    /// <summary>Download one fix zip from raw.githubusercontent.com (no auth).</summary>
    public async Task<DownloadedFile> DownloadFixAsync(
        OnlineFixEntry fix,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        using var res = await _dl.GetAsync(fix.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException($"Download failed ({(int)res.StatusCode}).", res.StatusCode);
        return await HttpFileDownloader.SaveResponseAsync(res, fix.FileName, progress, ct);
    }

    internal static bool TryParseEntry(string path, long size, out OnlineFixEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Expected: "<appid>/<something>.zip"
        int slash = path.IndexOf('/');
        if (slash <= 0) return false;
        if (!long.TryParse(path[..slash], out long appId) || appId <= 0) return false;
        string file = path[(slash + 1)..];
        if (file.Contains('/') || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;

        string game = file[..^4];
        const string suffix = " - fix";
        if (game.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            game = game[..^suffix.Length];
        game = game.Trim();
        if (game.Length == 0) game = appId.ToString();

        string download = $"https://raw.githubusercontent.com/{AppConfig.OnlineFixesOwner}/{AppConfig.OnlineFixesRepo}/{AppConfig.OnlineFixesBranch}/{appId}/{Uri.EscapeDataString(file)}";
        entry = new OnlineFixEntry(appId, game, file, download, size);
        return true;
    }
}
