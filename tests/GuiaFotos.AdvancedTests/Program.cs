using GuiaFotos.Core;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;

var root = Path.Combine(Path.GetTempPath(), "guiafotos-advanced-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int pass = 0, fail = 0;
async Task Test(string name, Func<Task> body) { try { await body(); pass++; Console.WriteLine("PASS " + name); } catch (Exception error) { fail++; Console.WriteLine("FAIL " + name + ": " + error); } }
void Assert(bool condition, string text) { if (!condition) throw new Exception(text); }
await Test("xxHash64 confere vetores públicos", () => { Assert(MediaIndex.XxHash64([]) == 0xef46db3751d8e999UL, "empty"); Assert(MediaIndex.XxHash64("a"u8) == 0xd24ec4f1a98c6e5bUL, "a"); Assert(MediaIndex.XxHash64("abc"u8) == 0x44bc2cf5ad770999UL, "abc"); return Task.CompletedTask; });
var first = Path.Combine(root, "a.bin"); var second = Path.Combine(root, "b.bin");
await Test("amostras iguais não viram duplicata exata se miolo difere", async () => {
    File.WriteAllBytes(first, new byte[1024 * 1024]); File.Copy(first, second);
    using (var changed = new FileStream(second, FileMode.Open, FileAccess.Write)) { changed.Position = 500000; changed.WriteByte(1); }
    Assert(await MediaIndex.Sample(first) == await MediaIndex.Sample(second), "Amostras deveriam coincidir");
    Assert(!await MediaIndex.SameBytes(first, second), "Miolo diferente foi aprovado");
    File.Copy(first, second, true); Assert(await MediaIndex.SameBytes(first, second), "Cópia igual recusada");
});
await Test("amostragem de 1 GiB lê só extremidades", async () => {
    var large = Path.Combine(root, "sparse.bin"); using (var stream = File.Create(large)) stream.SetLength(1024L * 1024 * 1024);
    var timer = Stopwatch.StartNew(); var sample = await MediaIndex.Sample(large); timer.Stop();
    Assert(sample.StartsWith("xxh64-16k-v1-"), "Algoritmo incorreto"); Console.WriteLine($"BENCH arquivo=1GiB amostra=32768bytes tempo={timer.Elapsed.TotalMilliseconds:F2}ms"); File.Delete(large);
});
await Test("cache ignora arquivo intacto e invalida tamanho/data", async () => {
    var thumbnail = Path.Combine(root, "thumb.jpg"); File.WriteAllText(thumbnail, "fixture"); var info = new FileInfo(first);
    var item = new MediaItem(first, "Foto", info.Length, DateTime.Now, "", thumbnail, "{}", LastWriteUtcTicks: info.LastWriteTimeUtc.Ticks, QuickSignature: await MediaIndex.Sample(first), PerceptualHash: 1);
    Assert(await MediaIndex.CanReuse(item), "Cache intacto não reutilizado"); File.AppendAllText(first, "new"); Assert(!await MediaIndex.CanReuse(item), "Tamanho alterado reutilizado");
});
await Test("pHash não muda com brilho uniforme", () => {
    var a = Enumerable.Range(0, 1024).Select(i => (byte)((i / 32 + i % 32) * 2)).ToArray(); var b = a.Select(x => (byte)(x + 20)).ToArray();
    Assert(System.Numerics.BitOperations.PopCount(MediaIndex.PHash(a) ^ MediaIndex.PHash(b)) < 8, "pHash sensível a brilho"); return Task.CompletedTask;
});
var native = Environment.GetEnvironmentVariable("GUIAFOTOS_ONNX_LIBRARY");
if (!string.IsNullOrEmpty(native)) await Test("inferência ONNX nativa real com grafo Identity", () => {
    using var runtime = new NativeOnnx(native); using var model = runtime.Load(Proto.Identity());
    Assert(model.InputShape.SequenceEqual(new long[] { 1, 4 }), "Shape lido incorretamente");
    var output = model.Infer([1, 2, 3, 4], [1, 4]).Values.Single(); Assert(output.SequenceEqual(new float[] { 1, 2, 3, 4 }), "Inferência nativa incorreta"); return Task.CompletedTask;
}); else Console.WriteLine("SKIP inferência nativa: configure GUIAFOTOS_ONNX_LIBRARY");
await Test("PPM preserva byte de pixel que parece whitespace", () => {
    var bytes = Encoding.ASCII.GetBytes("P6\n1 1\n255\n").Concat(new byte[] { 10, 32, 100 }).ToArray(); var image = RgbImage.ParsePpm(bytes);
    Assert(image.Pixels.SequenceEqual(new byte[] { 10, 32, 100 }), "Pixel consumido como cabeçalho"); return Task.CompletedTask;
});
await Test("alinhamento 5 landmarks preserva template já alinhado", () => {
    var pixels = new byte[112 * 112 * 3]; for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 180);
    var image = new RgbImage(112, 112, pixels); float[] template = [38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f];
    var aligned = image.Align(template); Assert(Math.Abs(aligned.Pixels[56 * 112 * 3 + 50] - pixels[56 * 112 * 3 + 50]) <= 1, "Alinhamento deslocado"); return Task.CompletedTask;
});
var faceFile = Path.Combine(root, "person.jpg"); var otherFile = Path.Combine(root, "other.jpg"); var thirdFile = Path.Combine(root, "different.jpg"); var faceThumb = Path.Combine(root, "face.bmp");
File.WriteAllText(faceFile, "one"); File.WriteAllText(otherFile, "two"); File.WriteAllText(thirdFile, "three"); File.WriteAllText(faceThumb, "face");
FaceObservation Face(string id, string path, int axis) { var embedding = new float[128]; embedding[axis] = 1; return new(id, path, $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}:fixture:{FaceModels.Version}", faceThumb, embedding, 0, 0, 112, 112); }
var personFile = Path.Combine(root, "people.sqlite"); string personId = "";
await Test("nomear rosto encontra semelhantes e exclui negativos persistentes", () => {
    using var people = new PeopleCatalog(personFile); var a = Face("a", faceFile, 0); var b = Face("b", otherFile, 0); var c = Face("c", thirdFile, 1);
    foreach (var f in new[] { a, b, c }) people.Replace(f.MediaPath, f.Stamp, [f]);
    personId = people.Name(a.Id, "Pessoa X"); var matches = people.Search(personId);
    Assert(matches.Count == 2 && matches.Any(x => x.Face.Id == "b" && !x.Confirmed), "Busca não encontrou vetor próximo");
    people.Reject("b", personId); Assert(people.Search(personId).Count == 1, "Rejeição ignorada"); return Task.CompletedTask;
});
await Test("decisão persiste ao reabrir e confirmar sobrepõe rejeição", () => {
    using var people = new PeopleCatalog(personFile); Assert(people.Search(personId).Count == 1, "Rejeição não persistiu");
    people.Confirm("b", personId); Assert(people.Search(personId).Count(x => x.Confirmed) == 2, "Confirmação falhou"); return Task.CompletedTask;
});
await Test("renomear preserva rostos e nomes no índice", () => {
    using var people = new PeopleCatalog(personFile); var moved = Path.Combine(root, "renamed.jpg"); File.Move(faceFile, moved); people.Remap(faceFile, moved);
    Assert(people.Search(personId).Any(x => x.Face.MediaPath == moved && x.Confirmed), "Nome perdido no rename"); return Task.CompletedTask;
});
await Test("cinco perfis com vetores, download e versões distintos", () => {
    Assert(FaceModels.Profiles.Length == 5 && FaceModels.Profiles.Select(p => p.Version).Distinct().Count() == 5, "Perfis repetidos");
    foreach (var profile in FaceModels.Profiles) {
        Assert(profile.Recognizer.Sha256.Length == 64 && profile.DownloadMb > 0, "Modelo sem pin ou tamanho aproximado");
        var vector = new float[profile.Dimensions]; vector[0] = 3; vector[1] = 4; FaceEngine.Normalize(vector);
        Assert(Math.Abs(vector[0] - .6) < 1e-6 && Math.Abs(FaceEngine.Similarity(vector, vector) - 1) < 1e-6, "Normalização incompatível");
    }
    return Task.CompletedTask;
});
await Test("preprocessamento respeita BGR AdaFace, RGB EdgeFace e raw SFace", () => {
    var pixels = new byte[112 * 112 * 3]; for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 255; pixels[i + 1] = 128; pixels[i + 2] = 0; }
    var crop = new RgbImage(112, 112, pixels);
    foreach (var id in new[] { "adaface-ir101", "adaface-ir50" }) {
        var input = FaceEngine.PrepareInput(crop, FaceModels.Find(id));
        Assert(input[0] == -1 && input[112 * 112 * 2] == 1 && Math.Abs(input[112 * 112] - 1f / 255) < 1e-6, "AdaFace não é BGR normalizado");
    }
    foreach (var id in new[] { "edgeface-base", "edgeface-xxs" }) {
        var input = FaceEngine.PrepareInput(crop, FaceModels.Find(id)); Assert(input[0] == 1 && input[112 * 112 * 2] == -1, "EdgeFace não é RGB normalizado");
    }
    var raw = FaceEngine.PrepareInput(crop, FaceModels.Find("sface")); Assert(raw[0] == 255 && raw[112 * 112] == 128 && raw[112 * 112 * 2] == 0, "SFace não é RGB raw");
    return Task.CompletedTask;
});
await Test("trocar perfil preserva nomes e rejeições sem misturar vetores 512D", () => {
    var workspace = Path.Combine(root, "profiles"); var photo = new FileInfo(otherFile);
    FaceObservation Observation(FaceProfile profile) { var vector = new float[profile.Dimensions]; vector[0] = 1; return new(profile.Id, otherFile, $"{photo.Length}:{photo.LastWriteTimeUtc.Ticks}:sample:{profile.Version}", faceThumb, vector, 0, 0, 100, 100); }
    var firstProfile = FaceModels.Find("adaface-ir101"); var secondProfile = FaceModels.Find("edgeface-base");
    var a = Observation(firstProfile); var b = Observation(secondProfile); string saved;
    using (var one = FaceModels.OpenCatalog(workspace, firstProfile)) { one.Replace(otherFile, a.Stamp, [a]); saved = one.Name(a.Id, "Confirmada"); }
    using (var two = FaceModels.OpenCatalog(workspace, secondProfile)) {
        Assert(two.Faces().Count == 0 && two.People().Count == 0, "Índices não são independentes");
        bool blocked = false; try { two.Replace(otherFile, a.Stamp, [a]); } catch (ArgumentException) { blocked = true; }
        Assert(blocked, "Aceitou embedding de outro modelo com mesma dimensão");
        two.Replace(otherFile, b.Stamp, [b]); two.Name(b.Id, "Outra");
    }
    using (var one = FaceModels.OpenCatalog(workspace, firstProfile)) Assert(one.Search(saved).Single().Confirmed && one.People().Single().Name == "Confirmada", "Perdeu nome ao trocar de perfil");
    bool wrongDatabase = false; try { using var wrong = new PeopleCatalog(FaceModels.CatalogPath(workspace, firstProfile), secondProfile); } catch (InvalidDataException) { wrongDatabase = true; }
    Assert(wrongDatabase, "Abriu banco de outro perfil"); return Task.CompletedTask;
});
await Test("modelo facial errado não é publicado", async () => {
    using var handler = new WrongModel(); bool rejected = false;
    try { await FaceModels.Ensure(Path.Combine(root, "bad-models"), handler: handler); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected && !File.Exists(Path.Combine(root, "bad-models", "yunet.onnx")), "Modelo inválido publicado");
});
await Test("WebDAV backup/restore em protocolo controlado e hash durante envio", async () => {
    using var dav = new DavHandler(); using var cloud = new Nextcloud("https://nextcloud.test", "user", "app-password", dav);
    var source = Path.Combine(root, "backup.jpg"); File.WriteAllBytes(source, Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray());
    var item = new MediaItem(source, "Foto", new FileInfo(source).Length, DateTime.Now, "", "", "{}");
    var snapshot = await cloud.Backup([item], "Backups"); var restore = Path.Combine(root, "restored"); await cloud.RestoreBackup(snapshot, restore);
    Assert(await MediaIndex.SameBytes(source, Path.Combine(restore, "backup.jpg")), "Backup não restaurou bytes");
    Assert(dav.Methods.Contains("MOVE") && dav.PutCalls == 2, "Upload ou manifesto ausente");
    Assert((await cloud.Backups("Backups")).Count == 1, "Backup válido não listado");
});
if (Environment.GetEnvironmentVariable("GUIAFOTOS_TEST_PRETRAINED") == "1" && native != null)
    await Test("cinco modelos reais: referência, recodificação e pessoa diferente", async () => {
        using var http = new HttpClient(); var tools = ToolPaths.Discover(Environment.GetEnvironmentVariable("GUIAFOTOS_TOOLS_ROOT") ?? root);
        var lena = Path.Combine(root, "lena.jpg"); var messi = Path.Combine(root, "messi.jpg"); var related = Path.Combine(root, "lena-related.jpg");
        File.WriteAllBytes(lena, await http.GetByteArrayAsync("https://raw.githubusercontent.com/opencv/opencv/master/samples/data/lena.jpg"));
        File.WriteAllBytes(messi, await http.GetByteArrayAsync("https://raw.githubusercontent.com/opencv/opencv/master/samples/data/messi5.jpg"));
        await Tools.Run(tools.Magick, [lena, "-resize", "400x400", "-quality", "75", related]);
        MediaItem Photo(string p) => new(p, "Foto", new FileInfo(p).Length, DateTime.Now, "", "", "{}");
        foreach (var profile in FaceModels.Profiles) {
        Console.WriteLine("PRETRAINED " + profile.Id);
        var modelDirectory = Path.Combine(root, "trained-models", profile.Id); await FaceModels.Ensure(modelDirectory, profile);
        using var engine = new FaceEngine(native, modelDirectory, tools, Path.Combine(root, "trained-faces", profile.Id), profile);
        var a = (await engine.Detect(Photo(lena))).OrderByDescending(x => x.Width * x.Height).First();
        var b = (await engine.Detect(Photo(related))).OrderByDescending(x => x.Width * x.Height).First();
        var different = await engine.Detect(Photo(messi)); Assert(different.Count > 0, "Nenhum rosto negativo detectado");
        Assert(FaceEngine.Similarity(a.Embedding, b.Embedding) >= .5, "Pessoa recodificada não reconhecida");
        Assert(different.All(x => FaceEngine.Similarity(a.Embedding, x.Embedding) < .5), "Pessoa diferente confundida com referência");
        if (profile.Id == FaceModels.Profiles[0].Id) {
            await FaceModels.EnsureDetector(modelDirectory, FaceDetectionOptions.Detailed);
            using var detailed = new FaceEngine(native, modelDirectory, tools, Path.Combine(root, "scrfd-faces"), profile, FaceDetectionOptions.Detailed);
            var detected = await detailed.Detect(Photo(lena)); Assert(detected.Count > 0, "SCRFD real não detectou referência");
            Assert(detected.Max(f => FaceEngine.Similarity(f.Embedding, a.Embedding)) >= .5, "YuNet e SCRFD discordaram da referência");
        }
        }
    });
