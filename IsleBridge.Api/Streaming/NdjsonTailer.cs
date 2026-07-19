using System.Text;

namespace IsleBridge.Api.Streaming;

/// <summary>
/// Tails an NDJSON out stream the way the contract (§1) prescribes: a byte offset
/// per file, a shrink-reset that first drains <c>&lt;file&gt;.old</c> from the last
/// offset, and a partial-line buffer that carries an incomplete trailing line to
/// the next poll. Not thread-safe; drive it from a single pump loop.
/// </summary>
public sealed class NdjsonTailer(string path)
{
    private long _offset;
    private string _buffer = "";

    /// <summary>
    /// Positions the offset at the current end of the file so a freshly started pump
    /// only emits lines written from now on, instead of replaying the whole history.
    /// </summary>
    public void SeekToEnd()
    {
        _offset = FileLength(path);
        _buffer = "";
    }

    /// <summary>Reads any new complete lines since the last poll.</summary>
    public IReadOnlyList<string> Poll()
    {
        var lines = new List<string>();

        var size = FileLength(path);
        if (size < _offset)
        {
            // Rotation: the plugin renamed <path> to <path>.old and started fresh.
            // Finish the tail of the rotated data first (carrying the partial buffer),
            // then reset and continue on the new file.
            ReadInto(path + ".old", ref _offset, ref _buffer, lines);
            _offset = 0;
            _buffer = "";
        }

        ReadInto(path, ref _offset, ref _buffer, lines);
        return lines;
    }

    private static long FileLength(string p)
    {
        var info = new FileInfo(p);
        return info.Exists ? info.Length : 0;
    }

    private static void ReadInto(string p, ref long offset, ref string buffer, List<string> sink)
    {
        if (!File.Exists(p)) return;

        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) return;
        fs.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, Encoding.UTF8);
        buffer += reader.ReadToEnd();
        offset = fs.Position;

        int nl;
        while ((nl = buffer.IndexOf('\n')) >= 0)
        {
            var line = buffer[..nl].TrimEnd('\r');
            buffer = buffer[(nl + 1)..];
            if (line.Length > 0) sink.Add(line);
        }
    }
}
