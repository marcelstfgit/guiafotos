using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GuiaFotos.Core;

namespace GuiaFotos.Windows;

public sealed class PeopleView : UserControl, IDisposable
{
    private readonly ListBox people = new() { DisplayMemberPath = "Name", MinWidth = 180, SelectionMode = SelectionMode.Extended };
    private readonly ListBox groups = new() { DisplayMemberPath = "Description", MinWidth = 180 };
    private readonly ListBox matches = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly Slider threshold = new() { Minimum = .1, Maximum = .95, Value = .5, Width = 130, ToolTip = "Maior valor exige rostos mais semelhantes; o valor não é probabilidade." };
    private readonly ComboBox modelSelector = new() { ItemsSource = FaceModels.Profiles, DisplayMemberPath = "Name", MinWidth = 310, Margin = new Thickness(4) };
    private readonly TextBlock modelDescription = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly ComboBox detectorSelector = new() { ItemsSource = new[] { "SCRFD 10G · detalhado", "YuNet · leve" }, SelectedIndex = 0, Margin = new Thickness(4) };
    private readonly CheckBox forceFaces = new() { Content = "Reanalisar fotos já processadas", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox detailed = new() { Content = "Buscar rostos pequenos (mais lento)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private readonly Slider clusterThreshold = new() { Minimum = .3, Maximum = .9, Value = .65, Width = 110 };
    private readonly ComboBox minimumFaces = new() { ItemsSource = new[] { 2, 3, 4, 5, 8, 10 }, SelectedItem = 3, Width = 55 };
    private FacePreferences preferences = new();
    private bool refreshing;
    private int searches;
    private FaceDetectionOptions Detection => new(detectorSelector.SelectedIndex == 0 ? "scrfd10g" : "yunet", detailed.IsChecked == true);
    private FaceProfile profile = FaceModels.Profiles[0];
    private readonly Dictionary<string, double> thresholds = new();
    private string workspace = "";
    private Catalog? catalog;
    private ToolPaths? tools;
    private PeopleCatalog? index;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? search;
    public Func<bool>? LibraryBusy { get; set; }
    public bool Busy => operation != null || searches > 0;
    public void Attach(string workspace, Catalog catalog, ToolPaths tools)
    {
        this.workspace = workspace; this.catalog = catalog; this.tools = tools;
        refreshing = true; preferences = FacePreferences.Load(Path.Combine(workspace, "face-preferences.json"));
        detectorSelector.SelectedIndex = preferences.Detector == "scrfd10g" ? 0 : 1; detailed.IsChecked = preferences.Tiles; minimumFaces.SelectedItem = preferences.MinimumFaces; refreshing = false;
        var preference = Path.Combine(workspace, "face-profile.txt");
        var saved = File.Exists(preference) ? File.ReadAllText(preference).Trim() : preferences.Profile;
        profile = FaceModels.Profiles.FirstOrDefault(p => p.Id == saved) ?? FaceModels.Profiles[0];
        modelSelector.SelectedItem = profile;
        if (index == null) SwitchProfile(profile);
    }
    public PeopleView()
    {
        var panel = new DockPanel { Margin = new Thickness(14) }; Content = panel;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Pessoas", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Dê um nome a um rosto e encontre outras fotos da mesma pessoa.", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 6, 0, 13) });
        var primary = new WrapPanel(); header.Children.Add(primary);
        Add(primary, "Analisar biblioteca", Analyze); modelSelector.MinWidth = 250; primary.Children.Add(modelSelector);
        Add(primary, "Nomear…", NameFace); Add(primary, "Unir pessoas", MergePeople); Add(primary, "Desfazer", () => Change(() => { index?.Undo(); })); Add(primary, "Cancelar", Cancel);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 7, 0, 8) }; header.Children.Add(toolbar);
        Add(toolbar, "Buscar pessoa", Search); Add(toolbar, "Confirmar", ConfirmFaces); Add(toolbar, "Não é esta pessoa", RejectFaces); Add(toolbar, "Abrir foto", OpenPhoto);
        toolbar.Children.Add(new TextBlock { Text = "Semelhança:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 3, 0) }); toolbar.Children.Add(threshold);
        var options = new Expander { Header = "Opções de reconhecimento", Margin = new Thickness(3, 0, 3, 10) }; header.Children.Add(options);
        var settingsPanel = new StackPanel(); options.Content = settingsPanel; var detectionPanel = new WrapPanel(); settingsPanel.Children.Add(detectionPanel);
        detectionPanel.Children.Add(detectorSelector); detectionPanel.Children.Add(detailed); detectionPanel.Children.Add(forceFaces); settingsPanel.Children.Add(modelDescription);
        var grouping = new WrapPanel { Margin = new Thickness(4) }; settingsPanel.Children.Add(grouping);
        grouping.Children.Add(new TextBlock { Text = "Agrupamento:", VerticalAlignment = VerticalAlignment.Center }); grouping.Children.Add(clusterThreshold);
        grouping.Children.Add(new TextBlock { Text = "Mínimo de fotos:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 3, 0) }); grouping.Children.Add(minimumFaces);
        Add(grouping, "Reagrupar", RebuildGroups); Add(grouping, "Nomear grupo…", () => NameFaces(true));
        threshold.ValueChanged += (_, _) => SavePreferences(); clusterThreshold.ValueChanged += (_, _) => SavePreferences();
        detectorSelector.SelectionChanged += (_, _) => SavePreferences(); detailed.Checked += (_, _) => SavePreferences(); detailed.Unchecked += (_, _) => SavePreferences(); minimumFaces.SelectionChanged += (_, _) => SavePreferences();
        modelSelector.SelectionChanged += (_, _) => {
            if (modelSelector.SelectedItem is not FaceProfile selected || string.IsNullOrEmpty(workspace)) return;
            if (Busy) { modelSelector.SelectedItem = profile; return; }
            try { SwitchProfile(selected); } catch (Exception error) { status.Text = error.Message; modelSelector.SelectedItem = profile; }
        };
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); columns.ColumnDefinitions.Add(new ColumnDefinition()); panel.Children.Add(columns);
        var left = new DockPanel { Margin = new Thickness(4) }; columns.Children.Add(left);
        var list = new StackPanel(); left.Children.Add(list);
        list.Children.Add(new TextBlock { Text = "Pessoas identificadas", FontWeight = FontWeights.Bold }); people.MaxHeight = 230; list.Children.Add(people);
        list.Children.Add(new TextBlock { Text = "Grupos sugeridos / sem nome", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 5) }); groups.MaxHeight = 350; list.Children.Add(groups);
        Grid.SetColumn(matches, 1); columns.Children.Add(matches);
        var virtualPanel = new FrameworkElementFactory(typeof(VirtualizingPhotoPanel)); matches.ItemsPanel = new ItemsPanelTemplate(virtualPanel);
        ScrollViewer.SetCanContentScroll(matches, true); ScrollViewer.SetHorizontalScrollBarVisibility(matches, ScrollBarVisibility.Disabled);
        var template = new DataTemplate(typeof(PersonMatch));
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(FrameworkElement.WidthProperty, 158d); item.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 150d); image.SetValue(Image.HeightProperty, 128d); image.SetValue(Image.StretchProperty, System.Windows.Media.Stretch.Uniform); image.SetBinding(Image.SourceProperty, new Binding("Thumbnail")); item.AppendChild(image);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Description")); text.SetValue(TextBlock.MarginProperty, new Thickness(2, 6, 2, 0)); text.SetValue(FrameworkElement.MaxHeightProperty, 34d); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); item.AppendChild(text);
        template.VisualTree = item; matches.ItemTemplate = template;
        matches.MouseDoubleClick += (_, _) => OpenPhoto();
        people.SelectionChanged += (_, _) => { if (!refreshing && operation == null && people.SelectedItem != null) Search(); };
        Loaded += (_, _) => { if (groups.ItemsSource == null) RebuildGroups(); };
        groups.SelectionChanged += (_, _) => { if (groups.SelectedItem is FaceGroup group) { search?.Cancel(); matches.ItemsSource = group.Faces.Select(f => new PersonMatch(f, 0, false)).ToList(); status.Text = group.Description + ". Nomeie um rosto para buscar a pessoa."; } };
    }
    private void SwitchProfile(FaceProfile selected)
    {
        if (index != null && profile.Id == selected.Id) return;
        var next = FaceModels.OpenCatalog(workspace, selected);
        thresholds[profile.Id] = threshold.Value;
        index?.Dispose(); index = next; profile = selected;
        matches.ItemsSource = null; people.ItemsSource = null; groups.ItemsSource = null;
        refreshing = true; threshold.Value = preferences.Search(profile.Id); clusterThreshold.Value = preferences.Cluster(profile.Id); refreshing = false;
        modelDescription.Text = selected.Description + " Escolha SCRFD para análise detalhada ou YuNet para análise leve. Qualidade e tempo variam com as fotos e o computador; não há ranking validado no aplicativo. Ao trocar, nomes e análises do outro motor ficam preservados no índice dele.";
        try { File.WriteAllText(Path.Combine(workspace, "face-profile.txt"), profile.Id); }
        catch (IOException error) { status.Text = "Motor selecionado; não foi possível salvar a preferência: " + error.Message; }
        preferences.Profile = profile.Id; SavePreferences(); RefreshPeople(); groups.ItemsSource = null; if (IsLoaded) RebuildGroups();
    }
    private void SavePreferences()
    {
        if (refreshing || string.IsNullOrEmpty(workspace)) return;
        preferences.Profile = profile.Id; preferences.Detector = Detection.Detector; preferences.Tiles = Detection.Tiles;
        preferences.MinimumFaces = minimumFaces.SelectedItem is int n ? n : 3;
        preferences.SearchThresholds[profile.Id] = threshold.Value; preferences.ClusterThresholds[profile.Id] = clusterThreshold.Value;
        try { preferences.Save(Path.Combine(workspace, "face-preferences.json")); } catch (IOException error) { status.Text = error.Message; }
    }
    private void Selectors(bool enabled) { modelSelector.IsEnabled = enabled; detectorSelector.IsEnabled = enabled; detailed.IsEnabled = enabled; forceFaces.IsEnabled = enabled; }
    private async Task Group(CancellationToken token)
    {
        if (index == null) return; var value = clusterThreshold.Value; var minimum = minimumFaces.SelectedItem is int n ? n : 3;
        status.Text = "Agrupando todos os rostos; os isolados ficam disponíveis para revisão…";
        var found = await Task.Run(() => index.UnknownGroups(value, minimum, token), token); token.ThrowIfCancellationRequested(); groups.ItemsSource = found;
    }
    private async void RebuildGroups() => await Work(async token => { await Group(token); status.Text = "Agrupamento concluído na biblioteca inteira."; });
    private async void Change(Action action) => await Work(async token => { await Task.Run(action, token); RefreshPeople(); await Group(token); status.Text = "Correção salva. Use Desfazer correção para voltar."; });
    private async Task Work(Func<CancellationToken, Task> work)
    {
        if (Busy || index == null) return;
        if (LibraryBusy?.Invoke() == true) { status.Text = "Aguarde a tarefa da biblioteca."; return; }
        operation = new(); Selectors(false);
        try { await work(operation.Token); }
        catch (OperationCanceledException) { status.Text = "Cancelado; trabalho já concluído foi preservado."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { operation.Dispose(); operation = null; Selectors(!Busy); }
    }
    private static void Add(Panel panel, string label, Action action)
    { var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(8, 5, 8, 5) }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private void RefreshPeople()
    {
        if (index == null) return; var selected = (people.SelectedItem as Person)?.Id; refreshing = true;
        people.ItemsSource = index.People(); people.SelectedItem = people.Items.Cast<Person>().FirstOrDefault(x => x.Id == selected); refreshing = false;
    }
    private async void Analyze()
    {
        if (Busy || index == null || catalog == null || tools == null) return;
        if (LibraryBusy?.Invoke() == true) { status.Text = "Aguarde a tarefa da biblioteca antes de analisar rostos."; return; }
        var photos = catalog.All().Where(x => x.Kind == "Foto").ToList();
        if (photos.Count == 0) { status.Text = "Adicione fotos na Biblioteca primeiro."; return; }
        if (MessageBox.Show($"Analisar com {profile.Name}? Na primeira ativação serão baixados aproximadamente {profile.DownloadMb} MB de modelos. Nenhuma foto será enviada. O índice deste motor ficará guardado localmente.", "Pessoas", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        operation = new(); Selectors(false); var detection = Detection; var rebuild = forceFaces.IsChecked == true; var token = operation.Token;
        try
        {
            var progress = new Progress<string>(value => status.Text = value);
            var native = Path.Combine(AppContext.BaseDirectory, "tools", "faces", "onnxruntime.dll");
            try { using var probe = new NativeOnnx(native); }
            catch (DllNotFoundException error) { throw new IOException("O motor facial precisa do Microsoft Visual C++ x64. Use 'Instalar runtime facial…' e tente novamente.", error); }
            var models = Path.Combine(workspace, "face-models", profile.Id); await FaceModels.Ensure(models, profile, progress, token); await FaceModels.EnsureDetector(models, detection, progress, token);
            var errors = new List<string>(); var updated = 0; var reused = 0;
            await Task.Run(async () =>
            {
                using var engine = new FaceEngine(native, models, tools, Path.Combine(workspace, "face-thumbnails", profile.Id), profile, detection);
                for (int i = 0; i < photos.Count; i++)
                {
                    token.ThrowIfCancellationRequested(); var photo = photos[i];
                    ((IProgress<string>)progress).Report($"Rostos {i + 1}/{photos.Count}: {photo.Name}");
                    try
                    {
                        photo = photo with { QuickSignature = await MediaIndex.Sample(photo.Path, token) };
                        var stamp = FaceEngine.Stamp(photo, profile, detection);
                        if (!rebuild && index.Indexed(photo.Path, stamp)) { reused++; continue; }
                        var faces = await engine.Detect(photo, token); index.Replace(photo.Path, stamp, faces); updated++;
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { errors.Add(photo.Name + ": " + error.Message); }
                }
            }, token);
            RefreshPeople(); await Group(token);
            status.Text = $"{updated} fotos analisadas; {reused} reutilizadas; {index.Faces().Count} rostos no índice. {errors.Count} falhas.";
            if (errors.Count > 0) MessageBox.Show(string.Join('\n', errors.Take(10)), "Fotos não analisadas");
        }
        catch (OperationCanceledException) { status.Text = "Análise cancelada; progresso concluído foi preservado."; RefreshPeople(); }
        catch (Exception error) { status.Text = "Não foi possível analisar rostos: " + error.Message; }
        finally { operation.Dispose(); operation = null; Selectors(!Busy); }
    }
    private void NameFace() => NameFaces(false);
    private void NameFaces(bool entireGroup)
    {
        if (Busy || index == null) return;
        var ids = entireGroup && groups.SelectedItem is FaceGroup group ? group.Faces.Select(f => f.Id).ToArray() : matches.SelectedItems.Cast<PersonMatch>().Select(m => m.Face.Id).ToArray();
        if (ids.Length == 0) return;
        var name = InputDialog.Ask(Window.GetWindow(this)!, "Nomear / separar pessoa", "Nome para os rostos selecionados (um novo nome separa da pessoa atual):", "");
        if (name != null) Change(() => index.AssignMany(ids, name));
    }
    private void MergePeople()
    {
        if (Busy || index == null) return; var selected = people.SelectedItems.Cast<Person>().ToArray(); if (selected.Length < 2) { status.Text = "Selecione duas pessoas com Ctrl para unir."; return; }
        Change(() => index.MergeMany(selected.Skip(1).Select(p => p.Id).ToArray(), selected[0].Id));
    }
    private async void Search()
    {
        if (operation != null || index == null || people.SelectedItem is not Person person) return;
        search?.Cancel(); var current = new CancellationTokenSource(); search = current; searches++; Selectors(false); var value = threshold.Value; var searchIndex = index;
        try { var found = await Task.Run(() => searchIndex.Search(person.Id, value, current.Token), current.Token); current.Token.ThrowIfCancellationRequested(); matches.ItemsSource = found; status.Text = $"{person.Name}: {found.Select(x => x.Face.MediaPath).Distinct().Count()} fotos; confirme/rejeite os rostos sugeridos."; }
        catch (OperationCanceledException) { }
        catch (Exception error) { status.Text = error.Message; }
        finally { searches--; if (ReferenceEquals(search, current)) search = null; current.Dispose(); Selectors(!Busy); }
    }
    private void ConfirmFaces()
    {
        if (Busy || index == null || people.SelectedItem is not Person person) return; var ids = matches.SelectedItems.Cast<PersonMatch>().Select(m => m.Face.Id).ToArray();
        Change(() => index.ConfirmMany(ids, person.Id));
    }
    private void RejectFaces()
    {
        if (Busy || index == null || people.SelectedItem is not Person person) return; var ids = matches.SelectedItems.Cast<PersonMatch>().Select(m => m.Face.Id).ToArray();
        Change(() => index.RejectMany(ids, person.Id));
    }
    private void OpenPhoto()
    { if (matches.SelectedItem is PersonMatch match) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(match.Face.MediaPath) { UseShellExecute = true }); } catch (Exception error) { status.Text = error.Message; } }
    public void Cancel() { operation?.Cancel(); search?.Cancel(); }
    public void Remap(string source, string destination)
    {
        foreach (var candidate in FaceModels.Profiles)
        {
            if (index != null && candidate.Id == profile.Id) { index.Remap(source, destination); continue; }
            if (!File.Exists(FaceModels.CatalogPath(workspace, candidate))) continue;
            using var other = FaceModels.OpenCatalog(workspace, candidate); other.Remap(source, destination);
        }
    }
    public void Dispose() => index?.Dispose();
}
