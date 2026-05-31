using System;

namespace Violeta.Services;

/// <summary>Estado de conexión del mando, compartido entre pantallas.</summary>
public enum MandoStatus
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>
/// Estado vivo de la app, compartido entre la pantalla de Conectar (que maneja el puente) y la de
/// Inicio (que lo refleja como un panel vivo). Singleton sencillo con un evento de cambio; quien
/// escuche debe marshalizar al hilo de UI si hace falta (aquí se actualiza siempre desde el hilo de UI).
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    private MandoStatus _status = MandoStatus.Disconnected;
    public MandoStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Changed?.Invoke(); } }
    }

    /// <summary>Nombre del mando conectado (o el buscado), para mostrarlo en Inicio.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Número de botones calibrados en la sesión actual.</summary>
    public int CalibratedCount { get; set; }

    public event Action? Changed;

    /// <summary>Notifica un cambio (p. ej. tras actualizar varias propiedades a la vez).</summary>
    public void Raise() => Changed?.Invoke();
}
