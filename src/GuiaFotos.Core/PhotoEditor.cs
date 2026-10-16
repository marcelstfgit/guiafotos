using System.Text.RegularExpressions;

namespace GuiaFotos.Core;

public sealed class PhotoEditor(ToolPaths tools, MediaLibrary library, FileActions actions)
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    { "FileName", "Directory", "FilePermissions", "FileModifyDate", "FileCreateDate", "FileAccessDate", "HardLink", "SymLink", "TestName", "FileSequence", "SourceFile" };
    private static readonly HashSet<string> SafeTags = new(StringComparer.OrdinalIgnoreCase) { "Title", "Description", "ImageDescription", "Subject", "Keywords", "Rating", "Artist", "Creator", "Copyright", "Rights", "DateTimeOriginal", "CreateDate", "ModifyDate", "OffsetTimeOriginal", "GPSLatitude", "GPSLongitude", "GPSAltitude", "GPSLatitudeRef", "GPSLongitudeRef", "Label", "Make", "Model", "UserComment" };
    public static void ValidateMetadata(IReadOnlyDictionary<string, string> updates, bool advancedMode = false)
    {
        if (updates.Count == 0) throw new ArgumentException("Selecione ao menos um campo para editar.");
        foreach (var (key, value) in updates)
        {
            var name = key.Split(':').Last();
            if (!Regex.IsMatch(key, "^[A-Za-z0-9][A-Za-z0-9:_-]{0,100}$") || Forbidden.Contains(name) || value.Length > 16000 || !advancedMode && !SafeTags.Contains(name)) throw new ArgumentException("Campo inválido, estrutural ou não permitido neste modo: " + key);
            if (name.Equals("Rating", StringComparison.OrdinalIgnoreCase) && (!int.TryParse(value, out var rating) || rating is < 0 or > 5)) throw new ArgumentException("Rating deve ser de 0 a 5.");
            if (name is "GPSLatitude" or "GPSLongitude")
            { if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gps) || !double.IsFinite(gps) || Math.Abs(gps) > (name == "GPSLatitude" ? 90 : 180)) throw new ArgumentException("Coordenada GPS inválida."); }
        }
    }
    private string Output(string source, string directory, string suffix, string? extension = null)
    { Directory.CreateDirectory(directory); return Files.Unique(System.IO.Path.Combine(directory, System.IO.Path.GetFileNameWithoutExtension(source) + suffix + (extension ?? System.IO.Path.GetExtension(source)))); }
    public async Task<string> MetadataCopy(string source, string directory, IReadOnlyDictionary<string, string> updates, CancellationToken token = default, bool advancedMode = false)
    {
        ValidateMetadata(updates, advancedMode);
        var target = Output(source, directory, "_metadata"); var temp = target + ".partial";
        var input = Path.Combine(Path.GetTempPath(), "guiafotos-metadata-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await FileActions.CopyVerified(source, temp, token);
            var values = updates.Where(x => x.Value.Length > 0).ToDictionary(x => x.Key, x => "base64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Value)));
            values["SourceFile"] = temp;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(input, options))
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, new[] { values }, cancellationToken: token);
            var args = new List<string> { "-overwrite_original", "-charset", "filename=UTF8", "-json=" + input };
            args.AddRange(updates.Where(x => x.Value.Length == 0).Select(x => "-" + x.Key + "="));
            args.AddRange(["--", temp]);
            // JSON import writes its update summary to stderr.
            var result = await Tools.Text(tools.ExifTool, args, token, includeDiagnostics: true);
            if (!Regex.IsMatch(result, "[1-9][0-9]* image files? updated")) throw new IOException("Nenhum campo foi atualizado; tag pode ser somente leitura ou não suportada neste formato. " + result.Trim());
            var metadata = await library.Metadata(temp, token);
            File.Move(temp, target); actions.Record("metadata-copy", source, target, await Files.Hash(target, token));
            Files.AtomicText(target + ".metadata.json", Json.Write(metadata)); return target;
        }
        finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<string> StripExif(string source, string directory, bool allPrivate = false, CancellationToken token = default)
    {
        var target = Output(source, directory, "_sem-exif"); var temp = target + ".partial";
        var before = await library.Metadata(source, token);
        try
        {
            // Só normaliza pixels se a orientação realmente exigir. Nunca achata animação, RAW ou TIFF multipágina.
            var orientation = before.FirstOrDefault(x => x.Key.EndsWith(":Orientation")).Value;
            var needsOrientation = orientation.ValueKind == System.Text.Json.JsonValueKind.Number && orientation.GetInt32() is > 1 and <= 8;
            if (needsOrientation && !MediaLibrary.IsVideo(source))
            {
                var extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
                if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) throw new NotSupportedException("Para remover a orientação deste formato sem perder páginas/quadros, exporte primeiro uma cópia raster pelo editor.");
                await Tools.Run(tools.Magick, [source, "-auto-orient", "-quality", "95", extension.TrimStart('.') + ":" + temp], token);
            }
            else await FileActions.CopyVerified(source, temp, token);
            var arguments = allPrivate ? new[] { "-overwrite_original", "-charset", "filename=UTF8", "-all=", "-tagsFromFile", "@", "-ICC_Profile", "--", temp }
                : new[] { "-overwrite_original", "-charset", "filename=UTF8", "-EXIF:all=", "--", temp };
            await Tools.Run(tools.ExifTool, arguments, token);
            var after = await library.Metadata(temp, token);
            if (after.Keys.Any(k => k.StartsWith("ExifIFD:") || k.StartsWith("IFD0:") || k.StartsWith("GPS:")))
                throw new IOException("O formato não permite remover todos os campos EXIF solicitados.");
            if (allPrivate && after.Keys.Any(IsPrivateMetadata)) throw new IOException("O formato ainda contém metadados pessoais. A cópia não foi publicada; use uma exportação raster ou remux de vídeo apropriada.");
            File.Move(temp, target); actions.Record("exif-copy", source, target, await Files.Hash(target, token));
            Files.AtomicText(target + ".audit.json", Json.Write(new { OriginalHash = await Files.Hash(source, token), RemovedFields = before.Keys.Except(after.Keys).ToArray(), RetainedFields = after.Keys.ToArray() })); return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool IsPrivateMetadata(string key)
    {
        var group = key.Split(':')[0]; var field = key.Split(':').Last().ToLowerInvariant();
        if (group.StartsWith("XMP", StringComparison.OrdinalIgnoreCase) || group is "IPTC" or "GPS" or "ExifIFD" or "IFD0" or "MakerNotes" or "Keys" or "ItemList" or "UserData") return true;
        return group == "QuickTime" && new[] { "location", "gps", "author", "artist", "copyright", "description", "title", "comment", "keywords", "make", "model", "software" }.Any(field.Contains);
    }
    public async Task<string> Edit(string source, string directory, string operation, Dictionary<string, string>? options = null, CancellationToken token = default)
    {
        if (MediaLibrary.IsVideo(source)) throw new ArgumentException("Edição de pixels disponível para fotos; vídeos podem ser catalogados, copiados e comparados.");
        var metadata = await library.Metadata(source, token);
        if (System.IO.Path.GetExtension(source).ToLowerInvariant() is ".tif" or ".tiff")
        {
            var countText = await Tools.Text(tools.Magick, ["-ping", source, "-format", "%n\\n", "info:"], token);
            if (int.TryParse(countText.Split('\n')[0], out var count) && count > 1) throw new NotSupportedException("TIFF multipágina: exporte uma página explicitamente para editar; o conjunto não será achatado.");
        }
        if (System.IO.Path.GetExtension(source).Equals(".gif", StringComparison.OrdinalIgnoreCase) || metadata.Any(x => x.Key.EndsWith(":PageCount") && x.Value.ValueKind == System.Text.Json.JsonValueKind.Number && x.Value.TryGetInt32(out var count) && count > 1)) throw new NotSupportedException("GIF/animação ou imagem multipágina: edição de um quadro não substituiria todo o conteúdo; use uma foto raster individual.");
        var args = new List<string> { source + "[0]", "-auto-orient" };
        switch (operation)
        {
            case "enhance": args.AddRange(["-auto-level", "-sigmoidal-contrast", "3x50%", "-modulate", "100,108,100", "-unsharp", "0x0.6+0.5+0.02"]); break;
            case "restore": args.AddRange(["-despeckle", "-noise", "1", "-contrast-stretch", "0.5%x0.5%", "-unsharp", "0x0.5+0.5+0.02"]); break;
            case "rotate": var degree = options?.GetValueOrDefault("degrees") ?? "90"; if (degree is not ("90" or "180" or "270")) throw new ArgumentException("Rotação inválida."); args.AddRange(["-rotate", degree]); break;
            case "crop": var geometry = options?.GetValueOrDefault("geometry") ?? ""; if (!Regex.IsMatch(geometry, "^[1-9][0-9]{0,5}x[1-9][0-9]{0,5}\\+[0-9]{1,6}\\+[0-9]{1,6}$")) throw new ArgumentException("Recorte esperado: largura x altura + x + y, por exemplo 800x600+0+0."); args.AddRange(["-crop", geometry, "+repage"]); break;
            case "adjust":
                double Value(string key, double fallback, double minimum, double maximum)
                {
                    if (options?.GetValueOrDefault(key) is not { } text) return fallback;
                    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < minimum || number > maximum) throw new ArgumentException("Ajuste fora da faixa: " + key);
                    return number;
                }
                var brightness = Value("brightness", 0, -100, 100); var contrast = Value("contrast", 0, -100, 100); var saturation = Value("saturation", 100, 0, 200);
                args.AddRange(["-brightness-contrast", brightness.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + contrast.ToString(System.Globalization.CultureInfo.InvariantCulture), "-modulate", "100," + saturation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",100"]); break;
            default: throw new ArgumentException("Operação de imagem não suportada.");
        }
        var target = Output(source, directory, "_" + operation, ".jpg"); var temp = target + ".partial";
        try
        {
            args.AddRange(["-quality", "95", "jpg:" + temp]); await Tools.Run(tools.Magick, args, token);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("A imagem não foi gerada.");
            File.Move(temp, target); actions.Record("edit-copy", source, target, await Files.Hash(target, token));
            Files.AtomicText(target + ".recipe.json", Json.Write(new { Original = source, OriginalHash = await Files.Hash(source, token), Operation = operation, Options = options, Created = DateTime.UtcNow, Tool = "ImageMagick" }));
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
