using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Violeta.Models;
using Violeta.Services;
using ZwiftClickV2.Bridge.Bridge;

namespace Violeta.Views;

public partial class ConnectView : UserControl
{
    private readonly List<Border> _stepBorders = new();
    private readonly List<TextBlock> _stepDots = new();
    private BridgeRunner? _runner;
    private int _lastStep = -1;
    private int _buttonCount;
    private string _calPlus = "sin calibrar";
    private string _calMinus = "sin calibrar";

    public ConnectView()
    {
        InitializeComponent();
        BuildSteps();
    }

    private Brush B(string key) => (Brush)FindResource(key);

    // ── Construcción de la lista de pasos en vivo ──────────────────────────
    private void BuildSteps()
    {
        foreach (var step in TutorialSteps.All)
        {
            var glyphBadge = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(11),
                Background = B("SurfaceHiBrush"), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = step.Glyph, FontSize = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var title = new TextBlock { Text = step.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush"), TextWrapping = TextWrapping.Wrap };
            var sub = new TextBlock { Text = step.Short, FontSize = 11.5, Foreground = B("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            var textStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(title);
            textStack.Children.Add(sub);

            var dot = new TextBlock { Text = "○", FontSize = 16, Foreground = B("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(glyphBadge, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(dot, 2);
            grid.Children.Add(glyphBadge);
            grid.Children.Add(textStack);
            grid.Children.Add(dot);

            var border = new Border
            {
                CornerRadius = new CornerRadius(12), Background = B("SurfaceBrush"),
                BorderBrush = B("StrokeBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0.55, Child = grid
            };

            _stepBorders.Add(border);
            _stepDots.Add(dot);
            spSteps.Children.Add(border);
        }
    }

    private void ResetSteps()
    {
        for (int i = 0; i < _stepBorders.Count; i++)
        {
            _stepBorders[i].Opacity = 0.55;
            _stepBorders[i].Background = B("SurfaceBrush");
            _stepDots[i].Text = "○";
            _stepDots[i].Foreground = B("TextMutedBrush");
        }
    }

    private void UpdateSteps(int current, bool error)
    {
        for (int i = 0; i < _stepBorders.Count; i++)
        {
            if (error && i == current)
            {
                _stepBorders[i].Opacity = 1; _stepBorders[i].Background = B("SurfaceHiBrush");
                _stepDots[i].Text = "✕"; _stepDots[i].Foreground = B("DangerBrush");
            }
            else if (i < current)
            {
                _stepBorders[i].Opacity = 1; _stepBorders[i].Background = B("SurfaceBrush");
                _stepDots[i].Text = "●"; _stepDots[i].Foreground = B("SuccessBrush");
            }
            else if (i == current)
            {
                _stepBorders[i].Opacity = 1; _stepBorders[i].Background = B("SurfaceHiBrush");
                _stepDots[i].Text = "◐"; _stepDots[i].Foreground = B("LilacBrush");
            }
            else
            {
                _stepBorders[i].Opacity = 0.55; _stepBorders[i].Background = B("SurfaceBrush");
                _stepDots[i].Text = "○"; _stepDots[i].Foreground = B("TextMutedBrush");
            }
        }
    }

    // ── Modo de acceso ─────────────────────────────────────────────────────
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlUserPass == null || pnlToken == null) return; // durante la carga del XAML
        pnlUserPass.Visibility = rbUserPass.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pnlToken.Visibility = rbToken.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Arrancar / detener ──────────────────────────────────────────────────
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        RunOptions? options = BuildOptions();
        if (options == null) return;

        _lastStep = -1;
        _buttonCount = 0;
        spLog.Children.Clear();
        ResetSteps();
        lblLastButton.Text = "–";
        lblLastButton.Foreground = B("LilacBrush");
        lblCount.Text = "Aún no se ha pulsado ningún botón.";

        btnStart.IsEnabled = false;
        btnStop.IsEnabled = true;
        SetStatus("Conectando…", error: false);

        btnCalPlus.IsEnabled = false;
        btnCalMinus.IsEnabled = false;
        _calPlus = "sin calibrar";
        _calMinus = "sin calibrar";
        UpdateCalLabel();

        _runner = new BridgeRunner();
        _runner.ProgressChanged += OnProgress;
        _runner.ButtonEmitted += OnButton;
        _runner.DiagnosticFrame += OnDiagnosticFrame;
        _runner.CalibrationFinished += OnCalibrationFinished;
        _runner.Finished += OnFinished;
        _runner.Start(options);
    }

    private RunOptions? BuildOptions()
    {
        AuthMode mode;
        string? user = null, pass = null, token = null;

        if (rbDiag.IsChecked == true)
        {
            mode = AuthMode.DiagnoseOnly;
        }
        else if (rbToken.IsChecked == true)
        {
            token = txtToken.Text?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("Pega tu token para continuar.", error: true);
                return null;
            }
            mode = chkAccess.IsChecked == true ? AuthMode.AccessToken : AuthMode.RefreshToken;
        }
        else
        {
            user = txtUser.Text?.Trim();
            pass = pwd.Password;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                SetStatus("Escribe el correo y la contraseña de tu cuenta.", error: true);
                return null;
            }
            mode = AuthMode.UsernamePassword;
        }

        string device = string.IsNullOrWhiteSpace(txtDevice.Text) ? "Zwift" : txtDevice.Text.Trim();

        // Mapeo de botones según el preset elegido (− , +).
        string preset = (cmbMapping.SelectedItem as ComboBoxItem)?.Tag as string ?? "gears";
        (byte minus, byte plus) = preset switch
        {
            "arrows" => (KeyboardEmulator.VK_LEFT, KeyboardEmulator.VK_RIGHT),
            "steer" => (KeyboardEmulator.VK_A, KeyboardEmulator.VK_D),
            _ => (KeyboardEmulator.VK_K, KeyboardEmulator.VK_I),   // gears (MyWoosh)
        };
        if (chkSwap.IsChecked == true)
            (minus, plus) = (plus, minus);

        return new RunOptions
        {
            Mode = mode,
            Username = user,
            Password = pass,
            Token = token,
            DeviceName = device,
            EmulateKeyboard = chkKeyboard.IsChecked == true,
            KeyMinus = minus,
            KeyPlus = plus
        };
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runner?.Stop();
        btnStart.IsEnabled = true;
        btnStop.IsEnabled = false;
        SetStatus("Detenido.", error: false);
    }

    // ── Eventos del puente ──────────────────────────────────────────────────
    private void OnProgress(BridgeProgress p)
    {
        AppendLog(p.Message, p.IsError);

        int idx = TutorialSteps.PhaseToStepIndex(p.Phase);
        if (idx >= 0)
        {
            _lastStep = idx;
            UpdateSteps(idx, error: false);
        }
        else if (p.Phase == BridgePhase.Failed)
        {
            UpdateSteps(_lastStep, error: true);
        }

        SetStatus(p.Message, p.IsError);

        if (p.IsError)
        {
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
        }
    }

    private void OnButton(BridgeButtonEvent b)
    {
        _buttonCount++;
        // Símbolo grande según la tecla emulada (no según el lado), para que coincida con el mapeo.
        string symbol = b.VirtualKey switch
        {
            KeyboardEmulator.VK_I => "+",
            KeyboardEmulator.VK_K => "−",
            KeyboardEmulator.VK_RIGHT or KeyboardEmulator.VK_D => "→",
            KeyboardEmulator.VK_LEFT or KeyboardEmulator.VK_A => "←",
            _ => "•"
        };
        lblLastButton.Text = symbol;
        lblLastButton.Foreground = B("BloomBrush");
        lblCount.Text = $"Botón {b.Label} · {_buttonCount} pulsación(es)";
    }

    private void OnDiagnosticFrame(string line)
    {
        // Tramas crudas post-unlock: en un color distinto, monoespaciado, para leer el hex.
        spLog.Children.Add(new TextBlock
        {
            Text = "  🔬 " + line,
            FontSize = 11.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = B("LilacBrush")
        });
        scLog.ScrollToEnd();
    }

    private void OnFinished(bool ok)
    {
        if (ok)
        {
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            btnCalPlus.IsEnabled = true;
            btnCalMinus.IsEnabled = true;
            UpdateSteps(5, error: false);
            SetStatus("¡Conectado! Calibra tus botones: pulsa «Calibrar +» y aprieta el botón de subir.", error: false, success: true);
        }
        else
        {
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnCalPlus.IsEnabled = false;
            btnCalMinus.IsEnabled = false;
        }
    }

    // ── Calibración ─────────────────────────────────────────────────────────
    private void CalPlus_Click(object sender, RoutedEventArgs e)
    {
        _runner?.StartCalibration("plus");
        btnCalPlus.IsEnabled = false; btnCalMinus.IsEnabled = false;
    }

    private void CalMinus_Click(object sender, RoutedEventArgs e)
    {
        _runner?.StartCalibration("minus");
        btnCalPlus.IsEnabled = false; btnCalMinus.IsEnabled = false;
    }

    private void OnCalibrationFinished(string action, string signature, bool ok)
    {
        btnCalPlus.IsEnabled = true;
        btnCalMinus.IsEnabled = true;
        string shortSig = signature.Length > 16 ? signature[..16] + "…" : signature;
        if (action == "plus") _calPlus = ok ? shortSig : "sin calibrar";
        else _calMinus = ok ? shortSig : "sin calibrar";
        UpdateCalLabel();
        AppendLog(ok
            ? $"✅ Botón «{(action == "plus" ? "+" : "−")}» calibrado (firma {shortSig})."
            : $"No detecté un botón claro para «{(action == "plus" ? "+" : "−")}». Reintenta y aprieta firme varias veces.",
            error: !ok);
    }

    private void UpdateCalLabel()
        => lblCal.Text = $"+ : {_calPlus}   ·   − : {_calMinus}";

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in spLog.Children)
            if (child is TextBlock tb) sb.AppendLine(tb.Text);
        try { Clipboard.SetText(sb.ToString()); SetStatus("Registro copiado al portapapeles.", error: false, success: true); }
        catch { SetStatus("No pude copiar el registro.", error: true); }
    }

    // ── Utilidades de UI ────────────────────────────────────────────────────
    private void SetStatus(string text, bool error, bool success = false)
    {
        lblStatus.Text = text;
        lblStatus.Foreground = error ? B("DangerBrush") : success ? B("SuccessBrush") : B("LilacSoftBrush");
    }

    private void AppendLog(string text, bool error)
    {
        spLog.Children.Add(new TextBlock
        {
            Text = "• " + text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5),
            Foreground = error ? B("DangerBrush") : B("TextMutedBrush")
        });
        scLog.ScrollToEnd();
    }
}
