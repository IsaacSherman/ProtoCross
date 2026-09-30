using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Temporary inability to read a project must not become its remembered contents.</summary>
public class ProjectDiscoveryReadFailureRegressionTests
{
    /// <summary>
    /// Releasing a reader's exclusive handle changes neither the project's text nor its stamp, so
    /// discovery must recover without waiting for another edit or a file-change notification.
    /// </summary>
    [Fact]
    public void AProjectBecomesReadableAfterItsTemporaryLockIsReleased()
    {
        var directory = TestPaths.CreateTempDirectory();
        var path = TestPaths.WriteSources(directory,
            ("billing.pcproj", "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>"))[0];
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var stamp = EntryStamp.OfFile(path);

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(stamp, EntryStamp.OfFile(path));
            var unreadable = ProjectDiscovery.Read(path);
            Assert.Null(unreadable.Project);
            Assert.NotEmpty(unreadable.Problems);
        }

        Assert.Equal(stamp, EntryStamp.OfFile(path));
        Assert.NotNull(ProjectClaim.Read(path).Project);
        var recovered = ProjectDiscovery.Read(path);

        Assert.True(recovered.Project is not null,
            "Discovery must retry a transient read failure once the project can be read again.");
        Assert.Empty(recovered.Problems);
    }
}
