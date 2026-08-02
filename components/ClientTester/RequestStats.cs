using System.Collections.Concurrent;

namespace ClientTester;

internal readonly record struct IntervalSnapshot(int Count, double AvgMs, double P95Ms);

internal readonly record struct Totals(long Sent, long Success, long Error);

/// <summary>
/// Thread-safe across every worker task. Latency samples are drained into a
/// fresh list on every <see cref="TakeIntervalSnapshot"/> call rather than
/// kept forever - this runs until Ctrl+C, so an unbounded sample list would
/// grow for as long as the process runs; only the current reporting
/// interval's samples are ever held at once.
/// </summary>
internal sealed class RequestStats
{
    private readonly ConcurrentQueue<double> _intervalLatenciesMs = new();
    private readonly ConcurrentDictionary<string, long> _errorReasons = new();
    private long _totalSent;
    private long _totalSuccess;
    private long _totalError;

    /// <summary>
    /// Fires the first time a given failure reason (an HTTP status code or
    /// an exception type+message) is ever seen, so it's visible immediately
    /// on the console instead of only as an aggregate count nobody can act
    /// on - a bare error counter can't tell "every request 404'd" apart from
    /// "every request timed out" or "DNS didn't resolve".
    /// </summary>
    public event Action<string>? NewErrorReasonSeen;

    public void RecordSuccess(double latencyMs)
    {
        Interlocked.Increment(ref _totalSent);
        Interlocked.Increment(ref _totalSuccess);
        _intervalLatenciesMs.Enqueue(latencyMs);
    }

    public void RecordFailure(double latencyMs, string reason)
    {
        Interlocked.Increment(ref _totalSent);
        Interlocked.Increment(ref _totalError);
        _intervalLatenciesMs.Enqueue(latencyMs);

        if (_errorReasons.TryAdd(reason, 1))
        {
            NewErrorReasonSeen?.Invoke(reason);
        }
        else
        {
            _errorReasons.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }
    }

    public Totals TotalsSnapshot() => new(
        Interlocked.Read(ref _totalSent),
        Interlocked.Read(ref _totalSuccess),
        Interlocked.Read(ref _totalError));

    public IReadOnlyCollection<KeyValuePair<string, long>> ErrorReasonsSnapshot() =>
        _errorReasons.OrderByDescending(kvp => kvp.Value).ToArray();

    public IntervalSnapshot TakeIntervalSnapshot()
    {
        var latencies = new List<double>();
        while (_intervalLatenciesMs.TryDequeue(out var latencyMs))
        {
            latencies.Add(latencyMs);
        }

        if (latencies.Count == 0)
        {
            return new IntervalSnapshot(0, 0, 0);
        }

        latencies.Sort();
        var avg = latencies.Average();
        var p95Index = Math.Min(latencies.Count - 1, (int)Math.Ceiling(latencies.Count * 0.95) - 1);
        return new IntervalSnapshot(latencies.Count, avg, latencies[p95Index]);
    }
}
