using System.Text.Json;

namespace IsleBridge.Api;

public class Config
{
    public string PluginBasePath { get; set; } = ".";
    public string InboxPath => Path.Combine(PluginBasePath, "Saved", "inbox.ndjson");
    public string ChatOutPath => Path.Combine(PluginBasePath, "Saved", "chat.ndjson");
    public static Config Get()
    {
      return  JsonSerializer.Deserialize<Config>( File.ReadAllText("config.json") ) ?? new Config();
    }
}