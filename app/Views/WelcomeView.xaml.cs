using System;
using System.Windows;
using System.Windows.Controls;

namespace Violeta.Views;

public partial class WelcomeView : UserControl
{
    /// <summary>Pide a la ventana principal que navegue a otra vista ("connect", "tutorial"…).</summary>
    public event Action<string>? NavigateRequested;

    public WelcomeView()
    {
        InitializeComponent();
    }

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("connect");
    private void How_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("tutorial");
}
