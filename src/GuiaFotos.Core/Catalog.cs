using Microsoft.Data.Sqlite;

namespace GuiaFotos.Core;

public sealed class Catalog : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public Catalog(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS media(path TEXT PRIMARY KEY, item TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public void Add(MediaItem item)
    {
        lock (gate) { using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO media(path,item) VALUES($p,$i) ON CONFLICT(path) DO UPDATE SET item=excluded.item";
        command.Parameters.AddWithValue("$p", item.Path); command.Parameters.AddWithValue("$i", Json.Write(item)); command.ExecuteNonQuery(); }
    }
    public IReadOnlyList<MediaItem> All(string query = "")
    {
        lock (gate) { using var command = connection.CreateCommand(); command.CommandText = "SELECT item FROM media";
        using var reader = command.ExecuteReader(); var items = new List<MediaItem>();
        while (reader.Read()) { var item = Json.Read<MediaItem>(reader.GetString(0));
        if (File.Exists(item.Path) && (query.Length == 0 || $"{item.Name} {item.Path} {item.Tags} {item.Album}".Contains(query, StringComparison.OrdinalIgnoreCase))) items.Add(item); }
        return items.OrderByDescending(x => x.Taken).ThenBy(x => x.Path).ToList(); }
    }
    public void Remove(string path)
    { lock (gate) { using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM media WHERE path=$p"; command.Parameters.AddWithValue("$p", path); command.ExecuteNonQuery(); } }
    public void Dispose() => connection.Dispose();
}
