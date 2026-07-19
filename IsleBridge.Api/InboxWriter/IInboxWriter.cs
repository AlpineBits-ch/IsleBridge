using System.Text.Json.Nodes;

namespace IsleBridge.Api.InboxWriter;

public interface IInboxWriter
{
    /// <summary>Appends a single already-built command envelope as one NDJSON line.</summary>
    Task AppendAsync(JsonObject command, CancellationToken ct = default);
}
