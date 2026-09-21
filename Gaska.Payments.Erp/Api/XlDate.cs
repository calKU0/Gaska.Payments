namespace Gaska.Payments.Erp;

/// <summary>Comarch ERP XL counts dates in days since 1800-12-28.</summary>
public static class XlDate
{
    private static readonly DateTime Epoch = new(1800, 12, 28);

    public static int FromDateTime(DateTime value) => (int)(value.Date - Epoch).TotalDays;

    public static DateTime ToDateTime(int value) => value <= 0 ? DateTime.MinValue : Epoch.AddDays(value);

    private static readonly DateTime TimeEpoch = new(1990, 1, 1);

    /// <summary>
    /// A moment as XL's date-and-time fields count it - seconds since 1990-01-01 - for
    /// <c>DataCzasOtw</c> on a report and <c>DataCzas</c> on an entry.
    /// </summary>
    public static int Moment(DateTime value) => (int)(value - TimeEpoch).TotalSeconds;
}
