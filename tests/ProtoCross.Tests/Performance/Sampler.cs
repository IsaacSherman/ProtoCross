using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.Runtime;
using System.Text;
using ProtoCross.LanguageServer.Hosting;

namespace ProtoCross.Tests.Performance;

/// <summary>What one operation cost, over enough runs for the number to mean something.</summary>
/// <param name="Milliseconds">Every sample, in the order taken.</param>
internal sealed record Sample(string Operation, string Corpus, string Warmth, IReadOnlyList<double> Milliseconds)
{
    /// <summary>The statistics, computed the one way this repository computes them.</summary>
    /// <remarks>
    /// The arithmetic used to live here and now lives in <see cref="LatencySample"/>, because the
    /// running server reports its own p95 in the status command and two nearest-rank implementations
    /// would eventually stop agreeing -- leaving a user comparing a status report against the
    /// measured results in <c>docs/performance.md</c> with two numbers that do not mean the same
    /// thing. What stays here is what the benchmark adds: which operation, which corpus, and whether
    /// it was warm.
    /// </remarks>
    private LatencySample Latencies => new(Milliseconds);

    public double Median => Latencies.Median;

    public double P95 => Latencies.P95;

    public double Min => Latencies.Min;

    public double Max => Latencies.Max;
}

/// <summary>
/// Runs an operation enough times to report a percentile, and throws the first runs away.
/// </summary>
/// <remarks>
/// <para>
/// <b>The discarded runs are not a courtesy.</b> The first call through any of these paths pays for
/// JIT, for a type initializer, and for a cache that has not been touched yet, and including it
/// would put a one-off cost into a per-keystroke number. What the budgets describe is the hundredth
/// hover of a session, not the first.
/// </para>
/// <para>
/// <b>No timing assertion lives outside <c>PROTOCROSS_BENCH</c>.</b> A wall-clock deadline on a
/// shared runner is a coin toss with a build attached, and this repository has already spent two
/// commits on deadlines that were fine locally. What CI checks instead is counted work, which is
/// deterministic -- see <c>PerformanceCostTests</c>.
/// </para>
/// </remarks>
internal static class Sampler
{
    /// <summary>Whether the measurement suite was asked for.</summary>
    public static bool Requested
        => Environment.GetEnvironmentVariable("PROTOCROSS_BENCH") is { Length: > 0 };

    /// <summary>Whether the code being measured was compiled with the JIT optimizer on.</summary>
    /// <remarks>
    /// <para>
    /// <b>A Debug build is not slower by a constant, so a Debug reading is not a Release reading with
    /// a factor missing.</b> It was 1.0x on some rows here and 1.8x on others, and the rows it moved
    /// most were the ones on the larger corpus -- which is to say, the rows the budgets are defined
    /// against. Measuring one and publishing it as the other is how "completion is at 48 ms against a
    /// 50 ms budget and will not stay there by accident" got written about an operation that runs at
    /// 29 ms.
    /// </para>
    /// <para>
    /// Asked of the assembly under measurement rather than of this one. They are built together
    /// today, so the two answers agree -- but the question is about the code being timed, and
    /// phrasing it that way means it stays right if that ever stops being true.
    /// </para>
    /// <para>
    /// This is the check BenchmarkDotNet makes by refusing to run at all on a non-optimized assembly.
    /// It is worth having whether or not that library is ever adopted, and
    /// <c>PerformanceMeasurementTests.TheOptimizerCheckAgreesWithTheBuildConfiguration</c> pins that
    /// the detection itself works.
    /// </para>
    /// </remarks>
    public static bool Optimized
        => typeof(ProtoCross.Compilation).Assembly
            .GetCustomAttribute<System.Diagnostics.DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    /// <summary>What the report says the measurement was taken on.</summary>
    public static string Configuration => Optimized ? "Release" : "Debug (not a measurement)";

    /// <summary>Whether this process runs the garbage collector the language server ships with.</summary>
    /// <remarks>
    /// <para>
    /// The test project asks for the server collector, because the resilience sweeps need it to use
    /// every core, and the language server ships with the workstation one. The two collect at
    /// different moments and pause for different lengths of time, and the pauses are where a p95
    /// comes from, so a latency taken under the one the server does not run describes a process
    /// nobody runs.
    /// </para>
    /// <para>
    /// A collector is chosen once, when the process starts, so no test can switch it for itself. A
    /// benchmark run starts the process with <c>DOTNET_gcServer=0</c>, which overrides the project,
    /// and this is how the benchmark knows that it did.
    /// </para>
    /// </remarks>
    public static bool ShippedCollector => !GCSettings.IsServerGC;

    /// <summary>What the report says the measurement was taken under.</summary>
    public static string Collector => ShippedCollector ? "workstation GC" : "server GC (not a measurement)";

    /// <summary>The one command that measures, as every refusal to measure quotes it.</summary>
    public const string Command =
        "PROTOCROSS_BENCH=1 DOTNET_gcServer=0 dotnet test ProtoCross.slnx -c Release "
        + "--filter \"FullyQualifiedName~Performance\"";

