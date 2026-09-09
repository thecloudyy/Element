using System.Collections.Concurrent;
using System.IO;
using ElementGui.Models;
using ElementGui.Services.Downloads;

namespace ElementGui.Services;

/// <summary>
/// Headless add pipeline for the Steam store plugin. Mirrors the logic of
/// <c>DownloadViewModel.FetchAsync</c>/<c>DownloadSourceByNameAsync</c>. Dynamic sources,
/// key-gating, but uses only the underlying SERVICES (never the UI view model), so the app
/// window stays silent. The plugin polls state via the HTTP server and picks a source with <see cref="Pick"/>.
/// </summary>
public class PluginAddService(
    ElementApiClient api,
    DownloadQueue queue,
    ManifestJobFactory jobs)
{
    public class SourceRow
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool NeedsKey { get; set; }
        public bool Locked { get; set; }
        public string? Stats { get; set; }
        public bool Downloading { get; set; }
        public double Progress { get; set; }
        public bool Indeterminate { get; set; }
        public bool Available => Status == "available";
        public bool CanDownload => Available && !Locked;
    }

    public class AddState
    {
        public long AppId;
        public string? GameName;
        public bool Checking;
        public bool SourcesLoaded;
        public List<SourceRow> Sources = [];
        public string? InstallStatus;
        public bool InstallFailed;
        public string? Error;
        public bool Busy; // a download+install is running
    }

    private readonly ConcurrentDictionary<long, AddState> _states = new();

    public AddState? GetState(long appId) => _states.TryGetValue(appId, out var s) ? s : null;

    /// <summary>Begin a headless add: check sources and, if auto-download is on, auto-download the best source.
    /// Auto-download off leaves the sources for the plugin to pick.</summary>
    public void Start(long appId, string? gameName = null)
    {
        var state = new AddState
        {
            AppId = appId,
            Checking = true,
            // The store page usually passes the name it already shows, letting CheckAsync skip a
            // lua.tools /details round-trip. Null/blank → CheckAsync fetches it as a fallback.
            GameName = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim(),
        };
        _states[appId] = state;
        PluginLog.Log($"PluginAdd.Start appid={appId} guest=true");
        _ = Task.Run(() => CheckAsync(appId, state));
    }

    /// <summary>Plugin picked a source → download+install it.</summary>
    public void Pick(long appId, string sourceName)
    {
        if (!_states.TryGetValue(appId, out var state))
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> NO STATE (Start not called?)");
            return;
        }
        if (state.Busy)
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> BUSY, ignored");
            return;
        }
        var row = state.Sources.FirstOrDefault(r =>
            string.Equals(r.Name, sourceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.DisplayName, sourceName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> ROW NOT FOUND. have=[{string.Join(", ", state.Sources.Select(s => s.Name))}]");
            return;
        }
        PluginLog.Log($"PluginAdd.Pick appid={appId} source='{row.Name}' canDownload={row.CanDownload} needsKey={row.NeedsKey} -> downloading");
        _ = Task.Run(() => DownloadAsync(appId, state, row));
    }

    private async Task CheckAsync(long appId, AddState state)
    {
        try
        {
            var nameTask = string.IsNullOrEmpty(state.GameName) ? SafeGetGameNameAsync(appId) : null;

            // Source checking is no longer available from the API. Use known sources.
            var statuses = new Dictionary<string, string>
            {
                ["Ryuu"] = "available",
            };

            // Premium (key-gated) sources first, mirroring the website/app ordering.
            var rows = statuses
                .OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0)
                .Select(kv =>
                {
                    var meta = SourceMeta.Get(kv.Key);
                    return new SourceRow
                    {
                        Name = kv.Key,
                        DisplayName = meta.DisplayName ?? kv.Key,
                        Status = kv.Value,
                        NeedsKey = meta.RequiresUserKey,
                    };
                }).ToList();

            if (nameTask is not null) state.GameName = await nameTask;

            PublishSources(state, rows, appId);
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Checking = false;
            PluginLog.Log($"PluginAdd.Check appid={appId} EXCEPTION: {ex}");
        }
    }

    private void PublishSources(AddState state, List<SourceRow> rows, long appId)
    {
        state.Sources = rows;
        state.SourcesLoaded = true;
        state.Checking = false;
        PluginLog.Log($"PluginAdd.Check appid={appId} sources=[{string.Join(", ", rows.Select(r => $"{r.Name}({r.Status},lock={r.Locked})"))}]");
    }

    private async Task<string?> SafeGetGameNameAsync(long appId)
    {
        try { return (await api.GetDetailsAsync(appId.ToString()))?.Name; } catch { return null; }
    }

    /// <summary>
    /// Queue the pick through the shared <see cref="DownloadQueue"/> and mirror its progress into this
    /// service's plain-POCO state, which the store-page popup polls over HTTP.
    /// </summary>
    private async Task DownloadAsync(long appId, AddState state, SourceRow row)
    {
        if (state.Busy) return;
        state.Busy = true;
        state.Error = null;
        state.InstallStatus = null;
        state.InstallFailed = false;
        row.Downloading = true;
        row.Indeterminate = true;
        row.Progress = 0;

        try
        {
            var job = jobs.CreateManifestJob(appId, state.GameName, row.Name, row.NeedsKey);
            var item = queue.Enqueue(job);

            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                row.Indeterminate = item.IsIndeterminate;
                row.Progress = item.Percent;
                row.Downloading = item.IsActive;
            }
            item.PropertyChanged += OnChanged;

            try
            {
                var result = await item.Completion;

                if (result is null || !result.Ok)
                {
                    state.Error = result?.Message ?? item.Message;
                    state.InstallFailed = true;
                    PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' FAILED: {state.Error}");
                }
                else
                {
                    state.InstallStatus = result.Message;
                    PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' OK: {state.InstallStatus}");
                }
            }
            finally { item.PropertyChanged -= OnChanged; }
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.InstallFailed = true;
            PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' EXCEPTION: {ex}");
        }
        finally
        {
            row.Downloading = false;
            row.Indeterminate = false;
            state.Busy = false;
        }
    }
}
