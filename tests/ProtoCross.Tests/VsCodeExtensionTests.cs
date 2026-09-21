using System.Text.Json;
using System.Text.RegularExpressions;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Workspace;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What the VS Code extension and the server have to agree on, checked from the server's side: the
/// grammar's colours, the settings the manifest declares, which of them are restricted, and the note
/// shown before a report is copied.
/// </summary>
/// <remarks>
/// Each of these is written twice, once in C# and once in a file the extension ships, and nothing at
/// build time connects the two. The extension's own tests run in an editor on a slower loop; these run
/// in the suite every change already runs, so a setting added to one side and not the other fails here.
/// </remarks>
public class VsCodeExtensionTests
{
    private static string ExtensionDirectory => Path.Combine(TestPaths.RepositoryRoot, "editors", "vscode");

    private static JsonElement Json(string relative)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(ExtensionDirectory, relative))).RootElement;

    private static JsonElement Manifest => Json("package.json");

    private static JsonElement Contract => Json(Path.Combine("src", "contract.json"));

    private static IReadOnlyList<string> Strings(JsonElement array)
        => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    /// <summary>Every setting the manifest declares, fully qualified.</summary>
    private static IReadOnlyList<string> DeclaredSettings
        => [.. Manifest.GetProperty("contributes").GetProperty("configuration").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)];

    /// <summary>Whether a qualified key is one the extension owns rather than one it passes to the server.</summary>
    private static bool IsExtensionOwned(string key)
        => Strings(Contract.GetProperty("extensionOwnedSettings"))
            .Any(name => key == $"{ProtoCrossSettings.Section}.{name}" || key.StartsWith($"{ProtoCrossSettings.Section}.{name}.", StringComparison.Ordinal));

    // ------------------------------------------------------- settings

    /// <summary>Every setting the server reads can be set from VS Code's settings.</summary>
    [Fact]
    public void TheManifestDeclaresEverySettingTheServerReads()
        => Assert.All(ProtoCrossSettings.Keys, key => Assert.Contains(key, DeclaredSettings));

    /// <summary>
    /// A declared setting the server does not read belongs to the extension, and is filtered out of what
    /// the server is sent.
    /// </summary>
    /// <remarks>
    /// The server reports every setting in its section it does not understand. A setting added to the
    /// manifest and left out of the contract would be sent, and would put a warning on every open file.
    /// </remarks>
    [Fact]
    public void EveryOtherDeclaredSettingIsOneTheExtensionOwns()
        => Assert.All(
            DeclaredSettings.Where(key => !ProtoCrossSettings.Keys.Contains(key)),
            key => Assert.True(IsExtensionOwned(key), $"'{key}' is neither read by the server nor listed in contract.json"));

    /// <summary>The extension owns nothing the server also reads, so filtering never hides a real setting.</summary>
    [Fact]
    public void NoSettingTheServerReadsIsTreatedAsTheExtensions()
        => Assert.All(ProtoCrossSettings.Keys, key => Assert.False(IsExtensionOwned(key), $"'{key}' would be filtered out before the server saw it"));

    /// <summary>
    /// The manifest restricts every setting the server withholds, and otherwise only settings the
    /// extension owns.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A server-restricted setting missing here would be applied by VS Code and then
    /// withheld by the server, and the settings editor would not say why. A server setting restricted here
    /// that the server honours -- an include path -- would be hidden by the editor in exactly the
    /// untrusted workspace spec 10.4.1 says keeps it.
    /// </remarks>
    [Fact]
    public void TheManifestRestrictsWhatTheServerWithholdsAndNothingElseOfTheServers()
    {
        var restricted = Strings(Manifest.GetProperty("capabilities").GetProperty("untrustedWorkspaces").GetProperty("restrictedConfigurations"));

        Assert.All(ProtoCrossSettings.RestrictedKeys, key => Assert.Contains(key, restricted));
        Assert.All(
            restricted.Where(key => !ProtoCrossSettings.RestrictedKeys.Contains(key)),
            key => Assert.True(IsExtensionOwned(key), $"'{key}' is restricted by the manifest but the server honours it untrusted"));
    }

    /// <summary>A setting the extension restricts is one it declares, so the restriction is not a typo.</summary>
    [Fact]
    public void EveryRestrictedSettingIsDeclared()
    {
        var restricted = Strings(Manifest.GetProperty("capabilities").GetProperty("untrustedWorkspaces").GetProperty("restrictedConfigurations"));

        Assert.All(restricted, key => Assert.Contains(key, DeclaredSettings));
    }

    /// <summary>The note before a report the extension wrote is the server's note, word for word.</summary>
    [Fact]
    public void TheExtensionsPrivacyNoteIsTheServers()
        => Assert.Equal(ServerStatus.PrivacyNote, Contract.GetProperty("privacyNote").GetString());

    // ------------------------------------------------------- the grammar

    /// <summary>
    /// The TextMate scope VS Code falls back to for each category the server's lexical layer produces.
    /// </summary>
    /// <remarks>
    /// VS Code's own table (<c>tokenClassificationRegistry.ts</c>). A semantic token is painted with the
    /// theme's colour for this scope, so a grammar that scopes the same token as this scope qualified by
    /// the language is painted the same colour before and after the server answers.
    /// </remarks>
    private static readonly Dictionary<string, string> FallbackScopes = new(StringComparer.Ordinal)
    {
        [SemanticTokenLegend.Keyword] = "keyword.control",
        [SemanticTokenLegend.String] = "string",
        [SemanticTokenLegend.Number] = "constant.numeric",
        [SemanticTokenLegend.Comment] = "comment",
        [SemanticTokenLegend.Operator] = "keyword.operator",
        [SemanticTokenLegend.Variable] = "variable.other.readwrite",
    };

    /// <summary>The shapes a sweep over real files might not happen to contain.</summary>
    private const string Awkward =
        """
        import proto "a\"b\\c.proto";
        /* one /* two */ int64 after = 1.5;
        fn ünïcödé_1(arg x: uint32) -> bool { return x >= 10 && not false || x != 2; }
        var dotted = receiver.value.count; // trailing
        var unterminated = "no closing quote
        var android = 3 % 2 - -1;
        var literals = 0xFF + 0b1010 + 1_000 + 1.5e-3 + 2E+8 + 0xE-1 - __INF * __NAN + __inf;
        var malformed = 0x_FF + 5u + 1e + 0b102 + 1_.5e+3 + 0X1F + 7.e;
        /* runs to the end
        of the file
        """;

    /// <summary>Every ProtoCross source in the repository, and the awkward shapes above.</summary>
    public static TheoryData<string> Sources()
    {
        var data = new TheoryData<string> { "(awkward shapes)" };
        var sources = SourcesUnder(TestPaths.RepositoryRoot);

        // A walk that tolerates what it cannot read can come back empty, and an empty sweep passes and
        // goes on passing. This repository has ProtoCross sources in it, so nothing found is a broken
        // walk rather than a clean bill of health.
        Assert.NotEmpty(sources);

        foreach (var file in sources)
        {
            data.Add(Path.GetRelativePath(TestPaths.RepositoryRoot, file));
        }

        return data;
    }

    /// <summary>
    /// Every <c>*.pcross</c> this repository maintains, in a stable order, descending only into
    /// directories one of them could be in.
    /// </summary>
    /// <remarks>
    /// The exclusions decide what to descend into rather than which results to keep, and the difference
    /// is the whole reason this is written out. A recursive <c>EnumerateFiles</c> walks every directory
    /// before a caller sees the first name, and its <c>SearchOption</c> overload enumerates with
    /// <c>IgnoreInaccessible = false</c>, so one directory this process cannot read throws and takes the
    /// sweep with it. The end-to-end suite leaves VS Code's agent host socket under
    /// <c>editors/vscode/.vscode-test</c>, which meant that running the editor tests and then the suite
    /// failed this theory on a path that was going to be filtered out anyway -- an order of two commands
    /// deciding whether the grammar got checked at all.
    ///
    /// Unreadable directories are still tolerated below, because the excluded names are what this
    /// repository happens to know about today and a sweep of the sources is not the place to discover
    /// what else a machine will not open.
    /// </remarks>
    private static List<string> SourcesUnder(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            try
            {
                found.AddRange(Directory.EnumerateFiles(directory, "*.pcross"));

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!IsSwept(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    pending.Push(child);
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                // Nothing this repository maintains is behind a directory it cannot open.
            }
        }

        // The stack hands directories back in whatever order the file system listed them, and a theory
        // whose cases are named by their file should not reorder itself between two runs.
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Whether a directory of this name holds sources this repository maintains.</summary>
    /// <remarks>
    /// Build output and installed packages hold copies of sources that are checked where they came from,
    /// and a dot-directory is where tools keep whole working copies of this repository -- whose sources
    /// are someone else's branch, and are not this change's to pass or fail.
    /// </remarks>
    private static bool IsSwept(string name)
        => !name.StartsWith('.') && name is not ("bin" or "obj" or "node_modules" or "artifacts");

    /// <summary>
    /// Wherever the server's lexical layer classifies a token, the grammar gives every character of it
    /// the scope VS Code paints that classification with.
    /// </summary>
    /// <remarks>
    /// #45 asks that the grammar never fight the server's classifications, because where they disagree
    /// the user watches colours change as the server answers. The lexical layer is compared rather than
    /// the refined one: refinement is the server saying something the grammar cannot know, and the
    /// grammar colours identifiers exactly as the lexical layer does so that refinement is the only
    /// change a reader sees.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Sources))]
    public void TheGrammarColoursEveryTokenAsTheServersLexicalLayerDoes(string source)
    {
        var text = source == "(awkward shapes)"
            ? Awkward
            : File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, source));

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var scopes = Grammar.Load(Path.Combine(ExtensionDirectory, "syntaxes", "protocross.tmLanguage.json")).Scope(lines);
        var tokens = SemanticTokenEncoder.Encode(text, source).Data;

        var checkedTokens = 0;
        int line = 0, start = 0;

        for (var i = 0; i < tokens.Count; i += 5)
        {
            line += tokens[i];
            start = tokens[i] == 0 ? start + tokens[i + 1] : tokens[i + 1];

            var category = SemanticTokenLegend.TokenTypes[tokens[i + 3]];
            var expected = $"{FallbackScopes[category]}.pcross";

            for (var column = start; column < start + tokens[i + 2]; column++)
            {
                Assert.True(
                    scopes[line][column] == expected,
                    $"{source}:{line + 1}:{column + 1} '{lines[line][start..(start + tokens[i + 2])]}' is {category} to the server "
                        + $"and '{scopes[line][column] ?? "unscoped"}' to the grammar, which should be '{expected}'");
            }

            checkedTokens++;
        }

        Assert.True(checkedTokens > 0 || string.IsNullOrWhiteSpace(text), "a source with text must have produced tokens to compare");
    }

    /// <summary>The grammar's keywords are spec 6.4's, no more and no fewer.</summary>
    /// <remarks>
    /// The sweep above already fails for a keyword the grammar misses, if a source in the repository uses
    /// it. This one also fails for a keyword nobody has written yet, and for a word the grammar colours as
    /// a keyword that the lexer reads as a name.
    /// </remarks>
    [Fact]
    public void TheGrammarsKeywordsAreTheLexersKeywords()
    {
        var keywords = Json(Path.Combine("syntaxes", "protocross.tmLanguage.json"))
            .GetProperty("repository").GetProperty("keywords").GetProperty("patterns")[0].GetProperty("match").GetString()!;

        var listed = Regex.Match(keywords, @"\(\?:([a-z0-9_|]+)\)").Groups[1].Value.Split('|');

        Assert.Equal(Lexer.Keywords.Keys.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Just enough of TextMate to scope a file the way VS Code would: match and begin/end rules, includes
    /// from the repository, line at a time, earliest match wins and ties go to the rule listed first.
    /// </summary>
    /// <remarks>
    /// A real TextMate engine is a WebAssembly build of Oniguruma, and nothing in this repository can run
    /// one. The grammar uses only what .NET's regular expressions read the same way -- Unicode categories,
    /// lookbehind, an end-of-line anchor on a single line -- so this reads the same scopes VS Code does for
    /// it. A construct this does not understand fails loudly rather than being skipped.
    /// </remarks>
    private sealed class Grammar
    {
        private sealed record Rule(string? Name, Regex? Match, Regex? Begin, Regex? End, IReadOnlyList<Rule> Inner);

        private readonly IReadOnlyList<Rule> _rules;

        private Grammar(IReadOnlyList<Rule> rules) => _rules = rules;

        public static Grammar Load(string path)
        {
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var repository = root.GetProperty("repository");

            return new Grammar(Patterns(root.GetProperty("patterns"), repository));
        }

        private static List<Rule> Patterns(JsonElement patterns, JsonElement repository)
        {
            var rules = new List<Rule>();

            foreach (var pattern in patterns.EnumerateArray())
            {
                if (pattern.TryGetProperty("include", out var include))
                {
                    rules.AddRange(Patterns(repository.GetProperty(include.GetString()!.TrimStart('#')).GetProperty("patterns"), repository));
                    continue;
                }

                var name = pattern.TryGetProperty("name", out var named) ? named.GetString() : null;
                var inner = pattern.TryGetProperty("patterns", out var nested) ? Patterns(nested, repository) : [];

                if (pattern.TryGetProperty("match", out var match))
                {
                    rules.Add(new Rule(name, new Regex(match.GetString()!), null, null, inner));
                }
                else if (pattern.TryGetProperty("begin", out var begin) && pattern.TryGetProperty("end", out var end))
                {
                    rules.Add(new Rule(name, null, new Regex(begin.GetString()!), new Regex(end.GetString()!), inner));
                }
                else
                {
                    throw new InvalidOperationException($"A grammar rule this test does not understand: {pattern}");
                }
            }

            return rules;
        }

        /// <summary>The scope of every character of every line; null where nothing scoped it.</summary>
        public string?[][] Scope(string[] lines)
        {
            var scopes = lines.Select(line => new string?[line.Length]).ToArray();
            Rule? open = null;

            for (var number = 0; number < lines.Length; number++)
            {
                var line = lines[number];
                var position = 0;

                while (position <= line.Length)
                {
                    if (open is not null)
                    {
                        var end = open.End!.Match(line, position);
                        var inner = Earliest(open.Inner, line, position);

                        if (end.Success && (inner is null || end.Index <= inner.Value.Found.Index))
                        {
                            Paint(scopes[number], position, end.Index + end.Length, open.Name);
                            position = end.Index + end.Length;
                            open = null;

                            if (end.Length == 0 && end.Index >= line.Length)
                            {
                                break;
                            }

                            continue;
                        }

                        if (inner is { } consumed)
                        {
                            var stop = Math.Max(consumed.Found.Index + consumed.Found.Length, position + 1);
                            Paint(scopes[number], position, stop, open.Name);
                            position = stop;
                            continue;
                        }

                        Paint(scopes[number], position, line.Length, open.Name);
                        break;
                    }

                    if (Earliest(_rules, line, position) is not { } next)
                    {
                        break;
                    }

                    var (rule, found) = next;
                    Paint(scopes[number], found.Index, found.Index + found.Length, rule.Name);
                    position = Math.Max(found.Index + found.Length, position + 1);

                    if (rule.Begin is not null)
                    {
                        open = rule;
                    }
                }
            }

            return scopes;
        }

        private static (Rule Rule, Match Found)? Earliest(IReadOnlyList<Rule> rules, string line, int position)
        {
            (Rule Rule, Match Found)? best = null;

            foreach (var rule in rules)
            {
                var found = (rule.Match ?? rule.Begin)!.Match(line, position);

                if (found.Success && (best is null || found.Index < best.Value.Found.Index))
                {
                    best = (rule, found);
                }
            }

            return best;
        }

        private static void Paint(string?[] line, int from, int to, string? name)
        {
            for (var column = from; column < Math.Min(to, line.Length); column++)
            {
                line[column] = name ?? line[column];
            }
        }
    }
}
