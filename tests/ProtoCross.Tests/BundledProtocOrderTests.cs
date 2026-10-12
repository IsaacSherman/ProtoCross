using ProtoCross.Binding;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which restored Grpc.Tools a machine with no protoc installed falls back to: the newest, judged by
/// the version each package folder is named for rather than by where the name falls in the alphabet.
/// </summary>
/// <remarks>
/// The order is tested on folder names given here rather than through
/// <see cref="ProtocLocator.FindBundledProtoc()"/>, which searches this machine's NuGet cache. Steering
/// that search means changing a process-wide environment variable, and every test running beside this
/// one would see it.
/// </remarks>
public class BundledProtocOrderTests
{
    private static string[] Preferred(params string[] folderNames)
        => [.. ProtocLocator.NewestFirst(folderNames.Select(name => Path.Combine("grpc.tools", name)))
            .Select(folder => Path.GetFileName(folder))];

    [Fact]
    public void TheFoldersFromTheIssueAreOrderedAsVersions()
    {
        Assert.Equal(
            ["2.100.0", "2.80.0", "2.80.0-pre1", "2.9.0"],
            Preferred("2.9.0", "2.80.0", "2.100.0", "2.80.0-pre1"));
    }

    [Fact]
    public void AComponentWithMoreDigitsIsTheNewerOne()
    {
        Assert.Equal(["2.100.0", "2.99.0"], Preferred("2.99.0", "2.100.0"));
    }

    [Fact]
    public void APrereleaseIsPreferredBelowItsOwnRelease()
    {
        Assert.Equal(["2.80.0", "2.80.0-pre1"], Preferred("2.80.0-pre1", "2.80.0"));
    }

    [Fact]
    public void TwoPrereleasesOfOneReleaseAreOrderedByTheirLabels()
    {
        Assert.Equal(["2.80.0-pre2", "2.80.0-pre1"], Preferred("2.80.0-pre1", "2.80.0-pre2"));
    }

    [Fact]
    public void APrereleaseOfANewerReleaseIsStillTheNewerOne()
    {
        Assert.Equal(["2.81.0-pre1", "2.80.0"], Preferred("2.80.0", "2.81.0-pre1"));
    }

    [Fact]
    public void AFolderWhoseNameIsNotAVersionIsPreferredBelowEveryOneWhoseNameIs()
    {
        Assert.Equal(
            ["2.9.0", "2.80.0-", "latest", "zzz"],
            Preferred("latest", "zzz", "2.9.0", "2.80.0-"));
    }

    [Fact]
    public void TheOrderDoesNotDependOnTheOrderTheFoldersAreListedIn()
    {
        string[] folders = ["2.9.0", "2.80.0", "2.100.0", "2.80.0-pre1", "latest"];
        var expected = Preferred(folders);

        foreach (var listing in Permutations(folders))
        {
            Assert.True(
                expected.SequenceEqual(Preferred(listing)),
                $"listed as {string.Join(", ", listing)}, the folders were preferred as "
                + $"{string.Join(", ", Preferred(listing))} rather than {string.Join(", ", expected)}");
        }
    }

    private static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var first = 0; first < items.Length; first++)
        {
            string[] rest = [.. items[..first], .. items[(first + 1)..]];
            foreach (var tail in Permutations(rest))
            {
                yield return [items[first], .. tail];
            }
        }
    }
}
