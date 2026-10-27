using System.Windows;

namespace OneStopMidi.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(string engineDescription)
    {
        InitializeComponent();
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        TitleText.Text = $"OneStopMidi {version}";
        EngineText.Text = $"Engine: {engineDescription}";
    }
}
