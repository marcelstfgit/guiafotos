using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GuiaFotos.Setup;

public static class Program
{
    public const string Version = "0.2.1";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GuiaFotos";
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GuiaFotos");
    private static string MenuRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "GuiaFotos");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GuiaFotos.lnk");

    [STAThread]
    public static int Main(string[] args)
    {
        if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version < new Version(10, 0))
        { MessageBox.Show("O GuiaFotos requer Windows 10/11 de 64 bits."); return 1; }
        if (args.Contains("--remove") || args.Contains("--remove-silent"))
        {
            var parent = args.FirstOrDefault(x => x.StartsWith("--parent=", StringComparison.Ordinal));
            if (parent != null && int.TryParse(parent[9..], out var id))
            {
                try { using var process = Process.GetProcessById(id); if (!process.WaitForExit(30000)) return 1; }
                catch (ArgumentException) { /* launcher already exited */ }
            }
        }
        if (args.Contains("--install-silent") || args.Contains("--remove-silent"))
        {
            try
            {
                if (args.Contains("--install-silent")) Install(false, _ => { }).GetAwaiter().GetResult();
                else Remove(_ => { });
                return 0;
            }
            catch { return 1; }
        }
        var app = new Application();
        if (args.Contains("--uninstall") || args.Contains("--uninstall-silent"))
        {
            try
            {
                // Run outside the installation to allow deleting the installed uninstaller.
                var temp = Path.Combine(Path.GetTempPath(), "GuiaFotos-uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp); var worker = Path.Combine(temp, "GuiaFotos.Remove.exe");
                File.Copy(Environment.ProcessPath!, worker);
                var start = new ProcessStartInfo(worker) { UseShellExecute = false };
                start.ArgumentList.Add(args.Contains("--uninstall-silent") ? "--remove-silent" : "--remove");
                start.ArgumentList.Add("--parent=" + Environment.ProcessId);
                Process.Start(start); return 0;
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Não foi possível iniciar a desinstalação"); return 1; }
        }
        return app.Run(new SetupWindow(args.Contains("--remove")));
    }

    public static void EnsureAppClosed()
    {
        foreach (var process in Process.GetProcessesByName("GuiaFotos"))
        {
            using (process)
                if (!process.HasExited) throw new IOException("Feche o GuiaFotos antes de instalar ou desinstalar.");
        }
    }

    public static async Task Install(bool desktop, Action<string> progress)
    {
        EnsureAppClosed();
        var root = InstallRoot;
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Já existe uma pasta de instalação. Desinstale a versão anterior em Configurações > Aplicativos e tente novamente. Seu catálogo será preservado.");
        var staging = root + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var stubSize = await Package.Extract(Environment.ProcessPath!, staging, progress);
            if (File.Exists(Path.Combine(staging, "portable.marker"))) throw new InvalidDataException("Pacote de instalação contém marcador portátil.");
            await Package.WriteStub(Environment.ProcessPath!, Path.Combine(staging, "Desinstalar.exe"), stubSize);
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(staging, x).Replace('\\', '/')).Order().ToArray();
            File.WriteAllText(Path.Combine(staging, Removal.Inventory), JsonSerializer.Serialize(new InstalledFiles("GuiaFotos", Version, files)));
            Directory.Move(staging, root);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }

        // Register removal before creating optional shortcuts, so a shortcut failure is recoverable.
        using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName", "GuiaFotos"); key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "GuiaFotos"); key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", Path.Combine(root, "GuiaFotos.exe"));
            key.SetValue("UninstallString", "\"" + Path.Combine(root, "Desinstalar.exe") + "\" --uninstall");
            key.SetValue("QuietUninstallString", "\"" + Path.Combine(root, "Desinstalar.exe") + "\" --uninstall-silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            var kib = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) / 1024;
            key.SetValue("EstimatedSize", (int)Math.Min(kib, int.MaxValue), RegistryValueKind.DWord);
        }
        Directory.CreateDirectory(MenuRoot);
        Shortcut(Path.Combine(MenuRoot, "GuiaFotos.lnk"), Path.Combine(root, "GuiaFotos.exe"));
        Shortcut(Path.Combine(MenuRoot, "Desinstalar GuiaFotos.lnk"), Path.Combine(root, "Desinstalar.exe"), "--uninstall");
        if (desktop) Shortcut(DesktopLink, Path.Combine(root, "GuiaFotos.exe"));
    }

    private static void Shortcut(string path, string target, string arguments = "")
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Não foi possível criar os atalhos do Windows.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        try { shortcut.TargetPath = target; shortcut.Arguments = arguments; shortcut.WorkingDirectory = InstallRoot; shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }

    public static void Remove(Action<string> progress)
    {
        EnsureAppClosed(); var root = InstallRoot;
        var inventoryPath = Path.Combine(root, Removal.Inventory);
        var inventory = JsonSerializer.Deserialize<InstalledFiles>(File.ReadAllText(inventoryPath)) ?? throw new InvalidDataException("Inventário ausente.");
        var files = Removal.Plan(root, inventory);
        // Only explicit installed files are removed. Unknown files and user data survive.
        foreach (var path in files) { progress(Path.GetFileName(path)); File.Delete(path); }
        var directories = files.SelectMany(x => Parents(Path.GetDirectoryName(x)!, root)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length);
        foreach (var directory in directories)
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        File.Delete(inventoryPath);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        File.Delete(Path.Combine(MenuRoot, "GuiaFotos.lnk")); File.Delete(Path.Combine(MenuRoot, "Desinstalar GuiaFotos.lnk")); File.Delete(DesktopLink);
        if (Directory.Exists(MenuRoot) && !Directory.EnumerateFileSystemEntries(MenuRoot).Any()) Directory.Delete(MenuRoot);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
    }

    private static IEnumerable<string> Parents(string path, string root)
    {
        while (path.Length > root.Length) { yield return path; path = Path.GetDirectoryName(path)!; }
    }
}

