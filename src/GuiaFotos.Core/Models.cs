using System.Security.Cryptography;
using System.Text.Json;

namespace GuiaFotos.Core;

public sealed record MediaItem(string Path, string Kind, long Size, DateTime Taken, string Sha256,
    string Thumbnail, string MetadataJson, ulong? Fingerprint = null, double Duration = 0,
    string VideoFingerprints = "", int Rating = 0, string Tags = "", string Album = "",
    long LastWriteUtcTicks = 0, string QuickSignature = "", ulong? PerceptualHash = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Details => $"{Taken:dd/MM/yyyy HH:mm} · {Size / 1048576.0:F1} MB · {Kind}";
}
public sealed record DuplicateGroup(string Type, double Confidence, IReadOnlyList<MediaItem> Items);
public sealed record RenameEntry(string Source, string Destination, string Sha256);
public sealed record FileOperation(string Id, string Type, string Source, string Destination, string Sha256, DateTime Time, string State = "committed", string? GroupId = null);
public sealed record VerifiedUpload(string Sha256, long Size);
public sealed record RenameTransaction(string Id, List<RenameEntry> Plan, string State);
public sealed record BackupEntry(string RelativePath, string RemotePath, string Sha256, long Size);
public sealed record BackupManifest(string Version, DateTime Time, List<BackupEntry> Files);
public sealed record RemoteItem(string Href, string Name, bool Folder, long Size, string ETag, string OriginalLocation = "");
public sealed record AiAction(string Operation, int[] Items, Dictionary<string, string>? Arguments = null);
public sealed record AiPlan(string Explanation, List<AiAction> Actions);
public sealed record ToolPaths(string ExifTool, string Ffmpeg, string Ffprobe, string Magick)
{
    public static ToolPaths Discover(string root)
    {
        string Find(string name) => OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(root, "tools", name + ".exe") : name == "magick" ? "convert" : name;
        return new(Find("exiftool"), Find("ffmpeg"), Find("ffprobe"), Find("magick"));
    }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("JSON vazio.");
}
public static class Files
{
    public static async Task<string> Hash(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    public static string Unique(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        for (var i = 1; File.Exists(path) || Directory.Exists(path); i++) path = System.IO.Path.Combine(directory, $"{stem}-{i:000}{extension}");
        return path;
    }
    public static void AtomicText(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(text); writer.Flush(); stream.Flush(true); }
        File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string SafeRelative(string path)
    {
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("Caminho relativo inválido.");
        return path;
    }
    public static string RestoreDestination(string root, string relative)
    {
        relative = SafeRelative(relative); root = System.IO.Path.GetFullPath(root);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Destino fora da pasta de recuperação.");
        var parent = System.IO.Path.GetDirectoryName(path);
        while (parent != null && !parent.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        { if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Pasta de recuperação contém um link para outro local."); parent = System.IO.Path.GetDirectoryName(parent); }
        return path;
    }
}
