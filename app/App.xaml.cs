using System.Windows;
using System.Windows.Threading;

namespace Violeta;

/// <summary>
/// Punto de entrada de la interfaz gráfica de Violeta. Captura errores no manejados para que la
/// app nunca se cierre de golpe sin explicarle a la persona qué pasó.
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Ha ocurrido un error inesperado:\n\n" + e.Exception.Message,
            "Violeta",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
