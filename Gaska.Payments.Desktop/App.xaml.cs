using System.Globalization;
using System.IO;
using System.Windows.Markup;
using System.Windows;
using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Desktop.ViewModels;
using Gaska.Payments.Desktop.Views;
using Gaska.Payments.Erp;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Serilog;

namespace Gaska.Payments.Desktop;

// System.Windows.Application is spelled out: unqualified, "Application" would bind to our own
// Gaska.Payments.Application namespace, which sits one level up from this one.
public partial class App : System.Windows.Application
{
    private XlWorker? _worker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without this WPF formats numbers and dates the American way regardless of the system
        // settings: amounts came out as 7,471.94 instead of 7 471,94.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // A shortcut can start the application in any folder; Serilog resolves relative sink
        // paths against the working directory, so anchor it to the application directory.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var logger = CreateLogger(configuration);

        var connectionString = configuration.GetSection("Erp")["ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show("Brak Erp:ConnectionString w appsettings.json.", "Konfiguracja",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var options = new XlOptions();
        configuration.GetSection(XlOptions.SectionName).Bind(options);

        // The registers come from configuration so the application and the service can be told
        // about a new account in one place, and so a register with nothing on it today is still
        // on the filter.
        var registers = new RegisterSettings();
        configuration.GetSection(RegisterSettings.SectionName).Bind(registers);

        // The archive of statements and courier reports, shared with the service - the button on
        // an entry opens whatever the service filed there.
        var documents = new SourceDocuments(configuration.GetSection("Archive")["Directory"] ?? string.Empty);

        if (registers.All.Count == 0)
        {
            MessageBox.Show(
                "Sekcja Settlement w appsettings.json nie wymienia żadnego rejestru - "
                + "nie ma czego pokazać.",
                "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Signing in through the Comarch ERP XL window: we neither see nor store the password,
        // and ERP itself records who created a settlement in CDN.Rozliczenia.R2_OpeNumerRL.
        _worker = new XlWorker(options, logger);

        var store = new DecisionStore(connectionString);

        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not prepare the proposals table.");
            MessageBox.Show($"Nie udało się przygotować tabeli propozycji: {ex.Message}",
                "Baza danych", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var viewModel = new MainViewModel(
            new QueueRepository(connectionString, registers),
            store,
            _worker,
            registers,
            documents,
            DescribeConnection(connectionString),
            logger);

        viewModel.AskForBank = prompt => BankWindow.Ask(MainWindow, prompt);

        viewModel.Confirm = question => MessageBox.Show(
            question, "Potwierdzenie", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        viewModel.LoginFailed += message => Dispatcher.Invoke(() =>
        {
            MessageBox.Show(message, "Comarch ERP XL", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        });

        logger.LogInformation(
            "Application started: version {Version}, database {Database}, archive {Archive}, " +
            "registers {Registers}.",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            DescribeConnection(connectionString),
            documents.IsConfigured ? "configured" : "not configured",
            string.Join(", ", registers.All));

        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        _ = viewModel.StartAsync();
    }

    /// <summary>
    /// Serilog writing next to the application. The settlement engine reports through
    /// <see cref="ILogger"/> and an accountant has no console to look at, so it goes to disk.
    /// Sinks and levels come from the "Serilog" section of appsettings.json.
    /// </summary>
    private static ILogger CreateLogger(IConfiguration configuration)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        return new SerilogLoggerFactory(Log.Logger).CreateLogger("RozliczaniePrzelewow");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _worker?.Dispose();

        // Without this the file sink can lose whatever is still buffered when the process exits.
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>Database and Windows user for the header - the API does not expose the signed-in operator.</summary>
    private static string DescribeConnection(string connectionString) =>
        $"{new SqlConnectionStringBuilder(connectionString).InitialCatalog} · {Environment.UserName}";
}
