namespace GuiaFotos.Core;

public static class FaceClustering
{
    // DBSCAN with a cannot-link constraint for different faces in the same photograph.
    // Sparse faces stay as singletons; they cannot bridge two dense components.
    public static List<FaceGroup> Cluster(IReadOnlyList<FaceObservation> input, VectorIndex index,
        double threshold = .65, int minimumFaces = 3, CancellationToken token = default)
    {
        if (minimumFaces < 2 || minimumFaces > 100 || threshold is < .1 or > .99) throw new ArgumentOutOfRangeException();
        var faces = input.OrderBy(x => x.Id, StringComparer.Ordinal).ToDictionary(x => x.Id);
        var visited = new HashSet<string>(); var assigned = new HashSet<string>(); var groups = new List<List<FaceObservation>>();
        List<FaceObservation> Neighbors(FaceObservation f) => index.Search(f.Embedding, threshold, token).Where(h => faces.ContainsKey(h.Id))
            .Select(h => faces[h.Id]).Where(x => x.Id == f.Id || x.MediaPath != f.MediaPath).ToList();
        foreach (var face in faces.Values)
        {
            token.ThrowIfCancellationRequested(); if (!visited.Add(face.Id)) continue;
            var neighbors = Neighbors(face); if (neighbors.Select(x => x.MediaPath).Distinct().Count() < minimumFaces) continue;
            var group = new List<FaceObservation> { face }; var media = new HashSet<string> { face.MediaPath }; assigned.Add(face.Id);
            var queue = new Queue<FaceObservation>(neighbors); var queued = neighbors.Select(x => x.Id).ToHashSet();
            while (queue.TryDequeue(out var next))
            {
                token.ThrowIfCancellationRequested(); if (assigned.Contains(next.Id) || media.Contains(next.MediaPath)) continue;
                assigned.Add(next.Id); media.Add(next.MediaPath); group.Add(next);
                if (!visited.Add(next.Id)) continue;
                var nearby = Neighbors(next);
                if (nearby.Select(x => x.MediaPath).Distinct().Count() >= minimumFaces)
                    foreach (var n in nearby) if (queued.Add(n.Id)) queue.Enqueue(n);
            }
            groups.Add(group);
        }
        groups.AddRange(faces.Values.Where(x => !assigned.Contains(x.Id)).Select(x => new List<FaceObservation> { x }));
        return groups.OrderByDescending(x => x.Count).ThenBy(x => x.Min(f => f.Id), StringComparer.Ordinal)
            .Select(g => new FaceGroup((g.Count == 1 ? "Rosto isolado " : "Grupo sugerido ") + g.Min(x => x.Id), g)).ToList();
    }
    public static List<FaceObservation> References(IEnumerable<FaceObservation> source, int maximum = 20)
    {
        var pool = source.OrderByDescending(x => x.Quality).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (pool.Count <= maximum) return pool;
        var selected = new List<FaceObservation> { pool[0] }; pool.RemoveAt(0);
        while (selected.Count < maximum && pool.Count > 0)
        {
            var next = pool.OrderByDescending(x => (1 - selected.Max(y => FaceEngine.Similarity(x.Embedding, y.Embedding))) * (.5 + .5 * x.Quality)).ThenBy(x => x.Id).First();
            selected.Add(next); pool.Remove(next);
        }
        return selected;
    }
}
