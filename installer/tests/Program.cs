using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GuiaFotos.Setup;
using GuiaFotos.Core;

var temp = Path.Combine(Path.GetTempPath(), "guiafotos-setup-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp); int passed = 0;
if (args.Length == 2 && args[0] == "--verify-installer")
{
    try
    {
        var offset = await Package.Extract(Path.GetFullPath(args[1]), temp, _ => { });
        if (File.Exists(Path.Combine(temp, "portable.marker"))) throw new Exception("Instalador contém marcador portátil");
        Console.WriteLine($"PASS instalador real: SHA256 do ZIP, manifesto completo e {Directory.GetFiles(temp, "*", SearchOption.AllDirectories).Length} arquivos; stub={offset} bytes");
    }
    finally { Directory.Delete(temp, recursive: true); }
    return;
}
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException) { Check(true, name); return; } throw new Exception("Não rejeitou: " + name); }
async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException) { Check(true, name); return; } throw new Exception("Não rejeitou: " + name); }
string MakeSetup(string name, string entry, bool corrupt = false)
{
    using var zipBytes = new MemoryStream();
    using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
    {
        var bytes = Encoding.UTF8.GetBytes("aplicativo de teste");
        using (var file = zip.CreateEntry(entry).Open()) file.Write(bytes);
        using var manifest = new StreamWriter(zip.CreateEntry("SHA256SUMS.txt").Open());
        manifest.WriteLine(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "  " + entry);
    }
    var payload = zipBytes.ToArray(); var path = Path.Combine(temp, name);
    using var output = new BinaryWriter(File.Create(path));
    output.Write(Encoding.ASCII.GetBytes("MZ-STUB")); output.Write(payload);
    output.Write((long)payload.Length); var hash = SHA256.HashData(payload); if (corrupt) hash[0] ^= 1;
    output.Write(hash); output.Write(Encoding.ASCII.GetBytes(Package.Marker)); return path;
}
try
{
    foreach (var path in new[] { "../foto.jpg", "/foto.jpg", "C:/foto.jpg", "tools/../../foto.jpg", "tools\\foto.jpg", "tools/../Data/x", "folder./a" })
        Reject(() => Package.SafePath(temp, path), "recusa caminho " + path);
    Check(AppPaths.Workspace(temp, "/appdata") == Path.Combine("/appdata", "GuiaFotos"), "instalado usa AppData");
    File.WriteAllText(Path.Combine(temp, "portable.marker"), "");
    Check(AppPaths.Workspace(temp, "/appdata") == Path.Combine(temp, "Data"), "portátil usa Data junto ao executável");
    var valid = MakeSetup("valid.exe", "GuiaFotos.exe"); var destination = Path.Combine(temp, "valid");
    var offset = await Package.Extract(valid, destination, _ => { });
    Check(offset == 7 && File.ReadAllText(Path.Combine(destination, "GuiaFotos.exe")) == "aplicativo de teste", "extrai pacote e confere manifesto");
    await Package.WriteStub(valid, Path.Combine(temp, "stub.exe"), offset);
    Check(File.ReadAllText(Path.Combine(temp, "stub.exe")) == "MZ-STUB", "desinstalador exclui ZIP e trailer");
    File.WriteAllText(Path.Combine(destination, "GuiaFotos.exe"), "alterado");
    await RejectAsync(() => Package.Verify(destination), "detecta arquivo alterado");
    await RejectAsync(() => Package.Extract(MakeSetup("bad.exe", "GuiaFotos.exe", true), Path.Combine(temp, "bad"), _ => { }), "rejeita hash de payload adulterado");
    await RejectAsync(() => Package.Extract(MakeSetup("traversal.exe", "../escape.txt"), Path.Combine(temp, "traversal"), _ => { }), "ZIP não escapa do staging");
    Check(!File.Exists(Path.Combine(temp, "escape.txt")), "nenhum arquivo escrito fora do staging");
    var inventory = new InstalledFiles("GuiaFotos", "0.1.1", ["GuiaFotos.exe"]);
    File.WriteAllText(Path.Combine(destination, "foto-do-usuario.jpg"), "preservar");
    var removal = Removal.Plan(destination, inventory);
    Check(removal.Count == 1 && !removal.Contains(Path.Combine(destination, "foto-do-usuario.jpg")), "remoção só lista arquivos instalados");
    Reject(() => Removal.Plan(destination, inventory with { Files = ["../Data/catalog.sqlite"] }), "manifesto de remoção não alcança catálogo");
    Directory.CreateDirectory(Path.Combine(temp, "outside"));
    Directory.CreateSymbolicLink(Path.Combine(destination, "link"), Path.Combine(temp, "outside"));
    Reject(() => Removal.Plan(destination, inventory with { Files = ["link/arquivo"] }), "remoção recusa pasta com link");
    using var invalid = new MemoryStream(new byte[100]);
    Reject(() => Package.ReadTrailer(invalid), "recusa trailer inválido");
    Console.WriteLine($"RESULT passed={passed} failed=0");
}
finally { Directory.Delete(temp, recursive: true); }
