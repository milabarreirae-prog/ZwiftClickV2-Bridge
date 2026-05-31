using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Violeta.Controls;

/// <summary>
/// Mando Click V2 en vivo. <see cref="Flash(string)"/> ilumina la pieza pulsada con un golpe de luz
/// (cara encendida + resplandor + rebote). <see cref="SetPowered"/> deja el mando "alimentado" con un
/// brillo tenue cuando está conectado. <see cref="StartDemo"/> lo hace parpadear solo para mostrar
/// qué hace antes de conectar.
/// </summary>
public partial class ClickDeck : UserControl
{
    /// <summary>Una pieza encendible: su escala, su resplandor y su cara iluminada.</summary>
    private sealed record Part(ScaleTransform Scale, DropShadowEffect Glow, UIElement Lit);

    private readonly Dictionary<string, Part> _parts = new();

    // Línea base del estado "alimentado" (conectado): glow tenue persistente.
    private bool _powered;
    private double LitBase => _powered ? 0.06 : 0.0;
    private double GlowBase => _powered ? 0.16 : 0.0;
    private double GlowBlurBase => _powered ? 13.0 : 0.0;

    // Mapa de tecla virtual → acción (preset MyWhoosh completo), como respaldo si no llega ActionId.
    private static readonly Dictionary<byte, string> KeyToAction = new()
    {
        [0x49] = "plus",   [0x4B] = "minus",            // I / K
        [0x25] = "left",   [0x27] = "right",            // ← / →
        [0x55] = "nav_up", [0x48] = "nav_down",         // U / H
        [0x31] = "btn_a",  [0x32] = "btn_b",            // 1 / 2
        [0x33] = "btn_x",  [0x34] = "btn_y",            // 3 / 4
    };

    private DispatcherTimer? _demo;
    private int _demoIdx;

    public ClickDeck()
    {
        InitializeComponent();

        _parts["plus"]    = new Part(sclPlus,  glowPlus,  litPlus);
        _parts["minus"]   = new Part(sclMinus, glowMinus, litMinus);
        _parts["nav_up"]  = new Part(sclUp,    glowUp,    litUp);
        _parts["nav_down"]= new Part(sclDown,  glowDown,  litDown);
        _parts["left"]    = new Part(sclLeft,  glowLeft,  litLeft);
        _parts["right"]   = new Part(sclRight, glowRight, litRight);
        _parts["btn_a"]   = new Part(sclA,     glowA,     litA);
        _parts["btn_b"]   = new Part(sclB,     glowB,     litB);
        _parts["btn_x"]   = new Part(sclX,     glowX,     litX);
        _parts["btn_y"]   = new Part(sclY,     glowY,     litY);

        Unloaded += (_, _) => StopDemo();
    }

    /// <summary>Ilumina la pieza de una acción ("plus", "left", "btn_a"…). Ignora ids desconocidos.</summary>
    public void Flash(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId) || !_parts.TryGetValue(actionId, out var p)) return;
        FlashPart(p);
    }

    /// <summary>Respaldo: ilumina por la tecla emulada cuando no se conoce la acción.</summary>
    public void FlashByKey(byte virtualKey)
    {
        if (KeyToAction.TryGetValue(virtualKey, out var id)) Flash(id);
    }

    private void FlashPart(Part p)
    {
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var spring  = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };

        p.Lit.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.95, new Duration(TimeSpan.FromMilliseconds(150)))
            { AutoReverse = true, FillBehavior = FillBehavior.Stop, EasingFunction = easeOut });

        p.Glow.BeginAnimation(DropShadowEffect.OpacityProperty,
            new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(150)))
            { AutoReverse = true, FillBehavior = FillBehavior.Stop, EasingFunction = easeOut });

        p.Glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(44, new Duration(TimeSpan.FromMilliseconds(150)))
            { AutoReverse = true, FillBehavior = FillBehavior.Stop, EasingFunction = easeOut });

        var pop = new DoubleAnimation(1.12, new Duration(TimeSpan.FromMilliseconds(140)))
            { AutoReverse = true, FillBehavior = FillBehavior.Stop, EasingFunction = spring };
        p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Deja el mando "encendido" con un brillo tenue (al conectar) o apagado (al desconectar).</summary>
    public void SetPowered(bool on)
    {
        _powered = on;
        foreach (var p in _parts.Values)
        {
            p.Lit.BeginAnimation(OpacityProperty, null);
            p.Glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            p.Glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            p.Lit.Opacity = LitBase;
            p.Glow.Opacity = GlowBase;
            p.Glow.BlurRadius = GlowBlurBase;
        }
    }

    // ── Demo suave (solo en Inicio cuando está desconectado): el mando parpadea solo ──
    private static readonly string[] DemoOrder =
        { "minus", "plus", "left", "right", "nav_up", "btn_a", "btn_b", "nav_down", "btn_x", "btn_y" };

    public void StartDemo()
    {
        if (_demo != null) return;
        _demo = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        _demo.Tick += (_, _) =>
        {
            Flash(DemoOrder[_demoIdx % DemoOrder.Length]);
            _demoIdx++;
        };
        _demo.Start();
    }

    public void StopDemo()
    {
        _demo?.Stop();
        _demo = null;
    }
}
