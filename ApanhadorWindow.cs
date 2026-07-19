using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Zimbar;

/// <summary>
/// Apanhador: ferramenta de captura do Acervo embutida no Zimbar. Print da tela,
/// imagem (colar/arquivo) ou texto -> vai pro Núcleo do Acervo (ac_inbox), com
/// opção de Transcrever (OCR nativo do Windows).
/// </summary>
public sealed class ApanhadorWindow : Window
{
    private static ApanhadorWindow? _instance;

    private readonly StackPanel _body;
    private byte[]? _png;
    private string _kind = "texto";
    private bool _transcrever;

    public static void Open()
    {
        if (_instance is null) { _instance = new ApanhadorWindow(); _instance.Closed += (_, _) => _instance = null; }
        _instance.Show();
        _instance.Activate();
        _instance.ShowMenu();
    }

    private ApanhadorWindow()
    {
        Title = "Apanhador";
        Width = 460; SizeToContent = SizeToContent.Height;
        MinHeight = 200;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/assets/ZimNotes.png")); } catch { }

        var ink = (Brush)FindResource("Ink");
        _body = new StackPanel();
        var card = new Border
        {
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = ink, BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("CardGlow"),
            Padding = new Thickness(20),
            Margin = new Thickness(20),
            Child = _body
        };
        Content = card;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not (Button or TextBox) && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    // -- Cabeçalho comum -------------------------------------------------

    private DockPanel Header(string titulo, bool voltar)
    {
        var ink = (Brush)FindResource("Ink");
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var fechar = new Button { Style = (Style)FindResource("Chip"), Content = "✕", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        fechar.Click += (_, _) => Close();
        DockPanel.SetDock(fechar, Dock.Right);
        dp.Children.Add(fechar);
        if (voltar)
        {
            var back = new Button { Style = (Style)FindResource("Chip"), Content = "‹ voltar", Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            back.Click += (_, _) => ShowMenu();
            DockPanel.SetDock(back, Dock.Left);
            dp.Children.Add(back);
        }
        var badge = Zui.IconBadge(this, "⚡", 0, 30);
        badge.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(badge, Dock.Left);
        dp.Children.Add(badge);
        dp.Children.Add(new TextBlock { Text = titulo, FontSize = 19, FontFamily = (FontFamily)FindResource("Display"), Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        return dp;
    }

    // -- Menu ------------------------------------------------------------

    private void ShowMenu()
    {
        _png = null; _transcrever = false;
        _body.Children.Clear();
        _body.Children.Add(Header("Apanhador", voltar: false));
        _body.Children.Add(new TextBlock
        {
            Text = "Pega agora, organiza depois no Acervo.", FontSize = 12.5, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(2, 0, 0, 12)
        });

        _body.Children.Add(MenuCard("⛶", "Capturar tela", "Arrasta um retângulo em qualquer lugar", 4, async () => await CapturarTela()));
        _body.Children.Add(MenuCard("▦", "Colar / escolher imagem", "Da área de transferência ou de um arquivo", 3, async () => await ColarOuArquivo()));
        _body.Children.Add(MenuCard("✎", "Escrever", "Ideia, frase, lembrete rápido", 2, () => { ShowTexto(); return Task.CompletedTask; }));
    }

    private FrameworkElement MenuCard(string glyph, string titulo, string sub, int tint, Func<Task> onClick)
    {
        var ink = (Brush)FindResource("Ink");
        var row = new DockPanel();
        var badge = Zui.IconBadge(this, glyph, tint, 42);
        badge.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = titulo, FontSize = 16, FontFamily = (FontFamily)FindResource("Display"), Foreground = ink });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = (Brush)FindResource("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        row.Children.Add(sp);
        return Zui.Block(this, row, background: Zui.Tint(this, tint), onClick: () => _ = onClick(), padding: new Thickness(14), margin: new Thickness(0, 0, 0, 10));
    }

    // -- Captura de tela / imagem ---------------------------------------

    private async Task CapturarTela()
    {
        Hide();
        await Task.Delay(180);        // dá tempo da janela sumir do print
        byte[]? png = null;
        try { png = RegionCapture.Capturar(); } catch { }
        Show(); Activate();
        if (png != null) { _png = png; _kind = "print"; ShowImagem(); }
        else ShowMenu();
    }

    private async Task ColarOuArquivo()
    {
        // 1) tenta imagem da área de transferência
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bs)
            { _png = Encode(bs); _kind = "print"; ShowImagem(); return; }
        }
        catch { }
        // 2) senão, abre arquivo
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                using var bmp = new Drawing.Bitmap(dlg.FileName);
                using var ms = new MemoryStream();
                bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                _png = ms.ToArray(); _kind = "foto"; ShowImagem();
            }
            catch { MessageBox.Show(this, "não consegui abrir essa imagem", "Apanhador"); }
        }
        await Task.CompletedTask;
    }

    private TextBox? _noteBox;

