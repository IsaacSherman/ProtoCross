using ProtoCross.Syntax;

namespace ProtoCross;

/// <summary>
/// One parsed source of a compilation: its syntax tree, and which source that tree is.
/// </summary>
/// <remarks>
/// What <see cref="SourceDocument"/> is to text, this is to a tree, and for the same reason. A
/// binder handed several trees has to stamp every declaration and every reference with the source it
/// is in, and a tree does not know: its spans carry the label diagnostics print, which is the base
/// file name, and two files of one name in two directories would share it. So the identity travels
/// with the tree rather than being looked up from it.
/// </remarks>
public sealed record SourceTree(SourceIdentity Document, CompilationUnit Unit)
{
    /// <summary>What the source this tree was parsed from is compiled for.</summary>
    /// <remarks>
    /// Carried from the <see cref="SourceDocument"/> for the reason the identity is: the binder has
    /// to know it of every declaration a call resolves to, and generation has to know it of every
    /// source it divides a module into, and neither is handed the documents.
    /// </remarks>
    public SourceRole Role { get; init; }
}
