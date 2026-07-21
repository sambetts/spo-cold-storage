using System.Security.Cryptography;

namespace Migration.Engine.Providers;

/// <summary>
/// A unit of content in flight between a source and a cold store, with its byte length and MD5
/// computed <b>once</b> so the same integrity value drives the cold-store write header, the
/// post-copy verification, and the delete-safety length check without re-hashing.
///
/// Deliberately re-readable (<see cref="OpenReadAsync"/> can be called more than once) so a
/// retry — or a verify-then-upload flow — never has to re-download the source. The SharePoint
/// adaptor backs this with a temp file (streams large files without buffering them in memory);
/// the in-memory adaptor backs it with a byte array. Disposing releases the backing store
/// (deletes the temp file).
/// </summary>
public interface ITransferContent : IAsyncDisposable
{
    /// <summary>Content length in bytes.</summary>
    long Length { get; }

    /// <summary>Base64 MD5 of the content, computed when the content was produced.</summary>
    string ContentMd5Base64 { get; }

    /// <summary>Opens a fresh readable stream over the content. May be called multiple times.</summary>
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>In-memory <see cref="ITransferContent"/> backed by a byte array. Ideal for tests.</summary>
public sealed class InMemoryTransferContent : ITransferContent
{
    private readonly byte[] _bytes;

    public InMemoryTransferContent(byte[] bytes)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Length = bytes.LongLength;
        ContentMd5Base64 = Convert.ToBase64String(MD5.HashData(bytes));
    }

    public long Length { get; }
    public string ContentMd5Base64 { get; }

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// <see cref="ITransferContent"/> backed by a temp file on disk, so multi-GB transfers stream
/// through without being buffered in memory. Disposing deletes the temp file.
/// </summary>
public sealed class TempFileTransferContent : ITransferContent
{
    private readonly string _path;
    private int _disposed;

    private TempFileTransferContent(string path, long length, string md5Base64)
    {
        _path = path;
        Length = length;
        ContentMd5Base64 = md5Base64;
    }

    public long Length { get; }
    public string ContentMd5Base64 { get; }

    /// <summary>Wraps an already-downloaded temp file, computing its MD5 from disk.</summary>
    public static TempFileTransferContent FromExistingFile(string path)
    {
        var length = new FileInfo(path).Length;
        using var fs = File.OpenRead(path);
        var md5 = Convert.ToBase64String(MD5.HashData(fs));
        return new TempFileTransferContent(path, length, md5);
    }

    /// <summary>
    /// Streams <paramref name="source"/> into a new temp file while hashing it in a single pass,
    /// so the caller never buffers the whole payload and never re-reads it to hash. Optionally
    /// enforces an expected length (throws on a truncated stream — the download-truncation guard).
    /// </summary>
    public static async Task<TempFileTransferContent> CopyFromAsync(
        Stream source,
        long? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SpoColdStorageXfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "content.bin");

        long written;
        string md5Base64;
        using (var md5 = MD5.Create())
        await using (var dest = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var hashing = new CryptoStream(dest, md5, CryptoStreamMode.Write))
        {
            await source.CopyToAsync(hashing, 1 << 20, cancellationToken).ConfigureAwait(false);
            await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
            md5Base64 = Convert.ToBase64String(md5.Hash!);
            written = dest.Length;
        }

        if (expectedLength is long expected && expected != written)
        {
            TryDelete(path);
            throw new IOException($"Content was truncated: wrote {written:N0} bytes, expected {expected:N0}.");
        }

        return new TempFileTransferContent(path, written, md5Base64);
    }

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            TryDelete(_path);
        }
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
