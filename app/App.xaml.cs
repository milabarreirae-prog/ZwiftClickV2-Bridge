using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Violeta;

/// <summary>
/// Punto de entrada de la interfaz gráfica de Violeta. Captura errores no manejados (en el hilo de
/// UI, en hilos de fondo y en tareas) para que la app nunca se cierre de golpe sin explicar qué pasó,
/// y deja un registro completo en disco para diagnóstico.
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "violeta-crash.log");

    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        MessageBox.Show(
            "Ha ocurrido un error inesperado:\n\n" + e.Exception.Message +
            "\n\n(Se guardó un detalle técnico en " + LogPath + ")",
            "Violeta",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Vuelca una excepción con su traza completa al registro de diagnóstico. Nunca lanza.</summary>
    private static void Log(string source, Exception? ex)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(LogPath,
                $"==== {stamp} [{source}] ====\n{ex}\n\n");
        }
        catch { /* no fatal */ }
    }
}
