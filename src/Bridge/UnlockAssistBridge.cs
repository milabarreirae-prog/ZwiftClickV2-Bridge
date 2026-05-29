using System.Diagnostics;
using System.Text;
using ZwiftClickV2.Bridge.BLE;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Modo interactivo "Unlock Assist" que guia al usuario paso a paso
/// para desbloquear el Click V2 antes de ejecutar el handshake ECDH.
/// 
/// Flujo:
/// 1. Conecta al Click V2
/// 2. Diagnostica el estado (Sleeping / Locked / Unlocked / NoResponse)
/// 3. Segun el estado, guia al usuario con instrucciones especificas
/// 4. Cuando el dispositivo esta listo, ejecuta el HotPairBridge
/// </summary>
public class UnlockAssistBridge
{
    public async Task<int> RunAsync(string deviceName = "Zwift Click")
    {
        Console.WriteLine("=============================================================");
        Console.WriteLine("  ZwiftClickV2 - UNLOCK ASSIST (Asistente de Desbloqueo)");
        Console.WriteLine("=============================================================");
        Console.WriteLine();
        Console.WriteLine("  Este modo diagnostica el estado de tu Click V2 y te guia");
        Console.WriteLine("  paso a paso para desbloquearlo.");
        Console.WriteLine();

        // ── Fase 1: Diagnostico inicial ─────────────────────────────────
        Console.WriteLine("--- FASE 1: Diagnostico inicial ---");
        Console.WriteLine("  Conectando al dispositivo para verificar su estado...");
        Console.WriteLine();

        var (state, rawResponse) = await HotPairBridge.DiagnoseDeviceState(deviceName);

        // ── Fase 2: Actuar segun el estado ──────────────────────────────
        switch (state)
        {
            case HotPairBridge.DeviceState.Unlocked_PublicKey:
                return await HandleUnlockedAsync(deviceName);

            case HotPairBridge.DeviceState.Locked_ErrorResponse:
                return await HandleLockedAsync(deviceName);

            case HotPairBridge.DeviceState.Sleeping_EchoOnly:
                return await HandleSleepingAsync(deviceName);

            case HotPairBridge.DeviceState.NoResponse:
                return await HandleNoResponseAsync(deviceName);

            default:
                Console.WriteLine();
                Console.WriteLine("[!] Estado desconocido. Mostrando respuesta cruda:");
                if (rawResponse != null)
                    Console.WriteLine($"    {BitConverter.ToString(rawResponse).Replace("-", " ")}");
                Console.WriteLine();
                Console.WriteLine("    Intentando hot-pair de todas formas...");
                return await TryHotPairAsync(deviceName);
        }
    }

    /// <summary>
    /// Dispositivo desbloqueado - ejecutar bridge inmediatamente.
    /// </summary>
    private async Task<int> HandleUnlockedAsync(string deviceName)
    {
        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("  [OK] DISPOSITIVO DESBLOQUEADO!");
        Console.WriteLine("=============================================================");
        Console.WriteLine();
        Console.WriteLine("  El Click V2 respondio con clave publica EC.");
        Console.WriteLine("  Esta listo para el handshake ECDH completo.");
        Console.WriteLine();
        Console.WriteLine("  Ejecutando HotPairBridge AHORA...");
        Console.WriteLine();

        return await TryHotPairAsync(deviceName);
    }

