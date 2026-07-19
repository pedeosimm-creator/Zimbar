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
using System.Windows.Threading;

namespace Zimbar;

/// <summary>
/// ZimNotes: a BIBLIOTECA de notas (tabela `notas`). Clicar numa nota abre
/// uma janela autoadesiva (StickyWindow) so com o texto dela, como o
/// Sticky Notes do Windows.
/// </summary>
public partial class NotesWindow : Window
{
    private static NotesWindow? _instance;

    private readonly List<JsonObject> _notas = new();
    private bool _loadingNotas;
    private DateTime _lastSync = DateTime.MinValue;
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    public static void Open()
    {
        _instance ??= new NotesWindow();
        _instance.Show();
        _instance.Activate();
        _ = _instance.LoadNotas();
    }

    /// <summary>As autoadesivas chamam isso ao salvar/excluir pra lista refletir na hora.</summary>
    public static void RefreshIfOpen()
    {
        if (_instance is not null) _ = _instance.LoadNotas();
    }

    private NotesWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { _syncTimer.Stop(); _instance = null; };
        Activated += (_, _) => _ = LoadNotasIfSafe();
        _syncTimer.Tick += (_, _) => _ = LoadNotasIfSafe();
        _syncTimer.Start();
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.N) { New_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
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
        if (_loadingNotas) return;
        _loadingNotas = true;
        try
        {
            var rows = await Supa.Select("notas?select=id,titulo,corpo,data_nota,cor&order=created_at.desc&limit=160");
            _notas.Clear();
            foreach (var node in rows)
                if (node is JsonObject n)
                    _notas.Add(n);

            RenderNotesList();
            StatusText.Text = "";
            _lastSync = DateTime.Now;
        }
        catch
        {
            _notas.Clear();
            NotesListPanel.Children.Clear();
            NotesListPanel.Children.Add(EmptyText("sem conexao com o banco agora"));
            CountText.Text = "";
            StatusText.Text = "offline";
        }
        finally
        {
            _loadingNotas = false;
        }
    }

    private async Task LoadNotasIfSafe()
    {
        if (_loadingNotas) return;
        if ((DateTime.Now - _lastSync).TotalSeconds < 4) return;
        await LoadNotas();
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
            NotesListPanel.Children.Add(EmptyText("nenhuma nota ainda - cria com + nota"));
            return;
        }
        if (filtered.Count == 0)
        {
            NotesListPanel.Children.Add(EmptyText("nada encontrado"));
            return;
        }

        foreach (var n in filtered)
            NotesListPanel.Children.Add(NoteCard(n));
    }

    /// <summary>Bloco neobrutal na cor da nota; clique abre a autoadesiva.</summary>
    private Border NoteCard(JsonObject n)
    {
        string titulo = TitleOf(n);
        string corpo = BodyPreview(n);
        string data = n["data_nota"]?.GetValue<string>() ?? "";
        string cor = n["cor"]?.GetValue<string>() ?? "";

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = titulo.Length == 0 ? "sem titulo" : titulo,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextMain"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (corpo.Length > 0)
            sp.Children.Add(new TextBlock
            {
                Text = corpo.Length > 110 ? corpo[..110] + "..." : corpo,
                FontSize = 11.8,
                Foreground = (Brush)FindResource("TextDim"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 36,
                Margin = new Thickness(0, 4, 0, 0)
            });
        sp.Children.Add(new TextBlock
        {
            Text = data,
            FontSize = 9.5,
            FontFamily = (FontFamily)FindResource("Mono"),
            Foreground = (Brush)FindResource("TextDone"),
            Margin = new Thickness(0, 6, 0, 0)
        });

        var ink = (Brush)FindResource("Ink");
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 0, ShadowDepth = 3, Direction = 315, Opacity = 1, Color = Color.FromRgb(0x18, 0x13, 0x20) };
        shadow.Freeze();
        var card = new Border
        {
            Background = StickyWindow.CorFundo(cor),
            BorderBrush = ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 4, 9),
            Cursor = Cursors.Hand,
            ToolTip = "clica pra abrir a autoadesiva",
            Effect = shadow,
            Child = sp
        };
        card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("AccentSoft");
        card.MouseLeave += (_, _) => card.BorderBrush = ink;
        card.MouseLeftButtonUp += (_, _) => OpenSticky(n);
        return card;
    }

    private static void OpenSticky(JsonObject n)
        => StickyWindow.OpenNote(IdOf(n), TitleOf(n), n["corpo"]?.GetValue<string>() ?? "", n["cor"]?.GetValue<string>() ?? "");

    private TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("TextDim"),
        FontSize = 12.5,
        Margin = new Thickness(4, 4, 0, 0)
    };

    /// <summary>Cria a nota no banco na hora e ja abre a autoadesiva dela.</summary>
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "criando...";
            string id = Supa.NewId();
            await Supa.Insert("notas", new JsonObject
            {
                ["id"] = id,
                ["titulo"] = "",
                ["corpo"] = "",
                ["data_nota"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["cor"] = ""
            });
            StatusText.Text = "";
            StickyWindow.OpenNote(id, "", "", "");
            await LoadNotas();
        }
        catch
        {
            StatusText.Text = "erro ao criar";
        }
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
}
