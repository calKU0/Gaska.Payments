using Gaska.Payments.Application.Posting;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Application;
using Gaska.Payments.Erp;
using Gaska.Payments.Service;
using Gaska.Payments.Service.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// A Windows service starts with System32 as its working directory. Serilog resolves relative
// sink paths against it, so the log would land in a system folder - anchor it to the application
// directory before anything reads configuration.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // A Windows service starts with System32 as its working directory, so configuration is read
    // from the application directory instead: running as a service and from the console then
    // behave identically.
    ContentRootPath = AppContext.BaseDirectory,
});

// Serilog's own failures - a mail server refusing the password, Seq unreachable - are swallowed
// by design and would otherwise be invisible. This puts them in a file of their own.
LoggingSetup.RedirectSelfLog("logs/serilog-selflog.txt");

// The console, file and Seq sinks come entirely from the "Serilog" section of appsettings.json,
// so they can be changed on a deployed machine without rebuilding. E-mail alerts are added on
// top, from "SerilogEmail" - see EmailAlertOptions for why that one cannot live in the array.
var alerts = EmailAlertOptions.Read(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    // Which machine an event came from cannot be written in configuration, and in Seq - where the
    // service, the test installation and whatever runs next all arrive together - it is the first
    // thing worth filtering on.
    .Enrich.WithProperty("Machine", Environment.MachineName)
    .WithEmailAlerts(alerts)
    .CreateLogger();

builder.Services.AddSerilog();

if (alerts.IsConfigured)
{
    Log.Information(
        "Alerts from {Level} upwards go to {Recipients} through {Host}:{Port}, at most {Batch} to a "
        + "message and at most one message every {Period} minutes.",
        alerts.Level, string.Join(", ", alerts.Recipients), alerts.MailServer, alerts.Port,
        alerts.BatchPostingLimit, alerts.BatchPostingPeriodMinutes);
}
else
{
    // Loud on purpose: a service nobody is alerted about looks exactly like a service with
    // nothing to report.
    Log.Warning("E-mail alerts are switched off - {Reason}.", alerts.DisabledReason);
}

builder.Services.AddWindowsService(options => options.ServiceName = "Gaska.Payments.Service");
builder.Services.AddAutomaticSettlement(builder.Configuration);

builder.Services.AddOptions<XlOptions>()
    .Bind(builder.Configuration.GetSection(XlOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Brak Xl:Database.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Operator), "Brak Xl:Operator.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => new PostingRepository(
    sp.GetRequiredService<IOptions<ErpOptions>>().Value.ConnectionString));

// One thread and one session for the whole life of the service, and every XL API call goes
// through it: the native library binds to the thread that first touches it and faults on every
// other. Signed in at start-up, signed out when the service stops.
builder.Services.AddSingleton<XlSessionHost>();
builder.Services.AddSingleton<ErpPostingService>();
builder.Services.AddHostedService<PaymentCycleWorker>();
builder.Services.AddHostedService<SettlementAdvisorWorker>();

try
{
    await builder.Build().RunAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Serwis zakończył się nieobsłużonym błędem.");
    throw;
}
finally
{
    // Without this the file sink can lose whatever is still buffered when the process exits.
    await Log.CloseAndFlushAsync();
}
