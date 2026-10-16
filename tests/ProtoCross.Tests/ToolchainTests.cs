using System.Runtime.InteropServices;
using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Tests for the toolchain discovery the C++ suites, and the command line's test verb, depend on. What
/// makes these worth writing is that a bug here does not fail anything: it makes a test skip, which reads
/// as "this machine is not equipped" whether or not that is true.
/// </summary>
public class ToolchainTests
{
    /// <summary>
    /// Setting the documented override must not leave the toolchain looking less capable than
    /// leaving it unset. It once returned the headers alone, which silently downgraded the
    /// link-and-run and conformance suites to a skip on a fully equipped machine -- and a skip is
    /// exactly the failure mode nobody investigates.
    /// </summary>
    [Fact]
    public void TheIncludeOverrideKeepsTheRestOfTheInstall()
    {
        var discovered = TestToolchain.RequireLinkableProtobufCpp();

        var overridden = TestToolchain.FindProtobufCpp(discovered.IncludeDirectory).Tool;

        Assert.NotNull(overridden);
        Assert.True(
            overridden.CanLink,
            "Pointing PROTOCROSS_PROTOBUF_CPP_INCLUDE at an install that can link produced one that "
            + $"cannot: {overridden.DescribeMissingLinkInputs()}");

        // Records compare by value, so this asserts the override found the same protoc, libraries,
        // and runtime binaries, not merely that it found some.
        Assert.Equal(discovered, overridden);
    }

    /// <summary>
    /// An override that does not actually contain protobuf headers is ignored rather than believed,
    /// so a stale variable in someone's shell profile does not disable the C++ suites outright.
    /// </summary>
    [Fact]
    public void AnOverrideWithoutHeadersFallsBackToDiscovery()
    {
        var discovered = TestToolchain.FindProtobufCpp();

        Assert.Equal(discovered, TestToolchain.FindProtobufCpp(Path.GetTempPath()));
    }

    [Fact]
    public void MissingLinkInputsAreNamedIndividually()
    {
        var headersOnly = new ProtobufCppInstall("/somewhere/include", null, null, null);

        Assert.False(headersOnly.CanLink);

        var description = headersOnly.DescribeMissingLinkInputs();
        Assert.Contains("/somewhere/include", description, StringComparison.Ordinal);
        Assert.Contains("a matching protoc", description, StringComparison.Ordinal);
        Assert.Contains("import libraries", description, StringComparison.Ordinal);
        Assert.Contains("runtime binaries", description, StringComparison.Ordinal);
    }

    // ------- where a project's install is found

    /// <summary>
    /// A <c>vcpkg_installed</c> above the directory a build is in is found, which is where vcpkg's
    /// manifest mode puts one beside the <c>vcpkg.json</c> that asked for it. A consumer's repository is
    /// laid out like that, and the search used to look only in this one's.
    /// </summary>
    [Fact]
    public void AVcpkgInstallAboveTheBuildIsFound()
    {
        var repository = TestPaths.CreateTempDirectory();
        var install = WriteVcpkgInstall(repository);
        var project = Directory.CreateDirectory(Path.Combine(repository, "billing", "rules")).FullName;

        var found = ToolLocator.FindProtobufCpp(project, configuredInclude: NoHeaders());

        Assert.True(found.Found, found.Missing);
        Assert.StartsWith(install, found.Tool.IncludeDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Of two <c>vcpkg_installed</c> directories above a build, the nearer wins: it is the install the
    /// nearest manifest asked for, and the farther one belongs to something that merely contains it.
    /// </summary>
    [Fact]
    public void TheNearestVcpkgInstallWins()
    {
        var outer = TestPaths.CreateTempDirectory();
        WriteVcpkgInstall(outer);
        var inner = Directory.CreateDirectory(Path.Combine(outer, "billing")).FullName;
        var nearer = WriteVcpkgInstall(inner);
        var project = Directory.CreateDirectory(Path.Combine(inner, "rules")).FullName;

        var found = ToolLocator.FindProtobufCpp(project, configuredInclude: NoHeaders());

        Assert.True(found.Found, found.Missing);
        Assert.StartsWith(nearer, found.Tool.IncludeDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stated include directory with no headers in it, so that a lookup under test ignores
    /// <c>PROTOCROSS_PROTOBUF_CPP_INCLUDE</c> without the suite changing a variable it reads concurrently.
    /// </summary>
    /// <remarks>
    /// A <c>VCPKG_INSTALLED_DIR</c> is searched before any <c>vcpkg_installed</c>, so where one is set,
    /// the tests of the upward search skip rather than test it.
    /// </remarks>
    private static string NoHeaders()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VCPKG_INSTALLED_DIR")))
        {
            Assert.Skip("VCPKG_INSTALLED_DIR is set, and it is searched before any vcpkg_installed directory.");
        }

        return TestPaths.CreateTempDirectory();
    }

    /// <summary>
    /// Lays out the headers of a vcpkg install in <c>vcpkg_installed</c> under <paramref name="directory"/>,
    /// for the triplet this machine's search tries first, and returns the install's own directory.
    /// </summary>
    private static string WriteVcpkgInstall(string directory)
    {
        var install = Path.Combine(directory, "vcpkg_installed");
        var triplet = Environment.GetEnvironmentVariable("VCPKG_DEFAULT_TRIPLET") is { Length: > 0 } configured
            ? configured
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "x64-windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-osx" : "x64-osx"
                : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-linux" : "x64-linux";
        var headers = Directory.CreateDirectory(Path.Combine(install, triplet, "include", "google", "protobuf"));
        File.WriteAllText(Path.Combine(headers.FullName, "message.h"), "// stands in for protobuf's header\n");
        return install;
    }
}
