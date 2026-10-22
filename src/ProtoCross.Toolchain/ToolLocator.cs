using System.Runtime.InteropServices;
using ProtoCross.Binding;

namespace ProtoCross.Toolchain;

/// <summary>
/// Finds the tools generated code is built and run with: dotnet, protoc, cmake, a C++ compiler, and a
/// protobuf C++ install.
/// </summary>
/// <remarks>
/// <para>
/// Each lookup answers with the tool, or with why there is none and what would provide one
/// (<see cref="ToolLookup{T}"/>). A test skips with that reason, and the command line will print it and
/// stop. Both read it from here, so a missing tool is described in one set of words wherever it is found
/// missing.
/// </para>
/// <para>
/// None of it throws: a machine without a C++ toolchain is an ordinary machine, and finding nothing is
/// an answer rather than an error.
/// </para>
/// </remarks>
public static class ToolLocator
{
    public static string ProtocExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "protoc.exe" : "protoc";

    /// <summary>The protoc ProtoCross reads schemas with, found as <see cref="ProtocLocator"/> finds it.</summary>
    public static ToolLookup<string> FindProtoc()
        => ProtocLocator.Locate() is { } protoc
            ? ToolLookup<string>.Of(protoc)
            : ToolLookup<string>.None(ProtocLocator.DescribeNotFound());

    public static ToolLookup<string> FindDotnet()
    {
        // The SDK exports this to the processes it starts, and it is the only entry that is
        // guaranteed to be the host actually running us.
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return ToolLookup<string>.Of(hostPath);
        }

