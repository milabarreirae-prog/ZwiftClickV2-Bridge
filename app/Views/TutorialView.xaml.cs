using System.Windows.Controls;
using Violeta.Models;

namespace Violeta.Views;

public partial class TutorialView : UserControl
{
    public TutorialView()
    {
        InitializeComponent();
        StepsList.ItemsSource = TutorialSteps.All;
    }
}
