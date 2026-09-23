namespace ProtoCross.Diagnostics;

/// <summary>
/// Everything about a diagnostic that is decided once, for the code, rather than at the place that
/// raises it: its <c>PC####</c> identifier, its severity, and the short title it renders with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these three and no more.</b> Code, severity and title are properties of the rule. A
/// message is a property of the occurrence -- nearly every one interpolates a name, a type or a
/// count, and the same code says different true things at different sites. Help is the same: PC0078
/// gives genuinely different advice at its two sites, one telling the author to bind an intermediate
/// to a local and the other to guard the field, so a single help string on the descriptor would have
/// to be vague enough to fit both and would then help with neither. Message and help therefore stay
/// arguments to <see cref="DiagnosticBag.Report"/>.
/// </para>
/// <para>
/// <b>Why a table of these rather than an enum with attributes.</b> An attribute has to be read by
/// reflection, on a path that runs per keystroke in an editor, and it is a trimming hazard; a source
/// generator is a great deal of machinery for a hundred rows. A <c>static readonly</c> field takes an
/// XML doc comment, which is where this codebase keeps its real documentation, and the spec section a
/// code belongs to is exactly the kind of thing that wants to be written next to it. It also composes
/// with #61, where a quick fix is a second table keyed by the same descriptors.
/// </para>
/// <para>
/// <b>Why the title is here at all.</b> Rendered output is <c>CODE: title</c> (spec 26), so a title
/// is published text. Held at the raise site, one code could -- and did -- carry two titles: PC0015
/// told a reader at the parser that <c>on_zero</c> is valid on division and at the binder that it is
/// valid only on integer division, and the first was wrong about the language rule in the line that
/// names the rule. One descriptor per code makes that disagreement unrepresentable.
/// </para>
/// </remarks>
public sealed record DiagnosticDescriptor(string Code, DiagnosticSeverity Severity, string Title);
