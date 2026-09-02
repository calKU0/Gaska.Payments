using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using BNPService;
using Gaska.Payments.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Gaska.Payments.Integrations.Bank;

/// <summary>
/// Downloads the operation history from BNP Paribas GOconnect Biznes (the CDC service, message
/// <c>GetAccountReport</c> returning camt.052) and turns it into payments to be settled.
/// </summary>
/// <remarks>
/// <c>GetAccountReport</c> takes a date range (FrDt/ToDt), unlike <c>GetStatement</c>, which works
/// on a single day - which is why it is the one used here.
/// </remarks>
public sealed class BnpStatementClient(BnpConnectionOptions options, ILogger<BnpStatementClient> logger)
{
    /// <summary>
    /// The communication certificate, loaded once for the life of the process.
    /// </summary>
    /// <remarks>
    /// It has to be a single load. <c>PersistKeySet</c> writes the private key into the machine's
    /// key store under a fresh random container name every time, and nothing ever removes those.
    /// Loading per request meant a container for every statement asked for - some fifty an hour,
    /// over a thousand a day - piling up on disk for as long as the service runs.
    /// </remarks>
    private readonly Lazy<X509Certificate2> _certificate =
        new(LoadCertificate(options), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every operation on the statements - credits and debits alike.</summary>
    /// <param name="accounts">
    /// The accounts to query. They come from the ERP bank registers rather than from
    /// configuration, so the list of accounts and the list of registers cannot drift apart.
    /// </param>
    public async Task<IReadOnlyList<BankPayment>> GetOperationsAsync(
        IReadOnlyList<BnpAccount> accounts, DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        var reports = await GetAccountReportsAsync(accounts, from, to, cancellationToken);

        return reports
            .SelectMany(BankPaymentMapper.MapAll)
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .OrderBy(p => p.BookingDate)
            .ToList();
    }

    /// <summary>
    /// The raw camt.052 reports for the given accounts.
    /// </summary>
    /// <remarks>
    /// An account the bank does not serve is skipped and we carry on. GOconnect answers such a
    /// number with error <c>E201</c> - the same one it gives for a malformed number - so the
    /// response alone cannot tell an account outside the agreement from a typo. Either way it is
    /// a matter of configuration, not a reason for the service to stop posting the remaining
    /// accounts for another hour. When every account fails the exception propagates: that is an
    /// outage.
    /// </remarks>
    private async Task<IReadOnlyList<AccountReport11>> GetAccountReportsAsync(
        IReadOnlyList<BnpAccount> accounts, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var certificate = _certificate.Value;
        var results = new List<AccountReport11>();
        var rejected = new List<string>();

        foreach (var account in accounts)
        {
            try
            {
                foreach (var (chunkFrom, chunkTo) in SplitRange(from, to, options.MaxDaysPerRequest))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var reports = await CallAsync(certificate, account, chunkFrom, chunkTo);
                    results.AddRange(reports);
                }
            }
            catch (FaultException<Document2> fault)
            {
                rejected.Add($"{account.Iban} ({DescribeFault(fault)})");
            }
        }

        if (rejected.Count == accounts.Count && accounts.Count > 0)
        {
            throw new InvalidOperationException(
                "Bank odrzucił zapytanie o każdy rachunek: " + string.Join("; ", rejected));
        }

        if (rejected.Count > 0)
        {
            logger.LogWarning(
                "The bank refused {Count} of {Total} accounts - check they are attached to the CDC " +
                "service in the GOconnect agreement: {Accounts}",
                rejected.Count, accounts.Count, string.Join("; ", rejected));
        }

        return results;
    }

    /// <summary>The rejection code and description from the bank's error report.</summary>
    private static string DescribeFault(FaultException<Document2> fault)
    {
        var error = fault.Detail?.ErrRpt?.ErrDesc?.FirstOrDefault();
        return error is null ? fault.Message : $"{error.RuleId}: {error.RuleDesc}";
    }

