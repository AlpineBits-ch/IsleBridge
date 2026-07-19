using System.Text.Json;

namespace IsleBridge.Api;

/// <summary>
/// Bridge configuration, loaded from <c>config.json</c> in the working directory.
/// Only <see cref="PluginBasePath"/> is required in the file; everything else has
/// a sensible default so an existing minimal config keeps working.
/// </summary>
public class Config
{
    /// <summary>
    /// On-box path to <c>.../Mods/CnCBridge</c>. All streams live under <c>Saved/</c>.
    /// </summary>
    public string PluginBasePath { get; set; } = ".";

    /// <summary>How often the tail pumps poll the out streams for new data.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Interval (seconds) requested when turning the periodic stats stream on.</summary>
    public int StatsInterval { get; set; } = 5;

    /// <summary>How often the flood-guard flag (<c>stats.on</c>) is refreshed while readers are attached.</summary>
    public int StatsHeartbeatMs { get; set; } = 5000;

    private string Saved(string file) => Path.Combine(PluginBasePath, "Saved", file);

    public string InboxPath => Saved("inbox.ndjson");
    public string ResultsPath => Saved("results.ndjson");
    public string EventsPath => Saved("events.ndjson");
    public string ChatPath => Saved("chat.ndjson");
    public string StatsPath => Saved("stats.ndjson");
    public string StatsOnPath => Saved("stats.on");

    public static Config Get()
    {
        return JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json")) ?? new Config();
    }
}
