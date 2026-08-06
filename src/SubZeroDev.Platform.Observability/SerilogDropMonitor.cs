using Serilog.Sinks.Async;

namespace SubZeroDev.Platform.Observability;

/// <summary>Watches the shared async buffer's supported inspector and exposes the exact
/// dropped-event count, plus one emergency console diagnostic on entry to failure-or-dropping and
/// one on recovery. Writes with <see cref="Console.Error"/> directly, bypassing Serilog: Serilog
/// itself may be the thing failing, so the diagnostic cannot depend on it being healthy.</summary>
internal sealed class SerilogDropMonitor : IAsyncLogEventSinkMonitor, IDisposable
{
    private readonly Lock _gate = new();
    private Timer? _timer;
    private IAsyncLogEventSinkInspector? _inspector;
    private bool _atCapacity;

    /// <summary>The exact number of events dropped so far because the buffer was full. Never an
    /// estimate: the supported inspector tracks it exactly.</summary>
    internal long DroppedMessagesCount => _inspector?.DroppedMessagesCount ?? 0;

    /// <summary>Whether the buffer is currently at capacity (actively dropping).</summary>
    internal bool AtCapacity => Volatile.Read(ref _atCapacity);

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        lock (_gate)
        {
            _inspector = inspector;
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }
    }

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _inspector = null;
        }
    }

    private void Poll()
    {
        var inspector = _inspector;
        if (inspector is null)
        {
            return;
        }

        var full = inspector.Count >= inspector.BufferSize;
        var previous = Volatile.Read(ref _atCapacity);

        if (full && !previous)
        {
            Volatile.Write(ref _atCapacity, true);
            Console.Error.WriteLine(
                $"[platform-telemetry] emergency: the local log buffer is full and dropping events "
                + $"(dropped so far: {inspector.DroppedMessagesCount}).");
        }
        else if (!full && previous)
        {
            Volatile.Write(ref _atCapacity, false);
            Console.Error.WriteLine(
                $"[platform-telemetry] recovered: the local log buffer is draining again "
                + $"(dropped total: {inspector.DroppedMessagesCount}).");
        }
    }

    public void Dispose() => StopMonitoring(_inspector!);
}
