using System.Collections.Generic;
using ZwiftClickV2.Bridge.Bridge;

namespace Violeta.Models;

/// <summary>
/// Un paso del proceso, explicado para una persona (no para una máquina). Se usa tanto en la
/// pantalla de tutorial como en la ejecución en vivo, donde se ilumina el paso actual.
/// </summary>
public sealed class TutorialStep
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Short { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// Los pasos canónicos del flujo, en lenguaje claro, y el mapa de fase técnica → paso humano.
/// </summary>
public static class TutorialSteps
{
    public static readonly IReadOnlyList<TutorialStep> All = new List<TutorialStep>
    {
        new()
        {
            Glyph = "🔍",
            Title = "1 · Encontrar tu mando",
            Short = "Tu PC busca el mando por Bluetooth.",
            Detail = "La app enciende el Bluetooth de tu PC y escucha a los dispositivos cercanos hasta " +
                     "reconocer tu mando de ciclismo indoor. Si tarda, basta con pulsar un botón del mando " +
                     "para despertarlo. No se conecta a internet en este paso."
        },
        new()
        {
            Glyph = "🤝",
            Title = "2 · Saludo seguro",
            Short = "Tu PC y el mando intercambian llaves.",
            Detail = "Tu PC y el mando se presentan e intercambian unas llaves públicas (un saludo " +
                     "criptográfico llamado ECDH). A partir de aquí pueden hablar en privado, sin que nadie " +
                     "alrededor pueda entender lo que se dicen."
        },
        new()
        {
            Glyph = "🎫",
            Title = "3 · El mando crea su reto",
            Short = "El mando emite un \"reto\" y tu PC lo recoge.",
            Detail = "El propio mando genera un pequeño paquete (su \"reto\") y lo emite. Tu PC solo lo recoge " +
                     "tal cual: no lo inventa ni lo firma. Es como recoger un sobre cerrado para llevarlo a " +
                     "quien debe abrirlo."
        },
        new()
        {
            Glyph = "✅",
            Title = "4 · Tu cuenta confirma que es tuyo",
            Short = "Se verifica con tu cuenta que el mando te pertenece.",
            Detail = "Tu PC entrega ese sobre al servicio en línea usando el inicio de sesión de TU cuenta. " +
                     "El servicio responde \"sí, este mando puede usarse con esta cuenta\". Tus credenciales " +
                     "solo viven en memoria durante el proceso: nunca se guardan ni se incrustan en la app."
        },
        new()
        {
            Glyph = "🔓",
            Title = "5 · Desbloqueo",
            Short = "El mando recibe la luz verde y se desbloquea.",
            Detail = "Con la confirmación de tu cuenta, tu PC le manda al mando la señal de desbloqueo. " +
                     "El mando queda listo para enviar lo que pulses."
        },
        new()
        {
            Glyph = "🎮",
            Title = "6 · Botones → teclas",
            Short = "Cada botón viaja cifrado y se convierte en una tecla.",
            Detail = "A partir de ahora, cada vez que pulsas un botón, el mando lo envía cifrado y tu PC lo " +
                     "traduce a una tecla del teclado (izquierda → ←, derecha → →). Así funciona con " +
                     "cualquier app de ciclismo indoor que entienda el teclado."
        },
    };

    /// <summary>Índice del paso humano correspondiente a una fase técnica (−1 si no aplica).</summary>
    public static int PhaseToStepIndex(BridgePhase phase) => phase switch
    {
        BridgePhase.Scanning or BridgePhase.Connecting => 0,
        BridgePhase.Handshake => 1,
        BridgePhase.WaitingChallenge or BridgePhase.ChallengeCaptured => 2,
        BridgePhase.ServerAuth => 3,
        BridgePhase.Unlocking => 4,
        BridgePhase.Listening or BridgePhase.SessionResolved or BridgePhase.ButtonPressed => 5,
        _ => -1
    };
}