    private async Task<IReadOnlyList<AccountReport11>> CallAsync(
        X509Certificate2 certificate, BnpAccount account, DateTime from, DateTime to)
    {
        var client = CreateClient(certificate);
        var request = BuildRequest(account.Iban, from, to);

        try
        {
            var response = await client.GetAccountReportAsync(request);

            var reports = response?.GetAccountReportResponse1?.Document?.BkToCstmrAcctRpt?.Rpt;
            return reports ?? [];
        }
        finally
        {
            await CloseAsync(client);
        }
    }

    /// <summary>
    /// The bank statement for one account and one day, as a PDF.
    /// </summary>
    /// <remarks>
    /// <c>GetStatement</c> works on a single day, unlike <c>GetAccountReport</c>, which takes a
    /// range - which is exactly what an archive of daily statements needs. The same call returns
    /// either the PDF or the camt.053 XML depending on <c>StmtFrmt</c>; we ask for the PDF, so the
    /// response arrives as <c>Document9</c> rather than <c>Document8</c>.
    ///
    /// Returns <c>null</c> when the bank has no statement for that day. That is the normal answer
    /// for a weekend or a holiday, and it arrives as a SOAP fault (<c>E502</c>, "wyciag za dany
    /// okres jest niedostepny") rather than as an empty response - verified against the live
    /// service. It is not an error, so it is reported at debug level and nowhere else: the cycle
    /// runs hourly and every weekend day inside the window would otherwise produce a warning on
    /// every pass.
    /// </remarks>
    public async Task<BankStatementPdf?> GetStatementPdfAsync(
        BnpAccount account, DateTime day, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = CreateClient(_certificate.Value);

        try
        {
            var response = await client.GetStatementAsync(BuildStatementRequest(account.Iban, day));

            // The response is a choice: Document9 carries the PDF, Document8 the camt.053 XML.
            if (response?.GetStatementResponse1?.Item is not Document9 document) return null;

            var details = document.Rpt?.RptDtls;
            var error = document.Rpt?.OprlErr;

            if (error is not null)
            {
                logger.LogDebug("No PDF statement for {Iban} on {Day:yyyy-MM-dd}: {Code} {Description}",
                    account.Iban, day, error.Err?.Prtry, error.Desc);
                return null;
            }

            if (details?.RptFile is not { Length: > 0 } content) return null;

            return new BankStatementPdf(details.RptNm ?? string.Empty, content);
        }
        catch (FaultException<Document2> fault)
        {
            logger.LogDebug("No PDF statement for {Iban} on {Day:yyyy-MM-dd}: {Reason}",
                account.Iban, day, DescribeFault(fault));
            return null;
        }
        finally
        {
            await CloseAsync(client);
        }
    }

    private static GetStatementRequestType BuildStatementRequest(string iban, DateTime day) => new()
    {
        Document = new Document7
        {
            GetStmt = new GetStatement
            {
                // The identifier has to be unique for the recipient and free of special characters.
                MsgId = new MessageIdentyfication1
                {
                    Id = $"GST{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}",
                },
                StmtQryDef = new StatementQueryDefinition
                {
                    StmtCrit = new StatementCriteria
                    {
                        NewCrit = new NewCriteria1
                        {
                            SchCrit = new SearchCriteria1
                            {
                                AcctId = new AccountIdentification1
                                {
                                    EQ = new AccountIdentification3Choice1
                                    {
                                        ItemElementName = ItemChoiceType74.IBAN,
                                        Item = iban,
                                    },
                                },
                                StmtValDt = new StatementValueSearch
                                {
                                    DtSch = new DatePeriodDetails4 { Item = day.Date },
                                },
                                StmtFrmt = StatementFormat.PDF,
                                StmtFrmtSpecified = true,
                                Lang = ISO2LangCode.PL,
                                LangSpecified = true,
                            },
                        },
                    },
                },
            },
        },
    };

