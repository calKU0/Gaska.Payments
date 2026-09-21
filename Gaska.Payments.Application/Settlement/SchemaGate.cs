namespace Gaska.Payments.Application.Settlement;

/// <summary>
/// Opens once the <c>pay.*</c> tables are in place, for whatever runs beside the cycle.
/// </summary>
/// <remarks>
/// The cycle prepares the schema and retries until the database answers. Anything else that works
/// on those tables waits here instead of preparing them a second time alongside - two sessions
/// creating the same table at once is a race one of them loses.
/// </remarks>
public sealed class SchemaGate
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _ready.TrySetResult();

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);
}
