using System.Diagnostics;
using System.Text.Json;
using Newtonsoft.Json;

namespace IsleBridge.Api.FilePolling;

public class Poller<T>(string path)
{
    private long _lastOffset;
    public async Task<IReadOnlyCollection<T>> PollAsync()
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Array.Empty<T>();
        }

        if (info.Length < _lastOffset) _lastOffset = 0;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_lastOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);

        var messages = new List<T>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Console.WriteLine(line);
            var msg = JsonConvert.DeserializeObject<T>(line);
            if(msg is null) continue;
            messages.Add(msg);
        }
        _lastOffset = fs.Position;
        
        return messages;
    }
}