    /// <summary>Runs <paramref name="operation"/> and reports what it cost.</summary>
    public static Sample Time(
        string name,
        string corpus,
        string warmth,
        Action operation,
        int iterations = 20,
        int discarded = 5)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var run = 0; run < discarded; run++)
        {
            operation();
        }

        var samples = new List<double>(iterations);
        var clock = new Stopwatch();

        for (var run = 0; run < iterations; run++)
        {
            clock.Restart();
            operation();
            clock.Stop();

            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return new Sample(name, corpus, warmth, samples);
    }
}

/// <summary>
/// Everything one measurement run found, written where a person can read it afterwards.
/// </summary>
/// <remarks>
/// A run that only asserted would say "too slow" and nothing else, and the next question is always
/// "by how much, and was it always?". The report answers that without anybody re-running anything,
/// and it is what gets pasted into the dated results section of <c>docs/performance.md</c>.
/// </remarks>
internal sealed class PerformanceReport
{
    private readonly List<Sample> _samples = [];
    private readonly List<string> _notes = [];

    /// <summary>Whether this section carries the run's heading, which only the first one does.</summary>
    private bool _first;

    public IReadOnlyList<Sample> Samples => _samples;

    public void Add(Sample sample) => _samples.Add(sample);

    public void Note(string note) => _notes.Add(note);

    /// <summary>Where a run leaves its report.</summary>
    public static string Directory
        => System.IO.Path.Combine(TestPaths.RepositoryRoot, "artifacts", "perf");

    /// <summary>The one report file this process is writing.</summary>
    public static string Path => System.IO.Path.Combine(Directory, "report.md");

    private static readonly Lock Gate = new();
    private static bool _started;

    /// <summary>
    /// Adds this report's section to the run's one report file.
    /// </summary>
    /// <remarks>
    /// <b>Append, with the first writer truncating.</b> The measurements are several tests, and
    /// xUnit does not promise which runs first -- so a report that each test wrote in full would
    /// hold whichever section happened to finish last and silently lose the rest. That is not
    /// hypothetical: it is what the first version of this did, and the descriptor numbers vanished
    /// behind the budget table. Truncating once per process, under a lock, makes the file the whole
    /// run regardless of order.
    /// </remarks>
    public string Append()
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (!_started)
            {
                File.WriteAllText(Path, string.Empty);
                _started = true;
                _first = true;
            }

            File.AppendAllText(Path, Render() + "\n");
        }

        return Path;
    }

    public string Render()
    {
        var report = new StringBuilder();

        if (_first)
        {
            report.Append(Heading());
        }

        // A section with only notes gets no table. An empty one with a header row reads as a
        // measurement that found nothing rather than as a measurement that is not a table.
        if (_samples.Count > 0)
        {
            report.Append("| Operation | Corpus | Warmth | Median | p95 | Min | Max | Budget | |\n");
            report.Append("|---|---|---|---:|---:|---:|---:|---:|---|\n");
        }

        foreach (var sample in _samples)
        {
            var budget = PerformanceBudgets.Find(sample.Operation);
            var ceiling = budget is null ? "-" : Milliseconds(budget.Milliseconds);
            var verdict = budget is null
                ? "measured"
                : sample.P95 <= budget.Milliseconds ? "within" : "**over**";

            report.Append($"| {sample.Operation} | {sample.Corpus} | {sample.Warmth} ");
            report.Append($"| {Milliseconds(sample.Median)} | {Milliseconds(sample.P95)} ");
            report.Append($"| {Milliseconds(sample.Min)} | {Milliseconds(sample.Max)} | {ceiling} | {verdict} |\n");
        }

        if (_notes.Count > 0)
        {
            report.Append("\n## Notes\n\n");

            foreach (var note in _notes)
            {
                report.Append($"- {note}\n");
            }
        }

        return report.ToString();
    }

    /// <summary>What every report opens with: where the numbers came from, and off what build.</summary>
    /// <remarks>
    /// Its own method so that it can be asserted on without writing a file, which is how
    /// <c>PerformanceMeasurementTests</c> pins that the configuration is stated at all. That is not a
    /// cosmetic property: a table of milliseconds carrying no build configuration is a table somebody
    /// pastes into a document, and the figures in this repository's own documentation were pasted
    /// from exactly such a table, off a Debug build.
    /// </remarks>
    public static string Heading()
    {
        var heading = new StringBuilder();

        heading.Append("# Performance measurement\n\n");
        heading.Append($"Taken {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName}, ");
        heading.Append($"{Environment.ProcessorCount} processors, {RuntimeName()}, ");
        heading.Append($"**{Sampler.Configuration}**, **{Sampler.Collector}**.\n\n");
        heading.Append("Normal corpus is `examples/simpleScript.pcross` at ");
        heading.Append(PerformanceCorpus.Lines(PerformanceCorpus.Normal).ToString(CultureInfo.InvariantCulture));
        heading.Append(" lines; stress is `tests/perf/corpus/wide.pcross` at ");
        heading.Append(PerformanceCorpus.Lines(PerformanceCorpus.Stress).ToString(CultureInfo.InvariantCulture));
        heading.Append(" lines.\n\n");

        return heading.ToString();
    }

    private static string Milliseconds(double value)
        => value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " ms";

    private static string RuntimeName()
        => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
