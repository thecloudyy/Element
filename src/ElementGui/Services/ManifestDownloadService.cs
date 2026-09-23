using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace ElementGui.Services;

/// <summary>
/// Auto-downloads Steam depot manifest files without a Steam login.
/// Request codes come from the ManifestDeX Code API
/// (<c>GET manifest.manifestdex.com/{gid}</c> with <c>User-Agent: ManifestDeX/1.0</c>,
/// 60 requests/min); the bytes come from Steam's own CDN using SteamKit's client,
/// exactly like DepotDownloader does. Files land in Steam's depotcache as
/// <c>{depotid}_{gid}.manifest</c>, validated with <see cref="ManifestFile"/>.
/// </summary>
public class ManifestDownloadService(
    SteamService steam,
    ILogger<ManifestDownloadService> log)
{
    private const string CodeApi = "https://manifest.manifestdex.com";
    private const string CodeUserAgent = "ManifestDeX/1.0";
    /// <summary>Min gap between code requests (the API allows 60/min).</summary>
    private static readonly TimeSpan CodePacing = TimeSpan.FromSeconds(1.2);

    private static readonly HttpClient _codeHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient _cdnHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly SemaphoreSlim _codeGate = new(1, 1);
    private static DateTime _lastCodeAt = DateTime.MinValue;
    private static List<SteamKit2.CDN.Server>? _servers;

    /// <summary>
    /// Returns the depotcache path for a depot's manifest, downloading it first
    /// when it isn't already there and valid. Null when it can't be obtained
    /// (no manifest id, API/CDN failure). Never throws.
    /// </summary>
    public async Task<string?> EnsureManifestFileAsync(
        long depotId, string manifestId, byte[]? depotKey,
        Action<string>? status, CancellationToken ct = default)
    {
        try
        {
            if (depotId <= 0
                || !ulong.TryParse(manifestId, out ulong gid)
                || gid == 0)
                return null;

            string? dir = steam.DepotCacheDir;
            if (string.IsNullOrEmpty(dir)) return null;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{depotId}_{manifestId}.manifest");

            if (ManifestFile.Matches(path, depotId, manifestId))
                return path;

            status?.Invoke("requesting manifest code");
            ulong code = await RequestCodeAsync(gid, ct);
            if (code == 0) return null;

            status?.Invoke("downloading manifest");
            if (!await TryDownloadAsync((uint)depotId, gid, code, depotKey, path, ct))
                return null;

            return ManifestFile.Matches(path, depotId, manifestId) ? path : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Manifest auto-download failed for depot {Depot} gid {Gid}", depotId, manifestId);
            return null;
        }
    }

    /// <summary>Request code for a manifest gid from the ManifestDeX Code API. 0 when unavailable.</summary>
    private static async Task<ulong> RequestCodeAsync(ulong manifestId, CancellationToken ct)
    {
        await _codeGate.WaitAsync(ct);
        try
        {
            // Pace serially: concurrent jobs share this gate, so the 60/min limit
            // holds process-wide no matter how many depots download at once.
            var gap = CodePacing - (DateTime.UtcNow - _lastCodeAt);
            if (gap > TimeSpan.Zero)
                await Task.Delay(gap, ct);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{CodeApi}/{manifestId}");
                    req.Headers.UserAgent.ParseAdd(CodeUserAgent);
                    using var res = await _codeHttp.SendAsync(req, ct);
                    _lastCodeAt = DateTime.UtcNow;
                    if ((int)res.StatusCode == 429)
                    {
                        var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                        if (wait > TimeSpan.FromSeconds(90)) return 0;
                        await Task.Delay(wait, ct);
                        continue;
                    }
                    if (!res.IsSuccessStatusCode) return 0;
                    var text = (await res.Content.ReadAsStringAsync(ct)).Trim();
                    if (ulong.TryParse(text, out ulong code) && code != 0)
                        return code;
                    return 0;
                }
                catch (OperationCanceledException) { throw; }
                catch { return 0; }
            }
            return 0;
        }
        finally
        {
            _codeGate.Release();
        }
    }

    private static async Task<List<SteamKit2.CDN.Server>> GetServersAsync(CancellationToken ct)
    {
        lock (_codeGate)
        {
            if (_servers is { Count: > 0 }) return _servers;
        }

        var found = await FetchServersAnonymouslyAsync(ct);
        if (found.Count == 0) return [];
        lock (_codeGate)
        {
            _servers = found;
            return _servers;
        }
    }

    /// <summary>Content-server list via a throwaway anonymous Steam session.
    /// Element itself never logs into SteamKit; this connects, reads the list,
    /// and disconnects. Cached per process run.</summary>
    private static async Task<List<SteamKit2.CDN.Server>> FetchServersAnonymouslyAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var client = new SteamClient();
        var mgr = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            try { user?.LogOnAnonymous(); }
            catch { tcs.TrySetResult(false); }
        });
        mgr.Subscribe<SteamUser.LoggedOnCallback>(cb => tcs.TrySetResult(cb.Result == EResult.OK));
        mgr.Subscribe<SteamClient.DisconnectedCallback>(_ => tcs.TrySetResult(false));

        var pump = Task.Run(async () =>
        {
            while (!linked.Token.IsCancellationRequested)
            {
                try { mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(500)); } catch { }
                try { await Task.Delay(100, linked.Token); } catch { break; }
            }
        }, linked.Token);

        try
        {
            client.Connect();
            if (!await tcs.Task.WaitAsync(linked.Token)) return [];
            var content = client.GetHandler<SteamContent>();
            if (content == null) return [];
            var list = await content.GetServersForSteamPipe().WaitAsync(linked.Token);
            return list.Where(s => !s.SteamChinaOnly && !string.IsNullOrEmpty(s.Host))
                .OrderBy(s => s.WeightedLoad)
                .ToList();
        }
        catch { return []; }
        finally
        {
            try { linked.Cancel(); } catch { }
            try { await pump; } catch { }
            try { client.Disconnect(); } catch { }
        }
    }

    private static async Task<bool> TryDownloadAsync(
        uint depotId, ulong manifestId, ulong code, byte[]? depotKey,
        string path, CancellationToken ct)
    {
        // LanCache first: plain HTTP, no Steam session or server discovery.
        // Discovered content servers are the fallback.
        var lanBytes = await TryDownloadFromLanCacheAsync(depotId, manifestId, code, ct);
        if (lanBytes is not null)
        {
            await File.WriteAllBytesAsync(path, lanBytes, ct);
            return true;
        }

        List<SteamKit2.CDN.Server> servers;
        try
        {
            servers = await GetServersAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { servers = []; }

        foreach (var server in servers.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The client is config-only here (no login needed): it just
                // builds the http://host/depot/{id}/manifest/{gid}/5/{code} call.
                using var cdn = new SteamKit2.CDN.Client(new SteamClient());
                var manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, server, depotKey);
                using var ms = new MemoryStream();
                manifest.Serialize(ms);
                var bytes = ms.ToArray();
                if (bytes.Length == 0) continue;
                await File.WriteAllBytesAsync(path, bytes, ct);
                return true;
            }
            catch { /* next server */ }
        }
        return false;
    }

    /// <summary>
    /// Raw LanCache fetch: the exact URL shape SteamKit itself builds
    /// (<c>http://lancache.steamcontent.com/depot/{id}/manifest/{gid}/5/{code}</c>),
    /// unzipped to the raw protobuf manifest bytes. Null on any failure.
    /// </summary>
    private static async Task<byte[]?> TryDownloadFromLanCacheAsync(
        uint depotId, ulong manifestId, ulong code, CancellationToken ct)
    {
        try
        {
            var url = $"http://lancache.steamcontent.com/depot/{depotId}/manifest/{manifestId}/5/{code}";
            using var res = await _cdnHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return null;
            using var ms = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault();
            if (entry == null) return null;
            using var es = entry.Open();
            using var outMs = new MemoryStream();
            await es.CopyToAsync(outMs, ct);
            return outMs.Length == 0 ? null : outMs.ToArray();
        }
        catch { return null; }
    }

    /// <summary>The depotcache path a manifest would live at, or null without a Steam folder.</summary>
    public string? ManifestPathFor(long depotId, string manifestId)
    {
        string? dir = steam.DepotCacheDir;
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, $"{depotId}_{manifestId}.manifest");
    }

    /// <summary>
    /// Bulk-fetch pins: skips what's already cached and valid, downloads the rest.
    /// Returns (downloaded, skipped, failed) plus one short line per failure.
    /// </summary>
    public async Task<(int ok, int skipped, int fail, List<string> failures)> EnsureManyAsync(
        IEnumerable<(long DepotId, string ManifestId, string? Key)> pins,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        var distinct = pins
            .Where(p => p.DepotId > 0 && ulong.TryParse(p.ManifestId, out ulong g) && g != 0)
            .Distinct()
            .ToList();

        int done = 0, ok = 0, skipped = 0, fail = 0;
        var failures = new List<string>();
        using var gate = new SemaphoreSlim(3);
        try
        {
            var tasks = distinct.Select(async pin =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    string? path = ManifestPathFor(pin.DepotId, pin.ManifestId);
                    if (path is not null && ManifestFile.Matches(path, pin.DepotId, pin.ManifestId))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                    byte[]? key = null;
                    try
                    {
                        if (!string.IsNullOrEmpty(pin.Key) && pin.Key.Length == 64)
                            key = Convert.FromHexString(pin.Key);
                    }
                    catch { key = null; }
                    string? got = await EnsureManifestFileAsync(pin.DepotId, pin.ManifestId, key, null, ct);
                    if (got is not null) Interlocked.Increment(ref ok);
                    else
                    {
                        Interlocked.Increment(ref fail);
                        lock (failures) failures.Add($"{pin.DepotId} / {pin.ManifestId}");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref fail);
                    lock (failures) failures.Add($"{pin.DepotId}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                    progress?.Report((double)Interlocked.Increment(ref done) / distinct.Count);
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            gate.Dispose();
        }
        failures.Sort(StringComparer.Ordinal);
        log.LogDebug("Manifest bulk: {Ok} ok, {Skipped} skipped, {Fail} failed", ok, skipped, fail);
        return (ok, skipped, fail, failures);
    }
}
