using System.Windows;
using GuiaFotos.Core;
namespace GuiaFotos.Windows;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        var review = e.Args.Length == 3 && e.Args[0] == "--ui-review";
        if (!e.Args.Contains("--smoke-test") && !review) { base.OnStartup(e); new MainWindow().Show(); return; }
        MainWindow? window = null;
        try
        {
            if (review) Environment.SetEnvironmentVariable("GUIAFOTOS_UI_REVIEW_ROOT", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guiafotos-ui-review-" + Guid.NewGuid().ToString("N")));
            base.OnStartup(e);
            window = new MainWindow(true); window.Show();
            if (review) await window.ReviewInterface(e.Args[1], e.Args[2]);
            var paths = ToolPaths.Discover(AppContext.BaseDirectory);
            await Tools.Text(paths.ExifTool, ["-ver"]); await Tools.Text(paths.Ffmpeg, ["-version"]); await Tools.Text(paths.Ffprobe, ["-version"]); await Tools.Text(paths.Magick, ["-version"]);
            await Task.Delay(1000); window.Close(); Shutdown(0);
        }
        catch (Exception error)
        {
            var log = Environment.GetEnvironmentVariable("GUIAFOTOS_SMOKE_LOG");
            if (!string.IsNullOrEmpty(log)) System.IO.File.WriteAllText(log, error.ToString());
            window?.Close(); Shutdown(1);
        }
    }
}
