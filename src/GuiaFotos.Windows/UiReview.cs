using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GuiaFotos.Core;

namespace GuiaFotos.Windows;

public partial class MainWindow
{
    private bool reviewMode;
    private Exception? lastReviewError;
    private readonly List<string> reviewNotes = [];
    private static async Task LayoutReady(FrameworkElement root)
    { root.UpdateLayout(); await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(120); root.UpdateLayout(); }

    internal async Task ReviewInterface(string output, string manifest)
    {
        reviewMode = true; output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new List<string>(); var screenshots = new List<object>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("UI review failed: " + name); checks.Add(name); }
        async Task Capture(string name, double width = 1366, double height = 768, double scale = 1)
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Normal;
            Width = width; Height = height; Left = Top = 0; await LayoutReady(this);
            var root = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
            screenshots.Add(new { file = name + ".png", viewport_dips = new[] { root.ActualWidth, root.ActualHeight }, render_dpi = 96 * scale, pixels = new[] { bitmap.PixelWidth, bitmap.PixelHeight } });
        }
        try
        {
            await LayoutReady(this); await Idle();
            await Capture("biblioteca-vazia-1366");
            var samples = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            var sourceFolder = Path.Combine(workspace, "review-downloads"); Directory.CreateDirectory(sourceFolder);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var originals = new List<string>(); var i = 0;
            foreach (var sample in samples.RootElement.GetProperty("photos").EnumerateArray())
            {
                var bytes = await http.GetByteArrayAsync(sample.GetProperty("url").GetString());
                Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(sample.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "sample photo " + i + " checksum");
                var target = Path.Combine(sourceFolder, "sample-" + i++ + ".jpg"); await File.WriteAllBytesAsync(target, bytes); originals.Add(target);
            }
            var photosRoot = Path.Combine(workspace, "review-photos");
            var folders = new[] { "Passeios", "Viagem", "Viagem extra" };
            for (var folderIndex = 0; folderIndex < folders.Length; folderIndex++)
            {
                var folder = Path.Combine(photosRoot, folders[folderIndex]); Directory.CreateDirectory(folder);
                for (i = 0; i < originals.Count; i++) File.Copy(originals[(i + folderIndex * 2) % originals.Count], Path.Combine(folder, $"Foto {i + 1:00}.jpg"));
            }
            await Scan(photosRoot, CancellationToken.None);
            var all = catalog.All();
            foreach (var photo in all)
            {
                var group = Array.IndexOf(folders, Path.GetFileName(Path.GetDirectoryName(photo.Path))); var number = int.Parse(Path.GetFileNameWithoutExtension(photo.Path).Split(' ').Last());
                catalog.Add(photo with { Taken = new DateTime(2026 - group, 3 + group, 18).AddMinutes(-number * 4), Album = number <= 3 ? "Favoritas da viagem" : "", Rating = number % 3 == 0 ? 5 : 0, Tags = number % 2 == 0 ? "natureza, paisagem" : "viagem" });
            }
            var hashes = new Dictionary<string, string>(); foreach (var p in catalog.All()) hashes[p.Path] = await Files.Hash(p.Path);
            libraryDirty = true; Refresh(); await LayoutReady(this);
            Check(Gallery.Items.Count == 24, "import a folder into the real catalog and show 24 photos");
            Check(Gallery.ActualWidth > ActualWidth * .70, "photos occupy the main library area");
            Check(Gallery.RealizedRows < 20, "photo row virtualization is active");
            var first = Gallery.Items[0]; var second = Gallery.Items[1]; var fourth = Gallery.Items[3];
            Gallery.Select(first); Gallery.Select(second, toggle: true); Check(Gallery.SelectedItems.Count == 2, "toggle selection keeps both photos");
            Gallery.Select(fourth, extend: true); Check(Gallery.SelectedItems.Count == 3, "range selection follows visible photo order");
            var selection = Gallery.SelectedItems.Select(p => p.Path).ToHashSet(); ThumbnailZoom.Value = 212; await LayoutReady(this);
            Check(selection.SetEquals(Gallery.SelectedItems.Select(p => p.Path)), "zoom preserves photo selection");
            ThumbnailZoom.Value = 164; await Capture("biblioteca-1366");
            await Capture("biblioteca-1920-150", 1280, 720, 1.5);
            var trip = Path.Combine(photosRoot, "Viagem"); Folders.SelectedItem = Folders.Items.Cast<LibraryLocation>().Single(p => p.Path == trip); await LayoutReady(this);
            Check(Gallery.Items.Count == 8 && Gallery.Items.All(p => Path.GetDirectoryName(p.Path) == trip), "folder navigation is exact and excludes similarly named siblings");
            Gallery.Select(Gallery.Items[0], extend: true); Check(Gallery.SelectedItems.Count == 1, "range selection recovers when filtering removed its anchor");
            Search.Text = "Foto 01"; searchDelay.Stop(); Refresh(); Check(Gallery.Items.Count == 1 && activeFolder == trip, "search preserves folder context");
            Search.Clear(); searchDelay.Stop(); Refresh();
            Albums.SelectedItem = Albums.Items.Cast<LibraryLocation>().Single(p => p.Name == "Favoritas da viagem"); await LayoutReady(this);
            Check(Gallery.Items.Count == 9 && Gallery.Items.All(p => p.Album == "Favoritas da viagem"), "album navigation filters the actual catalog");
            var editing = Gallery.Items[0]; Check(await OpenEditor(editing), "open the selected original in the integrated editor"); await Capture("editor-1366");
            Check(Preview.ActualWidth > 900 && Preview.ActualHeight > 450, "editor has a large photo canvas");
            var originalWidth = Preview.OriginalWidth; var originalHeight = Preview.OriginalHeight;
            Crop(this, new RoutedEventArgs()); Preview.SetNormalizedCrop(new Rect(.12, .14, .64, .62));
            var geometry = Preview.CropGeometry;
            Check(geometry != null && ApplyCropButton.IsEnabled, "visual crop maps the displayed image to original pixels");
            await Capture("recorte-1366");
            ApplyCrop(this, new RoutedEventArgs()); await Idle();
            Check(previewFile != null && File.Exists(previewFile) && Preview.OriginalWidth < originalWidth && Preview.OriginalHeight < originalHeight, "apply visual crop creates a real smaller derivative");
            var cropped = previewFile; UndoPreview(this, new RoutedEventArgs()); await Idle(); Check(previewFile == null && Preview.OriginalWidth == originalWidth, "undo restores original preview without changing original bytes");
            RedoPreview(this, new RoutedEventArgs()); await Idle(); Check(previewFile == cropped, "redo restores the cropped derivative");
            AlbumBox.Text = "Organizadas"; SaveOrganize(this, new RoutedEventArgs());
            Check(!Gallery.Items.Any(p => p.Path == editing.Path) && CurrentPhoto?.Path == editing.Path, "editor keeps photo identity after album change removes it from the library filter");
            await PreviewEdit("rotate", new() { ["degrees"] = "90" }); Check(previewSource?.Path == editing.Path && previewFile != cropped, "editing continues after the photo moves out of the active album");
            var exported = Path.Combine(workspace, "review-export.jpg"); Check(await ExportPreviewTo(exported) && File.Exists(exported), "export edited copy through the application export flow");
            BackToLibrary(this, new RoutedEventArgs()); Check(EditorScreen.Visibility == Visibility.Collapsed && activeAlbum == "Favoritas da viagem", "return from editor preserves library context");
            ShowAll(this, new RoutedEventArgs()); await LayoutReady(this);
            var many = Enumerable.Range(0, 12000).Select(n => first with { Path = Path.Combine(photosRoot, "Grande coleção", $"Foto {n:00000}.jpg") }).ToArray();
            Gallery.ItemsSource = many; await LayoutReady(this);
            Check(Gallery.RealizedRows < 20, "large collection creates only visible photo rows");
            libraryDirty = true; Refresh();
            foreach (var pair in hashes) Check(await Files.Hash(pair.Key) == pair.Value, "original bytes preserved: " + Path.GetFileName(Path.GetDirectoryName(pair.Key)) + "/" + Path.GetFileName(pair.Key));
            Check(lastReviewError == null, "no application operation failed during the UI flow");
            await File.WriteAllTextAsync(Path.Combine(output, "validacao-interface.json"), System.Text.Json.JsonSerializer.Serialize(new { commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), scope = "real WPF rendering and automated desktop flow; screenshots still require human visual review", passed = checks.Count, failed = 0, checks, screenshots, sample_license = "https://unsplash.com/license" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "erro-interface.txt"), error.ToString());
            try { await Capture("falha-interface"); } catch { }
            throw;
        }
        async Task Idle()
        {
            var limit = DateTime.UtcNow.AddSeconds(90);
            do { await Task.Delay(30); } while ((task != null || peopleView.Busy || versionsView.Busy) && DateTime.UtcNow < limit);
            if (task != null || peopleView.Busy || versionsView.Busy) throw new TimeoutException("UI operation did not finish");
            await LayoutReady(this);
        }
    }
}
