namespace GuiaFotos.Core;

public sealed record PhotoQuality(int Width, int Height, double Sharpness, double Exposure, double Score)
{
    public string Description => $"{Width} × {Height} · nitidez {Sharpness:F0} · exposição {Exposure:P0}";
    public static PhotoQuality Measure(RgbImage image)
    {
        var w = image.Width; var h = image.Height; var gray = new double[w * h]; int clipped = 0;
        for (int p = 0; p < gray.Length; p++) { gray[p] = .299 * image.Pixels[p * 3] + .587 * image.Pixels[p * 3 + 1] + .114 * image.Pixels[p * 3 + 2]; if (gray[p] < 5 || gray[p] > 250) clipped++; }
        double sum = 0, squared = 0; int count = 0;
        for (int y = 1; y < h - 1; y++) for (int x = 1; x < w - 1; x++)
        { int p = y * w + x; double lap = gray[p - 1] + gray[p + 1] + gray[p - w] + gray[p + w] - 4 * gray[p]; sum += lap; squared += lap * lap; count++; }
        var sharpness = count == 0 ? 0 : Math.Max(0, squared / count - Math.Pow(sum / count, 2));
        var exposure = 1 - clipped / (double)gray.Length;
        return new(w, h, sharpness, exposure, Math.Clamp(.65 * sharpness / (sharpness + 80) + .35 * exposure, 0, 1));
    }
}
