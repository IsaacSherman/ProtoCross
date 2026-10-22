using ProtoCross.Binding;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests.Harness;

/// <summary>
/// The tools a test builds and runs generated code with, as <see cref="ToolLocator"/> finds them: each
/// one, or a skip that says what is missing and how to provide it.
/// </summary>
/// <remarks>
/// <para>
/// A skip rather than a failure, because a machine without a C++ toolchain is not a broken one. The
/// reason is the lookup's own, so a test that skips says why in the words the command line will use, and
/// no test words it for itself.
/// </para>
/// <para>
/// The <c>Locate</c> methods are for a test that handles a missing tool itself: a regression that has
/// to fail rather than skip without one, or a lookup whose answer is what is under test.
/// </para>
/// </remarks>
internal static class TestToolchain
{
    public static string RequireDotnet() => Require(ToolLocator.FindDotnet());

    public static string RequireProtoc() => Require(ToolLocator.FindProtoc());

    /// <summary>The protoc of the Grpc.Tools version this repository pins, for a test of one version's rules.</summary>
    /// <remarks>
    /// Never a protoc found on <c>PATH</c>, and never another version in the NuGet cache. What protoc accepts
    /// and generates changes between versions -- protoc 21.x escapes a shorter C++ keyword list, protoc 35 puts
    /// a macro between <c>class</c> and a message's name, and Homebrew's refuses an <c>@</c> in
    /// <c>csharp_namespace</c> -- so another version would fail a test for a reason that is not ProtoCross's.
    /// The newest version in a cache is whatever that machine last restored, which is not always what this
    /// commit pins.
    /// </remarks>
    public static string RequirePinnedProtoc()
    {
        var pinned = TestPaths.CentralPackageVersions["Grpc.Tools"];
        var protoc = ProtocLocator.FindBundledProtoc(pinned);
        if (protoc is null)
        {
            Assert.Skip($"No Grpc.Tools {pinned} protoc in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    public static string RequireCMake() => Require(ToolLocator.FindCMake());

    public static CppCompiler RequireCppCompiler() => Require(ToolLocator.FindCppCompiler());

    public static ProtobufCppInstall RequireProtobufCpp() => Require(FindProtobufCpp());

    /// <summary>
    /// A protobuf C++ install that can link and run what it compiles, as well as parse it, and the cmake
    /// <see cref="CppTestWorkspace.BuildAndRun"/> links it with.
    /// </summary>
    public static ProtobufCppInstall RequireLinkableProtobufCpp()
    {
        RequireCMake();
        var protobuf = RequireProtobufCpp();
        if (!protobuf.CanLink)
        {
            Assert.Skip(protobuf.DescribeMissingLinkInputs());
        }

        return protobuf;
    }

    /// <summary>A protobuf C++ install with the protoc built beside it, whose C++ its headers match.</summary>
    public static ProtobufCppInstall RequireProtobufCppWithProtoc()
    {
        var protobuf = RequireProtobufCpp();
        if (protobuf.ProtocPath is null)
        {
            Assert.Skip(
                $"The protobuf C++ install at '{protobuf.IncludeDirectory}' has no protoc beside it, and C++ "
                + "another protoc generates need not match its headers. A vcpkg install provides one.");
        }

        return protobuf;
    }

    /// <summary>The dotnet host, or null, for a test that fails rather than skips without one.</summary>
    public static string? LocateDotnet() => ToolLocator.FindDotnet().Tool;

    /// <summary>The protoc ProtoCross reads schemas with, or null, for a test that fails rather than skips without one.</summary>
    public static string? LocateProtoc() => ToolLocator.FindProtoc().Tool;

    /// <summary>
    /// The protobuf C++ install the suite builds against, found from the repository: its
    /// <c>vcpkg_installed</c> is where the repository's vcpkg manifest installs one.
    /// </summary>
    public static ToolLookup<ProtobufCppInstall> FindProtobufCpp(string? configuredInclude = null)
        => ToolLocator.FindProtobufCpp(TestPaths.RepositoryRoot, configuredInclude);

    /// <summary>Runs protoc for one language over one or more schemas below a proto path.</summary>
    public static ProcessResult RunProtoc(
        string protoc,
        string outputFlag,
        string protoPath,
        string outputDirectory,
        params string[] protoFiles)
    {
        var startInfo = ProcessRunner.Create(protoc);
        startInfo.ArgumentList.Add($"--proto_path={protoPath}");

        // The same trailing well-known include the compiler passes. Without it a schema importing
        // google/protobuf/*.proto compiles in ProtoCross and then fails here, generating the message
        // classes -- which would look like a compiler bug rather than a harness one.
        foreach (var wellKnown in ProtocLocator.FindWellKnownTypeIncludePaths(protoc))
        {
            startInfo.ArgumentList.Add($"--proto_path={wellKnown}");
        }

        startInfo.ArgumentList.Add($"--{outputFlag}={outputDirectory}");

        foreach (var file in protoFiles)
        {
            startInfo.ArgumentList.Add(file);
        }

        return ProcessRunner.Run(startInfo);
    }

    private static T Require<T>(ToolLookup<T> lookup)
        where T : class
    {
        if (!lookup.Found)
        {
            Assert.Skip(lookup.Missing);
        }

        return lookup.Tool;
    }
}
