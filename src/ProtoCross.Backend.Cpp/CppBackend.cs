using System.Globalization;
using System.Text;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

/// <summary>
/// Emits a header-only C++ library of free functions over the generated protobuf messages.
/// </summary>
/// <remarks>
/// Spec 24.2 lists several candidate shapes. Free functions in the message's own namespace are
/// chosen here because they subclass nothing, require no protoc insertion points, and work
/// identically whether the protobuf codegen is regenerated or vendored. Declarations are emitted
/// ahead of definitions so methods may call one another in any order.
/// <para>
/// A header has two layouts. One whose methods call no other source's declares and defines a
/// namespace at a time, as every header always has. One whose methods do declares everything,
/// includes the headers it calls, then defines everything, so two sources calling each other compile
/// in either include order; see <c>WriteAroundSiblings</c>. Keeping the first layout for the common
/// case is what keeps generating a single source from moving.
/// </para>
/// </remarks>
public sealed class CppBackend : ITestProjectScaffold
{
    private const string ReceiverName = "self";
    private const string RuntimeNamespace = "::protocross_runtime";

    /// <summary>What a generated library header is called, after the source it was generated from.</summary>
    /// <remarks>
    /// One home, because three places need it and they had drifted: the file is written under this
    /// name, the generated tests include it by this name, and the include guard is built from it. The
    /// guard kept a copy of its own, so renaming <c>.pl.h</c> to <c>.pc.h</c> moved two of the three
    /// and left every generated header guarded by a macro named after an extension nothing produces.
    /// </remarks>
    private const string HeaderExtension = ".pc.h";

    /// <summary>Exit code a child uses when an <c>expect fail</c> body returned instead of dying.</summary>
    private const int DidNotTerminateExitCode = 91;

    /// <summary>Exit code a child uses when it does not recognize the requested test name.</summary>
    private const int UnknownTestExitCode = 92;

    public string Name => "cpp";

    public IReadOnlyList<GeneratedFile> Emit(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");
        var guard = MakeIncludeGuard(baseName + HeaderExtension);

        WriteHeader(writer, options, guard, module);

        var namespaces = module.Methods
            .GroupBy(m => NameConventions.GetCppNamespace(m.Receiver.File))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Namespace: g.Key, Methods: g.OrderBy(m => m.Receiver.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList()))
            .ToList();

        var siblings = SiblingHeadersCalledFrom(module);
        if (siblings.Count == 0)
        {
            foreach (var (name, methods) in namespaces)
            {
                using (OpenNamespace(writer, name))
                {
                    WriteDeclarations(writer, methods);
                    writer.WriteLine();
                    WriteDefinitions(writer, methods);
                }
            }
        }
        else
        {
            WriteAroundSiblings(writer, namespaces, siblings);
        }

        writer.WriteLine();
        writer.WriteLine($"#endif  // {guard}");

