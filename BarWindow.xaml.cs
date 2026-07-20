using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Zimbar;

/// <summary>
/// Zimbar v0.5 � Painel de panorama como primeira aba (com a captura dentro),
/// entrada com tr�s modos (captura / busca interna / pesquisa web), Norte com
/// etapas marc�veis, agenda em lista fluida, refer�ncias com categorias, aba
/// Listas, aba Not�cias, player nativo e barra redimension�vel pelo canto.
/// </summary>
public partial class BarWindow : Window
{
    private string _currentView = "Painel";
    private JsonObject? _foco;
    private JsonObject? _ritmo;
    private string _hojeTarget = "med"; // big | med | small
    private bool _agendaSemana = true;
    private DateTime _agendaRef = DateTime.Now.Date;
    private JsonArray _tarefasCache = new();
    private string _inputMode = "captura"; // captura | busca | web
    private string _buscaQuery = "";
    private string _newsTopic = "";
    private string _emailFolder = "inbox"; // inbox | spam | trash
    private string _emailAccountId = "all";
    private string? _emailExpandedId;
    private List<EmailAccount> _emailAccounts = new();
    private List<EmailItem> _emailItems = new();
    private bool _emailLoading;
    private readonly Dictionary<string, (DateTime At, List<EmailItem> Items)> _emailCache = new();
    private bool _emailForceRefresh;
    private int _emailReqGen;   // invalida buscas de e-mail obsoletas (troca de conta/pasta durante o fetch)

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // Sincronia com o Meu Espa�o: recarrega a aba atual sozinho enquanto a barra est� aberta.
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _busyModal;   // di�logo modal aberto: n�o minimiza ao perder foco

    public BarWindow()
    {
        InitializeComponent();
        Width = Config.BarWidth ?? 1160;
        ViewHost.Height = Config.ViewMax ?? 430;
        SizeChanged += (_, _) => FitNav();
        Loaded += (_, _) => FitNav();
        _statusTimer.Tick += (_, _) => { StatusText.Visibility = Visibility.Collapsed; _statusTimer.Stop(); };
        _playerTimer.Tick += (_, _) => _ = RenderTopPlayer();
        _refreshTimer.Tick += (_, _) => AutoRefresh();
        Activated += (_, _) => AutoRefresh();   // (A) recarrega quando a barra ganha foco
        StateChanged += Window_StateChanged;    // restaurar da barra de tarefas
        PreviewKeyDown += Window_PreviewKeyDown;
        BuildThemeList();
        BuildModeRow();
        ApplyShellDesign();
    }

    // -- Abrir / fechar (n�o fecha ao clicar fora!) -----------------

