namespace GuiaFotos.Core;

public sealed record FaceDetectionOptions(string Detector = "yunet", bool Tiles = false)
{
    public string Version => Detector + (Tiles ? "-tiles4096-v3" : "-whole-v2");
    public static FaceDetectionOptions Detailed => new("scrfd10g", true);
}
