using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public partial class ToolchainTests
{
    // ------- installs laid out off Windows

    /// <summary>
    /// A protoc and a CMake package are what a build links through, on every platform. Library file names
    /// are not asked for, because looking for vcpkg's Windows ones made every POSIX install headers-only.
    /// </summary>
    [Fact]
    public void AnInstallWithProtocAndACMakePackageCanLink()
    {
        var prefix = FakeInstall();
        var protoc = Touch(prefix, "bin", ToolLocator.ProtocExecutableName);
        Touch(prefix, "lib", "cmake", "protobuf", "protobuf-config.cmake");

        var install = LocateIn(prefix);

        Assert.True(install.CanLink, install.DescribeMissingLinkInputs());
        Assert.Equal(protoc, install.ProtocPath);
    }

    /// <summary>
    /// <c>libprotobuf</c> under each name a POSIX install gives it, in <c>lib</c> or in the multiarch directory
    /// a Debian package puts it in, and never a separate runtime directory, which is a Windows idea.
    /// </summary>
    [Theory]
    [InlineData("lib", "libprotobuf.a")]
    [InlineData("lib", "libprotobuf.so")]
    [InlineData("lib", "libprotobuf.dylib")]
    [InlineData("lib/x86_64-linux-gnu", "libprotobuf.so")]
    public void APosixLibraryIsFoundWhereItsInstallPutIt(string directory, string library)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows links libprotobuf.lib, so a POSIX library name is not one it reads.");
        }

        var prefix = FakeInstall();
        Touch(prefix, [.. directory.Split('/'), library]);

        var install = LocateIn(prefix);

        Assert.Equal(Path.Combine([prefix, .. directory.Split('/')]), install.LibraryDirectory);
        Assert.Null(install.BinaryDirectory);
    }

    /// <summary>A prefix holding protobuf's headers and nothing else yet.</summary>
    private static string FakeInstall()
    {
        var prefix = TestPaths.CreateTempDirectory();
        Touch(prefix, "include", "google", "protobuf", "message.h");
        return prefix;
    }

    private static ProtobufCppInstall LocateIn(string prefix)
    {
        var lookup = ToolLocator.FindProtobufCpp(prefix, configuredInclude: Path.Combine(prefix, "include"));
        Assert.True(lookup.Found, lookup.Missing);
        return lookup.Tool;
    }

    private static string Touch(string prefix, params string[] parts)
    {
        var path = Path.Combine([prefix, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
