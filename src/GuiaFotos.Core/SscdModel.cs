namespace GuiaFotos.Core;

public sealed record SscdManifest(string Version, string Sha256, int InputSize, int Dimensions, double MaximumExportError);

public sealed class SscdModel : IDisposable
{
    private readonly NativeOnnx runtime;
    private readonly NativeOnnx.Model model;
    public string Version { get; }
    private SscdModel(string native, string directory, SscdManifest manifest)
    {
        Version = manifest.Version + "-" + manifest.Sha256; runtime = new NativeOnnx(native);
        try { model = runtime.Load(Path.Combine(directory, "sscd.onnx")); }
        catch { runtime.Dispose(); throw; }
        if (!model.InputShape.SequenceEqual(new long[] { 1, 3, 320, 320 })) { Dispose(); throw new InvalidDataException("SSCD precisa de entradaRGB320x320."); }
    }
    public static async Task<SscdModel?> Open(string native, string directory, CancellationToken token = default)
    {
        var manifestPath = Path.Combine(directory, "sscd-model.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = Json.Read<SscdManifest>(await File.ReadAllTextAsync(manifestPath, token));
        if (manifest.InputSize != 320 || manifest.Dimensions != 512 || !double.IsFinite(manifest.MaximumExportError) || manifest.MaximumExportError > .0001 || manifest.MaximumExportError < 0 ||
            await Files.Hash(Path.Combine(directory, "sscd.onnx"), token) != manifest.Sha256) throw new InvalidDataException("SSCD sem exportação validada ou arquivo alterado.");
        return new(native, directory, manifest);
    }
    public async Task<float[]> Encode(string path, ToolPaths tools, CancellationToken token = default)
    {
        var ppm = await Tools.Run(tools.Magick, [path + "[0]", "-auto-orient", "-resize", "320x320!", "-depth", "8", "ppm:-"], token);
        var image = RgbImage.ParsePpm(ppm); var input = new float[3 * 320 * 320]; float[] mean = [.485f, .456f, .406f], std = [.229f, .224f, .225f];
        for (int p = 0; p < 320 * 320; p++) for (int c = 0; c < 3; c++) input[c * 320 * 320 + p] = (image.Pixels[p * 3 + c] / 255f - mean[c]) / std[c];
        token.ThrowIfCancellationRequested(); var vector = model.Infer(input, [1, 3, 320, 320]).Values.Single();
        if (vector.Length != 512) throw new InvalidDataException("Saída SSCD deve ter512dimensões.");
        return VectorIndex.Unit(vector);
    }
    public void Dispose() { model.Dispose(); runtime.Dispose(); }
}
