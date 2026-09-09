using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ElementGui.Models;
using ElementGui.Services.Downloads;

namespace ElementGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the Ryuu API (generator.ryuu.lol).</summary>
public class ElementApiClient(SteamAppInfoCache appInfo, CoverCache covers)
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private void AddAuthKey(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("X-Auth-Key", AppConfig.AuthKey);
    }

    // ── Steam public APIs (no auth) ──────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? [])
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return ([], []);
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? [])
                    .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return ([], []); }
    }

    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = covers.EnsureAsync(id, img, CancellationToken.None);
        return details;
    }

    // ── Ryuu API endpoints ───────────────────────────────────────

    /// <summary>
    /// Download a lua file, manifest, or zip for a game from the Ryuu API.
    /// </summary>
    /// <param name="appid">Steam app ID</param>
    /// <param name="fileType">"lua" for .lua, "manifest" for manifest, or omit/null for .zip</param>
    /// <param name="branch">Optional branch name (null = public)</param>
    public async Task<DownloadedFile> DownloadAsync(
        string appid, string? fileType, string? branch,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/download/{appid}";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(fileType)) query.Add($"file_type={Uri.EscapeDataString(fileType)}");
        if (!string.IsNullOrEmpty(branch)) query.Add($"branch={Uri.EscapeDataString(branch)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthKey(req);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string detail = await TryReadErrorAsync(res, ct);
            throw new ApiException(
                $"Download failed: {(int)res.StatusCode}{(detail is not null ? $" — {detail}" : "")}",
                res.StatusCode);
        }

        string ext = fileType == "lua" ? ".lua" : fileType == "manifest" ? ".manifest" : ".zip";
        return await HttpFileDownloader.SaveResponseAsync(res, $"{appid}{ext}", progress, ct);
    }

    /// <summary>
    /// Download a manifest zip for a game (convenience wrapper).
    /// </summary>
    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string? branch,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        => DownloadAsync(appid, "manifest", branch, progress, ct);

    /// <summary>
    /// Download a lua file for a game (convenience wrapper).
    /// </summary>
    public Task<DownloadedFile> DownloadLuaAsync(
        string appid, string? branch,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        => DownloadAsync(appid, "lua", branch, progress, ct);

    /// <summary>Request an update for a game (premium feature).</summary>
    public async Task<(bool ok, string? error)> RequestUpdateAsync(string appid, string? branch = "public", CancellationToken ct = default)
    {
        string url = $"/requestupdate?appid={appid}";
        if (!string.IsNullOrEmpty(branch)) url += $"&branch={Uri.EscapeDataString(branch)}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthKey(req);
        var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return (true, null);
        string? detail = await TryReadErrorAsync(res, ct);
        return (false, detail ?? $"Request failed: {(int)res.StatusCode}");
    }

    /// <summary>Request a game to be added.</summary>
    public async Task<(bool ok, string? error)> RequestGameAsync(string appid, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/request?appid={appid}");
        AddAuthKey(req);
        var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return (true, null);
        string? detail = await TryReadErrorAsync(res, ct);
        return (false, detail ?? $"Request failed: {(int)res.StatusCode}");
    }

    /// <summary>Request a non-public branch to be added.</summary>
    public async Task<(bool ok, string? error)> RequestBranchAsync(string appid, string branch, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/requestbranch?appid={appid}&branch={Uri.EscapeDataString(branch)}");
        AddAuthKey(req);
        var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return (true, null);
        string? detail = await TryReadErrorAsync(res, ct);
        return (false, detail ?? $"Request failed: {(int)res.StatusCode}");
    }

    // ── Plumbing ────────────────────────────────────────────────

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    /// <summary>Try to read {"error":"..."} or {"message":"..."} from a non-success response.</summary>
    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            string body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString();
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
            if (root.TryGetProperty("detail", out var det) && det.ValueKind == JsonValueKind.String)
                return det.GetString();
        }
        catch { /* not JSON or unreadable — fall through */ }
        return null;
    }
}
