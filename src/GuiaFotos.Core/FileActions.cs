namespace GuiaFotos.Core;

public sealed class FileActions(string workspace)
{
    private string Journal => System.IO.Path.Combine(workspace, "journal");
    public List<string> Warnings { get; } = [];
    public string Trash => System.IO.Path.Combine(workspace, "trash");
    public Action<string>? FaultInjection { get; set; }
    private string Transactions => System.IO.Path.Combine(workspace, "transactions");
    public FileOperation Record(string type, string source, string destination, string hash, string state = "committed", string? group = null)
    {
        var operation = new FileOperation(Guid.NewGuid().ToString("N"), type, source, destination, hash, DateTime.UtcNow, state, group);
        Files.AtomicText(System.IO.Path.Combine(Journal, operation.Id + ".json"), Json.Write(operation)); return operation;
    }
    public IReadOnlyList<FileOperation> History()
    {
        Warnings.Clear(); var result = new List<FileOperation>(); if (!Directory.Exists(Journal)) return result;
        foreach (var path in Directory.EnumerateFiles(Journal, "*.json"))
        { try { result.Add(Json.Read<FileOperation>(File.ReadAllText(path))); } catch (Exception e) when (e is System.Text.Json.JsonException or IOException or InvalidDataException) { Warnings.Add("Registro de histórico não carregado (preservado): " + System.IO.Path.GetFileName(path)); } }
        return result.OrderByDescending(x => x.Time).ToList();
    }
    private FileOperation State(FileOperation operation, string state)
    { var updated = operation with { State = state }; Files.AtomicText(System.IO.Path.Combine(Journal, operation.Id + ".json"), Json.Write(updated)); return updated; }
    private void SaveTransaction(RenameTransaction transaction) => Files.AtomicText(System.IO.Path.Combine(Transactions, transaction.Id + ".json"), Json.Write(transaction));
    public async Task<List<string>> Recover(CancellationToken token = default)
    {
        var warnings = new List<string>();
        if (Directory.Exists(Transactions)) foreach (var file in Directory.EnumerateFiles(Transactions, "*.json"))
        {
            RenameTransaction transaction;
            try { transaction = Json.Read<RenameTransaction>(File.ReadAllText(file)); } catch { warnings.Add("Transação corrompida: " + System.IO.Path.GetFileName(file)); continue; }
            if (transaction.State == "committed")
            {
                foreach (var operation in History().Where(x => x.GroupId == transaction.Id && x.State == "pending"))
                    State(operation, File.Exists(operation.Destination) && await Files.Hash(operation.Destination, token) == operation.Sha256 ? "committed" : "needs-attention");
                continue;
            }
            if (transaction.State != "pending") continue;
            var complete = true;
            foreach (var entry in transaction.Plan.AsEnumerable().Reverse())
            {
                if (File.Exists(entry.Destination))
                {
                    if (!File.Exists(entry.Source) && await Files.Hash(entry.Destination, token) == entry.Sha256) File.Move(entry.Destination, entry.Source);
                    else { complete = false; warnings.Add("Conflito após interrupção: " + entry.Source); }
                }
                else if (!File.Exists(entry.Source) || await Files.Hash(entry.Source, token) != entry.Sha256) { complete = false; warnings.Add("Arquivo alterado após interrupção: " + entry.Source); }
            }
            var recoveredState = complete ? "rolled-back" : "needs-attention";
            SaveTransaction(transaction with { State = recoveredState });
            foreach (var operation in History().Where(x => x.GroupId == transaction.Id)) State(operation, recoveredState);
        }
        foreach (var operation in History().Where(x => x.State == "pending" && x.GroupId == null))
        {
            var source = File.Exists(operation.Source); var destination = File.Exists(operation.Destination);
            if ((!source || operation.Type == "restore") && destination && await Files.Hash(operation.Destination, token) == operation.Sha256) State(operation, "committed");
            else if (source && await Files.Hash(operation.Source, token) == operation.Sha256) State(operation, "aborted-original-preserved");
            else { State(operation, "needs-attention"); warnings.Add("Verifique a operação interrompida: " + operation.Source); }
        }
        History(); warnings.AddRange(Warnings); return warnings;
    }
    public async Task<FileOperation> Quarantine(string source, CancellationToken token = default)
    {
        var hash = await Files.Hash(source, token); Directory.CreateDirectory(Trash);
        var destination = System.IO.Path.Combine(Trash, Guid.NewGuid().ToString("N") + "_" + System.IO.Path.GetFileName(source));
        var record = Record("trash", source, destination, hash, "pending");
        FaultInjection?.Invoke("trash-journal");
        try
        {
            await CopyVerified(source, destination, token); FaultInjection?.Invoke("trash-copy");
            if (await Files.Hash(source, token) != hash || await Files.Hash(destination, token) != hash) throw new IOException("O original mudou durante a operação.");
            token.ThrowIfCancellationRequested(); File.Delete(source); FaultInjection?.Invoke("trash-delete"); return State(record, "committed");
        }
        catch { if (File.Exists(source)) State(record, "aborted-original-preserved"); throw; }
    }
    public async Task<string> Restore(FileOperation operation, CancellationToken token = default)
    {
        if (operation.Type is not ("trash" or "rename")) throw new InvalidOperationException("Operação não restaurável.");
        if (operation.State != "committed") throw new InvalidOperationException("Esta operação não foi concluída ou já foi desfeita.");
        if (await Files.Hash(operation.Destination, token) != operation.Sha256) throw new IOException("O arquivo mudou desde a operação; restauração automática bloqueada.");
        var path = Files.Unique(operation.Source);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var restoration = Record("restore", operation.Destination, path, operation.Sha256, "pending");
        if (operation.Type == "rename") File.Move(operation.Destination, path);
        else await CopyVerified(operation.Destination, path, token);
        State(restoration, "committed"); State(operation, "restored"); return path;
    }
    public static List<RenameEntry> PlanNames(IEnumerable<MediaItem> items)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<RenameEntry>(); var index = 0;
        foreach (var item in items.OrderByDescending(x => x.Taken).ThenBy(x => x.Path))
        {
            var stem = $"{++index:000000}_{item.Taken:yyyy-MM-dd_HH-mm-ss}";
            var directory = System.IO.Path.GetDirectoryName(item.Path)!; var extension = System.IO.Path.GetExtension(item.Path).ToLowerInvariant();
            var target = System.IO.Path.Combine(directory, stem + extension); var collision = 0;
            while (File.Exists(target) || reserved.Contains(target)) target = System.IO.Path.Combine(directory, stem + $"_{++collision:000}" + extension);
            reserved.Add(target); result.Add(new(item.Path, target, item.Sha256));
        }
        return result;
    }
    public async Task<IReadOnlyList<FileOperation>> Rename(IReadOnlyList<RenameEntry> plan, CancellationToken token = default)
    {
        var verifiedPlan = new List<RenameEntry>();
        foreach (var entry in plan)
        {
            if (!File.Exists(entry.Source) || File.Exists(entry.Destination)) throw new IOException("O plano mudou; gere uma nova prévia.");
            var hash = await Files.Hash(entry.Source, token);
            if (entry.Sha256.Length > 0 && hash != entry.Sha256) throw new IOException("O conteúdo mudou desde a prévia.");
            verifiedPlan.Add(entry with { Sha256 = hash });
        }
        plan = verifiedPlan;
        var applied = new List<FileOperation>(); var group = Guid.NewGuid().ToString("N"); var transaction = new RenameTransaction(group, plan.ToList(), "pending"); SaveTransaction(transaction);
        try
        {
            foreach (var entry in plan)
            { token.ThrowIfCancellationRequested(); var record = Record("rename", entry.Source, entry.Destination, entry.Sha256, "pending", group); FaultInjection?.Invoke("rename-journal"); File.Move(entry.Source, entry.Destination); applied.Add(record); FaultInjection?.Invoke("rename-move"); }
            SaveTransaction(transaction with { State = "committed" });
            for (var i = 0; i < applied.Count; i++) applied[i] = State(applied[i], "committed");
            return applied;
        }
        catch
        {
            var rolledBack = true;
            foreach (var record in applied.AsEnumerable().Reverse())
            { if (File.Exists(record.Destination) && !File.Exists(record.Source) && await Files.Hash(record.Destination, CancellationToken.None) == record.Sha256) File.Move(record.Destination, record.Source); else rolledBack = false; }
            var state = rolledBack ? "rolled-back" : "needs-attention"; SaveTransaction(transaction with { State = state });
            foreach (var record in History().Where(x => x.GroupId == group)) State(record, state);
            throw;
        }
    }
    public static async Task CopyVerified(string source, string destination, CancellationToken token = default)
    {
        if (File.Exists(destination)) throw new IOException("O destino já existe.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = File.OpenRead(source)) await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
            if (await Files.Hash(temp, token) != await Files.Hash(source, token)) throw new IOException("Falha na verificação da cópia.");
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
