using System.Windows;

namespace OneStopMidi.App.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) => ValueBox.SelectAll();
    }

    public string Value => ValueBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }

    /// <summary>Shows the dialog; returns null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var d = new InputDialog(title, prompt, initial) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }
}
