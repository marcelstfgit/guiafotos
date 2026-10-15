using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace GuiaFotos.Core;

public sealed class Nextcloud : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri server;
    private readonly string username;
    public List<string> Warnings { get; } = [];
    public Nextcloud(string url, string user, string password, HttpMessageHandler? handler = null, bool allowLoopbackTest = false)
    {
        server = new Uri(url.TrimEnd('/') + "/"); username = user;
        if (server.Scheme != "https" && !(allowLoopbackTest && server.IsLoopback)) throw new ArgumentException("Nextcloud requer HTTPS.");
        if (server.UserInfo.Length > 0 || server.Query.Length > 0 || server.Fragment.Length > 0) throw new ArgumentException("URL de servidor inválida.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
    }
    private Uri Url(string area, string path = "") => new(server, "remote.php/dav/" + area + "/" + Uri.EscapeDataString(username) + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
    public async Task<List<RemoteItem>> List(string path = "", bool trash = false, CancellationToken token = default)
    {
        var url = Url(trash ? "trashbin" : "files", trash ? "trash" : path);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "1"); request.Content = new StringContent("<d:propfind xmlns:d='DAV:'><d:allprop/></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await client.SendAsync(request, token); response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(token)); XNamespace dav = "DAV:"; XNamespace nc = "http://nextcloud.org/ns";
        var result = new List<RemoteItem>();
        foreach (var entry in xml.Descendants(dav + "response"))
        {
            var href = entry.Element(dav + "href")?.Value ?? ""; var target = new Uri(server, href);
            if (target.AbsolutePath.TrimEnd('/') == url.AbsolutePath.TrimEnd('/')) continue;
            var prop = entry.Elements(dav + "propstat").FirstOrDefault(x => x.Element(dav + "status")?.Value.Contains(" 200 ") == true)?.Element(dav + "prop");
            if (prop == null) continue;
            var name = prop.Element(nc + "trashbin-filename")?.Value ?? Uri.UnescapeDataString(target.AbsolutePath.TrimEnd('/').Split('/').Last());
            long.TryParse(prop.Element(dav + "getcontentlength")?.Value, out var size);
            result.Add(new(href, name, prop.Element(dav + "resourcetype")?.Element(dav + "collection") != null, size, prop.Element(dav + "getetag")?.Value ?? "", prop.Element(nc + "trashbin-original-location")?.Value ?? ""));
        }
        return result;
    }
    public async Task EnsureFolder(string path, CancellationToken token = default)
    {
        var parts = Files.SafeRelative(path).Split('/'); var current = "";
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : current + "/" + part;
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Url("files", current));
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) continue;
            response.EnsureSuccessStatusCode();
        }
    }
    public async Task<VerifiedUpload> Upload(string source, string remote, CancellationToken token = default)
    {
        remote = Files.SafeRelative(remote); var parent = remote.Contains('/') ? remote[..remote.LastIndexOf('/')] : "";
        if (parent.Length > 0) await EnsureFolder(parent, token);
        var temp = remote + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using var input = new HashingReadStream(File.OpenRead(source)); var size = input.Length;
            using var put = new HttpRequestMessage(HttpMethod.Put, Url("files", temp)) { Content = new StreamContent(input) };
            put.Content.Headers.ContentLength = size; put.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var sent = await client.SendAsync(put, token); sent.EnsureSuccessStatusCode();
            if (input.BytesRead != size) throw new IOException("O tamanho enviado mudou durante o upload.");
            var hash = input.FinishHash();
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url("files", temp)), token); head.EnsureSuccessStatusCode();
            if (head.Content.Headers.ContentLength is long reported && reported != size) throw new IOException("Servidor recebeu tamanho incorreto.");
            // Valida SHA-256 lendo o conteúdo remoto, mesmo quando o servidor não publica checksums.
            using var check = await client.GetAsync(Url("files", temp), HttpCompletionOption.ResponseHeadersRead, token); check.EnsureSuccessStatusCode();
            await using var checkStream = await check.Content.ReadAsStreamAsync(token);
            var remoteHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(checkStream, token)).ToLowerInvariant();
            if (remoteHash != hash) throw new IOException("Checksum do upload não confere.");
            using var move = new HttpRequestMessage(new HttpMethod("MOVE"), Url("files", temp));
            move.Headers.Add("Destination", Url("files", remote).AbsoluteUri); move.Headers.Add("Overwrite", "F");
            using var result = await client.SendAsync(move, token); result.EnsureSuccessStatusCode();
            return new VerifiedUpload(hash, size);
        }
        catch
        { try { using var cleanup = await client.DeleteAsync(Url("files", temp), CancellationToken.None); } catch { /* preserva erro original */ } throw; }
    }
    public async Task Download(string remote, string destination, string? expectedHash = null, CancellationToken token = default, long? expectedSize = null, long? maxBytes = null)
    {
        Files.SafeRelative(remote); if (File.Exists(destination)) throw new IOException("Escolha um destino que não exista.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await client.GetAsync(Url("files", remote), HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long reported && (expectedSize != null && reported != expectedSize || maxBytes != null && reported > maxBytes)) throw new IOException("Tamanho do download não confere ou excede o limite.");
            string downloadedHash;
            await using (var input = new HashingReadStream(await response.Content.ReadAsStreamAsync(token))) await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long received = 0; int length;
                while ((length = await input.ReadAsync(buffer, token)) > 0)
                { received += length; if (maxBytes != null && received > maxBytes || expectedSize != null && received > expectedSize) throw new IOException("Download excedeu o limite declarado."); await output.WriteAsync(buffer.AsMemory(0, length), token); }
                await output.FlushAsync(token); output.Flush(true);
                downloadedHash = input.FinishHash();
            }
            if (response.Content.Headers.ContentLength is long size && new FileInfo(temp).Length != size) throw new IOException("Download incompleto.");
            if (expectedSize != null && new FileInfo(temp).Length != expectedSize) throw new IOException("Tamanho do backup não confere.");
            if (expectedHash != null && downloadedHash != expectedHash) throw new IOException("Checksum do backup não confere.");
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<string> Backup(IReadOnlyList<MediaItem> items, string root, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (items.Count == 0) throw new ArgumentException("Selecione arquivos para fazer backup.");
        root = Files.SafeRelative(root); var snapshot = root + "/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var manifest = new BackupManifest("1", DateTime.UtcNow, []); var index = 0;
        var baseDirectory = CommonDirectory(items.Select(x => x.Path).ToList());
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); progress?.Report($"Backup {++index}/{items.Count}: {item.Name}");
            var remote = snapshot + "/" + index.ToString("000000") + "_" + item.Name;
            var verified = await Upload(item.Path, remote, token);
            var relative = baseDirectory.Length > 0 ? System.IO.Path.GetRelativePath(baseDirectory, item.Path).Replace('\\', '/') : index.ToString("000000") + "_" + item.Name;
            manifest.Files.Add(new(Files.SafeRelative(relative), remote, verified.Sha256, verified.Size));
        }
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guiafotos-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try { Files.AtomicText(file, Json.Write(manifest)); await Upload(file, snapshot + "/manifest.json", token); }
        finally { if (File.Exists(file)) File.Delete(file); }
        return snapshot;
    }
    private static string CommonDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "";
        var candidate = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(paths[0]))!;
        while (candidate.Length > 0 && paths.Any(p => System.IO.Path.GetRelativePath(candidate, System.IO.Path.GetFullPath(p)).StartsWith("..") || System.IO.Path.IsPathRooted(System.IO.Path.GetRelativePath(candidate, p))))
            candidate = System.IO.Path.GetDirectoryName(candidate) ?? "";
        return candidate;
    }
    public async Task<List<RemoteItem>> Backups(string root, CancellationToken token = default)
    {
        Warnings.Clear(); var result = new List<RemoteItem>();
        foreach (var folder in (await List(root, token: token)).Where(x => x.Folder))
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guiafotos-list-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var snapshot = root + "/" + folder.Name;
                await Download(snapshot + "/manifest.json", temp, token: token, maxBytes: 10 * 1024 * 1024);
                var manifest = Json.Read<BackupManifest>(await File.ReadAllTextAsync(temp, token)); ValidateManifest(manifest, snapshot);
                result.Add(folder);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Warnings.Add("Backup incompleto ou inválido ignorado: " + folder.Name); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return result;
    }
    public static void ValidateManifest(BackupManifest manifest, string? snapshot = null)
    {
        if (manifest.Version != "1" || manifest.Files.Count is 0 or > 1000000) throw new InvalidDataException("Manifesto de backup inválido.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var remotePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in manifest.Files)
        {
            Files.SafeRelative(item.RelativePath); Files.SafeRelative(item.RemotePath);
            if (!paths.Add(item.RelativePath) || !remotePaths.Add(item.RemotePath) || item.Size < 0 || !System.Text.RegularExpressions.Regex.IsMatch(item.Sha256, "^[0-9a-f]{64}$") || snapshot != null && !item.RemotePath.StartsWith(Files.SafeRelative(snapshot) + "/", StringComparison.Ordinal)) throw new InvalidDataException("Manifesto contém caminho, tamanho ou checksum inválido.");
        }
    }
    public async Task RestoreBackup(string snapshot, string folder, CancellationToken token = default)
    {
        Files.SafeRelative(snapshot); var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guiafotos-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await Download(snapshot + "/manifest.json", temp, token: token, maxBytes: 10 * 1024 * 1024); var manifest = Json.Read<BackupManifest>(await File.ReadAllTextAsync(temp, token)); ValidateManifest(manifest, snapshot);
            foreach (var entry in manifest.Files)
            {
                var destination = Files.Unique(Files.RestoreDestination(folder, entry.RelativePath));
                await Download(entry.RemotePath, destination, entry.Sha256, token, expectedSize: entry.Size);
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task RestoreTrash(RemoteItem item, CancellationToken token = default)
    {
        var source = new Uri(server, item.Href); var allowed = Url("trashbin", "trash/").AbsoluteUri;
        if (!source.AbsoluteUri.StartsWith(allowed, StringComparison.Ordinal)) throw new ArgumentException("Item não pertence à lixeira desta conta.");
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), source); request.Headers.Add("Destination", Url("trashbin", "restore").AbsoluteUri); request.Headers.Add("Overwrite", "F");
        using var response = await client.SendAsync(request, token); response.EnsureSuccessStatusCode();
    }
    public void Dispose() => client.Dispose();
}
