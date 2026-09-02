namespace Gaska.Payments.Desktop.Data;

/// <summary>How far a cash entry has been settled against documents in ERP.</summary>
public enum SettlementState
{
    /// <summary>Nothing has been settled yet.</summary>
    Unsettled,

    /// <summary>Part of the amount has been matched to documents, the rest is waiting.</summary>
    Partial,

    /// <summary>Settled in full.</summary>
    Settled,
}

/// <summary>Turns the state code the queue query computes into an enum value.</summary>
public static class SettlementStates
{
    public static SettlementState Parse(string code) => code switch
    {
        "R" => SettlementState.Settled,
        "C" => SettlementState.Partial,
        _ => SettlementState.Unsettled,
    };

    /// <summary>
    /// The wording for the list column - the same words the settlement state filter uses.
    /// </summary>
    public static string Describe(this SettlementState state) => state switch
    {
        SettlementState.Settled => "rozliczony w pełni",
        SettlementState.Partial => "rozliczony częściowo",
        _ => "nierozliczony",
    };
}
