using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
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
    private bool _pwdVisible;
    private bool _operational;
    private bool _v1Mode = true;   // modo andriuz/V1 (por defecto): botones deterministas, sin calibración

    private KeyPreset _activePreset = KeyPresets.ById("mywhoosh_full");
    private readonly UserSettings _settings = UserSettings.Load();

    // Fichas de calibración por acción (id → controles + estado).
    private sealed record ActionChip(Border Border, TextBlock Status, Button CalButton);
    private readonly Dictionary<string, ActionChip> _chips = new();
    private readonly Dictionary<string, string> _actionGlyph = new();
    private readonly Dictionary<byte, string> _keyGlyph = new();

    public ConnectView()
    {
        InitializeComponent();
        BuildSteps();

        cmbMapping.ItemsSource = KeyPresets.All;
        ApplySettingsToControls();   // selecciona preset, modo de acceso, opciones…
    }

    private Brush B(string key) => (Brush)FindResource(key);

    // ── Preferencias guardadas ───────────────────────────────────────────────
    private void ApplySettingsToControls()
    {
        int idx = KeyPresets.All.ToList().FindIndex(p => p.Id == _settings.MappingPreset);
        if (idx < 0) idx = KeyPresets.All.ToList().FindIndex(p => p.Id == "mywhoosh_full"); // default: 10 botones
        cmbMapping.SelectedIndex = idx >= 0 ? idx : 0;   // dispara Preset_Changed → fichas + descripción

        chkSwap.IsChecked = _settings.SwapPlusMinus;
        chkAutoCal.IsChecked = _settings.AutoCalibrate;
        chkKeyboard.IsChecked = _settings.EmulateKeyboard;
        txtDevice.Text = string.IsNullOrWhiteSpace(_settings.DeviceName) ? "Zwift" : _settings.DeviceName;
        txtMyWhooshPath.Text = _settings.MyWhooshPath;
        chkAccess.IsChecked = _settings.TokenIsAccess;

        chkRememberEmail.IsChecked = _settings.RememberEmail;
        if (_settings.RememberEmail && !string.IsNullOrWhiteSpace(_settings.Email))
            txtUser.Text = _settings.Email;

        switch (_settings.AuthMode)
        {
            case "token": rbToken.IsChecked = true; break;
            case "diag": rbDiag.IsChecked = true; break;
            default: rbUserPass.IsChecked = true; break;
        }
    }

    /// <summary>Reúne las preferencias actuales (sin secretos) y las guarda en disco.</summary>
    public void PersistSettings()
    {
        _settings.MappingPreset = _activePreset.Id;
        _settings.SwapPlusMinus = chkSwap.IsChecked == true;
        _settings.AutoCalibrate = chkAutoCal.IsChecked == true;
        _settings.EmulateKeyboard = chkKeyboard.IsChecked == true;
        _settings.DeviceName = string.IsNullOrWhiteSpace(txtDevice.Text) ? "Zwift" : txtDevice.Text.Trim();
        _settings.MyWhooshPath = txtMyWhooshPath.Text?.Trim() ?? "";
        _settings.TokenIsAccess = chkAccess.IsChecked == true;
        _settings.AuthMode = rbToken.IsChecked == true ? "token" : rbDiag.IsChecked == true ? "diag" : "account";
        _settings.RememberEmail = chkRememberEmail.IsChecked == true;
        _settings.Email = _settings.RememberEmail ? (txtUser.Text?.Trim() ?? "") : "";
        _settings.Save();
    }

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

    // ── Preset y fichas de calibración ─────────────────────────────────────────
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cmbMapping.SelectedItem is not KeyPreset preset) return;
        _activePreset = preset;
        if (lblPresetDesc != null) lblPresetDesc.Text = preset.Description;
        BuildActionChips();
    }

    private void BuildActionChips()
    {
        spActions.Children.Clear();
        _chips.Clear();
        _actionGlyph.Clear();
        _keyGlyph.Clear();

        foreach (var action in _activePreset.Actions)
        {
            _actionGlyph[action.Id] = action.Glyph;
            _keyGlyph[action.Key] = action.Glyph;

            var glyph = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
                Background = B("SurfaceHiBrush"), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = action.Glyph, FontSize = 14, FontWeight = FontWeights.Bold,
                    Foreground = B("LilacBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };

            var label = new TextBlock { Text = action.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush"), TextWrapping = TextWrapping.Wrap };
            var status = new TextBlock { Text = "sin calibrar", FontSize = 11, Foreground = B("TextMutedBrush"), Margin = new Thickness(0, 1, 0, 0) };
            var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(label);
            texts.Children.Add(status);

            var calBtn = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = "Calibrar", Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12, IsEnabled = _operational, Tag = action.Id,
                VerticalAlignment = VerticalAlignment.Center
            };
            calBtn.Click += CalChip_Click;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(glyph, 0); Grid.SetColumn(texts, 1); Grid.SetColumn(calBtn, 2);
            grid.Children.Add(glyph); grid.Children.Add(texts); grid.Children.Add(calBtn);

            var border = new Border
            {
                CornerRadius = new CornerRadius(11), Background = B("SurfaceBrush"),
                BorderBrush = B("StrokeBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), Child = grid
            };

            _chips[action.Id] = new ActionChip(border, status, calBtn);
            spActions.Children.Add(border);
        }
    }

    private void SetChipsEnabled(bool enabled)
    {
        foreach (var chip in _chips.Values) chip.CalButton.IsEnabled = enabled;
    }

    private void HighlightChip(string actionId, bool active)
    {
        if (!_chips.TryGetValue(actionId, out var chip)) return;
        chip.Border.Background = active ? B("SurfaceHiBrush") : B("SurfaceBrush");
        chip.Border.BorderBrush = active ? B("PrimaryBrush") : B("StrokeBrush");
    }

    // ── Mostrar / ocultar contraseña ────────────────────────────────────────
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (!_pwdVisible)
        {
            txtPwdVisible.Text = pwd.Password;
            pwd.Visibility = Visibility.Collapsed;
            txtPwdVisible.Visibility = Visibility.Visible;
            btnReveal.Content = "🙈";
            _pwdVisible = true;
        }
        else
        {
            pwd.Password = txtPwdVisible.Text;
            txtPwdVisible.Visibility = Visibility.Collapsed;
            pwd.Visibility = Visibility.Visible;
            btnReveal.Content = "👁";
            _pwdVisible = false;
        }
    }

    private string CurrentPassword() => _pwdVisible ? txtPwdVisible.Text : pwd.Password;

    // ── Modo de acceso ─────────────────────────────────────────────────────
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlUserPass == null || pnlToken == null) return; // durante la carga del XAML
        pnlUserPass.Visibility = rbUserPass.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pnlToken.Visibility = rbToken.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Arrancar / detener ──────────────────────────────────────────────────
    private void Ride_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchMyWhoosh()) AppendLog("MyWhoosh abierto. Conectando el mando…", error: false);
        BeginConnect();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => BeginConnect();

    /// <summary>Entrada pública desde Inicio: abre MyWhoosh (si hay ruta) y conecta.</summary>
    public void StartRide()
    {
        if (LaunchMyWhoosh()) AppendLog("MyWhoosh abierto. Conectando el mando…", error: false);
        BeginConnect();
    }

    private bool LaunchMyWhoosh()
    {
        string path = txtMyWhooshPath.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path))
        {
            AppendLog("No encuentro MyWhoosh en la ruta indicada. Revisa «Opciones avanzadas».", error: true);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("No pude abrir MyWhoosh: " + ex.Message, error: true);
            return false;
        }
    }

    private void BeginConnect()
    {
        RunOptions? options = BuildOptions();
        if (options == null) return;
        _v1Mode = options.Protocol == ButtonProtocol.AndriuzV1;

        PersistSettings();

        _lastStep = -1;
        _buttonCount = 0;
        _operational = false;
        spLog.Children.Clear();
        ResetSteps();
        foreach (var id in _chips.Keys.ToList()) { _chips[id].Status.Text = "sin calibrar"; HighlightChip(id, false); }
        SetChipsEnabled(false);
        lblLastButton.Text = "–";
        lblLastButton.Foreground = B("LilacBrush");
        lblCount.Text = "Aún no se ha pulsado ningún botón.";
        deck.SetPowered(true);   // el mando "despierta" mientras conecta

        btnRide.IsEnabled = false;
        btnStart.IsEnabled = false;
        btnStop.IsEnabled = true;
        btnAutoCal.IsEnabled = false;
        prog.Value = 0;
        lblStep.Text = "Preparando…";
        SetStatus("Conectando…", error: false);

        AppState.Current.DeviceName = options.DeviceName;
        AppState.Current.Status = MandoStatus.Connecting;

        _runner = new BridgeRunner();
        _runner.ProgressChanged += OnProgress;
        _runner.ButtonEmitted += OnButton;
        _runner.DiagnosticFrame += OnDiagnosticFrame;
        _runner.CalibrationStepStarted += OnCalibrationStepStarted;
        _runner.CalibrationFinished += OnCalibrationFinished;
        _runner.GuidedCalibrationFinished += OnGuidedCalibrationFinished;
        _runner.Finished += OnFinished;
        _runner.Start(options);
    }

    private RunOptions? BuildOptions()
    {
        string device = string.IsNullOrWhiteSpace(txtDevice.Text) ? "Zwift" : txtDevice.Text.Trim();

        // Modo V1/andriuz (POR DEFECTO): botones en claro, SIN cuenta. No requiere credenciales.
        if (chkUseAccount.IsChecked != true)
            return new RunOptions
            {
                Protocol = ButtonProtocol.AndriuzV1,
                DeviceName = device,
                EmulateKeyboard = chkKeyboard.IsChecked == true,
                Actions = BuildActions(),
                AutoCalibrate = false,                 // V1 es determinista: sin calibración
                SavedButtonMap = _settings.ButtonMap,
            };

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
            pass = CurrentPassword();
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                SetStatus("Escribe el correo y la contraseña de tu cuenta.", error: true);
                return null;
            }
            mode = AuthMode.UsernamePassword;
        }

        return new RunOptions
        {
            Protocol = ButtonProtocol.ZwiftV2,
            Mode = mode,
            Username = user,
            Password = pass,
            Token = token,
            DeviceName = device,
            EmulateKeyboard = chkKeyboard.IsChecked == true,
            Actions = BuildActions(),
            AutoCalibrate = chkAutoCal.IsChecked == true,
            SavedButtonMap = _settings.ButtonMap,
        };
    }

    /// <summary>Acciones del preset activo, con la inversión + / − aplicada si la persona la pidió.</summary>
    private IReadOnlyList<MappableAction> BuildActions()
    {
        var actions = _activePreset.Actions.ToList();
        if (chkSwap.IsChecked == true)
        {
            var plus = actions.FirstOrDefault(a => a.Id == "plus");
            var minus = actions.FirstOrDefault(a => a.Id == "minus");
            if (plus != null && minus != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    if (actions[i].Id == "plus")
                        actions[i] = new MappableAction { Id = plus.Id, Glyph = plus.Glyph, Label = plus.Label, Key = minus.Key };
                    else if (actions[i].Id == "minus")
                        actions[i] = new MappableAction { Id = minus.Id, Glyph = minus.Glyph, Label = minus.Label, Key = plus.Key };
                }
            }
        }
        return actions;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runner?.Stop();
        _operational = false;
        deck.SetPowered(false);
        btnRide.IsEnabled = true;
        btnStart.IsEnabled = true;
        btnStop.IsEnabled = false;
        btnAutoCal.IsEnabled = false;
        SetChipsEnabled(false);
        prog.Value = 0;
        lblStep.Text = "Detenido";
        AppState.Current.Status = MandoStatus.Disconnected;
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
            prog.Value = idx + 1;
            lblStep.Text = $"Paso {idx + 1} de {TutorialSteps.All.Count}";
        }
        else if (p.Phase == BridgePhase.Failed)
        {
            UpdateSteps(_lastStep, error: true);
            lblStep.Text = "Se detuvo";
        }

        SetStatus(p.Message, p.IsError);

        if (p.IsError)
        {
            _operational = false;
            deck.SetPowered(false);
            btnRide.IsEnabled = true;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnAutoCal.IsEnabled = false;
            SetChipsEnabled(false);
            AppState.Current.Status = MandoStatus.Disconnected;
        }
    }

    private void OnButton(BridgeButtonEvent b)
    {
        _buttonCount++;
        string symbol =
            (!string.IsNullOrEmpty(b.ActionId) && _actionGlyph.TryGetValue(b.ActionId, out var g)) ? g
            : _keyGlyph.TryGetValue(b.VirtualKey, out var g2) ? g2
            : "•";
        lblLastButton.Text = symbol;
        lblLastButton.Foreground = B("BloomBrush");
        lblCount.Text = $"{b.Label} · {_buttonCount} pulsación(es)";

        // Ilumina la pieza pulsada en el mando en vivo (aquí y también en Inicio).
        if (!string.IsNullOrEmpty(b.ActionId)) deck.Flash(b.ActionId);
        else deck.FlashByKey(b.VirtualKey);
        AppState.Current.RaiseButton(b.ActionId, b.VirtualKey);
    }

    private void OnDiagnosticFrame(string line)
    {
        spLog.Children.Add(new TextBlock
        {
            Text = "  🔬 " + line,
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas, monospace"),
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
            _operational = true;
            deck.SetPowered(true);
            btnRide.IsEnabled = false;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            btnAutoCal.IsEnabled = true;
            SetChipsEnabled(true);
            UpdateSteps(5, error: false);
            prog.Value = 6;
            lblStep.Text = "¡Conectado!";
            AppState.Current.Status = MandoStatus.Connected;

            MarkPrecalibratedChips();

            // En modo V1/andriuz los botones son deterministas (bitmask): NO hay calibración.
            if (!_v1Mode && chkAutoCal.IsChecked == true)
            {
                SetStatus("¡Conectado! Recalibración: aprieta cada botón cuando se te pida.", error: false, success: true);
                SetChipsEnabled(false);
                btnAutoCal.IsEnabled = false;
                _runner?.StartGuidedCalibration();
            }
            else
            {
                SetStatus("¡Listo para rodar! Tus botones ya funcionan. Pruébalos: el «último botón» de abajo reacciona.", error: false, success: true);
            }
        }
        else
        {
            _operational = false;
            deck.SetPowered(false);
            btnRide.IsEnabled = true;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnAutoCal.IsEnabled = false;
            SetChipsEnabled(false);
            AppState.Current.Status = MandoStatus.Disconnected;
        }
    }

    /// <summary>Marca como «listo» los botones que ya funcionan sin calibrar: el perfil por defecto del
    /// Click V2 (+/−) y los que el usuario calibró en sesiones anteriores (guardados).</summary>
    private void MarkPrecalibratedChips()
    {
        foreach (var id in _chips.Keys)
        {
            // En modo V1/andriuz los 10 botones del Click V2 son deterministas (bitmask), sin calibrar.
            bool v1Known = _v1Mode;
            bool defaulted = id is "plus" or "minus";
            bool saved = _settings.ButtonMap.ContainsValue(id);
            if (!v1Known && !defaulted && !saved) continue;
            _chips[id].Status.Text = saved ? "✓ listo (guardado)" : "✓ listo (sin calibrar)";
            _chips[id].Status.Foreground = B("SuccessBrush");
        }
    }

    // ── Calibración ─────────────────────────────────────────────────────────
    private void AutoCal_Click(object sender, RoutedEventArgs e)
    {
        // Durante la calibración guiada, este botón actúa como «saltar este botón».
        if (_runner?.IsGuiding == true)
        {
            _runner.SkipCurrentCalibration();
            return;
        }
        SetChipsEnabled(false);
        btnAutoCal.IsEnabled = false;
        _runner?.StartGuidedCalibration();
    }

    private void CalChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionId)
        {
            SetChipsEnabled(false);
            btnAutoCal.IsEnabled = false;
            _runner?.StartCalibration(actionId);
        }
    }

    private void OnCalibrationStepStarted(string actionId)
    {
        foreach (var id in _chips.Keys) HighlightChip(id, id == actionId);
        if (_chips.TryGetValue(actionId, out var chip)) chip.Status.Text = "pulsa el botón ahora…";

        // En guiada, ofrecer saltar el botón actual (para mandos con menos botones que el preset).
        if (_runner?.IsGuiding == true)
        {
            btnAutoCal.Content = "⏭ Saltar este botón";
            btnAutoCal.IsEnabled = true;
        }
    }

    private void OnCalibrationFinished(string action, string signature, bool ok)
    {
        HighlightChip(action, false);
        string label = _activePreset.Actions.FirstOrDefault(a => a.Id == action)?.Label ?? action;
        // Persistir el mapeo aprendido: calibrar una vez, válido para siempre (no por sesión).
        if (ok && !string.IsNullOrEmpty(signature))
        {
            foreach (var k in _settings.ButtonMap.Where(p => p.Value == action).Select(p => p.Key).ToList())
                _settings.ButtonMap.Remove(k);
            _settings.ButtonMap[signature] = action;
            _settings.Save();
        }

        if (_chips.TryGetValue(action, out var chip))
        {
            if (ok)
            {
                string shortSig = signature.Length > 14 ? signature[..14] + "…" : signature;
                chip.Status.Text = $"✅ calibrado · firma {shortSig} (guardado)";
                chip.Status.Foreground = B("SuccessBrush");
            }
            else
            {
                chip.Status.Text = "no se detectó — reintenta";
                chip.Status.Foreground = B("DangerBrush");
            }
        }
        AppendLog(ok
            ? $"✅ Botón «{label}» calibrado."
            : $"No detecté un botón claro para «{label}». Reintenta y aprieta firme varias veces.",
            error: !ok);

        // Si no estamos en una secuencia guiada, re-habilitar para seguir calibrando a mano.
        if (_runner?.IsGuiding != true)
        {
            SetChipsEnabled(true);
            btnAutoCal.IsEnabled = true;
        }
    }

    private void OnGuidedCalibrationFinished()
    {
        SetChipsEnabled(true);
        btnAutoCal.Content = "✨ Calibración automática";
        btnAutoCal.IsEnabled = true;
        int done = _chips.Values.Count(c => c.Status.Text.StartsWith("✅"));
        SetStatus($"Calibración automática terminada ({done} botón(es) listos). ¡A rodar!", error: false, success: true);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in spLog.Children)
            if (child is TextBlock tb) sb.AppendLine(tb.Text);
        try { Clipboard.SetText(sb.ToString()); SetStatus("Registro copiado al portapapeles.", error: false, success: true); }
        catch { SetStatus("No pude copiar el registro.", error: true); }
    }

    // ── Lanzador MyWhoosh ─────────────────────────────────────────────────────
    private void BrowseMyWhoosh_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elige el ejecutable de MyWhoosh",
            Filter = "Aplicaciones (*.exe)|*.exe|Todos los archivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            txtMyWhooshPath.Text = dlg.FileName;
            PersistSettings();
        }
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
