namespace ProtoCross;

/// <summary>
/// What a source is compiled for: the program's behavior, or only the program's tests (spec 25.3.1).
/// </summary>
/// <remarks>
/// <para>
/// A fact about a source's place in one compilation rather than about its text. A project says which
/// of its sources are which (spec 5.4), and one file may be a production source of one project and a
/// test source of another, so the role travels with the <see cref="SourceDocument"/> a compilation is
/// handed instead of being read from the file. A <c>.pcrosstest</c> extension was the alternative,
/// and would have been a second way to say what a project's <c>&lt;Tests&gt;</c> group already says.
/// </para>
/// <para>
/// It says nothing about a source's tests. Any source may declare them, and whether they are bound
/// is a question about the whole build, which <see cref="CompilationOptions.SkipTests"/> answers:
/// a production build binds nobody's tests, and a test build binds everybody's.
/// </para>
/// </remarks>
public enum SourceRole
{
    /// <summary>
    /// Its methods are the program, generated into the behavior output for anything to call. Every
    /// source is one unless a project names it only among its tests.
    /// </summary>
    Production,

    /// <summary>
    /// It is compiled only to test with. Its methods are helpers, generated with the tests, and no
    /// method of a production source may call one.
    /// </summary>
    Test,
}
