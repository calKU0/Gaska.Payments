namespace Gaska.Payments.Erp;

/// <summary>How to reach the Comarch ERP XL database, and who we are inside it.</summary>
public sealed class ErpOptions
{
    public const string SectionName = "Erp";

    /// <summary>
    /// Connection to the Comarch ERP XL database. Reads from <c>CDN.*</c>; writes go to our own
    /// <c>pay.*</c> schema, with the two documented exceptions the XL API cannot cover.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Our own company's tax id - it lets us discard our own id from split payment messages.</summary>
    public string OwnNip { get; set; } = string.Empty;
}
