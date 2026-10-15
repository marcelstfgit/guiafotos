using System.Windows;
using GuiaFotos.Core;
namespace GuiaFotos.Windows;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!e.Args.Contains("--smoke-test")) { base.OnStartup(e); return; }
        StartupUri = null; base.OnStartup(e);
        var window = new MainWindow(true); window.Show();
        try
        {
            var paths = ToolPaths.Discover(AppContext.BaseDirectory);
            await Tools.Text(paths.ExifTool, ["-ver"]); await Tools.Text(paths.Ffmpeg, ["-version"]); await Tools.Text(paths.Ffprobe, ["-version"]); await Tools.Text(paths.Magick, ["-version"]);
            await Task.Delay(1000); window.Close(); Shutdown(0);
        }
        catch { window.Close(); Shutdown(1); }
    }
}
