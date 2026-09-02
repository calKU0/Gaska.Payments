using Gaska.Payments.Application.Posting;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Application;
using Gaska.Payments.Erp;
using Gaska.Payments.Service;
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

// Serilog is configured entirely from the "Serilog" section of appsettings.json, so the sinks
// and levels can be changed on a deployed machine without rebuilding. The bootstrap logger set
// up here also catches failures that happen before the host is built.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Services.AddSerilog();

builder.Services.AddWindowsService(options => options.ServiceName = "Gaska.Payments.Service");
builder.Services.AddAutomaticSettlement(builder.Configuration);

builder.Services.AddOptions<XlOptions>()
    .Bind(builder.Configuration.GetSection(XlOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Brak Xl:Database.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Operator), "Brak Xl:Operator.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => new PostingRepository(
    sp.GetRequiredService<IOptions<ErpOptions>>().Value.ConnectionString));

builder.Services.AddSingleton<ErpPostingService>();
builder.Services.AddHostedService<PaymentCycleWorker>();

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
