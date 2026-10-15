using System.Security.Cryptography;

namespace GuiaFotos.Core;

// Hash the bytes actually sent over WebDAV, with no preliminary whole-file read.
internal sealed class HashingReadStream(Stream source) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? finished;
    private bool disposed;
    public long BytesRead { get; private set; }
    public string FinishHash() => finished ??= Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = source.Read(buffer, offset, count); hash.AppendData(buffer, offset, read); BytesRead += read; return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { var read = await source.ReadAsync(buffer, token); hash.AppendData(buffer.Span[..read]); BytesRead += read; return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing && !disposed) { FinishHash(); disposed = true; hash.Dispose(); source.Dispose(); } base.Dispose(disposing); }
}
