using IsleBridge.Api.Dtos;
using IsleBridge.Api.InboxWriter;
using Microsoft.AspNetCore.Mvc;

namespace IsleBridge.Api.Controllers;


[Route("api/v1/command")]
[ApiController]
public class CommandController(IInboxWriter inbox) : ControllerBase
{
    [HttpPost("swap")]
    public async Task<IActionResult> Swap([FromBody] SwapCommand cmd, CancellationToken ct)
    {
        var dto = new CommandDto { Verb = "swap", Steam = cmd.Steam, Args = cmd.Args };
        await inbox.AppendAsync(dto, ct);
        return Accepted(new { dto.Id });
    }

    [HttpPost("setstats")]
    public async Task<IActionResult> SetStats([FromBody] SetStatsCommand cmd, CancellationToken ct)
    {
        var dto = new CommandDto { Verb = "setstats", Steam = cmd.Steam, Args = cmd.Args };
        await inbox.AppendAsync(dto, ct);
        return Accepted(new { dto.Id });
    }

    [HttpPost("prime")]
    public async Task<IActionResult> Prime([FromBody] TargetedCommand cmd, CancellationToken ct)
    {
        var dto = new CommandDto { Verb = "prime", Steam = cmd.Steam };
        await inbox.AppendAsync(dto, ct);
        return Accepted(new { dto.Id });
    }

    [HttpPost("unprime")]
    public async Task<IActionResult> Unprime([FromBody] TargetedCommand cmd, CancellationToken ct)
    {
        var dto = new CommandDto { Verb = "unprime", Steam = cmd.Steam };
        await inbox.AppendAsync(dto, ct);
        return Accepted(new { dto.Id });
    }
}