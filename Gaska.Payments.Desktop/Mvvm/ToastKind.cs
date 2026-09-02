namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>Severity of a message. It decides the toast's colour and how long it stays up.</summary>
public enum ToastKind
{
    /// <summary>Plain information - white.</summary>
    Info,

    /// <summary>The operation succeeded - green.</summary>
    Success,

    /// <summary>Something needs attention but nothing broke - orange.</summary>
    Warning,

    /// <summary>The operation failed - red.</summary>
    Error,
}
