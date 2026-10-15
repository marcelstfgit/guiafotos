using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GuiaFotos.Core;

namespace GuiaFotos.Windows;

public sealed class VersionsView : UserControl, IDisposable
{
    private readonly ListBox families = new() { DisplayMemberPath = "Description", SelectionMode = SelectionMode.Extended, MinWidth = 260 };
    private readonly ListBox photos = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly Slider threshold = new() { Minimum = .85, Maximum = .995, Value = .96, Width = 150, ToolTip = "Maior valor exige versões visualmente mais próximas. Ajuste para sua coleção." };
    private readonly CheckBox neural = new() { Content = "Incluir comparação SSCD", Margin = new Thickness(6) };
    private readonly CheckBox isolated = new() { Content = "Incluir fotos isoladas", Margin = new Thickness(6) };
    private readonly CheckBox force = new() { Content = "Reanalisar tudo", Margin = new Thickness(6) };
    private Catalog? catalog;
    private VisualCatalog? index;
    private ToolPaths? tools;
    private string workspace = "";
    private CancellationTokenSource? operation;
    public Func<bool>? LibraryBusy { get; set; }
    public Func<IReadOnlyList<string>, Task>? MoveToTrash { get; set; }
    public bool Busy => operation != null;
    public void Attach(string workspace, Catalog catalog, ToolPaths tools)
    {
        this.workspace = workspace; this.catalog = catalog; this.tools = tools;
        index = new(Path.Combine(workspace, "visual.sqlite"));
        neural.IsEnabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "sscd", "sscd-model.json")); neural.IsChecked = neural.IsEnabled; neural.ToolTip = neural.IsEnabled ? "Comparação adicional com modelo SSCD local." : "O modelo SSCD não está incluído nesta compilação; a comparação visual local continua disponível.";
        status.Text = neural.IsEnabled ? "SSCD disponível; o primeiro processamento cria o índice local." : "Comparação visual local disponível para brilho, compressão e resolução. O modelo SSCD não está incluído nesta compilação.";
    }
    public VersionsView()
    {
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Versões da mesma foto", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Encontre cópias com iluminação, compressão ou resolução alteradas. Os grupos são sugestões; compare antes de organizar. A sugestão de melhor cópia considera resolução, nitidez e exposição.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) });
        var toolbar = new WrapPanel(); header.Children.Add(toolbar);
        Add(toolbar, "Analisar / retomar biblioteca", Analyze); Add(toolbar, "Atualizar grupos", RefreshGroups);
        Add(toolbar, "Comparar selecionadas", Compare); Add(toolbar, "Unir grupos selecionados", Join);
        Add(toolbar, "Separar selecionadas do grupo", Separate); Add(toolbar, "Preferir cópia selecionada ★", Prefer);
        Add(toolbar, "Desfazer organização", () => Change(() => index?.Undo())); Add(toolbar, "Mover selecionadas para lixeira…", Trash);
        Add(toolbar, "Cancelar", Cancel); toolbar.Children.Add(neural); toolbar.Children.Add(force); toolbar.Children.Add(isolated);
        toolbar.Children.Add(new TextBlock { Text = "Exigência visual:", VerticalAlignment = VerticalAlignment.Center }); toolbar.Children.Add(threshold);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) }); columns.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(columns);
        columns.Children.Add(families); Grid.SetColumn(photos, 1); columns.Children.Add(photos);
        var template = new DataTemplate(typeof(VisualPhoto)); var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 160d); image.SetValue(Image.HeightProperty, 120d); image.SetBinding(Image.SourceProperty, new Binding("Thumbnail")); item.AppendChild(image);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Description")); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.MarginProperty, new Thickness(10)); item.AppendChild(text); template.VisualTree = item; photos.ItemTemplate = template;
        families.SelectionChanged += (_, _) => {
            if (families.SelectedItem is PhotoFamily family) { photos.ItemsSource = family.Photos.OrderByDescending(p => p.Id == family.Best.Id).ThenByDescending(VisualFeatures.BestScore).ToList(); status.Text = "Sugestão de melhor cópia: " + family.Best.Name + ". É uma avaliação heurística; o tamanho do arquivo não define a qualidade."; }
        };
    }
    private static void Add(Panel panel, string label, Action action)
    { var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(8, 5, 8, 5) }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private async Task Work(Func<CancellationToken, Task> work)
    {
        if (Busy || index == null || catalog == null || tools == null) return;
        if (LibraryBusy?.Invoke() == true) { status.Text = "Aguarde a tarefa da biblioteca/Pessoas."; return; }
        operation = new(); neural.IsEnabled = false; force.IsEnabled = false;
        try { await work(operation.Token); }
        catch (OperationCanceledException) { status.Text = "Cancelado; as fotos já analisadas ficam salvas para retomar."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { operation.Dispose(); operation = null; force.IsEnabled = true; neural.IsEnabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "sscd", "sscd-model.json")); }
    }
    private async Task Group(CancellationToken token)
    {
        var value = threshold.Value; var useNeural = neural.IsChecked == true; var includeSingles = isolated.IsChecked == true; status.Text = "Agrupando versões da biblioteca inteira…";
        var found = await Task.Run(() => index!.Families(value, token, useNeural, includeSingles), token); token.ThrowIfCancellationRequested(); families.ItemsSource = found; photos.ItemsSource = null;
        status.Text = $"{found.Count} famílias de versões; os resultados exigem revisão.";
    }
    private async void RefreshGroups() => await Work(Group);
    private async void Analyze() => await Work(async token => {
        var items = catalog!.All().Where(p => p.Kind == "Foto").ToList(); var useNeural = neural.IsChecked == true; var rebuild = force.IsChecked == true;
        var native = Path.Combine(AppContext.BaseDirectory, "tools", "faces", "onnxruntime.dll");
        using var sscd = useNeural ? await SscdModel.Open(native, Path.Combine(AppContext.BaseDirectory, "tools", "sscd"), token) : null;
        var progress = new Progress<string>(s => status.Text = s); int updated = 0, reused = 0; var failures = new List<string>(); var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(async () => {
            for (int i = 0; i < items.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var item = items[i]; ((IProgress<string>)progress).Report($"Comparação {i + 1}/{items.Count}: {item.Name}");
                try {
                    if (!rebuild && await index!.Indexed(item, sscd?.Version, token)) { reused++; continue; }
                    index!.Add(await VisualFeatures.Inspect(item, tools!, sscd, token)); updated++;
                } catch (Exception error) when (error is not OperationCanceledException) { failures.Add(item.Name + ": " + error.Message); }
            }
        }, token);
        await Group(token); status.Text += $" {updated} analisadas, {reused} reutilizadas; {watch.Elapsed.TotalSeconds:F1}s; {failures.Count} falhas.";
        if (failures.Count > 0) MessageBox.Show(string.Join('\n', failures.Take(12)), "Fotos não analisadas");
    });
    private async void Change(Action action) => await Work(async token => { await Task.Run(action, token); await Group(token); });
    private void Join()
    {
        if (Busy) return; var ids = families.SelectedItems.Cast<PhotoFamily>().SelectMany(f => f.Photos).Select(p => p.Id).Distinct().ToArray();
        if (ids.Length > 1) Change(() => index!.Join(ids)); else status.Text = "Selecione dois grupos com Ctrl para unir.";
    }
    private void Separate()
    {
        if (Busy || families.SelectedItem is not PhotoFamily family) return; var selected = photos.SelectedItems.Cast<VisualPhoto>().Select(p => p.Id).ToArray();
        if (selected.Length > 0) { isolated.IsChecked = true; Change(() => index!.Separate(selected, family.Photos.Select(p => p.Id).ToArray())); }
    }
    private void Prefer()
    { if (!Busy && families.SelectedItem is PhotoFamily family && photos.SelectedItem is VisualPhoto photo) Change(() => index!.Prefer(photo.Id, family.Photos.Select(p => p.Id).ToArray())); }
    private async void Trash()
    {
        if (Busy || MoveToTrash == null) return; var selected = photos.SelectedItems.Cast<VisualPhoto>().Select(p => p.Path).ToArray(); if (selected.Length == 0) return;
        try { await MoveToTrash(selected); RefreshGroups(); } catch (Exception error) { status.Text = error.Message; }
    }
    private async void Compare() => await Work(async token =>
    {
        var selected = photos.SelectedItems.Cast<VisualPhoto>().Take(4).ToArray(); if (selected.Length < 2) selected = families.SelectedItems.Cast<PhotoFamily>().Select(f => f.Best).DistinctBy(p => p.Id).Take(4).ToArray(); if (selected.Length < 2) { status.Text = "Selecione de duas a quatro versões com Ctrl."; return; }
        var window = new Window { Owner = Window.GetWindow(this), Title = "Comparar versões · originais preservados", Width = 1100, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid(); window.Content = grid;
        for (int i = 0; i < selected.Length; i++)
        {
            var photo = selected[i]; grid.ColumnDefinitions.Add(new ColumnDefinition()); var panel = new DockPanel { Margin = new Thickness(8) }; Grid.SetColumn(panel, i); grid.Children.Add(panel);
            var label = new TextBlock { Text = photo.Description + "\n" + photo.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
            try {
                var previews = Path.Combine(workspace, "visual-previews"); Directory.CreateDirectory(previews); var preview = Path.Combine(previews, photo.Id + ".jpg");
                if (!File.Exists(preview)) {
                    var temp = preview + "." + Guid.NewGuid().ToString("N") + ".jpg";
                    try { await Tools.Run(tools!.Magick, [photo.Path + "[0]", "-auto-orient", "-thumbnail", "1600x1600>", "-strip", temp], token); File.Move(temp, preview, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(preview); bitmap.EndInit(); bitmap.Freeze(); panel.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform }); }
            catch (Exception error) when (error is not OperationCanceledException) { panel.Children.Add(new Image { Source = new BitmapImage(new Uri(photo.Thumbnail)), Stretch = Stretch.Uniform }); }
        }
        window.ShowDialog();
    });
    public void Remap(string source, string destination) => index?.Remap(source, destination);
    public void Cancel() => operation?.Cancel();
    public void Dispose() => index?.Dispose();
}
