using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Violeta.Models;
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

/// <summary>Cómo lee Violeta los botones del mando.</summary>
public enum ButtonProtocol
{
    /// <summary>V1/andriuz (POR DEFECTO): en claro, SIN cuenta ni cifrado. Botones como bitmask 2308…0F.</summary>
    AndriuzV1,
    /// <summary>V2: handshake ECDH + unlock con cuenta Zwift (sesión cifrada; hoy solo da telemetría).</summary>
    ZwiftV2
}

/// <summary>Parámetros que la interfaz recoge de la persona para arrancar el puente.</summary>
public sealed class RunOptions
{
    /// <summary>Protocolo de botones. Por defecto V1/andriuz (en claro, sin cuenta).</summary>
    public ButtonProtocol Protocol { get; init; } = ButtonProtocol.AndriuzV1;
    public AuthMode Mode { get; init; } = AuthMode.UsernamePassword;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }     // refresh o access según Mode
    public string DeviceName { get; init; } = "Zwift";
    public bool EmulateKeyboard { get; init; } = true;

    /// <summary>Acciones asignables del preset elegido (ya con la inversión + / − aplicada si procede).</summary>
    public IReadOnlyList<MappableAction> Actions { get; init; } = new List<MappableAction>();

    /// <summary>Si es true, al desbloquear arranca la calibración guiada automática del preset.</summary>
    public bool AutoCalibrate { get; init; } = true;

    /// <summary>Mapa de botones aprendido en sesiones anteriores (firma → acción). Se carga al conectar.</summary>
    public IReadOnlyDictionary<string, string> SavedButtonMap { get; init; } = new Dictionary<string, string>();
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
    /// <summary>Diagnóstico: hex crudo de cada trama post-unlock (para mapear botones).</summary>
    public event Action<string>? DiagnosticFrame;
    /// <summary>Resultado de calibración: (acción, firma, éxito).</summary>
    public event Action<string, string, bool>? CalibrationFinished;
    /// <summary>Comienza un paso de calibración: identificador de la acción a pulsar.</summary>
    public event Action<string>? CalibrationStepStarted;
    /// <summary>La calibración guiada recorrió todas las acciones.</summary>
    public event Action? GuidedCalibrationFinished;
    /// <summary>Se dispara al terminar el arranque: éxito = puente operativo (escuchando botones).</summary>
    public event Action<bool>? Finished;

    private RunOptions? _options;
    public bool IsRunning { get; private set; }

    /// <summary>true mientras la calibración guiada automática está en curso.</summary>
    public bool IsGuiding => _bridge?.IsGuiding ?? false;

    public async void Start(RunOptions options)
    {
        if (IsRunning) return;
        IsRunning = true;
        _options = options;

        try
        {
            var bridge = new ZwiftClickBridge(options.EmulateKeyboard);
            ApplyActions(bridge, options.Actions);
            if (options.SavedButtonMap.Count > 0) bridge.LoadLearnedMap(options.SavedButtonMap);
            bridge.ProgressChanged += p => _ui.BeginInvoke(() => ProgressChanged?.Invoke(p));
            bridge.ButtonEmitted += b => _ui.BeginInvoke(() => ButtonEmitted?.Invoke(b));
            bridge.DiagnosticFrame += f => _ui.BeginInvoke(() => DiagnosticFrame?.Invoke(f));
            bridge.CalibrationFinished += (a, s, ok) => _ui.BeginInvoke(() => CalibrationFinished?.Invoke(a, s, ok));
            bridge.CalibrationStepStarted += a => _ui.BeginInvoke(() => CalibrationStepStarted?.Invoke(a));
            bridge.GuidedCalibrationFinished += () => _ui.BeginInvoke(() => GuidedCalibrationFinished?.Invoke());
            _bridge = bridge;

            // Modo V1/andriuz (por defecto): botones en claro, SIN cuenta. Modo V2: unlock con cuenta.
            bool ok = options.Protocol == ButtonProtocol.AndriuzV1
                ? await bridge.StartAndriuzAsync(options.DeviceName)
                : await bridge.StartAsync(options.DeviceName, await ResolveTokenAsync(options));
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

    /// <summary>Inicia la calibración de una acción del preset (p. ej. "plus"). Requiere el puente operativo.</summary>
    public void StartCalibration(string action) => _bridge?.StartCalibration(action);

    /// <summary>Inicia la calibración guiada automática sobre las acciones del preset actual.</summary>
    public void StartGuidedCalibration()
    {
        var ids = _options?.Actions.Select(a => a.Id);
        if (ids != null) _bridge?.StartGuidedCalibration(ids);
    }

    /// <summary>Salta el paso de calibración en curso (botón que el mando no tiene).</summary>
    public void SkipCurrentCalibration() => _bridge?.SkipCurrentCalibration();

    /// <summary>Olvida el mapeo aprendido.</summary>
    public void ClearCalibration() => _bridge?.ClearCalibration();

    /// <summary>Vuelca las acciones del preset al puente (id → tecla, id → etiqueta).</summary>
    private static void ApplyActions(ZwiftClickBridge bridge, IReadOnlyList<MappableAction> actions)
    {
        if (actions.Count == 0) return; // conserva el mapeo por defecto del puente (marchas)
        var keys = new Dictionary<string, byte>();
        var labels = new Dictionary<string, string>();
        foreach (var a in actions) { keys[a.Id] = a.Key; labels[a.Id] = a.Label; }
        bridge.SetActions(keys, labels);
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
