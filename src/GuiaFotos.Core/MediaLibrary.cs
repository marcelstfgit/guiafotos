using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GuiaFotos.Core;

public sealed class MediaLibrary(ToolPaths tools, string cache)
{
    public ScanStatistics LastScan { get; private set; } = new(0, 0, 0);
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".webm", ".mts", ".3gp" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".bmp", ".gif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2" };
    public static bool Supported(string path) => Videos.Contains(System.IO.Path.GetExtension(path)) || Images.Contains(System.IO.Path.GetExtension(path));
    public static bool IsVideo(string path) => Videos.Contains(System.IO.Path.GetExtension(path));
    public async Task<Dictionary<string, JsonElement>> Metadata(string path, CancellationToken token = default)
    {
        var json = await Tools.Text(tools.ExifTool, ["-json", "-G1", "-s", "-n", "-a", "-charset", "filename=UTF8", "--", path], token);
        using var document = JsonDocument.Parse(json);
        return document.RootElement[0].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
    public async Task<MediaItem> Inspect(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        var before = new FileInfo(path); var size = before.Length; var written = before.LastWriteTimeUtc.Ticks;
        var sample = await MediaIndex.Sample(path, token);
        var metadata = await Metadata(path, token);
        if (metadata.Keys.Any(k => k.EndsWith(":Error"))) throw new InvalidDataException("Arquivo corrompido ou formato não reconhecido: " + System.IO.Path.GetFileName(path));
        var taken = File.GetLastWriteTime(path);
        foreach (var name in new[] { "DateTimeOriginal", "CreateDate", "MediaCreateDate" })
        {
            var value = metadata.FirstOrDefault(x => x.Key.EndsWith(":" + name)).Value;
            if (value.ValueKind == JsonValueKind.String && DateTime.TryParseExact(value.GetString()![..Math.Min(19, value.GetString()!.Length)], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { taken = parsed; break; }
        }
        Directory.CreateDirectory(cache); var pathKey = MediaIndex.XxHash64(System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)).ToString("x16");
        var thumbnail = System.IO.Path.Combine(cache, pathKey + "-" + sample + "-" + written + ".jpg");
        var video = IsVideo(path); double duration = 0; string videoHashes = "";
        if (video)
        {
            var data = await Tools.Text(tools.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], token);
            using var probe = JsonDocument.Parse(data);
            if (probe.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var seconds)) double.TryParse(seconds.GetString(), CultureInfo.InvariantCulture, out duration);
        }
        if (!File.Exists(thumbnail))
        {
            try { await Tools.Run(tools.Ffmpeg, ["-v", "error", "-y", "-i", path, "-frames:v", "1", "-vf", "scale=400:300:force_original_aspect_ratio=decrease", thumbnail], token); }
            catch (IOException) { if (video) throw; await Tools.Run(tools.Magick, [path + "[0]", "-auto-orient", "-thumbnail", "400x300", thumbnail], token); }
        }
        var fingerprint = await Fingerprint(thumbnail, 0, token);
        if (video && duration > 0)
        {
            var hashes = new List<string>();
            foreach (var fraction in new[] { .15, .5, .85 }) hashes.Add((await Fingerprint(path, duration * fraction, token)).ToString("X16"));
            videoHashes = string.Join(',', hashes);
        }
        var gray = await Tools.Run(tools.Ffmpeg, ["-v", "error", "-i", thumbnail, "-frames:v", "1", "-vf", "scale=32:32", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"], token);
        if (sample != await MediaIndex.Sample(path, token)) throw new IOException("Arquivo mudou durante a importação.");
        before.Refresh();
        if (before.Length != size || before.LastWriteTimeUtc.Ticks != written) throw new IOException("Arquivo mudou durante a importação; atualize a pasta novamente.");
        return new MediaItem(path, video ? "Vídeo" : "Foto", size, taken, "", thumbnail, Json.Write(metadata), fingerprint, duration, videoHashes,
            LastWriteUtcTicks: written, QuickSignature: sample, PerceptualHash: MediaIndex.PHash(gray));
    }
    private async Task<ulong> Fingerprint(string path, double time, CancellationToken token)
    {
        var arguments = new List<string> { "-v", "error" };
        if (time > 0) arguments.AddRange(["-ss", time.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", path, "-frames:v", "1", "-vf", "scale=9:8", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]);
        var bytes = await Tools.Run(tools.Ffmpeg, arguments, token);
        if (bytes.Length < 72) throw new InvalidDataException("Não foi possível obter quadro para comparar.");
        ulong hash = 0; for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) if (bytes[y * 9 + x] > bytes[y * 9 + x + 1]) hash |= 1UL << (y * 8 + x);
        return hash;
    }
    public async Task<IReadOnlyList<string>> Scan(string folder, Catalog catalog, IProgress<string>? progress = null, CancellationToken token = default, bool forceFull = false)
    {
        var failures = new List<string>(); var existing = catalog.All().ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var reused = 0; var rebuilt = 0; long sampled = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(Supported))
        {
            token.ThrowIfCancellationRequested(); progress?.Report(System.IO.Path.GetFileName(path));
            try { var previous = existing.GetValueOrDefault(path);
                if (!forceFull && previous != null && await MediaIndex.CanReuse(previous, token))
                { reused++; sampled += Math.Min(previous.Size, 2L * MediaIndex.BlockSize); continue; }
                var current = await Inspect(path, token); rebuilt++;
                if (previous != null) current = current with { Tags = previous.Tags, Rating = previous.Rating, Album = previous.Album };
                catalog.Add(current); }
            catch (Exception error) when (error is not OperationCanceledException) { failures.Add(System.IO.Path.GetFileName(path) + ": " + error.Message); }
        }
        LastScan = new(reused, rebuilt, sampled);
        progress?.Report($"Índice atualizado: {reused} reutilizados, {rebuilt} processados.");
        return failures;
    }
    public async Task<IReadOnlyList<DuplicateGroup>> VerifiedDuplicates(Catalog catalog, bool similar, CancellationToken token = default)
    {
        var items = catalog.All(); var candidates = new List<MediaItem>();
        foreach (var group in items.GroupBy(x => new FileInfo(x.Path).Length).Where(x => x.Count() > 1))
            foreach (var item in group) candidates.Add(item with { Size = new FileInfo(item.Path).Length, QuickSignature = await MediaIndex.Sample(item.Path, token) });
        var exact = new List<DuplicateGroup>();
        foreach (var group in candidates.GroupBy(x => (x.Size, x.QuickSignature)).Where(x => x.Count() > 1))
        {
            var partitions = new List<List<MediaItem>>();
            foreach (var item in group)
            {
                List<MediaItem>? match = null;
                foreach (var partition in partitions) if (await MediaIndex.SameBytes(partition[0].Path, item.Path, token)) { match = partition; break; }
                if (match == null) partitions.Add([item]); else match.Add(item);
            }
            exact.AddRange(partitions.Where(x => x.Count > 1).Select(x => new DuplicateGroup("Idênticos (bytes completos conferidos)", 1, x)));
        }
        if (similar) exact.AddRange(Duplicates(items, true).Where(x => !x.Type.StartsWith("Candidatos") && !x.Type.StartsWith("Idênticos")));
        return exact;
    }
    public static IReadOnlyList<DuplicateGroup> Duplicates(IReadOnlyList<MediaItem> items, bool similar)
    {
        var result = items.Where(x => x.QuickSignature.StartsWith("xxh64-16k-v1-")).GroupBy(x => (x.Size, x.QuickSignature)).Where(x => x.Count() > 1)
            .Select(x => new DuplicateGroup("Candidatos (16 KB iniciais + finais; conferir)", 1, x.ToList())).ToList();
        if (!similar) return result;
        // Similaridade é uma sugestão; nunca aciona exclusão. Árvore BK evita comparação quadrática de fotos.
        var tree = new FingerprintTree();
        var originalPhotos = items.ToDictionary(x => x.Path);
        foreach (var item in items.Where(x => x.Kind == "Foto" && x.Fingerprint != null))
        {
            var indexed = item with { Fingerprint = item.PerceptualHash ?? item.Fingerprint };
            foreach (var other in tree.Search(indexed.Fingerprint!.Value, 8).Where(x => x.Path != item.Path))
            {
                var original = originalPhotos[other.Path];
                if (item.PerceptualHash != null && original.PerceptualHash != null && BitOperations.PopCount(item.Fingerprint!.Value ^ original.Fingerprint!.Value) > 18) continue;
                result.Add(new("Fotos semelhantes (pHash + dHash; revisar)", 1 - BitOperations.PopCount(indexed.Fingerprint.Value ^ other.Fingerprint!.Value) / 64.0, [original, item]));
            }
            tree.Add(indexed);
        }
        var videoTree = new FingerprintTree(); var originalVideos = items.Where(x => x.Kind == "Vídeo").ToDictionary(x => x.Path);
        foreach (var original in items.Where(x => x.Kind == "Vídeo" && x.VideoFingerprints.Length > 0))
        {
            var a = original with { Fingerprint = Convert.ToUInt64(original.VideoFingerprints.Split(',')[0], 16) };
            foreach (var b in videoTree.Search(a.Fingerprint!.Value, 6))
            {
                if (a.Path == b.Path || Math.Abs(a.Duration - b.Duration) > Math.Max(1, a.Duration * .02)) continue;
                var first = a.VideoFingerprints.Split(','); var second = b.VideoFingerprints.Split(','); if (first.Length != second.Length) continue;
                var distances = first.Zip(second, (x, y) => BitOperations.PopCount(Convert.ToUInt64(x, 16) ^ Convert.ToUInt64(y, 16))).ToArray();
                if (distances.All(d => d <= 6)) result.Add(new("Vídeos semelhantes (3 quadros; revisar)", 1 - distances.Average() / 64, [original, originalVideos[b.Path]]));
            }
            videoTree.Add(a);
        }
        return result;
    }
    private sealed class FingerprintTree
    {
        private sealed class Node(MediaItem value) { public MediaItem Value = value; public List<MediaItem> Matches = []; public Dictionary<int, Node> Children = []; }
        private Node? root;
        public void Add(MediaItem item)
        { if (root == null) { root = new(item); return; } var n = root; while (true) { var d = BitOperations.PopCount(n.Value.Fingerprint!.Value ^ item.Fingerprint!.Value); if (d == 0) { n.Matches.Add(item); return; } if (!n.Children.TryGetValue(d, out var child)) { n.Children[d] = new(item); return; } n = child; } }
        public IEnumerable<MediaItem> Search(ulong hash, int threshold)
        { if (root == null) yield break; var stack = new Stack<Node>(); stack.Push(root); while (stack.TryPop(out var n)) { var d = BitOperations.PopCount(hash ^ n.Value.Fingerprint!.Value); if (d <= threshold) { yield return n.Value; foreach (var match in n.Matches) yield return match; } foreach (var (distance, child) in n.Children) if (distance >= d - threshold && distance <= d + threshold) stack.Push(child); } }
    }
}
