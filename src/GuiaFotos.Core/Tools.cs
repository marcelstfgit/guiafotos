using System.Diagnostics;
using System.Text;

namespace GuiaFotos.Core;

public static class Tools
{
    public static async Task<byte[]> Run(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 120)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); } catch (Exception error) { throw new IOException($"Ferramenta não disponível: {System.IO.Path.GetFileName(executable)}", error); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try { await Task.WhenAll(read, process.WaitForExitAsync(limit.Token)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var diagnostic = await errors;
        if (process.ExitCode != 0) throw new IOException($"{System.IO.Path.GetFileName(executable)} falhou ({process.ExitCode}): {diagnostic[..Math.Min(1000, diagnostic.Length)]}");
        return output.ToArray();
    }
    public static async Task<string> Text(string executable, IEnumerable<string> arguments, CancellationToken token = default) => Encoding.UTF8.GetString(await Run(executable, arguments, token));
}
