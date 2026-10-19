using System.Globalization;
using System.Text;
using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    /// <summary>Exit code a child uses when an <c>expect fail</c> body returned instead of dying.</summary>
    private const int DidNotTerminateExitCode = 91;

    /// <summary>Exit code a child uses when it does not recognize the requested test name.</summary>
    private const int UnknownTestExitCode = 92;

    /// <summary>The local a generated test builds its receiver in, and calls its method on.</summary>
    private const string TestReceiverName = "receiver";

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
        var placement = new Placement(options.ProjectNamespace);

        // The schemas a test names beyond its target's are the ones a literal in it brings: no behavior
        // header has a reason to include those. They come first, as a header's own schemas do.
        var schemaHeaders = ProtoHeadersOf(
            SchemasNamedBeyond(module.Tests.Select(test => test.Target.Receiver.File), module.Tests));
        WriteTestHeader(
            writer,
            options,
            [.. schemaHeaders, .. HeadersTestedBy(module, baseName)],
            hasFailTests,
            hasFloatingPointExpectations);

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
            EmitCppTest(writer, test, functionNames[test], placement);
        }

        return [new GeneratedFile(baseName + NameConventions.TestsSuffix + ".cc", writer.ToString())];
    }

    public IReadOnlyList<GeneratedFile> EmitTestProject(
        ScaffoldOptions options,
        DiagnosticBag diagnostics)
        => [new GeneratedFile(CppTestProject.FileName, CppTestProject.Build(options))];

    /// <summary>
    /// The headers declaring the methods this source's tests call, sorted, each once: this source's
    /// own, and another source's wherever a test calls a method declared there.
    /// </summary>
    /// <param name="baseName">What this source's own header is named after.</param>
    /// <remarks>
    /// <para>
    /// A test may target a method in any source of the compilation (spec 5.3), and the driver calls it,
    /// so the driver includes whichever header declares it. A test whose target is in its own source
    /// includes the header named for this source, as every driver always has.
    /// </para>
    /// <para>
    /// The target is not the only method a test calls. A fixture, an argument or an expectation may
    /// call a method on a literal, <c>new Line { … }.cents()</c>, and that method may be declared in
    /// any source too, so it is asked the same question.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> HeadersTestedBy(IrModule module, string baseName)
        => module.Tests
            .SelectMany(test => MethodsCalledBy(test).Select(method => HeaderDeclaring(method, test, baseName)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The methods a test's driver calls: its target, and any its fixture, arguments and expectation call.</summary>
    private static IEnumerable<IrMethodSignature> MethodsCalledBy(IrTest test)
        => IrWalk.DescendantsAndSelf(test).OfType<IrMethodCall>().Select(call => call.Target).Prepend(test.Target);

    /// <summary>The header declaring <paramref name="method"/>, as the driver for <paramref name="test"/> names it.</summary>
    private static string HeaderDeclaring(IrMethodSignature method, IrTest test, string baseName)
        => test.Document is null || method.Declaration.Document == test.Document
            ? baseName + HeaderExtension
            : HeaderFor(method.Declaration.Document);

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

    private static void EmitCppTest(SourceWriter writer, IrTest test, string functionName, Placement placement)
    {
        if (test.Expectation is IrTestFailExpectation)
        {
            EmitCppFailTest(writer, test, functionName, placement);
            return;
        }

        using var scope = writer.Block($"static bool {functionName}()");

        EmitCppReceiver(writer, test, placement);

        if (test.Target.CanRefuse)
        {
            EmitCppRefusingCall(writer, test, placement);
        }
        else
        {
            writer.WriteLine($"const auto {IrExpectedFields.ResultName} = {CppInvocation(test, placement)};");
        }

        EmitCppComparison(writer, test, test.Expectation.Compared(), placement);

        writer.WriteLine();
        writer.WriteLine($"::std::cout << \"[ok] {EscapeString(test.Identity)}\" << ::std::endl;");
        writer.WriteLine("return true;");
    }

    /// <summary>
    /// Compares what the method gave, held in <c>actual</c>, with what the test expects of it: a value, or the
    /// fields an expected literal sets (spec 25.3).
    /// </summary>
    private static void EmitCppComparison(SourceWriter writer, IrTest test, IrTestExpectation compared, Placement placement)
    {
        switch (compared)
        {
            case IrTestReturnExpectation returnExpectation:
                writer.WriteLine($"const auto expected = {Expression(returnExpectation.Value, placement)};");
                using (writer.Block($"if ({ExpectationUnmet(returnExpectation.Value.Type)})"))
                {
                    // Printed on stdout rather than stderr so a harness reading the driver sees every
                    // result line in the order they happened.
                    writer.WriteLine(
                        $"::std::cout << \"[FAIL] {EscapeString(test.Identity)}\""
                        + " << \" (expected \" << expected << \", actual \" << actual << \")\" << ::std::endl;");
                    writer.WriteLine("return false;");
                }

                break;

            case IrTestFieldsExpectation fields:
                EmitCppFieldChecks(writer, test, fields, placement);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(test), test.Expectation, "Unhandled C++ test expectation.");
        }
    }

    /// <summary>
    /// Calls a method that may refuse, and fails the test unless it built its result or refused as the test
    /// expects. Whichever the test expects is left in <c>actual</c>, which the comparison after it reads as it
    /// reads any result.
    /// </summary>
    /// <remarks>
    /// The refusal is not printed: C++ has no one way to print a message, and a scalar's would be the only one shown.
    /// </remarks>
    private static void EmitCppRefusingCall(SourceWriter writer, IrTest test, Placement placement)
    {
        var target = test.Target;
        var refuses = test.Expectation is IrTestRefusalExpectation;
        var result = target.ReturnType is VoidType ? null : refuses ? "built" : IrExpectedFields.ResultName;
        var refusal = refuses ? IrExpectedFields.ResultName : "refusal";

        if (result is not null)
        {
            writer.WriteLine($"{TypeName(target.ReturnType)} {result}{{}};");
        }

        writer.WriteLine($"{TypeName(target.Refusal!)} {refusal}{{}};");
        var call = CppInvocation(test, placement, [.. new[] { result, refusal }.OfType<string>()]);
        using (writer.Block(refuses ? $"if ({call})" : $"if (!{call})"))
        {
            var instead = refuses ? "a refusal, but the method built its result" : "a result, but the method refused";
            writer.WriteLine($"::std::cout << \"[FAIL] {EscapeString(test.Identity)} (expected {instead})\" << ::std::endl;");
            writer.WriteLine("return false;");
        }
    }

    /// <summary>
    /// Emits what an expected message literal checks: each field the literal sets, of the local the method's
    /// result is held in, in the order <see cref="IrExpectedFields"/> gives (spec 25.3).
    /// </summary>
    /// <remarks>
    /// Each value is compared in a block of its own, as an <c>expect return</c> of it alone would be, so
    /// that every one is named <c>expected</c> and <c>found</c> and a failure prints both beside the field.
    /// </remarks>
    private static void EmitCppFieldChecks(SourceWriter writer, IrTest test, IrTestFieldsExpectation fields, Placement placement)
    {
        var expected = IrExpectedFields.Of(fields);

        foreach (var check in expected.Checks)
        {
            var failed = $"::std::cout << \"[FAIL] {EscapeString(test.Identity)} ('{EscapeString(check.Path)}'";
            switch (check)
            {
                case IrPresenceCheck presence:
                    using (writer.Block($"if (!{Expression(presence.Presence, placement)})"))
                    {
                        writer.WriteLine($"{failed} is not set)\" << ::std::endl;");
                        writer.WriteLine("return false;");
                    }

                    break;

                case IrValueCheck value:
                    using (writer.Block(string.Empty))
                    {
                        writer.WriteLine($"const auto expected = {Expression(value.Expected, placement)};");
                        writer.WriteLine($"const auto found = {Expression(value.Actual, placement)};");
                        using (writer.Block($"if ({ExpectationUnmet(value.Actual.Type, "found")})"))
                        {
                            writer.WriteLine($"{failed}: expected \" << expected << \", actual \" << found << \")\" << ::std::endl;");
                            writer.WriteLine("return false;");
                        }
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(fields), check, "Unhandled field check.");
            }
        }
    }

    /// <summary>Whether a test expects a floating-point value, and so has a NaN case to meet.</summary>
    /// <remarks>
    /// Asked by the condition each such test emits and by the <c>&lt;cmath&gt;</c> include that
    /// condition needs, so the two cannot fall out of step. A field an expected literal checks counts
    /// as one, since each is compared as an expectation of its own.
    /// </remarks>
    private static bool ExpectsFloatingPoint(IrTest test) => test.Expectation.Compared() switch
    {
        IrTestReturnExpectation returned => IsFloatingPoint(returned.Value.Type),
        IrTestFieldsExpectation fields => IrExpectedFields.Of(fields).Checks.OfType<IrValueCheck>().Any(check => IsFloatingPoint(check.Actual.Type)),
        _ => false,
    };

    private static bool IsFloatingPoint(PlType type) => type is ScalarType { IsFloatingPoint: true };

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
    /// <param name="type">What is compared: the value expected and the one returned are both of it.</param>
    /// <param name="actual">The local holding the value returned.</param>
    private static string ExpectationUnmet(PlType type, string actual = "actual")
        => IsFloatingPoint(type)
            ? $"!({actual} == expected || (::std::isnan({actual}) && ::std::isnan(expected)))"
            : $"{actual} != expected";

    /// <summary>
    /// Emits the body a child process runs for an <c>expect fail</c> test. It is never called in
    /// the parent, because the call it makes is expected to end the process.
    /// </summary>
    private static void EmitCppFailTest(SourceWriter writer, IrTest test, string functionName, Placement placement)
    {
        using var scope = writer.Block($"static void {functionName}()");

        EmitCppReceiver(writer, test, placement);

        writer.WriteLine("// The call is expected not to return, so its result is deliberately unused.");
        if (test.Target.CanRefuse)
        {
            // A refusal is a return, which the parent reports as one.
            var outs = test.Target.ReturnType is VoidType ? new[] { "refusal" } : ["result", "refusal"];
            if (test.Target.ReturnType is not VoidType)
            {
                writer.WriteLine($"{TypeName(test.Target.ReturnType)} result{{}};");
            }

            writer.WriteLine($"{TypeName(test.Target.Refusal!)} refusal{{}};");
            writer.WriteLine($"static_cast<void>({CppInvocation(test, placement, outs)});");
            return;
        }

        writer.WriteLine(test.Target.ReturnType is VoidType
            ? $"{CppInvocation(test, placement)};"
            : $"static_cast<void>({CppInvocation(test, placement)});");
    }

    /// <summary>
    /// Declares the local named <c>receiver</c> that a test calls its method on; nothing for a factory's
    /// method, which is called on nothing.
    /// </summary>
    private static void EmitCppReceiver(SourceWriter writer, IrTest test, Placement placement)
    {
        if (test.Target.HasReceiver)
        {
            EmitConstruction(writer, test.Receiver, TestReceiverName, NamesFor(test.Receiver, TestReceiverName), placement);
        }
    }

    /// <param name="outs">What a call to a method that may refuse passes for the references it gives its result and refusal through.</param>
    private static string CppInvocation(IrTest test, Placement placement, params string[] outs)
    {
        IReadOnlyList<string> receiver = test.Target.HasReceiver ? [TestReceiverName] : [];

        return InOrder(
            test,
            [.. test.Arguments.Select(argument => argument.Value)],
            [.. test.Arguments.Select(argument => Expression(argument.Value, placement))],
            arguments => $"{placement.QualifiedFunctionOf(test.Target)}({string.Join(", ", receiver.Concat(arguments).Concat(outs))})",
            placement,
            [TestReceiverName, .. outs]);
    }

    private static string UniqueTestFunctionName(IrTest test, HashSet<string> usedNames)
    {
        // A factory's method is named as the test targets it, so its test reads 'Invoice.factory.create'.
        var target = test.Target.HasReceiver
            ? $"{test.Target.Receiver.Name}_{test.Target.Name}"
            : $"{test.Target.Receiver.Name}_{ContextualKeywords.Factory}_{test.Target.Name}";
        var baseName = ToIdentifier($"{target}_{test.Name}");
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
}
