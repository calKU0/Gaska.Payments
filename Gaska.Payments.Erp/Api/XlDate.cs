namespace Gaska.Payments.Erp;

/// <summary>Comarch ERP XL counts dates in days since 1800-12-28.</summary>
public static class XlDate
{
    private static readonly DateTime Epoch = new(1800, 12, 28);

    public static int FromDateTime(DateTime value) => (int)(value.Date - Epoch).TotalDays;

    public static DateTime ToDateTime(int value) => value <= 0 ? DateTime.MinValue : Epoch.AddDays(value);
}