    private void ShowImagem()
    {
        var ink = (Brush)FindResource("Ink");
        _body.Children.Clear();
        _body.Children.Add(Header("Captura", voltar: true));

        _body.Children.Add(new Border
        {
            BorderBrush = ink, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("Mist"), MaxHeight = 300, Margin = new Thickness(0, 0, 0, 12),
            Child = new Image { Source = ToSource(_png!), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) }
        });

        _noteBox = new TextBox { Style = (Style)FindResource("InlineAdd"), FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
        _body.Children.Add(HintBox(_noteBox, "nota junto (opcional)"));

        // Toggle Transcrever (OCR)
        var trBtn = new Button { Style = (Style)FindResource("Chip"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10) };
        void PaintToggle() => trBtn.Content = (_transcrever ? "☑" : "☐") + "  Transcrever o texto da imagem (OCR)";
        PaintToggle();
        trBtn.Background = Ocr.Disponivel ? (Brush)FindResource("Surface") : (Brush)FindResource("Mist");
        trBtn.IsEnabled = Ocr.Disponivel;
        trBtn.ToolTip = Ocr.Disponivel ? "extrai o texto da imagem localmente" : "OCR do Windows não disponível";
        trBtn.Click += (_, _) => { _transcrever = !_transcrever; PaintToggle(); trBtn.Background = _transcrever ? (Brush)FindResource("LeafSoft") : (Brush)FindResource("Surface"); };
        _body.Children.Add(trBtn);

        var mandar = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "⚡  Mandar pro Acervo", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 9, 14, 9), FontSize = 14 };
        mandar.Click += async (_, _) => await Enviar(mandar);
        _body.Children.Add(mandar);
    }

    private void ShowTexto()
    {
        _kind = "texto"; _png = null;
        _body.Children.Clear();
        _body.Children.Add(Header("Escrever", voltar: true));
        var tb = new TextBox
        {
            Style = (Style)FindResource("InlineAdd"), FontSize = 14, MinHeight = 130,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _noteBox = tb;
        _body.Children.Add(HintBox(tb, "despeja aqui..."));
        var mandar = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "⚡  Mandar pro Acervo", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 9, 14, 9), FontSize = 14 };
        mandar.Click += async (_, _) => await Enviar(mandar);
        _body.Children.Add(mandar);
        tb.Focus();
    }

    private async Task Enviar(Button btn)
    {
        string nota = _noteBox?.Text.Trim() ?? "";
        if (_kind == "texto" && nota.Length == 0) { ShowStatusInline(btn, "escreve algo primeiro"); return; }
        btn.IsEnabled = false;
        var original = btn.Content;
        try
        {
            string? transcricao = null;
            if (_png != null && _transcrever)
            {
                btn.Content = "lendo o texto...";
                transcricao = await Ocr.LerAsync(_png);
                if (string.IsNullOrWhiteSpace(transcricao)) transcricao = "(nenhum texto encontrado)";
            }
            string? imagePath = null;
            if (_png != null)
            {
                btn.Content = "enviando imagem...";
                imagePath = await Acervo.UploadPng(_png);
            }
            btn.Content = "mandando...";
            await Acervo.Capturar(_kind, _kind == "texto" ? nota : nota, imagePath, transcricao);
            ShowFeito();
        }
        catch (Exception ex)
        {
            btn.Content = original;
            btn.IsEnabled = true;
            MessageBox.Show(this, "não foi: " + ex.Message, "Apanhador");
        }
    }

    private void ShowFeito()
    {
        var ink = (Brush)FindResource("Ink");
        _body.Children.Clear();
        _body.Children.Add(Header("Apanhador", voltar: false));
        var check = new Border
        {
            Width = 66, Height = 66, CornerRadius = new CornerRadius(33),
            Background = (Brush)FindResource("Leaf"), BorderBrush = ink, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 12),
            Child = new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeights.Bold, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        _body.Children.Add(check);
        _body.Children.Add(new TextBlock { Text = "No Núcleo!", FontSize = 22, FontFamily = (FontFamily)FindResource("Display"), Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) });
        var outra = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "⚡  Capturar outra", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 9, 14, 9), FontSize = 14 };
        outra.Click += (_, _) => ShowMenu();
        _body.Children.Add(outra);
    }

    // -- Helpers ---------------------------------------------------------

    private Grid HintBox(TextBox tb, string placeholder)
    {
        var g = new Grid();
        var hint = new TextBlock { Text = placeholder, FontSize = 13, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(12, 8, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        tb.TextChanged += (_, _) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        g.Children.Add(tb);
        g.Children.Add(hint);
        return g;
    }

    private void ShowStatusInline(Button btn, string msg)
    {
        var old = btn.Content; btn.Content = msg;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) => { btn.Content = old; t.Stop(); };
        t.Start();
    }

    private static BitmapSource ToSource(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
        return bi;
    }

    private static byte[] Encode(BitmapSource bs)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
