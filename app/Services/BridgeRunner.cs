using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.Bridge;

namespace Violeta.Services;

/// <summary>Cómo se obtiene el acceso a la cuenta del usuario para el paso de verificación.</summary>
public enum AuthMode
{
    /// <summary>Solo diagnóstico: no se inicia sesión ni se desbloquea (no necesita cuenta).</summary>
    DiagnoseOnly,
    UsernamePassword,
    RefreshToken,
    AccessToken
}

/// <summary>Parámetros que la interfaz recoge de la persona para arrancar el puente.</summary>
public sealed class RunOptions
{
    public AuthMode Mode { get; init; } = AuthMode.UsernamePassword;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }     // refresh o access según Mode
    public string DeviceName { get; init; } = "Zwift Click";
    public bool EmulateKeyboard { get; init; } = true;
}

/// <summary>
/// Envoltorio amigable de <see cref="ZwiftClickBridge"/> para la interfaz gráfica. Resuelve el token
/// de la cuenta del usuario (login/refresh) y ejecuta el puente en segundo plano, reenviando el
/// progreso al hilo de UI. No reimplementa nada del protocolo: reutiliza la lógica ya probada.
/// </summary>
public sealed class BridgeRunner
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private ZwiftClickBridge? _bridge;

    public event Action<BridgeProgress>? ProgressChanged;
    public event Action<BridgeButtonEvent>? ButtonEmitted;
    /// <summary>Se dispara al terminar el arranque: éxito = puente operativo (escuchando botones).</summary>
    public event Action<bool>? Finished;

    public bool IsRunning { get; private set; }

    public async void Start(RunOptions options)
    {
        if (IsRunning) return;
        IsRunning = true;

        try
        {
            string? token = await ResolveTokenAsync(options);

            var bridge = new ZwiftClickBridge(options.EmulateKeyboard);
            bridge.ProgressChanged += p => _ui.BeginInvoke(() => ProgressChanged?.Invoke(p));
            bridge.ButtonEmitted += b => _ui.BeginInvoke(() => ButtonEmitted?.Invoke(b));
            _bridge = bridge;

            bool ok = await bridge.StartAsync(options.DeviceName, token);
            await _ui.BeginInvoke(() => Finished?.Invoke(ok));

            if (!ok)
            {
                // El arranque no llegó a operativo: liberar para poder reintentar limpio.
                bridge.Stop();
                _bridge = null;
                IsRunning = false;
            }
            // Si ok == true, el puente sigue vivo escuchando botones hasta que se llame a Stop().
        }
        catch (Exception ex)
        {
            await _ui.BeginInvoke(() =>
            {
                ProgressChanged?.Invoke(new BridgeProgress(BridgePhase.Failed, FriendlyError(ex), true));
                Finished?.Invoke(false);
            });
            _bridge?.Stop();
            _bridge = null;
            IsRunning = false;
        }
    }

    public void Stop()
    {
        _bridge?.Stop();
        _bridge = null;
        IsRunning = false;
    }

    private static async Task<string?> ResolveTokenAsync(RunOptions o)
    {
        if (o.Mode == AuthMode.DiagnoseOnly) return null;

        using var oauth = new ZwiftOAuthClient();
        return o.Mode switch
        {
            AuthMode.AccessToken => o.Token,
            AuthMode.RefreshToken => await oauth.RefreshAsync(o.Token ?? string.Empty),
            AuthMode.UsernamePassword => await oauth.LoginAsync(o.Username ?? string.Empty, o.Password ?? string.Empty),
            _ => null
        };
    }

    private static string FriendlyError(Exception ex)
    {
        string m = ex.Message;
        if (m.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("HTTP 4", StringComparison.OrdinalIgnoreCase))
            return "No pude iniciar sesión con tu cuenta. Revisa tu correo/contraseña (o el token) e inténtalo otra vez.";
        return "Algo no salió bien: " + m;
    }
}
