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
            // WPF's minimum layout size takes precedence over a headless CI
            // desktop's tracking limit. This fixes the render viewport without
            // changing the user's normal resizable window or monitor settings.
            MinWidth = width; MinHeight = height; Width = width; Height = height; Left = Top = 0; await LayoutReady(this);
            var root = (FrameworkElement)Content;
            Check(Math.Abs(root.ActualWidth - width) < 1 && Math.Abs(root.ActualHeight - height) < 1, $"capture viewport {name}: {root.ActualWidth}x{root.ActualHeight} DIPs, requested {width}x{height}");
            var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var imagePath = Path.Combine(output, name + ".png");
            using (var file = File.Create(imagePath)) encoder.Save(file);
            screenshots.Add(new { file = name + ".png", sha256 = await Files.Hash(imagePath), viewport_dips = new[] { root.ActualWidth, root.ActualHeight }, render_dpi = 96 * scale, pixels = new[] { bitmap.PixelWidth, bitmap.PixelHeight } });
        }
        try
        {
            await LayoutReady(this); await Idle(); Refresh();
            await Capture("biblioteca-vazia-1366");
            Check(!EditAction.IsEnabled && !ExportAction.IsEnabled, "empty library disables actions that require a photo");
            File.Copy(manifest, Path.Combine(output, "ui-sample-photos.json"), true);
            await File.WriteAllTextAsync(Path.Combine(output, "LEIA-ME.txt"), "Capturas reais do aplicativo WPF no Windows com fotografias de demonstração Unsplash. Fontes e checksums: ui-sample-photos.json. Licença: https://unsplash.com/license\nA imagem 1920-150 é uma renderização de 1280x720 DIPs em 144 dpi; não é um teste do DPI do monitor. O fluxo automatizado não comprova precisão facial nem todos os gestos de mouse/teclado.\nReal WPF screenshots on Windows using Unsplash demo photographs. Sources and checksums: ui-sample-photos.json. The 1920-150 image is a 144 dpi rendering, not a monitor DPI test.\n");
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
            Check(Gallery.RealizedRows is > 0 and < 20 && Gallery.IsPhotoVisible(Gallery.Items[0].Path), "photo row virtualization displays actual thumbnails");
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
            Check(UndoButton.IsVisible && RedoButton.IsVisible && UndoButton.TransformToAncestor(this).Transform(new Point()).Y < ActualHeight - 120, "undo and redo stay visible without scrolling the tools");
            var originalWidth = Preview.OriginalWidth; var originalHeight = Preview.OriginalHeight;
            Crop(this, new RoutedEventArgs()); await LayoutReady(this); Preview.SetNormalizedCrop(new Rect(.12, .14, .64, .62));
            var geometry = Preview.CropGeometry;
            var expectedCropWidth = (int)Math.Round(originalWidth * .64); var expectedCropHeight = (int)Math.Round(originalHeight * .62);
            Check(geometry == $"{expectedCropWidth}x{expectedCropHeight}+{(int)Math.Round(originalWidth * .12)}+{(int)Math.Round(originalHeight * .14)}" && ApplyCropButton.IsEnabled, "visual crop maps normalized bounds to exact original pixels");
            await Capture("recorte-1366");
            ApplyCrop(this, new RoutedEventArgs()); await Idle();
            Check(previewFile != null && File.Exists(previewFile) && Preview.OriginalWidth == expectedCropWidth && Preview.OriginalHeight == expectedCropHeight, "apply visual crop creates a derivative with the selected pixel dimensions");
            var cropped = previewFile; UndoPreview(this, new RoutedEventArgs()); await Idle(); Check(previewFile == null && Preview.OriginalWidth == originalWidth, "undo restores original preview without changing original bytes");
            RedoPreview(this, new RoutedEventArgs()); await Idle(); Check(previewFile == cropped, "redo restores the cropped derivative");
            AlbumBox.Text = "Organizadas"; SaveOrganize(this, new RoutedEventArgs());
            Check(!Gallery.Items.Any(p => p.Path == editing.Path) && CurrentPhoto?.Path == editing.Path, "editor keeps photo identity after album change removes it from the library filter");
            await PreviewEdit("rotate", new() { ["degrees"] = "90" }); Check(previewSource?.Path == editing.Path && previewFile != cropped, "editing continues after the photo moves out of the active album");
            var expectedRating = CurrentPhoto!.Rating > 0 ? 0 : 5;
            TagsBox.Text = "natureza, organizada"; ToggleFavorite(this, new RoutedEventArgs()); SaveOrganize(this, new RoutedEventArgs());
            Check(catalog.All().Single(p => p.Path == editing.Path) is var organized && organized.Rating == expectedRating && organized.Tags == "natureza, organizada", "mark favorite then save tags without losing either change");
            var exported = Path.Combine(workspace, "review-export.jpg"); Check(await ExportPreviewTo(exported) && File.Exists(exported), "export edited copy through the application export flow");
            BackToLibrary(this, new RoutedEventArgs()); Check(EditorScreen.Visibility == Visibility.Collapsed && activeAlbum == "Favoritas da viagem", "return from editor preserves library context");
            ShowAll(this, new RoutedEventArgs()); await LayoutReady(this);
            var many = Enumerable.Range(0, 12000).Select(n => first with { Path = Path.Combine(photosRoot, "Grande coleção", $"Foto {n:00000}.jpg") }).ToArray();
            Gallery.ItemsSource = many; await LayoutReady(this);
            Check(Gallery.RealizedRows is > 0 and < 20, "large collection creates visible photo rows without realizing the whole collection");
            Gallery.ScrollTo(many[^1].Path); await LayoutReady(this);
            Check(Gallery.RealizedRows is > 0 and < 20 && Gallery.IsPhotoVisible(many[^1].Path), "scroll to the final thumbnail in a 12000-photo collection");
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
