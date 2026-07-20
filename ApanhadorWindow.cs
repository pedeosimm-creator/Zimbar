using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Zimbar;

/// <summary>
/// Apanhador: ferramenta de captura do Acervo embutida no Zimbar. Num painel só:
/// uma caixinha pra arrastar imagem / escrever / capturar tela e mandar pro Núcleo,
/// e a lista dos "últimos apanhados" — cada um pode ser encaminhado (pra uma pasta
/// de Referências, um capítulo de Estudo ou um Livro) ou transcrito (OCR nativo).
/// </summary>
public sealed class ApanhadorWindow : Window
{
    private static ApanhadorWindow? _instance;

    private readonly Border _card;
    private byte[]? _composePng;
    private string _composeKind = "print";
    private TextBox? _composeText;
    private StackPanel? _composeImgHost;
    private StackPanel? _listHost;

    public static void Open()
    {
        Ensure();
        _instance!.Show();
        _instance.Activate();
        _instance.ShowMain();
    }

    /// <summary>Atalho global: já cai na captura de tela e volta com a imagem pronta.</summary>
    public static async void OpenCapture()
    {
        Ensure();
        var w = _instance!;
        byte[]? png = null;
        try { png = RegionCapture.Capturar(); } catch { }
        w.Show(); w.Activate();
        w.ShowMain();
        if (png != null) { w._composePng = png; w._composeKind = "print"; w.RefreshComposeImage(); }
        await Task.CompletedTask;
    }

    private static void Ensure()
    {
        if (_instance is null) { _instance = new ApanhadorWindow(); _instance.Closed += (_, _) => _instance = null; }
    }

