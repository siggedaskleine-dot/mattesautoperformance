using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MattesAutoPerformance.Models;
using MattesAutoPerformance.Services;

namespace MattesAutoPerformance.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly HardwareService _hardware = new();
    private readonly HealthCheckService _health = new();
    private readonly ObservableCollection<HealthResult> _healthResults = new();
    private readonly SystemMonitor _monitor = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<HardwareSection> _sections = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        HealthResults.ItemsSource = _healthResults;

        _monitorTimer.Tick += (_, _) => UpdateMonitor();
        _monitorTimer.Start();
        UpdateMonitor();

        Loaded += async (_, _) => await LoadAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Falskt medan en skanning eller åtgärd pågår, så att två saker inte körs samtidigt.</summary>
    public bool CanRunFixes => !_busy;

    private bool Busy
    {
        set
        {
            _busy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRunFixes)));
            FixAllButton.IsEnabled = !value && FixAllCandidates().Count > 0;
            LogBusy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateMonitor()
    {
        var cpu = _monitor.CpuPercent();
        var ram = SystemMonitor.MemoryPercent();
        CpuBar.Value = cpu;
        RamBar.Value = ram;
        CpuText.Text = $"Processor {cpu:0} %";
        RamText.Text = $"Minne {ram:0} %";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Händelsen kan komma innan alla kontroller finns, medan fönstret byggs upp.
        if (HealthPage is null) return;
        var health = HealthTab.IsChecked == true;
        OverviewPage.Visibility = OverviewToolbar.Visibility = health ? Visibility.Collapsed : Visibility.Visible;
        HealthPage.Visibility = HealthToolbar.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = health && LogBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Översikt ------------------------------------------------------------------------

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Läser in din hårdvara…";

        try
        {
            // WMI-anrop kan ta några sekunder, så de körs utanför UI-tråden.
            _sections = await Task.Run(_hardware.ReadAll);
            Sections.ItemsSource = _sections;
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

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Spara rapport",
            FileName = $"Datorrapport {Environment.MachineName} {DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Textfil (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllText(dialog.FileName, ReportService.Build(_sections, _healthResults));
        StatusText.Text = "Rapporten sparades: " + dialog.FileName;
    }

    // ---- Hälsokontroll -------------------------------------------------------------------

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        Busy = true;
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
        Busy = false;
    }

    private async void FixButton_Click(object sender, RoutedEventArgs e)
    {
        var result = (HealthResult)((Button)sender).Tag;
        switch (result.Fix)
        {
            case FixKind.ManageStartup:
                var window = new StartupWindow { Owner = this };
                window.ShowDialog();
                if (window.Changed) await ScanAsync();
                break;
            case FixKind.Restart:
                AskRestart("Vill du starta om datorn nu? Spara det du arbetar med först.");
                break;
            case { } kind:
                await RunFixesAsync([kind], $"{result.FixLabel}?\n\n{result.FixHint}");
                break;
        }
    }

    private async void FixAll_Click(object sender, RoutedEventArgs e)
    {
        var fixes = FixAllCandidates();

        if (fixes.Count == 0)
        {
            MessageBox.Show(this, "Det finns inget som behöver fixas just nu.", "Mattes AutoPerformance",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var list = string.Join("\n", _healthResults.Where(r => r.Fix is { } f && fixes.Contains(f))
            .Select(r => "• " + r.FixLabel));
        await RunFixesAsync(fixes, $"Det här kommer att göras:\n\n{list}\n\nDet kan ta en stund. Fortsätta?");
    }

    /// <summary>Åtgärder för kontroller som hittade något och som "Fixa allt" får köra.</summary>
    private List<FixKind> FixAllCandidates() => _healthResults
        .Where(r => r.Fix?.IncludedInFixAll() == true && r.Status is HealthStatus.Warning or HealthStatus.Problem)
        .Select(r => r.Fix!.Value)
        .Distinct()
        .ToList();

    private async Task RunFixesAsync(IReadOnlyList<FixKind> fixes, string question)
    {
        if (MessageBox.Show(this, question, "Mattes AutoPerformance", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Busy = true;
        LogPanel.Visibility = Visibility.Visible;
        var log = new Progress<string>(AppendLog);
        var restart = false;

        try
        {
            if (fixes.Any(f => f.NeedsRestorePoint()) && !await Task.Run(() => FixService.CreateRestorePoint(log)))
            {
                var answer = MessageBox.Show(this,
                    "Det gick inte att skapa en återställningspunkt, så ändringarna går inte att backa automatiskt.\n\nFortsätta ändå?",
                    "Mattes AutoPerformance", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    AppendLog("Avbrutet.");
                    return;
                }
            }

            foreach (var fix in fixes)
            {
                var outcome = await Task.Run(() =>
                {
                    try
                    {
                        return FixService.Run(fix, log);
                    }
                    catch (Exception ex)
                    {
                        ((IProgress<string>)log).Report("Fel: " + ex.Message);
                        return new FixOutcome(false);
                    }
                });
                restart |= outcome.RestartRequired;
            }
        }
        finally
        {
            Busy = false;
        }

        AppendLog("Kör en ny skanning…");
        await ScanAsync();

        if (restart)
            AskRestart("Klart! Datorn behöver startas om för att ändringarna ska börja gälla. Starta om nu?");
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        LogBox.ScrollToEnd();
    }

    private void AskRestart(string question)
    {
        if (MessageBox.Show(this, question, "Mattes AutoPerformance", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"Mattes AutoPerformance startar om datorn.\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
