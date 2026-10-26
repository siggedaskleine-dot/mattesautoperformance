using System.Windows;
using MattesAutoPerformance.Services;

namespace MattesAutoPerformance.Views;

public partial class MainWindow : Window
{
    private readonly HardwareService _hardware = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Läser in din hårdvara…";

        try
        {
            // WMI-anrop kan ta några sekunder, så de körs utanför UI-tråden.
            Sections.ItemsSource = await Task.Run(_hardware.ReadAll);
            StatusText.Text = $"Senast inläst {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Något gick fel: " + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }
}
