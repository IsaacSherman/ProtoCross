using ProtoCross.Backend;

namespace ProtoCross.Cli;

/// <summary>What one backend generated for one directory, and which part of a build it is.</summary>
internal sealed record OutputGroup(string Directory, OutputRole Role, IReadOnlyList<GeneratedFile> Files);

/// <summary>Which part of a build a group of generated files is.</summary>
/// <remarks>
/// The program and its tests go to different places: <c>--out</c> and <c>--test-out</c> here, and two
/// output flags for a <c>protoc</c> plugin (#8). A test project is told apart from the tests beside it
/// because it is the one file an earlier build can have left there under another name.
/// </remarks>
internal enum OutputRole
{
    /// <summary>The generated program, under <c>--out</c>.</summary>
    Program,

    /// <summary>The generated tests, under <c>--test-out</c>.</summary>
    Tests,

    /// <summary>The build file <c>--scaffold</c> writes beside the tests.</summary>
    TestProject,
}
