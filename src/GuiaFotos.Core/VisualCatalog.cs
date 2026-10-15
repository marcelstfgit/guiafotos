using Microsoft.Data.Sqlite;

namespace GuiaFotos.Core;

public sealed class VisualCatalog : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private readonly string indexPath;
    private sealed record Pair(string A, string B);
    private sealed record Decisions(List<Pair> Joins, List<Pair> Apart, List<string> Preferred);
    public VisualCatalog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); indexPath = path + ".vptree";
        connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); connection.Open();
        Write("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS visual_media(path TEXT PRIMARY KEY,data TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS visual_join(a TEXT NOT NULL,b TEXT NOT NULL,PRIMARY KEY(a,b));" +
            "CREATE TABLE IF NOT EXISTS visual_apart(a TEXT NOT NULL,b TEXT NOT NULL,PRIMARY KEY(a,b));" +
            "CREATE TABLE IF NOT EXISTS visual_preferred(id TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS visual_undo(id INTEGER PRIMARY KEY AUTOINCREMENT,data TEXT NOT NULL);");
    }
    private void Write(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value); cmd.ExecuteNonQuery();
    }
    public static bool Current(VisualPhoto photo)
    {
        if (!File.Exists(photo.Path) || !File.Exists(photo.Thumbnail)) return false;
        var parts = photo.Stamp.Split(':'); var info = new FileInfo(photo.Path);
        return parts.Length >= 4 && parts[^1] == VisualFeatures.Version && long.TryParse(parts[0], out var size) && long.TryParse(parts[1], out var ticks) && size == info.Length && ticks == info.LastWriteTimeUtc.Ticks;
    }
    public List<VisualPhoto> Photos()
    {
        lock (gate) { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT data FROM visual_media"; using var rows = cmd.ExecuteReader(); var result = new List<VisualPhoto>();
            while (rows.Read()) { var p = Json.Read<VisualPhoto>(rows.GetString(0)); if (Current(p)) result.Add(p); } return result; }
    }
    public async Task<bool> Indexed(MediaItem item, string? neuralVersion = null, CancellationToken token = default)
    {
        VisualPhoto? photo;
        lock (gate) { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT data FROM visual_media WHERE path=$p"; cmd.Parameters.AddWithValue("$p", item.Path); photo = cmd.ExecuteScalar() is string data ? Json.Read<VisualPhoto>(data) : null; }
        if (photo == null || !Current(photo) || neuralVersion != null && photo.NeuralVersion != neuralVersion) return false;
        var sample = await MediaIndex.Sample(item.Path, token);
        return photo.Stamp == VisualFeatures.Stamp(item with { QuickSignature = sample });
    }
    public void Add(VisualPhoto photo)
    {
        lock (gate) {
            using var prior = connection.CreateCommand(); prior.CommandText = "SELECT data FROM visual_media WHERE path=$p"; prior.Parameters.AddWithValue("$p", photo.Path);
            if (prior.ExecuteScalar() is string oldData)
            {
                var old = Json.Read<VisualPhoto>(oldData);
                if (old.Stamp.Split(':').Take(3).SequenceEqual(photo.Stamp.Split(':').Take(3))) photo = photo with { Id = old.Id };
            }
            Write("INSERT INTO visual_media(path,data) VALUES($p,$d) ON CONFLICT(path) DO UPDATE SET data=excluded.data", ("$p", photo.Path), ("$d", Json.Write(photo))); }
    }
    private List<Pair> Pairs(string table)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT a,b FROM " + table; using var rows = cmd.ExecuteReader(); var result = new List<Pair>(); while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1))); return result;
    }
    private List<string> Preferred()
    { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id FROM visual_preferred"; using var rows = cmd.ExecuteReader(); var result = new List<string>(); while (rows.Read()) result.Add(rows.GetString(0)); return result; }
    private void Snapshot() => Write("INSERT INTO visual_undo(data) VALUES($d)", ("$d", Json.Write(new Decisions(Pairs("visual_join"), Pairs("visual_apart"), Preferred()))));
    private static Pair Ordered(string a, string b) => string.CompareOrdinal(a, b) < 0 ? new(a, b) : new(b, a);
    public List<PhotoFamily> Families(double threshold = .96, CancellationToken token = default, bool useNeural = true, bool includeSingles = false)
    {
        if (threshold is < .7 or > .9999) throw new ArgumentOutOfRangeException(nameof(threshold));
        lock (gate)
        {
            var photos = Photos().OrderByDescending(VisualFeatures.BestScore).ThenBy(p => p.Id, StringComparer.Ordinal).ToList(); var byId = photos.ToDictionary(p => p.Id);
            var index = new VectorIndex(photos.Select(p => new VectorEntry(p.Id, p.Embedding)), indexPath, token);
            var apart = Pairs("visual_apart").Select(p => Ordered(p.A, p.B)).ToHashSet();
            var parent = photos.ToDictionary(p => p.Id, p => p.Id);
            string Root(string id) { while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; } return id; }
            foreach (var pair in Pairs("visual_join")) if (parent.ContainsKey(pair.A) && parent.ContainsKey(pair.B))
            {
                var ra = Root(pair.A); var rb = Root(pair.B);
                if (apart.Any(p => parent.ContainsKey(p.A) && parent.ContainsKey(p.B) && ((Root(p.A) == ra && Root(p.B) == rb) || (Root(p.A) == rb && Root(p.B) == ra)))) continue;
                parent[rb] = ra;
            }
            var manual = photos.GroupBy(p => Root(p.Id)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
            var neural = photos.Where(p => useNeural && p.NeuralEmbedding != null).GroupBy(p => p.NeuralVersion).ToDictionary(g => g.Key, g => new VectorIndex(g.Select(p => new VectorEntry(p.Id, p.NeuralEmbedding!)), indexPath + "." + MediaIndex.XxHash64(System.Text.Encoding.UTF8.GetBytes(g.Key)).ToString("x16"), token));
            var assigned = new HashSet<string>(); var families = new List<PhotoFamily>(); var preferred = Preferred().ToHashSet();
            bool Matches(VisualPhoto a, VisualPhoto b)
            {
                if (apart.Contains(Ordered(a.Id, b.Id))) return false;
                if (Root(a.Id) == Root(b.Id)) return true;
                var aspectA = a.Quality.Width / (double)a.Quality.Height; var aspectB = b.Quality.Width / (double)b.Quality.Height;
                if (Math.Abs(Math.Log(aspectA / aspectB)) < .06 && VisualFeatures.Similarity(a, b) >= threshold) return true;
                return useNeural && a.NeuralEmbedding != null && b.NeuralEmbedding != null && a.NeuralVersion == b.NeuralVersion && FaceEngine.Similarity(a.NeuralEmbedding, b.NeuralEmbedding) >= .9;
            }
            foreach (var photo in photos)
            {
                token.ThrowIfCancellationRequested(); if (assigned.Contains(photo.Id)) continue;
                var group = manual.GetValueOrDefault(Root(photo.Id))?.Where(p => !assigned.Contains(p.Id)).ToList() ?? [photo];
                foreach (var member in group) assigned.Add(member.Id);
                var candidates = new HashSet<string>();
                foreach (var member in group)
                {
                    foreach (var hit in index.Search(member.Embedding, Math.Max(.7, threshold - .07), token)) candidates.Add(hit.Id);
                    if (member.NeuralEmbedding != null && neural.TryGetValue(member.NeuralVersion, out var n)) foreach (var hit in n.Search(member.NeuralEmbedding, .9, token)) candidates.Add(hit.Id);
                }
                foreach (var candidate in candidates.Where(byId.ContainsKey).Select(id => byId[id]).OrderByDescending(VisualFeatures.BestScore))
                {
                    token.ThrowIfCancellationRequested(); if (assigned.Contains(candidate.Id)) continue;
                    var batch = manual.GetValueOrDefault(Root(candidate.Id))?.Where(p => !assigned.Contains(p.Id)).ToList() ?? [candidate];
                    if (!batch.All(b => group.All(a => Matches(a, b)))) continue;
                    group.AddRange(batch); foreach (var b in batch) assigned.Add(b.Id);
                }
                if (group.Count > 1 || includeSingles) families.Add(new(group.Min(p => p.Id)!, group, group.FirstOrDefault(p => preferred.Contains(p.Id))?.Id, group.Any(p => manual.ContainsKey(Root(p.Id)))));
            }
            return families.OrderByDescending(f => f.Photos.Count).ToList();
        }
    }
    public void Join(IReadOnlyList<string> ids)
    {
        lock (gate)
        {
            ids = ids.Distinct().ToArray(); if (ids.Count < 2) return;
            var valid = Photos().Select(p => p.Id).ToHashSet(); if (ids.Any(id => !valid.Contains(id))) throw new ArgumentException("Foto não existe no índice atual.");
            var component = ids.ToHashSet(); bool expanded;
            do { expanded = false; foreach (var pair in Pairs("visual_join")) if (component.Contains(pair.A) || component.Contains(pair.B)) { expanded |= component.Add(pair.A); expanded |= component.Add(pair.B); } } while (expanded);
            if (Pairs("visual_apart").Any(p => component.Contains(p.A) && component.Contains(p.B) && !(ids.Contains(p.A) && ids.Contains(p.B))))
                throw new InvalidOperationException("Há uma separação salva entre esses grupos. Selecione os grupos completos para confirmar a união.");
            using var transaction = connection.BeginTransaction(); Snapshot();
            foreach (var pair in Pairs("visual_apart").Where(p => ids.Contains(p.A) && ids.Contains(p.B))) Write("DELETE FROM visual_apart WHERE a=$a AND b=$b", ("$a", pair.A), ("$b", pair.B));
            foreach (var b in ids.Skip(1)) { var pair = Ordered(ids[0], b); Write("INSERT OR IGNORE INTO visual_join(a,b) VALUES($a,$b)", ("$a", pair.A), ("$b", pair.B)); }
            transaction.Commit();
        }
    }
    public void Separate(IReadOnlyList<string> selected, IReadOnlyList<string> family)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction(); Snapshot(); var picked = selected.ToHashSet();
            foreach (var a in selected)
            {
                Write("DELETE FROM visual_join WHERE a=$a OR b=$a", ("$a", a));
                foreach (var b in family.Where(b => !picked.Contains(b))) { var p = Ordered(a, b); Write("INSERT OR IGNORE INTO visual_apart(a,b) VALUES($a,$b)", ("$a", p.A), ("$b", p.B)); }
            }
            foreach (var b in selected.Skip(1)) { var p = Ordered(selected[0], b); Write("INSERT OR IGNORE INTO visual_join(a,b) VALUES($a,$b)", ("$a", p.A), ("$b", p.B)); }
            var remainder = family.Where(id => !picked.Contains(id)).ToArray();
            foreach (var b in remainder.Skip(1)) { var p = Ordered(remainder[0], b); Write("INSERT OR IGNORE INTO visual_join(a,b) VALUES($a,$b)", ("$a", p.A), ("$b", p.B)); }
            transaction.Commit();
        }
    }
    public void Prefer(string photoId, IReadOnlyList<string> family)
    {
        if (!family.Contains(photoId)) throw new ArgumentException("Cópia preferida não pertence ao grupo.");
        lock (gate) { using var transaction = connection.BeginTransaction(); Snapshot(); foreach (var id in family) Write("DELETE FROM visual_preferred WHERE id=$i", ("$i", id)); Write("INSERT OR IGNORE INTO visual_preferred VALUES($i)", ("$i", photoId)); transaction.Commit(); }
    }
    public bool Undo()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id,data FROM visual_undo ORDER BY id DESC LIMIT 1"; long id; Decisions state;
            using (var rows = cmd.ExecuteReader()) { if (!rows.Read()) return false; id = rows.GetInt64(0); state = Json.Read<Decisions>(rows.GetString(1)); }
            using var transaction = connection.BeginTransaction(); Write("DELETE FROM visual_join; DELETE FROM visual_apart; DELETE FROM visual_preferred;");
            foreach (var p in state.Joins) Write("INSERT INTO visual_join VALUES($a,$b)", ("$a", p.A), ("$b", p.B));
            foreach (var p in state.Apart) Write("INSERT INTO visual_apart VALUES($a,$b)", ("$a", p.A), ("$b", p.B));
            foreach (var p in state.Preferred) Write("INSERT INTO visual_preferred VALUES($i)", ("$i", p));
            Write("DELETE FROM visual_undo WHERE id=$i", ("$i", id)); transaction.Commit(); return true;
        }
    }
    public void Remap(string source, string destination)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT data FROM visual_media WHERE path=$p"; cmd.Parameters.AddWithValue("$p", source); var data = cmd.ExecuteScalar() as string; if (data == null) return;
            var photo = Json.Read<VisualPhoto>(data) with { Path = destination }; using var transaction = connection.BeginTransaction();
            Write("DELETE FROM visual_media WHERE path=$s OR path=$d", ("$s", source), ("$d", destination)); Add(photo); transaction.Commit();
        }
    }
    public void Dispose() => connection.Dispose();
}
