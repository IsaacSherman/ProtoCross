using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What project discovery remembers between questions (#106): what a directory held and what a project
/// said, kept while a stat says the entry has not changed, and read again the moment it has.
/// </summary>
/// <remarks>
/// The rules are asked of a <see cref="StampedFacts{T}"/> of the test's own, with stamps written as
/// values, so that nothing here depends on the clock or on another test clearing the one discovery
/// shares. Discovery itself is asked only what must hold whatever it remembers: that a change is seen.
/// </remarks>
public class ProjectDiscoveryMemoryTests
{
    private static readonly EntryStamp Settled = new(DateTime.UtcNow.AddHours(-1), 10);

    /// <summary>A reader that counts how often it was asked, answering with the count.</summary>
    private sealed class CountingReader
    {
        public int Reads { get; private set; }

        public int Read(string path) => ++Reads;
    }

    // ------- the rules

    /// <summary>An entry whose stamp has not moved is answered from what was read, without reading it again.</summary>
    [Fact]
    public void AnEntryWhoseStampHasNotMovedIsNotReadAgain()
    {
        var facts = new StampedFacts<int>();
        var reader = new CountingReader();

        facts.Get("entry", Settled, reader.Read);
        var second = facts.Get("entry", Settled, reader.Read);

        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, second);
    }

    /// <summary>An entry whose stamp has moved, in its time or its length, is read again.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void AnEntryWhoseStampHasMovedIsReadAgain(int laterBySeconds, int longerBy)
    {
        var facts = new StampedFacts<int>();
        var reader = new CountingReader();

        facts.Get("entry", Settled, reader.Read);
        var moved = Settled with { Written = Settled.Written.AddSeconds(laterBySeconds), Length = Settled.Length + longerBy };
        var second = facts.Get("entry", moved, reader.Read);

        Assert.Equal(2, second);
    }

    /// <summary>
    /// What was read under a stamp too recent to trust is not kept, since the entry could still change
    /// within the same tick of the clock that stamped it and show no change.
    /// </summary>
    [Fact]
    public void AStampTooRecentToTrustIsReadAgain()
    {
        var facts = new StampedFacts<int>();
        var reader = new CountingReader();
        var recent = new EntryStamp(DateTime.UtcNow.AddHours(1), 10);

        facts.Get("entry", recent, reader.Read);
        facts.Get("entry", recent, reader.Read);

        Assert.Equal(2, reader.Reads);
    }

    /// <summary>Clearing forgets everything, so an entry is read again although its stamp has not moved.</summary>
    [Fact]
    public void ClearingForgetsWhatWasRead()
    {
        var facts = new StampedFacts<int>();
        var reader = new CountingReader();

        facts.Get("entry", Settled, reader.Read);
        facts.Clear();
        facts.Get("entry", Settled, reader.Read);

        Assert.Equal(2, reader.Reads);
    }

    // ------- changes are seen

    private static string Project(string pattern) => $"<ProtoCrossProject><Sources Include=\"{pattern}\" /></ProtoCrossProject>";

    /// <summary>
    /// A project added to a directory whose listing was already remembered is found, because adding it
    /// moves the directory's stamp.
    /// </summary>
    [Fact]
    public void AProjectAddedToADirectoryAlreadyListedIsFound()
    {
        var directory = TestPaths.CreateTempDirectory();
        var document = TestPaths.WriteSources(directory, ("doc.pcross", string.Empty))[0];
        Directory.SetLastWriteTimeUtc(directory, Settled.Written);
        Assert.Null(ProjectDiscovery.Find(document));

        TestPaths.WriteSources(directory, ("billing.pcproj", Project("*.pcross")));

        Assert.NotNull(ProjectDiscovery.Find(document));
    }

    /// <summary>A project edited after it was remembered is read again, because editing it moves its stamp.</summary>
    [Fact]
    public void AProjectEditedAfterItWasReadIsReadAgain()
    {
        var directory = TestPaths.CreateTempDirectory();
        var (document, project) = (
            TestPaths.WriteSources(directory, ("doc.pcross", string.Empty))[0],
            TestPaths.WriteSources(directory, ("billing.pcproj", Project("other/*.pcross")))[0]);
        File.SetLastWriteTimeUtc(project, Settled.Written);
        Directory.SetLastWriteTimeUtc(directory, Settled.Written);
        Assert.Null(ProjectDiscovery.Find(document));

        File.WriteAllText(project, Project("*.pcross"));

        Assert.NotNull(ProjectDiscovery.Find(document));
    }
}
