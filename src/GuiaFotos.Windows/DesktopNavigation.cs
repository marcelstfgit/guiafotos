using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GuiaFotos.Core;
using Microsoft.Win32;

namespace GuiaFotos.Windows;

public partial class MainWindow
{
    private string? activeFolder, activeAlbum;
    private bool navigationUpdating, filmstripUpdating;
    private IReadOnlyList<MediaItem>? cachedLibrary;
    private bool libraryDirty = true;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Stack<string?> undoEdits = new(), redoEdits = new();
    private readonly Dictionary<string, EditSession> editSessions = new(StringComparer.OrdinalIgnoreCase);
    private MediaItem? editorItem;
    private CancellationTokenSource? editorLoad;
    private long imageGeneration;
    private sealed record EditSession(string? Preview, string?[] Undo, string?[] Redo);
    public sealed record LibraryLocation(string Path, string Name, int Count);
    private IEnumerable<MediaItem> FilterLibrary(IEnumerable<MediaItem> source)
    {
        if (activeFolder != null) source = source.Where(p => StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(p.Path), activeFolder));
        if (activeAlbum != null) source = source.Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.Album, activeAlbum));
        return filter switch { "favorites" => source.Where(p => p.Rating > 0), "photos" => source.Where(p => p.Kind == "Foto"), "videos" => source.Where(p => p.Kind == "Vídeo"), _ => source };
    }
    private void UpdateLibraryNavigation()
    {
        var all = cachedLibrary ?? catalog.All(); navigationUpdating = true;
        Folders.ItemsSource = all.GroupBy(p => Path.GetDirectoryName(p.Path) ?? "").OrderByDescending(g => g.Max(p => p.Taken)).Select(g => new LibraryLocation(g.Key, Path.GetFileName(g.Key.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : g.Key, g.Count())).ToList();
        Albums.ItemsSource = all.Where(p => !string.IsNullOrWhiteSpace(p.Album)).GroupBy(p => p.Album, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).Select(g => new LibraryLocation(g.Key, g.Key, g.Count())).ToList();
        Folders.SelectedItem = Folders.Items.Cast<LibraryLocation>().FirstOrDefault(p => p.Path == activeFolder);
        Albums.SelectedItem = Albums.Items.Cast<LibraryLocation>().FirstOrDefault(p => p.Path == activeAlbum);
        navigationUpdating = false;
    }
    private void UpdateLibraryHeading(IReadOnlyList<MediaItem> items)
    {
        LibraryTitle.Text = activeAlbum ?? (activeFolder != null ? Path.GetFileName(activeFolder) : "Todas as pastas");
        if (filter == "favorites") LibraryTitle.Text += " · Favoritas";
        if (filter == "photos") LibraryTitle.Text += " · Fotos";
        if (filter == "videos") LibraryTitle.Text += " · Vídeos";
        LibrarySubtitle.Text = activeFolder ?? (activeAlbum != null ? $"Álbum · {items.Count} itens" : $"{items.Count} itens em {(cachedLibrary ?? []).Select(p => Path.GetDirectoryName(p.Path)).Distinct().Count()} pastas");
        EmptyLibrary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = cachedLibrary?.Count > 0 ? "Nenhuma foto encontrada" : "Sua biblioteca de fotos";
        EmptyHint.Text = cachedLibrary?.Count > 0 ? "Tente outra pesquisa ou limpe os filtros." : "Adicione uma pasta para começar a reencontrar suas fotos.";
        var selected = new SolidColorBrush(Color.FromRgb(208, 226, 239));
        FavoriteFilter.Background = filter == "favorites" ? selected : SystemColors.ControlBrush;
        PhotoFilter.Background = filter == "photos" ? selected : SystemColors.ControlBrush;
        VideoFilter.Background = filter == "videos" ? selected : SystemColors.ControlBrush;
    }
    private void UpdateSelectionBar()
    {
        if (SelectionCount == null) return;
        SelectionCount.Text = Gallery.SelectedItems.Count switch { 0 => "Nenhuma foto", 1 => "1 selecionada", var n => $"{n} selecionadas" };
        try { SelectionPreview.Source = Gallery.SelectedItem is { } item ? LoadImage(item.Thumbnail) : null; } catch { SelectionPreview.Source = null; }
    }
    private void ShowPage(int index)
    {
        if (EditorScreen.Visibility == Visibility.Visible) BackToLibrary(this, new RoutedEventArgs());
        Tabs.SelectedIndex = index;
        SelectionBar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || SelectionBar == null || EditorScreen == null) return;
        if (EditorScreen.Visibility == Visibility.Visible) return;
        SelectionBar.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbar.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowPeople(object sender, RoutedEventArgs e) => ShowPage(6);
    private void ShowVersions(object sender, RoutedEventArgs e) => ShowPage(7);
    private void ShowDuplicates(object sender, RoutedEventArgs e) => ShowPage(1);
    private void ShowCloud(object sender, RoutedEventArgs e) => ShowPage(2);
    private void ShowHistory(object sender, RoutedEventArgs e) { ShowPage(3); RefreshHistory(sender, e); }
    private void ShowAssistant(object sender, RoutedEventArgs e) => ShowPage(4);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(5);
    private void ClearSearch(object sender, RoutedEventArgs e) => Search.Clear();
    private void ClearFilters(object sender, RoutedEventArgs e) { filter = "all"; Refresh(); }
    private void ClearSelection(object sender, RoutedEventArgs e) => Gallery.SelectedItems.Clear();
    private void AlbumSelected(object sender, SelectionChangedEventArgs e)
    { if (!navigationUpdating && Albums.SelectedItem is LibraryLocation album) { activeAlbum = album.Path; activeFolder = null; navigationUpdating = true; Folders.SelectedItem = null; navigationUpdating = false; ShowPage(0); Refresh(); } }
    private void CreateAlbum(object sender, RoutedEventArgs e)
    {
        if (LibraryBusy) { Status.Text = "Aguarde a tarefa atual antes de organizar as fotos."; return; }
        var selection = Selection(); if (selection.Count == 0) { Status.Text = "Selecione as fotos que deseja reunir em um álbum."; return; }
        var name = InputDialog.Ask(this, "Novo álbum", "Nome do álbum"); if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var item in selection) { SaveCatalogItem(item with { Album = name.Trim() }); if (editorItem?.Path == item.Path && EditorScreen.Visibility == Visibility.Visible) AlbumBox.Text = name.Trim(); }
        activeAlbum = name.Trim(); activeFolder = null; libraryDirty = true; Refresh();
    }
    private void ThumbnailZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Gallery != null) Gallery.ThumbnailSize = e.NewValue; }
    private void LargerThumbnails(object sender, RoutedEventArgs e) => ThumbnailZoom.Value += 20;
    private void SmallerThumbnails(object sender, RoutedEventArgs e) => ThumbnailZoom.Value -= 20;
    private void ToggleFileNames(object sender, RoutedEventArgs e) { Gallery.ShowNames = ShowFileNames.IsChecked; Gallery.RefreshNames(); }
    private void ToggleFavorite(object sender, RoutedEventArgs e)
    { if (LibraryBusy) { Status.Text = "Aguarde a tarefa atual antes de organizar as fotos."; return; } foreach (var item in Selection()) { var updated = item with { Rating = item.Rating > 0 ? 0 : 5 }; SaveCatalogItem(updated); if (editorItem?.Path == item.Path && EditorScreen.Visibility == Visibility.Visible) Rating.SelectedIndex = updated.Rating; } libraryDirty = true; Refresh(); }
    private void SaveCatalogItem(MediaItem item)
    { catalog.Add(item); if (editorItem?.Path == item.Path) editorItem = item; if (previewSource?.Path == item.Path) previewSource = item; }
    private async void EditSelected(object sender, RoutedEventArgs e) { if (EditorScreen.Visibility == Visibility.Visible) { Preview.Focus(); return; } if (CurrentPhoto is { } item) await OpenEditor(item); else Status.Text = "Selecione uma foto ou dê duplo clique em uma miniatura."; }
    private void SaveEditSession()
    { if (editorItem != null) editSessions[editorItem.Path] = new EditSession(previewFile, undoEdits.ToArray(), redoEdits.ToArray()); }
    private async Task<bool> OpenEditor(MediaItem item)
    {
        if (LibraryBusy) { Status.Text = "Aguarde a tarefa atual antes de trocar a foto."; return false; }
        if (item.Kind == "Vídeo") { Gallery.Select(item); OpenOriginal(this, new RoutedEventArgs()); return false; }
        searchDelay.Stop(); SaveEditSession(); editorLoad?.Cancel(); editorLoad?.Dispose(); editorLoad = new(); var token = editorLoad.Token;
        editorItem = item; Gallery.Select(item); undoEdits.Clear(); redoEdits.Clear();
        previewFile = null; previewSource = item;
        if (editSessions.TryGetValue(item.Path, out var session))
        {
            previewFile = session.Preview != null && File.Exists(session.Preview) ? session.Preview : null;
            foreach (var file in session.Undo.Reverse()) undoEdits.Push(file);
            foreach (var file in session.Redo.Reverse()) redoEdits.Push(file);
        }
        SelectedName.Text = item.Name; Rating.SelectedIndex = item.Rating; TagsBox.Text = item.Tags; AlbumBox.Text = item.Album;
        LibrarySidebar.Visibility = Tabs.Visibility = SelectionBar.Visibility = LibraryToolbar.Visibility = Visibility.Collapsed;
        EditorScreen.Visibility = Visibility.Visible; CropActions.Visibility = Visibility.Collapsed; Preview.CropMode = false;
        Preview.OriginalWidth = Preview.OriginalHeight = 0;
        try { Preview.Source = LoadImage(item.Thumbnail); } catch { Preview.Source = null; }
        Preview.Focus();
        UpdateFilmstrip();
        UpdateEditButtons();
        try { await LoadEditorImage(previewFile ?? item.Path, token); Status.Text = "Foto aberta. As edições preservam o original."; return !token.IsCancellationRequested && editorItem?.Path == item.Path && Preview.OriginalWidth > 0; }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { Status.Text = "Não foi possível abrir a foto: " + error.Message; if (reviewMode) lastReviewError = error; return false; }
    }
    private void UpdateFilmstrip()
    {
        filmstripUpdating = true; Filmstrip.ItemsSource = Gallery.Items;
        Filmstrip.SelectedItem = Gallery.Items.FirstOrDefault(p => p.Path == editorItem?.Path);
        if (Filmstrip.SelectedItem != null) Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
        filmstripUpdating = false;
        var index = Gallery.Items.ToList().FindIndex(p => p.Path == editorItem?.Path);
        EditorPosition.Text = index >= 0 ? $"{index + 1} / {Gallery.Items.Count}" : "Fora do filtro atual";
        if (editorItem != null) SelectedName.Text = editorItem.Name;
    }
    private async Task LoadEditorImage(string path, CancellationToken token = default)
    {
        var generation = Interlocked.Increment(ref imageGeneration);
        var info = new FileInfo(path); var key = MediaIndex.XxHash64(Encoding.UTF8.GetBytes(path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)).ToString("x16");
        var folder = Path.Combine(workspace, "viewer"); Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, key + ".jpg"); var dimensionsPath = Path.Combine(folder, key + ".json"); int[] dimensions;
        if (File.Exists(imagePath) && File.Exists(dimensionsPath)) dimensions = Json.Read<int[]>(File.ReadAllText(dimensionsPath));
        else
        {
            var temporary = imagePath + "." + Guid.NewGuid().ToString("N") + ".jpg";
            try
            {
                var text = await Tools.Text(tools.Magick, [path + "[0]", "-auto-orient", "-format", "%w %h", "-write", "info:", "-thumbnail", "1800x1400>", "-quality", "92", "jpeg:" + temporary], token);
                dimensions = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (dimensions.Length != 2) throw new InvalidDataException("Dimensões de imagem indisponíveis.");
                token.ThrowIfCancellationRequested(); File.Move(temporary, imagePath, true); Files.AtomicText(dimensionsPath, Json.Write(dimensions));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        token.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref imageGeneration)) return;
        Preview.Source = LoadImage(imagePath); Preview.OriginalWidth = dimensions[0]; Preview.OriginalHeight = dimensions[1];
    }
    private void BackToLibrary(object sender, RoutedEventArgs e)
    {
        SaveEditSession(); editorLoad?.Cancel(); EditorScreen.Visibility = Visibility.Collapsed;
        LibrarySidebar.Visibility = Tabs.Visibility = SelectionBar.Visibility = LibraryToolbar.Visibility = Visibility.Visible;
        Tabs.SelectedIndex = 0; Preview.CropMode = false; CropActions.Visibility = Visibility.Collapsed;
        if (editorItem != null) Gallery.ScrollTo(editorItem.Path); Gallery.Focus();
    }
    private async Task NavigateEditor(int delta)
    {
        if (editorItem == null || task != null) return;
        var photos = Gallery.Items.Where(p => p.Kind == "Foto").ToList(); var index = photos.FindIndex(p => p.Path == editorItem.Path);
        if (photos.Count == 0) return;
        var target = Math.Clamp(index + delta, 0, photos.Count - 1); if (target != index) await OpenEditor(photos[target]);
    }
    private async void PreviousPhoto(object sender, RoutedEventArgs e) => await NavigateEditor(-1);
    private async void NextPhoto(object sender, RoutedEventArgs e) => await NavigateEditor(1);
    private async void FilmstripSelected(object sender, SelectionChangedEventArgs e) { if (!filmstripUpdating && Filmstrip.SelectedItem is MediaItem item && item.Path != editorItem?.Path) await OpenEditor(item); }
    private void UpdateEditButtons() { ExportButton.IsEnabled = previewFile != null; UndoButton.IsEnabled = undoEdits.Count > 0; RedoButton.IsEnabled = redoEdits.Count > 0; SaveEditSession(); }
    private async void RedoPreview(object sender, RoutedEventArgs e) { if (redoEdits.Count == 0 || task != null) return; await Run(async token => { undoEdits.Push(previewFile); previewFile = redoEdits.Pop(); await LoadEditorImage(previewFile ?? Selected().Path, token); UpdateEditButtons(); }); }
    private async void ApplyCrop(object sender, RoutedEventArgs e)
    { if (Preview.CropGeometry is not { } geometry) return; Preview.CropMode = false; CropActions.Visibility = Visibility.Collapsed; await PreviewEdit("crop", new() { ["geometry"] = geometry }); }
    private void CancelCrop(object sender, RoutedEventArgs e) { Preview.CropMode = false; CropActions.Visibility = Visibility.Collapsed; }
    private async void ApplyAdjustments(object sender, RoutedEventArgs e)
    { await PreviewEdit("adjust", new() { ["brightness"] = Brightness.Value.ToString("F1", CultureInfo.InvariantCulture), ["contrast"] = Contrast.Value.ToString("F1", CultureInfo.InvariantCulture), ["saturation"] = Saturation.Value.ToString("F1", CultureInfo.InvariantCulture) }); Brightness.Value = Contrast.Value = 0; Saturation.Value = 100; }
    private async void ExportSelected(object sender, RoutedEventArgs e)
    {
        if (EditorScreen.Visibility == Visibility.Visible && previewFile != null) { ExportPreview(sender, e); return; }
        var selection = Selection(); if (selection.Count == 0) { Status.Text = "Selecione as fotos que deseja exportar."; return; }
        var dialog = new OpenFolderDialog { Title = "Exportar cópias para uma pasta" }; if (dialog.ShowDialog(this) != true) return;
        var success = await Run(async token => { foreach (var item in selection) { var target = Files.Unique(Path.Combine(dialog.FolderName, item.Name)); await FileActions.CopyVerified(item.Path, target, token); actions.Record("export-copy", item.Path, target, await Files.Hash(target, token)); } });
        if (success) Status.Text = $"{selection.Count} cópias exportadas. Os originais foram preservados.";
    }
    private async void PrintSelected(object sender, RoutedEventArgs e)
    {
        if (CurrentPhoto is not { } item) return;
        if (item.Kind != "Foto") { Status.Text = "Selecione uma foto para imprimir."; return; }
        try { if (EditorScreen.Visibility != Visibility.Visible && !await OpenEditor(item) || Preview.Source == null || Preview.OriginalWidth < 1) return; var dialog = new PrintDialog(); if (dialog.ShowDialog() != true) return; var image = new Image { Source = Preview.Source, Stretch = Stretch.Uniform, Width = dialog.PrintableAreaWidth, Height = dialog.PrintableAreaHeight }; image.Measure(new Size(image.Width, image.Height)); image.Arrange(new Rect(0, 0, image.Width, image.Height)); dialog.PrintVisual(image, item.Name); }
        catch (Exception error) { Info(error.Message); }
    }
    private void ExitApplication(object sender, RoutedEventArgs e) => Close();
    private void ShowAbout(object sender, RoutedEventArgs e) => Info("GuiaFotos\nOrganize suas fotos por pastas, pessoas e semelhança.\nProjeto independente inspirado na experiência do Picasa.");
    private void WindowKeyboard(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or PasswordBox or ComboBox or RangeBase) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.O) { AddFolder(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.E) { ExportSelected(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { RefreshFolder(sender, e); e.Handled = true; }
        else if (EditorScreen.Visibility == Visibility.Visible)
        { if (e.Key == Key.Escape) { if (Preview.CropMode) CancelCrop(sender, e); else BackToLibrary(sender, e); e.Handled = true; } else if (e.Key == Key.Left) { PreviousPhoto(sender, e); e.Handled = true; } else if (e.Key == Key.Right) { NextPhoto(sender, e); e.Handled = true; } }
    }
}
