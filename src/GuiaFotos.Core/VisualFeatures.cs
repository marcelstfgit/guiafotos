using System.Text.Json;

namespace GuiaFotos.Core;

public sealed record VisualPhoto(string Id, string Path, string Stamp, string Thumbnail, byte[] Pixels, float[] Embedding,
    PhotoQuality Quality, float[]? NeuralEmbedding = null, string NeuralVersion = "")
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Description => Name + " · " + Quality.Description;
}
public sealed record PhotoFamily(string Id, IReadOnlyList<VisualPhoto> Photos, string? PreferredId = null, bool Manual = false)
{
    public VisualPhoto Best => Photos.FirstOrDefault(x => x.Id == PreferredId) ?? Photos.OrderByDescending(VisualFeatures.BestScore).First();
    public string Description => $"{Photos.Count} versões · {Best.Name}" + (Manual ? " · união manual" : " · revisar");
}

public static class VisualFeatures
{
    public const string Version = "visual-rank-v1";
    public static string Stamp(MediaItem item) => $"{new FileInfo(item.Path).Length}:{File.GetLastWriteTimeUtc(item.Path).Ticks}:{item.QuickSignature}:{Version}";
    public static async Task<VisualPhoto> Inspect(MediaItem item, ToolPaths tools, SscdModel? neural = null, CancellationToken token = default)
    {
        var sample = await MediaIndex.Sample(item.Path, token); item = item with { QuickSignature = sample }; var stamp = Stamp(item);
        var ppm = await Tools.Run(tools.Magick, [item.Path + "[0]", "-auto-orient", "-resize", "128x128!", "-depth", "8", "ppm:-"], token);
        var image = RgbImage.ParsePpm(ppm); var quality = PhotoQuality.Measure(image);
        using (var metadata = JsonDocument.Parse(item.MetadataJson))
        {
            int Dimension(string name) { foreach (var p in metadata.RootElement.EnumerateObject()) if (p.Name.EndsWith(":" + name) && (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var d))) return d; return 128; }
            int width = Dimension("ImageWidth"), height = Dimension("ImageHeight");
            int orientation = 1; foreach (var p in metadata.RootElement.EnumerateObject()) if (p.Name.EndsWith(":Orientation") && p.Value.ValueKind == JsonValueKind.Number) orientation = p.Value.GetInt32();
            if (orientation is >= 5 and <= 8) (width, height) = (height, width);
            quality = quality with { Width = width, Height = height };
        }
        var small = image.Resize(32, 32); var pixels = Rank(small); var embedding = Embedding(pixels);
        float[]? neuralVector = neural == null ? null : await neural.Encode(item.Path, tools, token);
        if (stamp != Stamp(item) || sample != await MediaIndex.Sample(item.Path, token)) throw new IOException("A foto mudou durante a comparação.");
        var id = MediaIndex.XxHash64(System.Text.Encoding.UTF8.GetBytes(item.Path + "|" + stamp)).ToString("x16");
        return new(id, item.Path, stamp, item.Thumbnail, pixels, embedding, quality, neuralVector, neural?.Version ?? "");
    }
    public static byte[] Rank(RgbImage image)
    {
        var gray = new byte[image.Width * image.Height]; var histogram = new int[256];
        for (int i = 0; i < gray.Length; i++) { gray[i] = (byte)Math.Clamp((int)Math.Round(.299 * image.Pixels[i * 3] + .587 * image.Pixels[i * 3 + 1] + .114 * image.Pixels[i * 3 + 2]), 0, 255); histogram[gray[i]]++; }
        var cumulative = new int[256]; int total = 0;
        for (int i = 0; i < 256; i++) { cumulative[i] = total + histogram[i] / 2; total += histogram[i]; }
        return gray.Select(v => (byte)(255L * cumulative[v] / gray.Length)).ToArray();
    }
    public static float[] Embedding(byte[] pixels)
    {
        if (pixels.Length != 1024) throw new ArgumentException("Assinatura requer32x32.");
        var values = new float[256];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) values[y * 16 + x] = (pixels[y * 64 + x * 2] + pixels[y * 64 + x * 2 + 1] + pixels[y * 64 + 32 + x * 2] + pixels[y * 64 + 33 + x * 2]) / 4f;
        float mean = values.Average(); for (int i = 0; i < values.Length; i++) values[i] -= mean;
        if (values.All(x => Math.Abs(x) < .001)) { values[0] = 1; values[1] = -1; }
        return VectorIndex.Unit(values);
    }
    public static double Similarity(VisualPhoto a, VisualPhoto b)
    {
        if (a.Pixels.Length != 1024 || b.Pixels.Length != 1024) throw new ArgumentException("Assinaturas inválidas.");
        // Brightness/contrast are removed by per-image luminance ranks, with
        // an independent fine-grid verification after the vector candidate search.
        double ma = a.Pixels.Average(x => (double)x), mb = b.Pixels.Average(x => (double)x), aa = 0, bb = 0, dot = 0;
        for (int i = 0; i < 1024; i++) { var x = a.Pixels[i] - ma; var y = b.Pixels[i] - mb; dot += x * y; aa += x * x; bb += y * y; }
        if (aa < 100 || bb < 100) return -1; // Flat images cannot be safely identified from structure.
        return Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1);
    }
    public static double BestScore(VisualPhoto photo)
        => .4 * Math.Clamp(Math.Log2(Math.Max(1, (long)photo.Quality.Width * photo.Quality.Height)) / 26, 0, 1) + .6 * photo.Quality.Score;
}
