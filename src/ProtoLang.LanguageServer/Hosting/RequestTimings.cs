using System.Collections.Concurrent;
using System.Diagnostics;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>Some runs of one operation, and what they cost.</summary>
/// <param name="Milliseconds">Every sample held, oldest first.</param>
/// <remarks>
/// <b>One home for the percentile rule, because two would be worse than none.</b> A status report
/// whose p95 meant something slightly different from the benchmark's p95 would be a number a reader
/// could compare against <c>docs/performance.md</c> and be misled by, which is a harder failure to
/// notice than having no number at all. The benchmark's <c>Sample</c> reports through this for that
/// reason.
/// </remarks>
public sealed record LatencySample(IReadOnlyList<double> Milliseconds)
{
    /// <summary>A measurement that has not happened.</summary>
    public static LatencySample None { get; } = new([]);

    public int Count => Milliseconds.Count;

    public double Median => Percentile(50);

    public double P95 => Percentile(95);

    public double Min => Milliseconds.Count == 0 ? double.NaN : Milliseconds.Min();

    public double Max => Milliseconds.Count == 0 ? double.NaN : Milliseconds.Max();

    /// <summary>The value at <paramref name="percentile"/>, by nearest rank.</summary>
    /// <remarks>
    /// Nearest-rank on the sorted samples: no interpolation, so every figure reported is a run that
    /// actually happened rather than an average of two that did. With twenty samples the 95th is the
    /// second slowest, which is the intent -- one outlier does not set the number, and two do.
    /// </remarks>
    public double Percentile(int percentile)
    {
        // A sample with no runs in it is a measurement that did not happen, and reporting 0.00 ms for
        // it would read as the fastest row in the table.
        if (Milliseconds.Count == 0)
        {
            return double.NaN;
        }

        var sorted = Milliseconds.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// What the requests this server has actually answered cost, kept as a short recent history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always on, which is only defensible because it is this cheap.</b> One
/// <see cref="Stopwatch"/> and one array write per request answered, against operations budgeted at
/// tens of milliseconds. The alternative -- switching it on when a user reports slowness -- asks the
/// user to reproduce a problem they have already had, and the reproduction is the part that does not
/// happen.
/// </para>
/// <para>
/// <b>Recent rather than cumulative, and the difference is the whole point.</b> A mean over a
/// day-long session is dominated by the session and says nothing about the last minute, which is when
/// the user noticed. A bounded ring answers "what is it doing now", and the count beside it says how
/// much of a now there is to speak of.
/// </para>
/// <para>
/// <b>Only answers are recorded.</b> A request refused for staleness under 26.1, or cancelled by a
/// keystroke, produced no answer -- so its duration is not the latency of anything, and folding it in
/// would make a fast server look fast for the wrong reason or a superseded hover look like a slow
/// one. What a report has instead, for a server that feels stuck, are the in-flight and outstanding
/// counts on <see cref="DeferredAnswers"/>.
/// </para>
/// </remarks>
public sealed class RequestTimings
{
    /// <summary>How many runs of each operation are kept.</summary>
    /// <remarks>
    /// Enough that a p95 means something -- the twentieth of twenty samples is the top of the range
    /// rather than a percentile -- and small enough that the whole structure stays a handful of
    /// kilobytes for every operation at once. It is also about a minute of hovering, which is the
    /// window a user describing "it just got slow" is talking about.
    /// </remarks>
    public const int Remembered = 50;

    private readonly ConcurrentDictionary<string, Ring> _operations = new(StringComparer.Ordinal);

    /// <summary>Remembers what one answer cost.</summary>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null or blank.</exception>
    public void Record(string operation, double milliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        _operations.GetOrAdd(operation, _ => new Ring()).Add(milliseconds);
    }

    /// <summary>Times <paramref name="answer"/> and records it, if it answers.</summary>
    /// <remarks>
    /// The recording is deliberately not in a <c>finally</c>. See the type's remarks: a request that
    /// threw did not answer, and the clock reading from it is the length of a refusal.
    /// </remarks>
    public async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var clock = Stopwatch.StartNew();
        var answered = await answer().ConfigureAwait(false);

        Record(operation, clock.Elapsed.TotalMilliseconds);

        return answered;
    }

    /// <summary>What that operation has recently cost, empty where it has not been asked.</summary>
    public LatencySample For(string operation)
        => _operations.TryGetValue(operation, out var ring) ? ring.Sample() : LatencySample.None;

    /// <summary>Every operation measured so far, in the order the budgets list them.</summary>
    /// <remarks>
    /// Budgeted operations first and in the documented order, so that a report reads the same way
    /// twice and can be compared line by line against <c>docs/performance.md</c>. What is left is
    /// alphabetical rather than in dictionary order, because a hash order changes between runs and a
    /// report that reorders itself looks like a report that changed.
    /// </remarks>
    public IReadOnlyList<string> Operations()
    {
        var measured = _operations.Keys.ToHashSet(StringComparer.Ordinal);

        var budgeted = PerformanceBudgets.All
            .Select(budget => budget.Operation)
            .Where(measured.Contains)
            .ToList();

        var rest = measured
            .Except(budgeted, StringComparer.Ordinal)
            .OrderBy(operation => operation, StringComparer.Ordinal);

        return [.. budgeted, .. rest];
    }

    /// <summary>The last <see cref="Remembered"/> runs of one operation.</summary>
    /// <remarks>
    /// Locked rather than lock-free. The contention is one operation's own requests, which
    /// <c>DeferredAnswers</c> already bounds to four at once, and the critical section is an array
    /// store -- so the cheaper structure would be bought with a reader that can observe a half-written
    /// history, in the one component whose job is to be trusted about numbers.
    /// </remarks>
    private sealed class Ring
    {
        private readonly double[] _samples = new double[Remembered];
        private readonly Lock _gate = new();

        private int _next;
        private int _held;

        public void Add(double milliseconds)
        {
            lock (_gate)
            {
                _samples[_next] = milliseconds;
                _next = (_next + 1) % Remembered;
                _held = Math.Min(_held + 1, Remembered);
            }
        }

        public LatencySample Sample()
        {
            lock (_gate)
            {
                // Oldest first, which matters only because it makes the copy describable; every
                // figure taken from it sorts anyway.
                var start = _held < Remembered ? 0 : _next;
                var ordered = new double[_held];

                for (var index = 0; index < _held; index++)
                {
                    ordered[index] = _samples[(start + index) % Remembered];
                }

                return new LatencySample(ordered);
            }
        }
    }
}
