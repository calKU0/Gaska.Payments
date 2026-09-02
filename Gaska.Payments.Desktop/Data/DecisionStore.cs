using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// Recording the accountant's decisions. Save for the one exception described below it touches
/// only our own <c>dbo</c> tables - settlements live in <c>CDN.Rozliczenia</c> and are read from
/// there.
/// </summary>
/// <remarks>
/// The methods are synchronous because they are called from the thread holding the XL session,
/// where no <c>await</c> may occur.
/// </remarks>
public sealed class DecisionStore(string connectionString)
{
    /// <summary>
    /// Adds the proposal table columns the accounting application uses.
    /// </summary>
    /// <remarks>
    /// The service creates the table (<c>PaymentSchema</c>) and owns it, but the application
    /// cannot assume the service has already started under a newer version. The conditions are
    /// idempotent and deliberately repeat the ones in the service.
    ///
    /// When adding a column the application reads, it has to be added in both places - otherwise
    /// the queue falls over at startup with "Invalid column name".
    /// </remarks>
    public void EnsureSchema() => Execute(
        """
        IF COL_LENGTH('pay.Payment', 'EntryContractorBefore') IS NULL
            ALTER TABLE pay.Payment ADD EntryContractorBefore INT NULL;

        IF COL_LENGTH('pay.Payment', 'EntryAccountBefore') IS NULL
            ALTER TABLE pay.Payment ADD EntryAccountBefore VARCHAR(31) NULL;

        IF COL_LENGTH('pay.Payment', 'EndToEndId') IS NULL
            ALTER TABLE pay.Payment ADD EndToEndId NVARCHAR(64) NULL;

        IF COL_LENGTH('pay.Payment', 'BankBic') IS NULL
            ALTER TABLE pay.Payment ADD BankBic VARCHAR(20) NULL;

        IF COL_LENGTH('pay.Payment', 'BankName') IS NULL
            ALTER TABLE pay.Payment ADD BankName NVARCHAR(100) NULL;

        IF COL_LENGTH('pay.Payment', 'BankClearing') IS NULL
            ALTER TABLE pay.Payment ADD BankClearing VARCHAR(20) NULL;

        IF COL_LENGTH('pay.Payment', 'ContractorFromBankAccount') IS NULL
            ALTER TABLE pay.Payment ADD ContractorFromBankAccount BIT NOT NULL
                CONSTRAINT DF_BnpSettlementPayment_ContractorFromBankAccount DEFAULT 0;

        IF COL_LENGTH('pay.Payment', 'PostingError') IS NULL
            ALTER TABLE pay.Payment ADD PostingError NVARCHAR(500) NULL;

        IF COL_LENGTH('pay.Payment', 'SourceFile') IS NULL
            ALTER TABLE pay.Payment ADD SourceFile NVARCHAR(400) NULL;
        """);

    /// <summary>
    /// Creates a bank card from the statement details and returns its code.
    /// </summary>
    /// <remarks>
    /// The XL API has no function for the bank register - there is only <c>XLNowyRachunek</c>,
    /// which requires the bank to exist already. Hence the direct insert into <c>CDN.Banki</c>.
    ///
    /// BNP sends no details of the counterparty's bank in any message - verified against the raw
    /// XML and against the GOconnect manual. The name and SWIFT code therefore come from the
    /// operator, or from a hint derived from our own register.
    ///
    /// The card's code is the bank code cut out of the account number according to the IBAN
    /// registry - the same one XL later finds it by (see <see cref="IbanParts"/>). The SWIFT code
    /// is sometimes empty and is no substitute for that code: a card created without it will
    /// exist, but XL will not match it by itself.
    /// </remarks>
    /// <returns>The code of the bank created, or null when there was too little to identify it.</returns>
    public BankRef? CreateBank(
        string bic, string name, string bankCode, string countryCode, string city = "", string postalCode = "")
    {
        // The card's code is the bank code from the account number - the same one XL will find
        // it by. The SWIFT code is kept for a bank with no sensible code, though XL will not bind
        // that one itself. Whitespace is trimmed once, here: the Trim helper only shortens to the
        // column width, and SQL returns the number after RTRIM - without this the comparison below
        // would differ by a space.
        var clearingCode = Trim(bankCode.Trim(), 20);
        var cardCode = clearingCode.Length > 0 ? clearingCode : bic.Trim();
        if (cardCode.Length == 0) return null;

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM CDN.Banki WHERE RTRIM(Bnk_Kod) = @kod)
            BEGIN
                INSERT INTO CDN.Banki
                    (Bnk_GIDTyp, Bnk_GIDFirma, Bnk_GIDLp, Bnk_Kod, Bnk_Nazwa, Bnk_Miasto, Bnk_KodP,
                     Bnk_Numer, Bnk_Swift, Bnk_KodKraju, Bnk_Aktywny, Bnk_Wsk, Bnk_PKOBP, Bnk_Format,
                     Bnk_IBAN)
                VALUES
                    (48,
                     (SELECT TOP 1 Bnk_GIDFirma FROM CDN.Banki ORDER BY Bnk_GIDNumer DESC),
                     0, @kod, @nazwa, @miasto, @kodp, @numer, @swift, @kraj, 1, 0, 0, 0, 1);
            END;

