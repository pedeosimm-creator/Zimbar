using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Zimbar;

/// <summary>
/// A janela do Zimbar. A interface inteira é a página de ui/ rodando num
/// WebView2; o C# fica com o que só ele consegue fazer — janela, notas
/// autoadesivas, captura de tela, OCR e as notícias (o RSS não libera CORS).
/// A conversa entre os dois é a <see cref="Ponte"/>.
/// </summary>
public partial class BarWindow : Window
{
    private Ponte? _ponte;
    private bool _pronta;

    /// <summary>Pasta ui/ ao lado do executável (ou a do projeto, no dev).</summary>
    private static string PastaUi
    {
        get
        {
            string aoLado = Path.Combine(AppContext.BaseDirectory, "ui");
            if (File.Exists(Path.Combine(aoLado, "index.html"))) return aoLado;
            // rodando pelo dotnet run: sobe até achar a pasta do projeto
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string tenta = Path.Combine(dir.FullName, "ui");
                if (File.Exists(Path.Combine(tenta, "index.html"))) return tenta;
                dir = dir.Parent;
            }
            return aoLado;
        }
    }

    public BarWindow()
    {
        InitializeComponent();
        // identidade própria: a BarWindow leva o id do Zimbar (o mesmo que é
        // carimbado nos atalhos), pra o botão da barra de tarefas mostrar o Z
        // e não se confundir com o atalho do ZimNotes, que aponta pro mesmo exe
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        IdentidadeJanela.Definir(helper.Handle, "PedroKuster.Zimbar");
        Cantos.ArredondarQuandoAbrir(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CollapseBar();
        };
        _ = IniciarSeguro();
    }

    /// <summary>Se o WebView2 não subir, o erro vai pro log em vez de sumir.</summary>
    private async Task IniciarSeguro()
    {
        try { await Iniciar(); }
        catch (Exception ex)
        {
            Log.Erro("subir webview", ex);
            MessageBox.Show(
                "O Zimbar não conseguiu abrir a interface.\n\n" + ex.Message +
                "\n\nDetalhes em: " + Log.Caminho,
                "Zimbar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task Iniciar()
    {
        // Perfil próprio: cookies/localStorage do Zimbar não se misturam com o Edge
        string perfil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zimbar", "WebView2");
        Directory.CreateDirectory(perfil);

        var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
        await Web.EnsureCoreWebView2Async(ambiente);

        var nucleo = Web.CoreWebView2;
        var cfg = nucleo.Settings;
        cfg.AreDefaultContextMenusEnabled = false;   // menu de navegador não combina
        cfg.IsStatusBarEnabled = false;
        cfg.AreBrowserAcceleratorKeysEnabled = false;
        cfg.IsZoomControlEnabled = false;
#if DEBUG
        cfg.AreDevToolsEnabled = true;
#else
        cfg.AreDevToolsEnabled = false;
#endif

        // ui/ vira um site local: assim vale CORS normal com o Supabase
        nucleo.SetVirtualHostNameToFolderMapping(
            "zimbar.local", PastaUi, CoreWebView2HostResourceAccessKind.Allow);

        _ponte = new Ponte(this, nucleo);
        nucleo.WebMessageReceived += (_, e) => _ponte.Receber(e.TryGetWebMessageAsString());

        // link com target=_blank abre no navegador de verdade, não numa janela órfã
        nucleo.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Ponte.AbrirNoNavegador(e.Uri);
        };

        nucleo.Navigate("https://zimbar.local/index.html");
        _pronta = true;
    }

    /// <summary>Manda um evento pra interface (captura nova, nota mudou...).</summary>
    public void Avisar(string json)
    {
        if (!_pronta || Web.CoreWebView2 is null) return;
        Dispatcher.Invoke(() => Web.CoreWebView2.PostWebMessageAsString(json));
    }

    // ── mover e redimensionar ──
    // O clique nasce DENTRO do WebView2, que é outra janela do Windows. Por
    // isso o DragMove() do WPF não serve (ele exige que o botão esteja sendo
    // segurado na janela dele). O jeito certo é devolver o clique pro Windows
    // como se tivesse acontecido na moldura: solta a captura do mouse e manda
    // um clique de "área não-cliente" com a parte que se quer arrastar.
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
                      HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15,
                      HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);

    private void Empurrar(int parte)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(h, WM_NCLBUTTONDOWN, new IntPtr(parte), IntPtr.Zero);
    }

    public void ArrastarJanela()
    {
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        Empurrar(HTCAPTION);
    }

    public void RedimensionarJanela(string borda)
    {
        EmpurrarBorda(borda);
    }

    private void EmpurrarBorda(string borda) => Empurrar(borda switch
    {
        "n" => HTTOP,      "s" => HTBOTTOM,   "w" => HTLEFT,     "e" => HTRIGHT,
        "nw" => HTTOPLEFT, "ne" => HTTOPRIGHT,
        "sw" => HTBOTTOMLEFT, "se" => HTBOTTOMRIGHT,
        _ => HTCAPTION
    });

    public void AlternarMaximizada()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ── ciclo de vida que o App usa no hotkey ──
    public bool IsCollapsed => !IsVisible;

    public void ShowBar()
    {
        Show();
        Activate();
        Web.Focus();
        Log.Escrever($"ShowBar — hwnd={new WindowInteropHelper(this).Handle} visivel={IsVisible} pos={Left:0},{Top:0} tam={ActualWidth:0}x{ActualHeight:0}");
    }

    public void ExpandBar() => ShowBar();

    /// <summary>Some da tela mas continua viva: o WebView2 não recarrega à toa.</summary>
    public void CollapseBar() => Hide();

    /// <summary>Só o "Sair" da bandeja liga isto; aí a janela pode morrer.</summary>
    public static bool Saindo;

    /// <summary>
    /// Alt+F4 esconde em vez de fechar. Fechando de verdade o WebView2 morre
    /// junto, e o próximo Ctrl+Alt+Z não tinha mais o que mostrar — a janela
    /// ficava "aberta" sem nunca aparecer.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Saindo) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }
}
