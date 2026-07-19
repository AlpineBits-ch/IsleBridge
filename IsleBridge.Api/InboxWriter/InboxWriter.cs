using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleBridge.Api.InboxWriter;

/// <summary>
/// Appends command envelopes to <c>inbox.ndjson</c> per contract §1: append-only,
/// never truncate. The envelope is serialized straight from the <see cref="JsonObject"/>
/// so engine field names (e.g. the PascalCase skin customizer keys) are preserved
/// exactly rather than being run through a camelCase policy.
/// </summary>
public class InboxWriter(Config config, ILogger<InboxWriter> logger) : IInboxWriter
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task AppendAsync(JsonObject command, CancellationToken ct = default)
    {
        var line = command.ToJsonString(Compact) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _lock.WaitAsync(ct);
        try
        {
            await WriteOnceAsync(bytes, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Transient IO writing inbox, retrying once");
            await Task.Delay(100, ct);
            await WriteOnceAsync(bytes, ct);
        }
        finally
        {
            _lock.Release();
        }

        logger.LogInformation("Queued {Verb} (id={Id})",
            command["verb"]?.GetValue<string>(), command["id"]?.GetValue<string>());
    }

    private async Task WriteOnceAsync(byte[] bytes, CancellationToken ct)
    {
        var path = config.InboxPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // FileShare.ReadWrite: the plugin may rename the inbox to .processing while it
        // drains; we only ever hold it briefly for an append.
        await using var fs = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
    }
}
