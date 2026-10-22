namespace ProtoCross.Toolchain;

public enum CppCompilerKind
{
    ClangOrGcc,
    Msvc,
}

/// <summary>A C++ compiler, and for MSVC reached through Visual Studio, the script that sets it up.</summary>
public sealed record CppCompiler(
    string FileName,
    CppCompilerKind Kind,
    string DisplayName,
    string? VisualStudioDevCommand = null);

/// <summary>A protobuf C++ install: headers, and for linking, libraries and runtime binaries.</summary>
/// <param name="LibraryDirectory">Where <c>libprotobuf</c> is, under the platform's own file names.</param>
/// <param name="BinaryDirectory">
/// Where a DLL build's runtime libraries are, which only Windows has: elsewhere a shared library sits in
/// <paramref name="LibraryDirectory"/>.
/// </param>
public sealed record ProtobufCppInstall(
    string IncludeDirectory,
    string? ProtocPath,
    string? LibraryDirectory,
    string? BinaryDirectory)
{
    /// <summary>
    /// Whether this install has everything needed to compile protoc's C++ for its headers, link and run, not
    /// just to parse: the protoc built with it, and the CMake package a build links it through.
    /// </summary>
    /// <remarks>
    /// The package rather than the library files, because a program is linked through it: protobuf 22 and
    /// later need Abseil and <c>utf8_range</c> beside <c>libprotobuf</c>, in a set that varies by version,
    /// platform, and static or shared build, and only the package names it. Looking for vcpkg's Windows file
    /// names instead once found no POSIX install linkable at all (#18).
    /// </remarks>
    public bool CanLink => ProtocPath is not null && FindCMakePackage() is not null;

    /// <summary>The prefix the install is under: the include directory's parent.</summary>
    /// <remarks>
    /// What a CMake build is given as <c>CMAKE_PREFIX_PATH</c>, because <c>find_package</c> looks for the config
    /// package beside <c>&lt;prefix&gt;/include</c>, under <c>&lt;prefix&gt;/share</c> or <c>&lt;prefix&gt;/lib</c>.
    /// </remarks>
    public string Prefix => PrefixOf(IncludeDirectory) ?? IncludeDirectory;

    /// <summary>
    /// The prefix <paramref name="includeDirectory"/> is under, its parent, whether or not the path ends in a
    /// separator; null for a root, which has none.
    /// </summary>
    /// <remarks>
    /// The separator comes off first because <see cref="Directory.GetParent"/> takes <c>x/include/</c> for a
    /// path inside <c>x/include</c> and answers <c>x/include</c> itself. An override written with a final
    /// separator then named an install whose package, libraries and protoc were all looked for in the wrong
    /// place, and a whole install was refused as headers alone.
    /// </remarks>
    public static string? PrefixOf(string includeDirectory)
        => Directory.GetParent(Path.TrimEndingDirectorySeparator(includeDirectory))?.FullName;

    /// <summary>
    /// The directory of the CMake package that <c>find_package(Protobuf CONFIG)</c> reads under
    /// <see cref="Prefix"/>, or null when the install has none.
    /// </summary>
    /// <remarks>
    /// A generated CMake project finds protobuf's libraries, its Abseil and <c>utf8_range</c> dependencies and
    /// protoc only through this package, so an install without one cannot build it, whatever headers it has.
    /// </remarks>
    public string? FindCMakePackage() => CMakeConfigPackage.Find(Prefix, "Protobuf");

    /// <summary>Why an install with no CMake package cannot build a generated CMake project, and what would.</summary>
    public string DescribeMissingCMakePackage()
        => $"The protobuf C++ headers at '{IncludeDirectory}' have no CMake package beside them: no "
            + $"ProtobufConfig.cmake or protobuf-config.cmake under '{Prefix}', which the generated CMake project "
            + "finds protobuf's libraries, their dependencies and protoc through. A vcpkg install provides one; set "
            + "PROTOCROSS_PROTOBUF_CPP_INCLUDE to the include directory of one to use it.";

    /// <summary>
    /// What is missing when <see cref="CanLink"/> is false, named so that whoever reads it learns which
    /// piece of a real install was not found rather than the requirement restated in general.
    /// </summary>
    public string DescribeMissingLinkInputs()
    {
        var missing = new List<string>();

        if (ProtocPath is null)
        {
            missing.Add("matching protoc");
        }

        if (FindCMakePackage() is null)
        {
            missing.Add($"CMake package (ProtobufConfig.cmake or protobuf-config.cmake under '{Prefix}')");
        }

        return $"The protobuf C++ headers at '{IncludeDirectory}' have no {string.Join(" and no ", missing)} "
            + "beside them. A vcpkg or Homebrew install provides both; set PROTOCROSS_PROTOBUF_CPP_INCLUDE "
            + "to the include directory of one to use it.";
    }
}
