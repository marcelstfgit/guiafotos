using System.Diagnostics;
using System.Text;

namespace GuiaFotos.Core;

public static class Tools
{
    public static async Task<byte[]> Run(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 120, bool includeDiagnostics = false)
    {
        // ExifTool recommends a UTF-8 argfile to avoid Windows code-page conversion.
        // MetadataCopy uses JSON for values, including whitespace and multiple lines.
        var exifTool = Path.GetFileNameWithoutExtension(executable).Equals("exiftool", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = exifTool, CreateNoWindow = true };
        byte[]? input = null;
        if (exifTool)
        {
            foreach (var argument in new[] { "-charset", "filename=UTF8", "-charset", "UTF8", "-@", "-" }) start.ArgumentList.Add(argument);
            var lines = arguments.ToArray();
            if (lines.Any(argument => argument.Length == 0 || argument[0] == '#' || char.IsWhiteSpace(argument[0]) || argument.IndexOfAny(['\r', '\n', '\0']) >= 0))
                throw new ArgumentException("Argumento ExifTool inválido; use importação JSON para valores de metadados.");
            input = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        }
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); } catch (Exception error) { throw new IOException($"Ferramenta não disponível: {System.IO.Path.GetFileName(executable)}", error); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        async Task SendArguments()
        {
            if (input == null) return;
            await process.StandardInput.BaseStream.WriteAsync(input, limit.Token);
            process.StandardInput.Close();
        }
        try { await Task.WhenAll(read, SendArguments(), process.WaitForExitAsync(limit.Token)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var diagnostic = await errors;
        if (process.ExitCode != 0) throw new IOException($"{System.IO.Path.GetFileName(executable)} falhou ({process.ExitCode}): {diagnostic[..Math.Min(1000, diagnostic.Length)]}");
        if (includeDiagnostics) output.Write(Encoding.UTF8.GetBytes(diagnostic));
        return output.ToArray();
    }
    public static async Task<string> Text(string executable, IEnumerable<string> arguments, CancellationToken token = default, bool includeDiagnostics = false) => Encoding.UTF8.GetString(await Run(executable, arguments, token, includeDiagnostics: includeDiagnostics));
}
