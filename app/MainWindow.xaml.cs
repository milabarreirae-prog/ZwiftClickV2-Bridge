using System.Windows;
using System.Windows.Controls;
using Violeta.Views;

namespace Violeta;

public partial class MainWindow : Window
{
    private readonly WelcomeView _welcome = new();
    private readonly TutorialView _tutorial = new();
    private readonly ConnectView _connect = new();
    private readonly AboutView _about = new();

    public MainWindow()
    {
        InitializeComponent();
        _welcome.NavigateRequested += Navigate;
        Navigate("home");
    }

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

    /// <summary>Cambia la vista activa y marca el botón de navegación correspondiente.</summary>
    public void Navigate(string target)
    {
        Host.Content = target switch
        {
            "tutorial" => _tutorial,
            "connect" => _connect,
            "about" => _about,
            _ => _welcome
        };

        btnHome.Tag = target == "home" ? "active" : null;
        btnTutorial.Tag = target == "tutorial" ? "active" : null;
        btnConnect.Tag = target == "connect" ? "active" : null;
        btnAbout.Tag = target == "about" ? "active" : null;
    }
}
