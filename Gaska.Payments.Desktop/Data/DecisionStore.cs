using Gaska.Payments.Erp.Posting;
using Gaska.Payments.Erp.Reads;
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
    /// The clearing code cut out of the account number goes into <c>Bnk_Numer</c> - that, and that
    /// alone, is what XL later finds the bank by (see <see cref="IbanParts"/>). The card's own code
    /// carries the country in front of it, so that two banks that happen to share a clearing code
    /// in different countries do not collide on one card. The SWIFT code is sometimes empty and is
    /// no substitute for the clearing code: a card created without one will exist, but XL will not
    /// match it by itself.
    /// </remarks>
    /// <returns>The code of the bank created, or null when there was too little to identify it.</returns>
    public BankRef? CreateBank(
        string bic, string name, string bankCode, string countryCode,
        string city = "", string postalCode = "", string street = "")
    {
        // Whitespace is trimmed once, here: the Trim helper only shortens to the column width, and
        // SQL returns the number after RTRIM - without this the comparison below would differ by a
        // space.
        var clearingCode = Trim(bankCode.Trim(), 20);
        var cardCode = Trim(CardCode(clearingCode, countryCode, bic), 20);
        if (cardCode.Length == 0) return null;

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM CDN.Banki WHERE RTRIM(Bnk_Kod) = @kod)
            BEGIN
                INSERT INTO CDN.Banki
                    (Bnk_GIDTyp, Bnk_GIDFirma, Bnk_GIDLp, Bnk_Kod, Bnk_Nazwa, Bnk_Ulica, Bnk_Miasto,
                     Bnk_KodP, Bnk_Numer, Bnk_Swift, Bnk_KodKraju, Bnk_Aktywny, Bnk_Wsk, Bnk_PKOBP,
                     Bnk_Format, Bnk_IBAN)
                VALUES
                    (48,
                     (SELECT TOP 1 Bnk_GIDFirma FROM CDN.Banki ORDER BY Bnk_GIDNumer DESC),
                     0, @kod, @nazwa, @ulica, @miasto, @kodp, @numer, @swift, @kraj, 1, 0, 0, 0, 1);
            END;

            SELECT TOP 1 RTRIM(Bnk_Kod), Bnk_GIDNumer, ISNULL(Bnk_IBAN, 0), ISNULL(RTRIM(Bnk_Numer), '')
            FROM CDN.Banki WHERE RTRIM(Bnk_Kod) = @kod;
            """;

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@kod", cardCode);
        command.Parameters.AddWithValue("@nazwa", Trim(name.Length > 0 ? name : cardCode, 100));
        command.Parameters.AddWithValue("@numer", clearingCode);
        command.Parameters.AddWithValue("@swift", Trim(bic, 20));
        command.Parameters.AddWithValue("@kraj", Trim(countryCode, 2));
        command.Parameters.AddWithValue("@ulica", Trim(street, 60));
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
    /// The code a new bank card gets: the country, a dash, then the clearing code from the account
    /// number - <c>RO-1234</c> for a Romanian bank whose code is 1234.
    /// </summary>
    /// <remarks>
    /// The country belongs in front because a clearing code is only unique within its own country:
    /// four digits identify a bank in Romania and quite another one in Bulgaria, and the register
    /// keeps one card per branch across every country we are paid from. It costs nothing to add -
    /// XL matches a bank by <c>Bnk_Numer</c>, which stays the bare code, and ignores
    /// <c>Bnk_Kod</c> entirely (see <see cref="IbanParts"/>).
    ///
    /// A bank with no clearing code falls back to its SWIFT, which is unique worldwide and needs
    /// no prefix. So does one whose country we were not told.
    /// </remarks>
    private static string CardCode(string clearingCode, string countryCode, string bic)
    {
        if (clearingCode.Length == 0) return bic.Trim();

        var country = countryCode.Trim().ToUpperInvariant();
        return country.Length == 2 ? $"{country}-{clearingCode}" : clearingCode;
    }

    /// <summary>
    /// Repairs a bank card Comarch ERP XL cannot match to an IBAN: it writes in the bank code from
    /// the account number and raises the IBAN flag, and takes over the details the operator typed.
    /// </summary>
    /// <remarks>
    /// Better this than creating a second card for the same bank - the register holds one card per
    /// branch and already carries several entries under a single SWIFT code. Existing links are
    /// untouched, because accounts point at a bank by GID rather than by clearing code. The card's
    /// own code is left alone as well: it may be written on documents and referred to elsewhere,
    /// and nothing about matching depends on it.
    ///
    /// A field the operator filled in is written through, one they left empty is not. The window
    /// shows them what is on the card, so anything different is a correction made by somebody
    /// looking at the bank's own document - and an empty box can then never wipe what ERP holds.
    /// </remarks>
    public void RepairBankForIban(
        int bankId, string bankCode, string name, string city, string postalCode, string street = "") => Execute(
        """
        UPDATE CDN.Banki
        SET Bnk_Numer = @numer,
            Bnk_IBAN = 1,
            Bnk_Nazwa = CASE WHEN @nazwa <> '' THEN @nazwa ELSE Bnk_Nazwa END,
            Bnk_Ulica = CASE WHEN @ulica <> '' THEN @ulica ELSE Bnk_Ulica END,
            Bnk_Miasto = CASE WHEN @miasto <> '' THEN @miasto ELSE Bnk_Miasto END,
            Bnk_KodP = CASE WHEN @kodp <> '' THEN @kodp ELSE Bnk_KodP END
        WHERE Bnk_GIDNumer = @bank;
        """,
        ("@bank", bankId), ("@numer", Trim(bankCode.Trim(), 20)), ("@nazwa", Trim(name, 100)),
        ("@ulica", Trim(street, 60)), ("@miasto", Trim(city, 40)), ("@kodp", Trim(postalCode, 10)));

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

        var sql = $"""
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
              AND r.{BankAccountSql.InUse}
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
    /// Sets on the cash entry the party the transfer was settled with, and the account chosen for
    /// it. The statement is shared with the service - see <see cref="CashEntrySql"/>.
    /// </summary>
    /// <param name="account">
    /// The account picked in the panel. Empty leaves the choice to the statement, which takes the
    /// one this contractor's other entries carry - what the service does when it settles alone.
    /// </param>
    public void UpdateEntryContractor(
        long paymentId, int erpEntryId, int contractorId, string account = "") => Execute(
        CashEntrySql.SetEntryContractor,
        ("@contractor", contractorId), ("@entry", erpEntryId), ("@paymentId", paymentId),
        ("@konto", Trim(account.Trim(), 50)));

    /// <summary>
    /// Writes the numbers of the settled documents onto the entry, in place of the bank's
    /// reference. See <see cref="CashEntrySql.SetEntryDocumentNumber"/>.
    /// </summary>
    public void UpdateEntryDocumentNumber(int erpEntryId, IReadOnlyList<string> documentNumbers) => Execute(
        CashEntrySql.SetEntryDocumentNumber,
        ("@entry", erpEntryId), ("@numer", CashEntrySql.NumberFor(documentNumbers)));

    /// <summary>
    /// Restores on the entry the party and account from before the contractor was swapped by hand.
    /// </summary>
    public void RestoreEntryContractor(long paymentId, int erpEntryId) => Execute(
        CashEntrySql.RestoreEntryContractor,
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

    /// <summary>
    /// Sets or clears ERP's "nie rozliczaj" box on one document payment
    /// (<c>CDN.TraPlat.TrP_Rozliczona</c> 0 <-> 2).
    /// </summary>
    /// <remarks>
    /// The same decision the accountants make on the payment tab of a document in XL, made here
    /// because that is where they are looking when they realise an open item is never going to be
    /// pursued. A payment already settled (state 1) is left alone - the flag would contradict a
    /// settlement that exists, and the settlement has to be revoked first.
    /// </remarks>
    public void SetPaymentDoNotSettle(int docType, int docId, int docLp, bool doNotSettle) => Execute(
        """
        UPDATE CDN.TraPlat
        SET TrP_Rozliczona = @flag
        WHERE TrP_GIDTyp = @typ AND TrP_GIDNumer = @numer AND TrP_GIDLp = @lp
          AND TrP_Typ = 1
          AND TrP_Rozliczona IN (0, 2);
        """,
        ("@flag", doNotSettle ? 2 : 0), ("@typ", docType), ("@numer", docId), ("@lp", docLp));

    /// <summary>
    /// Puts the contractor the operator chose onto the cash entry, together with the contra
    /// account that follows from them.
    /// </summary>
    /// <remarks>
    /// The same statement the service uses when it settles by itself, so an entry ends up looking
    /// the same whichever hand named the party. Written straight to <c>CDN.Zapisy</c> because no
    /// XL API function modifies an existing entry.
    ///
    /// It runs when the contractor is swapped rather than at settlement: the accountant swaps a
    /// contractor precisely when the one on the entry is wrong, and leaving the wrong one there
    /// until something is settled means an entry nobody can find in ERP in the meantime.
    /// </remarks>
    public void SetEntryContractor(long paymentId, int erpEntryId, int contractorId, string? account) =>
        Execute(
            CashEntrySql.SetEntryContractor,
            ("@paymentId", paymentId), ("@entry", erpEntryId), ("@contractor", contractorId),
            ("@konto", (object?)account ?? string.Empty));

    /// <summary>
    /// Takes ERP's "not subject to settlement" flag back off an entry (<c>KAZ_Rozliczony</c>
    /// 2 -> 0) and returns the transfer to the queue.
    /// </summary>
    /// <remarks>
    /// The way back from <see cref="MarkDoNotSettle"/>, and it has to exist: the flag is set with
    /// one click, in bulk, and a transfer flagged by mistake was otherwise beyond reach of the
    /// application altogether.
    ///
    /// Only an entry actually carrying the flag is touched. One settled in ERP is left alone -
    /// there the 1 means something else entirely.
    /// </remarks>
    public void ClearDoNotSettle(long paymentId, int erpEntryId, string user)
    {
        Execute(
            """
            UPDATE CDN.Zapisy
            SET KAZ_Rozliczony = 0
            WHERE KAZ_GIDNumer = @entry AND KAZ_Rozliczony = 2;
            """,
            ("@entry", erpEntryId));

        Execute(
            """
            UPDATE pay.Payment
            SET Status = 'Proposed', OperatorNote = 'Zdjęto znacznik niepodlegania rozliczeniu.',
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