            SELECT TOP 1 RTRIM(Bnk_Kod), Bnk_GIDNumer, ISNULL(Bnk_IBAN, 0), ISNULL(RTRIM(Bnk_Numer), '')
            FROM CDN.Banki WHERE RTRIM(Bnk_Kod) = @kod;
            """;

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@kod", Trim(cardCode, 20));
        command.Parameters.AddWithValue("@nazwa", Trim(name.Length > 0 ? name : cardCode, 100));
        command.Parameters.AddWithValue("@numer", clearingCode);
        command.Parameters.AddWithValue("@swift", Trim(bic, 20));
        command.Parameters.AddWithValue("@kraj", Trim(countryCode, 2));
        command.Parameters.AddWithValue("@miasto", Trim(city, 40));
        command.Parameters.AddWithValue("@kodp", Trim(postalCode, 10));

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        // A card under this code may have existed already - the INSERT then did nothing and we
        // are holding somebody else's row. That is why whether XL will match it is decided by the
        // row read back, not by the parameters we passed.
        var binds = reader.GetInt16(2) == 1 && reader.GetString(3) == clearingCode;

        return new BankRef(reader.GetString(0), reader.GetInt32(1), binds);
    }

    /// <summary>
    /// Repairs a bank card Comarch ERP XL cannot match to an IBAN: it writes in the bank code from
    /// the account number and raises the IBAN flag. Empty descriptive fields are filled in.
    /// </summary>
    /// <remarks>
    /// Better this than creating a second card for the same bank - the register holds one card per
    /// branch and already carries several entries under a single SWIFT code. Existing links are
    /// untouched, because accounts point at a bank by GID rather than by clearing code.
    /// </remarks>
    public void RepairBankForIban(int bankId, string bankCode, string name, string city, string postalCode) => Execute(
        """
        UPDATE CDN.Banki
        SET Bnk_Numer = @numer,
            Bnk_IBAN = 1,
            Bnk_Nazwa = CASE WHEN ISNULL(RTRIM(Bnk_Nazwa), '') = '' THEN @nazwa ELSE Bnk_Nazwa END,
            Bnk_Miasto = CASE WHEN ISNULL(RTRIM(Bnk_Miasto), '') = '' THEN @miasto ELSE Bnk_Miasto END,
            Bnk_KodP = CASE WHEN ISNULL(RTRIM(Bnk_KodP), '') = '' THEN @kodp ELSE Bnk_KodP END
        WHERE Bnk_GIDNumer = @bank;
        """,
        ("@bank", bankId), ("@numer", Trim(bankCode.Trim(), 20)), ("@nazwa", Trim(name, 100)),
        ("@miasto", Trim(city, 40)), ("@kodp", Trim(postalCode, 10)));

    /// <summary>
    /// Attaches the bank to a contractor account <c>XLNowyRachunek</c> left without one. Returns
    /// the number of accounts corrected.
    /// </summary>
    /// <remarks>
    /// This is not a shortcut for convenience but the only way. Verified on the test database:
    /// once a number passes IBAN validation XL takes over - it strips the country prefix, sets
    /// <c>RkB_IBAN = 1</c>, fills in <c>RkB_Kraj</c> and ignores the <c>BankKod</c> it was given
    /// entirely, leaving <c>RkB_BnkNumer = 0</c>. And it returns 0, meaning success. For a
    /// domestic account it recognises the bank itself from the clearing code inside the national
    /// number, so the problem is confined to foreign accounts.
    /// </remarks>
    public int LinkAccountBank(int contractorId, string account, int bankId, string swift)
    {
        // XL stores an IBAN without its country prefix, so the account is searched for in both forms.

        const string sql = """
            UPDATE r
            SET r.RkB_BnkTyp = b.Bnk_GIDTyp,
                r.RkB_BnkFirma = b.Bnk_GIDFirma,
                r.RkB_BnkNumer = b.Bnk_GIDNumer,
                r.RkB_BnkLp = 0,
                r.RkB_Swift = CASE WHEN ISNULL(r.RkB_Swift, '') = '' THEN @swift ELSE r.RkB_Swift END
            FROM CDN.RachunkiBankowe AS r
            CROSS JOIN CDN.Banki AS b
            WHERE b.Bnk_GIDNumer = @bank
              AND r.RkB_ObiTyp = 32
              AND r.RkB_ObiNumer = @knt
              AND ISNULL(r.RkB_BnkNumer, 0) = 0
              AND REPLACE(RTRIM(r.RkB_NrRachunku), ' ', '') IN (@pelny, @bezKraju);
            """;

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@bank", bankId);
        command.Parameters.AddWithValue("@knt", contractorId);
        command.Parameters.AddWithValue("@pelny", IbanParts.Compact(account));
        command.Parameters.AddWithValue("@bezKraju", IbanParts.WithoutCountryCode(account));
        command.Parameters.AddWithValue("@swift", Trim(swift, 20));

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets on the cash entry the party the transfer was actually settled with, along with the
    /// contra account.
    /// </summary>
    /// <remarks>
    /// The entry is created during posting, often on the anonymous party, because the automat does
    /// not know the party then. Once an accountant names a contractor by hand the entry has to
    /// reflect that - otherwise the open item hangs on the contractor's document while the entry
    /// belongs to nobody.
    ///
    /// The XL API leaves no way out here: for cash there are only <c>XLDodajRaport</c>,
    /// <c>XLDodajZapis</c> and <c>XLZamknijRaport</c> - no function that modifies an existing
    /// entry (checked against every function in the library).
    /// </remarks>
    public void UpdateEntryContractor(long paymentId, int erpEntryId, int contractorId) => Execute(
        """
        -- The state before the first swap is remembered once; later changes do not overwrite
        -- it, so that a revoke returns to what the automat set rather than to a previous attempt.
        UPDATE p
        SET p.EntryContractorBefore = z.KAZ_KNTNumer,
            p.EntryAccountBefore = ISNULL(z.KAZ_KontoPrzec, '')
        FROM pay.Payment AS p
        INNER JOIN CDN.Zapisy AS z ON z.KAZ_GIDNumer = @entry
        WHERE p.PaymentId = @paymentId
          AND p.EntryContractorBefore IS NULL
          AND z.KAZ_KNTNumer <> @contractor;

