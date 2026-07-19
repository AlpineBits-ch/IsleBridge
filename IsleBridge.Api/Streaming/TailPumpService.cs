namespace IsleBridge.Api.Streaming;

/// <summary>
/// Background pump that tails one out stream and publishes each new NDJSON line to a
/// <see cref="StreamHub"/>. One instance per stream (chat / events / stats / results).
/// </summary>
public sealed class TailPumpService(
    string name,
    string path,
    int pollIntervalMs,
    StreamHub hub,
    ILogger<TailPumpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tailer = new NdjsonTailer(path);
        tailer.SeekToEnd(); // live tail: only lines written from now on

        logger.LogInformation("Tailing {Stream} at {Path}", name, path);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollIntervalMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var line in tailer.Poll())
                    hub.Publish(line);
            }
            catch (Exception ex)
            {
                // A transient IO race (mid-rotation, plugin rename) shouldn't kill the pump.
                logger.LogWarning(ex, "Transient error tailing {Stream}", name);
            }
        }
    }
}
