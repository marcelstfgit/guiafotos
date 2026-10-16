using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GuiaFotos.Core;
using Microsoft.Win32;

namespace GuiaFotos.Windows;

public partial class MainWindow : Window
{
    private readonly string workspace;
    private readonly Catalog catalog;
    private readonly MediaLibrary library;
    private readonly FileActions actions;
    private readonly PhotoEditor editor;
    private readonly ToolPaths tools;
    private readonly WindowsSecrets secrets;
    private readonly OpenAiLogin login;
    private readonly AiAssistant ai;
    private Settings settings;
    private CancellationTokenSource? task;
    private string? previewFile;
    private MediaItem? previewSource;
    private AiPlan? plan;
    private List<MediaItem> planItems = [];
    private string filter = "all";
    private bool refreshing;
    private bool cloudTrash;
    private readonly PeopleView peopleView = new();
    private readonly VersionsView versionsView = new();
    private string SettingsFile => Path.Combine(workspace, "settings.json");
    public MainWindow() : this(false) { }
    public MainWindow(bool smokeTest)
    {
        workspace = smokeTest && Environment.GetEnvironmentVariable("GUIAFOTOS_UI_REVIEW_ROOT") is { Length: > 0 } reviewRoot ? reviewRoot : AppPaths.Workspace(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        InitializeComponent(); Directory.CreateDirectory(workspace);
        catalog = new Catalog(Path.Combine(workspace, "catalog.sqlite")); tools = ToolPaths.Discover(AppContext.BaseDirectory);
        library = new MediaLibrary(tools, Path.Combine(workspace, "thumbnails")); actions = new FileActions(workspace); editor = new PhotoEditor(tools, library, actions);
        secrets = new WindowsSecrets(Path.Combine(workspace, "credentials")); login = new OpenAiLogin(secrets, Path.Combine(workspace, "host-id.txt")); ai = new AiAssistant(secrets, login);
        settings = Settings.Load(SettingsFile); CloudUrl.Text = settings.NextcloudUrl; CloudUser.Text = settings.NextcloudUser; CloudFolder.Text = settings.NextcloudFolder;
        peopleView.LibraryBusy = () => task != null || versionsView.Busy; peopleView.Attach(workspace, catalog, tools); Tabs.Items.Add(new TabItem { Header = "Pessoas", Content = peopleView });
        versionsView.LibraryBusy = () => task != null || peopleView.Busy; versionsView.Attach(workspace, catalog, tools);
        versionsView.MoveToTrash = async paths => {
            if (!Confirm($"Mover {paths.Count} arquivos selecionados para a lixeira do GuiaFotos? Você poderá restaurá-los pelo Histórico.")) return;
            await Run(async token => { foreach (var path in paths) { await actions.Quarantine(path, token); catalog.Remove(path); } });
        };
        Tabs.Items.Add(new TabItem { Header = "Versões de fotos", Content = versionsView });
        Provider.SelectedIndex = settings.AiProvider == "Claude" ? 1 : settings.AiProvider == "ChatGPT" ? 2 : 0; Model.Text = settings.AiModel; ExportFolder.Text = settings.ExportDirectory;
        Gallery.PhotoOpened += item => _ = OpenEditor(item);
        Gallery.DeleteRequested += () => TrashSelected(this, new RoutedEventArgs());
        Gallery.ZoomRequested += delta => ThumbnailZoom.Value += delta;
        Preview.CropChanged += () => ApplyCropButton.IsEnabled = Preview.CropGeometry != null;
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); Refresh(); };
        if (!smokeTest) Loaded += async (_, _) => { await Run(async token => { var warnings = await actions.Recover(token); if (warnings.Count > 0) Info(string.Join('\n', warnings)); }); Refresh(); };
    }
    private bool Confirm(string text) => MessageBox.Show(this, text, "GuiaFotos — confirmar", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void Info(string text) { if (reviewMode) { reviewNotes.Add(text); return; } MessageBox.Show(this, text, "GuiaFotos", MessageBoxButton.OK, MessageBoxImage.Information); }
    private bool LibraryBusy => task != null || peopleView.Busy || versionsView.Busy;
    private async Task<bool> Run(Func<CancellationToken, Task> work)
    {
        if (LibraryBusy) { Info("Aguarde a tarefa atual da biblioteca/Pessoas ou cancele-a na respectiva tela."); return false; }
        task = new CancellationTokenSource(); Progress.Visibility = Visibility.Visible;
        try { await work(task.Token); Status.Text = "Tarefa concluída."; return true; }
        catch (OperationCanceledException) { Status.Text = "Tarefa cancelada; originais preservados."; return false; }
        catch (Exception error) { Status.Text = "Falha: " + error.Message; if (reviewMode) lastReviewError = error; else MessageBox.Show(this, error.Message, "GuiaFotos — operação não concluída", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        finally { task.Dispose(); task = null; Progress.Visibility = Visibility.Collapsed; libraryDirty = true; Refresh(); }
    }
    private IProgress<string> ProgressText() => new Progress<string>(text => Status.Text = text);
    private MediaItem? CurrentPhoto => EditorScreen.Visibility == Visibility.Visible ? editorItem : Gallery.SelectedItem;
    private List<MediaItem> Selection() => EditorScreen.Visibility == Visibility.Visible && editorItem != null ? [editorItem] : Gallery.SelectedItems.ToList();
    private MediaItem Selected() => CurrentPhoto ?? throw new InvalidOperationException("Selecione uma foto na biblioteca.");
    private void Refresh()
    {
        var updateNavigation = libraryDirty || cachedLibrary == null;
        if (updateNavigation) { cachedLibrary = catalog.All(); libraryDirty = false; }
        var query = Search.Text.Trim();
        var items = (cachedLibrary ?? []).Where(p => query.Length == 0 || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Tags.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Album.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        items = FilterLibrary(items).ToList();
        refreshing = true;
        Gallery.ItemsSource = items;
        refreshing = false;
        Count.Text = $"{items.Count} itens · {items.Sum(x => x.Size) / 1048576d:F1} MB";
        if (updateNavigation) UpdateLibraryNavigation(); UpdateLibraryHeading(items); UpdateSelectionBar();
        if (EditorScreen.Visibility == Visibility.Visible) UpdateFilmstrip();
    }
    private void ShowAll(object sender, RoutedEventArgs e) { activeFolder = null; activeAlbum = null; navigationUpdating = true; Folders.SelectedItem = Albums.SelectedItem = null; navigationUpdating = false; filter = "all"; Search.Text = ""; ShowPage(0); Refresh(); }
    private void ShowFavorites(object sender, RoutedEventArgs e) { filter = filter == "favorites" ? "all" : "favorites"; ShowPage(0); Refresh(); }
    private void ShowPhotos(object sender, RoutedEventArgs e) { filter = filter == "photos" ? "all" : "photos"; ShowPage(0); Refresh(); }
    private void ShowVideos(object sender, RoutedEventArgs e) { filter = filter == "videos" ? "all" : "videos"; ShowPage(0); Refresh(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (catalog != null) { searchDelay.Stop(); searchDelay.Start(); } }
    private void FolderSelected(object sender, SelectionChangedEventArgs e) { if (!navigationUpdating && Folders.SelectedItem is LibraryLocation folder) { activeFolder = folder.Path; activeAlbum = null; navigationUpdating = true; Albums.SelectedItem = null; navigationUpdating = false; ShowPage(0); Refresh(); } }
    private async Task Scan(string folder, CancellationToken token)
    {
        var progress = ProgressText(); var errors = await Task.Run(() => library.Scan(folder, catalog, progress, token), token);
        if (errors.Count > 0) Info($"{errors.Count} arquivos não importados:\n" + string.Join('\n', errors.Take(15)));
    }
    private async void AddFolder(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "Escolha a pasta de fotos e vídeos" }; if (dialog.ShowDialog(this) == true) await Run(token => Scan(dialog.FolderName, token)); }
    private async void Dropped(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Run(async token => { foreach (var path in paths) if (Directory.Exists(path)) await Scan(path, token); else if (MediaLibrary.Supported(path)) catalog.Add(await library.Inspect(path, token)); }); }
    private async void RefreshFolder(object sender, RoutedEventArgs e)
    { if (activeFolder != null) await Run(token => Scan(activeFolder, token)); else AddFolder(sender, e); }
    private static BitmapImage LoadImage(string path)
    { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image; }
    private void SelectedPhoto(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return;
        UpdateSelectionBar();
        if (Gallery.SelectedItem is not MediaItem item) return;
        Rating.SelectedIndex = item.Rating; TagsBox.Text = item.Tags; AlbumBox.Text = item.Album;
    }
    private void OpenOriginal(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(Selected().Path) { UseShellExecute = true }); } catch (Exception error) { Info(error.Message); } }
    private void RevealOriginal(object sender, RoutedEventArgs e) { try { var start = new ProcessStartInfo("explorer.exe"); start.ArgumentList.Add("/select," + Selected().Path); Process.Start(start); } catch (Exception error) { Info(error.Message); } }
    private async Task PreviewEdit(string operation, Dictionary<string, string>? options = null)
    {
        if (CurrentPhoto is not { } selected) { Status.Text = "Selecione uma foto para editar."; return; }
        if (EditorScreen.Visibility != Visibility.Visible && !await OpenEditor(selected)) return;
        editorLoad?.Cancel();
        await Run(async token => { var source = Selected(); var output = await editor.Edit(previewFile ?? source.Path, Path.Combine(workspace, "previews"), operation, options, token);
            undoEdits.Push(previewFile); redoEdits.Clear(); previewFile = output; previewSource = source; await LoadEditorImage(output, token); UpdateEditButtons(); Status.Text = "Prévia pronta; o original continua intacto."; });
    }
    private async void Lucky(object sender, RoutedEventArgs e) => await PreviewEdit("enhance");
    private async void RestoreOld(object sender, RoutedEventArgs e) => await PreviewEdit("restore");
    private async void Rotate(object sender, RoutedEventArgs e) => await PreviewEdit("rotate", new() { ["degrees"] = "90" });
    private async void Crop(object sender, RoutedEventArgs e) { if (CurrentPhoto is not { } item || LibraryBusy) return; if (EditorScreen.Visibility != Visibility.Visible && !await OpenEditor(item)) return; Preview.CropMode = true; CropActions.Visibility = Visibility.Visible; }
    private async void UndoPreview(object sender, RoutedEventArgs e) { if (undoEdits.Count == 0 || task != null) return; await Run(async token => { redoEdits.Push(previewFile); previewFile = undoEdits.Pop(); await LoadEditorImage(previewFile ?? Selected().Path, token); UpdateEditButtons(); }); }
    private async void ExportPreview(object sender, RoutedEventArgs e)
    {
        if (previewFile == null || previewSource == null) return;
        var dialog = new SaveFileDialog { Title = "Salvar cópia editada", FileName = Path.GetFileName(previewFile), Filter = "JPEG|*.jpg", InitialDirectory = ExportDirectory(previewSource) };
        if (dialog.ShowDialog(this) != true) return;
        await ExportPreviewTo(dialog.FileName);
    }
    private async Task<bool> ExportPreviewTo(string target)
    {
        if (previewFile == null || previewSource == null) return false;
        var source = previewFile; var original = previewSource;
        return await Run(async token => { if (File.Exists(target)) throw new IOException("Escolha outro nome; exportação preserva arquivos existentes."); await FileActions.CopyVerified(source, target, token); if (File.Exists(source + ".recipe.json")) await FileActions.CopyVerified(source + ".recipe.json", target + ".recipe.json", token); actions.Record("export-copy", original.Path, target, await Files.Hash(target, token)); catalog.Add(await library.Inspect(target, token)); });
    }
    private string ExportDirectory(MediaItem item) => string.IsNullOrWhiteSpace(settings.ExportDirectory) ? Path.Combine(Path.GetDirectoryName(item.Path)!, "GuiaFotos-exportados") : settings.ExportDirectory;
    private async void StripExif(object sender, RoutedEventArgs e)
        => await StripMetadata(false);
    private async void StripPrivate(object sender, RoutedEventArgs e) => await StripMetadata(true);
    private async Task StripMetadata(bool allPrivate)
    { if (!Confirm(allPrivate ? "Criar cópias sem EXIF/GPS/XMP/IPTC e metadados pessoais de vídeo? Perfis de cor são preservados quando possível; formatos que não permitem a remoção segura serão recusados." : "Criar cópias sem EXIF dos itens selecionados? Os originais serão preservados.")) return;
        await Run(async token => { var items = Selection(); if (items.Count == 0) throw new InvalidOperationException("Selecione itens."); var results = new List<string>(); foreach (var item in items) { var output = await editor.StripExif(item.Path, ExportDirectory(item), allPrivate, token); catalog.Add(await library.Inspect(output, token)); results.Add(Path.GetFileName(output) + " — relatório: " + output + ".audit.json"); } Info("Cópias verificadas:\n" + string.Join('\n', results)); }); }
    private void SaveOrganize(object sender, RoutedEventArgs e)
    { if (LibraryBusy) { Status.Text = "Aguarde a tarefa atual antes de organizar as fotos."; return; } try { var item = Selected(); SaveCatalogItem(item with { Rating = Math.Max(0, Rating.SelectedIndex), Tags = TagsBox.Text, Album = AlbumBox.Text }); libraryDirty = true; Refresh(); } catch (Exception error) { Info(error.Message); } }
    private async void EditMetadata(object sender, RoutedEventArgs e)
    {
        await Run(async token => { var item = Selected(); var tags = await library.Metadata(item.Path, token); var dialog = new MetadataDialog(tags) { Owner = this };
            if (dialog.ShowDialog() == true) { var output = await editor.MetadataCopy(item.Path, ExportDirectory(item), dialog.Updates, token, advancedMode: true); catalog.Add(await library.Inspect(output, token)); Info("Cópia com metadados editados:\n" + output); } });
    }
    private async void FindExact(object sender, RoutedEventArgs e) => await DuplicateSearch(false);
    private async void FindCandidates(object sender, RoutedEventArgs e) => await Run(async token => {
        DuplicateList.ItemsSource = await Task.Run(() => MediaLibrary.Duplicates(catalog.All(), false), token); Tabs.SelectedIndex = 1;
        Info("Candidatos por tamanho + primeiros e últimos 16 KB. Use Confirmar idênticos para comparar os bytes completos desses candidatos.");
    });
    private async void FindDuplicates(object sender, RoutedEventArgs e) => await DuplicateSearch(true);
    private async Task DuplicateSearch(bool similar)
    { await Run(async token => { DuplicateList.ItemsSource = await Task.Run(() => library.VerifiedDuplicates(catalog, similar, token), token); Tabs.SelectedIndex = 1; }); }
    private void DuplicateSelected(object sender, SelectionChangedEventArgs e)
    { if (DuplicateList.SelectedItem is DuplicateGroup group) { activeFolder = activeAlbum = null; filter = "all"; Search.Text = ""; Refresh(); Gallery.SelectedItems.Clear(); foreach (var item in group.Items) Gallery.SelectedItems.Add(item); ShowPage(0); Status.Text = "Grupo selecionado na Biblioteca. Compare antes de mover arquivos à lixeira."; } }
    private void RemapIndexes(string source, string destination)
    {
        peopleView.Remap(source, destination); versionsView.Remap(source, destination);
        if (editorItem?.Path == source) editorItem = editorItem with { Path = destination };
        if (previewSource?.Path == source) previewSource = previewSource with { Path = destination };
        if (editSessions.Remove(source, out var session)) editSessions[destination] = session;
    }
    private async void RenameByDate(object sender, RoutedEventArgs e)
    {
        var items = Selection(); if (items.Count == 0) { Info("Selecione os itens que deseja renomear."); return; }
        var rename = FileActions.PlanNames(items); var preview = string.Join('\n', rename.Take(20).Select(x => Path.GetFileName(x.Source) + " → " + Path.GetFileName(x.Destination)));
        if (!Confirm("Prefixo 000001 para a foto mais recente; nomes incluem a data de captura.\n\n" + preview + $"\n\nTotal: {rename.Count}. Confirmar renomeação? É possível desfazer pelo Histórico.")) return;
        await Run(async token => { await actions.Rename(rename, token); foreach (var item in items) catalog.Remove(item.Path); foreach (var entry in rename) { RemapIndexes(entry.Source, entry.Destination); var previous = items.Single(x => x.Path == entry.Source); catalog.Add((await library.Inspect(entry.Destination, token)) with { Tags = previous.Tags, Rating = previous.Rating, Album = previous.Album }); } });
    }
    private async void TrashSelected(object sender, RoutedEventArgs e)
    { var items = Selection(); if (items.Count == 0) return; if (!Confirm($"Mover {items.Count} arquivos para a lixeira do GuiaFotos? Uma cópia verificada será guardada antes de remover os arquivos da pasta.")) return;
        await Run(async token => { foreach (var item in items) { await actions.Quarantine(item.Path, token); catalog.Remove(item.Path); editSessions.Remove(item.Path); if (editorItem?.Path == item.Path && EditorScreen.Visibility == Visibility.Visible) { BackToLibrary(sender, e); editSessions.Remove(item.Path); editorItem = null; } } }); }
    private void RefreshHistory(object sender, RoutedEventArgs e) { History.ItemsSource = actions.History(); if (actions.Warnings.Count > 0) Info(string.Join('\n', actions.Warnings)); }
    private async void RestoreHistory(object sender, RoutedEventArgs e)
    { if (History.SelectedItem is not FileOperation entry || !Confirm("Restaurar este arquivo? Se o caminho original estiver ocupado, será usado outro nome.")) return; await Run(async token => { var output = await actions.Restore(entry, token); if (entry.Type == "rename") RemapIndexes(entry.Destination, output); catalog.Remove(entry.Destination); catalog.Add(await library.Inspect(output, token)); History.ItemsSource = actions.History(); }); }
    private void OpenRecycleBin(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
    private Nextcloud Cloud() => new(settings.NextcloudUrl, settings.NextcloudUser, secrets.Get("nextcloud-password") ?? throw new InvalidOperationException("Configure a senha de aplicativo Nextcloud."));
    private async void CheckCloud(object sender, RoutedEventArgs e) => await Run(async token => { using var cloud = Cloud(); await cloud.List(token: token); Info("Conexão Nextcloud validada para a conta configurada."); });
    private async Task Backup(IReadOnlyList<MediaItem> items, CancellationToken token)
    { if (items.Count == 0) throw new InvalidOperationException("Não há arquivos para backup."); using var cloud = Cloud(); var snapshot = await cloud.Backup(items, settings.NextcloudFolder, ProgressText(), token); Info("Backup verificado criado:\n" + snapshot); }
    private async void BackupSelected(object sender, RoutedEventArgs e) { var items = Selection(); if (Confirm($"Enviar {items.Count} arquivos selecionados para seu Nextcloud?")) await Run(token => Backup(items, token)); }
    private async void BackupAll(object sender, RoutedEventArgs e) { var items = catalog.All(); if (Confirm($"Enviar toda a biblioteca ({items.Count} arquivos) para o Nextcloud?")) await Run(token => Backup(items, token)); }
    private async void ListBackups(object sender, RoutedEventArgs e) => await Run(async token => { using var cloud = Cloud(); CloudList.ItemsSource = await cloud.Backups(settings.NextcloudFolder, token); cloudTrash = false; if (cloud.Warnings.Count > 0) Info(string.Join('\n', cloud.Warnings)); });
    private async void RestoreBackup(object sender, RoutedEventArgs e)
    { if (CloudList.SelectedItem is not RemoteItem item || cloudTrash) { Info("Selecione um backup da lista."); return; } var dialog = new OpenFolderDialog { Title = "Pasta de destino para restaurar" }; if (dialog.ShowDialog(this) != true) return;
        await Run(async token => { using var cloud = Cloud(); await cloud.RestoreBackup(settings.NextcloudFolder + "/" + item.Name, dialog.FolderName, token); await Scan(dialog.FolderName, token); }); }
    private async void UploadFile(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog(); if (dialog.ShowDialog(this) != true) return; var remote = InputDialog.Ask(this, "Upload Nextcloud", "Caminho remoto (não sobrescreve arquivos existentes)", settings.NextcloudFolder + "/" + Path.GetFileName(dialog.FileName)); if (remote == null) return;
        await Run(async token => { using var cloud = Cloud(); await cloud.Upload(dialog.FileName, remote, token); }); }
    private async void DownloadFile(object sender, RoutedEventArgs e)
    { var remote = InputDialog.Ask(this, "Download Nextcloud", "Caminho do arquivo no Nextcloud", settings.NextcloudFolder + "/"); if (remote == null) return; var dialog = new SaveFileDialog { FileName = Path.GetFileName(remote) }; if (dialog.ShowDialog(this) != true) return;
        await Run(async token => { using var cloud = Cloud(); await cloud.Download(remote, dialog.FileName, token: token); if (MediaLibrary.Supported(dialog.FileName)) catalog.Add(await library.Inspect(dialog.FileName, token)); }); }
    private async void ListCloudTrash(object sender, RoutedEventArgs e) => await Run(async token => { using var cloud = Cloud(); CloudList.ItemsSource = await cloud.List(trash: true, token: token); cloudTrash = true; });
    private async void RestoreCloudTrash(object sender, RoutedEventArgs e)
    { if (CloudList.SelectedItem is not RemoteItem item || !cloudTrash || !Confirm("Restaurar este item na localização original do Nextcloud?")) return; await Run(async token => { using var cloud = Cloud(); await cloud.RestoreTrash(item, token); CloudList.ItemsSource = await cloud.List(trash: true, token: token); }); }
    private string SelectedProvider => (Provider.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "OpenAI";
    private void SaveSettings(object sender, RoutedEventArgs e)
    { try { settings = new(CloudUrl.Text.Trim(), CloudUser.Text.Trim(), CloudFolder.Text.Trim(), SelectedProvider, Model.Text.Trim(), ExportFolder.Text.Trim()); settings.Save(SettingsFile);
        if (CloudPassword.Password.Length > 0) { secrets.Set("nextcloud-password", CloudPassword.Password); CloudPassword.Clear(); }
        if (AiKey.Password.Length > 0) { secrets.Set(SelectedProvider == "Claude" ? "claude-key" : "openai-key", AiKey.Password); AiKey.Clear(); }
        Info("Configurações salvas. Credenciais protegidas para este usuário Windows."); } catch (Exception error) { Info(error.Message); } }
    private async void CheckTools(object sender, RoutedEventArgs e) => await Run(async token => { var versions = new List<string>(); foreach (var (tool, arg) in new[] { (tools.ExifTool, "-ver"), (tools.Ffmpeg, "-version"), (tools.Magick, "-version") }) versions.Add((await Tools.Text(tool, [arg], token)).Split('\n')[0]); Info(string.Join('\n', versions)); });
    private async void LoadModels(object sender, RoutedEventArgs e) => await Run(async token => { Model.ItemsSource = await ai.Models(SelectedProvider, token); });
    private async void ChatGptLogin(object sender, RoutedEventArgs e) => await Run(async token => { await login.SignIn(token: token); Provider.SelectedIndex = 2; Model.ItemsSource = await ai.Models("ChatGPT", token); Info("Login ChatGPT validado. Escolha um modelo e salve as configurações."); });
    private void ChatGptLogout(object sender, RoutedEventArgs e) { login.SignOut(); Info("Credenciais locais do ChatGPT removidas."); }
    private async void GeneratePlan(object sender, RoutedEventArgs e)
    { var selection = Selection(); if (!Confirm($"Enviar nomes, datas, tipo e etiquetas de {selection.Count} itens e a instrução para {settings.AiProvider}? Nenhuma imagem será enviada. A API pode cobrar pelo uso.")) return;
        await Run(async token => { plan = null; ExecutePlanButton.IsEnabled = false; planItems = [];
            foreach (var item in selection) planItems.Add(item with { Sha256 = await Files.Hash(item.Path, token) });
            plan = await ai.Plan(settings.AiProvider, settings.AiModel, Instruction.Text, planItems, token); PlanView.Text = Json.Write(plan); ExecutePlanButton.IsEnabled = true; }); }
    private async void ExecutePlan(object sender, RoutedEventArgs e)
    { if (plan == null || !Confirm("Executar este plano?\n\n" + PlanView.Text)) return; var execute = plan; ExecutePlanButton.IsEnabled = false;
        await Run(async token => {
            var runner = new PlanRunner(catalog, library, editor, actions, workspace) { CloudFactory = Cloud, CloudRoot = settings.NextcloudFolder, ExportFolder = settings.ExportDirectory, PathRenamed = RemapIndexes };
            await runner.Execute(execute, planItems, token); DuplicateList.ItemsSource = runner.LastDuplicates;
        }); }
    private void CancelTask(object sender, RoutedEventArgs e) => task?.Cancel();
    private void ClosingApp(object? sender, CancelEventArgs e)
    { if (task != null || peopleView.Busy || versionsView.Busy) { e.Cancel = true; task?.Cancel(); peopleView.Cancel(); versionsView.Cancel(); Info("Cancelando a tarefa. Feche novamente quando ela terminar."); return; } peopleView.Dispose(); versionsView.Dispose(); catalog.Dispose(); }
}
