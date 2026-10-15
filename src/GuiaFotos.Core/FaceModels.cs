namespace GuiaFotos.Core;

public sealed record FaceModelFile(string Name, string RelativeUrl, string Sha256, long Size);
public sealed record FaceProfile(string Id, string Name, string Description, string Version, int Dimensions,
    bool Bgr, bool NormalizePixels, int DownloadMb, FaceModelFile Recognizer)
{
    public override string ToString() => Name;
}
public static class FaceModels
{
    public const string Version = "yunet2023-sface2021-v1";
    public static readonly FaceModelFile[] Files = [
        new("yunet.onnx", "face_detection_yunet/face_detection_yunet_2023mar.onnx", "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4", 232589),
        new("sface.onnx", "face_recognition_sface/face_recognition_sface_2021dec.onnx", "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79", 38696353)
    ];
    public static readonly FaceProfile[] Profiles = [
        new("adaface-ir101", "AdaFace IR101 · qualidade", "Reconhecimento pesado, treinado no WebFace12M. Prioriza qualidade; download aproximado de 249 MB.", "yunet2023-adaface-ir101-webface12m-v1", 512, true, true, 249,
            new("recognizer.onnx", "https://github.com/yakhyo/adaface-onnx/releases/download/weights/adaface_ir_101.onnx", "f2eb07d03de0af560a82e1214df799fec5e09375d43521e2868f9dc387e5a43e", 0)),
        new("adaface-ir50", "AdaFace IR50 · intermediário", "Rede menor da família AdaFace. Download aproximado de 166 MB.", "yunet2023-adaface-ir50-webface4m-v1", 512, true, true, 166,
            new("recognizer.onnx", "https://github.com/yakhyo/adaface-onnx/releases/download/weights/adaface_ir_50.onnx", "9c0ae385d7362323c92d218de598d32e52a9130b78faac6a8a924091aca62c0b", 0)),
        new("edgeface-base", "EdgeFace Base · arquitetura recente", "Família eficiente baseada em EdgeNeXt, publicada em 2024. Download aproximado de 70 MB.", "yunet2023-edgeface-base-v1", 512, false, true, 70,
            new("recognizer.onnx", "https://github.com/yakhyo/edgeface-onnx/releases/download/weights/edgeface_base.onnx", "b56942f072c67385f44734b9458b0ccc4a2226888a113f77e0c802ad0c77b4c3", 0)),
        new("edgeface-xxs", "EdgeFace XXS · compacto", "Modelo compacto para economizar memória. Download aproximado de 5 MB.", "yunet2023-edgeface-xxs-v1", 512, false, true, 5,
            new("recognizer.onnx", "https://github.com/yakhyo/edgeface-onnx/releases/download/weights/edgeface_xxs.onnx", "dc674de4cbc77fa0bf9a82d5149558ab8581d82a2cd3bb60f28fd1a5d3ff8a2f", 0)),
        new("sface", "SFace · compatibilidade", "Motor OpenCV anterior, mantido para comparação e índices existentes. Download aproximado de 38 MB.", Version, 128, false, false, 38, Files[1])
    ];
    public static readonly FaceModelFile Scrfd = new("scrfd10g.onnx", "https://github.com/yakhyo/uniface/releases/download/weights/scrfd_10g_kps.onnx", "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91", 0);
    public static Task EnsureDetector(string directory, FaceDetectionOptions options, IProgress<string>? progress = null, CancellationToken token = default)
        => EnsureFiles(directory, [options.Detector == "scrfd10g" ? Scrfd : Files[0]], progress, token, null);
    public static FaceProfile Find(string id) => Profiles.Single(p => p.Id == id);
    public static string CatalogPath(string workspace, FaceProfile profile) => Path.Combine(workspace, profile.Id == "sface" ? "people.sqlite" : "people-" + profile.Id + ".sqlite");
    public static PeopleCatalog OpenCatalog(string workspace, FaceProfile profile) => new(CatalogPath(workspace, profile), profile);
    public static Task Ensure(string directory, FaceProfile profile, IProgress<string>? progress = null, CancellationToken token = default, HttpMessageHandler? handler = null)
        => EnsureFiles(directory, [Files[0], profile.Recognizer], progress, token, handler);
    public static async Task Ensure(string directory, IProgress<string>? progress = null, CancellationToken token = default, HttpMessageHandler? handler = null)
        => await EnsureFiles(directory, Files, progress, token, handler);
    private static async Task EnsureFiles(string directory, FaceModelFile[] files, IProgress<string>? progress, CancellationToken token, HttpMessageHandler? handler)
    {
        Directory.CreateDirectory(directory);
        using var client = handler == null ? new HttpClient() : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromMinutes(10);
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Name);
            if (System.IO.File.Exists(path) && (file.Size == 0 || new FileInfo(path).Length == file.Size) && await Core.Files.Hash(path, token) == file.Sha256) continue;
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                progress?.Report("Baixando modelo local: " + file.Name);
                using var response = await client.GetAsync(file.RelativeUrl.StartsWith("https://", StringComparison.Ordinal) ? file.RelativeUrl : "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/" + file.RelativeUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(token)) await using (var output = System.IO.File.Create(partial))
                {
                    var buffer = new byte[81920]; long size = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    { size += count; if (size > (file.Size > 0 ? file.Size : 512L * 1024 * 1024)) throw new InvalidDataException("Modelo excedeu tamanho esperado."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
                }
                if ((file.Size > 0 && new FileInfo(partial).Length != file.Size) || await Core.Files.Hash(partial, token) != file.Sha256) throw new InvalidDataException("Checksum do modelo não confere.");
                System.IO.File.Move(partial, path, true);
            }
            finally { if (System.IO.File.Exists(partial)) System.IO.File.Delete(partial); }
        }
    }
}
