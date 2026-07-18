using IsleBridge.Api;
using IsleBridge.Api.Dtos;
using IsleBridge.Api.FilePolling;
using IsleBridge.Api.InboxWriter;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
 

Poller<ChatLine> _poller = new Poller<ChatLine>(Path.Join(Config.Get().PluginBasePath, "Saved/chat.ndjson"));
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddSingleton<IInboxWriter, InboxWriter>();
builder.Services.AddSingleton(Config.Get());
builder.Services.AddKeyedSingleton("chat", _poller);
builder.Services.AddControllers();
builder.Services.AddLogging();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();


app.MapControllers();
app.Run();

