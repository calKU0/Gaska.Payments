using Gaska.Payments.Integrations.Bank;

using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Application.Settlement;

public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    /// <summary>How many minutes apart the service repeats its cycle.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How many days back operations are downloaded from the bank. The window has to be wide
    /// enough to survive a weekend and an outage of the service.
    /// </summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>How far back sales documents are loaded into the matching index.</summary>
    public int ReceivablesLookbackMonths { get; set; } = 18;

    /// <summary>Whether to create the missing <c>pay.*</c> tables at startup.</summary>
    public bool EnsureSchemaOnStartup { get; set; } = true;

    /// <summary>
    /// Series of the ERP bank registers (<c>CDN.Rejestry.KAR_Seria</c>) the entries go to.
    /// Operations are assigned to a register by account number, not by position in this list.
    /// </summary>
    public List<string> Registers { get; set; } = [];

    /// <summary>
    /// Registers of credit card accounts. Cash entries are created on them exactly as on the
    /// other registers, but nothing there is matched or settled: a card statement is an
    /// employee's spending, not an open item with a contractor. The party on the entry is the
    /// anonymous one.
    /// </summary>
    public List<string> CardRegisters { get; set; } = [];

    /// <summary>
    /// The remaining registers we only post to: the social fund, the auxiliary social insurance
    /// account, the grant accounts. They behave exactly like the card ones.
    /// </summary>
    /// <remarks>
    /// The list is kept apart from <see cref="CardRegisters"/> for one reason only: to a human
    /// they are different things. The application draws a card icon next to the card registers,
    /// and putting one next to the social fund account would be a lie. The posting rule is the
    /// same for both lists and is written down in one place.
    /// </remarks>
    public List<string> PostOnlyRegisters { get; set; } = [];

    /// <summary>
    /// Every register we download statements for. The accounts to query in the bank come from
    /// this list, so the registers without settlement have to be visible in it even though they
    /// take a different path afterwards.
    /// </summary>
    public IReadOnlyList<string> AllRegisters =>
        [.. Registers, .. CardRegisters, .. PostOnlyRegisters];

    /// <summary>
    /// The operation kind forced by the register itself, or <c>null</c> when the register is an
    /// ordinary one and the kind is decided by the content of the operation.
    /// </summary>
    public string? CategoryOf(string? series) => series switch
    {
        null => null,
        _ when Lists(CardRegisters, series) => PaymentCategory.Card,
        _ when Lists(PostOnlyRegisters, series) => PaymentCategory.PostOnly,
        _ => null,
    };

    private static bool Lists(List<string> registers, string series) =>
        registers.Contains(series, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acronym of our own company's card. The legs of the split payment mechanism are transfers
    /// between our own accounts, so the party on them is ourselves.
    /// </summary>
    public string OwnContractorAcronym { get; set; } = string.Empty;
}
