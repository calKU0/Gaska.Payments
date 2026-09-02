namespace Gaska.Payments.Erp;

public sealed class XlOptions
{
    public const string SectionName = "Xl";

    /// <summary>Company (database) name inside XL - not the SQL database name.</summary>
    public string Database { get; set; } = string.Empty;

    public string Operator { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Version of the API structures - must match the wrapper in use.</summary>
    public int ApiVersion { get; set; } = 20251;

    /// <summary>Whether to create cash entries in the buffer, where they can still be corrected or deleted.</summary>
    public bool ToBuffer { get; set; } = true;

    /// <summary>The earliest booking date to post operations from.</summary>
    public DateTime PostFrom { get; set; } = DateTime.Today;

    /// <summary>Cap on operations per run - a safeguard for the first passes.</summary>
    public int MaxOperationsPerRun { get; set; } = 500;

    /// <summary>Cash operation symbol used for bank commissions and charges.</summary>
    public string FeeOperation { get; set; } = "PRW";
}
