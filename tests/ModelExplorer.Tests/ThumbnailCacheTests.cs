using System.Text;
using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public sealed class ContentKeyTests
{
    [Fact]
    public void The_key_is_sixteen_hex_characters()
    {
        var key = ContentKey.Compute(Stream("hello"), 5);

        Assert.Equal(16, key.Length);
        Assert.All(key, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Identical_content_gives_the_same_key()
    {
        Assert.Equal(
            ContentKey.Compute(Stream("same bytes"), 10),
            ContentKey.Compute(Stream("same bytes"), 10));
    }

    [Fact]
    public void Different_content_gives_a_different_key()
    {
        Assert.NotEqual(
            ContentKey.Compute(Stream("one"), 3),
            ContentKey.Compute(Stream("two"), 3));
    }

    /// <summary>
    /// The point of salting with the length. Two files can share their first
    /// 64 KB — same STL header, same exporter — and still be different models.
    /// </summary>
    [Fact]
    public void The_same_prefix_with_a_different_length_gives_a_different_key()
    {
        var prefix = new string('x', ContentKey.SampleBytes + 4096);

        Assert.NotEqual(
            ContentKey.Compute(Stream(prefix), 1_000_000),
            ContentKey.Compute(Stream(prefix), 2_000_000));
    }

    [Fact]
    public void Only_the_first_sample_is_read()
    {
        var head = new string('a', ContentKey.SampleBytes);

        // Same head and same declared length, different tails: indistinguishable
        // by design, and the reason a collision costs a wrong thumbnail at worst.
        Assert.Equal(
            ContentKey.Compute(Stream(head + "tail one"), 99),
            ContentKey.Compute(Stream(head + "tail two"), 99));
    }

    [Fact]
    public void An_empty_file_has_a_key()
    {
        Assert.Equal(16, ContentKey.Compute(Stream(string.Empty), 0).Length);
    }

    [Fact]
    public void A_short_read_does_not_truncate_the_sample()
    {
        var content = new string('z', 5000);

        // A stream that dribbles out a few bytes per Read, as a network share can.
        Assert.Equal(
            ContentKey.Compute(Stream(content), 5000),
            ContentKey.Compute(new DribblingStream(Encoding.UTF8.GetBytes(content)), 5000));
    }

    [Fact]
    public void Computing_from_a_real_file_matches_computing_from_its_bytes()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "model.stl");
        var bytes = Encoding.UTF8.GetBytes("solid cube\nendsolid");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(
            ContentKey.Compute(new MemoryStream(bytes), bytes.Length),
            ContentKey.Compute(path));
    }

    private static MemoryStream Stream(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    private sealed class DribblingStream(byte[] content) : MemoryStream(content)
    {
        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, 7)]);
    }
}

public sealed class ThumbnailCacheTests
{
    [Fact]
    public void Entries_are_sharded_by_the_first_two_characters()
    {
        var cache = new ThumbnailCache(@"C:\thumbs");

        Assert.Equal(@"C:\thumbs\ab\abcdef0123456789.png", cache.PathFor("abcdef0123456789"));
    }

    [Fact]
    public void A_written_thumbnail_is_found_again()
    {
        using var directory = new TempDirectory();
        var cache = new ThumbnailCache(directory.Path);
        var png = new byte[] { 1, 2, 3, 4 };

        Assert.False(cache.TryGetPath("00ff00ff00ff00ff", out _));

        cache.Write("00ff00ff00ff00ff", png);

        Assert.True(cache.TryGetPath("00ff00ff00ff00ff", out var path));
        Assert.Equal(png, File.ReadAllBytes(path));
    }

    [Fact]
    public void Writing_the_same_key_twice_replaces_it()
    {
        using var directory = new TempDirectory();
        var cache = new ThumbnailCache(directory.Path);

        cache.Write("1234567890abcdef", [1]);
        cache.Write("1234567890abcdef", [2, 2]);

        Assert.True(cache.TryGetPath("1234567890abcdef", out var path));
        Assert.Equal([2, 2], File.ReadAllBytes(path));
    }

    /// <summary>No temporary file may survive a write; the gate scrolls past thousands.</summary>
    [Fact]
    public void Writing_leaves_no_temporary_files_behind()
    {
        using var directory = new TempDirectory();
        var cache = new ThumbnailCache(directory.Path);

        cache.Write("deadbeefdeadbeef", [9]);

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Clear_removes_every_thumbnail_and_reports_the_count()
    {
        using var directory = new TempDirectory();
        var cache = new ThumbnailCache(directory.Path);

        cache.Write("aa00000000000000", [1]);
        cache.Write("bb00000000000000", [2]);
        cache.Write("bc00000000000000", [3]);

        Assert.Equal(3, cache.Clear());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.png", SearchOption.AllDirectories));
        Assert.False(cache.TryGetPath("aa00000000000000", out _));
    }

    [Fact]
    public void Clearing_a_cache_that_was_never_written_is_not_an_error()
    {
        using var directory = new TempDirectory();

        Assert.Equal(0, new ThumbnailCache(Path.Combine(directory.Path, "missing")).Clear());
    }
}
