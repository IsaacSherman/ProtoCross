namespace ProtoCross.Projects;

/// <summary>A file one build of a project compiles, and the role it compiles in (spec 25.3.1).</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Role">
/// <see cref="SourceRole.Production"/> for a file <c>&lt;Sources&gt;</c> names, and
/// <see cref="SourceRole.Test"/> for one only <c>&lt;Tests&gt;</c> names.
/// </param>
public sealed record ProjectMember(string Path, SourceRole Role);
