namespace GuiaFotos.Core;

public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score, float[] Landmarks);
public sealed record FaceObservation(string Id, string MediaPath, string Stamp, string Thumbnail, float[] Embedding,
    float X, float Y, float Width, float Height, double Quality = 1, int CoordinateWidth = 0, int CoordinateHeight = 0, bool Retained = false);

public sealed class FaceEngine : IDisposable
{
    private readonly NativeOnnx runtime;
    private readonly NativeOnnx.Model detector, recognizer;
    private readonly ToolPaths tools;
    private readonly string cache;
    private readonly FaceProfile profile;
    private readonly FaceDetectionOptions options;
    public FaceEngine(string nativeLibrary, string modelsDirectory, ToolPaths tools, string cache, FaceProfile? profile = null, FaceDetectionOptions? options = null)
    {
        this.profile = profile ?? FaceModels.Find("sface"); this.options = options ?? new();
        if (this.options.Detector is not ("yunet" or "scrfd10g")) throw new ArgumentException("Detector inválido.");
        this.tools = tools; this.cache = cache; Directory.CreateDirectory(cache);
        runtime = new NativeOnnx(nativeLibrary); NativeOnnx.Model? loadedDetector = null;
        try { loadedDetector = runtime.Load(Path.Combine(modelsDirectory, this.options.Detector == "scrfd10g" ? "scrfd10g.onnx" : "yunet.onnx")); detector = loadedDetector; recognizer = runtime.Load(Path.Combine(modelsDirectory, this.profile.Recognizer.Name)); }
        catch { loadedDetector?.Dispose(); runtime.Dispose(); throw; }
    }
    public static string Stamp(MediaItem item, FaceProfile? profile = null, FaceDetectionOptions? options = null)
        => $"{new FileInfo(item.Path).Length}:{System.IO.File.GetLastWriteTimeUtc(item.Path).Ticks}:{item.QuickSignature}:{(options ?? new()).Version}:{(profile ?? FaceModels.Find("sface")).Version}";
    public async Task<List<FaceObservation>> Detect(MediaItem item, CancellationToken token = default)
    {
        if (item.Kind != "Foto") return [];
        var stamp = Stamp(item, profile, options); var sample = await MediaIndex.Sample(item.Path, token);
        var ppm = await Tools.Run(tools.Magick, [item.Path + "[0]", "-auto-orient", "-thumbnail", options.Tiles ? "4096x4096>" : "2400x2400>", "-depth", "8", "ppm:-"], token);
        var image = RgbImage.ParsePpm(ppm); var candidates = new List<DetectedFace>();
        foreach (var tile in Tiles(image.Width, image.Height, options.Tiles))
        {
            token.ThrowIfCancellationRequested(); var region = image.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            int height = detector.InputShape[2] > 0 ? checked((int)detector.InputShape[2]) : 640;
            int width = detector.InputShape[3] > 0 ? checked((int)detector.InputShape[3]) : 640;
            if (height < 32 || width < 32 || height > 2048 || width > 2048) throw new NotSupportedException("Resolução do detector inválida.");
            var scale = Math.Min((double)width / region.Width, (double)height / region.Height);
            var scaled = region.Resize(Math.Max(1, (int)(region.Width * scale)), Math.Max(1, (int)(region.Height * scale)));
            var input = new float[3 * width * height]; bool scrfd = options.Detector == "scrfd10g";
            if (scrfd) Array.Fill(input, -1);
            for (int y = 0; y < scaled.Height; y++) for (int x = 0; x < scaled.Width; x++) for (int c = 0; c < 3; c++)
            {
                var pixel = scaled.Pixels[(y * scaled.Width + x) * 3 + (scrfd ? c : 2 - c)];
                input[c * width * height + y * width + x] = scrfd ? (pixel - 127.5f) / 128f : pixel;
            }
            var output = detector.Infer(input, [1, 3, height, width]);
            var detections = scrfd ? DecodeScrfd(detector.OutputNames.Select(n => output[n]).ToArray(), width, height) : Decode(output, width, height);
            foreach (var face in detections.Where(f => f.X < scaled.Width && f.Y < scaled.Height && f.X + f.Width > 0 && f.Y + f.Height > 0))
                candidates.Add(face with { X = (float)(face.X / scale + tile.X), Y = (float)(face.Y / scale + tile.Y), Width = (float)(face.Width / scale), Height = (float)(face.Height / scale),
                    Landmarks = face.Landmarks.Select((v, i) => (float)(v / scale + (i % 2 == 0 ? tile.X : tile.Y))).ToArray() });
        }
        var faces = Suppress(candidates); var result = new List<FaceObservation>(); var runId = Guid.NewGuid().ToString("N");
        for (int i = 0; i < faces.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var face = faces[i]; RgbImage crop;
            try { crop = image.Align(face.Landmarks); } catch (InvalidDataException) { continue; }
            var embedding = recognizer.Infer(PrepareInput(crop, profile), [1, 3, 112, 112]).Values.Single();
            if (embedding.Length != profile.Dimensions) throw new InvalidDataException("Dimensão facial não corresponde ao perfil."); Normalize(embedding);
            var id = runId + "-" + i.ToString("x4");
            var thumbnail = Path.Combine(cache, id + ".bmp"); crop.WriteBmp(thumbnail);
            var quality = PhotoQuality.Measure(crop).Score * face.Score;
            result.Add(new(id, item.Path, stamp, thumbnail, embedding, face.X, face.Y, face.Width, face.Height, quality, image.Width, image.Height));
        }
        if (stamp != Stamp(item, profile, options) || sample != await MediaIndex.Sample(item.Path, token)) throw new IOException("A foto mudou durante a análise; tente novamente.");
        return result;
    }
    public sealed record Tile(int X, int Y, int Width, int Height);
    public static IEnumerable<Tile> Tiles(int width, int height, bool detailed)
    {
        yield return new(0, 0, width, height);
        if (!detailed || Math.Max(width, height) <= 1100) yield break;
        const int size = 960, step = 720;
        var xs = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((width - size) / (double)step) + 1)).Select(i => Math.Min(i * step, Math.Max(0, width - size))).Distinct();
        var ys = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((height - size) / (double)step) + 1)).Select(i => Math.Min(i * step, Math.Max(0, height - size))).Distinct().ToArray();
        foreach (var y in ys) foreach (var x in xs) yield return new(x, y, Math.Min(size, width), Math.Min(size, height));
    }
    public static List<DetectedFace> DecodeScrfd(float[][] output, int width, int height)
    {
        if (output.Length != 9) throw new InvalidDataException("SCRFD precisa de nove saídas (score/bbox/landmarks).");
        var faces = new List<DetectedFace>(); int[] strides = [8, 16, 32];
        for (int level = 0; level < 3; level++)
        {
            int stride = strides[level], columns = width / stride, count = columns * (height / stride) * 2;
            var scores = output[level]; var boxes = output[level + 3]; var points = output[level + 6];
            if (scores.Length != count || boxes.Length != count * 4 || points.Length != count * 10) throw new InvalidDataException("Saídas SCRFD incompatíveis.");
            for (int i = 0; i < count; i++)
            {
                if (!float.IsFinite(scores[i]) || scores[i] < .6f) continue;
                float x = i / 2 % columns * stride, y = i / 2 / columns * stride;
                var left = boxes[i * 4] * stride; var top = boxes[i * 4 + 1] * stride; var right = boxes[i * 4 + 2] * stride; var bottom = boxes[i * 4 + 3] * stride;
                var landmarks = new float[10]; for (int p = 0; p < 5; p++) { landmarks[p * 2] = x + points[i * 10 + p * 2] * stride; landmarks[p * 2 + 1] = y + points[i * 10 + p * 2 + 1] * stride; }
                if (landmarks.Any(v => !float.IsFinite(v)) || !float.IsFinite(left + top + right + bottom) || left + right < 8 || top + bottom < 8) continue;
                faces.Add(new(x - left, y - top, left + right, top + bottom, scores[i], landmarks));
            }
        }
        return Suppress(faces);
    }
    public static List<DetectedFace> Suppress(IEnumerable<DetectedFace> candidates)
    {
        var result = new List<DetectedFace>();
        foreach (var face in candidates.OrderByDescending(f => f.Score).ThenByDescending(f => f.Width * f.Height))
        { if (result.All(other => IntersectionOverUnion(face, other) < .3)) result.Add(face); }
        return result;
    }
    public static float[] PrepareInput(RgbImage crop, FaceProfile profile)
    {
        if (crop.Width != 112 || crop.Height != 112 || crop.Pixels.Length != 112 * 112 * 3) throw new ArgumentException("O rosto deve ter 112 × 112 pixels RGB.");
        var input = new float[3 * 112 * 112];
        for (int p = 0; p < 112 * 112; p++) for (int c = 0; c < 3; c++)
        {
            float value = crop.Pixels[p * 3 + (profile.Bgr ? 2 - c : c)];
            input[c * 112 * 112 + p] = profile.NormalizePixels ? (value - 127.5f) / 127.5f : value;
        }
        return input;
    }
    public static void Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (vector.Length is not (128 or 512) || !double.IsFinite(norm) || norm < 1e-10) throw new InvalidDataException("Vetor facial inválido.");
        for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length is not (128 or 512)) throw new InvalidDataException("Vetores faciais incompatíveis.");
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa > 0 && bb > 0 ? Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1) : -1;
    }
    public static List<DetectedFace> Decode(IReadOnlyDictionary<string, float[]> output, int width, int height)
    {
        var candidates = new List<DetectedFace>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cls = output["cls_" + stride]; var obj = output["obj_" + stride]; var bbox = output["bbox_" + stride]; var kps = output["kps_" + stride];
            int columns = width / stride, rows = height / stride;
            if (cls.Length != rows * columns || obj.Length != cls.Length || bbox.Length != cls.Length * 4 || kps.Length != cls.Length * 10) throw new InvalidDataException("Saídas YuNet incompatíveis.");
            for (int i = 0; i < cls.Length; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1)); if (score < .85f) continue;
                var cx = (i % columns + bbox[i * 4]) * stride; var cy = (i / columns + bbox[i * 4 + 1]) * stride;
                var w = MathF.Exp(bbox[i * 4 + 2]) * stride; var h = MathF.Exp(bbox[i * 4 + 3]) * stride;
                var points = new float[10]; for (int p = 0; p < 5; p++) { points[p * 2] = (i % columns + kps[i * 10 + p * 2]) * stride; points[p * 2 + 1] = (i / columns + kps[i * 10 + p * 2 + 1]) * stride; }
                if (float.IsFinite(w) && float.IsFinite(h) && w >= 10 && h >= 10) candidates.Add(new(cx - w / 2, cy - h / 2, w, h, score, points));
            }
        }
        var result = new List<DetectedFace>();
        foreach (var face in candidates.OrderByDescending(x => x.Score).Take(5000))
        {
            if (result.All(x => IntersectionOverUnion(x, face) < .3)) result.Add(face);
            if (result.Count >= 200) break;
        }
        return result;
    }
    public static double IntersectionOverUnion(DetectedFace a, DetectedFace b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }
    public void Dispose() { recognizer.Dispose(); detector.Dispose(); runtime.Dispose(); }
}

