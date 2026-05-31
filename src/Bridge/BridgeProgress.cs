namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Fase del flujo del bridge, pensada para que una interfaz amigable pueda explicarle a la persona
/// —en lenguaje humano— qué está pasando por detrás en cada momento. La CLI sigue usando
/// <c>Console.WriteLine</c>; estas fases son un canal paralelo y opcional (eventos), nunca obligatorio.
/// </summary>
public enum BridgePhase
{
    /// <summary>Buscando el mando por Bluetooth.</summary>
    Scanning,
    /// <summary>Mando encontrado; abriendo el canal seguro.</summary>
    Connecting,
    /// <summary>Saludo ECDH: tu PC y el mando intercambian llaves públicas.</summary>
    Handshake,
    /// <summary>Esperando a que el mando genere y emita su reto.</summary>
    WaitingChallenge,
    /// <summary>Reto recibido del mando.</summary>
    ChallengeCaptured,
    /// <summary>Comprobando con tu cuenta que el mando es tuyo (paso server-backed).</summary>
    ServerAuth,
    /// <summary>Cuenta autorizada; enviando la señal de desbloqueo al mando.</summary>
    Unlocking,
    /// <summary>Desbloqueo completo; escuchando los botones.</summary>
    Listening,
    /// <summary>Canal cifrado de sesión resuelto: ya se pueden leer los botones.</summary>
    SessionResolved,
    /// <summary>Se pulsó un botón y se tradujo a una tecla.</summary>
    ButtonPressed,
    /// <summary>El bridge se detuvo de forma ordenada.</summary>
    Stopped,
    /// <summary>Algo falló; <see cref="BridgeProgress.IsError"/> será true.</summary>
    Failed
}

/// <summary>
/// Un latido de progreso del bridge: la fase técnica, un mensaje en lenguaje humano y si es un error.
/// </summary>
/// <param name="Phase">Fase del flujo.</param>
/// <param name="Message">Mensaje claro para mostrar a la persona usuaria.</param>
/// <param name="IsError">true si la fase representa un fallo.</param>
public sealed record BridgeProgress(BridgePhase Phase, string Message, bool IsError = false);

/// <summary>
/// Evento de botón ya traducido a algo entendible por una persona (lado pulsado y tecla emulada).
/// </summary>
/// <param name="Label">Descripción amable, p. ej. "Izquierda".</param>
/// <param name="VirtualKey">Código de tecla virtual de Windows emulado (0 si ninguno).</param>
/// <param name="ActionId">Identificador estable de la acción mapeada (p. ej. "plus", "left", "emote_peace"). Vacío si no aplica.</param>
public sealed record BridgeButtonEvent(string Label, byte VirtualKey, string ActionId = "");
