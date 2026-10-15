namespace GuiaFotos.Core;

public sealed record PlanProgress(string Id, string State, int CompletedActions, string? Error, IReadOnlyList<MediaItem> CurrentItems);
public sealed class PlanRunner(Catalog catalog, MediaLibrary library, PhotoEditor editor, FileActions files, string workspace)
{
    public Func<Nextcloud>? CloudFactory { get; set; }
    public string CloudRoot { get; set; } = "GuiaFotos";
    public string ExportFolder { get; set; } = "";
    public Action<string, string>? PathRenamed { get; set; }
    public IReadOnlyList<DuplicateGroup> LastDuplicates { get; private set; } = [];
    public async Task<IReadOnlyList<MediaItem>> Execute(AiPlan plan, IReadOnlyList<MediaItem> selection, CancellationToken token = default)
    {
        AiAssistant.Validate(plan, selection.Count);
        foreach (var item in selection)
            if (!File.Exists(item.Path) || item.Sha256.Length == 0 || await Files.Hash(item.Path, token) != item.Sha256) throw new IOException("Um item mudou desde o plano; gere outro plano.");
        foreach (var action in plan.Actions)
        {
            if (action.Operation is "enhance" or "restore" or "rotate" or "crop")
                foreach (var index in action.Items) if (MediaLibrary.IsVideo(selection[index].Path) || System.IO.Path.GetExtension(selection[index].Path).Equals(".gif", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O plano inclui edição raster de vídeo/animação; revise-o antes de executar.");
            if (action.Operation == "backup") { using var cloud = (CloudFactory ?? throw new ArgumentException("Configure o Nextcloud antes deste plano."))(); }
        }
        var id = Guid.NewGuid().ToString("N"); var log = System.IO.Path.Combine(workspace, "plans", id + ".json");
        var working = selection.ToArray(); var complete = 0;
        Files.AtomicText(log, Json.Write(new PlanProgress(id, "running", complete, null, working)));
        try
        {
            foreach (var action in plan.Actions)
            {
                token.ThrowIfCancellationRequested(); var indices = action.Items.Distinct().ToArray(); var items = indices.Select(i => working[i]).ToList();
                var args = action.Arguments ?? new();
                if (action.Operation == "backup") { using var cloud = CloudFactory!(); await cloud.Backup(items, CloudRoot, token: token); }
                else if (action.Operation == "find_duplicates") LastDuplicates = await Task.Run(() => MediaLibrary.Duplicates(items, true), token);
                else if (action.Operation == "rename_by_date")
                {
                    var rename = FileActions.PlanNames(items); await files.Rename(rename, token);
                    foreach (var entry in rename) { var index = Array.FindIndex(working, x => x.Path == entry.Source); var previous = working[index]; catalog.Remove(entry.Source); working[index] = (await library.Inspect(entry.Destination, token)) with { Tags = previous.Tags, Rating = previous.Rating, Album = previous.Album }; catalog.Add(working[index]); PathRenamed?.Invoke(entry.Source, entry.Destination); }
                }
                else foreach (var index in indices)
                {
                    var item = working[index]; var folder = ExportFolder.Length > 0 ? ExportFolder : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, "GuiaFotos-exportados"); string? output = null;
                    switch (action.Operation)
                    {
                        case "enhance": case "restore": case "rotate": case "crop": output = await editor.Edit(item.Path, folder, action.Operation, args, token); break;
                        case "strip_exif": output = await editor.StripExif(item.Path, folder, args.GetValueOrDefault("allPrivate") == "true", token); break;
                        case "metadata": output = await editor.MetadataCopy(item.Path, folder, args, token); break;
                        case "rate": item = item with { Rating = int.Parse(args["rating"]) }; break;
                        case "tag": item = item with { Tags = args.GetValueOrDefault("tags") ?? "" }; break;
                        case "album": item = item with { Album = args.GetValueOrDefault("name") ?? "" }; break;
                    }
                    if (output != null) item = (await library.Inspect(output, token)) with { Tags = item.Tags, Rating = item.Rating, Album = item.Album };
                    working[index] = item; catalog.Add(item);
                }
                complete++; Files.AtomicText(log, Json.Write(new PlanProgress(id, "running", complete, null, working)));
            }
            Files.AtomicText(log, Json.Write(new PlanProgress(id, "completed", complete, null, working))); return working;
        }
        catch (Exception error)
        {
            Files.AtomicText(log, Json.Write(new PlanProgress(id, "partial", complete, error.GetType().Name, working)));
            throw new IOException($"O plano parou após {complete} ações concluídas. Cópias e histórico foram preservados; os originais continuam disponíveis. Consulte o registro {id}.", error);
        }
    }
}
