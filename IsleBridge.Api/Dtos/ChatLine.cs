namespace IsleBridge.Api.Dtos;

public class ChatLine
{
    public string Text { get; init; }
    public string ModeName { get; init; }
    public long Ts { get; init; }
    public string Steam { get; set; }
    public string Name { get; set; }
}