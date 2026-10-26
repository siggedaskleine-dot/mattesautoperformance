using System.Windows;
using System.Windows.Controls;
using MattesAutoPerformance.Services;

namespace MattesAutoPerformance.Views;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
        Entries.ItemsSource = StartupService.GetAll();
    }

    /// <summary>Sant om användaren ändrade något, så att hälsokontrollen kan uppdateras.</summary>
    public bool Changed { get; private set; }

    private void Entry_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var entry = (StartupEntry)box.Tag;
        var enable = box.IsChecked == true;
        try
        {
            StartupService.SetEnabled(entry, enable);
            Changed = true;
            StatusText.Text = $"{entry.Name} {(enable ? "startar" : "startar inte längre")} med Windows.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            box.IsChecked = !enable;
            StatusText.Text = $"Kunde inte ändra {entry.Name}: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
