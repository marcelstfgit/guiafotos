using System.Security.Cryptography;
using System.Text;

namespace GuiaFotos.Core;

public interface ISecrets { string? Get(string name); void Set(string name, string value); void Remove(string name); }
public sealed class WindowsSecrets(string directory) : ISecrets
{
    private string PathFor(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9_-]{1,64}$")) throw new ArgumentException("Nome de credencial inválido.");
        return System.IO.Path.Combine(directory, name + ".secret");
    }
    public string? Get(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Credenciais persistentes exigem o DPAPI do Windows.");
        var path = PathFor(name); if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
    }
    public void Set(string name, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Credenciais persistentes exigem o DPAPI do Windows.");
        Directory.CreateDirectory(directory); var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        var path = PathFor(name); var temp = path + ".partial"; File.WriteAllBytes(temp, bytes); File.Move(temp, path, true);
    }
    public void Remove(string name) { var path = PathFor(name); if (File.Exists(path)) File.Delete(path); }
}
public sealed class MemorySecrets : ISecrets
{
    private readonly Dictionary<string, string> values = [];
    public string? Get(string name) => values.GetValueOrDefault(name);
    public void Set(string name, string value) => values[name] = value;
    public void Remove(string name) => values.Remove(name);
}
public sealed record Settings(string NextcloudUrl = "", string NextcloudUser = "", string NextcloudFolder = "GuiaFotos",
    string AiProvider = "OpenAI", string AiModel = "gpt-4.1-mini", string ExportDirectory = "")
{
    public static Settings Load(string path) => File.Exists(path) ? Json.Read<Settings>(File.ReadAllText(path)) : new();
    public void Save(string path) => Files.AtomicText(path, Json.Write(this));
}
