using System.Collections.ObjectModel;
using System.Windows;
using MattesAutoPerformance.Models;
using MattesAutoPerformance.Services;

namespace MattesAutoPerformance.Views;

public partial class MainWindow : Window
{
    private readonly HardwareService _hardware = new();
    private readonly HealthCheckService _health = new();
    private readonly ObservableCollection<HealthResult> _healthResults = new();

    public MainWindow()
    {
        InitializeComponent();
        HealthResults.ItemsSource = _healthResults;
        if (Elevation.IsAdministrator)
        {
            AdminButton.Visibility = Visibility.Collapsed;
            Title += " (administratör)";
        }
        Loaded += async (_, _) => await LoadAsync();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Händelsen kan komma innan alla kontroller finns, medan fönstret byggs upp.
        if (HealthPage is null) return;
        var health = HealthTab.IsChecked == true;
        OverviewPage.Visibility = OverviewToolbar.Visibility = health ? Visibility.Collapsed : Visibility.Visible;
        HealthPage.Visibility = HealthToolbar.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
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

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Elevation.RestartAsAdministrator())
            HealthStatusText.Text = "Appen startades inte som administratör.";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        HealthPlaceholder.Visibility = Visibility.Collapsed;
        HealthStatusText.Text = "Skannar datorn…";
        _healthResults.Clear();

        var checks = _health.Checks;
        foreach (var check in checks)
            _healthResults.Add(HealthResult.Running(check.Title, check.Icon));

        // Kontrollerna körs samtidigt och visas var för sig när de blir klara.
        await Task.WhenAll(checks.Select(async (check, index) =>
        {
            var result = await Task.Run(() => HealthCheckService.RunSafe(check));
            _healthResults[index] = result;
        }));

        var problems = _healthResults.Count(r => r.Status == HealthStatus.Problem);
        var warnings = _healthResults.Count(r => r.Status == HealthStatus.Warning);
        HealthStatusText.Text = (problems, warnings) switch
        {
            (0, 0) => $"Allt ser bra ut! Skannat {DateTime.Now:HH:mm}.",
            _ => $"Hittade {problems} problem och {warnings} sak(er) som bör åtgärdas. Skannat {DateTime.Now:HH:mm}.",
        };
        ScanButton.IsEnabled = true;
    }
}
