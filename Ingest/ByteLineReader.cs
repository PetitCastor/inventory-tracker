using System.Text;

namespace LogParser.Ingest;

/// <summary>
/// Reads whole lines out of a growing file while tracking the exact byte offset
/// just past each line's terminator, so ingestion can resume mid-file.
/// <para>
/// A trailing fragment with no '\n' is deliberately withheld: the game flushes
/// Game.log mid-line, and parsing a half-written record would corrupt the store.
/// The fragment is carried in <see cref="Pending"/> and completed on a later read.
/// </para>
/// </summary>
public sealed class ByteLineReader
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly MemoryStream _partial = new();

    /// <summary>Byte offset just past the last complete line handed out.</summary>
    public long Offset { get; private set; }

    /// <summary>Bytes of an unterminated trailing line held back for the next pass.</summary>
    public int Pending => (int)_partial.Length;

    public ByteLineReader(long startOffset = 0) => Offset = startOffset;

    /// <summary>Forget all state — used when a file is truncated or rotated.</summary>
    public void Reset()
    {
        Offset = 0;
        _partial.SetLength(0);
    }

    /// <summary>A complete line plus the byte offset it started at, used as its stable identity.</summary>
    public readonly record struct Line(string Text, long StartOffset);

    /// <summary>
    /// Streams complete lines from <paramref name="path"/> starting at <see cref="Offset"/>.
    /// FileShare.ReadWrite is mandatory: the game keeps its own handle on the live log.
    /// </summary>
    public IEnumerable<Line> ReadLines(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // The file shrank, so it was rotated or rewritten under us. Start over.
        if (fs.Length < Offset)
        {
            Reset();
        }

        // Offset counts only complete lines, so seeking to it replays any fragment
        // withheld last pass. Drop the stale copy rather than double-counting its bytes.
        _partial.SetLength(0);
        fs.Position = Offset;

        int read;
        while ((read = fs.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;

                _partial.Write(_buffer, start, i - start + 1);
                var lineStart = Offset;
                Offset += _partial.Length;
                var text = Decode(_partial, atFileStart: lineStart == 0);
                _partial.SetLength(0);
                start = i + 1;

                yield return new Line(text, lineStart);
            }

            if (start < read)
            {
                _partial.Write(_buffer, start, read - start);
            }
        }
    }

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    private static string Decode(MemoryStream ms, bool atFileStart)
    {
        var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        // Trim the terminator we just wrote, plus a CR if the file is CRLF.
        if (span.Length > 0 && span[^1] == (byte)'\n') span = span[..^1];
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];

        // A byte-order mark would be glued onto the first line's leading '<' and make it
        // unparseable, silently costing the file's first record.
        if (atFileStart && span.StartsWith(Bom)) span = span[Bom.Length..];

        return Encoding.UTF8.GetString(span);
    }
}