    /// <summary>
    /// Dispositivo bloqueado por DRM - guiar al usuario a usar Zwift oficial.
    /// </summary>
    private async Task<int> HandleLockedAsync(string deviceName)
    {
        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("  [BLOQUEADO] DISPOSITIVO CON DRM ACTIVO");
        Console.WriteLine("=============================================================");
        Console.WriteLine();
        Console.WriteLine("  El Click V2 rechaza el handshake. Necesita ser desbloqueado");
        Console.WriteLine("  mediante la aplicacion oficial de Zwift.");
        Console.WriteLine();
        Console.WriteLine("  INSTRUCCIONES:");
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │ 1. Abre Zwift oficial en tu PC                          │");
        Console.WriteLine("  │ 2. Ve a la pantalla de dispositivos                     │");
        Console.WriteLine("  │ 3. Conecta el Zwift Click V2                            │");
        Console.WriteLine("  │ 4. Espera ~30 segundos (LED debe ponerse azul/verde)    │");
        Console.WriteLine("  │ 5. CIERRA Zwift COMPLETAMENTE                           │");
        Console.WriteLine("  │    (verifica en Admin. de Tareas que no este corriendo) │");
        Console.WriteLine("  │ 6. Vuelve a ejecutar este asistente                     │");
        Console.WriteLine("  │    dotnet run --project src -- --unlock-assist          │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        Console.WriteLine("  O usa el script de Hot Pairing para hacerlo mas rapido:");
        Console.WriteLine("    powershell -File scripts\\hot_pairing.ps1");
        Console.WriteLine();

        // Preguntar si quiere intentar el hot-pair ahora
        Console.Write("  Ya hiciste el proceso con Zwift? (s/n): ");
        var key = Console.ReadKey();
        Console.WriteLine();

        if (key.KeyChar == 's' || key.KeyChar == 'S')
        {
            Console.WriteLine();
            Console.WriteLine("  Ejecutando HotPairBridge AHORA (ventana critica)...");
            Console.WriteLine("  Asegurate de que Zwift YA este cerrado.");
            Console.WriteLine();

            // Pequena pausa para asegurar liberacion del stack BLE
            await Task.Delay(500);
            return await TryHotPairAsync(deviceName);
        }

        Console.WriteLine();
        Console.WriteLine("  Vuelve a ejecutar --unlock-assist cuando hayas completado");
        Console.WriteLine("  el proceso con Zwift oficial.");
        return 1;
    }

    /// <summary>
    /// Dispositivo dormido (solo eco) - guiar ciclo de encendido.
    /// </summary>
    private async Task<int> HandleSleepingAsync(string deviceName)
    {
        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("  [DORMIDO] DISPOSITIVO EN MODO SLEEP");
        Console.WriteLine("=============================================================");
        Console.WriteLine();
        Console.WriteLine("  El Click V2 solo hace eco de 'RideOn' (6 bytes).");
        Console.WriteLine("  No esta procesando handshakes. Necesita un ciclo de");
        Console.WriteLine("  encendido para despertar.");
        Console.WriteLine();
        Console.WriteLine("  INSTRUCCIONES:");
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │ 1. APAGA el Click V2 (quita las baterias)               │");
        Console.WriteLine("  │ 2. Espera 5 segundos                                    │");
        Console.WriteLine("  │ 3. VUELVE a poner las baterias (enciende el Click)      │");
        Console.WriteLine("  │ 4. Espera a que el LED parpadee (modo pairing)          │");
        Console.WriteLine("  │ 5. Presiona ENTER para continuar                        │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        Console.Write("  Presiona ENTER cuando hayas completado el ciclo: ");
        Console.ReadLine();
        Console.WriteLine();

        // Re-diagnosticar despues del ciclo de encendido
        Console.WriteLine("  Verificando estado despues del ciclo de encendido...");
        var (newState, _) = await HotPairBridge.DiagnoseDeviceState(deviceName);

        if (newState == HotPairBridge.DeviceState.Unlocked_PublicKey)
        {
            return await HandleUnlockedAsync(deviceName);
        }
        else if (newState == HotPairBridge.DeviceState.Locked_ErrorResponse)
        {
            return await HandleLockedAsync(deviceName);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  [!] El dispositivo sigue sin responder correctamente.");
            Console.WriteLine("  Estado actual: " + newState);
            Console.WriteLine();
            Console.WriteLine("  Recomendacion: Abre Zwift oficial, conecta el Click 30s,");
            Console.WriteLine("  cierra Zwift y vuelve a ejecutar --unlock-assist.");
            return 1;
        }
    }

    /// <summary>
    /// Sin respuesta del dispositivo.
    /// </summary>
    private async Task<int> HandleNoResponseAsync(string deviceName)
    {
        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("  [SIN RESPUESTA] DISPOSITIVO NO DETECTADO");
        Console.WriteLine("=============================================================");
        Console.WriteLine();
        Console.WriteLine("  El Click V2 no respondio al ping de diagnostico.");
        Console.WriteLine();
        Console.WriteLine("  VERIFICA:");
        Console.WriteLine("  - El Click V2 esta encendido? (LED debe parpadear)");
        Console.WriteLine("  - Las baterias tienen carga?");
        Console.WriteLine("  - El Bluetooth de Windows esta activado?");
        Console.WriteLine("  - El Click no esta conectado a otro dispositivo?");
        Console.WriteLine();
        Console.Write("  Presiona ENTER para reintentar el diagnostico: ");
        Console.ReadLine();
        Console.WriteLine();

        // Reintentar
        return await RunAsync(deviceName);
    }

    /// <summary>
    /// Intenta ejecutar el HotPairBridge y retorna el resultado.
    /// </summary>
    private static async Task<int> TryHotPairAsync(string deviceName)
    {
        try
        {
            using var bridge = new HotPairBridge();
            bool success = await bridge.StartAsync(deviceName);

            if (success)
            {
                Console.WriteLine();
                Console.WriteLine("=============================================================");
                Console.WriteLine("  [OK] BRIDGE OPERATIVO!");
                Console.WriteLine("=============================================================");
                Console.WriteLine();
                Console.WriteLine("  El bridge esta corriendo y emulando teclas.");
                Console.WriteLine("  Abre MyWoosh y prueba los botones del Click V2.");
                Console.WriteLine();
                Console.WriteLine("  Presiona ENTER para detener el bridge...");
                Console.ReadLine();
                bridge.Stop();
                return 0;
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  [!] El Hot Pairing fallo.");
                Console.WriteLine("  Vuelve a ejecutar --unlock-assist para diagnostico detallado.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  [ERROR] Excepcion: {ex.Message}");
            return 1;
        }
    }
}