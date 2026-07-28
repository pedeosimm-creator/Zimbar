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
            // fixadas primeiro, como no celular
            var rows = await Supa.Select(
                "notas?select=id,titulo,corpo,data_nota,cor,created_at,fixada,itens" +
                "&order=fixada.desc,created_at.desc&limit=160");
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
            NotesListPanel.Children.Add(EmptyText("Ainda não tem nota nenhuma.\nToca em + nota pra escrever a primeira."));
            return;
        }
        if (filtered.Count == 0)
        {
            NotesListPanel.Children.Add(EmptyText("Nada com essa palavra.\nTenta outra."));
            return;
        }

        foreach (var n in filtered)
            NotesListPanel.Children.Add(NoteCard(n));
    }

    /// <summary>Cartão na cor da nota, no desenho do ZimNotes de celular.</summary>
    private Border NoteCard(JsonObject n)
    {
        string titulo = TitleOf(n);
        string corpo = BodyPreview(n);
        string cor = n["cor"]?.GetValue<string>() ?? "";
        bool fixada = n["fixada"]?.GetValue<bool>() ?? false;
        var itens = n["itens"] as JsonArray ?? new JsonArray();

        var sp = new StackPanel();

        // título com o alfinete à direita, se estiver fixada
        var linhaTitulo = new DockPanel { LastChildFill = true };
        if (fixada)
        {
            var pin = new TextBlock
            {
                Text = "📌",
                FontSize = 11,
                Opacity = 0.55,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(pin, Dock.Right);
            linhaTitulo.Children.Add(pin);
        }
        linhaTitulo.Children.Add(new TextBlock
        {
            Text = titulo.Length == 0 ? "Sem título" : titulo,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Paleta.TintaNota,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(linhaTitulo);

        if (corpo.Length > 0)
            sp.Children.Add(new TextBlock
            {
                Text = corpo.Length > 120 ? corpo[..120] + "…" : corpo,
                FontSize = 12,
                Foreground = Paleta.TintaNota,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // 2 linhas exatas: com MaxHeight solto a 3a linha ficava cortada no meio
                LineHeight = 17,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = 34,
                Margin = new Thickness(0, 5, 0, 0)
            });

        // prévia do checklist: os que faltam primeiro, que é o que interessa
        if (itens.Count > 0)
        {
            var abertos = itens.OfType<JsonObject>().Where(i => !(i["f"]?.GetValue<bool>() ?? false)).ToList();
            var mostra = (abertos.Count > 0 ? abertos : itens.OfType<JsonObject>().ToList()).Take(2);
            foreach (var it in mostra)
            {
                bool feito = it["f"]?.GetValue<bool>() ?? false;
                sp.Children.Add(new TextBlock
                {
                    Text = (feito ? "☑  " : "☐  ") + (it["t"]?.GetValue<string>() ?? ""),
                    FontSize = 11.5,
                    Foreground = Paleta.TintaNota,
                    Opacity = feito ? 0.45 : 0.78,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            int restantes = itens.Count - mostra.Count();
            if (restantes > 0)
                sp.Children.Add(new TextBlock
                {
                    Text = $"+{restantes}",
                    FontSize = 11,
                    Foreground = Paleta.TintaNota,
                    Opacity = 0.45,
                    Margin = new Thickness(0, 3, 0, 0)
                });
        }

        sp.Children.Add(new TextBlock
        {
            Text = Quando(n),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Paleta.TintaNota,
            Opacity = 0.5,
            Margin = new Thickness(0, 9, 0, 0)
        });

        var card = new Border
        {
            Background = StickyWindow.CorFundo(cor),
            CornerRadius = new CornerRadius(Paleta.Raio),
            Padding = new Thickness(15, 13, 15, 12),
            Margin = new Thickness(0, 0, 3, 10),
            Cursor = Cursors.Hand,
            ToolTip = "clica pra abrir a autoadesiva",
            Effect = Paleta.Sombra(16, 0.10),
            Child = sp
        };
        card.MouseEnter += (_, _) => card.Effect = Paleta.Sombra(22, 0.17);
        card.MouseLeave += (_, _) => card.Effect = Paleta.Sombra(16, 0.10);
        card.MouseLeftButtonUp += (_, _) => OpenSticky(n);
        return card;
    }

    /// <summary>"hoje", "ontem", "há 3 dias" — a mesma conversa do celular.</summary>
    private static string Quando(JsonObject n)
    {
        var bruto = n["created_at"]?.GetValue<string>();
        if (!DateTime.TryParse(bruto, out var d)) return n["data_nota"]?.GetValue<string>() ?? "";
        int dias = (DateTime.Now.Date - d.ToLocalTime().Date).Days;
        return dias switch
        {
            <= 0 => "hoje",
            1 => "ontem",
            < 7 => $"há {dias} dias",
            < 14 => "semana passada",
            < 60 => $"há {dias / 7} semanas",
            _ => d.ToLocalTime().ToString("d 'de' MMM")
        };
    }

    private static void OpenSticky(JsonObject n)
        => StickyWindow.OpenNote(IdOf(n), TitleOf(n), n["corpo"]?.GetValue<string>() ?? "", n["cor"]?.GetValue<string>() ?? "");

    private TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = Paleta.Fraca,
        FontSize = 12.5,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(10, 34, 10, 0)
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
