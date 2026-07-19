using IsleBridge.Api;
using IsleBridge.Api.InboxWriter;
using IsleBridge.Api.Streaming;

var builder = WebApplication.CreateBuilder(args);

var config = Config.Get();

builder.Services.AddOpenApi();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IInboxWriter, InboxWriter>();
builder.Services.AddControllers();

// One broadcast hub per out stream, keyed so the SSE controller can resolve them.
var chatHub = new StreamHub();
var eventsHub = new StreamHub();
var statsHub = new StreamHub();
var resultsHub = new StreamHub();

builder.Services.AddKeyedSingleton(StreamKeys.Chat, chatHub);
builder.Services.AddKeyedSingleton(StreamKeys.Events, eventsHub);
builder.Services.AddKeyedSingleton(StreamKeys.Stats, statsHub);
builder.Services.AddKeyedSingleton(StreamKeys.Results, resultsHub);

// One tail pump per stream, feeding its hub.
AddPump(StreamKeys.Chat, config.ChatPath, chatHub);
AddPump(StreamKeys.Events, config.EventsPath, eventsHub);
AddPump(StreamKeys.Stats, config.StatsPath, statsHub);
AddPump(StreamKeys.Results, config.ResultsPath, resultsHub);

// Flood guard: only emit stats while someone is reading the stats stream.
builder.Services.AddHostedService(sp => new StatsGateService(
    statsHub,
    sp.GetRequiredService<IInboxWriter>(),
    config,
    sp.GetRequiredService<ILogger<StatsGateService>>()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.Run();

return;

void AddPump(string name, string path, StreamHub hub)
{
    // AddSingleton<IHostedService>, not AddHostedService<T>: the latter uses
    // TryAddEnumerable which de-dupes by implementation type, so four TailPumpService
    // factories would collapse to one. Plain AddSingleton keeps all four.
    builder.Services.AddSingleton<IHostedService>(sp => new TailPumpService(
        name, path, config.PollIntervalMs, hub,
        sp.GetRequiredService<ILogger<TailPumpService>>()));
}