        -- The contra account follows from the party; left over from the previous contractor it
        -- throws the posting scheme off. We take the one ERP uses on that contractor's other
        -- entries - the cards have no accounts filled in, and we do not want to hard-code a scheme.
        DECLARE @konto VARCHAR(31) = (
            SELECT TOP 1 KAZ_KontoPrzec
            FROM CDN.Zapisy
            WHERE KAZ_KNTNumer = @contractor AND ISNULL(KAZ_KontoPrzec, '') <> ''
            GROUP BY KAZ_KontoPrzec
            ORDER BY COUNT(*) DESC);

        UPDATE CDN.Zapisy
        SET KAZ_KNTTyp = 32,
            KAZ_KNTNumer = @contractor,
            KAZ_KontoPrzec = ISNULL(@konto, KAZ_KontoPrzec)
        WHERE KAZ_GIDNumer = @entry AND KAZ_KNTNumer <> @contractor;
        """,
        ("@contractor", contractorId), ("@entry", erpEntryId), ("@paymentId", paymentId));

    /// <summary>
    /// Restores on the entry the party and account from before the contractor was swapped by hand.
    /// </summary>
    /// <remarks>
    /// Called when revoking a settlement: since the open item disappears, the entry should return
    /// to the shape the automat created it in. When nobody swapped anything there is nothing to
    /// undo.
    /// </remarks>
    public void RestoreEntryContractor(long paymentId, int erpEntryId) => Execute(
        """
        UPDATE z
        SET z.KAZ_KNTNumer = p.EntryContractorBefore,
            z.KAZ_KNTTyp = CASE WHEN p.EntryContractorBefore = 0 THEN 0 ELSE 32 END,
            z.KAZ_KontoPrzec = p.EntryAccountBefore
        FROM CDN.Zapisy AS z
        INNER JOIN pay.Payment AS p ON p.PaymentId = @paymentId
        WHERE z.KAZ_GIDNumer = @entry AND p.EntryContractorBefore IS NOT NULL;

