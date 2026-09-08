namespace Gaska.Payments.Erp.Reads;

/// <summary>
/// How a contractor's bank accounts are read out of ERP, and what is left out.
/// </summary>
/// <remarks>
/// Archived accounts are not the contractor's accounts any more. ERP keeps them so that old
/// documents still make sense, but a payment arriving today has nothing to do with them - and
/// leaving them in makes the register ambiguous: 345 account numbers here are held by more than
/// one contractor, and 32 of those have exactly one live holder and the rest retired. Reading
/// them all, the payer cannot be told apart and the transfer lands on the wrong card or on none.
///
/// Shared between the service and the application because both search the same register and must
/// arrive at the same contractor.
/// </remarks>
public static class BankAccountSql
{
    /// <summary>
    /// The account is in use. <c>RkB_CzasArchiwizacji</c> holds the moment of archiving and is
    /// zero for every account that has never been archived - 23 428 of them here against 1 060
    /// archived.
    /// </summary>
    public const string InUse = "RkB_CzasArchiwizacji = 0";

    /// <summary>
    /// A common table expression named <c>wycofane</c>: contractor and account-number pairs whose
    /// every card entry is archived.
    /// </summary>
    /// <remarks>
    /// <c>CDN.NumeryRachunkow</c> - the normalised numbers ERP matches by - carries no archiving
    /// mark of its own and is not kept in step: 186 numbers in it are backed only by an archived
    /// card entry. So filtering <c>CDN.RachunkiBankowe</c> alone would change nothing; the same
    /// retired number would come back through the other half of the union.
    ///
    /// Pairs are subtracted rather than accounts dropped outright, because a contractor may hold
    /// the same number twice - once archived, once live - and that one is still theirs. A number
    /// present only in <c>NumeryRachunkow</c>, with no card entry at all, is left alone: nothing
    /// says it is retired.
    /// </remarks>
    public const string RetiredAccounts = """
        wycofane AS (
            SELECT RkB_ObiNumer AS Knt, REPLACE(RTRIM(RkB_NrRachunku), ' ', '') AS Numer
            FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32 AND RTRIM(RkB_NrRachunku) <> '' AND RkB_CzasArchiwizacji <> 0
            EXCEPT
            SELECT RkB_ObiNumer, REPLACE(RTRIM(RkB_NrRachunku), ' ', '')
            FROM CDN.RachunkiBankowe
            WHERE RkB_ObiTyp = 32 AND RTRIM(RkB_NrRachunku) <> '' AND RkB_CzasArchiwizacji = 0
        )
        """;
}
