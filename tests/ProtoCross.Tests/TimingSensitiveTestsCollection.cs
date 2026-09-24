using Xunit;

namespace ProtoCross.Tests;

// These tests exercise their own concurrency and hang guards. Unrelated compiler builds and
// mutation sweeps must not consume the scheduling budget needed to reach their assertions. The
// mutation sweeps are in here too, for the same reason seen from the other side: each takes every
// core (see MutationSweep), so it runs where it cannot take them from a test with a deadline.
[CollectionDefinition("Timing-sensitive regressions", DisableParallelization = true)]
public sealed class TimingSensitiveTestsCollection;
