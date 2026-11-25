namespace ProtoCross.Tests.Harness;

/// <summary>How long a test waits for something that should happen before it calls the wait a hang.</summary>
/// <remarks>
/// <para>
/// A wait for something that should happen can only detect a hang. It ends the moment the thing
/// happens, so a passing run waits no longer under a generous bound than under a tight one, and the
/// bound has only to outlast the worst delay a loaded run imposes. The tighter bounds this suite gave
/// such waits failed in loaded runs and passed alone: five seconds for a descriptor load's second
/// thread, five and then fifteen for an ordered request behind three edits, and one second for the
/// first compile of a fresh process, which took 1.4 seconds beside a full run (#226).
/// </para>
/// <para>
/// One home, because the shorter copies were the ones that flaked. A wait whose property is that
/// something does <em>not</em> happen is the other kind: load can only make it pass, so it keeps a
/// short bound of its own and does not belong here.
/// </para>
/// </remarks>
internal static class Liveness
{
    public static TimeSpan Patience { get; } = TimeSpan.FromSeconds(30);
}
