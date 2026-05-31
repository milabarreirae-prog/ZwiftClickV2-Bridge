using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Violeta.Models;
using Violeta.Services;

namespace Violeta.Views;

public partial class WelcomeView : UserControl
{
    /// <summary>Pide a la ventana principal que navegue a otra vista ("connect", "tutorial"…).</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>Pide arrancar a rodar (abrir MyWhoosh + conectar) desde Inicio.</summary>
    public event Action? RideRequested;

    public WelcomeView()
    {
        InitializeComponent();
        AppState.Current.Changed += OnStateChanged;
        Loaded += (_, _) => Refresh();
    }

    private Brush B(string key) => (Brush)FindResource(key);

    /// <summary>Relee preferencias y estado, y repinta el panel. Se llama al navegar a Inicio.</summary>
    public void Refresh()
    {
        var s = UserSettings.Load();
        lblTileDevice.Text = string.IsNullOrWhiteSpace(s.DeviceName) ? "Zwift" : s.DeviceName;

        var preset = KeyPresets.ById(s.MappingPreset);
        lblTilePreset.Text = ShortPresetName(preset);

        bool myWhooshReady = !string.IsNullOrWhiteSpace(s.MyWhooshPath) && File.Exists(s.MyWhooshPath);
        lblTileMyWhoosh.Text = myWhooshReady ? "Listo" : "Sin configurar";
        lblTileMyWhoosh.Foreground = myWhooshReady ? B("SuccessBrush") : B("TextBrush");

        BuildButtonChips(preset, s.SwapPlusMinus);
        ApplyStatus();
    }

    private static string ShortPresetName(KeyPreset p) => p.Id switch
    {
        "gears" => "Cambio de marchas",
        "arrows" => "Flechas",
        "steer" => "Dirección",
        "mywhoosh_full" => "MyWhoosh completo",
        "emotes" => "Emotes",
        _ => p.Name
    };

    private void BuildButtonChips(KeyPreset preset, bool swap)
    {
        spButtonsChips.Children.Clear();
        foreach (var a in preset.Actions)
        {
            // Reflejar la inversión + / − en la etiqueta de la tecla mostrada.
            string keyName = KeyName(ResolveKey(preset, a, swap));
            var chip = new Border { Style = (Style)FindResource("Chip") };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = a.Glyph, FontWeight = FontWeights.Bold, Foreground = B("LilacBrush"), FontSize = 12.5 });
            sp.Children.Add(new TextBlock { Text = "  " + a.Label.Replace("(", "").Replace(")", "") + " · " + keyName, Foreground = B("TextBrush"), FontSize = 12.5 });
            chip.Child = sp;
            spButtonsChips.Children.Add(chip);
        }
    }

    private static byte ResolveKey(KeyPreset preset, MappableAction a, bool swap)
    {
        if (!swap) return a.Key;
        if (a.Id == "plus") foreach (var x in preset.Actions) if (x.Id == "minus") return x.Key;
        if (a.Id == "minus") foreach (var x in preset.Actions) if (x.Id == "plus") return x.Key;
        return a.Key;
    }

    private static string KeyName(byte vk) => vk switch
    {
        0x25 => "←", 0x27 => "→", 0x26 => "↑", 0x28 => "↓",
        0x49 => "I", 0x4B => "K", 0x41 => "A", 0x44 => "D", 0x55 => "U", 0x48 => "H",
        >= 0x31 and <= 0x39 => ((char)vk).ToString(),
        _ => "tecla"
    };

    // ── Estado de conexión en vivo ──────────────────────────────────────────
    private void OnStateChanged() => Dispatcher.Invoke(ApplyStatus);

    private void ApplyStatus()
    {
        var st = AppState.Current.Status;
        StopPulse();
        switch (st)
        {
            case MandoStatus.Connecting:
                ring.Stroke = B("LilacBrush");
                statusDot.Fill = B("LilacBrush");
                lblStatusTitle.Text = "Conectando…";
                lblStatusSub.Text = "Estamos hablando con tu mando.";
                StartPulse();
                break;
            case MandoStatus.Connected:
                ring.Stroke = B("SuccessBrush");
                statusDot.Fill = B("SuccessBrush");
                lblStatusTitle.Text = "¡Mando conectado!";
                lblStatusSub.Text = string.IsNullOrWhiteSpace(AppState.Current.DeviceName)
                    ? "Listo para rodar."
                    : $"{AppState.Current.DeviceName} · listo para rodar.";
                break;
            default:
                ring.Stroke = B("StrokeBrush");
                statusDot.Fill = B("TextMutedBrush");
                lblStatusTitle.Text = "Mando desconectado";
                lblStatusSub.Text = "Conéctalo para empezar a rodar.";
                break;
        }
    }

    private void StartPulse()
    {
        var anim = new DoubleAnimation(1.0, 0.35, new Duration(TimeSpan.FromSeconds(0.95)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        statusDot.BeginAnimation(OpacityProperty, anim);
    }

    private void StopPulse()
    {
        statusDot.BeginAnimation(OpacityProperty, null);
        statusDot.Opacity = 1;
    }

    private void Ride_Click(object sender, RoutedEventArgs e) => RideRequested?.Invoke();
    private void Go_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("connect");
}