public sealed class SetupWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 12) };
    private readonly Button action = new() { MinWidth = 150, Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox desktop = new() { Content = "Criar atalho na área de trabalho", IsChecked = true, Margin = new Thickness(0, 12, 0, 12) };
    private bool running;
    public SetupWindow(bool remove)
    {
        Title = remove ? "Desinstalar GuiaFotos" : "Instalar GuiaFotos " + Program.Version;
        Width = 640; Height = 460; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(28) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = remove
            ? "O aplicativo será removido. Fotos, catálogo, configurações e backups serão preservados."
            : "Biblioteca de fotos e vídeos para Windows. Instalação para este usuário, com runtime e ferramentas incluídos.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Destino: " + Program.InstallRoot, TextWrapping = TextWrapping.Wrap });
        if (!remove)
        {
            panel.Children.Add(desktop);
            panel.Children.Add(new TextBlock { Text = "Pré-validação: interface e ferramentas ainda precisam de teste em Windows real. Rostos: modelos locais baixados na primeira ativação; requer Visual C++ x64.", TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(status); panel.Children.Add(action);
        action.Content = remove ? "Desinstalar" : "Instalar";
        Closing += (_, e) => { if (running) e.Cancel = true; };
        action.Click += async (_, _) =>
        {
            if (action.Content as string == "Fechar") { Close(); return; }
            var useDesktop = desktop.IsChecked == true;
            running = true; action.IsEnabled = false; desktop.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(name => status.Text = "Processando: " + name);
                if (remove) await Task.Run(() => Program.Remove(name => ((IProgress<string>)progress).Report(name)));
                else await Task.Run(() => Program.Install(useDesktop, name => ((IProgress<string>)progress).Report(name)));
                status.Text = remove ? "Aplicativo removido. Seus dados foram preservados." : "Instalação concluída. Abra o GuiaFotos pelo menu Iniciar.";
                action.Content = "Fechar";
            }
            catch (Exception error) { status.Text = "Operação não concluída: " + error.Message; }
            finally { running = false; action.IsEnabled = true; desktop.IsEnabled = true; }
        };
    }
}
