using IsleBridge.Api.Dtos;

namespace IsleBridge.Api.InboxWriter;

public interface IInboxWriter
{
    Task AppendAsync(CommandDto command, CancellationToken ct = default);

}