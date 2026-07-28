using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Zimbar;

/// <summary>
/// Nota autoadesiva: uma janela minima so com o texto da nota, como o
/// Sticky Notes do Windows. Arrasta pelo topo, redimensiona pelas bordas
/// (resize nativo via WindowChrome), salva sozinha enquanto digita.
/// </summary>
public class StickyWindow : Window
{
    private static readonly Dictionary<string, StickyWindow> Abertas = new();
    private static int _cascade;

    // Cores das notas (mesmas chaves do banco) -> fundo da autoadesiva
    public static readonly (string Key, string Bg)[] Cores =
    {
        ("", "#FFF7A8"), ("uva", "#E4D5FF"), ("vinho", "#FFD0E8"),
        ("mel", "#FFE3A8"), ("mata", "#CFF5DC"), ("noite", "#D6EBFF"),
    };

    public static Brush CorFundo(string cor)
    {
        foreach (var (key, bg) in Cores)
            if (key == cor)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Cores[0].Bg));
    }

    private readonly string _id;
    private string _cor;
    private readonly TextBox _body;
    private readonly Border _root;
    private readonly StackPanel _dots;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(1100) };
    private bool _dirty;

    // ── o que veio do ZimNotes do celular ──
    // A ponte só manda titulo/corpo/cor, então estes a janela busca sozinha.
    // Enquanto não chegam, SaveNote não os inclui: gravar uma lista vazia
    // por não ter carregado ainda apagaria o checklist feito no celular.
    private JsonArray _itens = new();
    private bool _fixada;
    private bool _extrasProntos;
    private readonly StackPanel _itensBox;
    private readonly Border _itensWrap;
    private readonly Button _pinBtn;
    private DockPanel? _header;

    public static void OpenNote(string id, string titulo, string corpo, string cor)
    {
        if (Abertas.TryGetValue(id, out var w))
        {
            w.Activate();
            return;
        }
        var win = new StickyWindow(id, titulo, corpo, cor);
        Abertas[id] = win;
        win.Show();
        win.Activate();
    }

    private StickyWindow(string id, string titulo, string corpo, string cor)
    {
        _id = id;
        _cor = cor;

        Title = titulo.Length > 0 ? titulo : "nota";
        Marca.VestirNotas(this);
        Width = 268;
        Height = 268;
        MinWidth = 200;
        MinHeight = 160;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        SnapsToDevicePixels = true;
        Cantos.ArredondarQuandoAbrir(this);
        // Nao e Topmost: a nota se comporta como janela normal, pode ficar
        // atras das outras. Quem garante que ela nao some atras da barra do
        // Zimbar e o proprio Zimbar, que deixou de ser Topmost tambem.
        Topmost = false;

        // Resize nativo pelas bordas, sem moldura do Windows
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        // Cascateia as notas pra nao abrirem uma em cima da outra
        var wa = SystemParameters.WorkArea;
        int step = _cascade++ % 8;
        Left = Math.Min(wa.Right - Width - 20, wa.Left + 120 + step * 34);
        Top = Math.Min(wa.Bottom - Height - 20, wa.Top + 110 + step * 34);

        var ink = new SolidColorBrush(Color.FromRgb(0x18, 0x13, 0x20));

        // Corpo: so o texto, sem adornos
        _body = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ink,
            CaretBrush = ink,
            FontSize = 13.5,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(13, 4, 11, 6),
            Text = ComposeText(titulo, corpo)
        };
        _body.TextChanged += (_, _) =>
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        // Topo fininho: arrasto + cores + excluir + fechar
        _dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.45 };
        foreach (var (key, bg) in Cores)
        {
            var dot = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                BorderThickness = new Thickness(1.5),
                BorderBrush = key == _cor ? ink : new SolidColorBrush(Color.FromArgb(0x50, 0x18, 0x13, 0x20)),
                Tag = key
            };
            // No DOWN e marcando Handled: senao o DragMove() do cabecalho engole o clique
            // (era por isso que trocar de cor nao fazia nada).
            dot.MouseLeftButtonDown += (_, e) =>
            {
                _cor = (string)dot.Tag;
                if (_header is not null) _header.Background = CorFundo(_cor);
                MarkDots(ink);
                _dirty = true;
                _saveTimer.Stop();
                _saveTimer.Start();
                e.Handled = true;
            };
            _dots.Children.Add(dot);
        }

        Button TopBtn(string glyph, string tip)
        {
            return new Button
            {
                Content = glyph, FontSize = 11, Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x18, 0x13, 0x20))
            };
        }
        _pinBtn = TopBtn("📌", "fixar no topo da lista");
        _pinBtn.Opacity = 0.35;
        _pinBtn.Click += (_, _) =>
        {
            _fixada = !_fixada;
            _pinBtn.Opacity = _fixada ? 1 : 0.35;
            _pinBtn.ToolTip = _fixada ? "fixada — clique pra soltar" : "fixar no topo da lista";
            _dirty = true; _saveTimer.Stop(); _saveTimer.Start();
        };

        var delBtn = TopBtn("🗑", "excluir a nota");
        delBtn.Click += async (_, _) => await DeleteNote();
        var minBtn = TopBtn("—", "minimizar a nota");
        minBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        var closeBtn = TopBtn("✕", "fechar (a nota fica salva)");
        closeBtn.Click += (_, _) => Close();

        var topRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        topRight.Children.Add(_pinBtn);
        topRight.Children.Add(delBtn);
        topRight.Children.Add(minBtn);
        topRight.Children.Add(closeBtn);

        // Barra fininha só pra pegar e arrastar, como no protótipo: um rótulo
        // discreto à esquerda e os botões à direita. As cores foram pro pé.
        var titulo_ = new TextBlock
        {
            Text = "✎ nota",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0)
        };
        // O cabeçalho É a faixa de cor da nota: o corpo fica em papel branco,
        // porque texto sobre fundo colorido cansa a vista numa nota longa.
        var header = new DockPanel
        {
            Height = 34,
            Background = CorFundo(_cor),
            Cursor = Cursors.SizeAll,
            LastChildFill = true
        };
        _header = header;
        DockPanel.SetDock(topRight, Dock.Right);
        header.Children.Add(topRight);
        var dragZone = new Border { Background = Brushes.Transparent, Child = titulo_ };
        dragZone.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(dragZone);

        // Fita de cores no pé, sempre visível mas leve, com o atalho de lista
        // à direita — é o mesmo par de ferramentas que o celular tem embaixo.
        var btLista = new Button
        {
            Content = "☑ lista",
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x18, 0x13, 0x20)),
            Padding = new Thickness(6, 0, 0, 0),
            ToolTip = "adicionar item de lista"
        };
        btLista.Click += (_, _) => NovoItem(_itens.Count);

        var peLinha = new DockPanel { LastChildFill = true, Margin = new Thickness(11, 0, 11, 9) };
        DockPanel.SetDock(btLista, Dock.Right);
        peLinha.Children.Add(btLista);
        _dots.Margin = new Thickness(0);
        _dots.Opacity = 0.75;
        peLinha.Children.Add(_dots);
        var pe = new Border { Child = peLinha, Background = Brushes.Transparent };
        pe.MouseEnter += (_, _) => _dots.Opacity = 1;
        pe.MouseLeave += (_, _) => _dots.Opacity = 0.75;

        // Checklist: fica entre o texto e a fita de cores, e só aparece quando
        // a nota tem itens. Quem cria item é o celular; aqui você marca e
        // desmarca, que é o que se faz com uma lista no computador.
        _itensBox = new StackPanel();
        _itensWrap = new Border
        {
            Child = _itensBox,
            Background = Paleta.Cartao,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(11, 2, 11, 9),
            Effect = Paleta.Sombra(14, 0.10),
            Visibility = Visibility.Collapsed
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(pe, Dock.Bottom);
        DockPanel.SetDock(_itensWrap, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(pe);
        layout.Children.Add(_itensWrap);
        layout.Children.Add(_body);

        // Sem moldura grossa: a curva do Windows 11 já dá o recorte
        _root = new Border
        {
            Background = Paleta.Cartao,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,          // a faixa do topo respeita a curva
            Child = layout
        };
        Content = _root;

        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveNote(); };
        Closed += async (_, _) =>
        {
            Abertas.Remove(_id);
            _saveTimer.Stop();
            if (_dirty) await SaveNote();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            { _saveTimer.Stop(); _ = SaveNote(); e.Handled = true; }
        };

        Loaded += (_, _) => { _body.Focus(); _body.CaretIndex = _body.Text.Length; };
        _ = CarregarExtras();
    }

    /// <summary>Busca checklist e alfinete direto do banco, sem passar pela ponte.</summary>
    private async Task CarregarExtras()
    {
        try
        {
            var linhas = await Supa.Select(
                "notas?id=eq." + Uri.EscapeDataString(_id) + "&select=itens,fixada");
            if (linhas.Count > 0 && linhas[0] is JsonObject o)
            {
                _itens = o["itens"] as JsonArray ?? new JsonArray();
                _fixada = o["fixada"]?.GetValue<bool>() ?? false;
            }
        }
        catch (Exception ex) { Log.Escrever("nota — extras: " + ex.Message); }

        _extrasProntos = true;
        _pinBtn.Opacity = _fixada ? 1 : 0.35;
        if (_fixada) _pinBtn.ToolTip = "fixada — clique pra soltar";
        DesenharItens();
    }

    /// <summary>
    /// O checklist no mesmo desenho do ZimNotes de celular: cartão branco,
    /// caixinha arredondada que fica terracota quando marcada, texto riscado
    /// quando feito, e uma linha "novo item" no pé. Enter cria o próximo,
    /// Backspace no item vazio remove — igual lá.
    /// </summary>
    private void DesenharItens()
    {
        _itensBox.Children.Clear();
        if (_itens.Count == 0) { _itensWrap.Visibility = Visibility.Collapsed; return; }
        _itensWrap.Visibility = Visibility.Visible;

        for (int i = 0; i < _itens.Count; i++)
        {
            if (_itens[i] is not JsonObject item) continue;
            _itensBox.Children.Add(LinhaDoItem(item, i));
        }
        _itensBox.Children.Add(LinhaDeAdicionar());
    }

    private UIElement LinhaDoItem(JsonObject item, int indice)
    {
        bool feito = item["f"]?.GetValue<bool>() ?? false;

        var visto = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,7.5 L 6.2,10.7 L 12,4.4"),
            Stroke = Brushes.White,
            StrokeThickness = 2.2,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            Visibility = feito ? Visibility.Visible : Visibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var caixa = new Border
        {
            Width = 19,
            Height = 19,
            CornerRadius = new CornerRadius(6),
            Background = feito ? Paleta.Acento : Brushes.Transparent,
            BorderBrush = feito ? Paleta.Acento : new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0x26, 0x2E)),
            BorderThickness = new Thickness(1.8),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 10, 0),
            Child = visto
        };
        caixa.MouseLeftButtonDown += (_, e) => { MarcarItem(indice, !feito); e.Handled = true; };

        var texto = new TextBox
        {
            Text = item["t"]?.GetValue<string>() ?? "",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Paleta.TintaNota,
            CaretBrush = Paleta.TintaNota,
            FontSize = 12.5,
            FontFamily = Paleta.Fonte,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            Opacity = feito ? 0.5 : 1,
            TextDecorations = feito ? TextDecorations.Strikethrough : null,
            Padding = new Thickness(0, 1, 0, 1)
        };
        texto.TextChanged += (_, _) =>
        {
            item["t"] = texto.Text;
            _dirty = true; _saveTimer.Stop(); _saveTimer.Start();
        };
        texto.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { NovoItem(indice + 1); e.Handled = true; }
            else if (e.Key == Key.Back && texto.Text.Length == 0 && _itens.Count > 1)
            { RemoverItem(indice, true); e.Handled = true; }
        };

        var lixo = new Button
        {
            Content = "✕",
            FontSize = 10,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Paleta.Fraquinha,
            Padding = new Thickness(5, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0
        };
        lixo.Click += (_, _) => RemoverItem(indice, false);

        var linha = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
        DockPanel.SetDock(caixa, Dock.Left);
        DockPanel.SetDock(lixo, Dock.Right);
        linha.Children.Add(caixa);
        linha.Children.Add(lixo);
        linha.Children.Add(texto);
        linha.MouseEnter += (_, _) => lixo.Opacity = 1;
        linha.MouseLeave += (_, _) => lixo.Opacity = 0;
        return linha;
    }

    private UIElement LinhaDeAdicionar()
    {
        var b = new Button
        {
            Content = "+  novo item",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Paleta.Fraca,
            Padding = new Thickness(1, 5, 0, 2)
        };
        b.Click += (_, _) => NovoItem(_itens.Count);
        return b;
    }

    private void NovoItem(int posicao)
    {
        var novo = new JsonObject { ["id"] = Supa.NewId(), ["t"] = "", ["f"] = false };
        posicao = Math.Clamp(posicao, 0, _itens.Count);
        _itens.Insert(posicao, novo);
        DesenharItens();
        FocarItem(posicao);
        _dirty = true; _saveTimer.Stop(); _saveTimer.Start();
    }

    private void RemoverItem(int i, bool focarAnterior)
    {
        if (i < 0 || i >= _itens.Count) return;
        _itens.RemoveAt(i);
        DesenharItens();
        if (focarAnterior) FocarItem(Math.Max(0, i - 1));
        _dirty = true; _saveTimer.Stop(); _saveTimer.Start();
    }

    private void FocarItem(int i)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (i < 0 || i >= _itensBox.Children.Count) return;
            if (_itensBox.Children[i] is DockPanel d)
                foreach (var filho in d.Children)
                    if (filho is TextBox tb) { tb.Focus(); tb.CaretIndex = tb.Text.Length; return; }
        }), DispatcherPriority.Input);
    }

    private void MarcarItem(int i, bool feito)
    {
        if (i < 0 || i >= _itens.Count) return;
        if (_itens[i] is not JsonObject item) return;
        item["f"] = feito;
        DesenharItens();
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void MarkDots(Brush ink)
    {
        foreach (Border dot in _dots.Children)
            dot.BorderBrush = (string)dot.Tag == _cor
                ? ink
                : new SolidColorBrush(Color.FromArgb(0x50, 0x18, 0x13, 0x20));
    }

    /// <summary>Titulo (1a linha) + corpo viram um texto so; tira o titulo duplicado de notas antigas.</summary>
    private static string ComposeText(string titulo, string corpo)
    {
        if (corpo.StartsWith(titulo, StringComparison.Ordinal) && titulo.Length > 0)
            return corpo;
        if (titulo.Length == 0) return corpo;
        return corpo.Length == 0 ? titulo : titulo + Environment.NewLine + corpo;
    }

    private async Task SaveNote()
    {
        string texto = _body.Text;
        string titulo = texto.Split('\n', 2)[0].Trim().TrimEnd('\r');
        if (titulo.Length > 80) titulo = titulo[..80];
        try
        {
            var patch = new JsonObject
            {
                ["titulo"] = titulo,
                ["corpo"] = texto,
                ["cor"] = _cor,
                ["data_nota"] = DateTime.Now.ToString("yyyy-MM-dd")
            };
            // Só depois de carregar: mandar antes gravaria lista vazia por cima
            // do checklist que veio do celular.
            if (_extrasProntos)
            {
                patch["fixada"] = _fixada;
                patch["itens"] = _itens.DeepClone();
            }
            await Supa.Update("notas", "id=eq." + Uri.EscapeDataString(_id), patch);
            _dirty = false;
            Title = titulo.Length > 0 ? titulo : "nota";
            NotesWindow.RefreshIfOpen();
        }
        catch { /* offline: tenta de novo na proxima digitada/fechada */ }
    }

    private async Task DeleteNote()
    {
        var r = MessageBox.Show(this, "Excluir esta nota? Isso nao volta.",
            "ZimNotes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        try
        {
            await Supa.Delete("notas", "id=eq." + Uri.EscapeDataString(_id));
            _dirty = false;
            NotesWindow.RefreshIfOpen();
            Close();
        }
        catch { }
    }
}