        return ProcessRunner.FindOnPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet") is { } onPath
            ? ToolLookup<string>.Of(onPath)
            : ToolLookup<string>.None("No dotnet host found. Install the .NET SDK and put dotnet on PATH, or set DOTNET_HOST_PATH to it.");
    }

    /// <summary>
    /// Finds cmake, which the generated C++ test project is built with. Visual Studio ships one but does
    /// not put it on PATH, so an otherwise fully equipped Windows machine would go without.
    /// </summary>
    public static ToolLookup<string> FindCMake()
    {
        var onPath = ProcessRunner.FindOnPath(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmake.exe" : "cmake");

        if (onPath is not null)
        {
            return ToolLookup<string>.Of(onPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && VisualStudioCMakeCandidates().FirstOrDefault(File.Exists) is { } bundled)
        {
            return ToolLookup<string>.Of(bundled);
        }

        return ToolLookup<string>.None("No cmake found. Install CMake and put it on PATH, or install Visual Studio's C++ workload.");
    }

    public static ToolLookup<CppCompiler> FindCppCompiler()
    {
        if (ProcessRunner.FindOnPath("clang++") is { } clang)
        {
            return ToolLookup<CppCompiler>.Of(new CppCompiler(clang, CppCompilerKind.ClangOrGcc, "clang++"));
        }

        if (ProcessRunner.FindOnPath("g++") is { } gcc)
        {
            return ToolLookup<CppCompiler>.Of(new CppCompiler(gcc, CppCompilerKind.ClangOrGcc, "g++"));
        }

        return FindMsvc() is { Found: true } msvc
            ? msvc
            : ToolLookup<CppCompiler>.None("No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
    }

    public static ToolLookup<CppCompiler> FindMsvc()
    {
        var msvc = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProcessRunner.FindOnPath("cl.exe")
            : ProcessRunner.FindOnPath("cl");

        if (msvc is not null)
        {
            return ToolLookup<CppCompiler>.Of(new CppCompiler(msvc, CppCompilerKind.Msvc, "cl.exe"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && VisualStudioDevCommandCandidates().FirstOrDefault(File.Exists) is { } devCommand)
        {
            return ToolLookup<CppCompiler>.Of(
                new CppCompiler("cl.exe", CppCompilerKind.Msvc, "MSVC via VsDevCmd.bat", devCommand));
        }

        return ToolLookup<CppCompiler>.None(
            "No Visual Studio C++ Build Tools found: cl.exe is not on PATH, and there is no VsDevCmd.bat "
            + "to set it up. Install Visual Studio's C++ workload or the Build Tools.");
    }

    /// <summary>
    /// Finds a protobuf C++ install for code generated below <paramref name="searchFrom"/>.
    /// </summary>
    /// <param name="searchFrom">
    /// Where the build is: a project's directory, or the repository a test suite runs in. A
    /// <c>vcpkg_installed</c> there or in any directory above it is searched, nearest first, because that
    /// is where vcpkg's manifest mode installs beside the <c>vcpkg.json</c> that asked for it.
    /// </param>
    /// <param name="configuredInclude">
    /// Stands in for <c>PROTOCROSS_PROTOBUF_CPP_INCLUDE</c>. A parameter rather than a variable a test
    /// mutates, because the rest of the suite reads that variable concurrently.
    /// </param>
    /// <remarks>
    /// After the stated include directory, it searches <c>VCPKG_INSTALLED_DIR</c> and the
    /// <c>vcpkg_installed</c> directories, then <c>VCPKG_ROOT</c>'s classic-mode installs, then each
    /// <c>CMAKE_PREFIX_PATH</c> prefix, then the places a system package puts one. Nearest first, so that the install a project asked for wins
    /// over one that merely happens to be on the machine.
    /// </remarks>
    public static ToolLookup<ProtobufCppInstall> FindProtobufCpp(string searchFrom, string? configuredInclude = null)
    {
        ArgumentNullException.ThrowIfNull(searchFrom);

        var configured = configuredInclude
            ?? Environment.GetEnvironmentVariable("PROTOCROSS_PROTOBUF_CPP_INCLUDE");

        if (!string.IsNullOrWhiteSpace(configured) && ContainsProtobufCppHeaders(configured))
        {
            // Derived the same way as a discovered directory, deliberately. Returning the headers
            // alone would make setting the documented override *worse* than setting nothing: the
            // link-and-run and conformance suites would skip on a machine that is fully equipped,
            // because they need the libraries and protoc that sit beside those headers.
            return ToolLookup<ProtobufCppInstall>.Of(CreateProtobufCppInstall(configured));
        }

        IReadOnlyList<string> candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ?
            [
                .. VcpkgInstalledIncludeCandidates(searchFrom),
                .. CMakePrefixIncludeCandidates(),
                @"C:\vcpkg\installed\x64-windows\include",
                @"C:\tools\vcpkg\installed\x64-windows\include",
                @"C:\Program Files\protobuf\include",
                @"C:\Program Files (x86)\protobuf\include",
            ]
            :
            [
                .. VcpkgInstalledIncludeCandidates(searchFrom),
                .. CMakePrefixIncludeCandidates(),
                "/usr/local/include",
                "/usr/include",
                "/opt/homebrew/include",
            ];

        return candidates.FirstOrDefault(ContainsProtobufCppHeaders) is { } found
            ? ToolLookup<ProtobufCppInstall>.Of(CreateProtobufCppInstall(found))
            : ToolLookup<ProtobufCppInstall>.None(
                $"No protobuf C++ install found: none in a vcpkg_installed at or above '{searchFrom}', under "
                + "VCPKG_ROOT or CMAKE_PREFIX_PATH, or where a system package puts one. Install protobuf with "
                + "vcpkg, or set PROTOCROSS_PROTOBUF_CPP_INCLUDE to the include directory of an install.");
    }

    /// <summary>
    /// Finds a protobuf C++ install, as <see cref="FindProtobufCpp"/> does, that a generated CMake project can
    /// build against: one with the CMake package <c>find_package(Protobuf CONFIG)</c> reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Headers alone are enough for <see cref="FindProtobufCpp"/>, which also serves the suite's parse-only
    /// checks, and are not enough to build. Found that way, an install of headers alone let
    /// <c>protocross test</c> write its output and then fail in cmake's configure, where spec 25.3.3 says a
    /// missing tool is exit 2 and writes nothing.
    /// </para>
    /// <para>
    /// Only the package, where <see cref="ProtobufCppInstall.CanLink"/> also asks for a protoc beside the
    /// headers: the generated project finds protoc through the package, and the suite runs one itself.
    /// </para>
    /// </remarks>
    public static ToolLookup<ProtobufCppInstall> FindBuildableProtobufCpp(string searchFrom, string? configuredInclude = null)
    {
        var lookup = FindProtobufCpp(searchFrom, configuredInclude);
        return lookup.Found && lookup.Tool.FindCMakePackage() is null
            ? ToolLookup<ProtobufCppInstall>.None(lookup.Tool.DescribeMissingCMakePackage())
            : lookup;
    }

    private static ProtobufCppInstall CreateProtobufCppInstall(string includeDirectory)
    {
        if (ProtobufCppInstall.PrefixOf(includeDirectory) is not { } prefix)
        {
            return new ProtobufCppInstall(includeDirectory, null, null, null);
        }

        return new ProtobufCppInstall(
            includeDirectory,
            LocateProtocUnder(prefix),
            LocateLibraryDirectoryUnder(prefix),
            LocateRuntimeDirectoryUnder(prefix));
    }

    private static bool ContainsProtobufCppHeaders(string includeDirectory)
        => File.Exists(Path.Combine(includeDirectory, "google", "protobuf", "message.h"));

    /// <summary>The protoc built with the install: under vcpkg's tools directory, or in the prefix's <c>bin</c>.</summary>
    /// <remarks>
    /// vcpkg keeps executables out of <c>bin</c>, which on Windows holds its DLLs; Homebrew, a system package,
    /// and protobuf's own CMake install put protoc in <c>bin</c> beside the headers' <c>include</c>.
    /// </remarks>
    private static string? LocateProtocUnder(string prefix)
        => new[]
            {
                Path.Combine(prefix, "tools", "protobuf", ProtocExecutableName),
                Path.Combine(prefix, "bin", ProtocExecutableName),
            }
            .FirstOrDefault(File.Exists);

    /// <summary>The directory holding <c>libprotobuf</c> under the prefix: <c>lib</c>, or a multiarch one below it.</summary>
    /// <remarks>
    /// Debian and Ubuntu put a package's libraries in <c>lib/&lt;triplet&gt;</c>, such as
    /// <c>lib/x86_64-linux-gnu</c>, rather than in <c>lib</c> itself.
    /// </remarks>
    private static string? LocateLibraryDirectoryUnder(string prefix)
    {
        var lib = Path.Combine(prefix, "lib");
        return new[] { lib }.Concat(CMakeConfigPackage.Subdirectories(lib)).FirstOrDefault(
            directory => ProtobufLibraryNames().Any(name => File.Exists(Path.Combine(directory, name))));
    }

    /// <summary>The file names <c>libprotobuf</c> is linked from on this platform.</summary>
    /// <remarks>
    /// On Windows the import library of a DLL build and a static library share one name. Elsewhere the archive
    /// and the shared object differ, and either one links.
    /// </remarks>
    private static IEnumerable<string> ProtobufLibraryNames()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["libprotobuf.lib"]
            : ["libprotobuf.a", "libprotobuf.so", "libprotobuf.dylib"];

    /// <summary>The directory a program built against a DLL build needs on <c>PATH</c>: Windows only.</summary>
    /// <remarks>
    /// A shared library elsewhere is found from <see cref="ProtobufCppInstall.LibraryDirectory"/> or the rpath
    /// the build wrote, so no POSIX install has one.
    /// </remarks>
    private static string? LocateRuntimeDirectoryUnder(string prefix)
    {
        var bin = Path.Combine(prefix, "bin");
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(Path.Combine(bin, "libprotobuf.dll"))
            ? bin
            : null;
    }

    private static IEnumerable<string> VcpkgInstalledIncludeCandidates(string searchFrom)
    {
        var roots = new List<string>();

        var installedDir = Environment.GetEnvironmentVariable("VCPKG_INSTALLED_DIR");
        if (!string.IsNullOrWhiteSpace(installedDir))
        {
            roots.Add(installedDir);
        }

        for (var directory = new DirectoryInfo(Path.GetFullPath(searchFrom)); directory is not null; directory = directory.Parent)
        {
            var manifestInstall = Path.Combine(directory.FullName, "vcpkg_installed");
            if (Directory.Exists(manifestInstall))
            {
                roots.Add(manifestInstall);
            }
        }

        var vcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT");
        if (!string.IsNullOrWhiteSpace(vcpkgRoot))
        {
            roots.Add(Path.Combine(vcpkgRoot, "installed"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var triplet in CandidateVcpkgTriplets())
            {
                yield return Path.Combine(root, triplet, "include");
            }
        }
    }

    /// <summary>The include directory of each prefix <c>CMAKE_PREFIX_PATH</c> names, in its order.</summary>
    /// <remarks>
    /// The prefixes the generated <c>CMakeLists.txt</c>'s <c>find_package(Protobuf)</c> searches, so an
    /// install CMake would build against is one this finds too.
    /// </remarks>
    private static IEnumerable<string> CMakePrefixIncludeCandidates()
        => (Environment.GetEnvironmentVariable("CMAKE_PREFIX_PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(prefix => Path.Combine(prefix, "include"));

    private static IEnumerable<string> CandidateVcpkgTriplets()
    {
        var configured = Environment.GetEnvironmentVariable("VCPKG_DEFAULT_TRIPLET");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "x64-windows";
            yield return "x64-windows-static";
            yield return "x64-windows-static-md";
            yield return "x86-windows";
            yield return "arm64-windows";
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-osx" : "x64-osx";
            yield break;
        }

        yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-linux" : "x64-linux";
    }

    private static IEnumerable<string> VisualStudioCMakeCandidates()
        => VisualStudioInstallations().Select(installation => Path.Combine(
            installation, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe"));

    private static IEnumerable<string> VisualStudioDevCommandCandidates()
        => VisualStudioInstallations().Select(installation => Path.Combine(installation, "Common7", "Tools", "VsDevCmd.bat"));

    /// <summary>Where each Visual Studio version and edition installs, whether or not it is there.</summary>
    private static IEnumerable<string> VisualStudioInstallations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] versions = ["18", "2022", "17"];
        string[] editions = ["Community", "BuildTools", "Professional", "Enterprise"];

        foreach (var root in new[] { programFiles, programFilesX86 }.Where(root => !string.IsNullOrEmpty(root)))
        {
            foreach (var version in versions)
            {
                foreach (var edition in editions)
                {
                    yield return Path.Combine(root, "Microsoft Visual Studio", version, edition);
                }
            }
        }
    }
}
