namespace GuiaFotos.Core;

public sealed class FacePreferences
{
    public string Profile { get; set; } = "adaface-ir101";
    public string Detector { get; set; } = "scrfd10g";
    public bool Tiles { get; set; } = true;
    public int MinimumFaces { get; set; } = 3;
    public Dictionary<string, double> SearchThresholds { get; set; } = [];
    public Dictionary<string, double> ClusterThresholds { get; set; } = [];
    public double Search(string profile) => Math.Clamp(SearchThresholds.GetValueOrDefault(profile, .5), .1, .95);
    public double Cluster(string profile) => Math.Clamp(ClusterThresholds.GetValueOrDefault(profile, .65), .1, .95);
    public static FacePreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var result = Json.Read<FacePreferences>(File.ReadAllText(path));
        if (result.Detector is not ("yunet" or "scrfd10g")) result.Detector = "scrfd10g";
        result.MinimumFaces = Math.Clamp(result.MinimumFaces, 2, 100); return result;
    }
    public void Save(string path) => Files.AtomicText(path, Json.Write(this));
}

public sealed record CalibrationPair(double Score, bool Same);
public sealed record CalibrationResult(double Threshold, int FalseMatches, int MissedMatches, int Pairs);
public static class RecognitionCalibration
{
    // Choose on a held-out, labelled corpus. This never silently modifies user settings.
    public static CalibrationResult Choose(IReadOnlyList<CalibrationPair> pairs, double maximumFalsePositiveRate = .01)
    {
        if (!double.IsFinite(maximumFalsePositiveRate) || maximumFalsePositiveRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumFalsePositiveRate));
        if (!pairs.Any(p => p.Same) || !pairs.Any(p => !p.Same) || pairs.Any(p => !double.IsFinite(p.Score) || p.Score is < -1 or > 1)) throw new ArgumentException("Inclua pares positivos e negativos válidos.");
        int negatives = pairs.Count(p => !p.Same); CalibrationResult? best = null;
        foreach (var threshold in pairs.Select(p => p.Score).Append(1.0000001).Distinct().Order())
        {
            int wrong = pairs.Count(p => !p.Same && p.Score >= threshold), missed = pairs.Count(p => p.Same && p.Score < threshold);
            if (wrong / (double)negatives > maximumFalsePositiveRate) continue;
            var result = new CalibrationResult(threshold, wrong, missed, pairs.Count);
            if (best == null || missed < best.MissedMatches || missed == best.MissedMatches && wrong < best.FalseMatches) best = result;
        }
        return best!;
    }
}
