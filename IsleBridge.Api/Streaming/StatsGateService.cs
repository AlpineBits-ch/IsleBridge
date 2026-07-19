using System.Text.Json.Nodes;
using IsleBridge.Api.InboxWriter;

namespace IsleBridge.Api.Streaming;

/// <summary>
/// Implements the contract §7 flood guard. While at least one client is subscribed to the
/// stats stream, this turns the periodic stats stream on and keeps the reader flag
/// (<c>Saved/stats.on</c>) fresh so the plugin keeps emitting. When the last reader leaves
/// it turns the stream off and deletes the flag, so stats can never flood the disk with
/// nobody listening.
/// </summary>
public sealed class StatsGateService(
    StreamHub statsHub,
    IInboxWriter inbox,
    Config config,
    ILogger<StatsGateService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.StatsHeartbeatMs));
        var active = false;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var hasReaders = statsHub.SubscriberCount > 0;

                if (hasReaders && !active)
                {
                    logger.LogInformation("Stats readers attached; enabling stream");
                    await SendStatsMode("on", config.StatsInterval, stoppingToken);
                }

                if (hasReaders)
                    WriteFlag();

                if (!hasReaders && active)
                {
                    logger.LogInformation("No stats readers; disabling stream");
                    await SendStatsMode("off", null, stoppingToken);
                    DeleteFlag();
                }

                active = hasReaders;
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            if (active)
            {
                await SendStatsMode("off", null, CancellationToken.None);
                DeleteFlag();
            }
        }
    }

    private Task SendStatsMode(string mode, int? interval, CancellationToken ct)
    {
        var args = new JsonObject { ["mode"] = mode };
        if (interval is not null) args["interval"] = interval;

        var envelope = new JsonObject
        {
            ["id"] = Guid.CreateVersion7().ToString(),
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["verb"] = "stats",
            ["args"] = args
        };
        return inbox.AppendAsync(envelope, ct);
    }

    private void WriteFlag()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config.StatsOnPath)!);
            File.WriteAllText(config.StatsOnPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to refresh stats.on flag");
        }
    }

    private void DeleteFlag()
    {
        try
        {
            if (File.Exists(config.StatsOnPath)) File.Delete(config.StatsOnPath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to delete stats.on flag");
        }
    }
}
