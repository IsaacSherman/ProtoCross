using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Where a protobuf C++ install's CMake package is looked for, and that <c>protocross test</c> refuses an install
/// without one before it builds anything (spec 25.3.3).
/// </summary>
public class CMakeConfigPackageTests
{
    /// <summary>Each layout <c>find_package</c> reads, as vcpkg and system packages install them.</summary>
    [Theory]
    [InlineData("share/protobuf", "protobuf-config.cmake")]
    [InlineData("lib/cmake/protobuf", "ProtobufConfig.cmake")]
    [InlineData("lib/x86_64-linux-gnu/cmake/protobuf", "protobuf-config.cmake")]
    [InlineData("lib64/cmake/Protobuf", "protobuf-config.cmake")]
    [InlineData("share/protobuf-4.31/cmake", "protobuf-config.cmake")]
    [InlineData("protobuf/cmake", "protobuf-config.cmake")]
    [InlineData("Protobuf-6.33/cmake/protobuf", "ProtobufConfig.cmake")]
    [InlineData("Protobuf-6.33/lib/cmake/protobuf", "ProtobufConfig.cmake")]
    [InlineData("Protobuf-6.33/lib/protobuf/cmake", "ProtobufConfig.cmake")]
    [InlineData("protobuf/share/protobuf", "protobuf-config.cmake")]
    [InlineData("cmake", "ProtobufConfig.cmake")]
    [InlineData("", "protobuf-config.cmake")]
    public void APackageIsFoundWhereFindPackageLooks(string directory, string file)
    {
        var prefix = TestPaths.CreateTempDirectory();
        var package = Directory.CreateDirectory(Path.Combine(prefix, directory)).FullName;
        File.WriteAllText(Path.Combine(package, file), "# stands in for protobuf's package\n");

        Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(CMakeConfigPackage.Find(prefix, "Protobuf")!));
    }

    [Theory]
    [InlineData("share/protobuf", "protobuf-targets.cmake")]
    [InlineData("share/absl", "protobuf-config.cmake")]
    [InlineData("include/google/protobuf", "protobuf-config.cmake")]
    public void NothingElseIsTakenForThePackage(string directory, string file)
    {
        var prefix = TestPaths.CreateTempDirectory();
        var elsewhere = Directory.CreateDirectory(Path.Combine(prefix, directory)).FullName;
        File.WriteAllText(Path.Combine(elsewhere, file), "# not a package find_package reads\n");

        Assert.Null(CMakeConfigPackage.Find(prefix, "Protobuf"));
    }

    [Fact]
    public void AnInstallOfHeadersAloneIsNotBuildable()
    {
        var include = WriteHeaders(TestPaths.CreateTempDirectory());

        var headers = ToolLocator.FindProtobufCpp(TestPaths.CreateTempDirectory(), configuredInclude: include);
        var buildable = ToolLocator.FindBuildableProtobufCpp(TestPaths.CreateTempDirectory(), configuredInclude: include);

        Assert.True(headers.Found, headers.Missing);
        Assert.False(buildable.Found);
        Assert.Contains($"The protobuf C++ headers at '{include}' have no CMake package", buildable.Missing, StringComparison.Ordinal);
        Assert.Contains("PROTOCROSS_PROTOBUF_CPP_INCLUDE", buildable.Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallWithItsPackageIsBuildable()
    {
        var prefix = TestPaths.CreateTempDirectory();
        var include = WriteHeaders(prefix);
        var package = Directory.CreateDirectory(Path.Combine(prefix, "share", "protobuf")).FullName;
        File.WriteAllText(Path.Combine(package, "protobuf-config.cmake"), "# stands in for protobuf's package\n");

        var buildable = ToolLocator.FindBuildableProtobufCpp(TestPaths.CreateTempDirectory(), configuredInclude: include);

        Assert.True(buildable.Found, buildable.Missing);
        Assert.Equal(include, buildable.Tool.IncludeDirectory);
    }

    private static string WriteHeaders(string prefix)
    {
        var include = Path.Combine(prefix, "include");
        var headers = Directory.CreateDirectory(Path.Combine(include, "google", "protobuf"));
        File.WriteAllText(Path.Combine(headers.FullName, "message.h"), "// stands in for protobuf's header\n");
        return include;
    }
}
