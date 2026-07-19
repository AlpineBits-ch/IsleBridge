using System.Text;
using IsleBridge.Api.Streaming;
using Microsoft.AspNetCore.Mvc;

namespace IsleBridge.Api.Controllers;

/// <summary>
/// Server-Sent Events endpoints for the four read streams. Each connection gets a live
/// tail from the moment it connects; every NDJSON line becomes one <c>data:</c> event
/// carrying the raw JSON. The SDK subscribes and does the typing / correlation.
/// </summary>
[Route("api/v1/stream")]
[ApiController]
public class StreamController(
    [FromKeyedServices(StreamKeys.Chat)] StreamHub chat,
    [FromKeyedServices(StreamKeys.Events)] StreamHub events,
    [FromKeyedServices(StreamKeys.Stats)] StreamHub stats,
    [FromKeyedServices(StreamKeys.Results)] StreamHub results,
    ILogger<StreamController> logger) : ControllerBase
{
    [HttpGet("chat")]
    public Task Chat(CancellationToken ct) => Stream(chat, ct);

    [HttpGet("events")]
    public Task Events(CancellationToken ct) => Stream(events, ct);

    [HttpGet("stats")]
    public Task Stats(CancellationToken ct) => Stream(stats, ct);

    [HttpGet("results")]
    public Task Results(CancellationToken ct) => Stream(results, ct);

    private async Task Stream(StreamHub hub, CancellationToken ct)
    {
        
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = hub.Subscribe();
        var body = Response.Body;

        // Prime the pipe so the client's HttpClient returns headers immediately.
        await body.WriteAsync(": connected\n\n"u8.ToArray(), ct);
        await body.FlushAsync(ct);

        try
        {
            await foreach (var line in subscription.Reader.ReadAllAsync(ct))
            {
                var frame = Encoding.UTF8.GetBytes($"data: {line}\n\n");
                await body.WriteAsync(frame, ct);
                await body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected — subscription disposed by the using block
        }
    }
}
