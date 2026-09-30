using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A completed invalidation must not be undone by a concurrent reader.</summary>
public class ProjectDiscoveryForgetRaceRegressionTests
{
    /// <summary>
    /// A watcher may invalidate between the reader's generation check and publication. Repeated
    /// synchronized races exercise that boundary without requiring a particular thread ordering.
    /// </summary>
    [Fact]
    public async Task AReadRacingWithForgetCannotRestoreTheForgottenValue()
    {
        const int Attempts = 20_000;
        var facts = new StampedFacts<int>();
        var stamp = new EntryStamp(DateTime.UtcNow.AddHours(-1), 10);
        using var phase = new Barrier(2);
        var version = 0;
        var stale = 0;
        var reader = Task.Factory.StartNew(() =>
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                Meet();
                facts.Get("entry", stamp, _ =>
                {
                    var observed = Volatile.Read(ref version);
                    Meet();
                    return observed;
                });
                Meet();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            facts.Clear();
            Meet();
            Meet();
            Interlocked.Increment(ref version);
            facts.Clear();
            Meet();
            if (facts.Get("entry", stamp, _ => Volatile.Read(ref version)) != version)
            {
                stale++;
            }
        }

        await reader;
        Assert.True(stale == 0,
            $"{stale} completed invalidations were undone by a reader publishing an older value.");

        void Meet() => Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(30)),
            "Both sides of the invalidation race must reach the next phase.");
    }
}
