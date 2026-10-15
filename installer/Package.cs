using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuiaFotos.Setup;

// Installer = self-contained .NET executable + ZIP + 56-byte trailer.
// The last 16 bytes identify the trailer, preceded by ZIP length and SHA-256.
public static class Package
{
    public const string Marker = "GUIAFOTOSZIP-V1!";
    public const int TrailerSize = 56; // Int64 + SHA256 + ASCII marker
    public static (long Offset, long Length, byte[] Hash) ReadTrailer(Stream input)
    {
        if (input.Length < TrailerSize) throw new InvalidDataException("Instalador sem pacote.");
        input.Position = input.Length - TrailerSize;
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        long length = reader.ReadInt64(); var hash = reader.ReadBytes(32);
        if (Encoding.ASCII.GetString(reader.ReadBytes(16)) != Marker || length <= 0 || length > input.Length - TrailerSize)
            throw new InvalidDataException("Pacote do instalador inválido.");
        return (input.Length - TrailerSize - length, length, hash);
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw new InvalidDataException("Caminho inválido no pacote: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Arquivo fora do diretório de instalação.");
        return path;
    }

    public static async Task<long> Extract(string executable, string destination, Action<string> progress)
    {
        using var input = File.OpenRead(executable);
        var trailer = ReadTrailer(input);
        var zipPath = Path.Combine(destination, ".payload.zip");
        Directory.CreateDirectory(destination);
        input.Position = trailer.Offset;
        using (var output = File.Create(zipPath))
        {
            var bytes = new byte[1024 * 1024]; var remaining = trailer.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (remaining > 0)
            {
                var count = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining)));
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(bytes, 0, count); await output.WriteAsync(bytes.AsMemory(0, count)); remaining -= count;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), trailer.Hash))
                throw new InvalidDataException("A integridade do instalador não confere. Baixe novamente.");
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!seen.Add(entry.FullName)) throw new InvalidDataException("Arquivo duplicado no pacote.");
                var target = SafePath(destination, entry.FullName);
                if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new InvalidDataException("Link no pacote.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target); progress(entry.FullName);
            }
        }
        File.Delete(zipPath);
        await Verify(destination);
        return trailer.Offset;
    }

    public static async Task Verify(string directory)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(Path.Combine(directory, "SHA256SUMS.txt")))
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new InvalidDataException("Inventário inválido.");
            var name = line[66..];
            if (!listed.Add(name) || name == "SHA256SUMS.txt") throw new InvalidDataException("Inventário duplicado ou recursivo.");
            using var input = File.OpenRead(SafePath(directory, name));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(line[..64], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arquivo alterado: " + name);
        }
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(directory, x).Replace('\\', '/')).Where(x => x != "SHA256SUMS.txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(listed) || !listed.Contains("GuiaFotos.exe")) throw new InvalidDataException("Pacote incompleto.");
    }

    public static async Task WriteStub(string source, string target, long length)
    {
        using var input = File.OpenRead(source); using var output = File.Create(target);
        var bytes = new byte[1024 * 1024];
        while (length > 0)
        {
            var count = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(length, bytes.Length)));
            if (count == 0) throw new EndOfStreamException();
            await output.WriteAsync(bytes.AsMemory(0, count)); length -= count;
        }
    }
}

public sealed record InstalledFiles(string Product, string Version, string[] Files);

public static class Removal
{
    public const string Inventory = ".guiafotos-install.json";
    public static IReadOnlyList<string> Plan(string root, InstalledFiles inventory)
    {
        if (inventory.Product != "GuiaFotos" || inventory.Files.Length == 0) throw new InvalidDataException("Instalação não reconhecida.");
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A instalação é um link; remoção recusada.");
        var files = new List<string>();
        foreach (var name in inventory.Files)
        {
            var path = Package.SafePath(root, name); var parent = Path.GetDirectoryName(path);
            while (parent != null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Link na instalação; remoção recusada.");
                parent = Path.GetDirectoryName(parent);
            }
            files.Add(path);
        }
        return files;
    }
}
