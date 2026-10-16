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
