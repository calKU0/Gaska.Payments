namespace Gaska.Payments.Application.Settlement;

/// <summary>The life cycle of a settlement proposal.</summary>
public static class SettlementStatus
{
    /// <summary>The engine's proposal - it may be overwritten on the next run.</summary>
    public const string Proposed = "Proposed";

    /// <summary>Already settled in ERP outside this service - it needs nobody's attention.</summary>
    public const string SettledInErp = "SettledInErp";

    /// <summary>
    /// An operation that is to reach ERP as a cash entry but is not settled by this service -
    /// account debits, that is our own outgoing payments.
    /// </summary>
    public const string NoSettlement = "NoSettlement";

    /// <summary>Approved by the operator, waiting to be posted.</summary>
    public const string Accepted = "Accepted";

    /// <summary>Rejected by the operator.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Posted in ERP through the XL API.</summary>
    public const string Posted = "Posted";
}

/// <summary>The status of a single service run.</summary>
public static class RunStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
