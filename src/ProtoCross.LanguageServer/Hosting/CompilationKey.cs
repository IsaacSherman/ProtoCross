using ProtoCross.LanguageServer.Workspace;

namespace ProtoCross.LanguageServer.Hosting;

/// <summary>What names one compilation among those a server keeps.</summary>
/// <remarks>
/// <para>
/// A document with a project is compiled with the rest of it, so its compilation is the project's, and
/// every open document of one project shares it: that is what lets edits to two of them within one
/// pause be one compile, and a caret moving into a second of them compile nothing. A document without a
/// project is a compilation of its own, as every document was before projects.
/// </para>
/// <para>
/// One statement of it, because the scheduler deciding what to compile and the cache deciding what it
/// holds must agree: a scheduler keyed one way and a cache keyed another would compile a project once per
/// open document and cache it once.
/// </para>
/// </remarks>
internal static class CompilationKey
{
    /// <param name="projectPath">
    /// The project file the document compiles with, whether or not it can be read or built, or null
    /// when it has none.
    /// </param>
    public static string For(DocumentUri document, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(document);

        // A project file's own URI key, which no document shares, since a project is not a source.
        return projectPath is not null && DocumentUri.TryParse(projectPath, out var project)
            ? project.Key
            : document.Key;
    }
}
