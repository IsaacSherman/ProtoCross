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

    public static string RequireCMake() => Require(ToolLocator.FindCMake());

    public static CppCompiler RequireCppCompiler() => Require(ToolLocator.FindCppCompiler());

    public static CppCompiler RequireMsvc() => Require(ToolLocator.FindMsvc());

    public static ProtobufCppInstall RequireProtobufCpp() => Require(FindProtobufCpp());

    /// <summary>A protobuf C++ install that can link and run what it compiles, as well as parse it.</summary>
    public static ProtobufCppInstall RequireLinkableProtobufCpp()
    {
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
