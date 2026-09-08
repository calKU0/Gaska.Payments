using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Application.Settlement;

/// <summary>
/// Creates the service's own tables, in a schema of their own.
/// </summary>
/// <remarks>
/// Everything lives in <c>pay</c> rather than in <c>dbo</c>. The database is Comarch ERP XL's, and
/// <c>dbo</c> there is a crowded place: a schema of our own means no name can ever collide with
/// theirs, it is obvious at a glance which tables are ours, and rights can be granted over the lot
/// in one statement. Nothing is written to <c>CDN.*</c> except the two exceptions the XL API cannot
/// cover, each documented where it happens.
///
/// The script is idempotent and runs at service startup, which reduces deployment to copying files
/// - there is no separate migration step.
/// </remarks>
public static class PaymentSchema
{
    /// <summary>
    /// Creates the schema and moves across the tables an earlier version left in <c>dbo</c>.
    /// </summary>
    /// <remarks>
    /// The tables carried the <c>Bnp</c> prefix when the bank was all this did; they now hold
    /// courier collections too, and will hold card settlements. Renaming them is a move, not a
    /// re-creation - there is live data in them - so the rows come along and only the constraint
    /// names keep their old spelling, which nothing depends on.
    /// </remarks>
    private const string Migration = """
        IF SCHEMA_ID('pay') IS NULL EXEC ('CREATE SCHEMA pay');

        IF OBJECT_ID('dbo.BnpSettlementRun', 'U') IS NOT NULL AND OBJECT_ID('pay.Run', 'U') IS NULL
        BEGIN
            ALTER SCHEMA pay TRANSFER dbo.BnpSettlementRun;
            EXEC sp_rename 'pay.BnpSettlementRun', 'Run';
        END;

        IF OBJECT_ID('dbo.BnpSettlementPayment', 'U') IS NOT NULL AND OBJECT_ID('pay.Payment', 'U') IS NULL
        BEGIN
            ALTER SCHEMA pay TRANSFER dbo.BnpSettlementPayment;
            EXEC sp_rename 'pay.BnpSettlementPayment', 'Payment';
        END;

        IF OBJECT_ID('dbo.BnpSettlementAllocation', 'U') IS NOT NULL AND OBJECT_ID('pay.Allocation', 'U') IS NULL
        BEGIN
            ALTER SCHEMA pay TRANSFER dbo.BnpSettlementAllocation;
            EXEC sp_rename 'pay.BnpSettlementAllocation', 'Allocation';
        END;

        IF OBJECT_ID('dbo.BnpCodReport', 'U') IS NOT NULL AND OBJECT_ID('pay.CourierReport', 'U') IS NULL
        BEGIN
            ALTER SCHEMA pay TRANSFER dbo.BnpCodReport;
            EXEC sp_rename 'pay.BnpCodReport', 'CourierReport';
        END;

        IF OBJECT_ID('dbo.vw_BnpSettlementQueue', 'V') IS NOT NULL DROP VIEW dbo.vw_BnpSettlementQueue;
        """;