public sealed record RgbImage(int Width, int Height, byte[] Pixels)
{
    public static RgbImage ParsePpm(byte[] bytes)
    {
        int position = 0;
        string Next()
        {
            while (position < bytes.Length)
            {
                if (bytes[position] == '#') { while (position < bytes.Length && bytes[position] != '\n') position++; }
                else if (char.IsWhiteSpace((char)bytes[position])) position++;
                else break;
            }
            int start = position; while (position < bytes.Length && !char.IsWhiteSpace((char)bytes[position])) position++;
            return System.Text.Encoding.ASCII.GetString(bytes, start, position - start);
        }
        if (Next() != "P6") throw new InvalidDataException("Imagem RGB inválida.");
        int width = int.Parse(Next()), height = int.Parse(Next()); if (Next() != "255") throw new InvalidDataException("Profundidade RGB inválida.");
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new InvalidDataException("Resolução RGB inválida.");
        if (position >= bytes.Length || !char.IsWhiteSpace((char)bytes[position])) throw new InvalidDataException("Cabeçalho RGB incompleto.");
        if (bytes[position++] == '\r' && position < bytes.Length && bytes[position] == '\n') position++;
        if (bytes.Length - position != width * height * 3) throw new InvalidDataException("Pixels RGB incompletos.");
        return new(width, height, bytes[position..]);
    }
    private byte Sample(double x, double y, int channel)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1) return 0;
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double dx = x - x0, dy = y - y0;
        double Top(int yy) => Pixels[(yy * Width + x0) * 3 + channel] * (1 - dx) + Pixels[(yy * Width + x1) * 3 + channel] * dx;
        return (byte)Math.Clamp((int)Math.Round(Top(y0) * (1 - dy) + Top(y1) * dy), 0, 255);
    }
    public RgbImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > Width || y + height > Height) throw new ArgumentException("Recorte fora da imagem.");
        var pixels = new byte[width * height * 3];
        for (int row = 0; row < height; row++) Array.Copy(Pixels, ((y + row) * Width + x) * 3, pixels, row * width * 3, width * 3);
        return new(width, height, pixels);
    }
    public RgbImage Resize(int width, int height)
    {
        var output = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 3; c++)
            output[(y * width + x) * 3 + c] = Sample(Math.Clamp((x + .5) * Width / width - .5, 0, Width - 1), Math.Clamp((y + .5) * Height / height - .5, 0, Height - 1), c);
        return new(width, height, output);
    }
    public RgbImage Align(float[] source)
    {
        // Least-squares similarity transform, using the SFace/ArcFace 112px template.
        double[] target = [38.2946, 51.6963, 73.5318, 51.5014, 56.0252, 71.7366, 41.5493, 92.3655, 70.7299, 92.2041];
        if (source.Length != 10 || source.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Landmarks inválidos.");
        double sx = 0, sy = 0, tx = 0, ty = 0;
        for (int i = 0; i < 5; i++) { sx += source[2 * i]; sy += source[2 * i + 1]; tx += target[2 * i]; ty += target[2 * i + 1]; }
        sx /= 5; sy /= 5; tx /= 5; ty /= 5;
        double a = 0, b = 0, norm = 0;
        for (int i = 0; i < 5; i++) { var x = source[2 * i] - sx; var y = source[2 * i + 1] - sy; var u = target[2 * i] - tx; var v = target[2 * i + 1] - ty;
            a += x * u + y * v; b += x * v - y * u; norm += x * x + y * y; }
        if (norm < 1e-8) throw new InvalidDataException("Landmarks degenerados."); a /= norm; b /= norm;
        var det = a * a + b * b; if (det < 1e-12) throw new InvalidDataException("Alinhamento facial degenerado.");
        var dx = tx - a * sx + b * sy; var dy = ty - b * sx - a * sy; var pixels = new byte[112 * 112 * 3];
        for (int y = 0; y < 112; y++) for (int x = 0; x < 112; x++) for (int c = 0; c < 3; c++)
            pixels[(y * 112 + x) * 3 + c] = Sample((a * (x - dx) + b * (y - dy)) / det, (-b * (x - dx) + a * (y - dy)) / det, c);
        return new(112, 112, pixels);
    }
    public void WriteBmp(string path)
    {
        var stride = (Width * 3 + 3) / 4 * 4;
        using var output = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        output.Write((ushort)0x4d42); output.Write(54 + stride * Height); output.Write(0); output.Write(54);
        output.Write(40); output.Write(Width); output.Write(Height); output.Write((ushort)1); output.Write((ushort)24);
        output.Write(0); output.Write(stride * Height); output.Write(0); output.Write(0); output.Write(0); output.Write(0);
        for (int y = Height - 1; y >= 0; y--) { for (int x = 0; x < Width; x++) { var p = (y * Width + x) * 3; output.Write(Pixels[p + 2]); output.Write(Pixels[p + 1]); output.Write(Pixels[p]); } for (int i = Width * 3; i < stride; i++) output.Write((byte)0); }
    }
}