else Console.WriteLine("SKIP modelos treinados/fotos reais: exige GUIAFOTOS_TEST_PRETRAINED=1 e acesso à rede");
Console.WriteLine($"RESULT passed={pass} failed={fail}");
return fail == 0 ? 0 : 1;

sealed class WrongModel : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[232589]) });
}
sealed class DavHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> files = [];
    private readonly HashSet<string> folders = [];
    public List<string> Methods { get; } = [];
    public int PutCalls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath); var method = request.Method.Method; Methods.Add(method);
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        switch (method)
        {
            case "MKCOL": response.StatusCode = folders.Add(path) ? HttpStatusCode.Created : HttpStatusCode.MethodNotAllowed; break;
            case "PUT": files[path] = await request.Content!.ReadAsByteArrayAsync(token); PutCalls++; response.StatusCode = HttpStatusCode.Created; break;
            case "HEAD": response.Content = new ByteArrayContent(files[path]); break;
            case "GET": response.Content = files.TryGetValue(path, out var bytes) ? new ByteArrayContent(bytes) : new ByteArrayContent([]); if (!files.ContainsKey(path)) response.StatusCode = HttpStatusCode.NotFound; break;
            case "MOVE": var target = Uri.UnescapeDataString(new Uri(request.Headers.GetValues("Destination").Single()).AbsolutePath); if (files.ContainsKey(target)) response.StatusCode = HttpStatusCode.PreconditionFailed; else { files[target] = files[path]; files.Remove(path); response.StatusCode = HttpStatusCode.Created; } break;
            case "DELETE": files.Remove(path); break;
            case "PROPFIND": XNamespace d = "DAV:"; var children = folders.Where(x => x.StartsWith(path.TrimEnd('/') + "/") && !x[(path.TrimEnd('/').Length + 1)..].Contains('/'));
                response.StatusCode = HttpStatusCode.MultiStatus; response.Content = new StringContent(new XDocument(new XElement(d + "multistatus", children.Prepend(path).Select(p => new XElement(d + "response", new XElement(d + "href", p + "/"), new XElement(d + "propstat", new XElement(d + "status", "HTTP/1.1 200 OK"), new XElement(d + "prop", new XElement(d + "resourcetype", new XElement(d + "collection")))))))).ToString()); break;
            default: response.StatusCode = HttpStatusCode.MethodNotAllowed; break;
        }
        return response;
    }
}
static class Proto
{
    private static byte[] Var(ulong n) { var bytes = new List<byte>(); while (n >= 128) { bytes.Add((byte)(n | 128)); n >>= 7; } bytes.Add((byte)n); return bytes.ToArray(); }
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(x => x).ToArray();
    private static byte[] I(int field, ulong n) => Join(Var((ulong)field << 3), Var(n));
    private static byte[] B(int field, byte[] value) => Join(Var(((ulong)field << 3) | 2), Var((ulong)value.Length), value);
    private static byte[] S(int field, string text) => B(field, Encoding.UTF8.GetBytes(text));
    private static byte[] Value(string name) => Join(S(1, name), B(2, B(1, Join(I(1, 1), B(2, Join(B(1, I(1, 1)), B(1, I(1, 4))))))));
    public static byte[] Identity() => Join(I(1, 8), B(7, Join(B(1, Join(S(1, "x"), S(2, "y"), S(4, "Identity"))), S(2, "guiafotos-test"), B(11, Value("x")), B(12, Value("y")))), B(8, I(2, 13)));
}