        UPDATE pay.Payment
        SET EntryContractorBefore = NULL, EntryAccountBefore = NULL
        WHERE PaymentId = @paymentId;
        """,
        ("@paymentId", paymentId), ("@entry", erpEntryId));

    /// <summary>Records that a transfer was settled from the application.</summary>
    public void MarkSettled(long paymentId, string user) => Execute(
        """
        UPDATE pay.Payment
        SET Status = 'Posted', SettledAt = SYSDATETIME(),
            DecidedAt = SYSDATETIME(), DecidedBy = @user, PostingError = NULL
        WHERE PaymentId = @paymentId
        """,
        ("@user", Trim(user)), ("@paymentId", paymentId));

    /// <summary>
    /// Records why a settlement failed.
    /// </summary>
    /// <remarks>
    /// The message has to survive a refresh of the queue - it used to vanish along with the row,
    /// leaving the accountant with no idea why nothing had happened.
    /// </remarks>
    public void SaveError(long paymentId, string error) => Execute(
        """
        UPDATE pay.Payment SET PostingError = @error WHERE PaymentId = @paymentId
        """,
        ("@error", Trim(error, 500)), ("@paymentId", paymentId));

    /// <summary>Returns a transfer to the queue after its settlements were revoked.</summary>
    public void MarkUnsettled(long paymentId, string user) => Execute(
        """
        UPDATE pay.Payment
        SET Status = 'Proposed', SettledAt = NULL,
            DecidedAt = SYSDATETIME(), DecidedBy = @user
        WHERE PaymentId = @paymentId
        """,
        ("@user", Trim(user)), ("@paymentId", paymentId));

    /// <summary>
    /// Marks an ERP entry as not subject to settlement (<c>KAZ_Rozliczony = 2</c>).
    /// </summary>
    /// <remarks>
    /// This is a different thing from hiding it from the queue: there the transfer merely leaves
    /// our list while ERP still holds it as an unsettled open item. Here we tell ERP outright that
    /// there is nothing to settle - for commissions, refunds and movements between our own
    /// accounts.
    ///
    /// The flag cannot be set through the XL API once the entry exists (there is no function that
    /// modifies a cash entry), so it goes the same way the party does.
    /// </remarks>
    public void MarkDoNotSettle(long paymentId, int erpEntryId, string user)
    {
        Execute(
            """
            UPDATE CDN.Zapisy
            SET KAZ_Rozliczony = 2
            WHERE KAZ_GIDNumer = @entry AND KAZ_Rozliczony = 0;
            """,
            ("@entry", erpEntryId));

        Execute(
            """
            UPDATE pay.Payment
            SET Status = 'NoSettlement', OperatorNote = 'Oznaczony jako niepodlegający rozliczeniu.',
                DecidedAt = SYSDATETIME(), DecidedBy = @user
            WHERE PaymentId = @paymentId
            """,
            ("@user", Trim(user)), ("@paymentId", paymentId));
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string Trim(string value, int maxLength = 80) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
