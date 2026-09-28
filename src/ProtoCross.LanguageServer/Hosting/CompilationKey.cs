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
/// <b>So is a document its project was refused for.</b> Nothing is compiled for it, and what it holds
/// instead -- its refusal -- is its own: a project refused for one document may be compiled for another,
/// when the two are in folders whose settings differ, and a refusal held under the project's name would
/// evict the compilation the other documents share, once per question, back and forth.
/// </para>
/// <para>
/// One statement of it, because the scheduler deciding what to compile and the cache deciding what it
/// holds must agree: a scheduler keyed one way and a cache keyed another would compile a project once per
/// open document and cache it once.
/// </para>
/// </remarks>
internal static class CompilationKey
{
    /// <summary>The compilation a document is in, under the settings just resolved for it.</summary>
    public static string Of(DocumentConfiguration settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A project file's own URI key, which no document shares, since a project is not a source.
        return settings is { ProjectFiles: not null, ProjectPath: { } projectPath }
            && DocumentUri.TryParse(projectPath, out var project)
                ? project.Key
                : settings.Document.Key;
    }
}
