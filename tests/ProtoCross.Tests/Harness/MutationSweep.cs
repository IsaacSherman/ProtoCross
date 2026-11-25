using System.Runtime.ExceptionServices;

namespace ProtoCross.Tests.Harness;

/// <summary>
/// Runs one check per input of a mutation sweep, spread across every core, until the sweep is told
/// to stop.
/// </summary>
/// <remarks>
/// <para>
/// A resilience sweep compiles its file once per character of it, so its cost is the square of the
/// file's length, and on one core the sweeps over the corpus were among the longest tests in the
/// suite. Every input gets a lexer, a parser and a binder of its own, and nothing the compiler holds
/// is shared between them except immutable descriptors, so the inputs are independent: the order
/// they ran in only ever decided which failure was reported first.
/// </para>
/// <para>
/// Taking every core is also why each class that sweeps belongs to the "Timing-sensitive
/// regressions" collection. That collection runs alone, after everything else, so a sweep never
/// shares the machine with a test that has a deadline to meet.
/// </para>
/// <para>
/// A failing check stops the sweep, and the test reports that check's own exception rather than the
/// <see cref="AggregateException"/> the parallel loop wraps it in, so the failure still says what
/// went wrong on its first line.
/// </para>
/// </remarks>
internal static class MutationSweep
{
    /// <summary>How many inputs one hang guard covers.</summary>
    private const int BatchSize = 128;

    /// <summary>
    /// The indexes from <paramref name="from"/> up to, not including, <paramref name="to"/>, in
    /// batches that each get a hang guard of their own.
    /// </summary>
    /// <remarks>
    /// A budget for a whole sweep is a deadline on the length of the longest file in the corpus,
    /// because the sweep's cost is the square of it, and every vector written brings that deadline
    /// closer. The parser's sweep of <c>repeated_indexing.pcross</c>, 25 KB, takes 9 seconds alone
    /// against a 30-second budget. It ran past that in a run that took twice as long as the one before
    /// it, and with four sweeps sharing the machine it takes 40 (#226). A batch is a fixed amount of
    /// work, so a long file takes more batches rather than longer ones, and a stuck check still fails
    /// within one batch's budget.
    /// </remarks>
    public static IEnumerable<(int First, int End)> Batches(int from, int to)
    {
        for (var first = from; first < to; first += BatchSize)
        {
            yield return (first, Math.Min(first + BatchSize, to));
        }
    }

    /// <summary>Checks every index from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
    public static void For(int from, int to, CancellationToken stop, Action<int> check)
    {
        try
        {
            Parallel.For(from, to, (index, loop) =>
            {
                if (stop.IsCancellationRequested)
                {
                    loop.Stop();
                    return;
                }

                check(index);
            });
        }
        catch (AggregateException failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerExceptions[0]);
        }
    }
}
