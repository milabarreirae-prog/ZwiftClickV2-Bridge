using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Violeta.Services;

/// <summary>
/// Preferencias de comodidad de la persona, guardadas en <c>%AppData%\Violeta\settings.json</c>.
///
/// IMPORTANTE — privacidad: aquí NUNCA se guardan la contraseña ni el token. Esos secretos viven
/// solo en memoria durante el inicio de sesión (la promesa de Violeta). El correo se guarda solo
/// si la persona marca «recordar mi correo», porque es un identificador, no un secreto.
/// </summary>
public sealed class UserSettings
{
    public string MappingPreset { get; set; } = "mywhoosh_full"; // por defecto: los 10 botones del Click V2
    public bool SwapPlusMinus { get; set; }
    public string DeviceName { get; set; } = "Zwift";
    public bool EmulateKeyboard { get; set; } = true;
    public string AuthMode { get; set; } = "account"; // account | token | diag
    public bool TokenIsAccess { get; set; }
    public bool RememberEmail { get; set; }
    public string Email { get; set; } = "";
    public bool AutoCalibrate { get; set; } = false; // el Click V2 viene precalibrado; calibrar es opcional
    public string MyWhooshPath { get; set; } = "";

    /// <summary>
    /// Mapa aprendido de botones que PERSISTE entre sesiones: firma del botón → identificador de acción
    /// ("plus", "left", "btn_a"…). Así calibrar (cuando hace falta) es una sola vez para siempre, no
    /// cada vez que se conecta. Se combina con el perfil por defecto del Click V2 (que tiene prioridad
    /// inferior: lo aprendido manda).
    /// </summary>
    public Dictionary<string, string> ButtonMap { get; set; } = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Violeta");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Lee las preferencias del disco. Si no existen o están corruptas, devuelve valores por defecto.</summary>
    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<UserSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // Archivo inaccesible o corrupto: las preferencias son una comodidad, no un requisito.
        }
        return new UserSettings();
    }

    /// <summary>Guarda las preferencias en disco. Nunca lanza: un fallo aquí no debe romper la app.</summary>
    public void Save()
    {
        try
        {
            if (!RememberEmail) Email = string.Empty; // no conservar el correo si no se pidió
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // No fatal.
        }
    }
}
