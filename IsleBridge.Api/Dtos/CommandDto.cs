namespace IsleBridge.Api.Dtos;

public class CommandDto
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Verb { get; set; }
    public string Steam { get; set; }
    public object? Args { get; set; }
}


public record SwapArgs(string Species, double? Growth);

public record SetStatsArgs(
    double? Growth,
    double? Health,
    double? Stamina,
    double? Hunger,
    double? Thirst,
    double? Oxygen,
    double? Food,
    double? Blood,
    bool? Prime);

public record TargetedCommand(string Steam);
public record SwapCommand(string Steam, SwapArgs Args);
public record SetStatsCommand(string Steam, SetStatsArgs Args);