using Microsoft.Data.Sqlite;

namespace GuiaFotos.Core;

public sealed record Person(string Id, string Name, int ConfirmedFaces);
public sealed record PersonMatch(FaceObservation Face, double Score, bool Confirmed)
{
    public string Thumbnail => Face.Thumbnail;
    public string Description => Path.GetFileName(Face.MediaPath) + (Face.Retained ? " · detecção anterior preservada" : "") + (Confirmed ? " · confirmado" : $" · semelhança {Score:F2} (revisar)");
}
public sealed record FaceGroup(string Name, IReadOnlyList<FaceObservation> Faces)
{
    public string Description => $"{Name} · {Faces.Count} rostos / {Faces.Select(x => x.MediaPath).Distinct().Count()} fotos";
}

public sealed class PeopleCatalog : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private readonly FaceProfile profile;
    private readonly string indexPath;
    private VectorIndex? vectorIndex;
    private string? cachedVersion;
    private sealed record PersonRow(string Id, string Name);
    private sealed record RejectionRow(string Face, string Person);
    private sealed record DecisionState(List<PersonRow> People, Dictionary<string, string> Labels, List<RejectionRow> Rejected);
    public PeopleCatalog(string path, FaceProfile? profile = null)
    {
        this.profile = profile ?? FaceModels.Find("sface");
        indexPath = path + ".vptree";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS face_media(path TEXT PRIMARY KEY,stamp TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS faces(id TEXT PRIMARY KEY,path TEXT NOT NULL,data TEXT NOT NULL); CREATE INDEX IF NOT EXISTS faces_path ON faces(path);" +
            "CREATE TABLE IF NOT EXISTS people(id TEXT PRIMARY KEY,name TEXT NOT NULL UNIQUE COLLATE NOCASE);" +
            "CREATE TABLE IF NOT EXISTS face_labels(face_id TEXT PRIMARY KEY,person_id TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS face_rejected(face_id TEXT NOT NULL,person_id TEXT NOT NULL,PRIMARY KEY(face_id,person_id));" +
            "CREATE TABLE IF NOT EXISTS people_undo(id INTEGER PRIMARY KEY AUTOINCREMENT,data TEXT NOT NULL);";
        command.ExecuteNonQuery();
        using var metadata = connection.CreateCommand();
        metadata.CommandText = "CREATE TABLE IF NOT EXISTS face_profile(version TEXT NOT NULL); INSERT INTO face_profile(version) SELECT $v WHERE NOT EXISTS(SELECT 1 FROM face_profile); SELECT version FROM face_profile";
        metadata.Parameters.AddWithValue("$v", this.profile.Version);
        var stored = metadata.ExecuteScalar() as string;
        if (stored != this.profile.Version) { connection.Dispose(); throw new InvalidDataException("Índice pertence a outro modelo facial."); }
        using var existing = connection.CreateCommand(); existing.CommandText = "SELECT data FROM faces";
        using var reader = existing.ExecuteReader();
        while (reader.Read())
        {
            var face = Json.Read<FaceObservation>(reader.GetString(0));
            if (!Compatible(face)) { connection.Dispose(); throw new InvalidDataException("Índice contém vetores de outro modelo facial."); }
        }
    }
    private bool Compatible(FaceObservation face) => face.Stamp.EndsWith(":" + profile.Version, StringComparison.Ordinal) && face.Embedding.Length == profile.Dimensions;
    private int Write(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        return command.ExecuteNonQuery();
    }
    public bool Indexed(string path, string stamp)
    {
        lock (gate) { using var command = connection.CreateCommand(); command.CommandText = "SELECT stamp FROM face_media WHERE path=$p"; command.Parameters.AddWithValue("$p", path); return command.ExecuteScalar() as string == stamp; }
    }
    public void Replace(string path, string stamp, IReadOnlyList<FaceObservation> faces)
    {
        if (!stamp.EndsWith(":" + profile.Version, StringComparison.Ordinal) || faces.Any(f => f.MediaPath != path || f.Stamp != stamp || !Compatible(f) || f.Embedding.Any(x => !float.IsFinite(x)))) throw new ArgumentException("Rostos não correspondem à foto.");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            // Preserve human annotations when detector/tiling changes on the same
            // unchanged photo. Match geometry one-to-one before replacing the index.
            using (var prior = connection.CreateCommand())
            {
                prior.CommandText = "SELECT data FROM faces WHERE path=$p"; prior.Parameters.AddWithValue("$p", path);
                var oldFaces = new List<FaceObservation>(); using (var rows = prior.ExecuteReader()) while (rows.Read()) oldFaces.Add(Json.Read<FaceObservation>(rows.GetString(0)));
                var protectedIds = Labels().Keys.Concat(State().Rejected.Select(r => r.Face)).ToHashSet();
                var used = new HashSet<string>(); var updated = new List<FaceObservation>();
                foreach (var face in faces)
                {
                    DetectedFace Box(FaceObservation f)
                    {
                        double width = f.CoordinateWidth, height = f.CoordinateHeight;
                        if (width <= 0 || height <= 0) { var ratio = Math.Min(1, 1600d / Math.Max(1, Math.Max(face.CoordinateWidth, face.CoordinateHeight))); width = Math.Max(1, face.CoordinateWidth * ratio); height = Math.Max(1, face.CoordinateHeight * ratio); }
                        return new((float)(f.X / width), (float)(f.Y / height), (float)(f.Width / width), (float)(f.Height / height), 1, []);
                    }
                    var previous = oldFaces.Where(f => !used.Contains(f.Id) && f.Stamp.Split(':').Take(3).SequenceEqual(stamp.Split(':').Take(3)))
                        .Select(f => (Face: f, Overlap: FaceEngine.IntersectionOverUnion(Box(f), Box(face))))
                        .Where(x => x.Overlap >= .6 && FaceEngine.Similarity(x.Face.Embedding, face.Embedding) >= .5).OrderByDescending(x => x.Overlap).FirstOrDefault().Face;
                    if (previous != null) { used.Add(previous.Id); updated.Add(face with { Id = previous.Id }); }
                    else updated.Add(oldFaces.Any(f => f.Id == face.Id) || updated.Any(f => f.Id == face.Id) ? face with { Id = Guid.NewGuid().ToString("N") } : face);
                }
                foreach (var previous in oldFaces.Where(f => !used.Contains(f.Id) && protectedIds.Contains(f.Id) && f.Stamp.Split(':').Take(3).SequenceEqual(stamp.Split(':').Take(3))))
                    if (updated.All(f => f.Id != previous.Id)) updated.Add(previous with { Stamp = stamp, Retained = true });
                faces = updated;
            }
            var ids = faces.Select(x => x.Id).ToHashSet();
            using (var old = connection.CreateCommand())
            {
                old.CommandText = "SELECT id FROM faces WHERE path=$p"; old.Parameters.AddWithValue("$p", path);
                var removed = new List<string>(); using (var rows = old.ExecuteReader()) while (rows.Read()) if (!ids.Contains(rows.GetString(0))) removed.Add(rows.GetString(0));
                foreach (var id in removed) { Write("DELETE FROM face_labels WHERE face_id=$i", ("$i", id)); Write("DELETE FROM face_rejected WHERE face_id=$i", ("$i", id)); }
            }
            Write("DELETE FROM faces WHERE path=$p", ("$p", path));
            foreach (var face in faces) Write("INSERT INTO faces(id,path,data) VALUES($i,$p,$d)", ("$i", face.Id), ("$p", path), ("$d", Json.Write(face)));
            Write("INSERT INTO face_media(path,stamp) VALUES($p,$s) ON CONFLICT(path) DO UPDATE SET stamp=excluded.stamp", ("$p", path), ("$s", stamp));
            transaction.Commit(); Invalidate();
        }
    }
    private void Invalidate() { vectorIndex = null; cachedVersion = null; }
    public List<FaceObservation> Faces(int limit = int.MaxValue, bool onlyUnknown = false)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT data FROM faces " + (onlyUnknown ? "WHERE id NOT IN (SELECT face_id FROM face_labels)" : "");
            using var rows = command.ExecuteReader(); var result = new List<FaceObservation>();
            while (result.Count < limit && rows.Read())
            {
                var face = Json.Read<FaceObservation>(rows.GetString(0));
                if (File.Exists(face.MediaPath) && File.Exists(face.Thumbnail))
                {
                    var parts = face.Stamp.Split(':'); var info = new FileInfo(face.MediaPath);
                    if (parts.Length >= 2 && long.TryParse(parts[0], out var size) && long.TryParse(parts[1], out var ticks) && size == info.Length && ticks == info.LastWriteTimeUtc.Ticks) result.Add(face);
                }
            }
            return result;
        }
    }
    private Dictionary<string, string> Labels()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT face_id,person_id FROM face_labels";
        using var rows = command.ExecuteReader(); var result = new Dictionary<string, string>(); while (rows.Read()) result.Add(rows.GetString(0), rows.GetString(1)); return result;
    }
    public List<Person> People()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT p.id,p.name,COUNT(l.face_id) FROM people p LEFT JOIN face_labels l ON l.person_id=p.id GROUP BY p.id,p.name ORDER BY p.name";
            using var rows = command.ExecuteReader(); var result = new List<Person>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt32(2))); return result;
        }
    }
    public string Name(string faceId, string name)
    {
        name = name.Trim(); if (name.Length is < 1 or > 100) throw new ArgumentException("Informe um nome de até 100 caracteres.");
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM people WHERE name=$n"; command.Parameters.AddWithValue("$n", name);
            var id = command.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
            Write("INSERT OR IGNORE INTO people(id,name) VALUES($i,$n)", ("$i", id), ("$n", name)); Confirm(faceId, id); return id;
        }
    }
    public void Confirm(string faceId, string personId)
    {
        lock (gate)
        {
            using var exists = connection.CreateCommand(); exists.CommandText = "SELECT path FROM faces WHERE id=$f AND EXISTS(SELECT 1 FROM people WHERE id=$p)";
            exists.Parameters.AddWithValue("$f", faceId); exists.Parameters.AddWithValue("$p", personId);
            if (exists.ExecuteScalar() is not string path || !File.Exists(path)) throw new ArgumentException("Rosto ou pessoa não existe no índice atual.");
            Write("INSERT INTO face_labels(face_id,person_id) VALUES($f,$p) ON CONFLICT(face_id) DO UPDATE SET person_id=excluded.person_id", ("$f", faceId), ("$p", personId));
            Write("DELETE FROM face_rejected WHERE face_id=$f AND person_id=$p", ("$f", faceId), ("$p", personId));
        }
    }
    public void Reject(string faceId, string personId)
    {
        lock (gate)
        {
            Write("INSERT OR IGNORE INTO face_rejected(face_id,person_id) VALUES($f,$p)", ("$f", faceId), ("$p", personId));
            Write("DELETE FROM face_labels WHERE face_id=$f AND person_id=$p", ("$f", faceId), ("$p", personId));
        }
    }
    public List<PersonMatch> Search(string personId, double threshold = .5, CancellationToken token = default)
    {
        if (threshold < .1 || threshold > .95) throw new ArgumentOutOfRangeException(nameof(threshold));
        lock (gate)
        {
            var faces = Faces(); var labels = Labels();
            var reference = FaceClustering.References(faces.Where(x => labels.GetValueOrDefault(x.Id) == personId));
            if (reference.Count == 0) throw new InvalidOperationException("Nomeie ou confirme ao menos um rosto desta pessoa.");
            using var command = connection.CreateCommand(); command.CommandText = "SELECT face_id FROM face_rejected WHERE person_id=$p"; command.Parameters.AddWithValue("$p", personId);
            var rejected = new HashSet<string>(); using (var rows = command.ExecuteReader()) while (rows.Read()) rejected.Add(rows.GetString(0));
            var vectors = Index(faces, token); var candidates = new Dictionary<string, double>();
            foreach (var r in reference) foreach (var hit in vectors.Search(r.Embedding, threshold, token)) candidates[hit.Id] = Math.Max(candidates.GetValueOrDefault(hit.Id, -1), hit.Similarity);
            var result = new List<PersonMatch>();
            foreach (var face in faces.Where(f => candidates.ContainsKey(f.Id) || labels.GetValueOrDefault(f.Id) == personId))
            {
                token.ThrowIfCancellationRequested();
                if (rejected.Contains(face.Id) || labels.TryGetValue(face.Id, out var label) && label != personId) continue;
                var confirmed = labels.GetValueOrDefault(face.Id) == personId;
                var score = reference.Max(x => FaceEngine.Similarity(x.Embedding, face.Embedding));
                if (confirmed || score >= threshold) result.Add(new(face, score, confirmed));
            }
            return result.OrderByDescending(x => x.Confirmed).ThenByDescending(x => x.Score).ToList();
        }
    }
    private VectorIndex Index(List<FaceObservation> faces, CancellationToken token)
    {
        var version = string.Join("|", faces.Select(x => x.Id + ":" + x.Stamp));
        if (vectorIndex == null || cachedVersion != version)
        {
            vectorIndex = new VectorIndex(faces.Select(x => new VectorEntry(x.Id, x.Embedding)), indexPath, token);
            cachedVersion = version;
        }
        return vectorIndex;
    }
    public List<FaceGroup> UnknownGroups(double threshold = .65, int minimumFaces = 3, CancellationToken token = default)
    {
        lock (gate)
        {
            var faces = Faces(); var labels = Labels();
            var unknown = faces.Where(x => !labels.ContainsKey(x.Id)).ToList();
            return FaceClustering.Cluster(unknown, Index(faces, token), threshold, minimumFaces, token);
        }
    }
    private DecisionState State()
    {
        var people = People().Select(x => new PersonRow(x.Id, x.Name)).ToList(); var rejected = new List<RejectionRow>();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT face_id,person_id FROM face_rejected";
        using var rows = command.ExecuteReader(); while (rows.Read()) rejected.Add(new(rows.GetString(0), rows.GetString(1)));
        return new(people, Labels(), rejected);
    }
    private void Snapshot() { Write("INSERT INTO people_undo(data) VALUES($d)", ("$d", Json.Write(State()))); Write("DELETE FROM people_undo WHERE id NOT IN(SELECT id FROM people_undo ORDER BY id DESC LIMIT 50)"); }
    public void AssignMany(IReadOnlyList<string> faceIds, string name)
    {
        lock (gate)
        {
            if (faceIds.Count == 0) return;
            using var transaction = connection.BeginTransaction(); Snapshot();
            foreach (var id in faceIds) Name(id, name);
            transaction.Commit();
        }
    }
    public void Merge(string sourcePerson, string targetPerson) => MergeMany([sourcePerson], targetPerson);
    public void MergeMany(IReadOnlyList<string> sources, string targetPerson)
    {
        lock (gate)
        {
            sources = sources.Distinct().Where(p => p != targetPerson).ToArray(); if (sources.Count == 0) return;
            var known = People().Select(p => p.Id).ToHashSet();
            if (!known.Contains(targetPerson) || sources.Any(s => !known.Contains(s))) throw new ArgumentException("Pessoa inexistente.");
            using var transaction = connection.BeginTransaction(); Snapshot();
            foreach (var sourcePerson in sources) {
            Write("UPDATE face_labels SET person_id=$t WHERE person_id=$s", ("$t", targetPerson), ("$s", sourcePerson));
            Write("INSERT OR IGNORE INTO face_rejected SELECT face_id,$t FROM face_rejected WHERE person_id=$s", ("$t", targetPerson), ("$s", sourcePerson));
            Write("DELETE FROM face_rejected WHERE person_id=$s OR (person_id=$t AND face_id IN(SELECT face_id FROM face_labels WHERE person_id=$t))", ("$s", sourcePerson), ("$t", targetPerson));
            Write("DELETE FROM people WHERE id=$s", ("$s", sourcePerson)); } transaction.Commit();
        }
    }
    public void RejectMany(IReadOnlyList<string> faceIds, string personId)
    {
        if (faceIds.Count == 0) return;
        lock (gate) { using var transaction = connection.BeginTransaction(); Snapshot(); foreach (var id in faceIds) Reject(id, personId); transaction.Commit(); }
    }
    public void ConfirmMany(IReadOnlyList<string> faceIds, string personId)
    {
        if (faceIds.Count == 0) return;
        lock (gate) { using var transaction = connection.BeginTransaction(); Snapshot(); foreach (var id in faceIds) Confirm(id, personId); transaction.Commit(); }
    }
    public bool Undo()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT id,data FROM people_undo ORDER BY id DESC LIMIT 1";
            long id; DecisionState state;
            using (var rows = command.ExecuteReader()) { if (!rows.Read()) return false; id = rows.GetInt64(0); state = Json.Read<DecisionState>(rows.GetString(1)); }
            using var transaction = connection.BeginTransaction();
            Write("DELETE FROM face_labels; DELETE FROM face_rejected; DELETE FROM people;");
            foreach (var p in state.People) Write("INSERT INTO people(id,name) VALUES($i,$n)", ("$i", p.Id), ("$n", p.Name));
            foreach (var pair in state.Labels) Write("INSERT INTO face_labels SELECT $f,$p WHERE EXISTS(SELECT 1 FROM faces WHERE id=$f)", ("$f", pair.Key), ("$p", pair.Value));
            foreach (var r in state.Rejected) Write("INSERT INTO face_rejected SELECT $f,$p WHERE EXISTS(SELECT 1 FROM faces WHERE id=$f)", ("$f", r.Face), ("$p", r.Person));
            Write("DELETE FROM people_undo WHERE id=$i", ("$i", id)); transaction.Commit(); return true;
        }
    }
    public void Remap(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT data FROM faces WHERE path=$p"; command.Parameters.AddWithValue("$p", source);
            var faces = new List<FaceObservation>(); using (var rows = command.ExecuteReader()) while (rows.Read()) faces.Add(Json.Read<FaceObservation>(rows.GetString(0)));
            using var transaction = connection.BeginTransaction();
            Write("DELETE FROM face_labels WHERE face_id IN (SELECT id FROM faces WHERE path=$d)", ("$d", destination));
            Write("DELETE FROM face_rejected WHERE face_id IN (SELECT id FROM faces WHERE path=$d)", ("$d", destination));
            Write("DELETE FROM faces WHERE path=$d", ("$d", destination));
            Write("DELETE FROM face_media WHERE path=$d", ("$d", destination));
            foreach (var face in faces) Write("UPDATE faces SET path=$p,data=$d WHERE id=$i", ("$p", destination), ("$d", Json.Write(face with { MediaPath = destination })), ("$i", face.Id));
            Write("UPDATE face_media SET path=$d WHERE path=$s", ("$d", destination), ("$s", source));
            transaction.Commit(); Invalidate();
        }
    }
    public void Dispose() => connection.Dispose();
}
