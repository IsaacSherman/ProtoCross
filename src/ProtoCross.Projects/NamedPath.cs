using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>A file or directory a project names, resolved to a full path, and where the project names it.</summary>
/// <param name="Path">The full path, resolved against the project's directory when it was written relative.</param>
/// <param name="Span">Where the project file names it, so that a problem found with it later can point there.</param>
public sealed record NamedPath(string Path, SourceSpan Span);
