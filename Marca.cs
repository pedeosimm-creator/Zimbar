using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Zimbar;

/// <summary>
/// O Z em todas as janelas. Sem isso, as janelas criadas em código (nota
/// autoadesiva, pomodoro) aparecem na barra de tarefas e no Alt+Tab com o
/// ícone genérico do Windows, e não com a logo do Zimbar.
/// </summary>
public static class Marca
{
    private static BitmapImage? _z;
    private static BitmapImage? _folha;

    private static BitmapImage? Carregar(ref BitmapImage? cache, string arquivo)
    {
        if (cache is not null) return cache;
        try
        {
            cache = new BitmapImage(new Uri("pack://application:,,,/assets/" + arquivo));
            cache.Freeze();   // congelada: pode ser usada por qualquer janela
        }
        catch (Exception ex) { Log.Escrever($"Marca — não carregou {arquivo}: {ex.Message}"); }
        return cache;
    }

    /// <summary>O Z do Zimbar.</summary>
    public static BitmapImage? Logo => Carregar(ref _z, "Zimbar.png");

    /// <summary>A folha do ZimNotes — as janelas de nota têm marca própria.</summary>
    public static BitmapImage? LogoNotas => Carregar(ref _folha, "ZimNotes.png");

    /// <summary>Põe o Z na janela. Chame no construtor, antes do Show().</summary>
    public static void Vestir(Window janela)
    {
        if (Logo is BitmapImage l) janela.Icon = l;
    }

    /// <summary>Põe a folha do ZimNotes na janela.</summary>
    public static void VestirNotas(Window janela)
    {
        if (LogoNotas is BitmapImage l) janela.Icon = l;
    }
}
