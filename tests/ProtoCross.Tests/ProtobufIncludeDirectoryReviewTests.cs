using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public class ProtobufIncludeDirectoryReviewTests
{
    /// <summary>The install prefix is the include directory's parent, with or without a final separator.</summary>
    [Fact]
    public void ATrailingSeparatorDoesNotMoveTheProtobufInstallPrefix()
    {
        var prefix = TestPaths.CreateTempDirectory();
        var include = Directory.CreateDirectory(Path.Combine(prefix, "include")).FullName;
        var install = new ProtobufCppInstall(include, null, null, null);
        Assert.Equal(prefix, install.Prefix);

        var withSeparator = install with { IncludeDirectory = include + Path.DirectorySeparatorChar };

        Assert.Equal(prefix, withSeparator.Prefix);
    }
}
