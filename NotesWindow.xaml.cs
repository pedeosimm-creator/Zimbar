using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Zimbar;

/// <summary>
/// ZimNotes: janela independente de notas, gravada na tabela `notas`.
/// A interface usa lista pesquisavel + editor permanente para leitura e escrita rapida.
/// </summary>
public partial class NotesWindow : Window
{
    private static NotesWindow? _instance;

    private static readonly (string Key, string Hex)[] Cores =
    {
        ("", "#2B2150"), ("uva", "#40204A"), ("vinho", "#4A2230"),
        ("mel", "#4A3A1E"), ("mata", "#1E4A32"), ("noite", "#1E304A"),
    };

    private readonly List<JsonObject> _notas = new();
    private string? _editId;
    private string? _selectedId;
    private string _editCor = "";
    private bool _suppressEditorDirty;

    public static void Open()
    {
        _instance ??= new NotesWindow();
        _instance.Show();
        _instance.Activate();
        _ = _instance.LoadNotas();
    }

    private NotesWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _instance = null;
        PreviewKeyDown += Window_PreviewKeyDown;
        BuildColorRow();
        OpenEditor(null, focusTitle: false);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.S) { EditorSave_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.N) { New_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.B) { Bold_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }

        if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
                SearchBox.Text = "";
            else
                Close();
            e.Handled = true;
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button or TextBox) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseWin_Click(object sender, RoutedEventArgs e) => Close();

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    // -- Lista -------------------------------------------------------

    private async Task LoadNotas()
    {
        try
        {
            var rows = await Supa.Select("notas?select=id,titulo,corpo,data_nota,cor&order=created_at.desc&limit=160");
            _notas.Clear();
            foreach (var node in rows)
                if (node is JsonObject n)
                    _notas.Add(n);

            if (_selectedId is not null && _notas.All(n => IdOf(n) != _selectedId))
                _selectedId = null;
            _selectedId ??= _notas.FirstOrDefault() is JsonObject first ? IdOf(first) : null;

            RenderNotesList();
            var selected = _notas.FirstOrDefault(n => IdOf(n) == _selectedId);
            OpenEditor(selected, focusTitle: false);
            StatusText.Text = "";
        }
        catch
        {
            _notas.Clear();
            NotesListPanel.Children.Clear();
            NotesListPanel.Children.Add(EmptyText("sem conexao com o banco agora"));
            CountText.Text = "";
            StatusText.Text = "offline";
            OpenEditor(null, focusTitle: false);
        }
    }

    private void RenderNotesList()
    {
        NotesListPanel.Children.Clear();
        string q = SearchBox.Text.Trim();
        var filtered = _notas
            .Where(n => q.Length == 0 || Haystack(n).Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CountText.Text = q.Length == 0
            ? $"{_notas.Count} nota{(_notas.Count == 1 ? "" : "s")}"
            : $"{filtered.Count}/{_notas.Count} nota{(_notas.Count == 1 ? "" : "s")}";

        if (_notas.Count == 0)
        {
            NotesListPanel.Children.Add(EmptyText("nenhuma nota ainda"));
            return;
        }
        if (filtered.Count == 0)
        {
            NotesListPanel.Children.Add(EmptyText("nada encontrado"));
            return;
        }

        foreach (var n in filtered)
            NotesListPanel.Children.Add(NoteRow(n));
    }

    private Border NoteRow(JsonObject n)
    {
        string id = IdOf(n);
        string titulo = TitleOf(n);
        string corpo = BodyPreview(n);
        string data = n["data_nota"]?.GetValue<string>() ?? "";
        bool selected = id == _selectedId;

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = titulo.Length == 0 ? "sem titulo" : titulo,
            FontSize = 13.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (corpo.Length > 0)
            sp.Children.Add(new TextBlock
            {
                Text = corpo.Length > 108 ? corpo[..108] + "..." : corpo,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextDim"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 38,
                Margin = new Thickness(0, 5, 0, 0)
            });
        sp.Children.Add(new TextBlock
        {
            Text = data,
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextDone"),
            Margin = new Thickness(0, 7, 0, 0)
        });

        var card = new Border
        {
            Background = selected
                ? new SolidColorBrush(Color.FromArgb(0x26, 0x7D, 0xFF, 0xD7))
                : CorBrush(n["cor"]?.GetValue<string>() ?? ""),
            BorderBrush = selected
                ? (Brush)FindResource("Accent")
                : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 10, 12, 9),
            Margin = new Thickness(0, 0, 0, 9),
            Cursor = Cursors.Hand,
            Child = sp
        };
        card.MouseLeftButtonUp += (_, _) => SelectNote(n);
        return card;
    }

    private TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("TextDim"),
        FontSize = 12.5,
        Margin = new Thickness(4, 4, 0, 0)
    };

    private void SelectNote(JsonObject n)
    {
        _selectedId = IdOf(n);
        OpenEditor(n);
        RenderNotesList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderNotesList();

    private static string IdOf(JsonObject n) => n["id"]?.GetValue<string>() ?? "";
    private static string TitleOf(JsonObject n) => n["titulo"]?.GetValue<string>() ?? "";
    private static string Haystack(JsonObject n) => $"{TitleOf(n)}\n{n["corpo"]?.GetValue<string>() ?? ""}";

    private static string BodyPreview(JsonObject n)
    {
        string titulo = TitleOf(n);
        string corpo = n["corpo"]?.GetValue<string>() ?? "";
        return corpo.StartsWith(titulo, StringComparison.Ordinal) && corpo.Length > titulo.Length
            ? corpo[titulo.Length..].TrimStart('\n', '\r', ' ')
            : corpo;
    }

    private static Brush CorBrush(string cor)
    {
        foreach (var (key, hex) in Cores)
            if (key == cor && key != "")
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        return (Brush)Application.Current.Resources["ChipBg"];
    }

    // -- Editor ------------------------------------------------------

    private void BuildColorRow()
    {
        ColorRow.Children.Clear();
        foreach (var (key, hex) in Cores)
        {
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Tag = key
            };
            dot.MouseLeftButtonUp += (_, _) => { _editCor = key; MarkColor(); StatusText.Text = "alteracoes nao salvas"; };
            ColorRow.Children.Add(dot);
        }
    }

    private void MarkColor()
    {
        foreach (Border dot in ColorRow.Children)
            dot.BorderBrush = (string)dot.Tag == _editCor
                ? (Brush)FindResource("Accent")
                : Brushes.Transparent;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selectedId = null;
        OpenEditor(null);
        RenderNotesList();
        StatusText.Text = "nova nota";
    }

    private void OpenEditor(JsonObject? n, bool focusTitle = true)
    {
        _suppressEditorDirty = true;
        _editId = n is null ? null : IdOf(n);
        _editCor = n?["cor"]?.GetValue<string>() ?? "";
        EdTitle.Text = n is null ? "" : TitleOf(n);
        EdBody.Text = n is null ? "" : BodyPreview(n);
        EdDelete.Visibility = _editId is null ? Visibility.Collapsed : Visibility.Visible;
        MarkColor();
        StatusText.Text = "";
        _suppressEditorDirty = false;

        if (focusTitle)
        {
            EdTitle.Focus();
            EdTitle.CaretIndex = EdTitle.Text.Length;
        }
    }

    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressEditorDirty)
            StatusText.Text = "alteracoes nao salvas";
    }

    private async void EditorSave_Click(object sender, RoutedEventArgs e)
    {
        string titulo = EdTitle.Text.Trim();
        string corpo = EdBody.Text.Trim();
        if (titulo.Length == 0 && corpo.Length == 0)
        {
            StatusText.Text = "nota vazia";
            return;
        }
        if (titulo.Length == 0)
        {
            titulo = corpo.Split('\n', 2)[0].Trim();
            if (titulo.Length > 80) titulo = titulo[..80];
        }

        try
        {
            StatusText.Text = "salvando...";
            if (_editId is null)
            {
                string id = Supa.NewId();
                await Supa.Insert("notas", new JsonObject
                {
                    ["id"] = id,
                    ["titulo"] = titulo,
                    ["corpo"] = corpo,
                    ["data_nota"] = DateTime.Now.ToString("yyyy-MM-dd"),
                    ["cor"] = _editCor
                });
                _selectedId = id;
            }
            else
            {
                await Supa.Update("notas", "id=eq." + Uri.EscapeDataString(_editId), new JsonObject
                {
                    ["titulo"] = titulo,
                    ["corpo"] = corpo,
                    ["cor"] = _editCor
                });
                _selectedId = _editId;
            }

            await LoadNotas();
            StatusText.Text = "salvo";
        }
        catch
        {
            StatusText.Text = "erro ao salvar";
        }
    }

    private async void EditorDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editId is null) return;
        try
        {
            StatusText.Text = "excluindo...";
            await Supa.Delete("notas", "id=eq." + Uri.EscapeDataString(_editId));
            _selectedId = null;
            await LoadNotas();
            StatusText.Text = "excluido";
        }
        catch
        {
            StatusText.Text = "erro ao excluir";
        }
    }

    // -- Formatacao em texto simples --------------------------------

    private void Bold_Click(object sender, RoutedEventArgs e) => WrapSelection("**", "**");
    private void Heading_Click(object sender, RoutedEventArgs e) => PrefixCurrentOrSelectedLines("# ");
    private void Bullet_Click(object sender, RoutedEventArgs e) => PrefixCurrentOrSelectedLines("- ");
    private void Check_Click(object sender, RoutedEventArgs e) => PrefixCurrentOrSelectedLines("- [ ] ");

    private void Bigger_Click(object sender, RoutedEventArgs e)
    {
        EdBody.FontSize = Math.Min(22, EdBody.FontSize + 1);
        EdBody.Focus();
    }

    private void Smaller_Click(object sender, RoutedEventArgs e)
    {
        EdBody.FontSize = Math.Max(12, EdBody.FontSize - 1);
        EdBody.Focus();
    }

    private void WrapSelection(string before, string after)
    {
        int start = EdBody.SelectionStart;
        int length = EdBody.SelectionLength;
        string selected = EdBody.SelectedText;
        EdBody.SelectedText = before + selected + after;
        EdBody.Focus();
        EdBody.SelectionStart = start + before.Length;
        EdBody.SelectionLength = length;
    }

    private void PrefixCurrentOrSelectedLines(string prefix)
    {
        if (EdBody.SelectionLength > 0)
        {
            int start = EdBody.SelectionStart;
            string selected = EdBody.SelectedText.Replace("\r\n", "\n");
            string replaced = string.Join(Environment.NewLine, selected.Split('\n').Select(line =>
                line.StartsWith(prefix, StringComparison.Ordinal) || line.Length == 0 ? line : prefix + line));
            EdBody.SelectedText = replaced;
            EdBody.Focus();
            EdBody.SelectionStart = start;
            EdBody.SelectionLength = replaced.Length;
            return;
        }

        int oldCaret = EdBody.CaretIndex;
        int line = EdBody.GetLineIndexFromCharacterIndex(oldCaret);
        int lineStart = EdBody.GetCharacterIndexFromLineIndex(line);
        string lineText = EdBody.GetLineText(line);
        if (!lineText.StartsWith(prefix, StringComparison.Ordinal))
        {
            EdBody.Text = EdBody.Text.Insert(lineStart, prefix);
            EdBody.CaretIndex = oldCaret + prefix.Length;
        }
        EdBody.Focus();
    }
}
