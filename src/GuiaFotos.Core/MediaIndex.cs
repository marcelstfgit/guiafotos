using System.Buffers.Binary;

namespace GuiaFotos.Core;

public sealed record ScanStatistics(int Reused, int Rebuilt, long SampleBytesRead);

public static class MediaIndex
{
    private static readonly double[,] Cosines = CreateCosines();
    private static double[,] CreateCosines()
    {
        var table = new double[8, 32];
        for (int frequency = 0; frequency < 8; frequency++) for (int position = 0; position < 32; position++)
            table[frequency, position] = Math.Cos((2 * position + 1) * frequency * Math.PI / 64);
        return table;
    }
    public const int BlockSize = 16384;
    // A quick change detector, never a substitute for a verified full content hash.
    public static async Task<string> Sample(string path, CancellationToken token = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BlockSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var buffer = new byte[BlockSize * 2 + 8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, input.Length); var used = 8;
        foreach (var position in new[] { 0L, Math.Max(BlockSize, input.Length - BlockSize) }.Where(x => x < input.Length).Distinct())
        {
            input.Position = position;
            var count = await input.ReadAtLeastAsync(buffer.AsMemory(used, BlockSize), (int)Math.Min(BlockSize, input.Length - position), false, token);
            used += count;
        }
        return "xxh64-16k-v1-" + XxHash64(buffer.AsSpan(0, used)).ToString("x16");
    }

    public static ulong XxHash64(ReadOnlySpan<byte> bytes)
    {
        const ulong p1 = 11400714785074694791UL, p2 = 14029467366897019727UL, p3 = 1609587929392839161UL,
            p4 = 9650029242287828579UL, p5 = 2870177450012600261UL;
        static ulong Rotate(ulong x, int n) => (x << n) | (x >> (64 - n));
        static ulong Round(ulong a, ulong b) => unchecked(Rotate(a + b * p2, 31) * p1);
        static ulong Merge(ulong a, ulong b) => unchecked((a ^ Round(0, b)) * p1 + p4);
        unchecked
        {
            int offset = 0; ulong hash;
            if (bytes.Length >= 32)
            {
                ulong a = p1 + p2, b = p2, c = 0, d = 0UL - p1;
                do { a = Round(a, BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..])); b = Round(b, BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 8)..]));
                    c = Round(c, BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 16)..])); d = Round(d, BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 24)..])); offset += 32;
                } while (offset <= bytes.Length - 32);
                hash = Rotate(a, 1) + Rotate(b, 7) + Rotate(c, 12) + Rotate(d, 18);
                hash = Merge(Merge(Merge(Merge(hash, a), b), c), d);
            }
            else hash = p5;
            hash += (ulong)bytes.Length;
            while (offset <= bytes.Length - 8) { hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..])); hash = Rotate(hash, 27) * p1 + p4; offset += 8; }
            if (offset <= bytes.Length - 4) { hash ^= BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) * p1; hash = Rotate(hash, 23) * p2 + p3; offset += 4; }
            while (offset < bytes.Length) { hash ^= bytes[offset++] * p5; hash = Rotate(hash, 11) * p1; }
            hash ^= hash >> 33; hash *= p2; hash ^= hash >> 29; hash *= p3; hash ^= hash >> 32;
            return hash;
        }
    }

    public static async Task<bool> SameBytes(string left, string right, CancellationToken token = default)
    {
        await using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (a.Length != b.Length) return false;
        var aa = new byte[131072]; var bb = new byte[131072];
        while (true)
        {
            var n = await a.ReadAtLeastAsync(aa, aa.Length, false, token); var m = await b.ReadAtLeastAsync(bb, bb.Length, false, token);
            if (n != m || !aa.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, m))) return false;
            if (n == 0) return true;
        }
    }

    public static async Task<bool> CanReuse(MediaItem item, CancellationToken token = default)
    {
        var file = new FileInfo(item.Path);
        if (!file.Exists || item.LastWriteUtcTicks == 0 || item.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks ||
            item.Size != file.Length || !item.QuickSignature.StartsWith("xxh64-16k-v1-") || !File.Exists(item.Thumbnail) || item.PerceptualHash == null) return false;
        var signature = await Sample(item.Path, token);
        file.Refresh();
        return signature == item.QuickSignature && file.Length == item.Size && file.LastWriteTimeUtc.Ticks == item.LastWriteUtcTicks;
    }

    public static ulong PHash(ReadOnlySpan<byte> grayscale)
    {
        if (grayscale.Length != 1024) throw new ArgumentException("pHash requer 32x32 pixels.");
        var coefficients = new double[64];
        for (int u = 0; u < 8; u++) for (int v = 0; v < 8; v++)
        {
            double sum = 0;
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                sum += grayscale[y * 32 + x] * Cosines[u, x] * Cosines[v, y];
            coefficients[v * 8 + u] = Math.Abs(sum) < 1e-7 ? 0 : sum;
        }
        var median = coefficients.Skip(1).Order().ElementAt(31); ulong result = 0;
        for (var i = 1; i < 64; i++) if (coefficients[i] > median) result |= 1UL << i;
        return result;
    }
}