    private ApanhadorWindow()
    {
        Title = "Apanhador";
        Width = 480;
        MinHeight = 240;
        double maxH = SystemParameters.WorkArea.Height - 60;
        MaxHeight = maxH;
        Height = Math.Min(668, maxH);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/assets/ZimNotes.png")); } catch { }

        _card = new Border
        {
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = (Brush)FindResource("Ink"), BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("CardGlow"),
            Padding = new Thickness(18), Margin = new Thickness(18)
        };
        Content = _card;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not (Button or TextBox or ComboBox) && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private Brush Res(string k) => (Brush)FindResource(k);
    private FontFamily Font(string k) => (FontFamily)FindResource(k);

    // -- Cabeçalho -------------------------------------------------------

    private DockPanel Header(string titulo, Action? voltar)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var fechar = new Button { Style = (Style)FindResource("Chip"), Content = "✕", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        fechar.Click += (_, _) => Close();
        DockPanel.SetDock(fechar, Dock.Right);
        dp.Children.Add(fechar);
        if (voltar != null)
        {
            var back = new Button { Style = (Style)FindResource("Chip"), Content = "‹ voltar", Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            back.Click += (_, _) => voltar();
            DockPanel.SetDock(back, Dock.Left);
            dp.Children.Add(back);
        }
        var badge = Zui.IconBadge(this, "⚡", 0, 30);
        badge.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(badge, Dock.Left);
        dp.Children.Add(badge);
        dp.Children.Add(new TextBlock { Text = titulo, FontSize = 19, FontFamily = Font("Display"), Foreground = Res("Ink"), VerticalAlignment = VerticalAlignment.Center });
        return dp;
    }

    private TextBlock Label(string t) => new()
    {
        Text = t, FontSize = 10.5, FontWeight = FontWeights.Bold, FontFamily = Font("Mono"),
        Foreground = Res("TextDim"), Margin = new Thickness(2, 0, 0, 6)
    };

    // -- Painel principal: compose + lista -------------------------------

    private void ShowMain()
    {
        var root = new DockPanel();
        var head = Header("Apanhador", null);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var compose = BuildCompose();
        DockPanel.SetDock(compose, Dock.Top);
        root.Children.Add(compose);

        var lbl = Label("ÚLTIMOS APANHADOS");
        lbl.Margin = new Thickness(2, 14, 0, 6);
        DockPanel.SetDock(lbl, Dock.Top);
        root.Children.Add(lbl);

        _listHost = new StackPanel();
        _listHost.Children.Add(new TextBlock { Text = "carregando...", FontSize = 12.5, Foreground = Res("TextDone"), Margin = new Thickness(2, 4, 0, 0) });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _listHost, Margin = new Thickness(0, 0, -6, 0)
        };
        root.Children.Add(scroll);

        _card.Child = root;
        _ = LoadList();
    }

    private FrameworkElement BuildCompose()
    {
        var inner = new StackPanel();

        _composeImgHost = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        inner.Children.Add(_composeImgHost);
        RefreshComposeImage();

        _composeText = new TextBox
        {
            Style = (Style)FindResource("InlineAdd"), FontSize = 13.5, MinHeight = 56,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        var hintGrid = new Grid();
        var hint = new TextBlock { Text = "arrasta uma imagem, cola ou escreve aqui...", FontSize = 13, Foreground = Res("TextDone"), Margin = new Thickness(2, 2, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        _composeText.TextChanged += (_, _) => hint.Visibility = _composeText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        hintGrid.Children.Add(_composeText);
        hintGrid.Children.Add(hint);
        inner.Children.Add(hintGrid);

        // barra de ações do compose
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var tela = MiniBtn("⛶  capturar tela", () => _ = CapturarTela());
        var img = MiniBtn("▦  imagem", () => _ = ColarOuArquivo());
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(tela); left.Children.Add(img);
        DockPanel.SetDock(left, Dock.Left);
        actions.Children.Add(left);

        var mandar = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "⚡ mandar", Padding = new Thickness(14, 7, 14, 7), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
        mandar.Click += async (_, _) => await MandarComposeParaNucleo(mandar);
        DockPanel.SetDock(mandar, Dock.Right);
        actions.Children.Add(mandar);
        inner.Children.Add(actions);

        var box = Zui.Block(this, inner, background: Res("Surface"), padding: new Thickness(14), margin: new Thickness(0, 0, 4, 0));
        box.AllowDrop = true;
        box.DragOver += Compose_DragOver;
        box.Drop += Compose_Drop;
        return box;
    }

    private Button MiniBtn(string txt, Action onClick)
    {
        var b = new Button { Style = (Style)FindResource("Chip"), Content = txt, FontSize = 12, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), Background = Res("CardBg") };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RefreshComposeImage()
    {
        if (_composeImgHost is null) return;
        _composeImgHost.Children.Clear();
        if (_composePng is null) { _composeImgHost.Visibility = Visibility.Collapsed; return; }
        _composeImgHost.Visibility = Visibility.Visible;
        var dp = new DockPanel();
        var rem = new Button { Style = (Style)FindResource("Chip"), Content = "✕", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top };
        rem.Click += (_, _) => { _composePng = null; _composeKind = "texto"; RefreshComposeImage(); };
        DockPanel.SetDock(rem, Dock.Right);
        dp.Children.Add(rem);
        dp.Children.Add(new Border
        {
            BorderBrush = Res("Ink"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
            Background = Res("Mist"), MaxHeight = 190, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Image { Source = ToSource(_composePng), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(3) }
        });
        _composeImgHost.Children.Add(dp);
    }

    private void Compose_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Compose_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var f = files.FirstOrDefault(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                if (f != null)
                {
                    using var bmp = new Drawing.Bitmap(f);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                    _composePng = ms.ToArray(); _composeKind = "foto"; RefreshComposeImage();
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bs)
            { _composePng = Encode(bs); _composeKind = "print"; RefreshComposeImage(); }
        }
        catch { }
        e.Handled = true;
    }

    private async Task CapturarTela()
    {
        Hide();
        await Task.Delay(180);
        byte[]? png = null;
        try { png = RegionCapture.Capturar(); } catch { }
        Show(); Activate();
        if (png != null) { _composePng = png; _composeKind = "print"; RefreshComposeImage(); }
    }

    private async Task ColarOuArquivo()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bs)
            { _composePng = Encode(bs); _composeKind = "print"; RefreshComposeImage(); return; }
        }
        catch { }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                using var bmp = new Drawing.Bitmap(dlg.FileName);
                using var ms = new MemoryStream();
                bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                _composePng = ms.ToArray(); _composeKind = "foto"; RefreshComposeImage();
            }
            catch { MessageBox.Show(this, "não consegui abrir essa imagem", "Apanhador"); }
        }
        await Task.CompletedTask;
    }

    private async Task MandarComposeParaNucleo(Button btn)
    {
        string texto = _composeText?.Text.Trim() ?? "";
        if (_composePng is null && texto.Length == 0) { Flash(btn, "escreve ou anexa algo"); return; }
        btn.IsEnabled = false;
        var original = btn.Content;
        try
        {
            string? imagePath = null;
            if (_composePng != null) { btn.Content = "enviando imagem..."; imagePath = await Acervo.UploadPng(_composePng); }
            btn.Content = "mandando...";
            string kind = _composePng != null ? _composeKind : "texto";
            await Acervo.Capturar(kind, texto, imagePath, null);
            // limpa o compose e recarrega a lista
            _composePng = null; _composeKind = "texto";
            if (_composeText != null) _composeText.Text = "";
            RefreshComposeImage();
            Flash(btn, "✓ no Núcleo!", (Brush)FindResource("Leaf"));
            await LoadList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "não foi: " + ex.Message, "Apanhador");
        }
        finally
        {
            btn.Content = original; btn.IsEnabled = true;
        }
    }

    // -- Lista de últimos apanhados --------------------------------------

    private async Task LoadList()
    {
        List<Acervo.Item> itens;
        try { itens = await Acervo.ListarNucleo(20); }
        catch { if (_listHost != null) { _listHost.Children.Clear(); _listHost.Children.Add(DimLine("sem conexão com o Acervo agora")); } return; }
        if (_listHost is null) return;
        _listHost.Children.Clear();
        if (itens.Count == 0) { _listHost.Children.Add(DimLine("núcleo limpo — nada esperando encaminhar")); return; }
        foreach (var it in itens) _listHost.Children.Add(ItemCard(it));
    }

    private FrameworkElement ItemCard(Acervo.Item it)
    {
        var row = new DockPanel();
        if (it.HasImage)
        {
            var thumb = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(9),
                BorderBrush = Res("Ink"), BorderThickness = new Thickness(2), Background = Res("Mist"),
                Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top
            };
            var im = new Image { Stretch = Stretch.UniformToFill };
            try { im.Source = new BitmapImage(new Uri(Acervo.PublicUrl(it.ImagePath!))); } catch { }
            thumb.Child = im;
            DockPanel.SetDock(thumb, Dock.Left);
            row.Children.Add(thumb);
        }

        var sp = new StackPanel();
        var kindDot = it.Kind == "texto" ? "✎ texto" : it.Kind == "foto" ? "▦ foto" : "⛶ print";
        var meta = new TextBlock { Text = $"{kindDot}  ·  {it.CriadoEm:dd/MM HH:mm}", FontSize = 10, FontFamily = Font("Mono"), Foreground = Res("TextDone"), Margin = new Thickness(0, 0, 0, 3) };
        sp.Children.Add(meta);
        string preview = it.TextoAtual;
        if (preview.Length == 0) preview = it.HasImage ? "(imagem — transcreva pra virar texto)" : "(vazio)";
        sp.Children.Add(new TextBlock { Text = preview, FontSize = 13, Foreground = Res("TextMain"), TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var enc = new Button { Style = (Style)FindResource("Chip"), Content = "↳ encaminhar", FontSize = 12, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), Background = Res("Accent") };
        enc.Click += (_, _) => ShowForward(it);
        btns.Children.Add(enc);
        if (it.HasImage)
        {
            var tr = new Button { Style = (Style)FindResource("Chip"), Content = "◔ transcrever", FontSize = 12, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), Background = Res("CardBg") };
            tr.IsEnabled = Ocr.Disponivel;
            tr.ToolTip = Ocr.Disponivel ? "extrai o texto da imagem (OCR)" : "OCR do Windows não disponível";
            tr.Click += async (_, _) => await TranscreverItem(it, tr);
            btns.Children.Add(tr);
        }
        var del = new Button { Style = (Style)FindResource("Chip"), Content = "🗑", FontSize = 12, Padding = new Thickness(9, 5, 9, 5), Background = Res("CardBg") };
        del.Click += async (_, _) => { try { await Acervo.Apagar(it); await LoadList(); } catch { } };
        btns.Children.Add(del);
        sp.Children.Add(btns);

        row.Children.Add(sp);
        return Zui.Block(this, row, background: Res("CardBg"), padding: new Thickness(12), margin: new Thickness(0, 0, 4, 8));
    }

    private async Task TranscreverItem(Acervo.Item it, Button btn)
    {
        if (!it.HasImage) return;
        btn.IsEnabled = false; var old = btn.Content; btn.Content = "lendo...";
        try
        {
            var bytes = await Acervo.BaixarImagem(it.ImagePath!);
            var t = await Ocr.LerAsync(bytes);
            if (string.IsNullOrWhiteSpace(t)) t = "(nenhum texto encontrado)";
            await Acervo.SalvarTranscricao(it.Id, t);
            it.Transcription = t;
            await LoadList();
        }
        catch (Exception ex) { btn.Content = old; btn.IsEnabled = true; MessageBox.Show(this, "não deu pra transcrever: " + ex.Message, "Apanhador"); }
    }

    // -- Encaminhar (refs / estudo / livro) ------------------------------

    private void ShowForward(Acervo.Item it)
    {
        var root = new DockPanel();
        var head = Header("Encaminhar", ShowMain);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var body = new StackPanel();

        // prévia do item
        var prev = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (it.HasImage)
        {
            var thumb = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(9), BorderBrush = Res("Ink"), BorderThickness = new Thickness(2), Background = Res("Mist"), Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
            var im = new Image { Stretch = Stretch.UniformToFill };
            try { im.Source = new BitmapImage(new Uri(Acervo.PublicUrl(it.ImagePath!))); } catch { }
            thumb.Child = im; DockPanel.SetDock(thumb, Dock.Left); prev.Children.Add(thumb);
        }
        var previewText = new TextBlock { Text = it.TextoAtual.Length > 0 ? it.TextoAtual : "(sem texto ainda)", FontSize = 13, Foreground = Res("TextMain"), TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        prev.Children.Add(previewText);
        body.Children.Add(Zui.Block(this, prev, background: Res("Surface"), padding: new Thickness(10), margin: new Thickness(0, 0, 4, 12)));

        // se é imagem sem texto, oferece transcrever pra virar citação
        if (it.HasImage && !it.HasText && Ocr.Disponivel)
        {
            var trBtn = new Button { Style = (Style)FindResource("Chip"), Content = "◔ transcrever o texto da imagem", FontSize = 12.5, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
            trBtn.Click += async (_, _) =>
            {
                trBtn.IsEnabled = false; trBtn.Content = "lendo...";
                try
                {
                    var bytes = await Acervo.BaixarImagem(it.ImagePath!);
                    var t = await Ocr.LerAsync(bytes);
                    if (string.IsNullOrWhiteSpace(t)) t = "(nenhum texto encontrado)";
                    await Acervo.SalvarTranscricao(it.Id, t); it.Transcription = t;
                    ShowForward(it); // recarrega com o texto
                }
                catch (Exception ex) { trBtn.IsEnabled = true; trBtn.Content = "◔ transcrever"; MessageBox.Show(this, ex.Message, "Apanhador"); }
            };
            body.Children.Add(trBtn);
        }

        body.Children.Add(Label("MANDAR PRA"));
        var destArea = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var okBtn = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "↳ encaminhar", Padding = new Thickness(14, 9, 14, 9), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0), IsEnabled = false };

        string dest = "";
        var b1 = DestBtn("📁", "Referências", 3);
        var b2 = DestBtn("✎", "Estudos", 2);
        var b3 = DestBtn("📖", "Livros", 4);
        b3.IsEnabled = it.HasText;
        b3.ToolTip = it.HasText ? null : "transcreva a imagem primeiro pra virar citação";
        var destRow = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 0) };
        destRow.Children.Add(b1); destRow.Children.Add(b2); destRow.Children.Add(b3);
        body.Children.Add(destRow);
        body.Children.Add(destArea);
        body.Children.Add(okBtn);

        // estado dos destinos
        Acervo.Destino? pasta = null, estudo = null, livro = null;
        Acervo.Secao? secao = null;
        string pagina = "", autor = "";

        void Recolor()
        {
            b1.Background = dest == "refs" ? Res("AccentSoft") : Res("CardBg");
            b2.Background = dest == "estudo" ? Res("AccentSoft") : Res("CardBg");
            b3.Background = dest == "livro" ? Res("AccentSoft") : Res("CardBg");
        }
        void UpdateReady()
        {
            okBtn.IsEnabled = dest == "refs" ? pasta != null
                : dest == "estudo" ? secao != null
                : dest == "livro" ? (livro != null && it.HasText)
                : false;
        }

        async void PickRefs()
        {
            dest = "refs"; Recolor(); destArea.Children.Clear();
            destArea.Children.Add(Label("QUAL PASTA?"));
            var cb = NewCombo();
            destArea.Children.Add(cb);
            UpdateReady();
            try
            {
                var pastas = await Acervo.Pastas();
                cb.ItemsSource = pastas; cb.DisplayMemberPath = "Nome";
                cb.SelectionChanged += (_, _) => { pasta = cb.SelectedItem as Acervo.Destino; UpdateReady(); };
            }
            catch { destArea.Children.Add(DimLine("não carregou as pastas")); }
        }

        async void PickEstudo()
        {
            dest = "estudo"; Recolor(); destArea.Children.Clear();
            destArea.Children.Add(Label("QUAL ESTUDO?"));
            var cbE = NewCombo();
            destArea.Children.Add(cbE);
            destArea.Children.Add(Label("QUAL CAPÍTULO?"));
            var cbS = NewCombo();
            destArea.Children.Add(cbS);
            UpdateReady();
            try
            {
                var estudos = await Acervo.Estudos();
                cbE.ItemsSource = estudos; cbE.DisplayMemberPath = "Nome";
                cbE.SelectionChanged += async (_, _) =>
                {
                    estudo = cbE.SelectedItem as Acervo.Destino; secao = null; cbS.ItemsSource = null; UpdateReady();
                    if (estudo == null) return;
                    try { var secs = await Acervo.Secoes(estudo.Id); cbS.ItemsSource = secs; cbS.DisplayMemberPath = "Titulo"; }
                    catch { }
                };
                cbS.SelectionChanged += (_, _) => { secao = cbS.SelectedItem as Acervo.Secao; UpdateReady(); };
            }
            catch { destArea.Children.Add(DimLine("não carregou os estudos")); }
        }

        async void PickLivro()
        {
            dest = "livro"; Recolor(); destArea.Children.Clear();
            destArea.Children.Add(Label("QUAL LIVRO? (vira citação)"));
            var cb = NewCombo();
            destArea.Children.Add(cb);
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            var pg = new TextBox { Style = (Style)FindResource("InlineAdd"), FontSize = 13, Margin = new Thickness(0, 0, 4, 0), ToolTip = "página (opcional)" };
            var au = new TextBox { Style = (Style)FindResource("InlineAdd"), FontSize = 13, Margin = new Thickness(4, 0, 0, 0), ToolTip = "autor (opcional)" };
            pg.TextChanged += (_, _) => pagina = pg.Text; au.TextChanged += (_, _) => autor = au.Text;
            grid.Children.Add(HintWrap(pg, "página")); grid.Children.Add(HintWrap(au, "autor"));
            destArea.Children.Add(grid);
            UpdateReady();
            try
            {
                var livros = await Acervo.Livros();
                cb.ItemsSource = livros; cb.DisplayMemberPath = "Nome";
                cb.SelectionChanged += (_, _) => { livro = cb.SelectedItem as Acervo.Destino; UpdateReady(); };
            }
            catch { destArea.Children.Add(DimLine("não carregou os livros")); }
        }

        b1.Click += (_, _) => PickRefs();
        b2.Click += (_, _) => PickEstudo();
        b3.Click += (_, _) => PickLivro();

        okBtn.Click += async (_, _) =>
        {
            okBtn.IsEnabled = false; var old = okBtn.Content; okBtn.Content = "encaminhando...";
            try
            {
                if (dest == "refs" && pasta != null) await Acervo.EncaminharParaPasta(it, pasta);
                else if (dest == "estudo" && estudo != null && secao != null) await Acervo.EncaminharParaEstudo(it, estudo.Nome, secao);
                else if (dest == "livro" && livro != null)
                {
                    int? pg = int.TryParse(pagina.Trim(), out var p) ? p : null;
                    await Acervo.EncaminharParaLivro(it, livro, pg, autor);
                }
                ShowMain();
            }
            catch (Exception ex) { okBtn.IsEnabled = true; okBtn.Content = old; MessageBox.Show(this, "não deu pra encaminhar: " + ex.Message, "Apanhador"); }
        };

        Recolor();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body, Margin = new Thickness(0, 0, -6, 0) };
        root.Children.Add(scroll);
        _card.Child = root;
    }

    private Button DestBtn(string glyph, string txt, int tint)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = txt, FontSize = 11.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        var b = new Button { Style = (Style)FindResource("Chip"), Content = sp, Padding = new Thickness(6, 10, 6, 10), Margin = new Thickness(0, 0, 6, 0), Background = Res("CardBg") };
        return b;
    }

    private ComboBox NewCombo() => new()
    {
        FontFamily = Font("Body"), FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
        Margin = new Thickness(0, 0, 0, 4), MinWidth = 120
    };

    // -- helpers ---------------------------------------------------------

    private TextBlock DimLine(string t) => new() { Text = t, FontSize = 12.5, Foreground = Res("TextDone"), Margin = new Thickness(2, 4, 0, 4), TextWrapping = TextWrapping.Wrap };

    private Grid HintWrap(TextBox tb, string placeholder)
    {
        var g = new Grid();
        var hint = new TextBlock { Text = placeholder, FontSize = 13, Foreground = Res("TextDone"), Margin = new Thickness(10, 6, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        tb.TextChanged += (_, _) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        g.Children.Add(tb); g.Children.Add(hint);
        return g;
    }

    private void Flash(Button btn, string msg, Brush? bg = null)
    {
        var old = btn.Content; var oldBg = btn.Background;
        btn.Content = msg; if (bg != null) btn.Background = bg;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { btn.Content = old; btn.Background = oldBg; t.Stop(); };
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
