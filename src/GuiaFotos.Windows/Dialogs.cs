using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GuiaFotos.Windows;

public sealed class InputDialog : Window
{
    private readonly TextBox input;
    private InputDialog(string title, string label, string initial)
    {
        Title = title; Width = 600; Height = 210; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(input);
        var button = new Button { Content = "Confirmar", IsDefault = true }; button.Click += (_, _) => DialogResult = true; panel.Children.Add(button); Content = panel;
    }
    public static string? Ask(Window owner, string title, string label, string initial = "")
    { var dialog = new InputDialog(title, label, initial) { Owner = owner }; return dialog.ShowDialog() == true ? dialog.input.Text : null; }
}
public sealed class MetadataRow(string key, string value)
{ public string Tag { get; } = key; public string Original { get; } = value; public string Value { get; set; } = value; }
public sealed class MetadataDialog : Window
{
    private readonly List<MetadataRow> rows;
    public Dictionary<string, string> Updates => rows.Where(x => x.Value != x.Original).ToDictionary(x => x.Tag, x => x.Value);
    public MetadataDialog(Dictionary<string, JsonElement> metadata)
    {
        Title = "Metadados completos — editar em cópia"; Width = 1000; Height = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        rows = metadata.OrderBy(x => x.Key).Select(x => new MetadataRow(x.Key, x.Value.ToString())).ToList();
        var panel = new DockPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = "Editor avançado: alteração manual dos campos graváveis do ExifTool, sempre em cópia. Campos estruturais de arquivo são bloqueados; campos técnicos podem ser somente leitura. A coluna original permanece para comparação.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal }; var save = new Button { Content = "Salvar alterações em cópia" }; save.Click += (_, _) => { if (Updates.Count == 0) { MessageBox.Show(this, "Altere ao menos um valor."); return; } DialogResult = true; }; bottom.Children.Add(save);
        var add = new Button { Content = "Adicionar tag…" }; add.Click += (_, _) => { var tag = InputDialog.Ask(this, "Tag ExifTool", "Nome da tag (ex.: XMP:Description, EXIF:DateTimeOriginal)"); if (!string.IsNullOrWhiteSpace(tag) && rows.All(x => x.Tag != tag)) { rows.Add(new(tag, "")); grid.Items.Refresh(); } }; bottom.Children.Add(add); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Grupo / campo", Binding = new Binding("Tag"), IsReadOnly = true, Width = new DataGridLength(230) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Valor editável", Binding = new Binding("Value") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Original", Binding = new Binding("Original"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        panel.Children.Add(grid); Content = panel;
    }
    private readonly DataGrid grid = new();
}
