using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
/// ZimNotes solto: o MESMO painel "Minhas notas" que fica embutido no Zimbar,
/// só que numa janela livre (é o que o botão "destacar" abre). Clicar numa
/// nota abre a autoadesiva (StickyWindow) com o texto dela.
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
        Cantos.ArredondarQuandoAbrir(this);
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
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
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
            // fixadas primeiro, como no celular; pasta vem junto pra mostrar a coleção
            var rows = await Supa.Select(
                "notas?select=id,titulo,corpo,data_nota,cor,created_at,fixada,itens,pasta" +
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
            NotesListPanel.Children.Add(EmptyText("sem conexão com o banco agora"));
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
        CountText.Text = $"{_notas.Count} nota{(_notas.Count == 1 ? "" : "s")}";

        if (_notas.Count == 0)
        {
            NotesListPanel.Children.Add(EmptyText("Ainda não tem nota nenhuma.\nToca em ＋ pra escrever a primeira."));
            return;
        }

        foreach (var n in _notas)
            NotesListPanel.Children.Add(NoteCard(n));
    }

    /// <summary>Cartão simples: faixa da cor no topo, título, prévia e a coleção.</summary>
    private Border NoteCard(JsonObject n)
    {
        string titulo = TitleOf(n);
        string corpo = BodyPreview(n);
        string cor = n["cor"]?.GetValue<string>() ?? "";
        string pasta = n["pasta"]?.GetValue<string>() ?? "";
        bool fixada = n["fixada"]?.GetValue<bool>() ?? false;

        var sp = new StackPanel();

        var linhaTitulo = new DockPanel { LastChildFill = true };
        if (fixada)
        {
            var pin = new TextBlock
            {
                Text = "📌",
                FontSize = 10.5,
                Opacity = 0.55,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(pin, Dock.Right);
            linhaTitulo.Children.Add(pin);
        }
        linhaTitulo.Children.Add(new TextBlock
        {
            Text = titulo.Length == 0 ? "sem título" : titulo,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Paleta.Tinta2,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(linhaTitulo);

        if (corpo.Length > 0)
            sp.Children.Add(new TextBlock
            {
                Text = corpo.Length > 90 ? corpo[..90] + "…" : corpo,
                FontSize = 11.5,
                Foreground = Paleta.Fraca,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            });

        // a coleção (pasta), quando tem — é a "categoria" que o Pedro quer ver
        if (pasta.Length > 0)
        {
            var chip = new Border
            {
                Background = Paleta.FundoBaixo,
                CornerRadius = new CornerRadius(100),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = pasta.ToUpperInvariant(),
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Paleta.Fraca
                }
            };
            sp.Children.Add(chip);
        }

        // A cor da nota é uma faixa no alto; o papel do cartão fica branco.
        var faixa = new Border
        {
            Height = 5,
            Background = StickyWindow.CorFundo(cor),
            VerticalAlignment = VerticalAlignment.Top
        };
        var miolo = new Border { Padding = new Thickness(14, 15, 14, 12), Child = sp };
        var pilha = new Grid();
        pilha.Children.Add(miolo);
        pilha.Children.Add(faixa);

        var card = new Border
        {
            Background = Paleta.Cartao,
            CornerRadius = new CornerRadius(Paleta.Raio),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 3, 9),
            Cursor = Cursors.Hand,
            ToolTip = "clica pra abrir a autoadesiva",
            Effect = Paleta.Sombra(15, 0.09),
            Child = pilha
        };
        card.MouseEnter += (_, _) => card.Effect = Paleta.Sombra(20, 0.15);
        card.MouseLeave += (_, _) => card.Effect = Paleta.Sombra(15, 0.09);
        card.MouseLeftButtonUp += (_, _) => OpenSticky(n);
        return card;
    }

    private static void OpenSticky(JsonObject n)
        => StickyWindow.OpenNote(IdOf(n), TitleOf(n), n["corpo"]?.GetValue<string>() ?? "", n["cor"]?.GetValue<string>() ?? "");

    private TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = Paleta.Fraca,
        FontSize = 12,
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

    private static string IdOf(JsonObject n) => n["id"]?.GetValue<string>() ?? "";
    private static string TitleOf(JsonObject n) => n["titulo"]?.GetValue<string>() ?? "";

    // tira as marcas de formatação (negrito/sublinhado do celular) da prévia
    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled);

    private static string BodyPreview(JsonObject n)
    {
        string titulo = TitleOf(n);
        string corpo = n["corpo"]?.GetValue<string>() ?? "";
        if (corpo.StartsWith(titulo, StringComparison.Ordinal) && corpo.Length > titulo.Length)
            corpo = corpo[titulo.Length..];
        corpo = TagRegex.Replace(corpo, "");
        foreach (var linha in corpo.Split('\n'))
        {
            var t = linha.Trim();
            if (t.Length > 0) return t;
        }
        return "";
    }
}
