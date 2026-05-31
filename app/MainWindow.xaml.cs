using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Violeta.Interop;
using Violeta.Views;

namespace Violeta;

public partial class MainWindow : Window
{
    private readonly WelcomeView _welcome = new();
    private readonly TutorialView _tutorial = new();
    private readonly ConnectView _connect = new();
    private readonly AboutView _about = new();

    // Paso vertical entre botones de navegacion (alto 46 + margen inferior 8).
    private const double NavStep = 54;

    // Glifos de la barra de titulo (Segoe Fluent Icons / MDL2): maximizar (E922) y restaurar (E923).
    private static readonly string GlyphMaximize = char.ConvertFromUtf32(0xE922);
    private static readonly string GlyphRestore = char.ConvertFromUtf32(0xE923);

    public MainWindow()
    {
        InitializeComponent();

        // Acabado nativo de Windows 11: barra oscura, esquinas redondeadas, maximizado correcto.
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyWindows11(this);
            WindowEffects.EnableMaximizeFix(this);
        };
        StateChanged += (_, _) =>
            btnMax.Content = WindowState == WindowState.Maximized ? GlyphRestore : GlyphMaximize;

        _welcome.NavigateRequested += Navigate;
        _welcome.RideRequested += () => { Navigate("connect"); _connect.StartRide(); };
        Navigate("home");

        Closing += (_, _) => _connect.PersistSettings();
    }

    // -- Barra de titulo -------------------------------------------------------
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // -- Navegacion ------------------------------------------------------------
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
        {
            string target = b.Name switch
            {
                nameof(btnTutorial) => "tutorial",
                nameof(btnConnect) => "connect",
                nameof(btnAbout) => "about",
                _ => "home"
            };
            Navigate(target);
        }
    }

    /// <summary>Cambia la vista activa, desliza el indicador y funde la entrada del contenido.</summary>
    public void Navigate(string target)
    {
        Host.Content = target switch
        {
            "tutorial" => _tutorial,
            "connect" => _connect,
            "about" => _about,
            _ => _welcome
        };

        if (target is not ("tutorial" or "connect" or "about"))
            _welcome.Refresh();

        btnHome.Tag = target == "home" ? "active" : null;
        btnTutorial.Tag = target == "tutorial" ? "active" : null;
        btnConnect.Tag = target == "connect" ? "active" : null;
        btnAbout.Tag = target == "about" ? "active" : null;

        int index = target switch { "tutorial" => 1, "connect" => 2, "about" => 3, _ => 0 };
        MoveIndicator(index);
        PlayEnterTransition();
    }

    private void MoveIndicator(int index)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(300));

        navIndicator.BeginAnimation(MarginProperty,
            new ThicknessAnimation(new Thickness(0, index * NavStep, 0, 0), dur) { EasingFunction = ease });
        navAccent.BeginAnimation(MarginProperty,
            new ThicknessAnimation(new Thickness(0, index * NavStep + 12, 0, 0), dur) { EasingFunction = ease });
    }

    private void PlayEnterTransition()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Host.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = ease });
        hostShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = ease });
    }
}
