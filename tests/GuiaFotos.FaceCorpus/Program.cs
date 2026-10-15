using GuiaFotos.Core;
using System.Diagnostics;

if (args.Length != 4) { Console.WriteLine("Uso: dotnet run --project tests/GuiaFotos.FaceCorpus -- corpus.json pasta-de-modelos pasta-do-pacote resultado.json"); return 2; }
var manifestPath = Path.GetFullPath(args[0]); var corpus = Json.Read<Corpus>(File.ReadAllText(manifestPath));
var directory = Path.GetDirectoryName(manifestPath)!;
string PhotoPath(string value) => Path.GetFullPath(Path.Combine(directory, value));
if (!corpus.Pairs.Any(p => p.Split == "calibration" && p.SamePerson) || !corpus.Pairs.Any(p => p.Split == "calibration" && !p.SamePerson) ||
    !corpus.Pairs.Any(p => p.Split == "evaluation" && p.SamePerson) || !corpus.Pairs.Any(p => p.Split == "evaluation" && !p.SamePerson)) throw new ArgumentException("Inclua positivos e negativos em calibration e evaluation.");
var calibrationFiles = corpus.Pairs.Where(p => p.Split == "calibration").SelectMany(p => new[] { PhotoPath(p.Reference), PhotoPath(p.Candidate) }).ToHashSet();
if (corpus.Pairs.Where(p => p.Split == "evaluation").SelectMany(p => new[] { PhotoPath(p.Reference), PhotoPath(p.Candidate) }).Any(calibrationFiles.Contains)) throw new ArgumentException("A avaliação precisa usar fotos diferentes da calibração.");
var calibrationHashes = new HashSet<string>();
foreach (var path in calibrationFiles) calibrationHashes.Add(await Files.Hash(path));
foreach (var path in corpus.Pairs.Where(p => p.Split == "evaluation").SelectMany(p => new[] { PhotoPath(p.Reference), PhotoPath(p.Candidate) }).Distinct())
    if (calibrationHashes.Contains(await Files.Hash(path))) throw new ArgumentException("A avaliação contém uma cópia byte a byte de uma foto de calibração.");
using var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); }; var token = cancellation.Token;
var package = Path.GetFullPath(args[2]); var tools = ToolPaths.Discover(package);
var native = Environment.GetEnvironmentVariable("GUIAFOTOS_ONNX_LIBRARY") ?? Path.Combine(package, "tools", "faces", "onnxruntime.dll");
var results = new List<ProfileResult>();
foreach (var profile in FaceModels.Profiles)
{
    var models = Path.Combine(Path.GetFullPath(args[1]), profile.Id); await FaceModels.Ensure(models, profile, token: token); await FaceModels.EnsureDetector(models, FaceDetectionOptions.Detailed, token: token);
    using var engine = new FaceEngine(native, models, tools, Path.Combine(Path.GetFullPath(args[1]), "corpus-crops", profile.Id), profile, FaceDetectionOptions.Detailed);
    var faces = new Dictionary<string, FaceObservation>(); var watch = Stopwatch.StartNew();
    async Task<FaceObservation> Detect(string name)
    {
        var path = PhotoPath(name); if (faces.TryGetValue(path, out var cached)) return cached;
        var item = new MediaItem(path, "Foto", new FileInfo(path).Length, DateTime.Now, "", "", "{}", QuickSignature: await MediaIndex.Sample(path, token));
        var detected = await engine.Detect(item, token); if (detected.Count != 1) throw new InvalidDataException($"{name}: o corpus requer exatamente um rosto por foto; foram encontrados {detected.Count}.");
        faces[path] = detected[0]; return detected[0];
    }
    var scored = new List<ScoredPair>();
    foreach (var pair in corpus.Pairs)
    {
        token.ThrowIfCancellationRequested(); var a = await Detect(pair.Reference); var b = await Detect(pair.Candidate);
        scored.Add(new(pair.Reference, pair.Candidate, pair.Split, pair.SamePerson, FaceEngine.Similarity(a.Embedding, b.Embedding)));
    }
    var chosen = RecognitionCalibration.Choose(scored.Where(p => p.Split == "calibration").Select(p => new CalibrationPair(p.Score, p.SamePerson)).ToArray(), corpus.MaximumFalsePositiveRate);
    var evaluation = scored.Where(p => p.Split == "evaluation").ToArray();
    results.Add(new(profile.Id, chosen, evaluation.Count(p => !p.SamePerson && p.Score >= chosen.Threshold), evaluation.Count(p => p.SamePerson && p.Score < chosen.Threshold), evaluation.Length, faces.Count, watch.Elapsed.TotalSeconds, scored));
    Files.AtomicText(Path.GetFullPath(args[3]), Json.Write(new { Corpus = manifestPath, Detector = FaceDetectionOptions.Detailed, Results = results, AutomaticSettingsChanges = false, Partial = results.Count != FaceModels.Profiles.Length }));
    Console.WriteLine($"{profile.Name}: threshold={chosen.Threshold:F4}, evaluation_false_matches={results[^1].FalseMatches}, missed={results[^1].Missed}, photos={faces.Count}, seconds={watch.Elapsed.TotalSeconds:F1}");
}
return 0;
sealed record Corpus(List<CorpusPair> Pairs, double MaximumFalsePositiveRate = .01);
sealed record CorpusPair(string Reference, string Candidate, bool SamePerson, string Split);
sealed record ScoredPair(string Reference, string Candidate, string Split, bool SamePerson, double Score);
sealed record ProfileResult(string Profile, CalibrationResult Calibration, int FalseMatches, int Missed, int EvaluationPairs, int Photos, double Seconds, List<ScoredPair> Scores);