    private const string Script = """
        IF OBJECT_ID('pay.Run', 'U') IS NULL
        BEGIN
            CREATE TABLE pay.Run
            (
                RunId            INT IDENTITY(1,1)   NOT NULL CONSTRAINT PK_BnpSettlementRun PRIMARY KEY,
                StartedAt        DATETIME2(0)        NOT NULL,
                FinishedAt       DATETIME2(0)        NULL,
                PeriodFrom       DATE                NOT NULL,
                PeriodTo         DATE                NOT NULL,
                PaymentsFetched  INT                 NOT NULL CONSTRAINT DF_BnpSettlementRun_Fetched DEFAULT 0,
                PaymentsProposed INT                 NOT NULL CONSTRAINT DF_BnpSettlementRun_Proposed DEFAULT 0,
                Status           VARCHAR(20)         NOT NULL,
                ErrorMessage     NVARCHAR(2000)      NULL
            );
        END;

        IF OBJECT_ID('pay.Payment', 'U') IS NULL
        BEGIN
            CREATE TABLE pay.Payment
            (
                PaymentId         BIGINT         NOT NULL CONSTRAINT PK_BnpSettlementPayment PRIMARY KEY,
                BankExternalId    VARCHAR(64)    NOT NULL,
                CreditedAccount   VARCHAR(34)    NOT NULL,
                BookingDate       DATE           NOT NULL,
                Amount            DECIMAL(19,2)  NOT NULL,
                Currency          CHAR(3)        NOT NULL,
                PayerName         NVARCHAR(140)  NOT NULL,
                PayerAccount      VARCHAR(34)    NOT NULL,
                Description       NVARCHAR(500)  NOT NULL,
                ContractorId      INT            NOT NULL,
                ContractorSource  NVARCHAR(80)   NOT NULL,
                Confidence        VARCHAR(10)    NOT NULL,
                Strategy          VARCHAR(40)    NOT NULL,
                AllocatedAmount   DECIMAL(19,2)  NOT NULL,
                UnallocatedAmount DECIMAL(19,2)  NOT NULL,
                Notes             NVARCHAR(1000) NOT NULL,
                Status            VARCHAR(20)    NOT NULL,
                ErpEntryId        INT            NULL,
                FirstSeenAt       DATETIME2(0)   NOT NULL,
                LastUpdatedAt     DATETIME2(0)   NOT NULL,
                LastRunId         INT            NOT NULL,
                OperatorDocuments NVARCHAR(500)  NULL,
                OperatorNote      NVARCHAR(500)  NULL,
                DecidedAt         DATETIME2(0)   NULL,
                DecidedBy         NVARCHAR(80)   NULL
            );

            CREATE INDEX IX_BnpSettlementPayment_Status
                ON pay.Payment (Status, Confidence) INCLUDE (BookingDate, Amount);

            CREATE INDEX IX_BnpSettlementPayment_BookingDate
                ON pay.Payment (BookingDate);

            CREATE INDEX IX_BnpSettlementPayment_Contractor
                ON pay.Payment (ContractorId);
        END;

        IF OBJECT_ID('pay.Allocation', 'U') IS NULL
        BEGIN
            CREATE TABLE pay.Allocation
            (
                AllocationId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BnpSettlementAllocation PRIMARY KEY,
                PaymentId    BIGINT            NOT NULL,
                DocType      INT               NOT NULL,
                DocId        INT               NOT NULL,
                DocLp        INT               NOT NULL,
                DocNumber    VARCHAR(50)       NOT NULL,
                ContractorId INT               NOT NULL,
                Amount       DECIMAL(19,2)     NOT NULL,
                Score        DECIMAL(5,3)      NOT NULL,
                Reason       NVARCHAR(200)     NOT NULL,
                CONSTRAINT FK_BnpSettlementAllocation_Payment FOREIGN KEY (PaymentId)
                    REFERENCES pay.Payment (PaymentId) ON DELETE CASCADE
            );

            CREATE INDEX IX_BnpSettlementAllocation_Payment
                ON pay.Allocation (PaymentId);

            CREATE INDEX IX_BnpSettlementAllocation_Document
                ON pay.Allocation (DocType, DocId, DocLp);
        END;

        -- Schema touch-ups for installations created by an earlier version.
        -- NOTE: the accounting application reads some of these columns too and repeats them on
        -- its own side (DecisionStore.EnsureSchema), because it cannot wait for the service to
        -- start under a newer version.
        IF COL_LENGTH('pay.Payment', 'OperatorDocuments') IS NULL
            ALTER TABLE pay.Payment ADD OperatorDocuments NVARCHAR(500) NULL;

        IF COL_LENGTH('pay.Payment', 'OperatorNote') IS NULL
            ALTER TABLE pay.Payment ADD OperatorNote NVARCHAR(500) NULL;

        IF COL_LENGTH('pay.Payment', 'DecidedAt') IS NULL
            ALTER TABLE pay.Payment ADD DecidedAt DATETIME2(0) NULL;

        IF COL_LENGTH('pay.Payment', 'DecidedBy') IS NULL
            ALTER TABLE pay.Payment ADD DecidedBy NVARCHAR(80) NULL;

        -- Direction of the operation and the trail of posting it in ERP (the XL API stage).
        IF COL_LENGTH('pay.Payment', 'Direction') IS NULL
            ALTER TABLE pay.Payment ADD Direction CHAR(1) NOT NULL
                CONSTRAINT DF_BnpSettlementPayment_Direction DEFAULT 'P';

        IF COL_LENGTH('pay.Payment', 'RegisterSeries') IS NULL
            ALTER TABLE pay.Payment ADD RegisterSeries VARCHAR(5) NULL;

        IF COL_LENGTH('pay.Payment', 'ErpReportId') IS NULL
            ALTER TABLE pay.Payment ADD ErpReportId INT NULL;

        IF COL_LENGTH('pay.Payment', 'PostedAt') IS NULL
            ALTER TABLE pay.Payment ADD PostedAt DATETIME2(0) NULL;

        IF COL_LENGTH('pay.Payment', 'SettledAt') IS NULL
            ALTER TABLE pay.Payment ADD SettledAt DATETIME2(0) NULL;

        IF COL_LENGTH('pay.Payment', 'PostingError') IS NULL
            ALTER TABLE pay.Payment ADD PostingError NVARCHAR(500) NULL;

        IF COL_LENGTH('pay.Payment', 'PostingCategory') IS NULL
            ALTER TABLE pay.Payment ADD PostingCategory VARCHAR(20) NOT NULL
                CONSTRAINT DF_BnpSettlementPayment_PostingCategory DEFAULT 'Standard';

        -- The order reference the bank returned. For transfers ordered from XL it ties the
        -- entry to one specific document payment, which is worth having to hand when explaining
        -- what happened.
        IF COL_LENGTH('pay.Payment', 'EndToEndId') IS NULL
            ALTER TABLE pay.Payment ADD EndToEndId NVARCHAR(64) NULL;

        -- The counterparty's bank as supplied in the statement. For foreign accounts the BIC
        -- is the only way to identify the bank - there is no clearing code there.
        IF COL_LENGTH('pay.Payment', 'BankBic') IS NULL
            ALTER TABLE pay.Payment ADD BankBic VARCHAR(20) NULL;

        IF COL_LENGTH('pay.Payment', 'BankName') IS NULL
            ALTER TABLE pay.Payment ADD BankName NVARCHAR(100) NULL;

        IF COL_LENGTH('pay.Payment', 'BankClearing') IS NULL
            ALTER TABLE pay.Payment ADD BankClearing VARCHAR(20) NULL;

        -- The party and account the entry carried before an accountant swapped the contractor
        -- by hand. This lets revoking a settlement return to the state before that change.
        IF COL_LENGTH('pay.Payment', 'EntryContractorBefore') IS NULL
            ALTER TABLE pay.Payment ADD EntryContractorBefore INT NULL;

        IF COL_LENGTH('pay.Payment', 'EntryAccountBefore') IS NULL
            ALTER TABLE pay.Payment ADD EntryAccountBefore VARCHAR(31) NULL;

        -- Whether the payer account is what identified the contractor. It decides whether the
        -- party reaches the ERP cash entry at all - the remaining evidence stays on our side.
        IF COL_LENGTH('pay.Payment', 'ContractorFromBankAccount') IS NULL
            ALTER TABLE pay.Payment ADD ContractorFromBankAccount BIT NOT NULL
                CONSTRAINT DF_BnpSettlementPayment_ContractorFromBankAccount DEFAULT 0;

        IF COL_LENGTH('pay.Allocation', 'SettlementId') IS NULL
            ALTER TABLE pay.Allocation ADD SettlementId INT NULL;

        -- The file an operation came out of: the courier report behind a cash on delivery entry.
        -- Bank operations leave it empty - their statement is found in the archive by register and
        -- date, so storing a path for them would only be a second copy of the same fact.
        IF COL_LENGTH('pay.Payment', 'SourceFile') IS NULL
            ALTER TABLE pay.Payment ADD SourceFile NVARCHAR(400) NULL;

        -- Which courier report attachments have been read. POP3 has no flags, so this is the only
        -- record that a message has been dealt with; the key is the UIDL the protocol guarantees
        -- to be stable, together with the attachment's name - one message can carry several.
        IF OBJECT_ID('pay.CourierReport', 'U') IS NULL
        BEGIN
            CREATE TABLE pay.CourierReport
            (
                Uid         VARCHAR(70)    NOT NULL,
                FileName    NVARCHAR(200)  NOT NULL,
                ReceivedAt  DATETIME2(0)   NOT NULL,
                Sender      NVARCHAR(200)  NOT NULL,
                Subject     NVARCHAR(300)  NOT NULL,
                Courier     NVARCHAR(40)   NOT NULL,
                Format      VARCHAR(20)    NOT NULL,
                PayoutDate  DATE           NULL,
                PayoutTotal DECIMAL(19,2)  NULL,
                ParcelCount INT            NOT NULL CONSTRAINT DF_BnpCodReport_ParcelCount DEFAULT 0,
                FilePath    NVARCHAR(400)  NOT NULL,
                Note        NVARCHAR(500)  NULL,
                ReadAt      DATETIME2(0)   NOT NULL,
                CONSTRAINT PK_BnpCodReport PRIMARY KEY (Uid, FileName)
            );

            CREATE INDEX IX_BnpCodReport_PayoutDate ON pay.CourierReport (PayoutDate);
        END;

        -- The application's queue joins one proposal to each cash entry by ErpEntryId. Without an
        -- index that is a scan of the whole table per entry - 18 591 entries over the default
        -- sixty-day window, which is most of the three seconds the queue took to load.
        IF NOT EXISTS (SELECT 1 FROM sys.indexes
                       WHERE object_id = OBJECT_ID('pay.Payment')
                         AND name = 'IX_BnpSettlementPayment_ErpEntry')
        BEGIN
            CREATE INDEX IX_BnpSettlementPayment_ErpEntry
                ON pay.Payment (ErpEntryId) INCLUDE (PaymentId);
        END;

        -- How far a courier report has got. A report waits as long as it has to: the entries
        -- cannot be created before the money arrives, because they carry the transfer's date.
        --   Pending   - read and archived, waiting for the courier's transfer
        --   Posted    - the transfer arrived and matched, the parcels are in the register
        --   Mismatch  - the transfer arrived but the sums differ; nothing is settled from it
        --   Ignored   - not ours to post: dropshipping, a day before the takeover, a stray file
        IF COL_LENGTH('pay.CourierReport', 'Status') IS NULL
            ALTER TABLE pay.CourierReport ADD Status VARCHAR(20) NOT NULL
                CONSTRAINT DF_BnpCodReport_Status DEFAULT 'Pending';

        -- The courier's transfer this report was paid by: the ERP cash entry, its day and its
        -- amount. The day is what every entry made from the report carries.
        IF COL_LENGTH('pay.CourierReport', 'PayoutEntryId') IS NULL
            ALTER TABLE pay.CourierReport ADD PayoutEntryId INT NULL;

        IF COL_LENGTH('pay.CourierReport', 'PayoutBookedOn') IS NULL
            ALTER TABLE pay.CourierReport ADD PayoutBookedOn DATE NULL;

        IF COL_LENGTH('pay.CourierReport', 'PayoutAmount') IS NULL
            ALTER TABLE pay.CourierReport ADD PayoutAmount DECIMAL(19,2) NULL;

        -- When the accounting team was told the sums do not agree. Kept so that one report
        -- produces one message rather than one an hour.
        IF COL_LENGTH('pay.CourierReport', 'NotifiedAt') IS NULL
            ALTER TABLE pay.CourierReport ADD NotifiedAt DATETIME2(0) NULL;

        -- Whether the courier's own transfer has been flagged as not subject to settlement. Set
        -- only once every parcel of the report has been settled in the COD register.
        IF COL_LENGTH('pay.CourierReport', 'PayoutClosedAt') IS NULL
            ALTER TABLE pay.CourierReport ADD PayoutClosedAt DATETIME2(0) NULL;
        """;

