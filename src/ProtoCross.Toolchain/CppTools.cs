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
public sealed record ProtobufCppInstall(
    string IncludeDirectory,
    string? ProtocPath,
    string? LibraryDirectory,
    string? BinaryDirectory)
{
    /// <summary>Whether this install has everything needed to link and run, not just to parse.</summary>
    public bool CanLink => ProtocPath is not null && LibraryDirectory is not null && BinaryDirectory is not null;

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
            missing.Add("a matching protoc");
        }

        if (LibraryDirectory is null)
        {
            missing.Add("import libraries");
        }

        if (BinaryDirectory is null)
        {
            missing.Add("runtime binaries");
        }

        return $"The protobuf C++ headers at '{IncludeDirectory}' have no {string.Join(", ", missing)} "
            + "beside them. A vcpkg install provides all of them; set PROTOCROSS_PROTOBUF_CPP_INCLUDE "
            + "to the include directory of one to use it.";
    }
}
