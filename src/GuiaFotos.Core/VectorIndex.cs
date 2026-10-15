using System.Text;

namespace GuiaFotos.Core;

public sealed record VectorEntry(string Id, float[] Vector);
public sealed record VectorHit(string Id, double Similarity);

// Exact vantage-point metric tree: persistent topology, original float32 vectors,
// cosine thresholds via Euclidean distance on unit vectors. No ANN recall loss.
public sealed class VectorIndex
{
    private sealed record Node(int Entry, double Radius, int Inner, int Outer);
    private readonly VectorEntry[] entries;
    private readonly List<Node> nodes = [];
    private readonly int root;
    public int Count => entries.Length;
    public bool LoadedFromDisk { get; }
    public long DistanceEvaluations { get; private set; }
    public VectorIndex(IEnumerable<VectorEntry> source, string? cache = null, CancellationToken token = default)
    {
        entries = source.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new VectorEntry(x.Id, Unit(x.Vector))).ToArray();
        if (entries.Select(x => x.Id).Distinct().Count() != entries.Length || entries.Any(x => x.Vector.Length != entries[0].Vector.Length)) throw new ArgumentException("Vetores incompatíveis ou IDs repetidos.");
        if (cache != null && TryLoad(cache, out root)) { LoadedFromDisk = true; return; }
        root = Build(Enumerable.Range(0, entries.Length).ToArray(), token);
        if (cache != null) Save(cache);
    }
    public static float[] Unit(float[] values)
    {
        if (values.Length is < 1 or > 4096) throw new ArgumentException("Dimensão inválida.");
        double norm = 0; foreach (var x in values) { if (!float.IsFinite(x)) throw new ArgumentException("Vetor não finito."); norm += (double)x * x; }
        if (norm < 1e-15) throw new ArgumentException("Vetor vazio.");
        norm = Math.Sqrt(norm); return values.Select(x => (float)(x / norm)).ToArray();
    }
    private static double Distance(float[] a, float[] b)
    {
        double sum = 0; for (int i = 0; i < a.Length; i++) { var d = (double)a[i] - b[i]; sum += d * d; } return Math.Sqrt(sum);
    }
    private int Build(int[] points, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (points.Length == 0) return -1;
        int pivot = points[0], id = nodes.Count; nodes.Add(new(pivot, 0, -1, -1));
        if (points.Length == 1) return id;
        var ordered = points.Skip(1).Select(i => (Index: i, Distance: Distance(entries[pivot].Vector, entries[i].Vector))).OrderBy(x => x.Distance).ThenBy(x => x.Index).ToArray();
        int half = ordered.Length / 2; var radius = ordered[half].Distance;
        var inner = Build(ordered.Take(half).Select(x => x.Index).ToArray(), token);
        var outer = Build(ordered.Skip(half).Select(x => x.Index).ToArray(), token);
        nodes[id] = new(pivot, radius, inner, outer); return id;
    }
    public List<VectorHit> Search(float[] query, double minimumSimilarity, CancellationToken token = default)
    {
        if (!double.IsFinite(minimumSimilarity) || minimumSimilarity is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumSimilarity));
        var vector = Unit(query); if (entries.Length == 0) return [];
        if (vector.Length != entries[0].Vector.Length) throw new ArgumentException("Dimensão diferente do índice.");
        var radius = Math.Sqrt(Math.Max(0, 2 - 2 * minimumSimilarity)); var result = new List<VectorHit>();
        var stack = new Stack<int>(); stack.Push(root);
        while (stack.TryPop(out var id))
        {
            token.ThrowIfCancellationRequested(); var node = nodes[id]; var distance = Distance(vector, entries[node.Entry].Vector); DistanceEvaluations++;
            double cosine = Math.Clamp(1 - distance * distance / 2, -1, 1);
            if (cosine + 1e-7 >= minimumSimilarity) result.Add(new(entries[node.Entry].Id, cosine));
            if (node.Inner >= 0 && distance - radius <= node.Radius + 1e-6) stack.Push(node.Inner);
            if (node.Outer >= 0 && distance + radius >= node.Radius - 1e-6) stack.Push(node.Outer);
        }
        return result.OrderByDescending(x => x.Similarity).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
    private ulong Fingerprint()
    {
        ulong value = 14695981039346656037;
        foreach (var entry in entries)
        {
            value = unchecked((value ^ MediaIndex.XxHash64(Encoding.UTF8.GetBytes(entry.Id))) * 1099511628211);
            value = unchecked((value ^ MediaIndex.XxHash64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(entry.Vector.AsSpan()))) * 1099511628211);
        }
        return value;
    }
    private bool TryLoad(string path, out int loadedRoot)
    {
        loadedRoot = -1;
        try
        {
            if (new FileInfo(path).Length > 256L + entries.Length * 20L) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || MediaIndex.XxHash64(bytes.AsSpan(0, bytes.Length - 8)) != System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8))) return false;
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 8, false));
            if (reader.ReadString() != "GF-VPTREE-1" || reader.ReadUInt64() != Fingerprint() || reader.ReadInt32() != entries.Length) return false;
            loadedRoot = reader.ReadInt32();
            for (int i = 0; i < entries.Length; i++) nodes.Add(new(reader.ReadInt32(), reader.ReadDouble(), reader.ReadInt32(), reader.ReadInt32()));
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException();
            var seen = new HashSet<int>(); var used = new HashSet<int>(); var stack = new Stack<int>(); if (loadedRoot >= 0) stack.Push(loadedRoot);
            while (stack.TryPop(out var i))
            {
                if (i < 0 || i >= nodes.Count || !seen.Add(i)) throw new InvalidDataException(); var n = nodes[i];
                if (n.Entry < 0 || n.Entry >= entries.Length || !used.Add(n.Entry) || !double.IsFinite(n.Radius) || n.Radius < 0) throw new InvalidDataException();
                if (n.Inner != -1) stack.Push(n.Inner); if (n.Outer != -1) stack.Push(n.Outer);
            }
            if (seen.Count != nodes.Count) throw new InvalidDataException(); return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException) { nodes.Clear(); return false; }
    }
    private void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var writer = new BinaryWriter(new MemoryStream()))
            {
                writer.Write("GF-VPTREE-1"); writer.Write(Fingerprint()); writer.Write(entries.Length); writer.Write(root);
                foreach (var node in nodes) { writer.Write(node.Entry); writer.Write(node.Radius); writer.Write(node.Inner); writer.Write(node.Outer); }
                writer.Flush(); var bytes = ((MemoryStream)writer.BaseStream).ToArray();
                using var output = new BinaryWriter(File.Create(temp)); output.Write(bytes); output.Write(MediaIndex.XxHash64(bytes)); output.Flush();
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
