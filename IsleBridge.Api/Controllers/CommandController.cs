using System.Text.Json.Nodes;
using IsleBridge.Api.InboxWriter;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace IsleBridge.Api.Controllers;

/// <summary>
/// Fire-and-forget command ingress. The Api is a proxy: it validates the envelope has a
/// verb, fills in <c>id</c>/<c>ts</c> if the caller omitted them, and appends the line to
/// the inbox. Acks and read payloads come back asynchronously on the results stream —
/// correlation by <c>id</c> is the SDK's job (contract §4).
/// </summary>
[Route("api/v1/command")]
[ApiController]
public class CommandController(IInboxWriter inbox, ILogger<CommandController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Send([FromBody] JsonObject? envelope, CancellationToken ct)
    {
        logger.LogInformation("Received command envelope: {Envelope}", envelope?.ToJsonString());
        if (envelope is null)
            return BadRequest(new { error = "empty body" });

        var verb = envelope["verb"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(verb))
            return BadRequest(new { error = "missing verb" });

        // The SDK normally owns the id; fill one in only if a raw caller omitted it.
        var id = envelope["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.CreateVersion7().ToString();
            envelope["id"] = id;
        }

        envelope["ts"] ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await inbox.AppendAsync(envelope, ct);
        return Accepted(new { id });
    }
}
