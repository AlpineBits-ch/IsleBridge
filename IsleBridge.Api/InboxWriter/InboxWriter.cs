using System.Text;
using System.Text.Json;
using IsleBridge.Api.Dtos;
using Microsoft.Extensions.Options;

namespace IsleBridge.Api.InboxWriter;

public class InboxWriter(Config config, ILogger<InboxWriter> logger) : IInboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path = Path.Join(Config.Get().PluginBasePath, "Saved/inbox.ndjson");

    public async Task AppendAsync(CommandDto command, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(command, JsonOptions) + "\n";
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

        logger.LogInformation("Queued {Verb} for {Steam} (id={Id})", command.Verb, command.Steam, command.Id);
    }

    private async Task WriteOnceAsync(byte[] bytes, CancellationToken ct)
    {
        await using var fs = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
    }
}

public class BridgeOptions
{
    public required string InboxPath { get; set; }
    public required string ChatOutPath { get; set; }
}