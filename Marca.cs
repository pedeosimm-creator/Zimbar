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

    /// <summary>A logo, carregada uma vez só e compartilhada entre as janelas.</summary>
    public static BitmapImage? Logo
    {
        get
        {
            if (_z is not null) return _z;
            try
            {
                _z = new BitmapImage(new Uri("pack://application:,,,/assets/Zimbar.png"));
                _z.Freeze();   // congelada: pode ser usada por qualquer janela
            }
            catch (Exception ex) { Log.Escrever("Marca — não deu pra carregar a logo: " + ex.Message); }
            return _z;
        }
    }

    /// <summary>Põe o Z na janela. Chame no construtor, antes do Show().</summary>
    public static void Vestir(Window janela)
    {
        var logo = Logo;
        if (logo is not null) janela.Icon = logo;
    }
}
