using GuiaFotos.Core;
using System.Diagnostics;

var root = Path.Combine(Path.GetTempPath(), "guiafotos-grouping-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int passed = 0, failed = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> run) { try { await run(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
float[] V(double angle, int dimension = 128) { var v = new float[dimension]; v[0] = (float)Math.Cos(angle); v[1] = (float)Math.Sin(angle); return v; }
FaceObservation Face(string id, string path, double angle, string stamp = "fixture", float x = 0) => new(id, path, stamp, "fixture", V(angle), x, 0, 80, 80, .8, 400, 200);
await Test("índice vetorial exato equivale à força bruta e persiste", () => {
    var random = new Random(481); var entries = Enumerable.Range(0, 220).Select(i => new VectorEntry(i.ToString("D4"), VectorIndex.Unit(Enumerable.Range(0, 128).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray()))).ToArray();
    var path = Path.Combine(root, "vectors.bin"); var tree = new VectorIndex(entries, path);
    foreach (var q in entries.Take(15)) foreach (var threshold in new[] { -1d, -.1, .1, .6, 1d }) {
        var expected = entries.Where(e => FaceEngine.Similarity(q.Vector, e.Vector) + 1e-7 >= threshold).Select(e => e.Id).Order().ToArray();
        var actual = tree.Search(q.Vector, threshold).Select(h => h.Id).Order().ToArray(); Assert(expected.SequenceEqual(actual), "Busca inexata em " + threshold);
    }
    var loaded = new VectorIndex(entries, path); Assert(loaded.LoadedFromDisk && loaded.Search(entries[0].Vector, .9).Single().Id == entries[0].Id, "Cache não reutilizado");
    var data = File.ReadAllBytes(path); data[35] ^= 4; File.WriteAllBytes(path, data); Assert(!new VectorIndex(entries, path).LoadedFromDisk, "Cache adulterado aceito");
    var changed = entries.Select((e, i) => i == 0 ? e with { Vector = entries[1].Vector } : e).ToArray(); Assert(!new VectorIndex(changed, path).LoadedFromDisk, "Vetor alterado reutilizou árvore obsoleta"); return Task.CompletedTask;
});
await Test("DBSCAN não propaga ponte esparsa entre duas pessoas", () => {
    double[] angles = [-.1, -.05, 0, .1, .45, .8, .9, .95, 1];
    var faces = angles.Select((a, i) => Face(i.ToString(), "photo" + i, a)).ToArray(); var index = new VectorIndex(faces.Select(f => new VectorEntry(f.Id, f.Embedding)));
    var groups = FaceClustering.Cluster(faces, index, Math.Cos(.36), 4);
    Assert(groups.Count == 2 && groups.All(g => g.Faces.Count >= 4), "Ponte virou uma pessoa só ou fragmentou núcleos"); return Task.CompletedTask;
});
await Test("duas faces da mesma foto não são unidas automaticamente", () => {
    var faces = new[] { Face("a", "same.jpg", 0), Face("b", "same.jpg", .01), Face("c", "other.jpg", .02), Face("d", "third.jpg", .03) };
    var groups = FaceClustering.Cluster(faces, new(faces.Select(f => new VectorEntry(f.Id, f.Embedding))));
    Assert(groups.All(g => g.Faces.Select(f => f.MediaPath).Distinct().Count() == g.Faces.Count), "Unificou coocorrências"); return Task.CompletedTask;
});
await Test("agrupamento alcança rostos depois do antigo limite de1000", () => {
    var faces = Enumerable.Range(0, 1005).Select(i => Face(i.ToString("D5"), "photo" + i, 0)).ToArray(); var watch = Stopwatch.StartNew();
    var groups = FaceClustering.Cluster(faces, new(faces.Select(f => new VectorEntry(f.Id, f.Embedding))));
    Assert(groups.Single().Faces.Count == 1005, "Biblioteca truncada"); Console.WriteLine($"BENCH faces=1005 grouped={groups[0].Faces.Count} time={watch.Elapsed.TotalMilliseconds:F0}ms"); return Task.CompletedTask;
});
await Test("cancelamento interrompe agrupamento em andamento", async () => {
    var faces = Enumerable.Range(0, 3000).Select(i => Face(i.ToString("D5"), "p" + i, i * .00001)).ToArray(); var index = new VectorIndex(faces.Select(f => new VectorEntry(f.Id, f.Embedding)));
    using var cts = new CancellationTokenSource(); cts.CancelAfter(10); bool stopped = false;
    try { await Task.Run(() => FaceClustering.Cluster(faces, index, .65, 3, cts.Token)); } catch (OperationCanceledException) { stopped = true; }
    Assert(stopped, "Cancelamento ignorado");
});
var original = Path.Combine(root, "original.jpg"); var thumbnail = Path.Combine(root, "thumbnail.bmp"); File.WriteAllText(original, "photo"); File.WriteAllText(thumbnail, "thumbnail");
string Stamp(string version) => $"{new FileInfo(original).Length}:{File.GetLastWriteTimeUtc(original).Ticks}:fixture:{version}:{FaceModels.Version}";
FaceObservation Saved(string id, float x, double angle, string stamp) => Face(id, original, angle, stamp, x) with { Thumbnail = thumbnail };
await Test("troca de detector e ausência total preservam nomes e rejeições", () => {
    var db = Path.Combine(root, "people.sqlite"); string ana, bia; var stamp = Stamp("yunet");
    using (var people = new PeopleCatalog(db)) {
        var faces = new[] { Saved("a", 0, 0, stamp), Saved("b", 120, 1, stamp), Saved("c", 240, .01, stamp) }; people.Replace(original, stamp, faces);
        ana = people.Name("a", "Ana"); bia = people.Name("b", "Bia"); people.Reject("c", ana);
        var next = Stamp("scrfd"); people.Replace(original, next, [Saved("new-a", 0, 0, next)]);
        Assert(people.Faces().Count == 3 && people.Faces().Count(f => f.Retained) == 2, "Rostos corrigidos desapareceram");
        Assert(people.Search(ana).Count == 1 && people.People().Single(p => p.Id == bia).ConfirmedFaces == 1, "Decisões perdidas");
        next = Stamp("empty"); people.Replace(original, next, []); Assert(people.Search(ana).Single().Confirmed, "Ausência total apagou nome");
        people.Replace(original, next, [Saved("a", 240, 2, next), Saved("new-ana", 0, 0, next)]);
        Assert(people.Faces().Select(f => f.Id).Distinct().Count() == people.Faces().Count && people.Search(ana).Single().Face.X == 0, "Reanálise com permutação colidiu IDs");
        people.Replace(original, next, []);
    }
    using (var people = new PeopleCatalog(db)) {
        Assert(people.Search(ana).Count == 1 && people.Faces().Count == 3, "Rejeição não persistiu");
        people.Merge(bia, ana); Assert(people.People().Count == 1, "Merge falhou"); Assert(people.Undo() && people.People().Count == 2, "Undo merge falhou");
        people.AssignMany(["a"], "Separada"); Assert(people.People().Any(p => p.Name == "Separada"), "Split falhou"); Assert(people.Undo(), "Undo split indisponível");
    }
    return Task.CompletedTask;
});
await Test("cobertura dos blocos, NMS e decodificação SCRFD", () => {
    var tiles = FaceEngine.Tiles(2400, 1800, true).ToArray(); Assert(tiles.Length > 1 && tiles.Any(t => t.X + t.Width == 2400 && t.Y + t.Height == 1800), "Bordas sem cobertura");
    float[][] output = new float[9][]; int[] strides = [8, 16, 32];
    for (int i = 0; i < 3; i++) { int count = 64 / strides[i] * (64 / strides[i]) * 2; output[i] = new float[count]; output[i + 3] = new float[count * 4]; output[i + 6] = new float[count * 10]; }
    output[0][36] = .9f; for (int j = 0; j < 4; j++) output[3][36 * 4 + j] = 2;
    var faces = FaceEngine.DecodeScrfd(output, 64, 64); Assert(faces.Single().Width == 32 && faces[0].X == 0, "SCRFD decode incorreto");
    Assert(FaceEngine.Suppress([faces[0], faces[0] with { Score = .8f }]).Count == 1, "Tiles duplicaram detecção"); return Task.CompletedTask;
});
await Test("calibração seleciona limite sem falso positivo no corpus rotulado", () => {
    var result = RecognitionCalibration.Choose([new(.9, true), new(.8, true), new(.7, false), new(.4, false)], 0);
    Assert(result.FalseMatches == 0 && result.MissedMatches == 0 && result.Threshold > .7 && result.Threshold <= .8, "Calibração incorreta"); return Task.CompletedTask;
});
VisualPhoto Photo(string id, string path, double angle = 0) {
    File.WriteAllText(path, id); var stamp = $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}:sample:{VisualFeatures.Version}";
    var pixels = Enumerable.Range(0, 1024).Select(i => (byte)((i * (id[0] + 1)) % 255)).ToArray();
    return new(id, path, stamp, thumbnail, pixels, VisualFeatures.Embedding(pixels), new(1200, 800, 100, .9, .8));
}
await Test("famílias manuais, separação, preferência e undo sobrevivem reabertura", () => {
    var db = Path.Combine(root, "visual.sqlite"); var a = Photo("A", Path.Combine(root, "a.jpg")); var b = Photo("B", Path.Combine(root, "b.jpg")); var c = Photo("C", Path.Combine(root, "c.jpg"));
    using (var store = new VisualCatalog(db)) {
        store.Add(a); store.Add(b); store.Add(c); store.Join([a.Id, b.Id, c.Id]); store.Prefer(b.Id, [a.Id, b.Id, c.Id]); Assert(store.Families().Single().Best.Id == b.Id, "Preferência ignorada");
        store.Separate([a.Id], [a.Id, b.Id, c.Id]); Assert(store.Families().Single().Photos.Select(p => p.Id).ToHashSet().SetEquals([b.Id, c.Id]), "Separar hub desmontou remanescente");
        bool blocked = false; try { store.Join([a.Id, c.Id]); } catch (InvalidOperationException) { blocked = true; } Assert(blocked, "União indireta anulou separação");
    }
    using (var store = new VisualCatalog(db)) {
        Assert(store.Undo() && store.Families().Single().Photos.Count == 3, "Undo persistente falhou");
        var moved = Path.Combine(root, "a-renamed.jpg"); File.Move(a.Path, moved); store.Remap(a.Path, moved); store.Add(a with { Path = moved, Id = "new-id" });
        Assert(store.Families().Single().Photos.Any(p => p.Id == a.Id && p.Path == moved), "Reanálise perdeu identidade após renomear");
    }
    return Task.CompletedTask;
});
await Test("preferências por motor persistem", () => {
    var path = Path.Combine(root, "face-preferences.json"); var prefs = new FacePreferences { Detector = "yunet", Tiles = false, MinimumFaces = 5 };
    prefs.SearchThresholds["sface"] = .63; prefs.ClusterThresholds["sface"] = .72; prefs.Save(path); var loaded = FacePreferences.Load(path);
    Assert(loaded.Detector == "yunet" && !loaded.Tiles && loaded.MinimumFaces == 5 && loaded.Search("sface") == .63 && loaded.Cluster("sface") == .72, "Preferência perdida"); return Task.CompletedTask;
});
await Test("variantes reais de formato, brilho e resolução com ferramentas locais", async () => {
    var tools = ToolPaths.Discover(Environment.GetEnvironmentVariable("GUIAFOTOS_TOOLS_ROOT") ?? root); var source = Path.Combine(root, "scene.ppm"); var pixels = new byte[256 * 256 * 3];
    for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { int p = (y * 256 + x) * 3; pixels[p] = (byte)(20 + x * .6); pixels[p + 1] = (byte)(25 + y * .6); pixels[p + 2] = (byte)(40 + ((x / 23 + y / 31) % 3) * 50); }
    using (var stream = File.Create(source)) { stream.Write(System.Text.Encoding.ASCII.GetBytes("P6\n256 256\n255\n")); stream.Write(pixels); }
    var jpeg = Path.Combine(root, "base.jpg"); var smaller = Path.Combine(root, "smaller.webp"); var brighter = Path.Combine(root, "brighter.jpg"); var unrelated = Path.Combine(root, "unrelated.jpg");
    await Tools.Run(tools.Magick, [source, "-quality", "95", jpeg]); await Tools.Run(tools.Magick, [jpeg, "-resize", "100x100", "-quality", "55", smaller]);
    await Tools.Run(tools.Magick, [jpeg, "-brightness-contrast", "15x5", "-quality", "70", brighter]); await Tools.Run(tools.Magick, ["-seed", "1847", "-size", "256x256", "plasma:fractal", unrelated]);
    MediaItem Item(string path) => new(path, "Foto", new FileInfo(path).Length, DateTime.Now, "", thumbnail, "{}", QuickSignature: "");
    var a = await VisualFeatures.Inspect(Item(jpeg), tools); var b = await VisualFeatures.Inspect(Item(smaller), tools); var c = await VisualFeatures.Inspect(Item(brighter), tools); var d = await VisualFeatures.Inspect(Item(unrelated), tools);
    double ab = VisualFeatures.Similarity(a, b), ac = VisualFeatures.Similarity(a, c), ad = VisualFeatures.Similarity(a, d);
    Console.WriteLine($"CORPUS resize+webp={ab:F4} brightness+jpeg={ac:F4} different-image={ad:F4}"); Assert(ab >= .96 && ac >= .96 && ad < .9, "Variantes/negativos não separados pelo limite declarado");
    using var store = new VisualCatalog(Path.Combine(root, "actual-visual.sqlite")); foreach (var p in new[] { a, b, c, d }) store.Add(p); Assert(store.Families().Single().Photos.Count == 3, "Família visual não consolidou pares");
    Assert(await store.Indexed(Item(jpeg)), "Retomada não reutilizou foto concluída");
    File.AppendAllText(jpeg, "changed"); Assert(!await store.Indexed(Item(jpeg)), "Retomada reutilizou arquivo alterado");
});
await Test("opção SSCD desligada não usa embeddings antigos e isoladas podem ser corrigidas", () => {
    using var store = new VisualCatalog(Path.Combine(root, "neural-switch.sqlite"));
    var a = Photo("X", Path.Combine(root, "neural-a.jpg")); var b = Photo("Y", Path.Combine(root, "neural-b.jpg"));
    var vector = new float[512]; vector[0] = 1;
    store.Add(a with { NeuralEmbedding = vector, NeuralVersion = "fixture" }); store.Add(b with { NeuralEmbedding = vector, NeuralVersion = "fixture" });
    Assert(store.Families(useNeural: true).Count == 1 && store.Families(useNeural: false).Count == 0, "Checkbox neural ignorado");
    Assert(store.Families(useNeural: false, includeSingles: true).Count == 2, "Fotos isoladas inacessíveis"); return Task.CompletedTask;
});
await Test("orientação EXIF mantém família com cópia normalizada", async () => {
    var tools = ToolPaths.Discover(Environment.GetEnvironmentVariable("GUIAFOTOS_TOOLS_ROOT") ?? root);
    var tagged = Path.Combine(root, "oriented.jpg"); var normalized = Path.Combine(root, "normalized.jpg");
    await Tools.Run(tools.Magick, ["-seed", "91", "-size", "240x160", "plasma:fractal", tagged]);
    await Tools.Run(tools.ExifTool, ["-overwrite_original", "-Orientation#=6", tagged]);
    await Tools.Run(tools.Magick, [tagged, "-auto-orient", "-strip", normalized]);
    MediaItem Item(string path, string metadata) => new(path, "Foto", new FileInfo(path).Length, DateTime.Now, "", thumbnail, metadata);
    var a = await VisualFeatures.Inspect(Item(tagged, "{\"File:ImageWidth\":240,\"File:ImageHeight\":160,\"IFD0:Orientation\":6}"), tools);
    var b = await VisualFeatures.Inspect(Item(normalized, "{\"File:ImageWidth\":160,\"File:ImageHeight\":240}"), tools);
    Assert(a.Quality.Width == b.Quality.Width && a.Quality.Height == b.Quality.Height && VisualFeatures.Similarity(a, b) >= .96, "Orientação corrigida virou outra foto");
});
Console.WriteLine($"RESULT passed={passed} failed={failed}; synthetic_geometry_and_transformed_images_only=true; pretrained_accuracy_tested=false");
return failed == 0 ? 0 : 1;
