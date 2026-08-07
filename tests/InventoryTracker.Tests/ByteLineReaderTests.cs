using System.Text;
using InventoryTracker.Ingest;

namespace InventoryTracker.Tests;

public sealed class ByteLineReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_path);

    private void Write(string content) => File.WriteAllText(_path, content, new UTF8Encoding(false));

    private void Append(string content) => File.AppendAllText(_path, content, new UTF8Encoding(false));

    [Fact]
    public void Yields_complete_lines_with_their_start_offsets()
    {
        Write("alpha\nbeta\ngamma\n");

        var reader = new ByteLineReader();
        var lines = reader.ReadLines(_path).ToList();

        Assert.Equal(["alpha", "beta", "gamma"], lines.Select(l => l.Text));
        Assert.Equal([0, 6, 11], lines.Select(l => l.StartOffset));
        Assert.Equal(17, reader.Offset);
    }

    [Fact]
    public void Withholds_a_trailing_fragment_until_it_is_terminated()
    {
        // The game flushes Game.log mid-line; parsing half a record would corrupt the store.
        Write("done\nhalf-writ");

        var reader = new ByteLineReader();
        Assert.Equal(["done"], reader.ReadLines(_path).Select(l => l.Text));
        Assert.Equal(5, reader.Offset);

        Append("ten\n");

        Assert.Equal(["half-written"], reader.ReadLines(_path).Select(l => l.Text));
    }

    [Fact]
    public void Resumes_from_its_offset_without_repeating_anything()
    {
        Write("one\ntwo\n");

        var reader = new ByteLineReader();
        Assert.Equal(2, reader.ReadLines(_path).Count());

        Append("three\n");

        Assert.Equal(["three"], reader.ReadLines(_path).Select(l => l.Text));
    }

    [Fact]
    public void Starts_over_when_the_file_shrinks()
    {
        Write("aaaa\nbbbb\ncccc\n");

        var reader = new ByteLineReader();
        reader.ReadLines(_path).ToList();
        Assert.Equal(15, reader.Offset);

        // Rotated or rewritten under us.
        Write("new\n");

        Assert.Equal(["new"], reader.ReadLines(_path).Select(l => l.Text));
    }

    [Fact]
    public void Strips_a_byte_order_mark_from_the_first_line_only()
    {
        // A BOM glued onto the first '<' makes the line unparseable and silently costs the
        // file's first record.
        File.WriteAllText(_path, "<first>\n<second>\n", new UTF8Encoding(true));

        var lines = new ByteLineReader().ReadLines(_path).ToList();

        Assert.Equal(["<first>", "<second>"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Handles_crlf_terminators()
    {
        Write("alpha\r\nbeta\r\n");

        Assert.Equal(["alpha", "beta"], new ByteLineReader().ReadLines(_path).Select(l => l.Text));
    }

    [Fact]
    public void Reassembles_a_line_that_spans_buffer_reads()
    {
        // The internal buffer is 64 KiB; a line longer than that must not be split.
        var huge = new string('x', 200_000);
        Write($"{huge}\ntail\n");

        var lines = new ByteLineReader().ReadLines(_path).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(huge, lines[0].Text);
        Assert.Equal("tail", lines[1].Text);
    }
}
