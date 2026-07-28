using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Zimbar;

/// <summary>
/// Cantos arredondados de verdade, os do Windows 11. Como as janelas do
/// Zimbar não têm moldura, quem faz a curva (e a sombra que vem junto) é
/// o DWM — não dá pra fingir com CSS porque a janela é retangular.
/// </summary>
public static class Cantos
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    private enum Preferencia { Padrao = 0, NaoArredondar = 1, Arredondar = 2, ArredondarPouco = 3 }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int valor, int tam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint valor, int tam);

    /// <summary>Arredonda a janela. Chame depois que ela já tem handle (SourceInitialized/Loaded).</summary>
    public static void Arredondar(Window janela, bool pouco = false)
    {
        try
        {
            var h = new WindowInteropHelper(janela).Handle;
            if (h == IntPtr.Zero) return;
            int pref = (int)(pouco ? Preferencia.ArredondarPouco : Preferencia.Arredondar);
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            uint semBorda = DWMWA_COLOR_NONE;      // sem a linha clara do Windows por cima
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref semBorda, sizeof(uint));
        }
        catch { /* Windows 10: fica reto mesmo */ }
    }

    /// <summary>Liga o arredondamento assim que a janela ganha handle.</summary>
    public static void ArredondarQuandoAbrir(Window janela, bool pouco = false)
    {
        if (new WindowInteropHelper(janela).Handle != IntPtr.Zero) { Arredondar(janela, pouco); return; }
        janela.SourceInitialized += (_, _) => Arredondar(janela, pouco);
    }
}
