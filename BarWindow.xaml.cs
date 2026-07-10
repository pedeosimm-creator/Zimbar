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
    private string? _refsCat;              // null = todas
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
        ViewHost.MaxHeight = Config.ViewMax ?? 430;
        _statusTimer.Tick += (_, _) => { StatusText.Visibility = Visibility.Collapsed; _statusTimer.Stop(); };
        _playerTimer.Tick += (_, _) => _ = RenderTopPlayer();
        _refreshTimer.Tick += (_, _) => AutoRefresh();
        Activated += (_, _) => AutoRefresh();   // (A) recarrega quando a barra ganha foco
        Deactivated += (_, _) => CollapseBar(); // clicar fora minimiza pra barra de tarefas
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
        var wa = SystemParameters.WorkArea;
        var w = Math.Min(ActualWidth > 0 ? ActualWidth : Width, wa.Width);
        var h = Math.Min(ActualHeight > 0 ? ActualHeight : 140, wa.Height);
        Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - w));
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
    }

    /// <summary>Clicar fora minimiza a barra pra barra de tarefas, como uma janela normal.</summary>
    private void CollapseBar()
    {
        if (!IsVisible || _busyModal) return;
        if (WindowState == WindowState.Minimized) return;
        if (IsUserBusy()) return;   // popup/edi��o/arraste aberto: n�o � "clicar fora" de verdade

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
        if (PomoPopup.IsOpen || ModePickerPopup.IsOpen || DatePopup.IsOpen
            || ThemePopup.IsOpen || KbEditPopup.IsOpen) return true;
        if (_hojeRenameId != null || _refRenameId != null || _catRename != null) return true;
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
            case "Refs": _ = LoadRefs(); break;
            case "Links": _ = LoadLinks(); break;
            case "Listas": _ = LoadListas(); break;
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
                Key.D5 => "Refs", Key.D6 => "Links", Key.D7 => "Listas", Key.D8 => "News",
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
        _v0 = _pendV = ViewHost.MaxHeight;
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
        ViewHost.MaxHeight = _pendV;
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sizing) return;
        _sizing = false;
        CompositionTarget.Rendering -= Sizing_Rendering;
        ((UIElement)sender).ReleaseMouseCapture();
        Width = _pendW;
        ViewHost.MaxHeight = _pendV;
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
            ("Agenda", AgendaView), ("Refs", RefsView),
            ("Links", LinksView), ("Listas", ListasView), ("News", NewsView),
            ("Email", EmailView),
            ("Busca", BuscaView),
        };
        foreach (var (name, el) in views)
            el.Visibility = name == view ? Visibility.Visible : Visibility.Collapsed;

        // Aba ativa vira bloco vivo (accent + texto tinta); as outras, transparentes
        var accent = (Brush)FindResource("Accent");
        var ink = (Brush)FindResource("TextInk");
        var txt = (Brush)FindResource("TextMain");
        foreach (var (btn, name) in new[]
        {
            (NavPainel, "Painel"), (NavHoje, "Hoje"), (NavKanban, "Kanban"), (NavAgenda, "Agenda"),
            (NavRefs, "Refs"), (NavLinks, "Links"), (NavListas, "Listas"), (NavNews, "News"), (NavEmail, "Email")
        })
        {
            bool on = name == view;
            btn.Background = on ? accent : Brushes.Transparent;
            btn.Foreground = on ? ink : txt;
            btn.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
        }

        var visivel = views.FirstOrDefault(v => v.Name == view).El;
        if (visivel is not null) AnimateIn(visivel, fromY: 8, ms: 170);

        switch (view)
        {
            case "Painel": _ = LoadPainel(); break;
            case "Hoje": _ = LoadHoje(); break;
            case "Kanban": _ = LoadKanban(); break;
            case "Agenda": _ = LoadAgenda(); break;
            case "Refs": _ = LoadRefs(); break;
            case "Links": _ = LoadLinks(); break;
            case "Listas": _ = LoadListas(); break;
            case "News": _ = LoadNews(); break;
            case "Email": LoadEmail(); break;
            case "Busca": _ = LoadBusca(); break;
        }
    }

    private void ApplyShellDesign()
    {
        ShellRail.Visibility = Visibility.Collapsed;
        RailColumn.Width = new GridLength(0);
        ShellStack.Margin = new Thickness(20, 16, 22, 12);
        CommandSurface.BorderBrush = (Brush)FindResource("CardBorderBrush");
        CommandSurface.BorderThickness = new Thickness(1);
        CommandSurface.Padding = new Thickness(12, 9, 12, 9);
        CommandDock.Margin = new Thickness(0);
        DecorLayer.Visibility = Visibility.Collapsed;
        DecorLayer.Opacity = 1;
        DecorScanline.Opacity = 0;
        DecorOrbit.Opacity = 0;
        Card.BorderThickness = new Thickness(1);
        Card.Padding = new Thickness(0);
        ViewHost.Margin = new Thickness(0, 4, 0, 0);

        SetNavLabels(full: true, orbital: false);
        NavItems.Margin = new Thickness(0);
        ActionItems.Margin = new Thickness(0);
        ShellSeparator.Height = 1;
        ShellSeparator.Margin = new Thickness(2, 12, 2, 2);
        RailMark.Text = "Z";
        RailTitle.Text = "HUD";
        RailMode.Text = "CAPTURE";
        DragGlyph.Text = "⠿";
        Card.CornerRadius = new CornerRadius(28);
        CommandSurface.Background = (Brush)FindResource("SurfaceHi");
        CommandSurface.CornerRadius = new CornerRadius(18);
        ShellSeparator.Opacity = 0.16;
        RailMark.Text = "Z";
        RailTitle.Text = "AURORA\nFLOW";
        RailMode.Text = "SOFT HUD";
    }

    private void SetNavLabels(bool full, bool orbital)
    {
        (Button Btn, string Full, string Compact, string Orbital)[] items =
        {
            (NavPainel, "◈ Painel", "◈", "01 PAN"),
            (NavHoje, "☀ Hoje", "☀", "02 HOJ"),
            (NavKanban, "◱ Kanban", "◱", "03 KBN"),
            (NavAgenda, "◷ Agenda", "◷", "04 AGD"),
            (NavRefs, "◇ Referencias", "◇", "05 REF"),
            (NavLinks, "⌘ Links", "⌘", "06 LNK"),
            (NavListas, "☰ Listas", "☰", "07 LST"),
            (NavNews, "✷ Noticias", "✷", "08 NEW"),
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

    // -- Entrada com 5 modos: captura � busca � web � evento � refer�ncia --

    private static readonly (string Mode, string Icon, string Label, string HintText)[] Modes =
    {
        ("captura", "+", "captura", "esvazia a cabeca - Enter joga na captura, voce decide depois..."),
        ("busca", "B", "busca", "busca em tudo - tarefas, agenda, notas, referencias, listas..."),
        ("web", "W", "web", "pesquisa na internet - Enter abre no navegador..."),
        ("evento", "E", "evento", "novo evento - ex: 12/08 gravacao, ou amanha reuniao..."),
        ("referencia", "R", "referencia", "guarda uma referencia - #categoria no comeco classifica..."),
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

    private DateTime _eventoDate = DateTime.Now.Date;
    private string _refTargetCat = "";

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
        UpdateModePicker();
    }

    private static string DayLabel(DateTime d)
    {
        var hoje = DateTime.Now.Date;
        if (d == hoje) return "hoje";
        if (d == hoje.AddDays(1)) return "amanha";
        var culture = new CultureInfo("pt-BR");
        return $"{culture.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).TrimEnd('.')} {d:dd/MM}";
    }

    /// <summary>Atualiza o clicador sutil no fim da barra conforme o modo.</summary>
    private void UpdateModePicker()
    {
        if (_inputMode == "evento")
        {
            ModePicker.Visibility = Visibility.Visible;
            ModePicker.Content = "data  " + DayLabel(_eventoDate) + "  v";
        }
        else if (_inputMode == "referencia")
        {
            ModePicker.Visibility = Visibility.Visible;
            ModePicker.Content = "pasta  " + (_refTargetCat.Length == 0 ? "sem categoria" : "#" + _refTargetCat) + "  v";
        }
        else ModePicker.Visibility = Visibility.Collapsed;
    }

    private void ModePicker_Click(object sender, RoutedEventArgs e)
    {
        if (_inputMode == "evento")
            OpenDatePicker(ModePicker, _eventoDate, d => { _eventoDate = d; UpdateModePicker(); Input.Focus(); });
        else if (_inputMode == "referencia")
            _ = FillRefPicker();
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

    private async Task FillRefPicker()
    {
        var cats = await RefCats(await GetKvArray("me2_ideias"));
        ModePickerList.Children.Clear();
        ModePickerList.Children.Add(PickerOption("sem categoria", _refTargetCat.Length == 0, () =>
        { _refTargetCat = ""; ModePickerPopup.IsOpen = false; UpdateModePicker(); Input.Focus(); }));
        foreach (var c in cats)
        {
            string cc = c;
            ModePickerList.Children.Add(PickerOption("#" + c, _refTargetCat == cc, () =>
            { _refTargetCat = cc; ModePickerPopup.IsOpen = false; UpdateModePicker(); Input.Focus(); }));
        }
        ModePickerPopup.IsOpen = true;
    }

    private Button PickerOption(string label, bool on, Action onClick)
    {
        var b = new Button
        {
            Style = (Style)FindResource("GhostItem"),
            Content = new TextBlock
            {
                Text = (on ? "✓ " : "   ") + label, FontSize = 12.5,
                Foreground = (Brush)FindResource(on ? "Accent" : "TextMain")
            },
            Padding = new Thickness(9, 5, 9, 5)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
        => Hint.Visibility = string.IsNullOrEmpty(Input.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        e.Handled = true;

        if (_inputMode == "web")
        {
            OpenExternal("https://www.google.com/search?q=" + Uri.EscapeDataString(text));
            Input.Clear();
            HideBar();
            return;
        }

        if (_inputMode == "busca")
        {
            _buscaQuery = text;
            SwitchView("Busca");
            return;
        }

        if (_inputMode == "evento")
        {
            try
            {
                // Dia escolhido nos chips; se escrever uma data no texto, ela manda.
                string titulo = text;
                string data = _eventoDate.ToString("yyyy-MM-dd");
                var (t2, d2) = ParseDataDoTexto(text);
                if (d2 != DateTime.Now.Date.ToString("yyyy-MM-dd") || Regex.IsMatch(text, @"\d{1,2}/\d{1,2}|amanh|hoje"))
                { titulo = t2; data = d2; }
                await Supa.Insert("tarefas", new JsonObject
                {
                    ["id"] = "t" + Ms(),
                    ["titulo"] = titulo,
                    ["descricao"] = "",
                    ["status"] = "a fazer",
                    ["prazo"] = data
                });
                Input.Clear();
                ShowStatus($"evento em {DateTime.Parse(data):dd/MM}");
                if (_currentView is "Agenda" or "Painel") SwitchView(_currentView);
            }
            catch (Exception ex) { ShowStatus("nao criou o evento: " + ex.Message, error: true); }
            return;
        }

        if (_inputMode == "referencia")
        {
            try
            {
                var item = new JsonObject { ["id"] = Supa.NewId(), ["ts"] = Ms() };
                var m = Regex.Match(text, @"^#([\w\p{L}-]+)\s+(.+)$");
                if (m.Success) { item["cat"] = m.Groups[1].Value.ToLower(new CultureInfo("pt-BR")); item["text"] = m.Groups[2].Value; }
                else { item["text"] = text; if (_refTargetCat.Length > 0) item["cat"] = _refTargetCat; }
                await PushKvList("me2_ideias", item);
                Input.Clear();
                ShowStatus(_refTargetCat.Length > 0 ? $"referencia em #{_refTargetCat}" : "referencia guardada");
                if (_currentView == "Refs") await LoadRefs();
            }
            catch (Exception ex) { ShowStatus("nao guardou: " + ex.Message, error: true); }
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

        // -- Hero minimalista: sauda��o (display fino) + data --
        var hero = new StackPanel { Margin = new Thickness(2, 0, 2, 14) };
        hero.Children.Add(new TextBlock
        {
            Text = saud + ", Pedro",
            FontSize = 25,
            FontFamily = (FontFamily)FindResource("Display"),
            Foreground = (Brush)FindResource("TextMain"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.4, Color = ((SolidColorBrush)FindResource("Accent")).Color }
        });
        hero.Children.Add(new TextBlock
        {
            Text = culture.TextInfo.ToTitleCase(hoje.ToString("dddd, dd 'de' MMMM", culture)).ToUpper(culture),
            FontSize = 10, FontFamily = (FontFamily)FindResource("Mono"),
            Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(1, 3, 0, 0)
        });
        PainelPanel.Children.Add(hero);

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
        Grid.SetColumn(GlassCardInto(grid, 0, hojeBody, "Hoje"), 0);

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
        Grid.SetColumn(GlassCardInto(grid, 1, proxBody, "Agenda"), 1);

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

    private Border GlassCardInto(Grid g, int col, UIElement body, string? gotoView)
    {
        var card = GlassCard(body, gotoView);
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

    /// <summary>Card de vidro futurista com brilho na borda ao passar o mouse.</summary>
    private Border GlassCard(UIElement body, string? gotoView)
    {
        return Zui.GlassCard(this, body, gotoView is null ? null : () => SwitchView(gotoView));
    }

    private int ResponsiveColumns(double minItemWidth, int maxColumns)
    {
        var width = ViewHost.ActualWidth > 1 ? ViewHost.ActualWidth : ActualWidth - 60;
        width = Math.Max(minItemWidth, width - 18);
        return Math.Max(1, Math.Min(maxColumns, (int)Math.Floor(width / minItemWidth)));
    }

    private Border StatCard(string title, UIElement body, string? gotoView)
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

    private Border CapturaItem(JsonObject item, bool compact = false)
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
        Act("ideia", () => PushKvList("me2_ideias", new JsonObject
        { ["id"] = Supa.NewId(), ["text"] = text, ["ts"] = Ms() }));

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

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x7B, 0x5C, 0xD6)),
            BorderBrush = (Brush)FindResource("AccentSoft"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8, 10, 7),
            Margin = compact ? new Thickness(0, 0, 8, 8) : new Thickness(0, 0, 0, 7),
            Child = sp
        };
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

        foreach (var (arr, titulo, corKey, bgKey) in Niveis)
        {
            var section = new StackPanel();
            // Etiqueta do n�vel: bolinha de cor + nome (harmonizado)
            var tag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tag.Children.Add(new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource(corKey), Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            tag.Children.Add(new TextBlock
            {
                Text = titulo, FontSize = 10, FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource(corKey), VerticalAlignment = VerticalAlignment.Center
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
                    Foreground = (Brush)FindResource("TextDone"),
                    Margin = new Thickness(6, 0, 0, 2)
                });

            var secBorder = new Border
            {
                Background = (Brush)FindResource(bgKey),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(13, 11, 13, 11),
                Margin = new Thickness(0, 0, 0, 8),
                AllowDrop = true,
                Tag = arr
            };
            secBorder.Child = section;
            secBorder.DragEnter += (_, _) => secBorder.BorderBrush = (Brush)FindResource(corKey);
            secBorder.DragLeave += (_, _) => secBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            secBorder.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            secBorder.Drop += async (_, e) =>
            {
                secBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                if (e.Data.GetData(DataFormats.Text) is string dragId) await MoveHojeById(dragId, arr, null);
            };
            HojePanel.Children.Add(secBorder);
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
        double h = tall ? 8 : 5;
        var g = new Grid { Height = h, Margin = new Thickness(0, 4, tall ? 2 : 20, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.001), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.001), GridUnitType.Star) });
        var fill = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(h / 2) };
        if (tall) fill.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6, Color = ((SolidColorBrush)FindResource("Accent")).Color };
        var rest = new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(h / 2) };
        Grid.SetColumn(fill, 0); Grid.SetColumn(rest, 1);
        g.Children.Add(fill); g.Children.Add(rest);
        return g;
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
            Content = "data  " + DayLabel(_agendaAddDate) + "  v",
            FontSize = 11.5, Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        dayBtn.Click += (_, _) => OpenDatePicker(dayBtn, _agendaAddDate,
            d => { _agendaAddDate = d; dayBtn.Content = "data  " + DayLabel(d) + "  v"; });
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

            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = dia.ToString(), FontSize = 12,
                FontWeight = ehHoje ? FontWeights.Bold : FontWeights.Normal,
                Foreground = (Brush)FindResource(ehHoje ? "Accent" : "TextDim"),
                Margin = new Thickness(1, 0, 0, 3)
            });

            int mostrados = 0;
            foreach (var t in evs)
            {
                if (mostrados >= 3) break;
                var tt = t;
                var chip = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x26, 0x8F, 0xD0, 0xFF)),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = t["titulo"]?.GetValue<string>() ?? "", FontSize = 9.5,
                        Foreground = (Brush)FindResource("TextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                chip.MouseLeftButtonUp += (_, ev) => { OpenKbEdit(tt); ev.Handled = true; };
                cell.Children.Add(chip);
                mostrados++;
            }
            foreach (var texto in recurs)
            {
                if (mostrados >= 3) break;
                var tb = new TextBlock
                {
                    FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(1, 0, 0, 2)
                };
                tb.Inlines.Add(new Run("- ") { Foreground = (Brush)FindResource("BlockBlue") });
                tb.Inlines.Add(new Run(texto) { Foreground = (Brush)FindResource("TextDim") });
                cell.Children.Add(tb);
                mostrados++;
            }
            int total = evs.Count + recurs.Count;
            if (total > mostrados)
                cell.Children.Add(new TextBlock { Text = $"+{total - mostrados}", FontSize = 9, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(1, 0, 0, 0) });

            var cellBorder = new Border
            {
                MinHeight = 66, Margin = new Thickness(1.5),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 4, 5, 4),
                Background = ehHoje ? (Brush)FindResource("Surface") : new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                BorderBrush = ehHoje ? (Brush)FindResource("AccentSoft") : Brushes.Transparent,
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

            var col = new StackPanel { MinHeight = 138 };
            var head = new DockPanel { Margin = new Thickness(2, 0, 2, 11) };
            var countPill = new Border
            {
                Background = (Brush)FindResource("ChipBg"),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = itens.Count.ToString(),
                    FontSize = 10,
                    FontFamily = (FontFamily)FindResource("Mono"),
                    Foreground = (Brush)FindResource("TextDim")
                }
            };
            DockPanel.SetDock(countPill, Dock.Right);
            head.Children.Add(countPill);
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new Border
            {
                Width = 7, Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = accent,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = status.ToUpper(new CultureInfo("pt-BR")),
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("Mono"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain"),
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(titleRow);
            col.Children.Add(head);

            foreach (var t in itens) col.Children.Add(KanbanCard(t));

            var colInner = new Border
            {
                Background = (Brush)FindResource("Surface"),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(10, 10, 10, 8),
                MinHeight = 120,
                Child = col
            };
            var colBorder = new Border
            {
                AllowDrop = true,
                Background = Brushes.Transparent, // precisa pro hit-test do drop
                Margin = new Thickness(c == 0 ? 0 : 8, 0, 0, 0),
                Child = colInner,
                Tag = status
            };
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
            ? (Brush)FindResource("Accent")
            : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        lane.Background = active
            ? (Brush)FindResource("SurfaceHi")
            : (Brush)FindResource("Surface");
        var accentColor = FindResource("Accent") is SolidColorBrush accentBrush
            ? accentBrush.Color
            : Color.FromRgb(125, 255, 215);
        lane.Effect = active
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.32,
                Color = accentColor
            }
            : null;

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

    private Border KanbanCard(JsonObject t)
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
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Opacity = 0.85
        });
        Grid.SetColumn(sp, 1);
        body.Children.Add(sp);

        var card = new Border
        {
            Background = (Brush)FindResource("ChipBg"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Child = body,
            ToolTip = "clica: editar - arrasta: mover de coluna"
        };
        card.MouseEnter += (_, _) =>
        {
            card.Background = (Brush)FindResource("SurfaceHi");
            card.BorderBrush = (Brush)FindResource("AccentSoft");
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = (Brush)FindResource("ChipBg");
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        };

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
        return card;
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
        KbEdDateBtn.Content = "data  " + (_kbEditDate is DateTime d ? d.ToString("dd/MM/yyyy") : "sem data");
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

    // -- REFER�NCIAS (me2_ideias) com categorias --------------------

    private bool _refDragging;
    private Point _refDownPos;

    private async Task<List<string>> RefCats(JsonArray ideias)
    {
        var stored = (await GetKvArray("zimbar_ref_cats"))
            .Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0);
        var fromItems = ideias.OfType<JsonObject>()
            .Select(r => r["cat"]?.GetValue<string>() ?? "").Where(s => s.Length > 0);
        return stored.Concat(fromItems).Distinct().OrderBy(s => s).ToList();
    }

    private static readonly string[] CatBlocos =
        { "BlockPurple", "BlockBlue", "BlockLime", "BlockYellow", "BlockPink", "BlockCoral" };

    private async Task LoadRefs()
    {
        try
        {
            var ideias = await GetKvArray("me2_ideias");
            RefsPanel.Children.Clear();

            // Cabe�alho: r�tulo + bot�o "+ categoria" � direita
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var addCat = RevealAdd("+ categoria", "nome da categoria (ex: inspiracao)", async text =>
            {
                var nome = text.TrimStart('#').Trim().ToLower(new CultureInfo("pt-BR"));
                if (nome.Length == 0) return;
                var lista = await GetKvArray("zimbar_ref_cats");
                if (!lista.Any(x => (x?.GetValue<string>() ?? "") == nome)) lista.Add(nome);
                await SetKvArray("zimbar_ref_cats", lista);
                await LoadRefs();
            });
            addCat.Margin = new Thickness(0);
            DockPanel.SetDock(addCat, Dock.Right);
            head.Children.Add(addCat);
            head.Children.Add(HudLabel("REFERENCIAS"));
            RefsPanel.Children.Add(head);

            var cats = await RefCats(ideias);
            var itens = ideias.OfType<JsonObject>().ToList();

            int ci = 0;
            bool algo = false;
            var mapCols = cats.Count + (itens.Any(r => (r["cat"]?.GetValue<string>() ?? "").Length == 0) ? 1 : 0);
            var map = new UniformGrid
            {
                Columns = Math.Min(4, Math.Max(1, mapCols)),
                Margin = new Thickness(0, 2, 0, 6)
            };

            // Mapa por categoria: cada categoria vira uma lane compacta.
            foreach (var c in cats)
            {
                var doGrupo = itens.Where(r => (r["cat"]?.GetValue<string>() ?? "") == c).ToList();
                map.Children.Add(RefSection(c, c, CatBlocos[ci++ % CatBlocos.Length], doGrupo));
                algo = true;
            }
            // Sem categoria
            var semcat = itens.Where(r => (r["cat"]?.GetValue<string>() ?? "").Length == 0).ToList();
            if (semcat.Count > 0)
            {
                map.Children.Add(RefSection("sem categoria", "", null, semcat));
                algo = true;
            }

            if (algo) RefsPanel.Children.Add(map);

            if (!algo)
                RefsPanel.Children.Add(DimText("nada aqui ainda - use o modo referencia la em cima, ou o botao abaixo. crie categorias com + categoria."));

            RefsPanel.Children.Add(RevealAdd("+ referencia", "#categoria no comeco classifica (ex: #design https://...)", async text =>
            {
                var item = new JsonObject { ["id"] = Supa.NewId(), ["ts"] = Ms() };
                var m = Regex.Match(text, @"^#([\w\p{L}-]+)\s+(.+)$");
                if (m.Success) { item["cat"] = m.Groups[1].Value.ToLower(new CultureInfo("pt-BR")); item["text"] = m.Groups[2].Value; }
                else item["text"] = text;
                await PushKvList("me2_ideias", item);
                await LoadRefs();
            }));
        }
        catch
        {
            RefsPanel.Children.Clear();
            RefsPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    /// <summary>Se��o de uma categoria: cabe�alho colorido (alvo de arrasto) + itens listados.</summary>
    private Border RefSection(string titulo, string cat, string? blockKey, List<JsonObject> itens)
    {
        var bloco = blockKey is not null ? (Brush)FindResource(blockKey) : (Brush)FindResource("TextDone");
        var sp = new StackPanel { MinHeight = 126 };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var count = new Border
        {
            Background = (Brush)FindResource("ChipBg"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock
            {
                Text = itens.Count.ToString(),
                FontSize = 10,
                FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextDim")
            }
        };
        DockPanel.SetDock(count, Dock.Right);
        head.Children.Add(count);

        // Editar/excluir categoria (s� categorias reais)
        if (cat.Length > 0)
        {
            var catActs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(catActs, Dock.Right);
            Button CatBtn(string g, string tip)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("NavBtn"), Content = g, FontSize = 10.5,
                    Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(2, 0, 0, 0),
                    Foreground = (Brush)FindResource("TextDone"), ToolTip = tip
                };
                catActs.Children.Add(b);
                return b;
            }
            CatBtn("✎", "renomear categoria").Click += (_, _) => _ = RenameCategoria(cat);
            CatBtn("✕", "excluir categoria (itens viram sem categoria)").Click += async (_, _) => await DeleteCategoria(cat);
            head.Children.Add(catActs);
        }

        head.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(3),
            Background = bloco, Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (cat.Length > 0 && _catRename == cat)
        {
            var box = new TextBox { Style = (Style)FindResource("InlineAdd"), Text = cat, FontSize = 12.5, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) { _catRename = null; await LoadRefs(); e.Handled = true; }
                else if (e.Key == Key.Enter) { e.Handled = true; await ApplyRenameCategoria(cat, box.Text); }
            };
            head.Children.Add(box);
        }
        else
            head.Children.Add(new TextBlock
            {
                Text = titulo.ToUpper(new CultureInfo("pt-BR")),
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("Mono"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain"), VerticalAlignment = VerticalAlignment.Center
            });
        sp.Children.Add(head);

        if (itens.Count == 0)
            sp.Children.Add(new TextBlock
            {
                Text = "solte referencias aqui", FontSize = 11,
                Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(2, 2, 0, 2)
            });
        else
            foreach (var r in itens) sp.Children.Add(RefIdeiaItem(r, showCat: false));

        var card = new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12, 11, 12, 11),
            Margin = new Thickness(0, 0, 9, 9),
            AllowDrop = true,
            Child = sp
        };
        // Soltar um item aqui = classifica nesta categoria (ou remove, se "sem categoria")
        card.DragEnter += (_, _) => card.BorderBrush = (Brush)FindResource("Accent");
        card.DragLeave += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        card.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        card.Drop += async (_, e) =>
        {
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            if (e.Data.GetData(DataFormats.Text) is string dragId) await SetRefCat(dragId, cat);
        };
        return card;
    }

    private async Task SetRefCat(string id, string cat)
    {
        try
        {
            var arr = await GetKvArray("me2_ideias");
            foreach (var n in arr.OfType<JsonObject>())
                if (n["id"]?.GetValue<string>() == id) n["cat"] = cat;
            await SetKvArray("me2_ideias", arr);
            ShowStatus(cat.Length == 0 ? "tirado da categoria" : "classificado em #" + cat);
            await LoadRefs();
        }
        catch { ShowStatus("nao classificou", error: true); }
    }

    private string? _catRename;

    private Task RenameCategoria(string cat) { _catRename = cat; return LoadRefs(); }

    private async Task ApplyRenameCategoria(string oldCat, string novoRaw)
    {
        _catRename = null;
        var novo = novoRaw.TrimStart('#').Trim().ToLower(new CultureInfo("pt-BR"));
        if (novo.Length == 0 || novo == oldCat) { await LoadRefs(); return; }
        try
        {
            var ideias = await GetKvArray("me2_ideias");
            foreach (var n in ideias.OfType<JsonObject>())
                if ((n["cat"]?.GetValue<string>() ?? "") == oldCat) n["cat"] = novo;
            await SetKvArray("me2_ideias", ideias);

            var lista = await GetKvArray("zimbar_ref_cats");
            var nl = new JsonArray();
            foreach (var x in lista) { var s = x?.GetValue<string>() ?? ""; if (s.Length > 0 && s != oldCat) nl.Add(s); }
            if (!nl.Any(x => x?.GetValue<string>() == novo)) nl.Add(novo);
            await SetKvArray("zimbar_ref_cats", nl);
            ShowStatus($"categoria virou #{novo}");
            await LoadRefs();
        }
        catch { ShowStatus("nao renomeou", error: true); await LoadRefs(); }
    }

    private async Task DeleteCategoria(string cat)
    {
        try
        {
            var ideias = await GetKvArray("me2_ideias");
            foreach (var n in ideias.OfType<JsonObject>())
                if ((n["cat"]?.GetValue<string>() ?? "") == cat) n["cat"] = "";
            await SetKvArray("me2_ideias", ideias);

            var lista = await GetKvArray("zimbar_ref_cats");
            var nl = new JsonArray();
            foreach (var x in lista) { var s = x?.GetValue<string>() ?? ""; if (s.Length > 0 && s != cat) nl.Add(s); }
            await SetKvArray("zimbar_ref_cats", nl);
            ShowStatus($"categoria #{cat} excluida");
            await LoadRefs();
        }
        catch { ShowStatus("nao excluiu", error: true); }
    }

    /// <summary>Move a refer�ncia arrastada pra antes do alvo (reordena me2_ideias).</summary>
    private async Task ReorderRef(string dragId, string targetId)
    {
        if (dragId == targetId) return;
        try
        {
            var arr = await GetKvArray("me2_ideias");
            var list = arr.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()!).ToList();
            var dragged = list.FirstOrDefault(o => o["id"]?.GetValue<string>() == dragId);
            if (dragged is null) return;
            list.Remove(dragged);
            int idx = list.FindIndex(o => o["id"]?.GetValue<string>() == targetId);
            if (idx < 0) idx = list.Count;
            list.Insert(idx, dragged);
            var novo = new JsonArray();
            foreach (var o in list) novo.Add(o);
            await SetKvArray("me2_ideias", novo);
            await LoadRefs();
        }
        catch { ShowStatus("nao reordenou", error: true); }
    }

    private string? _refRenameId;

    private Border RefIdeiaItem(JsonObject r, bool showCat = true)
    {
        string id = r["id"]?.GetValue<string>() ?? "";
        string text = r["text"]?.GetValue<string>() ?? "";
        bool ehLink = LooksLikeUrl(text);

        var card = new Border
        {
            Background = (Brush)FindResource("ChipBg"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(0, 0, 0, 6),
            AllowDrop = true
        };

        // Editando este? campo inline discreto.
        if (_refRenameId == id)
        {
            var box = new TextBox { Style = (Style)FindResource("InlineAdd"), Text = text, FontSize = 12.5 };
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) { _refRenameId = null; RenderRefsInPlace(); e.Handled = true; return; }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                var novo = box.Text.Trim();
                _refRenameId = null;
                if (novo.Length > 0)
                {
                    var arr = await GetKvArray("me2_ideias");
                    foreach (var n in arr.OfType<JsonObject>())
                        if (n["id"]?.GetValue<string>() == id) n["text"] = novo;
                    await SetKvArray("me2_ideias", arr);
                }
                await LoadRefs();
            };
            card.Child = box;
            return card;
        }

        var row = new DockPanel();

        // A��es discretas (aparecem no hover)
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(acts, Dock.Right);
        Button MiniBtn(string glyph, string tip)
        {
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"), Content = glyph, FontSize = 11,
                Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(2, 0, 0, 0),
                Foreground = (Brush)FindResource("TextDim"), ToolTip = tip
            };
            acts.Children.Add(b);
            return b;
        }
        MiniBtn("✎", "editar").Click += (_, _) => { _refRenameId = id; RenderRefsInPlace(); };
        MiniBtn("✕", "apagar").Click += async (_, _) =>
        {
            var arr = await GetKvArray("me2_ideias");
            var novo = new JsonArray();
            foreach (var n in arr.ToList())
                if (n is JsonObject o && o["id"]?.GetValue<string>() != id) novo.Add(n.DeepClone());
            await SetKvArray("me2_ideias", novo);
            await LoadRefs();
        };
        row.Children.Add(acts);

        var content = new TextBlock
        {
            Foreground = (Brush)FindResource("TextMain"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 16
        };
        content.Inlines.Add(new Run(ehLink ? text.Replace("https://", "").Replace("http://", "").TrimEnd('/') : text));

        var main = new Button
        {
            Style = (Style)FindResource("GhostItem"), Content = content,
            Padding = new Thickness(0),
            ToolTip = ehLink ? "clica pra abrir" : "clica pra copiar"
        };
        main.Click += (_, _) =>
        {
            if (_refDragging) { _refDragging = false; return; }
            if (ehLink) { OpenExternal(text.Contains("://") ? text : "https://" + text); ShowStatus("abrindo"); }
            else { Clipboard.SetText(text); ShowStatus("copiado"); }
        };
        row.Children.Add(main);
        card.Child = row;

        card.MouseEnter += (_, _) =>
        {
            acts.Opacity = 1;
            card.Background = (Brush)FindResource("SurfaceHi");
            card.BorderBrush = (Brush)FindResource("AccentSoft");
        };
        card.MouseLeave += (_, _) =>
        {
            acts.Opacity = 0;
            card.Background = (Brush)FindResource("ChipBg");
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        };

        // Arrastar pra reordenar / recategorizar
        card.PreviewMouseLeftButtonDown += (_, e) => { _refDownPos = e.GetPosition(this); _refDragging = false; };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _refDragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _refDownPos.X) > 6 || Math.Abs(p.Y - _refDownPos.Y) > 6)
            {
                _refDragging = true;
                card.Opacity = 0.5;
                DragDrop.DoDragDrop(card, id, DragDropEffects.Move);
                card.Opacity = 1;
            }
        };
        card.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        card.Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.Text) is string dragId) await ReorderRef(dragId, id);
        };
        return card;
    }

    private void RenderRefsInPlace() => _ = LoadRefs();

    // -- LINKS: pastas simples + lista direta -----------------------

    private (string Id, string Name)? _currentFolder;
    private string? _linkEditId;
    private List<string> _linkOrder = new();
    private List<JsonObject> _linkCurrentRefs = new();

    private async Task LoadLinks()
    {
        try
        {
            if (_currentFolder is null)
            {
                var pastas = await Supa.Select("zimbar_folders?select=id,name&parent_id=is.null&order=name.asc");
                RenderLinksRoot(pastas);
            }
            else
            {
                string fid = _currentFolder.Value.Id;
                var cats = await Supa.Select($"zimbar_folders?select=id,name&parent_id=eq.{fid}&order=name.asc");
                var ids = new List<string> { fid };
                ids.AddRange(cats.OfType<JsonObject>().Select(c => c["id"]?.GetValue<string>() ?? ""));
                string inList = string.Join(",", ids.Where(x => x.Length > 0).Select(Uri.EscapeDataString));
                var refs = await Supa.Select($"zimbar_refs?select=id,kind,title,content,folder_id&folder_id=in.({inList})&order=created_at.desc");
                _linkOrder = (await GetKvArray(LinkOrderKey(fid)))
                    .Select(x => x?.GetValue<string>() ?? "")
                    .Where(x => x.Length > 0)
                    .ToList();
                RenderLinksFolder(refs);
            }
        }
        catch
        {
            LinksPanel.Children.Clear();
            LinksPanel.Children.Add(DimText("sem conexao com o banco agora"));
        }
    }

    // Raiz: s� pastas prim�rias
    private void RenderLinksRoot(JsonArray pastas)
    {
        LinksPanel.Children.Clear();
        _linkOrder.Clear();
        LinksPanel.Children.Add(HudLabel("LINKS - suas pastas"));

        var wrap = new UniformGrid
        {
            Columns = ResponsiveColumns(minItemWidth: 190, maxColumns: 5),
            Margin = new Thickness(0, 8, 0, 0)
        };
        foreach (var node in pastas)
            if (node is JsonObject p)
            {
                string id = p["id"]?.GetValue<string>() ?? "";
                string name = p["name"]?.GetValue<string>() ?? "";

                var del = new Button
                {
                    Style = (Style)FindResource("NavBtn"), Content = "x", FontSize = 10.5,
                    Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Foreground = (Brush)FindResource("TextDone"), Visibility = Visibility.Hidden,
                    ToolTip = "excluir pasta"
                };
                del.Click += async (_, _) => await DeleteFolder(id, name);

                var content = new Grid
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                                    Foreground = (Brush)FindResource("TextMain"), Margin = new Thickness(0, 6, 0, 0),
                                    TextTrimming = TextTrimming.CharacterEllipsis }
                            }
                        },
                        del
                    }
                };
                var card = new Border
                {
                    Background = (Brush)FindResource("Surface"),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(16, 12, 12, 14),
                    Margin = new Thickness(0, 0, 10, 10),
                    MinHeight = 84, Cursor = Cursors.Hand,
                    Child = content
                };
                card.MouseEnter += (_, _) => { card.BorderBrush = (Brush)FindResource("Accent"); del.Visibility = Visibility.Visible; };
                card.MouseLeave += (_, _) => { card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)); del.Visibility = Visibility.Hidden; };
                card.MouseLeftButtonUp += (_, _) => { _currentFolder = (id, name); _ = LoadLinks(); };
                wrap.Children.Add(card);
            }
        if (pastas.Count == 0) wrap.Children.Add(DimText("nenhuma pasta ainda"));
        LinksPanel.Children.Add(wrap);

        LinksPanel.Children.Add(RevealAdd("+ pasta", "nome da pasta", async text =>
        {
            await Supa.Insert("zimbar_folders", new JsonObject { ["name"] = text, ["parent_id"] = null });
            await LoadLinks();
        }));
    }

    /// <summary>Exclui uma pasta. Links que ainda estejam em categorias antigas entram na contagem e s�o removidos junto.</summary>
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
                _busyModal = true;   // nao deixa virar aba enquanto o dialogo esta aberto
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

    private void RenderLinksFolder(JsonArray refs)
    {
        LinksPanel.Children.Clear();
        string fid = _currentFolder!.Value.Id;
        _linkCurrentRefs = refs.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
        var links = OrderedLinks(_linkCurrentRefs);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var back = new Button
        {
            Style = (Style)FindResource("Chip"), Content = "< pastas",
            FontSize = 11, Padding = new Thickness(10, 4, 10, 4)
        };
        back.Click += (_, _) => { _currentFolder = null; _ = LoadLinks(); };
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);
        head.Children.Add(HudLabel(_currentFolder.Value.Name));
        LinksPanel.Children.Add(head);

        var table = new StackPanel();
        var header = new Grid { Margin = new Thickness(8, 0, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock Th(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpper(new CultureInfo("pt-BR")),
                FontSize = 10,
                FontFamily = (FontFamily)FindResource("Mono"),
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 0, 12, 0)
            };
        }
        var thName = Th("nome");
        var thUrl = Th("link");
        Grid.SetColumn(thUrl, 1);
        header.Children.Add(thName);
        header.Children.Add(thUrl);
        table.Children.Add(header);

        if (links.Count == 0)
            table.Children.Add(DimText("nenhum link nesta pasta"));
        else
            foreach (var r in links) table.Children.Add(LinkRow(r));

        LinksPanel.Children.Add(new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 11, 12, 12),
            Margin = new Thickness(0, 0, 0, 9),
            Child = table
        });

        LinksPanel.Children.Add(LinkAddBox(fid));
    }


    private List<JsonObject> OrderedLinks(IEnumerable<JsonObject> refs)
    {
        var links = refs.ToList();
        if (_linkOrder.Count == 0) return links;
        var rank = _linkOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return links
            .Select((item, original) => (item, original, id: item["id"]?.GetValue<string>() ?? ""))
            .OrderBy(x => rank.TryGetValue(x.id, out var pos) ? pos : int.MaxValue)
            .ThenBy(x => x.original)
            .Select(x => x.item)
            .ToList();
    }

    private static string LinkOrderKey(string folderId) => "zimbar_link_order_" + folderId;

    private FrameworkElement LinkAddBox(string folderId)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var button = Zui.Button(this, "+ link");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Background = Brushes.Transparent;
        button.FontSize = 11;
        panel.Children.Add(button);

        var editor = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 5, 0, 0) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.34, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.66, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Style = (Style)FindResource("InlineAdd"),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "nome do link"
        };
        var urlBox = new TextBox
        {
            Style = (Style)FindResource("InlineAdd"),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "URL"
        };
        var hintName = new TextBlock { Text = "nome", FontSize = 12, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(10, 5, 0, 0), IsHitTestVisible = false };
        var hintUrl = new TextBlock { Text = "https://site.com", FontSize = 12, Foreground = (Brush)FindResource("TextDone"), Margin = new Thickness(10, 5, 0, 0), IsHitTestVisible = false };

        var nameHost = new Grid();
        nameHost.Children.Add(nameBox);
        nameHost.Children.Add(hintName);
        var urlHost = new Grid();
        urlHost.Children.Add(urlBox);
        urlHost.Children.Add(hintUrl);
        Grid.SetColumn(urlHost, 1);
        editor.Children.Add(nameHost);
        editor.Children.Add(urlHost);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 2);
        var save = Zui.Button(this, "salvar");
        var cancel = Zui.Button(this, "cancelar");
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        editor.Children.Add(actions);
        panel.Children.Add(editor);

        void SyncHints()
        {
            hintName.Visibility = nameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            hintUrl.Visibility = urlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        nameBox.TextChanged += (_, _) => SyncHints();
        urlBox.TextChanged += (_, _) => SyncHints();

        button.Click += (_, _) =>
        {
            button.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            nameBox.Focus();
            SyncHints();
        };
        cancel.Click += (_, _) =>
        {
            nameBox.Clear();
            urlBox.Clear();
            editor.Visibility = Visibility.Collapsed;
            button.Visibility = Visibility.Visible;
        };

        async Task Save()
        {
            string title = nameBox.Text.Trim();
            string url = urlBox.Text.Trim();
            if (url.Length == 0 && title.Length > 0 && LooksLikeUrl(title))
            {
                url = title;
                title = "";
            }
            if (url.Length == 0) { ShowStatus("preencha o link", error: true); return; }
            var parsed = ParseLinkInput(title.Length > 0 ? title + " | " + url : url);
            await Supa.Insert("zimbar_refs", new JsonObject
            {
                ["kind"] = parsed.Kind,
                ["title"] = parsed.Title,
                ["content"] = parsed.Content,
                ["folder_id"] = folderId
            });
            ShowStatus("link adicionado");
            await LoadLinks();
        }

        save.Click += async (_, _) => await Save();
        nameBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Save(); } if (e.Key == Key.Escape) cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        urlBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Save(); } if (e.Key == Key.Escape) cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        return panel;
    }

    private async Task MoveLink(string id, int delta)
    {
        if (_currentFolder is null) return;
        var visibleIds = OrderedLinks(_linkCurrentRefs)
            .Select(x => x["id"]?.GetValue<string>() ?? "")
            .Where(x => x.Length > 0)
            .ToList();
        if (visibleIds.Count == 0) return;
        _linkOrder = visibleIds;
        int i = _linkOrder.IndexOf(id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _linkOrder.Count) return;
        (_linkOrder[i], _linkOrder[j]) = (_linkOrder[j], _linkOrder[i]);
        await SetKvArray(LinkOrderKey(_currentFolder.Value.Id), new JsonArray(_linkOrder.Select(x => JsonValue.Create(x)).ToArray()));
        await LoadLinks();
    }

    private UIElement LinkRow(JsonObject r)
    {
        string id = r["id"]?.GetValue<string>() ?? "";
        string kind = r["kind"]?.GetValue<string>() ?? "link";
        string title = r["title"]?.GetValue<string>() ?? "";
        string content = r["content"]?.GetValue<string>() ?? "";
        bool ehLink = kind == "link" || LooksLikeUrl(content);
        if (ehLink) content = EnsureUrl(content);

        string label = title.Length > 0 ? title
            : ehLink ? LinkLabel(content)
            : content;

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(0)
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_linkEditId == id)
        {
            var titleBox = new TextBox
            {
                Style = (Style)FindResource("InlineAdd"),
                Text = label,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var urlBox = new TextBox
            {
                Style = (Style)FindResource("InlineAdd"),
                Text = content,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(urlBox, 1);
            row.Children.Add(titleBox);
            row.Children.Add(urlBox);

            var acts = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(acts, 2);
            Button EditBtn(string glyph, string tip)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("NavBtn"),
                    Content = glyph,
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(2, 0, 0, 0),
                    Foreground = (Brush)FindResource("TextDim"),
                    ToolTip = tip
                };
                acts.Children.Add(b);
                return b;
            }
            EditBtn("✓", "salvar").Click += async (_, _) =>
            {
                var parsed = ParseLinkInput(titleBox.Text.Trim().Length > 0
                    ? titleBox.Text.Trim() + " | " + urlBox.Text.Trim()
                    : urlBox.Text.Trim());
                _linkEditId = null;
                try
                {
                    await Supa.Update("zimbar_refs", "id=eq." + Uri.EscapeDataString(id), new JsonObject
                    {
                        ["kind"] = parsed.Kind,
                        ["title"] = parsed.Title,
                        ["content"] = parsed.Content
                    });
                    await LoadLinks();
                }
                catch { ShowStatus("nao renomeou", error: true); }
            };
            EditBtn("✕", "cancelar").Click += (_, _) => { _linkEditId = null; _ = LoadLinks(); };
            row.Children.Add(acts);

            titleBox.Loaded += (_, _) => { titleBox.Focus(); titleBox.SelectAll(); };
            titleBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _linkEditId = null; _ = LoadLinks(); e.Handled = true; } };
            urlBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _linkEditId = null; _ = LoadLinks(); e.Handled = true; } };
            card.Child = row;
            return card;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
        Grid.SetColumn(actions, 2);
        Button ActionBtn(string glyph, string tip)
        {
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"), Content = glyph, FontSize = 11,
                Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(2, 0, 0, 0),
                Foreground = (Brush)FindResource("TextDim"), ToolTip = tip
            };
            actions.Children.Add(b);
            return b;
        }
        ActionBtn("↑", "subir").Click += async (_, _) => await MoveLink(id, -1);
        ActionBtn("↓", "descer").Click += async (_, _) => await MoveLink(id, 1);
        ActionBtn("✎", "renomear").Click += (_, _) => { _linkEditId = id; _ = LoadLinks(); };
        var del = ActionBtn("✕", "apagar");
        del.Click += async (_, _) =>
        {
            try { await Supa.Delete("zimbar_refs", "id=eq." + Uri.EscapeDataString(id)); await LoadLinks(); }
            catch { ShowStatus("nao apagou", error: true); }
        };
        row.Children.Add(actions);

        var titleCell = new TextBlock
        {
            Foreground = ehLink ? (Brush)FindResource("Accent") : (Brush)FindResource("TextMain"),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = ehLink ? Cursors.Hand : Cursors.Arrow,
            TextDecorations = ehLink ? TextDecorations.Underline : null,
            Margin = new Thickness(0, 0, 12, 0),
            Text = label
        };
        var urlCell = new TextBlock
        {
            Foreground = (Brush)FindResource("TextDim"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = ehLink ? Cursors.Hand : Cursors.Arrow,
            Text = ehLink ? content.Replace("https://", "").Replace("http://", "") : content
        };
        void OpenOrCopy()
        {
            if (ehLink) { OpenExternal(content); ShowStatus("abrindo"); }
            else { Clipboard.SetText(content); ShowStatus("copiado"); }
        }
        titleCell.MouseLeftButtonUp += (_, _) => OpenOrCopy();
        urlCell.MouseLeftButtonUp += (_, _) => OpenOrCopy();
        Grid.SetColumn(urlCell, 1);
        row.Children.Add(titleCell);
        row.Children.Add(urlCell);

        card.Child = row;
        card.MouseEnter += (_, _) => { actions.Opacity = 1; card.Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)); };
        card.MouseLeave += (_, _) => { actions.Opacity = 0; card.Background = Brushes.Transparent; };
        return card;
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

            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11, 10, 11, 10),
                Margin = new Thickness(0, 0, 8, 8),
                Child = col
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

    private Border EmailEmptyCard()
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private async void Print_Click(object sender, RoutedEventArgs e) => await TriggerRelampago(0x32);
    private async void Record_Click(object sender, RoutedEventArgs e) => await TriggerRelampago(0x33);

    /// <summary>Dispara o hotkey global do Rel�mpago (Ctrl+Shift+2/3), subindo ele se preciso.</summary>
    private async Task TriggerRelampago(byte vk)
    {
        HideBar();
        if (Process.GetProcessesByName("Relampago").Length == 0)
        {
            var exe = new[]
            {
                @"D:\Relampago\bin\Release\net9.0-windows\Relampago.exe",
                @"D:\Relampago\bin\Debug\net9.0-windows\Relampago.exe",
                @"D:\Relampago\artifacts\Relampago-1.0.0-win-x64\Relampago.exe"
            }.FirstOrDefault(File.Exists);

            if (exe is null)
            {
                ((App)Application.Current).Notify("Zimbar", "Nao achei o Relampago.exe no D:\\Relampago.");
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            await Task.Delay(2200);
        }

        await Task.Delay(250);
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x10, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
