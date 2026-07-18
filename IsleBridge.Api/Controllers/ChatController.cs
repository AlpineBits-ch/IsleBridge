using IsleBridge.Api.Dtos;
using IsleBridge.Api.FilePolling;
using Microsoft.AspNetCore.Mvc;

namespace IsleBridge.Api.Controllers;

[Route("api/v1/chat")]
[ApiController]
public class ChatController([FromKeyedServices("chat")] Poller<ChatLine> poller) : ControllerBase
{
    
    [HttpGet]
    public async Task<IActionResult> Poll()
    {
        return (Ok(await poller.PollAsync()));
    }
}