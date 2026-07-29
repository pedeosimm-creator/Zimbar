using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Zimbar;

/// <summary>
/// ZimNotes como aplicativo próprio do computador.
///
/// A janela não desenha nada: ela hospeda ui/notas.html, que é a MESMA
/// interface da aba Notas dentro do Zimbar (notas.css + notas.js). Foi de
/// propósito — assim os dois são iguais por construção, e não porque
/// alguém lembrou de repetir a mudança dos dois lados.
///
/// Ao C# sobra o que o navegador não faz: mover, redimensionar e os três
/// botões do canto, já que a janela é sem moldura.
/// </summary>
public partial class NotesWindow : Window
{
    private static NotesWindow? _instance;

    public static void Open()
    {
        _instance ??= new NotesWindow();
        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Normal;
        _instance.Show();
        _instance.Activate();
        _instance.Web.Focus();
    }

    private NotesWindow()
    {
        InitializeComponent();
        Cantos.ArredondarQuandoAbrir(this);
        Closed += (_, _) => _instance = null;
        _ = IniciarSeguro();
    }

    /// <summary>Pasta ui/ ao lado do executável (ou a do projeto, no dev).</summary>
    private static string PastaUi
    {
        get
        {
            string aoLado = Path.Combine(AppContext.BaseDirectory, "ui");
            if (File.Exists(Path.Combine(aoLado, "notas.html"))) return aoLado;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string tenta = Path.Combine(dir.FullName, "ui");
                if (File.Exists(Path.Combine(tenta, "notas.html"))) return tenta;
                dir = dir.Parent;
            }
            return aoLado;
        }
    }

    private async Task IniciarSeguro()
    {
        try { await Iniciar(); }
        catch (Exception ex)
        {
            Log.Erro("subir webview das notas", ex);
            MessageBox.Show("O ZimNotes não conseguiu abrir.\n\n" + ex.Message +
                            "\n\nDetalhes em: " + Log.Caminho,
                            "ZimNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task Iniciar()
    {
        // mesmo perfil do Zimbar: o tema e as preferências são compartilhados
        string perfil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zimbar", "WebView2");
        Directory.CreateDirectory(perfil);

        var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
        await Web.EnsureCoreWebView2Async(ambiente);

        var nucleo = Web.CoreWebView2;
        var cfg = nucleo.Settings;
        cfg.AreDefaultContextMenusEnabled = false;
        cfg.IsStatusBarEnabled = false;
        cfg.IsZoomControlEnabled = false;
#if DEBUG
        cfg.AreDevToolsEnabled = true;
#else
        cfg.AreDevToolsEnabled = false;
#endif

        nucleo.SetVirtualHostNameToFolderMapping(
            "zimbar.local", PastaUi, CoreWebView2HostResourceAccessKind.Allow);

        nucleo.WebMessageReceived += (_, e) => Receber(e.TryGetWebMessageAsString());
        nucleo.NewWindowRequested += (_, e) => { e.Handled = true; Ponte.AbrirNoNavegador(e.Uri); };

        nucleo.Navigate("https://zimbar.local/notas.html");
    }

    /// <summary>Só o que é da janela; o resto o JavaScript resolve sozinho.</summary>
    private void Receber(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return;
        JsonObject? m;
        try { m = JsonNode.Parse(bruto) as JsonObject; }
        catch { return; }
        if (m is null) return;

        switch (m["acao"]?.GetValue<string>())
        {
            case "arrastar":
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                Empurrar(HTCAPTION);
                break;
            case "redimensionar":
                Empurrar(m["borda"]?.GetValue<string>() switch
                {
                    "n" => HTTOP, "s" => HTBOTTOM, "w" => HTLEFT, "e" => HTRIGHT,
                    "nw" => HTTOPLEFT, "ne" => HTTOPRIGHT,
                    "sw" => HTBOTTOMLEFT, "se" => HTBOTTOMRIGHT,
                    _ => HTCAPTION
                });
                break;
            case "minimizar":
                WindowState = WindowState.Minimized;
                break;
            case "maximizar":
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
                break;
            case "fechar":
                Close();
                break;
            case "abrirLink":
                Ponte.AbrirNoNavegador(m["url"]?.GetValue<string>() ?? "");
                break;
        }
    }

    // O clique nasce DENTRO do WebView2, que é outra janela do Windows: o
    // DragMove() do WPF não serve. Devolve-se o clique pro Windows como se
    // tivesse acontecido na moldura.
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
}