    public void ShowBar()
    {
        Input.Clear();
        SetInputMode("captura");
        Show();
        UpdateLayout();
        if (Config.BarLeft is double l && Config.BarTop is double t) { Left = l; Top = t; }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + wa.Height * 0.2;
        }
        KeepOnScreen();
        Activate();
        Input.Focus();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(170))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Card.BeginAnimation(OpacityProperty, fade);
        CardSlide.BeginAnimation(TranslateTransform.YProperty, slide);

        SwitchView("Painel");
        _lastRefresh = DateTime.Now;   // acabou de carregar via SwitchView
        _ = RenderTopPlayer();
        _playerTimer.Start();
        _refreshTimer.Start();   // (B) polling enquanto aberta
    }

    public void HideBar()
    {
        _playerTimer.Stop();
        _refreshTimer.Stop();
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
    }

    public bool IsCollapsed => WindowState == WindowState.Minimized;

    private void KeepOnScreen()
    {
        // Tela VIRTUAL (todos os monitores) — a barra pode morar no monitor 2.
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        var w = Math.Min(ActualWidth > 0 ? ActualWidth : Width, SystemParameters.VirtualScreenWidth);
        var h = Math.Min(ActualHeight > 0 ? ActualHeight : 140, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(Left, vl, Math.Max(vl, vr - w));
        Top = Math.Clamp(Top, vt, Math.Max(vt, vb - h));
    }

    /// <summary>Minimiza a barra pra barra de tarefas, como uma janela normal (Ctrl+Alt+Z).</summary>
    public void CollapseBar()
    {
        if (!IsVisible || _busyModal) return;
        if (WindowState == WindowState.Minimized) return;

        ShowInTaskbar = true;                  // bot�o normal na barra de tarefas
        WindowState = WindowState.Minimized;
    }

    /// <summary>Restaura a barra minimizada (bot�o da barra de tarefas ou hotkey).</summary>
    public void ExpandBar()
    {
        if (!IsVisible) { ShowBar(); return; }
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Activate();
        Input.Focus();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            ShowInTaskbar = false;   // volta a ser HUD flutuante quando aberta
            Activate();
            Input.Focus();
            AutoRefresh();
        }
    }

    /// <summary>
    /// Recarrega a aba atual sozinho (foco ou polling), sem atrapalhar o uso:
    /// pula se algum popup/edi��o est� aberto, se est� arrastando ou digitando,
    /// e n�o repete se acabou de recarregar h� pouco.
    /// </summary>
    private void AutoRefresh()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (_currentView == "Email") return;
        if ((DateTime.Now - _lastRefresh).TotalSeconds < 3) return;
        if (IsUserBusy()) return;
        _lastRefresh = DateTime.Now;
        ReloadCurrentView();
    }

    private bool IsUserBusy()
    {
        if (PomoPopup.IsOpen || DatePopup.IsOpen
            || ThemePopup.IsOpen || KbEditPopup.IsOpen) return true;
        if (_hojeRenameId != null) return true;
        if (Mouse.LeftButton == MouseButtonState.Pressed) return true;         // arrastando/clicando
        if (Input.IsKeyboardFocused && !string.IsNullOrEmpty(Input.Text)) return true; // digitando
        return false;
    }

    /// <summary>Refaz a busca da aba vis�vel. News/Busca ficam de fora (evita bater no Bing / re-pesquisar).</summary>
    private void ReloadCurrentView()
    {
        switch (_currentView)
        {
            case "Painel": _ = LoadPainel(); break;
            case "Hoje": _ = LoadHoje(); break;
            case "Kanban": _ = LoadKanban(); break;
            case "Agenda": _ = LoadAgenda(); break;
            case "Links": _ = LoadLinks(); break;
            case "Listas": _ = LoadListas(); break;
            case "Contas": _ = LoadContas(); break;
            case "Email": LoadEmail(); break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (KbEditPopup.IsOpen) { KbEditPopup.IsOpen = false; e.Handled = true; return; }
            HideBar(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            string? v = e.Key switch
            {
                Key.D1 => "Painel", Key.D2 => "Hoje", Key.D3 => "Kanban", Key.D4 => "Agenda",
                Key.D5 => "Links", Key.D6 => "Listas", Key.D7 => "News", Key.D8 => "Contas",
                Key.D9 => "Email",
                _ => null
            };
            if (v is not null) { SwitchView(v); e.Handled = true; }
        }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        DragMove();
        Config.BarLeft = Left; Config.BarTop = Top;
        Config.Save();
    }

    private void DragZone_MouseDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        Config.BarLeft = Left; Config.BarTop = Top;
        Config.Save();
        e.Handled = true;
    }

    // -- Redimensionar pelo canto (?) � aplica 1x por frame, fluido -

    private bool _sizing;
    private Point _sizeStart;
    private double _w0, _v0, _pendW, _pendV;
    private bool _sizePending;

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _sizing = true;
        _sizeStart = e.GetPosition(this);
        _w0 = _pendW = ActualWidth;
        _v0 = _pendV = ViewHost.Height;
        ((UIElement)sender).CaptureMouse();
        CompositionTarget.Rendering += Sizing_Rendering;
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_sizing) return;
        var p = e.GetPosition(this);
        _pendW = Math.Clamp(_w0 + (p.X - _sizeStart.X), 980, 1760);
        _pendV = Math.Clamp(_v0 + (p.Y - _sizeStart.Y), 280, 760);
        _sizePending = true; // aplicado no pr�ximo frame (evita re-layout por pixel)
        e.Handled = true;
    }

    private void Sizing_Rendering(object? sender, EventArgs e)
    {
        if (!_sizePending) return;
        _sizePending = false;
        Width = _pendW;
        ViewHost.Height = _pendV;
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sizing) return;
        _sizing = false;
        CompositionTarget.Rendering -= Sizing_Rendering;
        ((UIElement)sender).ReleaseMouseCapture();
        Width = _pendW;
        ViewHost.Height = _pendV;
        Config.BarWidth = _pendW;
        Config.ViewMax = _pendV;
        Config.Save();
        e.Handled = true;
    }

    // -- Navega��o --------------------------------------------------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string view }) SwitchView(view);
    }

    private void SwitchView(string view)
    {
        _currentView = view;
        var views = new (string Name, UIElement El)[]
        {
            ("Painel", PainelView), ("Hoje", HojeView), ("Kanban", KanbanView),
            ("Agenda", AgendaView),
            ("Links", LinksView), ("Listas", ListasView), ("News", NewsView),
            ("Contas", ContasView), ("Email", EmailView),
            ("Busca", BuscaView),
        };
        foreach (var (name, el) in views)
            el.Visibility = name == view ? Visibility.Visible : Visibility.Collapsed;

        // Aba ativa = bloco de TINTA com texto claro + SOMBRA na cor do accent
        // (detalhe do .nb-tab.active do Acervo — deixa o tema visível)
        var ink = (Brush)FindResource("Ink");
        var onInk = (Brush)FindResource("Surface");   // texto claro sobre a tinta
        var txt = (Brush)FindResource("TextDim");
        var accentColor = ((SolidColorBrush)FindResource("Accent")).Color;
        foreach (var (btn, name) in new[]
        {
            (NavPainel, "Painel"), (NavHoje, "Hoje"), (NavKanban, "Kanban"), (NavAgenda, "Agenda"),
            (NavLinks, "Links"), (NavListas, "Listas"), (NavNews, "News"), (NavContas, "Contas"), (NavEmail, "Email")
        })
        {
            bool on = name == view;
            btn.Background = on ? ink : Brushes.Transparent;
            btn.Foreground = on ? onInk : txt;
            btn.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
            btn.Effect = on
                ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 0, ShadowDepth = 3, Direction = 315, Opacity = 1, Color = accentColor }
                : null;
        }

        var visivel = views.FirstOrDefault(v => v.Name == view).El;
        if (visivel is not null) AnimateIn(visivel, fromY: 0, ms: 130);   // s� fade: sem "tiltada" na troca

        switch (view)
        {
            case "Painel": _ = LoadPainel(); break;
            case "Hoje": _ = LoadHoje(); break;
            case "Kanban": _ = LoadKanban(); break;
            case "Agenda": _ = LoadAgenda(); break;
            case "Links": _ = LoadLinks(); break;
            case "Listas": _ = LoadListas(); break;
            case "News": _ = LoadNews(); break;
            case "Contas": _ = LoadContas(); break;
            case "Email": LoadEmail(); break;
            case "Busca": _ = LoadBusca(); break;
        }
    }

    private void ApplyShellDesign()
    {
        // Shell NEOBRUTALISTA: papel chapado, borda de tinta grossa, blocos retos.
        ShellRail.Visibility = Visibility.Collapsed;
        RailColumn.Width = new GridLength(0);
        ShellStack.Margin = new Thickness(20, 16, 22, 12);
        CommandSurface.BorderBrush = (Brush)FindResource("Ink");
        CommandSurface.BorderThickness = new Thickness(2);
        CommandSurface.Padding = new Thickness(12, 9, 12, 9);
        CommandDock.Margin = new Thickness(0);
        DecorLayer.Visibility = Visibility.Collapsed;
        DecorScanline.Opacity = 0;
        DecorOrbit.Opacity = 0;
        Card.BorderThickness = new Thickness(3);
        Card.Padding = new Thickness(0);
        ViewHost.Margin = new Thickness(0, 4, 0, 0);

        SetNavLabels(full: true, orbital: false);
        NavItems.Margin = new Thickness(0);
        ActionItems.Margin = new Thickness(0);
        DragGlyph.Text = "⠿";
        Card.CornerRadius = new CornerRadius(16);
        CommandSurface.Background = (Brush)FindResource("Surface");
        CommandSurface.CornerRadius = new CornerRadius(10);

        // Régua sólida de tinta separando o miolo (nada de gradiente etéreo)
        ShellSeparator.Fill = (Brush)FindResource("Ink");
        ShellSeparator.Height = 2;
        ShellSeparator.Margin = new Thickness(2, 12, 2, 2);
        ShellSeparator.Opacity = 0.85;
    }

    /// <summary>Se as abas não couberem na largura atual, caem pra versão só-ícone (nenhuma some).</summary>
    private void FitNav()
    {
        if (!IsLoaded) return;
        double avail = NavDock.ActualWidth - ActionItems.ActualWidth - 6;
        if (avail <= 0) return;
        SetNavLabels(full: true, orbital: false);
        NavItems.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (NavItems.DesiredSize.Width > avail)
            SetNavLabels(full: false, orbital: false);
    }

    private void SetNavLabels(bool full, bool orbital)
    {
        (Button Btn, string Full, string Compact, string Orbital)[] items =
        {
            (NavPainel, "◈ Painel", "◈", "01 PAN"),
            (NavHoje, "☀ Hoje", "☀", "02 HOJ"),
            (NavKanban, "◱ Kanban", "◱", "03 KBN"),
            (NavAgenda, "◷ Agenda", "◷", "04 AGD"),
            (NavLinks, "⌘ Links", "⌘", "05 LNK"),
            (NavListas, "☰ Listas", "☰", "06 LST"),
            (NavNews, "✷ Noticias", "✷", "07 NEW"),
            (NavContas, "$ Contas", "$", "08 CTS"),
            (NavEmail, "✉ Email", "✉", "09 EML"),
        };

        foreach (var (btn, longText, compact, orb) in items)
        {
            btn.Content = orbital ? orb : full ? longText : compact;
            btn.ToolTip = longText;
            btn.Padding = full ? new Thickness(12, 7, 12, 7) : orbital ? new Thickness(9, 7, 9, 7) : new Thickness(10, 7, 10, 7);
            btn.MinWidth = full ? 0 : orbital ? 58 : 34;
        }
    }

    private void ShowStatus(string msg, bool error = false)
    {
        StatusText.Text = msg;
        StatusText.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A))
            : (Brush)FindResource("Accent");
        StatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // -- Entrada com 2 modos: captura � busca --

    private static readonly (string Mode, string Icon, string Label, string HintText)[] Modes =
    {
        ("captura", "+", "captura", "esvazia a cabeca - Enter joga na captura, voce decide depois..."),
        ("busca", "B", "busca", "busca em tudo - tarefas, agenda, notas, listas..."),
    };

    private readonly Dictionary<string, Button> _modeBtns = new();

    private void BuildModeRow()
    {
        ModeRow.Children.Clear();
        _modeBtns.Clear();
        foreach (var (mode, icon, label, _) in Modes)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                FontSize = 10.5,
                Padding = new Thickness(10, 4, 10, 4),
                Background = Brushes.Transparent
            };
            b.Click += (_, _) => { SetInputMode(mode); Input.Focus(); };
            _modeBtns[mode] = b;
            ModeRow.Children.Add(b);
        }
        SetInputMode("captura");
    }

    private void SetInputMode(string mode)
    {
        _inputMode = mode;
        var accent = (Brush)FindResource("Accent");
        var ink = (Brush)FindResource("TextInk");
        var txt = (Brush)FindResource("TextMain");
        foreach (var (m, icon, _, hint) in Modes)
        {
            if (m != mode) continue;
            ModeIcon.Text = icon;
            if (Input.Text.Length == 0) Hint.Text = hint;
        }
        foreach (var (m, btn) in _modeBtns)
        {
            bool on = m == mode;
            btn.Background = on ? accent : Brushes.Transparent;
            btn.Foreground = on ? ink : txt;
        }
    }

    private static string DayLabel(DateTime d)
    {
        var hoje = DateTime.Now.Date;
        if (d == hoje) return "hoje";
        if (d == hoje.AddDays(1)) return "amanha";
        var culture = new CultureInfo("pt-BR");
        return $"{culture.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).TrimEnd('.')} {d:dd/MM}";
    }

    // -- Seletor de data caprichado (mini-calend�rio) ---------------

    private DateTime _pickerMonth;
    private DateTime _pickerSelected;
    private Action<DateTime>? _pickerCallback;

    private void OpenDatePicker(FrameworkElement target, DateTime current, Action<DateTime> onPick)
    {
        _pickerSelected = current.Date;
        _pickerMonth = new DateTime(current.Year, current.Month, 1);
        _pickerCallback = onPick;
        DatePopup.PlacementTarget = target;
        RenderMiniCalendar();
        DatePopup.IsOpen = true;
    }

    private void RenderMiniCalendar()
    {
        DateCalHost.Children.Clear();
        var culture = new CultureInfo("pt-BR");
        var hoje = DateTime.Now.Date;

        // Cabe�alho: � M�s Ano �
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        Button NavBtn(string g)
        {
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"), Content = g, FontSize = 14,
                Padding = new Thickness(8, 2, 8, 2), Foreground = (Brush)FindResource("TextMain")
            };
            return b;
        }
        var prev = NavBtn("<"); DockPanel.SetDock(prev, Dock.Left);
        var next = NavBtn(">"); DockPanel.SetDock(next, Dock.Right);
        prev.Click += (_, _) => { _pickerMonth = _pickerMonth.AddMonths(-1); RenderMiniCalendar(); };
        next.Click += (_, _) => { _pickerMonth = _pickerMonth.AddMonths(1); RenderMiniCalendar(); };
        head.Children.Add(prev);
        head.Children.Add(next);
        head.Children.Add(new TextBlock
        {
            Text = culture.TextInfo.ToTitleCase(_pickerMonth.ToString("MMMM yyyy", culture)),
            FontSize = 13.5, FontFamily = (FontFamily)FindResource("Display"),
            Foreground = (Brush)FindResource("TextMain"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        DateCalHost.Children.Add(head);

        // Cabe�alho dos dias da semana
        var wk = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 3) };
        foreach (var w in new[] { "s", "t", "q", "q", "s", "s", "d" })
            wk.Children.Add(new TextBlock
            {
                Text = w, FontSize = 10, FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextDone"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        DateCalHost.Children.Add(wk);

        // Grade de dias (segunda-first)
        var grid = new UniformGrid { Columns = 7 };
        int offset = ((int)_pickerMonth.DayOfWeek + 6) % 7;
        int dias = DateTime.DaysInMonth(_pickerMonth.Year, _pickerMonth.Month);
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
        for (int dia = 1; dia <= dias; dia++)
        {
            var d = new DateTime(_pickerMonth.Year, _pickerMonth.Month, dia);
            bool ehHoje = d == hoje, ehSel = d == _pickerSelected;
            var cell = new Border
            {
                Width = 34, Height = 32, Margin = new Thickness(1),
                CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                Background = ehSel ? (Brush)FindResource("Accent") : Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                BorderBrush = ehHoje && !ehSel ? (Brush)FindResource("Accent") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = dia.ToString(), FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource(ehSel ? "TextInk" : "TextMain")
                }
            };
            var dd = d;
            cell.MouseEnter += (_, _) => { if (dd != _pickerSelected) cell.Background = (Brush)FindResource("ChipBg"); };
            cell.MouseLeave += (_, _) => { if (dd != _pickerSelected) cell.Background = Brushes.Transparent; };
            cell.MouseLeftButtonUp += (_, _) => { DatePopup.IsOpen = false; _pickerCallback?.Invoke(dd); };
            grid.Children.Add(cell);
        }
        DateCalHost.Children.Add(grid);

        // Atalhos r�pidos
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        void Q(string label, DateTime d)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"), Content = label, FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4)
            };
            b.Click += (_, _) => { DatePopup.IsOpen = false; _pickerCallback?.Invoke(d); };
            quick.Children.Add(b);
        }
        Q("hoje", hoje);
        Q("amanha", hoje.AddDays(1));
        DateCalHost.Children.Add(quick);
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
        => Hint.Visibility = string.IsNullOrEmpty(Input.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        e.Handled = true;

        if (_inputMode == "busca")
        {
            _buscaQuery = text;
            SwitchView("Busca");
            return;
        }

        try
        {
            var box = await GetKvArray("me2_inbox");
            box.Insert(0, new JsonObject { ["id"] = Supa.NewId(), ["text"] = text });
            await SetKvArray("me2_inbox", box);
            Input.Clear();
            ShowStatus("capturado - decide o destino no painel");
            if (_currentView == "Painel") await LoadPainel();
        }
        catch (Exception ex) { ShowStatus("nao capturou: " + ex.Message, error: true); }
    }

    // -- PAINEL: panorama � a estrela; captura fica compacta embaixo -

    private async Task LoadPainel()
    {
        try
        {
            var focoT = Supa.GetKv("me2_foco");
            var inboxT = GetKvArray("me2_inbox");
            var tarefasT = Supa.Select("tarefas?select=id,titulo,status,prazo&order=created_at.desc");
            var ritmoT = Supa.GetKv("me2_ritmo");
            await Task.WhenAll(focoT, inboxT, tarefasT, ritmoT);
            _foco = await RolloverFoco(focoT.Result is string fs ? JsonNode.Parse(fs) as JsonObject : null);
            _ritmo = ritmoT.Result is string rs ? JsonNode.Parse(rs) as JsonObject : null;
            _tarefasCache = tarefasT.Result;
            RenderPainel(inboxT.Result);
        }
        catch
        {
            PainelPanel.Children.Clear();
            PainelPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private void RenderPainel(JsonArray inbox)
    {
        PainelPanel.Children.Clear();
        var hoje = DateTime.Now.Date;
        var culture = new CultureInfo("pt-BR");

        int tot = 0, feitas = 0;
        var pend = new Dictionary<string, int> { ["big"] = 0, ["med"] = 0, ["small"] = 0 };
        foreach (var (arr, _, _, _) in Niveis)
            if (_foco?[arr] is JsonArray a)
                foreach (var x in a)
                {
                    tot++;
                    if (x?["done"]?.GetValue<bool>() == true) feitas++;
                    else pend[arr]++;
                }

        int h24 = DateTime.Now.Hour;
        string saud = h24 < 5 ? "boa madrugada" : h24 < 12 ? "bom dia" : h24 < 18 ? "boa tarde" : "boa noite";

        // -- Hero neobrutal: saudacao (display pesada) com "Pedro" em marca-texto --
        var hero = new StackPanel { Margin = new Thickness(2, 0, 2, 15) };
        var heroLine = new TextBlock
        {
            FontSize = 29,
            FontFamily = (FontFamily)FindResource("Display"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Ink"),
            TextWrapping = TextWrapping.Wrap
        };
        heroLine.Inlines.Add(new Run(saud + ", "));
        // "Pedro" num bloco de accent (marca-texto), estilo neobrutal
        var marca = new InlineUIContainer(new Border
        {
            Background = (Brush)FindResource("Accent"),
            BorderBrush = (Brush)FindResource("Ink"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 0, 8, 1),
            Child = new TextBlock
            {
                Text = "Pedro", FontSize = 29, FontFamily = (FontFamily)FindResource("Display"),
                FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextInk")
            }
        }) { BaselineAlignment = BaselineAlignment.Center };
        heroLine.Inlines.Add(marca);
        hero.Children.Add(heroLine);
        hero.Children.Add(new TextBlock
        {
            Text = culture.TextInfo.ToTitleCase(hoje.ToString("dddd, dd 'de' MMMM", culture)).ToUpper(culture),
            FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("Mono"),
            Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(2, 6, 0, 0)
        });
        PainelPanel.Children.Add(hero);

        // -- Faixa "pode gastar hoje" (do contas.pedro), preenchida async --
        var contasHost = new ContentControl { Margin = new Thickness(0, 0, 0, 4) };
        PainelPanel.Children.Add(contasHost);
        _ = FillContasHome(contasHost);

        // -- TOPO: CAPTURA R�PIDA (o principal) --
        var capHead = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        var capCount = new TextBlock
        {
            Text = inbox.Count == 0 ? "vazia" : $"{inbox.Count}",
            FontSize = 10, FontFamily = (FontFamily)FindResource("Mono"),
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(capCount, Dock.Right);
        capHead.Children.Add(capCount);
        capHead.Children.Add(HudLabel("CAPTURA RAPIDA"));
        PainelPanel.Children.Add(capHead);

        if (inbox.Count == 0)
            PainelPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("Surface"),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock
                {
                    Text = "cabeca vazia - joga qualquer coisa na barra la em cima, aperta Enter e decide o destino aqui.",
                    FontSize = 12.5, Foreground = (Brush)FindResource("TextDone"),
                    TextWrapping = TextWrapping.Wrap, LineHeight = 20
                }
            });
        else
        {
            var capWrap = new UniformGrid
            {
                Columns = ResponsiveColumns(minItemWidth: 390, maxColumns: 3),
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var node in inbox)
                if (node is JsonObject item)
                    capWrap.Children.Add(CapturaItem(item, compact: true));
            PainelPanel.Children.Add(capWrap);
        }

        // -- EMBAIXO: HOJE (tarefas de verdade) � PR�XIMOS EVENTOS --
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // -- HOJE: lista as tarefas por n�vel --
        var hojeBody = new StackPanel();
        var hojeHead = new DockPanel();
        var placar = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        placar.Inlines.Add(new Run($"{feitas}") { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Accent") });
        placar.Inlines.Add(new Run($"/{tot}") { FontSize = 11, Foreground = (Brush)FindResource("TextDim") });
        DockPanel.SetDock(placar, Dock.Right);
        hojeHead.Children.Add(placar);
        hojeHead.Children.Add(HudLabel("HOJE"));
        hojeBody.Children.Add(hojeHead);
        hojeBody.Children.Add(ProgressBarZ(tot == 0 ? 0 : (double)feitas / tot, tall: true));

        var tasksWrap = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        int mostradas = 0;
        foreach (var (arr, _, corKey, _) in Niveis)
            if (_foco?[arr] is JsonArray items)
                foreach (var node in items)
                    if (node is JsonObject item && mostradas < 7)
                    {
                        bool done = item["done"]?.GetValue<bool>() == true;
                        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        line.Children.Add(new Border
                        {
                            Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5),
                            Background = (Brush)FindResource(corKey), Margin = new Thickness(2, 0, 9, 0),
                            VerticalAlignment = VerticalAlignment.Center, Opacity = done ? 0.4 : 1
                        });
                        line.Children.Add(new TextBlock
                        {
                            Text = item["text"]?.GetValue<string>() ?? "", FontSize = 12.5,
                            TextDecorations = done ? TextDecorations.Strikethrough : null,
                            Foreground = (Brush)FindResource(done ? "TextDone" : "TextMain"),
                            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                            MaxWidth = 260
                        });
                        tasksWrap.Children.Add(line);
                        mostradas++;
                    }
        if (mostradas == 0)
            tasksWrap.Children.Add(new TextBlock { Text = "nada no plano ainda", FontSize = 12, Foreground = (Brush)FindResource("TextDone") });
        else if (tot > mostradas)
            tasksWrap.Children.Add(new TextBlock { Text = $"+{tot - mostradas} no plano", FontSize = 11, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(18, 2, 0, 0) });
        hojeBody.Children.Add(tasksWrap);
        Grid.SetColumn(GlassCardInto(grid, 0, hojeBody, "Hoje", tintIndex: 0), 0);

        // -- PR�XIMOS EVENTOS: eventos + recorrentes (cor diferente), mesmo formato --
        var proxBody = new StackPanel();
        proxBody.Children.Add(HudLabel("PROXIMOS EVENTOS"));
        var proxWrap = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var eventos = new List<(DateTime D, string Titulo, bool Recur, JsonObject? T)>();
        foreach (var t in _tarefasCache.OfType<JsonObject>())
            if (t["prazo"]?.GetValue<string>() is string p && DateTime.TryParse(p, out var d) && d >= hoje)
                eventos.Add((d, t["titulo"]?.GetValue<string>() ?? "", false, t));
        for (int i = 0; i < 45; i++)
            foreach (var texto in RecurDoDia(hoje.AddDays(i)))
                eventos.Add((hoje.AddDays(i), texto, true, null));

        foreach (var (d, titulo, recur, t) in eventos.OrderBy(e => e.D).Take(6))
            proxWrap.Children.Add(EventoRow(d, titulo, recur, t));
        if (proxWrap.Children.Count == 0)
            proxWrap.Children.Add(new TextBlock { Text = "nada marcado pela frente", FontSize = 12, Foreground = (Brush)FindResource("TextDone") });
        proxBody.Children.Add(proxWrap);
        Grid.SetColumn(GlassCardInto(grid, 1, proxBody, "Agenda", tintIndex: 4), 1);

        PainelPanel.Children.Add(grid);
        AnimateIn(PainelPanel);
    }

    /// <summary>Linha de evento com selo de data. Recorrentes ganham cor pr�pria + ?.</summary>
    private UIElement EventoRow(DateTime d, string titulo, bool recur, JsonObject? tarefa)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Cursor = recur ? Cursors.Arrow : Cursors.Hand };
        var selo = new Border
        {
            Background = recur ? (Brush)FindResource("BlockBlue") : (Brush)FindResource("Accent"),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = d.ToString("dd/MM"), FontSize = 10, FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextInk")
            }
        };
        DockPanel.SetDock(selo, Dock.Left);
        row.Children.Add(selo);
        var tb = new TextBlock
        {
            FontSize = 12.5, Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        tb.Inlines.Add(new Run(titulo));
        row.Children.Add(tb);
        if (!recur && tarefa is not null)
            row.MouseLeftButtonUp += (_, ev) => { OpenKbEdit(tarefa); ev.Handled = true; };
        return row;
    }

    private FrameworkElement GlassCardInto(Grid g, int col, UIElement body, string? gotoView, int? tintIndex = null)
    {
        var card = Zui.GlassCard(this, body, gotoView is null ? null : () => SwitchView(gotoView), tintIndex: tintIndex);
        card.Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 0 ? 6 : 0, 0);
        Grid.SetColumn(card, col);
        g.Children.Add(card);
        return card;
    }

    private static void PlacePanel(Grid g, int row, int col, UIElement el)
    {
        Grid.SetRow(el, row); Grid.SetColumn(el, col);
        g.Children.Add(el);
    }

    /// <summary>R�tulo HUD: mono, mai�sculo, espa�ado, com brilho suave.</summary>
    private TextBlock HudLabel(string text) => Zui.HudLabel(this, text);

    /// <summary>Bloco neobrutal (sombra dura por borda dupla); hover realca a borda ao abrir aba.</summary>
    private FrameworkElement GlassCard(UIElement body, string? gotoView)
    {
        return Zui.GlassCard(this, body, gotoView is null ? null : () => SwitchView(gotoView));
    }

    private int ResponsiveColumns(double minItemWidth, int maxColumns)
    {
        var width = ViewHost.ActualWidth > 1 ? ViewHost.ActualWidth : ActualWidth - 60;
        width = Math.Max(minItemWidth, width - 18);
        return Math.Max(1, Math.Min(maxColumns, (int)Math.Floor(width / minItemWidth)));
    }

    private FrameworkElement StatCard(string title, UIElement body, string? gotoView)
        => Zui.StatCard(this, title, body, gotoView is null ? null : () => SwitchView(gotoView));

    // -- Player no topo, ao lado do campo de texto ------------------

    private async Task RenderTopPlayer()
    {
        if (!IsVisible) return;
        var np = await MediaCtl.Get();
        if (np is null) { PlayerBar.Visibility = Visibility.Collapsed; return; }

        PlayerContent.Children.Clear();
        PlayerBar.Visibility = Visibility.Visible;

        // Barrinha de equalizer minimalista (3 tra�os) pulsando quando toca
        var eq = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        foreach (var h in new[] { 7.0, 12.0, 9.0 })
            eq.Children.Add(new Border
            {
                Width = 2.5, Height = np.Playing ? h : 4,
                CornerRadius = new CornerRadius(1.5),
                Background = (Brush)FindResource("Accent"),
                Margin = new Thickness(1, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        PlayerContent.Children.Add(eq);

        var title = new TextBlock
        {
            Text = np.Title, FontSize = 11.5,
            Foreground = (Brush)FindResource("TextDim"), MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = np.Title + (np.Artist.Length > 0 ? " - " + np.Artist : "")
        };
        PlayerContent.Children.Add(title);

        void Ctl(string label, Func<Task> act, string tip)
        {
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"),
                Content = label, FontSize = 11,
                Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(2, 0, 0, 0),
                Foreground = (Brush)FindResource("TextDim"), ToolTip = tip
            };
            b.Click += async (_, _) => { await act(); await Task.Delay(280); _ = RenderTopPlayer(); };
            PlayerContent.Children.Add(b);
        }
        Ctl("⏮", MediaCtl.Prev, "anterior");
        Ctl(np.Playing ? "⏸" : "▶", MediaCtl.Toggle, np.Playing ? "pausar" : "tocar");
        Ctl("⏭", MediaCtl.Next, "proxima");
    }

    // -- Captura: item da inbox com destinos (vive no Painel) ------

    private FrameworkElement CapturaItem(JsonObject item, bool compact = false)
    {
        string id = item["id"]?.GetValue<string>() ?? "";
        string text = item["text"]?.GetValue<string>() ?? "";

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        void Act(string label, Func<Task> send)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                FontSize = compact ? 10.5 : 11,
                Padding = new Thickness(compact ? 7 : 9, 3, compact ? 7 : 9, 3),
                Margin = new Thickness(0, 0, compact ? 5 : 8, 0),
                Background = Brushes.Transparent
            };
            b.Click += async (_, _) =>
            {
                try
                {
                    await send();
                    await RemoveInbox(id);
                    ShowStatus("enviado " + label);
                    await LoadPainel();
                }
                catch (Exception ex) { ShowStatus(ex.Message, error: true); }
            };
            acts.Children.Add(b);
        }

        Act("plano", () => AddHojeItem("med", text));
        Act("tarefa", () => AddTarefa(text));
        Act("norte", () => PushKvList("me2_sparks", new JsonObject
        { ["id"] = Supa.NewId(), ["text"] = text, ["cat"] = "criativa", ["ts"] = Ms() }));

        var del = new Button
        {
            Style = (Style)FindResource("Chip"),
            Content = "x",
            FontSize = compact ? 10.5 : 11,
            Padding = new Thickness(compact ? 6 : 8, 3, compact ? 6 : 8, 3),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = "descartar"
        };
        del.Click += async (_, _) => { await RemoveInbox(id); await LoadPainel(); };
        acts.Children.Add(del);

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = compact ? 12.5 : 14,
            Foreground = (Brush)FindResource("TextMain"),
            TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(acts);

        // Card padrão: branco (paper) com sombra dura, igual aos outros cards
        return Zui.Block(this, sp,
            background: (Brush)FindResource("Surface"),
            padding: new Thickness(12, 9, 11, 9),
            margin: compact ? new Thickness(0, 0, 10, 10) : new Thickness(0, 0, 4, 8),
            radius: 12);
    }

    private static async Task RemoveInbox(string id)
    {
        var box = await GetKvArray("me2_inbox");
        var novo = new JsonArray();
        foreach (var n in box.ToList())
            if (n is JsonObject o && o["id"]?.GetValue<string>() != id)
                novo.Add(n.DeepClone());
        await SetKvArray("me2_inbox", novo);
    }

    // -- BUSCA INTERNA ----------------------------------------------

    private async Task LoadBusca()
    {
        BuscaPanel.Children.Clear();
        string q = _buscaQuery.Trim();
        if (q.Length == 0)
        {
            BuscaPanel.Children.Add(DimText("digita no campo la em cima com o modo busca ligado"));
            return;
        }
        BuscaPanel.Children.Add(SectionLabel($"BUSCA - \"{q}\""));
        var carregando = DimText("procurando...");
        BuscaPanel.Children.Add(carregando);

        try
        {
            string safe = Regex.Replace(q, @"[,()*\\]", " ").Trim();
            string pat = Uri.EscapeDataString("*" + safe + "*");

            var tarefasT = Supa.Select($"tarefas?select=id,titulo,status,prazo&titulo=ilike.{pat}&limit=12");
            var notasT = Supa.Select("notas?select=id,titulo,corpo&or=" +
                Uri.EscapeDataString($"(titulo.ilike.*{safe}*,corpo.ilike.*{safe}*)") + "&limit=10");
            var zrefsT = Supa.Select("zimbar_refs?select=id,kind,title,content&or=" +
                Uri.EscapeDataString($"(title.ilike.*{safe}*,content.ilike.*{safe}*)") + "&limit=10");
            var ideiasT = GetKvArray("me2_ideias");
            var listasT = Supa.Select("mural_items?select=categoria,texto");
            var inboxT = GetKvArray("me2_inbox");
            var focoT = Supa.GetKv("me2_foco");
            await Task.WhenAll(tarefasT, notasT, zrefsT, ideiasT, listasT, inboxT, focoT);

            BuscaPanel.Children.Remove(carregando);
            bool Match(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
            int achou = 0;

            // Plano de hoje
            var foco = focoT.Result is string fs ? JsonNode.Parse(fs) as JsonObject : null;
            var doPlano = new List<string>();
            foreach (var (arr, titulo, _, _) in Niveis)
                if (foco?[arr] is JsonArray a)
                    foreach (var x in a)
                        if (x is JsonObject o && Match(o["text"]?.GetValue<string>()))
                            doPlano.Add($"{o["text"]!.GetValue<string>()}  ({titulo.ToLower(new CultureInfo("pt-BR"))})");
            if (doPlano.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("PLANO DE HOJE"));
                foreach (var s in doPlano) { BuscaPanel.Children.Add(BuscaRow("-", s, null, () => SwitchView("Hoje"))); achou++; }
            }

            // Kanban / agenda
            if (tarefasT.Result.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("KANBAN / AGENDA"));
                foreach (var node in tarefasT.Result)
                    if (node is JsonObject t)
                    {
                        string meta = t["status"]?.GetValue<string>() ?? "";
                        if (t["prazo"]?.GetValue<string>() is string p && DateTime.TryParse(p, out var d))
                            meta += "  -  " + d.ToString("dd/MM");
                        var tt = t;
                        BuscaPanel.Children.Add(BuscaRow("-", t["titulo"]?.GetValue<string>() ?? "", meta, () => OpenKbEdit(tt)));
                        achou++;
                    }
            }

            // Notas
            if (notasT.Result.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("NOTAS (abre o ZimNotes)"));
                foreach (var node in notasT.Result)
                    if (node is JsonObject n)
                    {
                        BuscaPanel.Children.Add(BuscaRow("nota", n["titulo"]?.GetValue<string>() ?? "(sem titulo)", null,
                            () => { HideBar(); NotesWindow.Open(); }));
                        achou++;
                    }
            }

            // Refer�ncias (ideias + links salvos)
            var refsAchadas = new List<(string Texto, string? Meta, Action Acao)>();
            foreach (var node in ideiasT.Result)
                if (node is JsonObject r && Match(r["text"]?.GetValue<string>()))
                {
                    string texto = r["text"]!.GetValue<string>();
                    refsAchadas.Add((texto, r["cat"]?.GetValue<string>() is string c && c.Length > 0 ? "#" + c : null, () =>
                    {
                        if (LooksLikeUrl(texto)) { OpenExternal(texto.Contains("://") ? texto : "https://" + texto); HideBar(); }
                        else { Clipboard.SetText(texto); ShowStatus("copiado"); }
                    }));
                }
            foreach (var node in zrefsT.Result)
                if (node is JsonObject r)
                {
                    string content = r["content"]?.GetValue<string>() ?? "";
                    string title = r["title"]?.GetValue<string>() ?? "";
                    string texto = title.Length > 0 ? title : content;
                    bool ehLink = (r["kind"]?.GetValue<string>() ?? "link") == "link";
                    refsAchadas.Add((texto, "links", () =>
                    {
                        if (ehLink) { OpenExternal(content); HideBar(); }
                        else { Clipboard.SetText(content); ShowStatus("copiado"); }
                    }));
                }
            if (refsAchadas.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("REFERENCIAS"));
                foreach (var (texto, meta, acao) in refsAchadas)
                { BuscaPanel.Children.Add(BuscaRow("ref", texto, meta, acao)); achou++; }
            }

            // Listas (mural)
            var deListas = new List<string>();
            foreach (var node in listasT.Result)
                if (node is JsonObject it && Match(it["texto"]?.GetValue<string>()))
                    deListas.Add($"{it["categoria"]?.GetValue<string>()}  -  {it["texto"]!.GetValue<string>()}");
            if (deListas.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("LISTAS"));
                foreach (var s in deListas) { BuscaPanel.Children.Add(BuscaRow("-", s, null, () => SwitchView("Listas"))); achou++; }
            }

            // Captura
            var daCaptura = inboxT.Result.OfType<JsonObject>()
                .Where(o => Match(o["text"]?.GetValue<string>()))
                .Select(o => o["text"]!.GetValue<string>()).ToList();
            if (daCaptura.Count > 0)
            {
                BuscaPanel.Children.Add(SectionLabel("NA CAPTURA"));
                foreach (var s in daCaptura) { BuscaPanel.Children.Add(BuscaRow("-", s, null, () => SwitchView("Painel"))); achou++; }
            }

            if (achou == 0)
                BuscaPanel.Children.Add(DimText($"nada com \"{q}\" - tenta outra palavra"));
        }
        catch
        {
            BuscaPanel.Children.Remove(carregando);
            BuscaPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private Button BuscaRow(string icon, string text, string? meta, Action onClick)
    {
        var row = new DockPanel();
        if (meta is not null)
        {
            var m = new TextBlock
            {
                Text = meta,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextDone"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };
            DockPanel.SetDock(m, Dock.Right);
            row.Children.Add(m);
        }
        row.Children.Add(new TextBlock
        {
            Text = $"{icon}  {text}",
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var b = new Button { Style = (Style)FindResource("GhostItem"), Content = row };
        b.Click += (_, _) => onClick();
        return b;
    }

    // -- HOJE: dif�cil / m�dia / f�cil com contraste ----------------

    private async Task LoadHoje()
    {
        try
        {
            _hojeRenameId = null;
            var focoT = Supa.GetKv("me2_foco");
            var ritmoT = Supa.GetKv("me2_ritmo");
            await Task.WhenAll(focoT, ritmoT);
            _foco = focoT.Result is null ? null : JsonNode.Parse(focoT.Result) as JsonObject;
            _foco = await RolloverFoco(_foco);
            _ritmo = ritmoT.Result is string rs ? JsonNode.Parse(rs) as JsonObject : null;
            RenderHoje();
        }
        catch
        {
            HojePanel.Children.Clear();
            HojePanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    /// <summary>
    /// Virada de dia igual � do site: arquiva o placar de ontem no me2_arch e
    /// carrega os itens N�O feitos pra hoje (em vez de descartar tudo).
    /// </summary>
    private static async Task<JsonObject> RolloverFoco(JsonObject? f)
    {
        string hoje = Today();
        if (f is null)
            return new JsonObject
            {
                ["date"] = hoje,
                ["frog"] = new JsonObject { ["text"] = "", ["done"] = false },
                ["big"] = new JsonArray(),
                ["med"] = new JsonArray(),
                ["small"] = new JsonArray()
            };
        if (f["date"]?.GetValue<string>() == hoje) return f;

        // Arquiva o dia anterior (melhor esfor�o)
        try
        {
            int tot = 0, done = 0;
            foreach (var arr in new[] { "big", "med", "small" })
                if (f[arr] is JsonArray a)
                    foreach (var x in a)
                    {
                        tot++;
                        if (x?["done"]?.GetValue<bool>() == true) done++;
                    }
            if (tot > 0)
            {
                var archRaw = await Supa.GetKv("me2_arch");
                var arch = archRaw is null ? null : JsonNode.Parse(archRaw) as JsonObject;
                arch ??= new JsonObject { ["week"] = Monday(DateTime.Now.Date).ToString("yyyy-MM-dd"), ["days"] = new JsonObject(), ["history"] = new JsonArray() };
                if (arch["days"] is not JsonObject) arch["days"] = new JsonObject();
                (arch["days"] as JsonObject)![f["date"]?.GetValue<string>() ?? hoje] = new JsonObject
                {
                    ["done"] = done,
                    ["total"] = tot,
                    ["frog"] = f["frog"]?["done"]?.GetValue<bool>() == true
                };
                await Supa.SetKv("me2_arch", arch.ToJsonString());
            }
        }
        catch { }

        // N�o feitos v�m junto pra hoje
        foreach (var arrName in new[] { "big", "med", "small" })
        {
            var pendentes = new JsonArray();
            if (f[arrName] is JsonArray a)
                foreach (var x in a.ToList())
                    if (x is JsonObject o && o["done"]?.GetValue<bool>() != true)
                        pendentes.Add(o.DeepClone());
            f[arrName] = pendentes;
        }
        if (f["frog"]?["done"]?.GetValue<bool>() == true)
            f["frog"] = new JsonObject { ["text"] = "", ["done"] = false };

        f["date"] = hoje;
        await Supa.SetKv("me2_foco", f.ToJsonString());
        return f;
    }

    private static readonly (string Arr, string Titulo, string CorKey, string BgKey)[] Niveis =
    {
        ("big", "DIFICEIS", "Dificil", "DificilBg"),
        ("med", "MEDIAS", "Media", "MediaBg"),
        ("small", "FACEIS", "Facil", "FacilBg"),
    };

    private string? _hojeRenameId;

    private void RenderHoje()
    {
        HojePanel.Children.Clear();
        bool isToday = _foco?["date"]?.GetValue<string>() == Today();

        var ink = (Brush)FindResource("Ink");
        foreach (var (arr, titulo, corKey, bgKey) in Niveis)
        {
            var section = new StackPanel();
            // Etiqueta do nivel: bolinha de cor + nome em mono (texto tinta, legivel)
            var tag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            tag.Children.Add(new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource(corKey), BorderBrush = ink, BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            });
            tag.Children.Add(new TextBlock
            {
                Text = titulo, FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = ink, VerticalAlignment = VerticalAlignment.Center
            });
            section.Children.Add(tag);

            int n = 0;
            if (isToday && _foco?[arr] is JsonArray items)
                foreach (var node in items)
                    if (node is JsonObject item)
                    {
                        section.Children.Add(HojeItem(item, corKey, arr));
                        n++;
                    }
            if (n == 0)
                section.Children.Add(new TextBlock
                {
                    Text = "arrasta tarefas pra ca", FontSize = 11,
                    Foreground = (Brush)FindResource("TextDim"),
                    Margin = new Thickness(6, 0, 0, 2)
                });

            // Bloco neobrutal: pastel opaco da cor do nivel + borda de tinta + sombra dura
            var levelCol = ((SolidColorBrush)FindResource(corKey)).Color;
            byte Mix(byte c) => (byte)(255 - (255 - c) * 0.30);
            var pastel = new SolidColorBrush(Color.FromRgb(Mix(levelCol.R), Mix(levelCol.G), Mix(levelCol.B)));
            var secBorder = new Border
            {
                Background = pastel,
                BorderBrush = ink,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 11, 13, 11),
                Margin = new Thickness(0, 0, 4, 0),
                AllowDrop = true,
                Tag = arr,
                Child = section
            };
            secBorder.DragEnter += (_, _) => secBorder.BorderBrush = (Brush)FindResource("AccentSoft");
            secBorder.DragLeave += (_, _) => secBorder.BorderBrush = ink;
            secBorder.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            secBorder.Drop += async (_, e) =>
            {
                secBorder.BorderBrush = ink;
                if (e.Data.GetData(DataFormats.Text) is string dragId) await MoveHojeById(dragId, arr, null);
            };
            var sombra = new Border { Background = ink, CornerRadius = new CornerRadius(10), Margin = new Thickness(4, 4, 0, 0) };
            HojePanel.Children.Add(new Grid
            {
                Margin = new Thickness(0, 0, 0, 9),
                SnapsToDevicePixels = true,
                Children = { sombra, secBorder }
            });
        }

        // Adi��o: seletor de n�vel (blocos) + bot�o discreto que revela o campo
        var addRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var levelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(levelRow, Dock.Right);
        foreach (var (arr, _, corKey, _) in Niveis)
        {
            var dot = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = (Brush)FindResource(corKey),
                BorderBrush = (Brush)FindResource("Ink"),
                BorderThickness = new Thickness(_hojeTarget == arr ? 2.5 : 1),
                Opacity = _hojeTarget == arr ? 1.0 : 0.4,
                Tag = arr,
                ToolTip = arr == "big" ? "dificil" : arr == "med" ? "media" : "facil"
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _hojeTarget = arr;
                foreach (var child in levelRow.Children.OfType<Border>())
                {
                    bool on = (child.Tag as string) == _hojeTarget;
                    child.BorderThickness = new Thickness(on ? 2.5 : 1);
                    child.Opacity = on ? 1.0 : 0.4;
                }
            };
            levelRow.Children.Add(dot);
        }
        addRow.Children.Add(levelRow);
        addRow.Children.Add(RevealAdd("+ tarefa do dia", "escolhe o nivel na cor ao lado e digita", async text =>
        {
            await AddHojeItem(_hojeTarget, text);
            await LoadHoje();
        }));
        HojePanel.Children.Add(addRow);

        // -- Ritmo de hoje: chips marc�veis (abaixo do plano) --
        HojePanel.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 12, 0, 10), Opacity = 0.35,
            Background = (Brush)FindResource("AccentSoft")
        });
        HojePanel.Children.Add(SectionLabel("RITMO DE HOJE - toca pra marcar"));
        var ritmoWrap = new WrapPanel { Margin = new Thickness(2, 2, 0, 0) };
        string hojeKey = Today();
        if (_ritmo?["items"] is JsonArray habitos && habitos.Count > 0)
            foreach (var node in habitos)
                if (node is JsonObject hb)
                {
                    var days = hb["days"] as JsonObject ?? new JsonObject();
                    bool feito = days[hojeKey]?.GetValue<bool>() == true;
                    var chip = new Border
                    {
                        Background = feito ? (Brush)FindResource("Accent") : (Brush)FindResource("ChipBg"),
                        BorderBrush = feito ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(11, 5, 11, 5),
                        Margin = new Thickness(0, 0, 7, 7),
                        Cursor = Cursors.Hand,
                        ToolTip = feito ? "feito hoje - clica pra desmarcar" : "clica quando fizer hoje",
                        Child = new TextBlock
                        {
                            Text = (feito ? "✓ " : "• ") + (hb["text"]?.GetValue<string>() ?? ""),
                            FontSize = 12,
                            Foreground = (Brush)FindResource(feito ? "TextInk" : "TextMain")
                        }
                    };
                    var hh = hb;
                    chip.MouseLeftButtonUp += async (_, ev) =>
                    {
                        ev.Handled = true;
                        var dd = hh["days"] as JsonObject ?? new JsonObject();
                        if (dd[hojeKey]?.GetValue<bool>() == true) dd.Remove(hojeKey);
                        else dd[hojeKey] = true;
                        hh["days"] = dd;
                        RenderHoje();
                        try { await Supa.SetKv("me2_ritmo", _ritmo!.ToJsonString()); }
                        catch { ShowStatus("nao sincronizou o ritmo", error: true); }
                    };
                    ritmoWrap.Children.Add(chip);
                }
        else ritmoWrap.Children.Add(new TextBlock { Text = "sem habitos no ritmo", FontSize = 12, Foreground = (Brush)FindResource("TextDone") });
        HojePanel.Children.Add(ritmoWrap);
    }

    private FrameworkElement HojeItem(JsonObject item, string corKey, string arr)
    {
        bool done = item["done"]?.GetValue<bool>() ?? false;
        string text = item["text"]?.GetValue<string>() ?? "";
        string id = item["id"]?.GetValue<string>() ?? "";

        // Renomeando este? Vira campo inline.
        if (_hojeRenameId == id)
        {
            var box = new TextBox { Style = (Style)FindResource("InlineAdd"), Text = text, FontSize = 13 };
            box.Margin = new Thickness(0, 1, 0, 1);
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) { _hojeRenameId = null; RenderHoje(); e.Handled = true; return; }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                var novo = box.Text.Trim();
                _hojeRenameId = null;
                if (novo.Length > 0) item["text"] = novo;
                RenderHoje();
                try { await Supa.SetKv("me2_foco", _foco!.ToJsonString()); }
                catch { ShowStatus("nao sincronizou", error: true); }
            };
            return box;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = done ? "✓  " : "•  ",
            Foreground = (Brush)FindResource(corKey),
            FontSize = 13.5
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            TextDecorations = done ? TextDecorations.Strikethrough : null,
            Foreground = (Brush)FindResource(done ? "TextDone" : "TextMain"),
            FontSize = 13.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var btn = new Button
        {
            Style = (Style)FindResource("GhostItem"),
            Content = row,
            AllowDrop = true,
            ToolTip = "clica: marca/desmarca - arrasta pra mover/reordenar - botao direito: renomear, excluir"
        };
        btn.Click += async (_, _) =>
        {
            if (_hojeDragging) { _hojeDragging = false; return; }
            item["done"] = !done;
            RenderHoje();
            try { await Supa.SetKv("me2_foco", _foco!.ToJsonString()); }
            catch { ShowStatus("nao sincronizou", error: true); }
        };
        // Arrastar pra reordenar / trocar de n�vel
        btn.PreviewMouseLeftButtonDown += (_, e) => { _hojeDownPos = e.GetPosition(this); _hojeDragging = false; };
        btn.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _hojeDragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _hojeDownPos.X) > 6 || Math.Abs(p.Y - _hojeDownPos.Y) > 6)
            {
                _hojeDragging = true;
                btn.Opacity = 0.5;
                DragDrop.DoDragDrop(btn, id, DragDropEffects.Move);
                btn.Opacity = 1;
            }
        };
        btn.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        btn.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.Text) is string dragId && dragId != id)
                await MoveHojeById(dragId, arr, id);
        };

        // Menu de contexto: renomear � mover � excluir
        var menu = new ContextMenu();
        var mRen = new MenuItem { Header = "renomear" };
        mRen.Click += (_, _) => { _hojeRenameId = id; RenderHoje(); };
        menu.Items.Add(mRen);
        var mMove = new MenuItem { Header = "mover para" };
        foreach (var (destArr, destTit, _, _) in Niveis)
            if (destArr != arr)
            {
                var mi = new MenuItem { Header = destTit.ToLower(new CultureInfo("pt-BR")) };
                string dest = destArr;
                mi.Click += async (_, _) => await MoveHoje(item, arr, dest);
                mMove.Items.Add(mi);
            }
        menu.Items.Add(mMove);
        menu.Items.Add(new Separator());
        var mDel = new MenuItem { Header = "excluir" };
        mDel.Click += async (_, _) => await DeleteHoje(item, arr);
        menu.Items.Add(mDel);
        btn.ContextMenu = menu;
        return btn;
    }

    private bool _hojeDragging;
    private Point _hojeDownPos;

    /// <summary>Move/reordena uma tarefa do Hoje por id: pro n�vel toArr, antes de beforeId (ou fim).</summary>
    private async Task MoveHojeById(string dragId, string toArr, string? beforeId)
    {
        if (_foco is null) return;
        JsonObject? dragged = null;
        // Remove de onde estiver
        foreach (var (a, _, _, _) in Niveis)
            if (_foco[a] is JsonArray arr)
                foreach (var x in arr.OfType<JsonObject>().ToList())
                    if (x["id"]?.GetValue<string>() == dragId) { dragged = (JsonObject)x.DeepClone()!; arr.Remove(x); }
        if (dragged is null) return;

        if (_foco[toArr] is not JsonArray) _foco[toArr] = new JsonArray();
        var dest = (JsonArray)_foco[toArr]!;
        int idx = beforeId is null ? dest.Count
            : dest.OfType<JsonObject>().ToList().FindIndex(o => o["id"]?.GetValue<string>() == beforeId);
        if (idx < 0) idx = dest.Count;
        dest.Insert(idx, dragged);

        RenderHoje();
        try { await Supa.SetKv("me2_foco", _foco.ToJsonString()); }
        catch { ShowStatus("nao sincronizou", error: true); }
    }

    private async Task MoveHoje(JsonObject item, string fromArr, string toArr)
    {
        if (_foco?[fromArr] is JsonArray from) from.Remove(item);
        if (_foco?[toArr] is not JsonArray) _foco![toArr] = new JsonArray();
        (_foco![toArr] as JsonArray)!.Add(item.DeepClone());
        RenderHoje();
        try { await Supa.SetKv("me2_foco", _foco.ToJsonString()); ShowStatus("movido"); }
        catch { ShowStatus("nao sincronizou", error: true); }
    }

    private async Task DeleteHoje(JsonObject item, string arr)
    {
        if (_foco?[arr] is JsonArray a) a.Remove(item);
        RenderHoje();
        try { await Supa.SetKv("me2_foco", _foco!.ToJsonString()); ShowStatus("excluida"); }
        catch { ShowStatus("nao sincronizou", error: true); }
    }

    private async Task AddHojeItem(string arr, string text)
    {
        if (_foco is null || _foco["date"]?.GetValue<string>() != Today())
        {
            var raw = await Supa.GetKv("me2_foco");
            _foco = await RolloverFoco(raw is null ? null : JsonNode.Parse(raw) as JsonObject);
        }
        if (_foco[arr] is not JsonArray) _foco[arr] = new JsonArray();
        (_foco[arr] as JsonArray)!.Add(new JsonObject
        {
            ["id"] = Supa.NewId(),
            ["text"] = text,
            ["done"] = false
        });
        await Supa.SetKv("me2_foco", _foco.ToJsonString());
    }

    private FrameworkElement ProgressBarZ(double frac, bool tall = false)
    {
        // Trilho branco com borda de tinta, preenchimento accent chapado (DESIGN.md §5)
        double h = tall ? 14 : 11;
        var inner = new Grid { Height = h - 4, Margin = new Thickness(0) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.001), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.001), GridUnitType.Star) });
        var fill = new Border
        {
            Background = (Brush)FindResource("Accent"),
            BorderBrush = (Brush)FindResource("Ink"),
            BorderThickness = new Thickness(frac > 0.02 ? 1.5 : 0),
            CornerRadius = new CornerRadius(3)
        };
        Grid.SetColumn(fill, 0);
        inner.Children.Add(fill);
        return new Border
        {
            Height = h,
            Margin = new Thickness(0, 4, tall ? 2 : 20, 4),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Ink"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(1.5),
            Child = inner
        };
    }

    /// <summary>Fade + leve subida, d� o toque de fluidez na troca de conte�do.</summary>
    private static void AnimateIn(UIElement el, double fromY = 10, int ms = 190)
    {
        var tt = new TranslateTransform(0, fromY);
        el.RenderTransform = tt;
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms + 30))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // -- AGENDA: semana em lista fluida + m�s -----------------------

    private async Task LoadAgenda()
    {
        try
        {
            if (_ritmo is null && await Supa.GetKv("me2_ritmo") is string rs)
                _ritmo = JsonNode.Parse(rs) as JsonObject;

            DateTime ini, fim;
            if (_agendaSemana) { ini = Monday(_agendaRef); fim = ini.AddDays(6); }
            else { ini = new DateTime(_agendaRef.Year, _agendaRef.Month, 1); fim = ini.AddMonths(1).AddDays(-1); }

            var tarefas = await Supa.Select(
                $"tarefas?select=id,titulo,prazo,status&prazo=gte.{ini:yyyy-MM-dd}&prazo=lte.{fim:yyyy-MM-dd}&order=prazo.asc");
            RenderAgenda(tarefas, ini, fim);
        }
        catch
        {
            AgendaPanel.Children.Clear();
            AgendaPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private void RenderAgenda(JsonArray tarefas, DateTime ini, DateTime fim)
    {
        AgendaPanel.Children.Clear();
        var culture = new CultureInfo("pt-BR");

        // Cabe�alho: � per�odo � + toggle semana/m�s
        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(toggles, Dock.Right);
        foreach (var (label, semana) in new[] { ("semana", true), ("mes", false) })
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                Background = _agendaSemana == semana ? (Brush)FindResource("ChipBgHover") : Brushes.Transparent
            };
            b.Click += (_, _) => { _agendaSemana = semana; _agendaRef = DateTime.Now.Date; _ = LoadAgenda(); };
            toggles.Children.Add(b);
        }
        header.Children.Add(toggles);

        var navs = new StackPanel { Orientation = Orientation.Horizontal };
        var prev = new Button { Style = (Style)FindResource("Chip"), Content = "<", Padding = new Thickness(10, 3, 10, 3) };
        var next = new Button { Style = (Style)FindResource("Chip"), Content = ">", Padding = new Thickness(10, 3, 10, 3) };
        prev.Click += (_, _) => { _agendaRef = _agendaSemana ? _agendaRef.AddDays(-7) : _agendaRef.AddMonths(-1); _ = LoadAgenda(); };
        next.Click += (_, _) => { _agendaRef = _agendaSemana ? _agendaRef.AddDays(7) : _agendaRef.AddMonths(1); _ = LoadAgenda(); };
        string label2 = _agendaSemana
            ? $"{ini:dd/MM} - {fim:dd/MM}"
            : culture.TextInfo.ToTitleCase(_agendaRef.ToString("MMMM yyyy", culture));
        navs.Children.Add(prev);
        navs.Children.Add(new TextBlock
        {
            Text = label2, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0)
        });
        navs.Children.Add(next);
        header.Children.Add(navs);
        AgendaPanel.Children.Add(header);

        var porDia = new Dictionary<string, List<JsonObject>>();
        foreach (var node in tarefas)
            if (node is JsonObject t && t["prazo"]?.GetValue<string>() is string p)
            {
                if (!porDia.TryGetValue(p, out var list)) porDia[p] = list = new();
                list.Add(t);
            }

        if (_agendaSemana) RenderSemanaLista(ini, porDia, culture);
        else RenderMes(ini, porDia);

        AgendaPanel.Children.Add(EventoAddRow());
    }

    private DateTime _agendaAddDate = DateTime.Now.Date;

    /// <summary>Adicionar evento: clicador de dia (n�o texto) + t�tulo.</summary>
    private FrameworkElement EventoAddRow()
    {
        if (_agendaAddDate < DateTime.Now.Date) _agendaAddDate = _agendaSelDay ?? DateTime.Now.Date;
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };

        // Clicador de dia (abre menu de dias)
        var dayBtn = new Button
        {
            Style = (Style)FindResource("Chip"),
            Content = "\U0001F4C5  " + DayLabel(_agendaAddDate) + "  ▾",
            FontSize = 11.5, Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        dayBtn.Click += (_, _) => OpenDatePicker(dayBtn, _agendaAddDate,
            d => { _agendaAddDate = d; dayBtn.Content = "\U0001F4C5  " + DayLabel(d) + "  ▾"; });
        DockPanel.SetDock(dayBtn, Dock.Left);
        row.Children.Add(dayBtn);

        // T�tulo do evento
        var grid = new Grid();
        var tb = new TextBox { Style = (Style)FindResource("InlineAdd") };
        var hint = new TextBlock
        {
            Text = "titulo do evento - Enter cria no dia escolhido", FontSize = 12,
            Foreground = (Brush)FindResource("TextDone"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0), IsHitTestVisible = false
        };
        tb.TextChanged += (_, _) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var titulo = tb.Text.Trim();
            if (titulo.Length == 0) return;
            e.Handled = true;
            try
            {
                await Supa.Insert("tarefas", new JsonObject
                {
                    ["id"] = "t" + Ms(), ["titulo"] = titulo, ["descricao"] = "",
                    ["status"] = "a fazer", ["prazo"] = _agendaAddDate.ToString("yyyy-MM-dd")
                });
                tb.Clear();
                ShowStatus($"evento em {_agendaAddDate:dd/MM}");
                await LoadAgenda();
            }
            catch (Exception ex) { ShowStatus(ex.Message, error: true); }
        };
        grid.Children.Add(tb);
        grid.Children.Add(hint);
        row.Children.Add(grid);
        return row;
    }

    /// <summary>Semana como lista fluida: um dia por linha, eventos como p�lulas.</summary>
    private void RenderSemanaLista(DateTime monday, Dictionary<string, List<JsonObject>> porDia, CultureInfo culture)
    {
        for (int i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            bool ehHoje = d == DateTime.Now.Date;

            var row = new DockPanel();

            // Bloco do dia � esquerda
            var dia = new StackPanel { Width = 84, VerticalAlignment = VerticalAlignment.Center };
            dia.Children.Add(new TextBlock
            {
                Text = culture.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).ToLower(culture),
                FontSize = 10,
                Foreground = (Brush)FindResource(ehHoje ? "Accent" : "TextDim")
            });
            dia.Children.Add(new TextBlock
            {
                Text = d.ToString("dd/MM"),
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(ehHoje ? "Accent" : "TextMain")
            });
            DockPanel.SetDock(dia, Dock.Left);
            row.Children.Add(dia);

            // Eventos + recorrentes como p�lulas
            var pills = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            if (porDia.TryGetValue(d.ToString("yyyy-MM-dd"), out var items))
                foreach (var t in items)
                {
                    bool feito = t["status"]?.GetValue<string>() == "feito";
                    var pill = new Border
                    {
                        Background = (Brush)FindResource("ChipBg"),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 2, 6, 2),
                        Cursor = Cursors.Hand,
                        ToolTip = "clica pra editar",
                        Child = new TextBlock
                        {
                            Text = t["titulo"]?.GetValue<string>() ?? "",
                            FontSize = 12,
                            TextDecorations = feito ? TextDecorations.Strikethrough : null,
                            Foreground = (Brush)FindResource(feito ? "TextDone" : "TextMain"),
                            MaxWidth = 320,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    };
                    var tt = t;
                    pill.MouseLeftButtonUp += (_, ev) => { OpenKbEdit(tt); ev.Handled = true; };
                    pills.Children.Add(pill);
                }
            foreach (var texto in RecurDoDia(d))
                pills.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 2, 6, 2),
                    Child = new TextBlock
                    {
                        Text = texto,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("AccentSoft")
                    }
                });
            if (pills.Children.Count == 0)
                pills.Children.Add(new TextBlock
                {
                    Text = "-",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextDone"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            row.Children.Add(pills);

            AgendaPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(ehHoje
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                BorderBrush = ehHoje ? (Brush)FindResource("Accent") : Brushes.Transparent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 5),
                Child = row
            });
        }
    }

    private DateTime? _agendaSelDay;

    private void RenderMes(DateTime first, Dictionary<string, List<JsonObject>> porDia)
    {
        var culture = new CultureInfo("pt-BR");
        var hoje = DateTime.Now.Date;
        int dias = DateTime.DaysInMonth(first.Year, first.Month);

        // Cabe�alho dos dias da semana
        var wk = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var w in new[] { "seg", "ter", "qua", "qui", "sex", "sab", "dom" })
            wk.Children.Add(new TextBlock
            {
                Text = w, FontSize = 9.5, FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextDone"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        AgendaPanel.Children.Add(wk);

        // Grade do m�s: cada c�lula mostra o dia + os eventos ESCRITOS
        var grid = new UniformGrid { Columns = 7 };
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());

        for (int dia = 1; dia <= dias; dia++)
        {
            var d = new DateTime(first.Year, first.Month, dia);
            bool ehHoje = d == hoje;
            var evs = porDia.TryGetValue(d.ToString("yyyy-MM-dd"), out var l) ? l : new List<JsonObject>();
            var recurs = RecurDoDia(d).ToList();

            bool temEvento = evs.Count + recurs.Count > 0;
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = dia.ToString(), FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource(ehHoje ? "TextInk" : "TextDim"),
                Margin = new Thickness(1, 0, 0, 3)
            });

            int mostrados = 0;
            // Eventos = chip SKY com borda de tinta; recorrentes = chip SUN
            void EventChip(string texto, string bgKey, JsonObject? tarefa)
            {
                var chip = new Border
                {
                    Background = (Brush)FindResource(bgKey),
                    BorderBrush = (Brush)FindResource("Ink"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 0, 3), Cursor = tarefa is null ? Cursors.Arrow : Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = texto, FontSize = 9.5, FontWeight = FontWeights.Medium,
                        Foreground = (Brush)FindResource("Ink"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                if (tarefa is not null) chip.MouseLeftButtonUp += (_, ev) => { OpenKbEdit(tarefa); ev.Handled = true; };
                cell.Children.Add(chip);
            }
            foreach (var t in evs)
            {
                if (mostrados >= 3) break;
                EventChip(t["titulo"]?.GetValue<string>() ?? "", "SkySoft", t);
                mostrados++;
            }
            foreach (var texto in recurs)
            {
                if (mostrados >= 3) break;
                EventChip(texto, "SunSoft", null);
                mostrados++;
            }
            int total = evs.Count + recurs.Count;
            if (total > mostrados)
                cell.Children.Add(new TextBlock { Text = $"+{total - mostrados}", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(1, 0, 0, 0) });

            var ink = (Brush)FindResource("Ink");
            var cellBorder = new Border
            {
                MinHeight = 66, Margin = new Thickness(2),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 5, 6, 5),
                Background = ehHoje ? (Brush)FindResource("AccentSoft")
                    : temEvento ? (Brush)FindResource("Surface") : (Brush)FindResource("Cream"),
                BorderThickness = new Thickness(2),
                BorderBrush = ink,
                Cursor = Cursors.Hand, Child = cell,
                ToolTip = "clica pra marcar evento neste dia"
            };
            var dd = d;
            cellBorder.MouseLeftButtonUp += (_, ev) =>
            {
                if (ev.Handled) return;
                _agendaAddDate = dd; ShowStatus($"dia escolhido: {dd:dd/MM} - escreve o evento no campo abaixo");
            };
            grid.Children.Add(cellBorder);
        }
        AgendaPanel.Children.Add(grid);
    }

    /// <summary>Itens recorrentes do ritmo (semanal por dia da semana, mensal por dia do m�s).</summary>
    private IEnumerable<string> RecurDoDia(DateTime d)
    {
        if (_ritmo?["recur"] is not JsonArray recur) yield break;
        foreach (var node in recur)
        {
            if (node is not JsonObject r) continue;
            string tipo = r["type"]?.GetValue<string>() ?? "";
            string texto = r["text"]?.GetValue<string>() ?? "";
            if (tipo == "mensal" && (int)(r["dom"]?.GetValue<double>() ?? -1) == d.Day)
                yield return texto;
            else if (tipo == "semanal" && (int)(r["dow"]?.GetValue<double>() ?? -1) == (int)d.DayOfWeek)
                yield return texto;
        }
    }

    // -- KANBAN: arrastar entre colunas + edi��o --------------------

    private static readonly string[] KanbanStatuses = { "a fazer", "fazendo", "feito" };
    private bool _kbDragging;
    private Point _kbDownPos;

    private async Task LoadKanban()
    {
        try
        {
            _tarefasCache = await Supa.Select("tarefas?select=id,titulo,status,prazo&order=created_at.desc");
            RenderKanban();
        }
        catch
        {
            KanbanPanel.Children.Clear();
            KanbanPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private void RenderKanban()
    {
        KanbanPanel.Children.Clear();
        KanbanPanel.Children.Add(SectionLabel("KANBAN"));

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        for (int c = 0; c < KanbanStatuses.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int c = 0; c < KanbanStatuses.Length; c++)
        {
            string status = KanbanStatuses[c];
            var accent = KanbanStatusBrush(status);
            var itens = _tarefasCache.OfType<JsonObject>()
                .Where(t => (t["status"]?.GetValue<string>() ?? "a fazer") == status)
                .ToList();

            var ink = (Brush)FindResource("Ink");
            var col = new StackPanel { MinHeight = 138 };
            // Cabecalho = TAG de accent com borda de tinta + contador em bloquinho
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 11) };
            var countPill = new Border
            {
                Background = (Brush)FindResource("Surface"),
                BorderBrush = ink,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = itens.Count.ToString(),
                    FontSize = 10, FontWeight = FontWeights.Bold,
                    FontFamily = (FontFamily)FindResource("Mono"),
                    Foreground = ink
                }
            };
            DockPanel.SetDock(countPill, Dock.Right);
            head.Children.Add(countPill);
            head.Children.Add(new Border
            {
                Background = accent,
                BorderBrush = ink, BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 3, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = status.ToUpper(new CultureInfo("pt-BR")),
                    FontSize = 10.5, FontWeight = FontWeights.Bold,
                    FontFamily = (FontFamily)FindResource("Mono"),
                    Foreground = ink
                }
            });
            col.Children.Add(head);

            foreach (var t in itens) col.Children.Add(KanbanCard(t));

            var colInner = new Border
            {
                Background = (Brush)FindResource("Surface"),
                BorderBrush = ink,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 10, 10, 8),
                MinHeight = 120,
                Margin = new Thickness(0, 0, 4, 4),
                Child = col
            };
            var sombra = new Border { Background = ink, CornerRadius = new CornerRadius(10), Margin = new Thickness(4, 4, 0, 0) };
            var colBorder = new Grid
            {
                Margin = new Thickness(c == 0 ? 0 : 9, 0, 0, 0),
                Background = Brushes.Transparent, // precisa pro hit-test do drop
                SnapsToDevicePixels = true,
                Children = { sombra, colInner }
            };
            colBorder.AllowDrop = true;
            colBorder.DragEnter += (_, _) => SetKanbanDropVisual(colInner, true);
            colBorder.DragLeave += (_, _) => SetKanbanDropVisual(colInner, false);
            colBorder.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            colBorder.Drop += (_, e) =>
            {
                SetKanbanDropVisual(colInner, false);
                if (e.Data.GetData(DataFormats.Text) is string id) _ = MoveKanban(id, status);
            };
            Grid.SetColumn(colBorder, c);
            grid.Children.Add(colBorder);
        }
        KanbanPanel.Children.Add(grid);

        KanbanPanel.Children.Add(MakeAddBox("+ novo card em \"a fazer\"", async text =>
        {
            await AddTarefa(text);
            await LoadKanban();
        }));
        AnimateIn(KanbanPanel, fromY: 6, ms: 160);
    }

    private void SetKanbanDropVisual(Border lane, bool active)
    {
        lane.RenderTransformOrigin = new Point(0.5, 0.5);
        if (lane.RenderTransform is not ScaleTransform)
            lane.RenderTransform = new ScaleTransform(1, 1);

        lane.BorderBrush = active
            ? (Brush)FindResource("AccentSoft")
            : (Brush)FindResource("Ink");
        lane.Background = active
            ? (Brush)FindResource("ChipBgHover")
            : (Brush)FindResource("Surface");

        if (lane.RenderTransform is ScaleTransform scale)
        {
            double to = active ? 1.015 : 1.0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(active ? 130 : 180)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(active ? 130 : 180)) { EasingFunction = ease });
        }
    }

    private Brush KanbanStatusBrush(string status) => status switch
    {
        "fazendo" => (Brush)FindResource("Media"),
        "feito" => (Brush)FindResource("Facil"),
        _ => (Brush)FindResource("Accent")
    };

    /// <summary>Move otimista: reordena na mem�ria e re-renderiza na hora, salva em segundo plano.</summary>
    private async Task MoveKanban(string id, string status)
    {
        var card = _tarefasCache.OfType<JsonObject>().FirstOrDefault(t => t["id"]?.GetValue<string>() == id);
        if (card is null) return;
        if ((card["status"]?.GetValue<string>() ?? "a fazer") == status) return;
        card["status"] = status;
        RenderKanban(); // resposta instant�nea
        try { await Supa.Update("tarefas", "id=eq." + Uri.EscapeDataString(id), new JsonObject { ["status"] = status }); }
        catch { ShowStatus("nao sincronizou o card", error: true); await LoadKanban(); }
    }

    private FrameworkElement KanbanCard(JsonObject t)
    {
        string titulo = t["titulo"]?.GetValue<string>() ?? "";
        string? prazo = t["prazo"]?.GetValue<string>();
        string id = t["id"]?.GetValue<string>() ?? "";
        string status = t["status"]?.GetValue<string>() ?? "a fazer";
        var accent = KanbanStatusBrush(status);

        var sp = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        });
        if (prazo is not null && DateTime.TryParse(prazo, out var dt))
            sp.Children.Add(new TextBlock
            {
                Text = dt.ToString("dd/MM"), FontSize = 10,
                FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 5, 0, 0)
            });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Bolinha de status colorida com borda de tinta (estilo Acervo)
        body.Children.Add(new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
            Background = accent,
            BorderBrush = (Brush)FindResource("Ink"), BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(sp, 1);
        body.Children.Add(sp);

        var ink = (Brush)FindResource("Ink");
        var card = new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 3, 3),
            Cursor = Cursors.Hand,
            Child = body,
            ToolTip = "clica: editar - arrasta: mover de coluna"
        };
        card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("Accent");
        card.MouseLeave += (_, _) => card.BorderBrush = (Brush)FindResource("Ink");

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _kbDownPos = e.GetPosition(this);
            _kbDragging = false;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _kbDragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _kbDownPos.X) > 6 || Math.Abs(p.Y - _kbDownPos.Y) > 6)
            {
                _kbDragging = true;
                AnimateDragCard(card, true);
                DragDrop.DoDragDrop(card, id, DragDropEffects.Move);
                AnimateDragCard(card, false);
            }
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!_kbDragging) OpenKbEdit(t);
            _kbDragging = false;
        };
        var menu = new ContextMenu();
        var sendToday = new MenuItem { Header = "enviar para hoje" };
        foreach (var (arr, tit, _, _) in Niveis)
        {
            string target = arr;
            var mi = new MenuItem { Header = tit.ToLower(new CultureInfo("pt-BR")) };
            mi.Click += async (_, _) =>
            {
                await AddHojeItem(target, titulo);
                ShowStatus("enviado para hoje");
                if (_currentView == "Hoje") await LoadHoje();
            };
            sendToday.Items.Add(mi);
        }
        menu.Items.Add(sendToday);
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "editar" };
        edit.Click += (_, _) => OpenKbEdit(t);
        menu.Items.Add(edit);
        card.ContextMenu = menu;

        // Sombra dura atrás (estilo Acervo)
        var sombra = new Border { Background = ink, CornerRadius = new CornerRadius(12), Margin = new Thickness(3, 3, 0, 0) };
        return new Grid
        {
            Margin = new Thickness(0, 0, 0, 9),
            SnapsToDevicePixels = true,
            Children = { sombra, card }
        };
    }

    private static void AnimateDragCard(Border card, bool dragging)
    {
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        if (card.RenderTransform is not ScaleTransform)
            card.RenderTransform = new ScaleTransform(1, 1);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(dragging ? 0.58 : 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        if (card.RenderTransform is ScaleTransform scale)
        {
            double to = dragging ? 0.985 : 1;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        }
    }

    // Edi��o de card
    private string? _kbEditId;
    private string _kbEditStatus = "a fazer";

    private DateTime? _kbEditDate;

    private void OpenKbEdit(JsonObject t)
    {
        _kbEditId = t["id"]?.GetValue<string>();
        _kbEditStatus = t["status"]?.GetValue<string>() ?? "a fazer";
        KbEdTitle.Text = t["titulo"]?.GetValue<string>() ?? "";
        string? prazo = t["prazo"]?.GetValue<string>();
        _kbEditDate = prazo is not null && DateTime.TryParse(prazo, out var dt) ? dt.Date : null;
        UpdateKbDateBtn();
        BuildKbStatusRow();
        KbEditPopup.IsOpen = true;
        KbEdTitle.Focus();
    }

    private void UpdateKbDateBtn()
    {
        KbEdDateBtn.Content = "\U0001F4C5  " + (_kbEditDate is DateTime d ? d.ToString("dd/MM/yyyy") : "sem data") + "  ▾";
        KbEdDateClear.Visibility = _kbEditDate is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void KbEdDateBtn_Click(object sender, RoutedEventArgs e)
        => OpenDatePicker(KbEdDateBtn, _kbEditDate ?? DateTime.Now.Date, d => { _kbEditDate = d; UpdateKbDateBtn(); });

    private void KbEdDateClear_Click(object sender, RoutedEventArgs e)
    { _kbEditDate = null; UpdateKbDateBtn(); }

    private void BuildKbStatusRow()
    {
        KbEdStatusRow.Children.Clear();
        foreach (var s in KanbanStatuses)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = s,
                FontSize = 11.5,
                Background = s == _kbEditStatus ? (Brush)FindResource("ChipBgHover") : Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            b.Click += (_, _) => { _kbEditStatus = s; BuildKbStatusRow(); };
            KbEdStatusRow.Children.Add(b);
        }
    }

    private void KbEdCancel_Click(object sender, RoutedEventArgs e) => KbEditPopup.IsOpen = false;

    private async void KbEdSave_Click(object sender, RoutedEventArgs e)
    {
        if (_kbEditId is null) return;
        var patch = new JsonObject
        {
            ["titulo"] = KbEdTitle.Text.Trim(),
            ["status"] = _kbEditStatus,
            ["prazo"] = _kbEditDate is DateTime d ? d.ToString("yyyy-MM-dd") : null
        };

        try
        {
            await Supa.Update("tarefas", "id=eq." + Uri.EscapeDataString(_kbEditId), patch);
            KbEditPopup.IsOpen = false;
            ShowStatus("card atualizado");
            if (_currentView == "Kanban") await LoadKanban();
            if (_currentView == "Agenda") await LoadAgenda();
            if (_currentView == "Painel") await LoadPainel();
        }
        catch { ShowStatus("nao salvou o card", error: true); }
    }

    private async void KbEdDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_kbEditId is null) return;
        try
        {
            await Supa.Delete("tarefas", "id=eq." + Uri.EscapeDataString(_kbEditId));
            KbEditPopup.IsOpen = false;
            ShowStatus("card excluido");
            if (_currentView == "Kanban") await LoadKanban();
            if (_currentView == "Agenda") await LoadAgenda();
            if (_currentView == "Painel") await LoadPainel();
        }
        catch { ShowStatus("nao excluiu", error: true); }
    }

    private static Task AddTarefa(string text) => Supa.Insert("tarefas", new JsonObject
    {
        ["id"] = "t" + Ms(),
        ["titulo"] = text,
        ["descricao"] = "",
        ["status"] = "a fazer"
    });

    // -- LINKS: barra de favoritos - tudo a vista, 1 clique abre ----

    private string? _linkEditId;
    private readonly Dictionary<string, List<string>> _linkOrders = new();

    private async Task LoadLinks()
    {
        try
        {
            var foldersT = Supa.Select("zimbar_folders?select=id,name,parent_id&order=name.asc");
            var refsT = Supa.Select("zimbar_refs?select=id,kind,title,content,folder_id&order=created_at.asc");
            var ordersT = Supa.Select("app_kv?select=k,v&k=like.zimbar_link_order_*");
            await Task.WhenAll(foldersT, refsT, ordersT);

            _linkOrders.Clear();
            foreach (var row in ordersT.Result.OfType<JsonObject>())
            {
                string k = row["k"]?.GetValue<string>() ?? "";
                if (!k.StartsWith("zimbar_link_order_", StringComparison.Ordinal)) continue;
                try
                {
                    if (row["v"]?.GetValue<string>() is string s && JsonNode.Parse(s) is JsonArray arr)
                        _linkOrders[k["zimbar_link_order_".Length..]] =
                            arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
                }
                catch { }
            }

            RenderLinksBoard(foldersT.Result, refsT.Result);
        }
        catch
        {
            LinksPanel.Children.Clear();
            LinksPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private void RenderLinksBoard(JsonArray folders, JsonArray refs)
    {
        LinksPanel.Children.Clear();

        var roots = new List<(string Id, string Name)>();
        var kids = new Dictionary<string, List<(string Id, string Name)>>();
        foreach (var f in folders.OfType<JsonObject>())
        {
            string id = f["id"]?.GetValue<string>() ?? "";
            string name = f["name"]?.GetValue<string>() ?? "";
            string? parent = f["parent_id"]?.GetValue<string>();
            if (id.Length == 0) continue;
            if (string.IsNullOrEmpty(parent)) roots.Add((id, name));
            else
            {
                if (!kids.TryGetValue(parent, out var l)) kids[parent] = l = new();
                l.Add((id, name));
            }
        }
        var porPasta = new Dictionary<string, List<JsonObject>>();
        foreach (var r in refs.OfType<JsonObject>())
        {
            string fid = r["folder_id"]?.GetValue<string>() ?? "";
            if (!porPasta.TryGetValue(fid, out var l)) porPasta[fid] = l = new();
            l.Add(r);
        }

        LinksPanel.Children.Add(HudLabel("LINKS - sua barra de favoritos: 1 clique abre"));

        var board = new UniformGrid
        {
            Columns = ResponsiveColumns(minItemWidth: 252, maxColumns: 4),
            Margin = new Thickness(0, 8, 0, 0)
        };
        int i = 0;
        foreach (var (id, name) in roots)
            board.Children.Add(LinkFolderCard(id, name, porPasta, kids, tint: i++));

        if (roots.Count == 0)
            LinksPanel.Children.Add(DimText("nenhuma pasta ainda - cria a primeira abaixo"));
        else
            LinksPanel.Children.Add(board);

        LinksPanel.Children.Add(RevealAdd("+ pasta", "nome da pasta", async text =>
        {
            await Supa.Insert("zimbar_folders", new JsonObject { ["name"] = text, ["parent_id"] = null });
            await LoadLinks();
        }));
    }

    /// <summary>Card de uma pasta: bloco colorido com os links direto nele (e subpastas como secoes).</summary>
    private Border LinkFolderCard(string id, string name,
        Dictionary<string, List<JsonObject>> porPasta,
        Dictionary<string, List<(string Id, string Name)>> kids, int tint)
    {
        var col = new StackPanel();

        var del = new Button
        {
            Style = (Style)FindResource("NavBtn"), Content = "✕", FontSize = 10.5,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0),
            Foreground = (Brush)FindResource("TextDim"), Visibility = Visibility.Hidden,
            ToolTip = "excluir pasta e tudo dentro"
        };
        del.Click += async (_, _) => await DeleteFolder(id, name);

        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
        DockPanel.SetDock(del, Dock.Right);
        head.Children.Add(del);
        head.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13.5, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        col.Children.Add(head);

        void AddLinks(string folderId)
        {
            var lista = OrderedLinks(porPasta.TryGetValue(folderId, out var l) ? l : new(), folderId);
            var ids = lista.Select(x => x["id"]?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
            foreach (var r in lista) col.Children.Add(LinkRow(r, folderId, ids));
        }

        AddLinks(id);

        if (kids.TryGetValue(id, out var cats))
            foreach (var (cid, cname) in cats)
            {
                var catDel = new Button
                {
                    Style = (Style)FindResource("NavBtn"), Content = "✕", FontSize = 9,
                    Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(4, 0, 0, 0),
                    Foreground = (Brush)FindResource("TextDone"), Opacity = 0,
                    ToolTip = "excluir esta secao"
                };
                catDel.Click += async (_, _) => await DeleteFolder(cid, cname);
                var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 3) };
                sub.Children.Add(new TextBlock
                {
                    Text = cname.ToUpper(new CultureInfo("pt-BR")), FontSize = 9.5,
                    FontFamily = (FontFamily)FindResource("Mono"),
                    Foreground = (Brush)FindResource("TextDim"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sub.Children.Add(catDel);
                sub.MouseEnter += (_, _) => catDel.Opacity = 1;
                sub.MouseLeave += (_, _) => catDel.Opacity = 0;
                col.Children.Add(sub);
                AddLinks(cid);
            }

        col.Children.Add(RevealAdd("+ link", "nome | url  (ou so a url)", async text =>
        {
            var parsed = ParseLinkInput(text);
            await Supa.Insert("zimbar_refs", new JsonObject
            {
                ["kind"] = parsed.Kind,
                ["title"] = parsed.Title,
                ["content"] = parsed.Content,
                ["folder_id"] = id
            });
            await LoadLinks();
        }));

        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 0, ShadowDepth = 3, Direction = 315, Opacity = 1, Color = Color.FromRgb(0x18, 0x13, 0x20) };
        shadow.Freeze();
        var card = new Border
        {
            Background = Zui.Tint(this, tint),
            BorderBrush = (Brush)FindResource("Ink"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 9, 9),
            Effect = shadow,
            Child = col
        };
        card.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
        card.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;
        return card;
    }

    private List<JsonObject> OrderedLinks(List<JsonObject> refs, string folderId)
    {
        if (!_linkOrders.TryGetValue(folderId, out var order) || order.Count == 0) return refs;
        var rank = order.Select((rid, i) => (rid, i)).ToDictionary(x => x.rid, x => x.i);
        return refs
            .Select((item, original) => (item, original, id: item["id"]?.GetValue<string>() ?? ""))
            .OrderBy(x => rank.TryGetValue(x.id, out var pos) ? pos : int.MaxValue)
            .ThenBy(x => x.original)
            .Select(x => x.item)
            .ToList();
    }

    private static string LinkOrderKey(string folderId) => "zimbar_link_order_" + folderId;

    private async Task MoveLink(string folderId, List<string> visibleIds, string id, int delta)
    {
        int i = visibleIds.IndexOf(id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= visibleIds.Count) return;
        (visibleIds[i], visibleIds[j]) = (visibleIds[j], visibleIds[i]);
        await SetKvArray(LinkOrderKey(folderId), new JsonArray(visibleIds.Select(x => JsonValue.Create(x)).ToArray()));
        await LoadLinks();
    }

    /// <summary>Favicon do site (via Google), com bolinha de fallback embaixo.</summary>
    private static readonly string[] DotTints = { "Sun", "Leaf", "Grape", "Rose", "Sky", "Tang" };

    /// <summary>Pontinho do link: bolinha com borda de tinta, cor estável por domínio.</summary>
    private FrameworkElement Favicon(string url, bool ehLink)
    {
        string dom = "";
        try { dom = new Uri(EnsureUrl(url)).Host.Replace("www.", ""); } catch { }
        int h = 0; foreach (char c in dom) h = (h * 31 + c) & 0x7fffffff;
        var cor = (Brush)FindResource(ehLink ? DotTints[h % DotTints.Length] : "TextDone");

        return new Border
        {
            Width = 11, Height = 11, Margin = new Thickness(2, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = cor,
            BorderBrush = (Brush)FindResource("Ink"), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6)
        };
    }

    /// <summary>Linha de link: favicon + nome, clique abre. Acoes aparecem no hover.</summary>
    private UIElement LinkRow(JsonObject r, string folderId, List<string> visibleIds)
    {
        string id = r["id"]?.GetValue<string>() ?? "";
        string kind = r["kind"]?.GetValue<string>() ?? "link";
        string title = r["title"]?.GetValue<string>() ?? "";
        string content = r["content"]?.GetValue<string>() ?? "";
        bool ehLink = kind == "link" || LooksLikeUrl(content);
        if (ehLink) content = EnsureUrl(content);
        string label = title.Length > 0 ? title : ehLink ? LinkLabel(content) : content;

        // Edicao inline (nome | url)
        if (_linkEditId == id)
        {
            var box = new TextBox
            {
                Style = (Style)FindResource("InlineAdd"),
                Text = title.Length > 0 ? title + " | " + content : content,
                FontSize = 12, Margin = new Thickness(0, 2, 0, 2)
            };
            box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) { _linkEditId = null; await LoadLinks(); e.Handled = true; return; }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                var parsed = ParseLinkInput(box.Text.Trim());
                _linkEditId = null;
                try
                {
                    await Supa.Update("zimbar_refs", "id=eq." + Uri.EscapeDataString(id), new JsonObject
                    { ["kind"] = parsed.Kind, ["title"] = parsed.Title, ["content"] = parsed.Content });
                }
                catch { ShowStatus("nao salvou", error: true); }
                await LoadLinks();
            };
            return box;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
        Button ActionBtn(string glyph, string tip)
        {
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"), Content = glyph, FontSize = 10.5,
                Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(1, 0, 0, 0),
                Foreground = (Brush)FindResource("TextDim"), ToolTip = tip
            };
            actions.Children.Add(b);
            return b;
        }
        ActionBtn("↑", "subir").Click += async (_, _) => await MoveLink(folderId, visibleIds.ToList(), id, -1);
        ActionBtn("↓", "descer").Click += async (_, _) => await MoveLink(folderId, visibleIds.ToList(), id, 1);
        ActionBtn("✎", "editar").Click += (_, _) => { _linkEditId = id; _ = LoadLinks(); };
        ActionBtn("✕", "apagar").Click += async (_, _) =>
        {
            try { await Supa.Delete("zimbar_refs", "id=eq." + Uri.EscapeDataString(id)); await LoadLinks(); }
            catch { ShowStatus("nao apagou", error: true); }
        };

        var row = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        var icon = Favicon(content, ehLink);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var cell = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 4, 4, 4),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            ToolTip = ehLink ? content : "clica pra copiar",
            Child = row
        };
        cell.MouseEnter += (_, _) => { actions.Opacity = 1; cell.Background = (Brush)FindResource("Surface"); };
        cell.MouseLeave += (_, _) => { actions.Opacity = 0; cell.Background = Brushes.Transparent; };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement fe && fe is Button) return;
            if (ehLink) { OpenExternal(content); ShowStatus("abrindo"); }
            else { Clipboard.SetText(content); ShowStatus("copiado"); }
            e.Handled = true;
        };
        return cell;
    }

    /// <summary>Exclui uma pasta. Links que ainda estejam em categorias antigas entram na contagem e sao removidos junto.</summary>
    private async Task DeleteFolder(string id, string name)
    {
        try
        {
            var cats = await Supa.Select($"zimbar_folders?select=id&parent_id=eq.{Uri.EscapeDataString(id)}");
            var catIds = cats.OfType<JsonObject>().Select(c => c["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
            var ids = new List<string> { id };
            ids.AddRange(catIds);
            string inList = string.Join(",", ids.Select(Uri.EscapeDataString));
            var refs = await Supa.Select($"zimbar_refs?select=id&folder_id=in.({inList})");
            int nCats = catIds.Count, nLinks = refs.Count;

            if (nCats > 0 || nLinks > 0)
            {
                bool wasTop = Topmost;
                _busyModal = true;   // nao deixa minimizar enquanto o dialogo esta aberto
                Topmost = false;     // pro MessageBox nao ficar atras da barra
                var r = MessageBox.Show(this,
                    $"Excluir a pasta \"{name}\" e tudo dentro?\n\n{nLinks} link(s) serao apagados. Isso nao volta.",
                    "Excluir pasta - Zimbar", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                Topmost = wasTop;
                _busyModal = false;
                if (r != MessageBoxResult.OK) return;
            }

            if (nLinks > 0) await Supa.Delete("zimbar_refs", $"folder_id=in.({inList})");
            foreach (var cid in catIds) await Supa.Delete("zimbar_folders", "id=eq." + Uri.EscapeDataString(cid));
            await Supa.Delete("zimbar_folders", "id=eq." + Uri.EscapeDataString(id));
            await LoadLinks();
            ShowStatus($"pasta \"{name}\" excluida");
        }
        catch { ShowStatus("nao excluiu a pasta", error: true); }
    }

    // -- LISTAS: puxadas do mural do Meu Espa�o (tabela mural_items) ----

    private JsonArray _muralCache = new();

    private async Task LoadListas()
    {
        try
        {
            _muralCache = await Supa.Select("mural_items?select=id,categoria,texto&order=created_at.asc");
            RenderListas();
        }
        catch
        {
            ListasPanel.Children.Clear();
            ListasPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    private static readonly (string Cat, string BlockKey)[] ListaCatsConhecidas =
    {
        ("ler/assistir", "BlockBlue"), ("comprar", "BlockYellow"),
        ("estudos", "BlockLime"), ("exames", "BlockPink"),
    };

    private string ListaCatEstilo(string cat)
    {
        foreach (var (c, b) in ListaCatsConhecidas)
            if (cat.Contains(c, StringComparison.OrdinalIgnoreCase) || c.Contains(cat, StringComparison.OrdinalIgnoreCase))
                return b;
        var blocos = new[] { "BlockPurple", "BlockCoral", "BlockLime", "BlockBlue", "BlockYellow", "BlockPink" };
        return blocos[Math.Abs(cat.GetHashCode()) % blocos.Length];
    }

    private void RenderListas()
    {
        ListasPanel.Children.Clear();
        ListasPanel.Children.Add(SectionLabel("LISTAS - do mural do Meu Espaco"));

        // Agrupa mural_items por categoria (preservando ordem de apari��o)
        var ordem = new List<string>();
        var grupos = new Dictionary<string, List<JsonObject>>();
        // Categorias fixas do mural aparecem sempre, mesmo vazias
        foreach (var (fixa, _) in ListaCatsConhecidas)
        {
            var chave = fixa == "estudos" ? "estudos" : fixa;
            grupos[chave] = new();
            ordem.Add(chave);
        }
        foreach (var node in _muralCache)
            if (node is JsonObject it)
            {
                string cat = it["categoria"]?.GetValue<string>() ?? "outros";
                if (!grupos.TryGetValue(cat, out var l)) { grupos[cat] = l = new(); ordem.Add(cat); }
                l.Add(it);
            }

        var grid = new UniformGrid { Columns = Math.Min(4, Math.Max(1, ordem.Count)) };
        foreach (var cat in ordem)
        {
            var blockKey = ListaCatEstilo(cat);
            var bloco = (Brush)FindResource(blockKey);
            var col = new StackPanel();

            // Cabe�alho da lista = bolinha de cor + nome (harmonizado com o resto)
            var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            tagRow.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(3),
                Background = bloco, Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            tagRow.Children.Add(new TextBlock
            {
                Text = cat, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain"), VerticalAlignment = VerticalAlignment.Center
            });
            col.Children.Add(tagRow);

            foreach (var it in grupos[cat])
                col.Children.Add(MuralItemRow(it, bloco));

            col.Children.Add(RevealAdd("+ item", "novo item nesta lista", async text =>
            {
                await Supa.Insert("mural_items", new JsonObject
                { ["id"] = "m" + Ms(), ["categoria"] = cat, ["texto"] = text });
                await LoadListas();
            }));

            // Bloco neobrutal opaco na cor da lista + sombra dura (DESIGN.md §4)
            var ink = (Brush)FindResource("Ink");
            var bc = ((SolidColorBrush)bloco).Color;
            byte Mix(byte c) => (byte)(255 - (255 - c) * 0.34);
            var face = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(Mix(bc.R), Mix(bc.G), Mix(bc.B))),
                BorderBrush = ink,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 10, 11, 10),
                Margin = new Thickness(0, 0, 4, 4),
                Child = col
            };
            var sombra = new Border { Background = ink, CornerRadius = new CornerRadius(10), Margin = new Thickness(4, 4, 0, 0) };
            grid.Children.Add(new Grid
            {
                Margin = new Thickness(0, 0, 9, 9),
                SnapsToDevicePixels = true,
                Children = { sombra, face }
            });
        }
        if (ordem.Count == 0)
            ListasPanel.Children.Add(DimText("o mural esta vazio - cria uma lista abaixo"));
        else
            ListasPanel.Children.Add(grid);

        ListasPanel.Children.Add(RevealAdd("+ nova lista", "nome da nova lista (ex: presentes)", async text =>
        {
            var cat = text.Trim().ToLower(new CultureInfo("pt-BR"));
            if (cat.Length == 0) return;
            await Supa.Insert("mural_items", new JsonObject
            { ["id"] = "m" + Ms(), ["categoria"] = cat, ["texto"] = "" });
            await LoadListas();
        }));
    }

    private UIElement MuralItemRow(JsonObject it, Brush bloco)
    {
        string id = it["id"]?.GetValue<string>() ?? "";
        string text = it["texto"]?.GetValue<string>() ?? "";
        if (text.Length == 0) // item semente vazio (usado s� pra criar a lista)
            return new Border { Height = 0 };

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var del = new Button
        {
            Style = (Style)FindResource("Chip"),
            Content = "✕",
            FontSize = 9.5,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Brushes.Transparent,
            ToolTip = "apagar item"
        };
        DockPanel.SetDock(del, Dock.Right);
        del.Click += async (_, _) =>
        {
            try { await Supa.Delete("mural_items", "id=eq." + Uri.EscapeDataString(id)); await LoadListas(); }
            catch { ShowStatus("nao apagou", error: true); }
        };
        row.Children.Add(del);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(2),
            Background = bloco, Margin = new Thickness(2, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12.5,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(body);
        return row;
    }

    // -- CONTAS: painel de meta do contas.pedro (quanto pode gastar hoje) ---

    private Contas.Snapshot? _contasCache;

    /// <summary>Faixa compacta no Painel: "pode gastar hoje R$ X", clica pra abrir a aba Contas.</summary>
    private async Task FillContasHome(ContentControl host)
    {
        var s = _contasCache ?? await Contas.Carregar();
        _contasCache = s;
        if (!s.Ok || !s.TemMeta) { host.Content = null; return; }   // silencioso se offline/sem meta

        var row = new DockPanel();
        var esq = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        esq.Children.Add(new TextBlock { Text = "PODE GASTAR HOJE", FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("Mono"), Foreground = (Brush)FindResource("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        esq.Children.Add(new TextBlock { Text = Contas.Fmt(s.DispHoje), FontSize = 22, FontFamily = (FontFamily)FindResource("Display"), Foreground = (Brush)FindResource(s.DispHoje >= 0 ? "Ink" : "Tang"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(esq);
        var dir = new TextBlock { Text = $"{s.DiasRestantes} dia{(s.DiasRestantes == 1 ? "" : "s")} p/ meta →", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextDim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dir, Dock.Right);
        row.Children.Add(dir);

        host.Content = Zui.Block(this, row,
            background: (Brush)FindResource(s.DispHoje >= 0 ? "LeafSoft" : "TangSoft"),
            onClick: () => SwitchView("Contas"),
            padding: new Thickness(14, 9, 14, 9),
            margin: new Thickness(0, 0, 0, 6));
    }

    private async Task LoadContas()
    {
        if (_contasCache is null)
        {
            ContasPanel.Children.Clear();
            ContasPanel.Children.Add(DimText("carregando o contas..."));
        }
        var snap = await Contas.Carregar();
        _contasCache = snap;
        RenderContas(snap);
    }

    private void RenderContas(Contas.Snapshot s)
    {
        ContasPanel.Children.Clear();
        ContasPanel.Children.Add(HudLabel("CONTAS - do contas.pedro"));

        if (!s.Ok)
        {
            ContasPanel.Children.Add(DimText("sem conexao com o contas agora"));
            return;
        }
        if (!s.TemMeta)
        {
            ContasPanel.Children.Add(DimText("nenhuma meta definida no contas ainda - abre o contaspedro1.netlify.app e cria a meta."));
            return;
        }

        // Hero: quanto ainda pode gastar hoje (grande)
        var heroBody = new StackPanel();
        heroBody.Children.Add(HudLabel(s.PeriodoEncerrado ? "PERIODO ENCERRADO" : "PODE GASTAR HOJE"));
        var valColor = s.DispHoje >= 0 ? "Leaf" : "Tang";
        heroBody.Children.Add(new TextBlock
        {
            Text = Contas.Fmt(s.DispHoje),
            FontSize = 34, FontFamily = (FontFamily)FindResource("Display"),
            Foreground = (Brush)FindResource(valColor),
            Margin = new Thickness(0, 2, 0, 0)
        });
        var sub = new TextBlock
        {
            FontSize = 12, Foreground = (Brush)FindResource("TextDim"),
            Margin = new Thickness(1, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        };
        sub.Inlines.Add(new Run($"orcamento do dia {Contas.Fmt(s.OrcHoje)}"));
        if (s.GastoHoje > 0) sub.Inlines.Add(new Run($"  ·  ja gastou {Contas.Fmt(s.GastoHoje)} hoje"));
        heroBody.Children.Add(sub);
        ContasPanel.Children.Add(Zui.Block(this, heroBody,
            background: (Brush)FindResource(s.DispHoje >= 0 ? "LeafSoft" : "TangSoft"),
            margin: new Thickness(0, 8, 4, 10)));

        if (s.Inviavel)
            ContasPanel.Children.Add(Zui.Block(this,
                new TextBlock { Text = "⚠ mesmo sem gastar nada, a meta nao fecha. ajuste no contas.", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Tang"), TextWrapping = TextWrapping.Wrap },
                background: (Brush)FindResource("TangSoft"), margin: new Thickness(0, 0, 4, 10)));

        // Métricas: dias restantes, livre até a meta, meta
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Metric(int col, string label, string valor, string tint)
        {
            var b = new StackPanel();
            b.Children.Add(HudLabel(label));
            b.Children.Add(new TextBlock { Text = valor, FontSize = 17, FontFamily = (FontFamily)FindResource("Display"), Foreground = (Brush)FindResource("Ink"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = Zui.Block(this, b, background: (Brush)FindResource(tint), margin: new Thickness(col == 0 ? 0 : 5, 0, col == 2 ? 4 : 5, 0));
            Grid.SetColumn(card, col);
            grid.Children.Add(card);
        }
        Metric(0, "dias ate a meta", s.DiasRestantes.ToString(), "SkySoft");
        Metric(1, "livre ate a meta", Contas.Fmt(s.Restante), "GrapeSoft");
        Metric(2, "meta", s.Alvo.ToString("dd/MM"), "SunSoft");
        ContasPanel.Children.Add(grid);

        // Atualizar conta (saldo + fatura)
        ContasPanel.Children.Add(ContaUpdateRow(s));

        // Próximos 6 meses da planilha
        if (s.Proximos6.Count > 0)
            ContasPanel.Children.Add(ProximosMesesPanel(s));

        AnimateIn(ContasPanel, fromY: 0, ms: 130);
    }

    private FrameworkElement ProximosMesesPanel(Contas.Snapshot s)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 8, 4, 4) };
        wrap.Children.Add(HudLabel("PRÓXIMOS 6 MESES - da planilha"));
        var ptbr = new CultureInfo("pt-BR");
        double maxTot = Math.Max(1, s.Proximos6.Max(m => m.Total));

        foreach (var mes in s.Proximos6)
        {
            var nome = new DateTime(mes.Ano, mes.Mes, 1).ToString("MMM/yy", ptbr);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            head.Children.Add(new TextBlock { Text = nome, FontSize = 13, FontFamily = (FontFamily)FindResource("Display"), Foreground = (Brush)FindResource("Ink"), VerticalAlignment = VerticalAlignment.Center });
            var tot = new TextBlock { Text = Contas.Fmt(mes.Total), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Ink"), HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(tot, Dock.Right);
            head.Children.Add(tot);

            var body = new StackPanel();
            body.Children.Add(head);

            // barrinha proporcional
            var track = new Border { Height = 7, CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("Mist"), Margin = new Thickness(0, 0, 0, mes.Itens.Count > 0 ? 7 : 0) };
            var fill = new Border { Height = 7, CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(6, 224 * (mes.Total / maxTot)) };
            var tg = new Grid(); tg.Children.Add(track); tg.Children.Add(fill);
            body.Children.Add(tg);

            // itens do mês
            foreach (var (item, valor) in mes.Itens.Take(6))
            {
                var r = new DockPanel { Margin = new Thickness(2, 2, 2, 0) };
                var v = new TextBlock { Text = Contas.Fmt(valor), FontSize = 11.5, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("Mono"), Foreground = (Brush)FindResource("TextDim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(v, Dock.Right);
                r.Children.Add(v);
                r.Children.Add(new TextBlock { Text = item, FontSize = 12, Foreground = (Brush)FindResource("TextMain"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                body.Children.Add(r);
            }
            if (mes.Itens.Count > 6)
                body.Children.Add(new TextBlock { Text = $"+{mes.Itens.Count - 6} outros", FontSize = 11, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(2, 3, 0, 0) });
            if (mes.Itens.Count == 0)
                body.Children.Add(new TextBlock { Text = "nada comprometido", FontSize = 11.5, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(2, 0, 0, 0) });

            wrap.Children.Add(Zui.Block(this, body, background: (Brush)FindResource("CardBg"), padding: new Thickness(13), margin: new Thickness(0, 0, 0, 8)));
        }
        return wrap;
    }

    private FrameworkElement ContaUpdateRow(Contas.Snapshot s)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        var btn = Zui.Button(this, "✎ atualizar conta");
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.FontSize = 12; btn.Background = (Brush)FindResource("Surface");
        panel.Children.Add(btn);

        var editor = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 4, 0) };

        TextBox Field(string label, double val)
        {
            editor.Children.Add(HudLabel(label));
            var tb = new TextBox { Style = (Style)FindResource("InlineAdd"), FontSize = 14, Margin = new Thickness(0, 2, 0, 8), Text = val.ToString("0.##", CultureInfo.InvariantCulture) };
            editor.Children.Add(tb);
            return tb;
        }
        var contaBox = Field("EM CONTA", s.SaldoConta);
        var faturaBox = Field("FATURA", s.Fatura);

        var acts = new DockPanel();
        var salvar = Zui.Button(this, "atualizar valores"); salvar.Background = (Brush)FindResource("Accent"); salvar.HorizontalAlignment = HorizontalAlignment.Stretch; salvar.HorizontalContentAlignment = HorizontalAlignment.Center;
        acts.Children.Add(salvar);
        editor.Children.Add(acts);
        panel.Children.Add(editor);

        btn.Click += (_, _) => { btn.Visibility = Visibility.Collapsed; editor.Visibility = Visibility.Visible; contaBox.Focus(); contaBox.SelectAll(); };

        async Task Salvar()
        {
            double P(string t) => double.TryParse(t.Trim().Replace("R$", "").Replace(",", ".").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            double saldo = P(contaBox.Text), fatura = P(faturaBox.Text);
            if (double.IsNaN(saldo) || double.IsNaN(fatura)) { ShowStatus("valores invalidos", error: true); return; }
            try
            {
                salvar.IsEnabled = false;
                await Contas.AtualizarConta(saldo, fatura);
                ShowStatus("conta atualizada");
                _contasCache = null;
                await LoadContas();
            }
            catch (Exception ex) { salvar.IsEnabled = true; ShowStatus("nao atualizou: " + ex.Message, error: true); }
        }
        salvar.Click += async (_, _) => await Salvar();
        faturaBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Salvar(); } };
        contaBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; faturaBox.Focus(); faturaBox.SelectAll(); } };
        return panel;
    }

    // -- NOT�CIAS ---------------------------------------------------

    private async Task LoadNews(bool force = false)
    {
        NewsPanel.Children.Clear();
        if (_newsTopic.Length == 0) _newsTopic = News.Categorias[0].Query;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = new Button
        {
            Style = (Style)FindResource("Chip"),
            Content = "↻",
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brushes.Transparent,
            ToolTip = "atualizar"
        };
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += (_, _) => _ = LoadNews(force: true);
        head.Children.Add(refresh);

        var chips = new WrapPanel();
        foreach (var (label, query) in News.Categorias)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                FontSize = 10.5,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Background = _newsTopic == query ? (Brush)FindResource("ChipBgHover") : Brushes.Transparent
            };
            b.Click += (_, _) => { _newsTopic = query; _ = LoadNews(); };
            chips.Children.Add(b);
        }
        head.Children.Add(chips);
        NewsPanel.Children.Add(head);

        var carregando = DimText("carregando noticias...");
        NewsPanel.Children.Add(carregando);

        try
        {
            var items = await News.Fetch(_newsTopic, force);
            NewsPanel.Children.Remove(carregando);
            if (items.Count == 0) { NewsPanel.Children.Add(DimText("nada por agora")); return; }

            var grid = new UniformGrid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(-5, 0, -5, 0)
            };
            void UpdateColumns(double width)
            {
                if (width <= 0) width = ViewHost.ActualWidth;
                grid.Columns = width >= 900 ? 4 : width >= 660 ? 3 : width >= 430 ? 2 : 1;
            }
            grid.Loaded += (_, _) => UpdateColumns(grid.ActualWidth);
            grid.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
            foreach (var n in items) grid.Children.Add(NewsCard(n));
            NewsPanel.Children.Add(grid);
            AnimateIn(grid, fromY: 8, ms: 200);
        }
        catch
        {
            NewsPanel.Children.Remove(carregando);
            NewsPanel.Children.Add(DimText("sem internet agora - tenta atualizar daqui a pouco"));
        }
    }

    /// <summary>Card quadrad�o de not�cia com a imagem da manchete no topo.</summary>
    private Border NewsCard(NewsItem n)
    {
        var sp = new StackPanel();

        // Imagem (recorte 16:9), com placeholder que some se a img carregar
        var imgHost = new Grid { Height = 150 };
        imgHost.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x7B, 0x5C, 0xD6)),
            Child = new TextBlock
            {
                Text = "img", FontSize = 16, Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        if (n.Image.Length > 0)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 400;
                bmp.UriSource = new Uri(n.Image);
                bmp.EndInit();
                var img = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                imgHost.Children.Add(img);
            }
            catch { /* fica o placeholder */ }
        }
        // Cantos de cima arredondados via clip
        var imgClip = new Border
        {
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            ClipToBounds = true,
            Child = imgHost
        };
        sp.Children.Add(imgClip);

        // Texto
        var body = new StackPanel { Margin = new Thickness(11, 9, 11, 10) };
        body.Children.Add(new TextBlock
        {
            Text = n.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 58,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(new TextBlock
        {
            Text = n.Source + (n.When != default ? "  -  " + RelTime(new DateTimeOffset(n.When).ToUnixTimeMilliseconds()) : ""),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextDone"),
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(body);

        var card = new Border
        {
            MinWidth = 210,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(5, 0, 5, 10),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Child = sp,
            ToolTip = "clica pra abrir no navegador"
        };
        card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("AccentSoft");
        card.MouseLeave += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        string link = n.Link;
        card.MouseLeftButtonUp += (_, _) => { if (link.Length > 0) { OpenExternal(link); ShowStatus("abrindo no navegador"); } };
        return card;
    }

    // -- EMAIL: hub read-only para abrir caixas por conta ------------

    private void LoadEmail()
    {
        _emailAccounts = EmailAccounts.Load();
        if (_emailAccountId != "all" && _emailAccounts.All(a => a.Id != _emailAccountId))
            _emailAccountId = "all";
        // Sem conta Gmail visível, as pastas de categoria do Gmail não retornam nada: volta pra Entrada.
        if (!EmailSelectionHasGmail() && _emailFolder.StartsWith("gmail_", StringComparison.OrdinalIgnoreCase))
            _emailFolder = "inbox";
        _emailItems.Clear();
        int gen = ++_emailReqGen;   // qualquer nova chamada invalida a busca anterior em voo
        var visibleAccounts = _emailAccountId == "all"
            ? _emailAccounts
            : _emailAccounts.Where(a => a.Id == _emailAccountId).ToList();
        string cacheKey = EmailCacheKey(visibleAccounts);
        if (!_emailForceRefresh
            && _emailCache.TryGetValue(cacheKey, out var cached)
            && (DateTime.Now - cached.At).TotalMinutes < 4)
        {
            _emailItems = cached.Items.ToList();
            _emailLoading = false;
            RenderEmail();
            return;
        }
        _emailForceRefresh = false;
        _emailLoading = visibleAccounts.Any(EmailAccounts.CanFetch);
        RenderEmail();
        if (_emailLoading) _ = LoadEmailItemsOAuth(cacheKey, gen);
    }

    private string EmailCacheKey(List<EmailAccount> accounts)
        => _emailFolder + "|" + string.Join(",", accounts.Where(EmailAccounts.CanFetch).Select(a => a.Id).OrderBy(x => x));

    private bool EmailSelectionHasGmail()
        => (_emailAccountId == "all" ? _emailAccounts : _emailAccounts.Where(a => a.Id == _emailAccountId))
            .Any(a => a.Provider == "gmail");

    private void EmailView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Se o cursor está sobre um ScrollViewer interno (corpo do e-mail) que ainda pode rolar,
        // deixa o evento seguir pra ele em vez de capturar tudo pro painel externo.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != EmailView;)
        {
            if (d is ScrollViewer inner)
            {
                bool canScroll = (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5)
                              || (e.Delta > 0 && inner.VerticalOffset > 0.5);
                if (canScroll) return;
                break;
            }
            d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
        }
        EmailView.ScrollToVerticalOffset(EmailView.VerticalOffset - e.Delta * 0.85);
        e.Handled = true;
    }

    private void RenderEmail()
    {
        EmailPanel.Children.Clear();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(Zui.Button(this, "Atualizar", onClick: (_, _) => { _emailForceRefresh = true; _emailExpandedId = null; LoadEmail(); }, tooltip: "buscar e-mails agora"));
        actions.Children.Add(Zui.Button(this, "+ Gmail", onClick: (_, _) => AddEmailAccount("gmail")));
        actions.Children.Add(Zui.Button(this, "+ Outlook", onClick: (_, _) => AddEmailAccount("outlook")));
        actions.Children.Add(Zui.Button(this, "OAuth", onClick: (_, _) => OpenEmailOAuthSettings(), tooltip: "configurar client IDs"));
        head.Children.Add(actions);
        head.Children.Add(HudLabel("EMAIL"));
        EmailPanel.Children.Add(head);

        var folderDefs = new List<(string id, string label)> { ("inbox", "Entrada") };
        if (EmailSelectionHasGmail())   // categorias só existem no Gmail
        {
            folderDefs.Add(("gmail_social", "Social"));
            folderDefs.Add(("gmail_promotions", "Promocoes"));
            folderDefs.Add(("gmail_updates", "Atualizacoes"));
            folderDefs.Add(("gmail_forums", "Foruns"));
        }
        folderDefs.Add(("spam", "Spam"));
        folderDefs.Add(("trash", "Lixo"));

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (id, label) in folderDefs)
            folders.Children.Add(EmailChip(label, _emailFolder == id, () => { _emailFolder = id; _emailExpandedId = null; LoadEmail(); }));
        EmailPanel.Children.Add(folders);

        var accounts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        accounts.Children.Add(EmailChip("Todas", _emailAccountId == "all", () => { _emailAccountId = "all"; _emailExpandedId = null; LoadEmail(); }));
        foreach (var a in _emailAccounts)
            accounts.Children.Add(EmailChip(a.DisplayName, _emailAccountId == a.Id, () => { _emailAccountId = a.Id; _emailExpandedId = null; LoadEmail(); }));
        EmailPanel.Children.Add(accounts);

        var visibleAccounts = _emailAccountId == "all"
            ? _emailAccounts
            : _emailAccounts.Where(a => a.Id == _emailAccountId).ToList();

        if (_emailAccounts.Count == 0)
        {
            EmailPanel.Children.Add(EmailEmptyCard());
            return;
        }

        var openPanel = new StackPanel();
        if (_emailLoading)
        {
            openPanel.Children.Add(DimText("carregando e-mails..."));
        }
        else if (_emailItems.Count > 0)
        {
            foreach (var item in _emailItems)
                openPanel.Children.Add(EmailMessageRow(item));
        }
        else
        {
            openPanel.Children.Add(DimText("sem mensagens OAuth nesta visao; use abrir para acessar pelo navegador"));
        }
        openPanel.Children.Add(HudLabel("ABRIR NO NAVEGADOR"));
        openPanel.Children.Add(HudLabel(EmailAccounts.FolderLabel(_emailFolder).ToUpperInvariant()));
        foreach (var account in visibleAccounts)
            openPanel.Children.Add(EmailAccountRow(account));
        EmailPanel.Children.Add(new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 10, 12, 12),
            Child = openPanel
        });
    }

    private async Task LoadEmailItemsOAuth(string cacheKey, int gen)
    {
        var visibleAccounts = _emailAccountId == "all"
            ? _emailAccounts
            : _emailAccounts.Where(a => a.Id == _emailAccountId).ToList();
        var all = new List<EmailItem>();
        var errors = new List<string>();
        foreach (var account in visibleAccounts.Where(EmailAccounts.CanFetch))
        {
            try { all.AddRange(await EmailOAuth.FetchAsync(account, _emailFolder, 25)); }
            catch (Exception ex) { errors.Add(account.DisplayName + ": " + ex.Message); } // uma conta ruim não apaga as outras
        }

        var items = all.OrderByDescending(i => i.When).Take(50).ToList();
        _emailCache[cacheKey] = (DateTime.Now, items.ToList());

        // Se o usuário trocou de conta/pasta durante a busca, descarta este resultado (não sobrescreve a tela atual).
        if (gen != _emailReqGen) return;

        _emailItems = items;
        _emailLoading = false;
        if (errors.Count > 0) ShowStatus("email: " + string.Join(" · ", errors), error: true);
        if (_currentView == "Email") RenderEmail();
    }

    private Button EmailChip(string label, bool on, Action click)
    {
        var b = Zui.Button(this, label);
        b.Background = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
        b.Foreground = on ? (Brush)FindResource("TextInk") : (Brush)FindResource("TextMain");
        b.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
        b.Click += (_, _) => click();
        return b;
    }

    private FrameworkElement EmailEmptyCard()
    {
        var body = new StackPanel();
        body.Children.Add(Zui.BodyText(this, "Nenhuma conta adicionada", 16, weight: FontWeights.SemiBold));
        body.Children.Add(Zui.DimText(this, "Adicione Gmail ou Outlook para abrir Entrada, Spam e Lixo direto no navegador."));
        return Zui.GlassCard(this, body, padding: new Thickness(16, 14, 16, 14));
    }


    private UIElement EmailMessageRow(EmailItem item)
    {
        bool expanded = _emailExpandedId == EmailMessageKey(item);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

        var meta = new StackPanel();
        meta.Children.Add(new TextBlock
        {
            Text = EmailAccounts.ProviderLabel(item.Provider),
            FontSize = 10.5,
            FontFamily = (FontFamily)FindResource("Mono"),
            Foreground = (Brush)FindResource(item.Unread ? "Accent" : "TextDim")
        });
        meta.Children.Add(new TextBlock
        {
            Text = item.AccountName,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextDone"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        grid.Children.Add(meta);

        var body = new StackPanel { Margin = new Thickness(8, 0, 12, 0) };
        body.Children.Add(new TextBlock
        {
            Text = item.From.Length == 0 ? "(sem remetente)" : item.From,
            FontSize = 14.2,
            FontWeight = item.Unread ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(new TextBlock
        {
            Text = item.Subject.Length == 0 ? "(sem assunto)" : item.Subject,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextDone"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        body.Children.Add(new TextBlock
        {
            Text = item.Snippet,
            FontSize = 11.2,
            Foreground = (Brush)FindResource("TextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var when = new TextBlock
        {
            Text = item.When.LocalDateTime.ToString(item.When.Date == DateTimeOffset.Now.Date ? "HH:mm" : "dd/MM"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextDone"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var row = new StackPanel();
        row.Children.Add(grid);
        if (expanded)
        {
            var detail = new StackPanel { Margin = new Thickness(84, 10, 8, 2) };
            detail.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = item.Body.Length > 0 ? item.Body : "carregando corpo do e-mail...",
                    FontSize = 12.2,
                    Foreground = (Brush)FindResource("TextMain"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
            detail.Children.Add(Zui.Button(this, "abrir no navegador", onClick: (_, _) =>
            {
                EmailAccounts.OpenMessage(item);
                ShowStatus("abrindo e-mail");
            }, tooltip: "abrir mensagem original"));
            row.Children.Add(detail);
        }

        var card = new Border
        {
            Background = expanded ? (Brush)FindResource("SurfaceHi") : item.Unread ? (Brush)FindResource("ChipBg") : Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 9, 8, 9),
            Cursor = Cursors.Hand,
            Child = row
        };
        card.MouseEnter += (_, _) => card.Background = (Brush)FindResource("SurfaceHi");
        card.MouseLeave += (_, _) => card.Background = expanded ? (Brush)FindResource("SurfaceHi") : item.Unread ? (Brush)FindResource("ChipBg") : Brushes.Transparent;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            _emailExpandedId = expanded ? null : EmailMessageKey(item);
            RenderEmail();
            if (!expanded && item.Body.Length == 0) _ = LoadEmailBody(item);
        };
        return card;
    }

    private static string EmailMessageKey(EmailItem item)
        => item.AccountId + ":" + item.MessageId + ":" + item.WebLink;

    private async Task LoadEmailBody(EmailItem item)
    {
        try
        {
            var account = _emailAccounts.FirstOrDefault(a => a.Id == item.AccountId);
            if (account is null) return;
            string body = await EmailOAuth.FetchBodyAsync(account, item);
            if (body.Length == 0) body = item.Snippet;
            string key = EmailMessageKey(item);
            int idx = _emailItems.FindIndex(i => EmailMessageKey(i) == key);
            if (idx >= 0)
            {
                _emailItems[idx] = _emailItems[idx] with { Body = body };
                string cacheKey = EmailCacheKey(_emailAccountId == "all" ? _emailAccounts : _emailAccounts.Where(a => a.Id == _emailAccountId).ToList());
                // preserva o timestamp original: atualizar o corpo não deve renovar a validade da lista
                var at = _emailCache.TryGetValue(cacheKey, out var prev) ? prev.At : DateTime.Now;
                _emailCache[cacheKey] = (at, _emailItems.ToList());
            }
            if (_currentView == "Email" && _emailExpandedId == key) RenderEmail();
        }
        catch (Exception ex)
        {
            ShowStatus("email: " + ex.Message, error: true);
        }
    }

    private UIElement EmailAccountRow(EmailAccount account)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var provider = new Border
        {
            Background = account.Provider == "gmail"
                ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xA8, 0xDC))
                : new SolidColorBrush(Color.FromArgb(0x22, 0x8F, 0xD0, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 5, 9, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = EmailAccounts.ProviderLabel(account.Provider),
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextMain")
            }
        };
        grid.Children.Add(provider);

        var label = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        label.Children.Add(new TextBlock
        {
            Text = account.DisplayName,
            FontSize = 13.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        label.Children.Add(new TextBlock
        {
            Text = account.Address + "  -  " + EmailAccounts.FolderLabel(_emailFolder),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var acts = new StackPanel { Orientation = Orientation.Horizontal };
        acts.Children.Add(Zui.Button(this, "abrir", onClick: (_, _) => OpenEmailFolder(account), tooltip: "abrir no navegador"));
        acts.Children.Add(Zui.Button(this, "x", onClick: (_, _) => DeleteEmailAccount(account), tooltip: "remover conta do Zimbar"));
        Grid.SetColumn(acts, 2);
        grid.Children.Add(acts);

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 9, 6, 9),
            Cursor = Cursors.Hand,
            Child = grid
        };
        card.MouseEnter += (_, _) => card.Background = (Brush)FindResource("ChipBg");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            OpenEmailFolder(account);
        };
        return card;
    }

    private static bool IsInsideButton(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OpenEmailFolder(EmailAccount account)
    {
        EmailAccounts.OpenFolder(account, _emailFolder);
        ShowStatus("abrindo " + EmailAccounts.FolderLabel(_emailFolder).ToLower(new CultureInfo("pt-BR")));
    }

    private async void AddEmailAccount(string provider)
    {
        string clientId = EmailAccounts.ClientIdFor(provider);
        if (clientId.Length == 0)
            OpenEmailOAuthSettings();
        clientId = EmailAccounts.ClientIdFor(provider);
        if (clientId.Length == 0)
        {
            ShowStatus("configure o client_id OAuth primeiro", error: true);
            return;
        }
        if (!EmailAccounts.IsClientIdPlausible(provider, clientId))
        {
            ShowStatus(EmailAccounts.ClientIdHint(provider), error: true);
            OpenEmailOAuthSettings();
            return;
        }

        bool wasTop = Topmost;
        _busyModal = true;
        Topmost = false;
        try
        {
            ShowStatus("abrindo login OAuth...");
            var oauthAccount = await EmailOAuth.ConnectAsync(provider);
            _emailAccounts = EmailAccounts.Load();
            // dedup por provedor+endereço: reconectar a mesma caixa substitui em vez de duplicar
            // (BuildAccount gera um Id novo a cada login, então Upsert por Id não bastaria).
            _emailAccounts.RemoveAll(a => a.Provider == oauthAccount.Provider
                && a.Address.Equals(oauthAccount.Address, StringComparison.OrdinalIgnoreCase));
            _emailAccounts.Add(oauthAccount);
            EmailAccounts.Save(_emailAccounts);
            _emailAccountId = oauthAccount.Id;
            LoadEmail();
            ShowStatus("conta conectada");
        }
        catch (Exception ex)
        {
            ShowStatus("email: " + ex.Message, error: true);
        }
        finally
        {
            Topmost = wasTop;
            _busyModal = false;
        }
    }


    private void OpenEmailOAuthSettings()
    {
        bool wasTop = Topmost;
        _busyModal = true;
        Topmost = false;
        try
        {
            var dlg = new EmailOAuthSettingsDialog { Owner = this };
            dlg.ShowDialog();
        }
        finally
        {
            Topmost = wasTop;
            _busyModal = false;
        }
    }

    private void DeleteEmailAccount(EmailAccount account)
    {
        _emailAccounts = EmailAccounts.Load();
        _emailAccounts.RemoveAll(a => a.Id == account.Id);
        EmailAccounts.Save(_emailAccounts);
        if (_emailAccountId == account.Id) _emailAccountId = "all";
        _emailCache.Clear();   // o cache tinha e-mails da conta removida (podiam ressuscitar numa troca de pasta)
        LoadEmail();           // recarrega do zero em vez de reusar _emailItems antigo
        ShowStatus("conta removida");
    }


    private void Notas_Click(object sender, RoutedEventArgs e)
    {
        HideBar();
        NotesWindow.Open();
    }

    private void Pomo_Click(object sender, RoutedEventArgs e) => PomoPopup.IsOpen = true;

    private void PomoStart_Click(object sender, RoutedEventArgs e)
    {
        PomoPopup.IsOpen = false;
        if (sender is Button { Tag: string t })
        {
            var parts = t.Split(',');
            HideBar();
            PomoWindow.Launch(int.Parse(parts[0]), int.Parse(parts[1]));
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ThemePopup.IsOpen = true;

    private void BuildThemeList()
    {
        ThemeList.Children.Clear();
        ThemeList.Children.Add(Zui.SectionLabel(this, "Cores"));
        foreach (var (name, pal) in ThemeManager.Themes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16, Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pal.Accent)),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextMain"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var b = new Button { Style = (Style)FindResource("GhostItem"), Content = row };
            b.Click += (_, _) =>
            {
                ThemeManager.Apply(name);
                Config.Save();
                ThemePopup.IsOpen = false;
                ApplyShellDesign();
                SwitchView(_currentView); // re-renderiza com as cores novas
            };
            ThemeList.Children.Add(b);
        }
    }

    // -- Utilit�rios ------------------------------------------------

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");
    private static long Ms() => DateTimeOffset.Now.ToUnixTimeMilliseconds();
    private static DateTime Monday(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static async Task<JsonArray> GetKvArray(string k)
        => await Supa.GetKv(k) is string s && JsonNode.Parse(s) is JsonArray a ? a : new JsonArray();

    private static Task SetKvArray(string k, JsonArray a) => Supa.SetKv(k, a.ToJsonString());

    private static async Task PushKvList(string k, JsonObject item)
    {
        var arr = await GetKvArray(k);
        arr.Insert(0, item);
        await SetKvArray(k, arr);
    }

    private static string RelTime(double tsMs)
    {
        if (tsMs <= 0) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)tsMs).LocalDateTime;
        var diff = DateTime.Now - dt;
        if (diff.TotalMinutes < 60) return $"ha {(int)diff.TotalMinutes}min";
        if (diff.TotalHours < 24) return $"ha {(int)diff.TotalHours}h";
        return $"ha {(int)diff.TotalDays}d";
    }

    /// <summary>Caixinha de adi��o sutil usada no rodap� de cada aba.</summary>
    private FrameworkElement MakeAddBox(string placeholder, Func<string, Task> onEnter)
        => Zui.InlineAddBox(this, placeholder, onEnter, ShowStatus);

    /// <summary>
    /// Adi��o discreta: mostra s� um bot�o �+ label�; ao clicar, revela o campo,
    /// foca, e some de novo quando voc� confirma (Enter) ou desiste (Esc/vazio).
    /// Menos caixas de texto poluindo a tela.
    /// </summary>
    private FrameworkElement RevealAdd(string label, string placeholder, Func<string, Task> onEnter)
        => Zui.RevealAdd(this, label, placeholder, onEnter, ShowStatus);

    private static (string Titulo, string Data) ParseDataDoTexto(string text)
    {
        var hoje = DateTime.Now.Date;
        var m = Regex.Match(text, @"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b");
        if (m.Success)
        {
            int d = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value);
            int y = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : hoje.Year;
            if (y < 100) y += 2000;
            try
            {
                var dt = new DateTime(y, mo, d);
                if (!m.Groups[3].Success && dt < hoje) dt = dt.AddYears(1);
                return (RemoveToken(text, m.Value), dt.ToString("yyyy-MM-dd"));
            }
            catch (ArgumentOutOfRangeException) { }
        }
        var mA = Regex.Match(text, @"\bamanh[aã]\b", RegexOptions.IgnoreCase);
        if (mA.Success) return (RemoveToken(text, mA.Value), hoje.AddDays(1).ToString("yyyy-MM-dd"));
        var mH = Regex.Match(text, @"\bhoje\b", RegexOptions.IgnoreCase);
        if (mH.Success) return (RemoveToken(text, mH.Value), hoje.ToString("yyyy-MM-dd"));
        return (text, hoje.ToString("yyyy-MM-dd"));
    }

    private static string RemoveToken(string text, string token)
        => Regex.Replace(text.Replace(token, " ").Trim(), @"\s{2,}", " ");

    private TextBlock SectionLabel(string text) => Zui.SectionLabel(this, text);

    private TextBlock DimText(string text) => Zui.DimText(this, text);

    private static (string Kind, string Title, string Content) ParseLinkInput(string raw)
    {
        raw = raw.Trim();
        string title = "";
        string content = raw;

        int pipe = raw.IndexOf('|');
        if (pipe > 0)
        {
            title = raw[..pipe].Trim();
            content = raw[(pipe + 1)..].Trim();
        }

        bool link = LooksLikeUrl(content);
        if (link)
        {
            content = EnsureUrl(content);
            if (title.Length == 0) title = LinkLabel(content);
        }

        return (link ? "link" : "texto", title, content);
    }

    private static string EnsureUrl(string url)
        => url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;

    private static string LinkLabel(string url)
    {
        try
        {
            var u = new Uri(EnsureUrl(url));
            var path = u.AbsolutePath is "/" ? "" : u.AbsolutePath.TrimEnd('/');
            return (u.Host + path).TrimEnd('/');
        }
        catch
        {
            return url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        }
    }

    private static bool LooksLikeUrl(string t)
        => !t.Contains(' ') && (t.Contains("://") || (t.Contains('.') && t.Length > 3));

    private static void OpenExternal(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
