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
