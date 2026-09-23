using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ElementGui.Models;
using ElementGui.Services.Downloads;

namespace ElementGui.Services;

/// <summary>
/// Typed client for the Hubcap API (hubcapmanifest.com).
/// Limited by daily quota: /status and /search are free, /lua and /manifest count toward usage.
/// </summary>
public class HubcapApiClient()
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.HubcapApiBaseUrl),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Active Hubcap key: the machine-local build secret (env-baked, never in the repo).</summary>
    public string? ActiveKey => string.IsNullOrWhiteSpace(BuildSecrets.HubcapKey) ? null : BuildSecrets.HubcapKey;

    public bool HasKey => !string.IsNullOrWhiteSpace(ActiveKey);

    private void AddAuth(HttpRequestMessage req)
    {
        if (ActiveKey is { Length: > 0 } key)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public record HubcapStatus(
        bool Available,
        bool ManifestExists,
        string? GameName,
        string? Error);

    /// <summary>
    /// Free check: does Hubcap have this app? No download, no quota spent.
    /// </summary>
    public async Task<HubcapStatus> GetStatusAsync(string appid, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status/{Uri.EscapeDataString(appid)}");
            AddAuth(req);
            var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new HubcapStatus(false, false, null, null);
            if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                return new HubcapStatus(false, false, null, "invalid_key");
            if (res.StatusCode == (HttpStatusCode)429)
                return new HubcapStatus(false, false, null, "limit");
            if (!res.IsSuccessStatusCode)
                return new HubcapStatus(false, false, null, $"http_{(int)res.StatusCode}");

            var data = JsonSerializer.Deserialize<HubcapStatusResponse>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (data is null)
                return new HubcapStatus(false, false, null, "parse");
            bool available = string.Equals(data.Status, "available", StringComparison.OrdinalIgnoreCase)
                && data.ManifestFileExists;
            return new HubcapStatus(available, data.ManifestFileExists, data.GameName, null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new HubcapStatus(false, false, null, "offline");
        }
    }

    /// <summary>
    /// Download the full Lua to a temp/staging file to prove the app is in the DB.
    /// Counts toward daily usage. Returns null when Hubcap has no lua (404).
    /// </summary>
    public async Task<DownloadedFile?> DownloadLuaTempAsync(string appid, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/lua/{Uri.EscapeDataString(appid)}");
        AddAuth(req);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            throw new ApiException("Your Hubcap key is invalid or expired.", res.StatusCode);
        if (res.StatusCode == (HttpStatusCode)429)
            throw new ApiException("Your Hubcap daily limit has been reached.", res.StatusCode);
        if (!res.IsSuccessStatusCode)
        {
            string? detail = await TryReadErrorAsync(res, ct);
            throw new ApiException($"Hubcap download failed ({(int)res.StatusCode}{(detail is not null ? $" — {detail}" : "")})", res.StatusCode);
        }

        // Save as a temp lua so Fetch can keep it and Download can move it to the folder location.
        var staged = await HttpFileDownloader.SaveResponseAsync(res, $"{appid}_hubcap_temp.lua", null, ct);
        // Ensure .lua extension even if server sent a generic name.
        if (!staged.FilePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            string withExt = staged.FilePath + ".lua";
            try { File.Move(staged.FilePath, withExt, overwrite: true); } catch { /* keep original */ }
            if (File.Exists(withExt))
                return new DownloadedFile(withExt, $"{appid}.lua");
        }
        return staged;
    }

    /// <summary>
    /// Download the game manifest ZIP for an app (individual, per-app download).
    /// Counts toward daily usage. Returns null when Hubcap has no manifest (404).
    /// </summary>
    public async Task<DownloadedFile?> DownloadManifestTempAsync(string appid, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/manifest/{Uri.EscapeDataString(appid)}");
        AddAuth(req);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            throw new ApiException("Your Hubcap key is invalid or expired.", res.StatusCode);
        if (res.StatusCode == (HttpStatusCode)429)
            throw new ApiException("Your Hubcap daily limit has been reached.", res.StatusCode);
        if (!res.IsSuccessStatusCode)
        {
            string? detail = await TryReadErrorAsync(res, ct);
            throw new ApiException($"Hubcap download failed ({(int)res.StatusCode}{(detail is not null ? $" — {detail}" : "")})", res.StatusCode);
        }

        var staged = await HttpFileDownloader.SaveResponseAsync(res, $"{appid}_hubcap_manifest.zip", null, ct);
        // Ensure .zip extension even if server sent a generic name.
        if (!staged.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string withExt = staged.FilePath + ".zip";
            try { File.Move(staged.FilePath, withExt, overwrite: true); } catch { /* keep original */ }
            if (File.Exists(withExt))
                return new DownloadedFile(withExt, $"{appid}.zip");
        }
        return staged;
    }

    /// <summary>
    /// Generate a single depot manifest (binary .manifest) via Hubcap, straight
    /// from Steam. Counts toward generation limits. Writes the bytes to
    /// <paramref name="destPath"/> and returns true. Returns false when Hubcap
    /// has no manifest for this depot (404); throws on an invalid key or an
    /// exhausted quota so the caller can surface it.
    /// </summary>
    public async Task<bool> GenerateDepotManifestAsync(long depotId, string manifestId, string destPath, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/generate/manifest?depot_id={depotId}&manifest_id={Uri.EscapeDataString(manifestId)}");
        AddAuth(req);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return false;
        if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            throw new ApiException("Your Hubcap key is invalid or expired.", res.StatusCode);
        if (res.StatusCode == (HttpStatusCode)429)
            throw new ApiException("Your Hubcap daily limit has been reached.", res.StatusCode);
        if (!res.IsSuccessStatusCode)
            return false;
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
            return false;
        await File.WriteAllBytesAsync(destPath, bytes, ct);
        return true;
    }

    public record HubcapUsage(int Used, int Limit);

    /// <summary>
    /// Free: current daily usage and limit. Returns null when unreachable/unauthorized.
    /// </summary>
    public async Task<HubcapUsage?> GetUserStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/user/stats");
            AddAuth(req);
            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return null;
            var data = JsonSerializer.Deserialize<HubcapStatsResponse>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (data is null)
                return null;
            return new HubcapUsage(data.DailyUsage, data.DailyLimit);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public record HubcapContents(bool ZipExists, int ManifestCount);

    /// <summary>
    /// Free: list manifests inside the zip Hubcap holds, without downloading it.
    /// No quota spent. Null when unreachable/unauthorized.
    /// </summary>
    public async Task<HubcapContents?> GetManifestContentsAsync(string appid, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/manifest/{Uri.EscapeDataString(appid)}/contents");
            AddAuth(req);
            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return null;
            var data = JsonSerializer.Deserialize<HubcapContentsResponse>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (data is null)
                return null;
            return new HubcapContents(data.ZipExists, data.ManifestCount);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private sealed class HubcapStatusResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string? Status { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("manifest_file_exists")] public bool ManifestFileExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("game_name")] public string? GameName { get; set; }
    }

    private sealed class HubcapContentsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("zip_exists")] public bool ZipExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("manifest_count")] public int ManifestCount { get; set; }
    }

    private sealed class HubcapStatsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("daily_usage")] public int DailyUsage { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("daily_limit")] public int DailyLimit { get; set; }
    }

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
        catch { /* not JSON */ }
        return null;
    }
}
