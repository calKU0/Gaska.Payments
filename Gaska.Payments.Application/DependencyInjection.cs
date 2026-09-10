using Gaska.Payments.Application.Couriers;
using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Domain.Couriers;
using Gaska.Payments.Domain.Matching;
using Gaska.Payments.Domain;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Bank;
using Gaska.Payments.Integrations.Couriers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Gaska.Payments.Application.Bank;

namespace Gaska.Payments.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers everything the settlement cycle needs: the BNP client, reads from ERP, the
    /// matching engine and the proposal store.
    /// </summary>
    public static IServiceCollection AddAutomaticSettlement(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ErpOptions>()
            .Bind(configuration.GetSection(ErpOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Brak Erp:ConnectionString.")
            .ValidateOnStart();

        services.AddOptions<BnpConnectionOptions>()
            .Bind(configuration.GetSection(BnpConnectionOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.CertificatePath), "Brak Bnp:CertificatePath.")
            .ValidateOnStart();

        services.AddOptions<SettlementOptions>()
            .Bind(configuration.GetSection(SettlementOptions.SectionName))
            .Validate(o => o.IntervalMinutes > 0, "Settlement:IntervalMinutes musi być dodatnie.")
            .Validate(o => o.LookbackDays > 0, "Settlement:LookbackDays musi być dodatnie.")
            .Validate(o => o.Registers.Count > 0, "Nie wskazano żadnego rejestru w Settlement:Registers.")
            .ValidateOnStart();

        services.AddOptions<ArchiveOptions>()
            .Bind(configuration.GetSection(ArchiveOptions.SectionName));

        // The mailbox and the notifications are bound in their own right, not only as part of
        // CodOptions: the classes that use them live in Integrations and have no business knowing
        // which register the collections are posted to. Left unbound they would silently get an
        // empty host and the service would skip the mail without failing.
        services.AddOptions<CodMailboxOptions>()
            .Bind(configuration.GetSection($"{CodOptions.SectionName}:{nameof(CodOptions.Mailbox)}"));

        services.AddOptions<CodNotificationOptions>()
            .Bind(configuration.GetSection($"{CodOptions.SectionName}:{nameof(CodOptions.Notifications)}"));

        services.AddOptions<CodOptions>()
            .Bind(configuration.GetSection(CodOptions.SectionName))
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Register),
                "Brak Cod:Register – nie wiadomo, do którego rejestru trafiają pobrania.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.OperationSymbol),
                "Brak Cod:OperationSymbol – nie wiadomo, jaką operacją kasową księgować pobrania.")
            .ValidateOnStart();

        services.AddSingleton(sp => new ErpReadRepository(ErpConnectionString(sp)));
        services.AddSingleton(sp => new PaymentStore(ErpConnectionString(sp)));
        services.AddSingleton(sp => new BnpStatementClient(
            sp.GetRequiredService<IOptions<BnpConnectionOptions>>().Value,
            sp.GetRequiredService<ILogger<BnpStatementClient>>()));

        services.AddSingleton(sp => new PaymentMatcher(new MatchingOptions
        {
            OwnNip = sp.GetRequiredService<IOptions<ErpOptions>>().Value.OwnNip,
        }));

        services.AddSingleton<BankSettlementPipeline>();
        services.AddSingleton<StatementArchive>();

        // Cash on delivery: the mailbox, the readers of the four courier formats, and the
        // pipeline that turns their reports into entries waiting to be posted.
        services.AddSingleton<ICodReportReader, GlsCsvReader>();
        services.AddSingleton<ICodReportReader, HellmannXlsxReader>();
        services.AddSingleton<ICodReportReader, DieraXlsxReader>();
        services.AddSingleton<ICodReportReader, DpdXlsReader>();
        services.AddSingleton<ICodReportReader, FedexReportReader>();
        services.AddSingleton<CodMailbox>();
        services.AddSingleton<CodStore>();
        services.AddSingleton<CodNotifier>();
        services.AddSingleton<ShipmentReader>();
        services.AddSingleton<CodPipeline>();

        return services;
    }

    private static string ErpConnectionString(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<ErpOptions>>().Value.ConnectionString;
}