    /// <summary>
    /// A client for one call. The service is stateful per call and the certificate is attached to
    /// the channel, so a client is not shared between operations.
    /// </summary>
    private cdcws00101PortClient CreateClient(X509Certificate2 certificate)
    {
        var binding = new BasicHttpBinding
        {
            MaxReceivedMessageSize = 200_000_000,
            MaxBufferSize = 200_000_000,
            ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max,
            OpenTimeout = TimeSpan.FromMinutes(2),
            SendTimeout = TimeSpan.FromMinutes(5),
            ReceiveTimeout = TimeSpan.FromMinutes(5),
            Security =
            {
                Mode = BasicHttpSecurityMode.Transport,
                Transport = { ClientCredentialType = HttpClientCredentialType.Certificate },
            },
        };

        var client = new cdcws00101PortClient(binding, new EndpointAddress(options.Endpoint));
        client.ClientCredentials.ClientCertificate.Certificate = certificate;
        return client;
    }

    private static async Task CloseAsync(cdcws00101PortClient client)
    {
        try { await client.CloseAsync(); }
        catch (CommunicationException) { client.Abort(); }
        catch (TimeoutException) { client.Abort(); }
    }

    private static GetAccountReportRequestType BuildRequest(string iban, DateTime from, DateTime to) => new()
    {
        Document = new Document3
        {
            GetAcctRpt = new GetAccountReport
            {
                // The identifier has to be unique for the recipient and free of special characters.
                MsgId = new MessageIdentyfication
                {
                    Id = $"GAR{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}",
                },
                AcctRptQryDef = new AccountReportQueryDefinition
                {
                    AcctRptCrit = new AccountReportCriteria
                    {
                        NewCrit = new NewCriteria
                        {
                            SchCrit = new SearchCriteria
                            {
                                AcctId = new AccountIdentification
                                {
                                    EQ = new AccountIdentification3Choice
                                    {
                                        ItemElementName = ItemChoiceType8.IBAN,
                                        Item = iban,
                                    },
                                },
                                AcctRptValDt = new AccountReportValueSearch
                                {
                                    DtSch = new DatePeriodDetails2
                                    {
                                        FrDt = from.Date,
                                        ToDt = to.Date,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    private static Func<X509Certificate2> LoadCertificate(BnpConnectionOptions options) => () =>
    {
        // As a Windows service the process starts with System32 as its working directory, so a
        // relative path is resolved against the application directory rather than the current one.
        var path = Path.IsPathRooted(options.CertificatePath)
            ? options.CertificatePath
            : Path.Combine(AppContext.BaseDirectory, options.CertificatePath);

        if (string.IsNullOrWhiteSpace(options.CertificatePath) || !File.Exists(path))
        {
            throw new FileNotFoundException("Nie znaleziono certyfikatu komunikacyjnego do GOconnect.", path);
        }

        // Schannel needs the key written to a store - with EphemeralKeySet, authenticating
        // with a client certificate fails with SEC_E_UNKNOWN_CREDENTIALS (0x8009030D).
        // MachineKeySet works only with administrator rights, hence the fall back to UserKeySet.
        X509KeyStorageFlags[] attempts =
        [
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable,
        ];

        Exception? last = null;
        foreach (var flags in attempts)
        {
            try
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    path, options.CertificatePassword, flags);

                if (certificate.HasPrivateKey) return certificate;
                last = new InvalidOperationException($"Certyfikat wczytany z flagami {flags} nie ma klucza prywatnego.");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            "Nie udało się wczytać certyfikatu komunikacyjnego z kluczem prywatnym.", last);
    };

    private static IEnumerable<(DateTime From, DateTime To)> SplitRange(DateTime from, DateTime to, int maxDays)
    {
        var cursor = from.Date;
        while (cursor <= to.Date)
        {
            var chunkEnd = cursor.AddDays(maxDays - 1);
            if (chunkEnd > to.Date) chunkEnd = to.Date;
            yield return (cursor, chunkEnd);
            cursor = chunkEnd.AddDays(1);
        }
    }
}
