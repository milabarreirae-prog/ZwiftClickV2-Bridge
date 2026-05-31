using System.Collections.Generic;
using System.Linq;
using ZwiftClickV2.Bridge.Bridge;

namespace Violeta.Models;

/// <summary>
/// Una acción asignable a un botón del mando: un identificador estable (para el aprendiz de
/// calibración del puente), una etiqueta y un símbolo para la interfaz, y la tecla que emula.
/// </summary>
public sealed class MappableAction
{
    public required string Id { get; init; }
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public required byte Key { get; init; }
}

/// <summary>
/// Un preset de mapeo: un conjunto de acciones que la persona puede calibrar a los botones de su
/// mando. Los dos primeros suelen ser «+» (subir) y «−» (bajar), que admiten el inversor «Invertir».
/// </summary>
public sealed class KeyPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<MappableAction> Actions { get; init; }

    public override string ToString() => Name;
}

/// <summary>Catálogo de presets disponibles en la interfaz de Violeta.</summary>
public static class KeyPresets
{
    private static MappableAction Plus(byte key) => new() { Id = "plus", Glyph = "+", Label = "+ (subir marcha)", Key = key };
    private static MappableAction Minus(byte key) => new() { Id = "minus", Glyph = "−", Label = "− (bajar marcha)", Key = key };

    public static readonly IReadOnlyList<KeyPreset> All = new List<KeyPreset>
    {
        new()
        {
            Id = "mywhoosh_full",
            Name = "MyWhoosh completo  (los 10 botones del Click V2)",
            Description = "Mapea todos los botones del Click V2 a los atajos confirmados de MyWhoosh: " +
                          "marchas (I/K), dirección (←/→), UI mínima (U), ocultar UI (H) y cuatro emotes " +
                          "(1–4). Calibra solo los botones que tenga tu mando; los demás se quedan sin asignar.",
            Actions = new List<MappableAction>
            {
                Plus(KeyboardEmulator.VK_I),
                Minus(KeyboardEmulator.VK_K),
                new() { Id = "left",    Glyph = "←", Label = "Izquierda (←)",       Key = KeyboardEmulator.VK_LEFT },
                new() { Id = "right",   Glyph = "→", Label = "Derecha (→)",         Key = KeyboardEmulator.VK_RIGHT },
                new() { Id = "nav_up",  Glyph = "↑", Label = "UI mínima (U)",       Key = KeyboardEmulator.VK_U },
                new() { Id = "nav_down",Glyph = "↓", Label = "Ocultar UI (H)",      Key = KeyboardEmulator.VK_H },
                new() { Id = "btn_a",   Glyph = "A", Label = "Emote: paz (1)",       Key = KeyboardEmulator.VK_1 },
                new() { Id = "btn_b",   Glyph = "B", Label = "Emote: saludo (2)",    Key = KeyboardEmulator.VK_2 },
                new() { Id = "btn_x",   Glyph = "X", Label = "Emote: choque (3)",    Key = KeyboardEmulator.VK_3 },
                new() { Id = "btn_y",   Glyph = "Y", Label = "Emote: dab (4)",       Key = KeyboardEmulator.VK_4 },
            },
        },
    };

    public static KeyPreset ById(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All[0];
}