    /// <summary>
    /// The operator's queue: payments awaiting a decision, one readable row each. Payments
    /// already settled in ERP, and those the operator has ruled on, are left out.
    /// </summary>
    private const string QueueViewScript = """
        CREATE OR ALTER VIEW pay.Queue AS
        SELECT
            p.PaymentId,
            CASE p.Confidence
                WHEN 'High'   THEN 1
                WHEN 'Medium' THEN 2
                WHEN 'Low'    THEN 3
                ELSE               4
            END                                              AS Priorytet,
            CASE p.Confidence
                WHEN 'High'   THEN N'1. pewne'
                WHEN 'Medium' THEN N'2. do akceptacji'
                WHEN 'Low'    THEN N'3. podpowiedź'
                ELSE               N'4. do wyjaśnienia'
            END                                              AS Pewnosc,
            p.BookingDate                                    AS DataWplaty,
            p.Amount                                         AS Kwota,
            p.Currency                                       AS Waluta,
            p.ContractorId                                   AS KontrahentId,
            COALESCE(NULLIF(k.Knt_Nazwa1, ''), p.PayerName)  AS Kontrahent,
            ISNULL(k.Knt_Akronim, '')                        AS Akronim,
            p.PayerName                                      AS NadawcaZBanku,
            p.PayerAccount                                   AS RachunekNadawcy,
            p.Description                                    AS TytulPrzelewu,
            dok.Lista                                        AS Dokumenty,
            ISNULL(dok.Ile, 0)                               AS LiczbaDokumentow,
            p.AllocatedAmount                                AS Przypisano,
            p.UnallocatedAmount                              AS Nieprzypisane,
            p.ContractorSource                               AS SkadKontrahent,
            p.Strategy                                       AS Strategia,
            p.Notes                                          AS Uwagi,
            p.ErpEntryId                                     AS ZapisErpId,
            p.OperatorDocuments,
            p.OperatorNote,
            p.DecidedAt,
            p.DecidedBy,
            p.FirstSeenAt,
            p.LastUpdatedAt
        FROM pay.Payment AS p
        LEFT JOIN CDN.KntKarty AS k
            ON k.Knt_GIDTyp = 32 AND k.Knt_GIDNumer = p.ContractorId
        OUTER APPLY (
            SELECT STRING_AGG(a.DocNumber, ' + ') WITHIN GROUP (ORDER BY a.DocNumber) AS Lista,
                   COUNT(*)                                                           AS Ile
            FROM pay.Allocation AS a
            WHERE a.PaymentId = p.PaymentId
        ) AS dok
        WHERE p.Status = 'Proposed' AND p.Direction = 'P';
        """;

    public static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Three commands, not one batch: the migration has to finish before the tables it renamed
        // are referred to by their new names, and CREATE OR ALTER VIEW cannot share a batch at all.
        foreach (var script in new[] { Migration, Script, QueueViewScript })
        {
            await using var command = new SqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
