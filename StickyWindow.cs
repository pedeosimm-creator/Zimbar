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
        Width = 320;
        Height = 320;
        MinWidth = 200;
        MinHeight = 160;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        SnapsToDevicePixels = true;
        // A barra do Zimbar e Topmost; sem isso a nota some atras dela ao trocar de aba.
        Topmost = true;

        // Resize nativo pelas bordas, sem moldura do Windows
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
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
            FontSize = 14,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 8, 10, 10),
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
                _root!.Background = CorFundo(_cor);
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
        var delBtn = TopBtn("🗑", "excluir a nota");
        delBtn.Click += async (_, _) => await DeleteNote();
        var minBtn = TopBtn("—", "minimizar a nota");
        minBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        var closeBtn = TopBtn("✕", "fechar (a nota fica salva)");
        closeBtn.Click += (_, _) => Close();

        var topRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        topRight.Children.Add(delBtn);
        topRight.Children.Add(minBtn);
        topRight.Children.Add(closeBtn);

        var header = new DockPanel
        {
            Height = 34,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x16, 0x16, 0x13)),
            Cursor = Cursors.SizeAll,
            LastChildFill = true
        };
        DockPanel.SetDock(topRight, Dock.Right);
        header.Children.Add(topRight);
        var dragZone = new Border { Background = Brushes.Transparent, Padding = new Thickness(9, 0, 0, 0), Child = _dots };
        dragZone.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(dragZone);
        header.MouseEnter += (_, _) => _dots.Opacity = 1;
        header.MouseLeave += (_, _) => _dots.Opacity = 0.45;

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_body);

        _root = new Border
        {
            Background = CorFundo(_cor),
            BorderBrush = ink,
            BorderThickness = new Thickness(2.5),
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

        // Layout interno acompanha o tamanho da janela: fonte, respiro e cabecalho
        // encolhem/crescem junto, em vez de ficar tudo fixo em nota grande.
        SizeChanged += (_, _) => AdaptLayout(header);
        Loaded += (_, _) => { AdaptLayout(header); _body.Focus(); _body.CaretIndex = _body.Text.Length; };
    }

    /// <summary>Ajusta fonte/respiro/cabecalho conforme a nota e esticada ou encolhida.</summary>
    private void AdaptLayout(DockPanel header)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        double menor = Math.Min(w, h);

        // fonte: 12.5 numa nota minima (200) ate 19 numa bem grande (620)
        double f = 12.5 + (Math.Clamp(w, 200, 620) - 200) * (19 - 12.5) / 420.0;
        _body.FontSize = Math.Round(f, 1);

        // respiro proporcional
        double pad = Math.Round(8 + (Math.Clamp(menor, 160, 560) - 160) * 12.0 / 400.0);
        _body.Padding = new Thickness(pad + 4, pad, pad + 2, pad + 2);

        // cabecalho e borda encolhem em notas pequenas
        header.Height = w < 260 ? 28 : w < 420 ? 34 : 40;
        _root.BorderThickness = new Thickness(w < 260 ? 2 : 2.5);

        // em nota muito estreita, esconde as bolinhas de cor pra sobrar arrasto
        _dots.Visibility = w < 235 ? Visibility.Collapsed : Visibility.Visible;
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
            await Supa.Update("notas", "id=eq." + Uri.EscapeDataString(_id), new JsonObject
            {
                ["titulo"] = titulo,
                ["corpo"] = texto,
                ["cor"] = _cor,
                ["data_nota"] = DateTime.Now.ToString("yyyy-MM-dd")
            });
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