        return
        [
            new GeneratedFile(CppRuntime.FileName, CppRuntime.Source),
            new GeneratedFile(baseName + HeaderExtension, writer.ToString()),
        ];
    }

    /// <summary>
    /// Writes a header that calls into other sources' headers: every declaration, then those headers,
    /// then every definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method may call one declared in another source of the compilation (spec 5.3), and two
    /// sources may call each other. Each header therefore declares everything it defines before it
    /// includes anything it calls, so whichever of two such headers is included first, the other's
    /// definitions find its declarations already written, and the guard stops the include going round
    /// again. Including them at the top instead would leave the second header's definitions calling
    /// functions the first had not yet declared.
    /// </para>
    /// <para>
    /// Only a header that calls another source is laid out this way. One that calls none keeps the
    /// layout it has always had, declarations and definitions a namespace at a time, so generating a
    /// single source does not move.
    /// </para>
    /// </remarks>
    private static void WriteAroundSiblings(
        SourceWriter writer,
        IReadOnlyList<(string Namespace, List<IrMethod> Methods)> namespaces,
        IReadOnlyList<string> siblings)
    {
        foreach (var (name, methods) in namespaces)
        {
            using (OpenNamespace(writer, name))
            {
                WriteDeclarations(writer, methods);
            }
        }

        writer.WriteLine();
        writer.WriteLine("// The sources this one calls, included after its own declarations so that two sources");
        writer.WriteLine("// calling one another compile whichever header is included first.");
        foreach (var sibling in siblings)
        {
            writer.WriteLine($"#include \"{sibling}\"");
        }

        foreach (var (name, methods) in namespaces)
        {
            writer.WriteLine();
            using (OpenNamespace(writer, name))
            {
                WriteDefinitions(writer, methods);
            }
        }
    }

    /// <summary>Opens a C++ namespace, or nothing for the global one.</summary>
    private static IDisposable? OpenNamespace(SourceWriter writer, string name)
        => string.IsNullOrEmpty(name) ? null : writer.Block($"namespace {name}", $"}}  // namespace {name}");

    private static void WriteDeclarations(SourceWriter writer, IReadOnlyList<IrMethod> methods)
    {
        writer.WriteLine("// Declarations precede definitions so methods may call one another");
        writer.WriteLine("// regardless of the order they appear in the ProtoCross source.");
        foreach (var method in methods)
        {
            writer.WriteLine(Signature(method) + ";");
        }
    }

    private static void WriteDefinitions(SourceWriter writer, IReadOnlyList<IrMethod> methods)
    {
        var first = true;
        foreach (var method in methods)
        {
            if (!first)
            {
                writer.WriteLine();
            }

            first = false;
            EmitMethod(writer, method);
        }
    }

    /// <summary>
    /// The headers of the other sources this module's methods call into, sorted, each once.
    /// </summary>
    /// <remarks>
    /// Asked of the IR: a call carries its callee's signature, and the signature says which source
    /// declares it. A header is named after its source by the same rule this backend names its own
    /// by, which <c>PC2006</c> keeps unambiguous within a compilation.
    /// </remarks>
    private static IReadOnlyList<string> SiblingHeadersCalledFrom(IrModule module)
        => module.Methods
            .SelectMany(method => IrWalk.DescendantsAndSelf(method)
                .OfType<IrMethodCall>()
                .Where(call => call.Target.Declaration.Document != method.Signature.Declaration.Document)
                .Select(call => HeaderFor(call.Target.Declaration.Document)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The header a source's behavior is generated into.</summary>
    private static string HeaderFor(SourceIdentity document)
        => Path.GetFileNameWithoutExtension(document.Name) + HeaderExtension;

    public IReadOnlyList<GeneratedFile> EmitTests(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        if (module.Tests.Count == 0)
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var functionNames = module.Tests.ToDictionary(
            test => test,
            test => UniqueTestFunctionName(test, usedNames));

        var hasFailTests = module.Tests.Any(t => t.Expectation is IrTestFailExpectation);
        var hasFloatingPointExpectations = module.Tests.Any(ExpectsFloatingPoint);

        WriteTestHeader(writer, options, HeadersTestedBy(module, baseName), hasFailTests, hasFloatingPointExpectations);

        foreach (var test in module.Tests)
        {
            writer.WriteLine(test.Expectation is IrTestFailExpectation
                ? $"static void {functionNames[test]}();"
                : $"static bool {functionNames[test]}();");
        }

        writer.WriteLine();
        EmitMain(writer, module, functionNames);

        foreach (var test in module.Tests)
        {
            writer.WriteLine();
            EmitCppTest(writer, test, functionNames[test]);
        }

        return [new GeneratedFile(baseName + NameConventions.TestsSuffix + ".cc", writer.ToString())];
    }

    public IReadOnlyList<GeneratedFile> EmitTestProject(
        ScaffoldOptions options,
        DiagnosticBag diagnostics)
        => [new GeneratedFile(CppTestProject.FileName, CppTestProject.Build(options))];

    /// <summary>
    /// The headers declaring the methods this source's tests target, sorted, each once: this source's
    /// own, and another source's wherever a test targets a method declared there.
    /// </summary>
    /// <param name="baseName">What this source's own header is named after.</param>
    /// <remarks>
    /// A test may target a method in any source of the compilation (spec 5.3), and the driver calls it,
    /// so the driver includes whichever header declares it. A test whose target is in its own source
    /// includes the header named for this source, as every driver always has.
    /// </remarks>
    private static IReadOnlyList<string> HeadersTestedBy(IrModule module, string baseName)
        => module.Tests
            .Select(test => test.Document is null || test.Target.Declaration.Document == test.Document
                ? baseName + HeaderExtension
                : HeaderFor(test.Target.Declaration.Document))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void WriteTestHeader(
        SourceWriter writer,
        BackendOptions options,
        IReadOnlyList<string> headers,
        bool hasFailTests,
        bool hasFloatingPointExpectations)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protocross from {options.SourceFileName}.");
        writer.WriteLine("//     ProtoCross unit tests for generated behavior.");
        writer.WriteLine("//     Run with no arguments to run every test; the exit code is 0 when they");
        writer.WriteLine("//     all pass. '--run <name>' runs one test, which is how a test that expects");
        writer.WriteLine("//     the process to terminate is observed.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();

        // Only for std::isnan, and only where a NaN can be expected, so a driver with no floating-point
        // expectation stays byte-for-byte what it was.
        if (hasFloatingPointExpectations)
        {
            writer.WriteLine("#include <cmath>");
        }

        writer.WriteLine("#include <cstring>");
        writer.WriteLine("#include <iostream>");

        if (hasFailTests)
        {
            writer.WriteLine("#include <cstdlib>");
            writer.WriteLine("#include <string>");
            writer.WriteLine();
            writer.WriteLine("#if !defined(_WIN32)");
            writer.WriteLine("#include <sys/wait.h>");
            writer.WriteLine("#endif");
        }

        writer.WriteLine();
        foreach (var header in headers)
        {
            writer.WriteLine($"#include \"{header}\"");
        }

        writer.WriteLine();

        writer.WriteLine("// Reported when '--run' names a test this driver does not have.");
        writer.WriteLine($"static const int kProtoCrossUnknownTest = {UnknownTestExitCode};");

        if (hasFailTests)
        {
            writer.WriteLine();
            writer.WriteLine("// Reported when an 'expect fail' body returned, which is what tells the parent");
            writer.WriteLine("// that the method did not terminate rather than that it failed some other way.");
            writer.WriteLine($"static const int kProtoCrossDidNotTerminate = {DidNotTerminateExitCode};");
            writer.WriteLine();
            EmitExpectFailHelpers(writer);
        }
        else
        {
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Emits the helpers behind <c>expect fail</c>. What such a test asserts is that the call does
    /// not return, which cannot be observed from inside the process it ends, so the driver reruns
    /// itself for that one test and inspects how the child died.
    /// </summary>
    private static void EmitExpectFailHelpers(SourceWriter writer)
    {
        writer.WriteLine("// Turns what std::system reports into a plain exit code. POSIX returns a wait");
        writer.WriteLine("// status rather than the code itself, and a child killed by a signal has no exit");
        writer.WriteLine("// code at all, which -1 stands in for so it can never match the expected one.");
        using (writer.Block("static int protocross_exit_code(int status)"))
        {
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine("return status;");
            writer.WriteLine("#else");
            using (writer.Block("if (WIFEXITED(status))"))
            {
                writer.WriteLine("return WEXITSTATUS(status);");
            }

            writer.WriteLine();
            writer.WriteLine("return -1;");
            writer.WriteLine("#endif");
        }

        writer.WriteLine();
        writer.WriteLine("// Runs one test in a child process and reports whether it terminated as expected.");
        using (writer.Block(
            "static bool protocross_expect_fail(const char* self, const char* test, const char* identity)"))
        {
            writer.WriteLine("::std::string command = \"\\\"\";");
            writer.WriteLine("command += self;");
            writer.WriteLine("command += \"\\\" --run \";");
            writer.WriteLine("command += test;");
            writer.WriteLine();
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine("// cmd.exe strips the outermost pair of quotes from the command line it is");
            writer.WriteLine("// handed, so a path containing a space needs a second pair to survive.");
            writer.WriteLine("command = \"\\\"\" + command + \"\\\"\";");
            writer.WriteLine("#endif");
            writer.WriteLine();
            writer.WriteLine("const int code = protocross_exit_code(::std::system(command.c_str()));");
            writer.WriteLine();

            EmitExpectFailRejection(
                writer,
                "code == kProtoCrossDidNotTerminate",
                "the method returned instead of terminating the process");
            EmitExpectFailRejection(
                writer,
                "code == kProtoCrossUnknownTest",
                "the child process did not recognize the test name");

            // The exit code is normative (spec 10.2.1), so this is an equality check rather than
            // "died somehow": a child that crashed for an unrelated reason must not pass.
            using (writer.Block($"if (code != {CppRuntime.FailExitCode})"))
            {
                writer.WriteLine(
                    "::std::cout << \"[FAIL] \" << identity << \" (the child process exited with \" << code");
                writer.Indent();
                writer.WriteLine(
                    $"<< \", not the ProtoCross failure code {CppRuntime.FailExitCode})\" << ::std::endl;");
                writer.Unindent();
                writer.WriteLine("return false;");
            }

            writer.WriteLine();
            writer.WriteLine("::std::cout << \"[ok] \" << identity << ::std::endl;");
            writer.WriteLine("return true;");
        }

        writer.WriteLine();
    }

    private static void EmitExpectFailRejection(SourceWriter writer, string condition, string reason)
    {
        using (writer.Block($"if ({condition})"))
        {
            writer.WriteLine(
                $"::std::cout << \"[FAIL] \" << identity << \" ({EscapeString(reason)})\" << ::std::endl;");
            writer.WriteLine("return false;");
        }

        writer.WriteLine();
    }

    private static void EmitMain(
        SourceWriter writer,
        IrModule module,
        IReadOnlyDictionary<IrTest, string> functionNames)
    {
        using var scope = writer.Block("int main(int argc, char** argv)");

        writer.WriteLine("// Single-test mode, used by the parent process for 'expect fail' tests.");
        using (writer.Block("if (argc >= 3 && ::std::strcmp(argv[1], \"--run\") == 0)"))
        {
            foreach (var test in module.Tests)
            {
                var name = functionNames[test];
                using (writer.Block($"if (::std::strcmp(argv[2], \"{EscapeString(name)}\") == 0)"))
                {
                    if (test.Expectation is IrTestFailExpectation)
                    {
                        writer.WriteLine($"{name}();");
                        writer.WriteLine("return kProtoCrossDidNotTerminate;");
                    }
                    else
                    {
                        writer.WriteLine($"return {name}() ? 0 : 1;");
                    }
                }

                writer.WriteLine();
            }

            writer.WriteLine("::std::cout << \"protocross: unknown test \" << argv[2] << ::std::endl;");
            writer.WriteLine("return kProtoCrossUnknownTest;");
        }

        writer.WriteLine();
        writer.WriteLine("int failures = 0;");

        foreach (var test in module.Tests)
        {
            var name = functionNames[test];
            writer.WriteLine(test.Expectation is IrTestFailExpectation
                ? $"if (!protocross_expect_fail(argv[0], \"{EscapeString(name)}\", "
                    + $"\"{EscapeString(test.Identity)}\")) {{ ++failures; }}"
                : $"if (!{name}()) {{ ++failures; }}");
        }

        writer.WriteLine();

        // The summary line is what lets a harness tell a driver that ran every test from one that
        // ran none: both exit 0.
        writer.WriteLine(
            $"::std::cout << \"protocross: {module.Tests.Count} test(s), \" << failures << \" failed\" "
            + "<< ::std::endl;");
        writer.WriteLine("return failures == 0 ? 0 : 1;");
    }

    private static void EmitCppTest(SourceWriter writer, IrTest test, string functionName)
    {
        if (test.Expectation is IrTestFailExpectation)
        {
            EmitCppFailTest(writer, test, functionName);
            return;
        }

        if (test.Expectation is not IrTestReturnExpectation returnExpectation)
        {
            throw new ArgumentOutOfRangeException(nameof(test), test.Expectation, "Unhandled C++ test expectation.");
        }

        using var scope = writer.Block($"static bool {functionName}()");

        EmitCppReceiver(writer, test);

        writer.WriteLine($"const auto actual = {CppInvocation(test)};");
        writer.WriteLine($"const auto expected = {Expression(returnExpectation.Value)};");
        using (writer.Block($"if ({ExpectationUnmet(test)})"))
        {
            // Printed on stdout rather than stderr so a harness reading the driver sees every
            // result line in the order they happened.
            writer.WriteLine(
                $"::std::cout << \"[FAIL] {EscapeString(test.Identity)}\""
                + " << \" (expected \" << expected << \", actual \" << actual << \")\" << ::std::endl;");
            writer.WriteLine("return false;");
        }

        writer.WriteLine();
        writer.WriteLine($"::std::cout << \"[ok] {EscapeString(test.Identity)}\" << ::std::endl;");
        writer.WriteLine("return true;");
    }

    /// <summary>Whether a test expects a floating-point value, and so has a NaN case to meet.</summary>
    /// <remarks>
    /// Asked by the condition each such test emits and by the <c>&lt;cmath&gt;</c> include that
    /// condition needs, so the two cannot fall out of step.
    /// </remarks>
    private static bool ExpectsFloatingPoint(IrTest test)
        => test.Expectation is IrTestReturnExpectation { Value.Type: ScalarType { IsFloatingPoint: true } };

    /// <summary>
    /// The condition under which a returned value does not meet its <c>expect return</c>: they are
    /// unequal, unless both are NaN (spec 25.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A NaN is unequal to everything, itself included, so <c>!=</c> alone fails every NaN expectation
    /// however right the result is. The generated C# test never had that problem, because xUnit's
    /// <c>Assert.Equal</c> compares doubles with <c>Equals</c>, where a NaN equals a NaN -- which is how
    /// <c>expect return __NAN;</c> came to pass in one backend and fail in the other.
    /// </para>
    /// <para>
    /// The NaN case is asked of <c>std::isnan</c> rather than as <c>x != x</c>. Both are true of a NaN
    /// and of nothing else, but only one says so to a reader who has not memorised IEEE 754, and to
    /// everyone else the second reads as a typo.
    /// </para>
    /// <para>
    /// Only a floating-point expectation gets the longer condition, so every other test stays
    /// byte-for-byte what it was. Nothing else about <c>==</c> changes: <c>0.0</c> still meets a
    /// <c>-0.0</c>, as it does under <c>Equals</c>.
    /// </para>
    /// </remarks>
    private static string ExpectationUnmet(IrTest test)
        => ExpectsFloatingPoint(test)
            ? "!(actual == expected || (::std::isnan(actual) && ::std::isnan(expected)))"
            : "actual != expected";

    /// <summary>
    /// Emits the body a child process runs for an <c>expect fail</c> test. It is never called in
    /// the parent, because the call it makes is expected to end the process.
    /// </summary>
    private static void EmitCppFailTest(SourceWriter writer, IrTest test, string functionName)
    {
        using var scope = writer.Block($"static void {functionName}()");

        EmitCppReceiver(writer, test);

        writer.WriteLine("// The call is expected not to return, so its result is deliberately unused.");
        writer.WriteLine(test.Target.ReturnType is VoidType
            ? $"{CppInvocation(test)};"
            : $"static_cast<void>({CppInvocation(test)});");
    }

    private static void EmitCppReceiver(SourceWriter writer, IrTest test)
    {
        writer.WriteLine($"{QualifiedTypeName(test.Target.Receiver)} receiver;");
        EmitCppFixtureFields(writer, "receiver", false, test.Receiver, new NameAllocator());
    }

    private static string CppInvocation(IrTest test)
    {
        var arguments = new List<string> { "receiver" };
        arguments.AddRange(test.Arguments.Select(a => Expression(a.Value)));

        return $"{QualifiedFunctionName(test.Target)}({string.Join(", ", arguments)})";
    }

    private static void EmitCppFixtureFields(
        SourceWriter writer,
        string target,
        bool targetIsPointer,
        IrTestMessageValue message,
        NameAllocator names)
    {
        var access = targetIsPointer ? "->" : ".";
        foreach (var value in message.Fields.OrderBy(v => v.Field.FieldNumber))
        {
            var field = value.Field;
            var accessor = NameConventions.GetCppFieldName(field);
            if (value.MessageValue is not null)
            {
                var local = names.Next(field.Name);
                var mutator = field.IsRepeated ? $"add_{accessor}" : $"mutable_{accessor}";
                writer.WriteLine($"auto* {local} = {target}{access}{mutator}();");
                EmitCppFixtureFields(writer, local, true, value.MessageValue, names);
                continue;
            }

            var setter = field.IsRepeated ? $"add_{accessor}" : $"set_{accessor}";
            writer.WriteLine($"{target}{access}{setter}({Expression(value.ScalarValue!)});");
        }
    }

    private static void WriteHeader(SourceWriter writer, BackendOptions options, string guard, IrModule module)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protocross from {options.SourceFileName}.");
        foreach (var line in options.PolicyDescription)
        {
            writer.WriteLine($"//     {line}");
        }

        writer.WriteLine("//     Signed overflow is undefined behavior in C++, so arithmetic routes through");
        writer.WriteLine($"//     {CppRuntime.FileName} rather than using the built-in operators directly.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine($"#ifndef {guard}");
        writer.WriteLine($"#define {guard}");
        writer.WriteLine();

        if (UsesFloatingRemainder(module))
        {
            writer.WriteLine("#include <cmath>");
        }

        writer.WriteLine("#include <cstdint>");

        if (UsesFoldableFloatingDivision(module))
        {
            writer.WriteLine("#include <functional>");
        }

        writer.WriteLine("#include <limits>");
        writer.WriteLine("#include <string>");
        writer.WriteLine();

        var protoHeaders = module.Methods
            .Select(m => NameConventions.GetCppProtoHeader(m.Receiver.File))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(header => header, StringComparer.Ordinal);

        foreach (var header in protoHeaders)
        {
            writer.WriteLine($"#include \"{header}\"");
        }

        writer.WriteLine($"#include \"{CppRuntime.FileName}\"");
        writer.WriteLine();
    }

    /// <summary>The macro a generated header guards itself with, built from its own file name.</summary>
    /// <remarks>
    /// From the whole file name, extension included, so that renaming the file renames the guard and
    /// the two cannot say different things -- which is the whole of the defect this replaces. The
    /// <c>PROTOCROSS_</c> prefix is what makes it unique in a consumer's build, where a macro called
    /// <c>CASTS_PC_H_</c> would be a collision waiting for the day they generate from a schema of
    /// their own by that name. <c>protocross_runtime.h</c> spells its guard out, and arrives at the
    /// same shape because its name already begins with the prefix.
    /// </remarks>
    private static string MakeIncludeGuard(string fileName)
    {
        var builder = new StringBuilder("PROTOCROSS_");
        foreach (var c in fileName)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        builder.Append('_');
        return builder.ToString();
    }

    private static string Signature(IrMethod method)
    {
        var parameters = new List<string> { $"const {QualifiedTypeName(method.Receiver)}& {ReceiverName}" };
        parameters.AddRange(method.Parameters.Select(p => $"{ParameterTypeName(p.Type)} {Escape(p.Name)}"));

        // The method name needs escaping too: ProtoCross names are snake_case and every C++ keyword
        // is lowercase, so 'fn class()' or 'fn operator()' would otherwise emit invalid C++.
        return $"inline {TypeName(method.ReturnType)} {Escape(method.Name)}({string.Join(", ", parameters)})";
    }

    private static void EmitMethod(SourceWriter writer, IrMethod method)
    {
        using var scope = writer.Block(Signature(method));
        EmitStatements(writer, method.Body.Statements);
    }

    private static void EmitStatements(SourceWriter writer, IReadOnlyList<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            EmitStatement(writer, statement);
        }
    }

    private static void EmitStatement(SourceWriter writer, IrStatement statement)
    {
        switch (statement)
        {
            case IrBlock block:
            {
                using var scope = writer.Block(string.Empty);
                EmitStatements(writer, block.Statements);
                break;
            }

            case IrVariableDeclaration declaration:
                writer.WriteLine(
                    $"{TypeName(declaration.Local.Type)} {Escape(declaration.Local.Name)} = "
                    + $"{Expression(declaration.Initializer)};");
                break;

            case IrAssignment assignment:
                writer.WriteLine($"{Expression(assignment.Target)} = {Expression(assignment.Value)};");
                break;

            case IrReturn { Value: null }:
                writer.WriteLine("return;");
                break;

            case IrReturn returnStatement:
                writer.WriteLine($"return {Expression(returnStatement.Value!)};");
                break;

            case IrForEach forEach:
            {
                using var scope = writer.Block(
                    $"for (const auto& {Escape(forEach.Loop.Name)} : {Expression(forEach.Collection)})");
                EmitStatements(writer, forEach.Body.Statements);
                break;
            }

            case IrIf ifStatement:
                EmitIf(writer, ifStatement);
                break;

            case IrWhile whileStatement:
            {
                using var scope = writer.Block($"while ({Expression(whileStatement.Condition)})");
                EmitStatements(writer, whileStatement.Body.Statements);
                break;
            }

            case IrBreak:
                writer.WriteLine("break;");
                break;

            case IrContinue:
                writer.WriteLine("continue;");
                break;

            case IrExpressionStatement expression:
                writer.WriteLine($"{Expression(expression.Expression)};");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unhandled statement.");
        }
    }

    /// <summary>
    /// Emits an if/else chain. The chain is flattened rather than nested, so an 'else if' in the
    /// source stays an 'else if' in the output instead of gaining a brace level per branch.
    /// </summary>
    private static void EmitIf(SourceWriter writer, IrIf statement)
    {
        var keyword = "if";

        while (true)
        {
            using (writer.Block($"{keyword} ({Expression(statement.Condition)})"))
            {
                EmitStatements(writer, statement.Then.Statements);
            }

            // The binder only ever puts a block or a nested 'if' in the else branch.
            if (statement.Else is IrIf nested)
            {
                statement = nested;
                keyword = "else if";
                continue;
            }

            if (statement.Else is IrBlock elseBlock)
            {
                using var scope = writer.Block("else");
                EmitStatements(writer, elseBlock.Statements);
            }

            return;
        }
    }

    private static string Expression(IrExpression expression) => expression switch
    {
        IrThis => ReceiverName,
        IrLocalReference local => Escape(local.Local.Name),
        IrParameterReference parameter => Escape(parameter.Parameter.Name),
        IrFieldAccess field => $"{Expression(field.Receiver)}.{NameConventions.GetCppFieldName(field.Field)}()",

        // Uniform in C++, unlike C#: protoc emits has_x() for every field with presence,
        // message-typed or not.
        IrFieldPresence presence
            => $"{Expression(presence.Receiver)}.has_{NameConventions.GetCppFieldName(presence.Field)}()",
        IrMethodCall call => EmitCall(call),
        IrBinary binary => EmitBinary(binary),
        IrIntegerDivision division => EmitIntegerDivision(division),
        IrUnary unary => EmitUnary(unary),
        IrConversion conversion => EmitConversion(conversion),
        IrEnumValue enumValue => QualifiedEnumValueName(enumValue.Value),
        IrLiteral literal => EmitLiteral(literal),
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
    };

    private static string EmitCall(IrMethodCall call)
    {
        var arguments = new List<string> { Expression(call.Receiver) };
        arguments.AddRange(call.Arguments.Select(Expression));

        var ns = NameConventions.GetCppNamespace(call.Target.Receiver.File);
        var name = Escape(call.Target.Name);
        var qualified = string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";

        return $"{qualified}({string.Join(", ", arguments)})";
    }

    private static string EmitBinary(IrBinary binary)
    {
        var left = Expression(binary.Left);
        var right = Expression(binary.Right);

        if (binary.OverflowingType is { } scalar)
        {
            // Never a bare operator, under any policy: signed overflow is undefined behavior, so
            // even the wrapping case has to be spelled out in the unsigned domain.
            var stem = CppRuntime.Stem(binary.Behavior);
            return $"{RuntimeNamespace}::{stem}_{ArithmeticHelperName(binary.Operator)}_{HelperSuffix(scalar)}"
                + $"({left}, {right})";
        }

        if (IsFloatingRemainder(binary))
        {
            return $"::std::fmod({left}, {right})";
        }

        if (IsFoldableFloatingDivision(binary))
        {
            // A call is not a constant expression, so the quotient is left for the program to work
            // out, exactly as one taken from fields already is. ::std::divides is specified to
            // return left / right and nothing besides, which keeps the repair in this header
            // instead of in protocross_runtime.h, which every generated project carries.
            return $"::std::divides<{TypeName(binary.ResultType)}>{{}}({left}, {right})";
        }

        // C++20 defines a shift of a signed value, left and right, but only by a count from zero to
        // one less than the width: anything else is undefined behavior. The mask is what brings every
        // count into that range, which is also what spec 10.1 says a count means.
        if (binary.ShiftCountMask is { } mask)
        {
            return $"({left} {OperatorText(binary.Operator)} ({right} & {mask}))";
        }

        return $"({left} {OperatorText(binary.Operator)} {right})";
    }

    /// <summary>
    /// Whether <paramref name="binary"/> is <c>%</c> on <c>float</c> or <c>double</c> operands, which
    /// C++ spells <c>std::fmod</c> rather than <c>%</c>.
    /// </summary>
    /// <remarks>
    /// C++ defines the built-in <c>%</c> on integers only, so a bare operator here does not compile.
    /// <c>std::fmod</c> computes exactly the remainder spec 10.2 states -- truncated, exact, carrying
    /// the sign of the dividend -- and it is fully specified by the C standard, so no runtime helper
    /// is needed the way the undefined integer and conversion cases need one. The emitter and
    /// <see cref="UsesFloatingRemainder"/> both ask this, so the <c>&lt;cmath&gt;</c> include can never
    /// fall out of step with the calls that need it.
    /// </remarks>
    private static bool IsFloatingRemainder(IrBinary binary)
        => binary.Operator == IrBinaryOperator.Modulo
            && binary.ResultType is ScalarType { IsFloatingPoint: true };

    /// <summary>
    /// Whether anything in <paramref name="module"/> is a floating-point remainder, and so needs
    /// <c>&lt;cmath&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The include is conditional rather than unconditional so that a header for a module with no
    /// floating remainder stays byte-for-byte what it was before this construct compiled at all.
    /// </remarks>
    private static bool UsesFloatingRemainder(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrBinary>().Any(IsFloatingRemainder);

    /// <summary>
    /// Whether <paramref name="binary"/> is a floating-point <c>/</c> the C++ front end would work
    /// out for itself, which is the case a compiler may refuse rather than emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A division by zero is undefined behavior in C++ however it is written, so a compiler that can
    /// see one is entitled to reject it, and MSVC does: <c>1.0 / 0.0</c> is error C2124, "divide or
    /// mod by zero", and the generated header does not compile at all. Spec 10.2 says that quotient
    /// is an infinity, so the language would be promising an answer one backend cannot deliver for
    /// the most direct way an author can ask for it.
    /// </para>
    /// <para>
    /// Both operands, not just the divisor. The refusal comes from constant folding, so it needs
    /// every operand to be a constant: <c>self.numerator() / 0.0</c> compiles, and it is still
    /// emitted as a bare <c>/</c>. Asking about the divisor alone would put a function call around
    /// every <c>x / 2.0</c> in the corpus to fix something that was never broken.
    /// </para>
    /// <para>
    /// <see cref="IsConstantExpression"/> answers the other half, and answers it by node kind rather
    /// than by value, because the zero is often not written as one -- <c>-0.0</c>, <c>0 as double</c>
    /// and <c>0.0 * 2.0</c> are all divisors the fold reaches and a list of spellings would not.
    /// Knowing the value would need a constant evaluator in the backend, and having one would buy a
    /// narrower rule for a construct nobody writes twice.
    /// </para>
    /// </remarks>
    private static bool IsFoldableFloatingDivision(IrBinary binary)
        => binary.Operator == IrBinaryOperator.Divide
            && binary.ResultType is ScalarType { IsFloatingPoint: true }
            && IsConstantExpression(binary.Left)
            && IsConstantExpression(binary.Right);

    /// <summary>
    /// Whether this expression is emitted as something the C++ front end can evaluate while
    /// compiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A question about the emitted C++ rather than about the IR, so it follows what the emitter
    /// writes. The four node kinds here become literals and operators; everything else becomes a
    /// call or a member access, and neither is a constant expression. Integer arithmetic is the case
    /// worth naming: it looks constant and is not, because it routes through
    /// <c>protocross_runtime.h</c>, so <c>(2 - 2) as double</c> is a divisor the front end cannot
    /// work out. Counting it as constant anyway costs an inlined call that was not needed, which is
    /// the direction this predicate is allowed to be wrong in.
    /// </para>
    /// <para>
    /// A reference to a local is deliberately absent. MSVC does fold through a <c>const</c> local,
    /// so this answer depends on <see cref="EmitStatement"/> declaring locals without <c>const</c> --
    /// which it must anyway, since a ProtoCross local can be assigned.
    /// </para>
    /// </remarks>
    private static bool IsConstantExpression(IrExpression expression) => expression switch
    {
        IrLiteral => true,
        IrUnary unary => IsConstantExpression(unary.Operand),
        IrBinary binary => IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right),
        IrConversion conversion => IsConstantExpression(conversion.Operand),
        _ => false,
    };

    /// <summary>
    /// Whether anything in <paramref name="module"/> is a foldable floating-point division, and so
    /// needs <c>&lt;functional&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Conditional for the same reason <see cref="UsesFloatingRemainder"/> is: a header for a module
    /// that writes no such division stays byte-for-byte what it was before this repair existed.
    /// </remarks>
    private static bool UsesFoldableFloatingDivision(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrBinary>().Any(IsFoldableFloatingDivision);

    private static string EmitIntegerDivision(IrIntegerDivision division)
    {
        if (division.ResultType is not ScalarType scalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.ResultType, "Integer division must produce a scalar.");
        }

        var left = Expression(division.Left);
        var right = Expression(division.Right);
        var stem = CppRuntime.Stem(division.Behavior)
            + (division.Operator == IrBinaryOperator.Modulo ? "_mod" : "_div");
        var suffix = HelperSuffix(scalar);

        return division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => $"{RuntimeNamespace}::{stem}_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fail => $"{RuntimeNamespace}::{stem}_or_fail_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fallback =>
                $"{RuntimeNamespace}::{stem}_or_{suffix}({left}, {right}, {Expression(division.OnZero!)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
        };
    }

    private static string EmitUnary(IrUnary unary)
    {
        var operand = Expression(unary.Operand);

        if (unary.Operator == IrUnaryOperator.Negate)
        {
            return unary.OverflowingType is { } scalar
                ? $"{RuntimeNamespace}::{CppRuntime.Stem(unary.Behavior)}_neg_{HelperSuffix(scalar)}({operand})"
                : $"(-{operand})";
        }

        return unary.Operator == IrUnaryOperator.BitwiseNot ? $"(~{operand})" : $"(!{operand})";
    }

    private static string ArithmeticHelperName(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "add",
        IrBinaryOperator.Subtract => "sub",
        IrBinaryOperator.Multiply => "mul",
        IrBinaryOperator.Divide => "div",
        IrBinaryOperator.Modulo => "mod",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
    };

    private static string HelperSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int32 => "i32",
        ScalarKind.Int64 => "i64",
        ScalarKind.UInt32 => "u32",
        ScalarKind.UInt64 => "u64",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar.Kind, "Not an integer kind."),
    };

    /// <summary>
    /// Emits an explicit conversion (spec 10.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integer narrowing needs no helper here, unlike <c>+</c>, <c>-</c>, and <c>*</c>: C++20
    /// defines the conversion to a signed type as two's complement (P0907R4), and the runtime
    /// header already asserts C++20, so <c>static_cast</c> is the explicit statement of the
    /// wrapping rule rather than a reliance on a default. The same goes for widening to a
    /// floating-point type.
    /// </para>
    /// <para>
    /// The two directions that lose range do need helpers, because C++ makes both undefined rather
    /// than merely implementation-defined: converting a <c>double</c> outside <c>float</c>'s range,
    /// and converting a floating-point value outside the target integer's range.
    /// </para>
    /// </remarks>
    private static string EmitConversion(IrConversion conversion)
    {
        if (conversion.Behavior != ConversionBehavior.WrapOrSaturate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Behavior, "Unhandled conversion behavior.");
        }

        var operand = Expression(conversion.Operand);
        var target = TypeName(conversion.TargetType);

        return conversion.Kind switch
        {
            ConversionKind.Identity => operand,
            ConversionKind.IntegerToInteger or ConversionKind.IntegerToFloat =>
                $"static_cast<{target}>({operand})",
            ConversionKind.FloatToFloat => conversion.TargetType.Kind == ScalarKind.Float
                ? $"{RuntimeNamespace}::narrow_f64_to_f32({operand})"
                : $"static_cast<{target}>({operand})",
            ConversionKind.FloatToInteger =>
                $"{RuntimeNamespace}::trunc_sat_f64_to_{HelperSuffix(conversion.TargetType)}"
                + $"(static_cast<double>({operand}))",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
        };
    }

    private static string EmitLiteral(IrLiteral literal) => literal.Value switch
    {
        null => "{}",
        bool value => value ? "true" : "false",
        long value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        long value => value.ToString(CultureInfo.InvariantCulture),
        ulong value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        double value => FormatDouble(value, literal.LiteralType),
        string value => FormatString(value),
        _ => throw new ArgumentOutOfRangeException(nameof(literal), literal.Value, "Unhandled literal."),
    };

    /// <summary>
    /// Formats a string literal. The lexer decodes escapes, so the IR holds real control
    /// characters; re-escaping them here is what keeps the emitted literal on one line.
    /// </summary>
    private static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        // Octal, not \x: a hex escape in C++ is greedy and would swallow any hex
                        // digit that happens to follow it. Octal escapes stop after three digits.
                        builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        // Non-ASCII passes through as UTF-8; generated files are written as UTF-8.
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Formats a signed integer literal with the suffix its type requires.</summary>
    /// <remarks>
    /// <para>
    /// A negative one is parenthesized, so that it reads as one expression wherever it lands, as
    /// everything else this backend emits does.
    /// </para>
    /// <para>
    /// The most negative value of each type has no literal of its own. C++ has no negative literals,
    /// only negations of positive ones, and the magnitude of each MIN is one more than its type's MAX:
    /// <c>2147483648</c> is not an <c>int</c>, and <c>9223372036854775808</c> is not a <c>long long</c>
    /// -- or anything signed at all. Each is spelled the way <c>&lt;climits&gt;</c> spells it, as MAX
    /// negated less one, which is a constant expression of exactly the right type.
    /// </para>
    /// </remarks>
    private static string FormatInteger(long value, ScalarType scalar)
    {
        if (value == long.MinValue)
        {
            return "(-9223372036854775807LL - 1)";
        }

        if (scalar.Kind == ScalarKind.Int32 && value == int.MinValue)
        {
            return "(-2147483647 - 1)";
        }

        var text = value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);
        return value < 0 ? $"({text})" : text;
    }

    private static string FormatInteger(ulong value, ScalarType scalar)
        => value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);

    private static string IntegerSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int64 => "LL",
        ScalarKind.UInt64 => "ULL",
        ScalarKind.UInt32 => "U",
        _ => string.Empty,
    };

    private static string FormatDouble(double value, PlType type)
    {
        var isFloat = type is ScalarType { Kind: ScalarKind.Float };

        // No literal form in C++; these come from <limits> via the numeric_limits template.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            var cppType = isFloat ? "float" : "double";
            var accessor = double.IsNaN(value) ? "quiet_NaN()" : "infinity()";
            var text2 = $"::std::numeric_limits<{cppType}>::{accessor}";
            return double.IsNegativeInfinity(value) ? $"(-{text2})" : text2;
        }

        // A float is spelled as the float it is, in the fewest digits that give it back, rather than as
        // the double that holds it: both round-trip, but only the first is what an author wrote.
        var text = isFloat
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

        if (!text.Contains('.', StringComparison.Ordinal)
            && !text.Contains('E', StringComparison.Ordinal)
            && !text.Contains('e', StringComparison.Ordinal))
        {
            text += ".0";
        }

        if (isFloat)
        {
            text += "f";
        }

        return double.IsNegative(value) ? $"({text})" : text;
    }

    private static string OperatorText(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "+",
        IrBinaryOperator.Subtract => "-",
        IrBinaryOperator.Multiply => "*",
        IrBinaryOperator.Divide => "/",
        IrBinaryOperator.Modulo => "%",
        IrBinaryOperator.Equal => "==",
        IrBinaryOperator.NotEqual => "!=",
        IrBinaryOperator.LessThan => "<",
        IrBinaryOperator.LessThanOrEqual => "<=",
        IrBinaryOperator.GreaterThan => ">",
        IrBinaryOperator.GreaterThanOrEqual => ">=",
        IrBinaryOperator.LogicalAnd => "&&",
        IrBinaryOperator.LogicalOr => "||",
        IrBinaryOperator.BitwiseAnd => "&",
        IrBinaryOperator.BitwiseOr => "|",
        IrBinaryOperator.BitwiseXor => "^",
        IrBinaryOperator.ShiftLeft => "<<",
        IrBinaryOperator.ShiftRight => ">>",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unhandled operator."),
    };

    private static string QualifiedTypeName(MessageDescriptor message)
    {
        var ns = NameConventions.GetCppNamespace(message.File);
        var name = NameConventions.GetCppTypeName(message);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string QualifiedFunctionName(IrMethodSignature signature)
    {
        var ns = NameConventions.GetCppNamespace(signature.Receiver.File);
        var name = Escape(signature.Name);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string TypeName(PlType type) => type switch
    {
        VoidType => "void",
        ScalarType scalar => scalar.Kind switch
        {
            ScalarKind.Double => "double",
            ScalarKind.Float => "float",
            ScalarKind.Int32 => "::std::int32_t",
            ScalarKind.Int64 => "::std::int64_t",
            ScalarKind.UInt32 => "::std::uint32_t",
            ScalarKind.UInt64 => "::std::uint64_t",
            ScalarKind.Bool => "bool",
            ScalarKind.String or ScalarKind.Bytes => "::std::string",
            _ => throw new ArgumentOutOfRangeException(nameof(type), scalar.Kind, "Unhandled scalar."),
        },
        MessageType message => QualifiedTypeName(message.Descriptor),
        EnumPlType enumType => QualifiedEnumName(enumType.Descriptor),
        RepeatedType repeated => RepeatedTypeName(repeated),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled type."),
    };

    private static string QualifiedEnumName(EnumDescriptor descriptor)
    {
        var ns = NameConventions.GetCppNamespace(descriptor.File);
        var name = NameConventions.GetCppTypeName(descriptor);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    /// <summary>
    /// An enum constant, fully qualified. protoc emits enum values at namespace scope rather than
    /// as members of the enum, so this qualifies the value name and not the type name.
    /// </summary>
    private static string QualifiedEnumValueName(EnumValueDescriptor value)
    {
        var ns = NameConventions.GetCppNamespace(value.EnumDescriptor.File);
        var name = NameConventions.GetCppValueName(value);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string RepeatedTypeName(RepeatedType repeated)
    {
        // protobuf stores message and string elements in RepeatedPtrField, everything else in
        // RepeatedField. Repeated enums are stored as int, not as the enum type.
        var container = repeated.ElementType is MessageType
            or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes }
            ? "RepeatedPtrField"
            : "RepeatedField";

        var element = repeated.ElementType is EnumPlType ? "int" : TypeName(repeated.ElementType);
        return $"::google::protobuf::{container}<{element}>";
    }

    /// <summary>Message-typed parameters are passed by const reference; scalars by value.</summary>
    private static string ParameterTypeName(PlType type) => type switch
    {
        MessageType or RepeatedType or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes }
            => $"const {TypeName(type)}&",
        _ => TypeName(type),
    };

    private static string Escape(string name) => NameConventions.EscapeCppKeyword(name);

    private static string UniqueTestFunctionName(IrTest test, HashSet<string> usedNames)
    {
        var baseName = ToIdentifier($"{test.Target.Receiver.Name}_{test.Target.Name}_{test.Name}");
        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = baseName + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    private static string ToIdentifier(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length == 0 || !(char.IsLetter(builder[0]) || builder[0] == '_'))
        {
            builder.Insert(0, "test_");
        }

        var identifier = builder.ToString().TrimEnd('_');
        return Escape(identifier);
    }

    private static string EscapeString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private sealed class NameAllocator
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Next(string stem)
        {
            var escaped = Escape(stem);
            _counts.TryGetValue(escaped, out var count);
            _counts[escaped] = count + 1;
            return count == 0 ? escaped : escaped + count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
