using System.Xml.Linq;

namespace ProtoCross.Toolchain;

/// <summary>One test a generated C# project actually executed, as its test run reported it.</summary>
/// <param name="Message">
/// What the test said when it failed, empty when it passed or said nothing.
/// </param>
/// <remarks>
/// <b>The message is the whole reason a failure here is actionable.</b> Without it a conformance
/// failure reads "Failed: protocross.conformance.DivisionCase.strict_quotient" followed by the word
/// "Failed" again, which says that a vector did not pass and nothing whatever about why -- and these
/// run in a generated project in a temporary directory that is gone by the time anybody looks. An
/// <c>expect fail</c> vector in particular reports one of five quite different things through this
/// channel: the child never started, never reached the method, returned instead of terminating,
/// exited with the wrong code, or never exited at all. Those have different causes and different
/// fixes, and the run that needed to tell them apart was on a machine nobody can log into.
/// </remarks>
public sealed record ExecutedTest(string Name, string Outcome, string Message)
{
    public bool Passed => string.Equals(Outcome, "Passed", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to show a reader: the message where there is one, the outcome otherwise.</summary>
    public string Detail => string.IsNullOrWhiteSpace(Message) ? Outcome : Message.Trim();
}

/// <summary>Reads the per-test results a <c>dotnet test --logger trx</c> run writes.</summary>
public static class TrxLog
{
    /// <summary>
    /// The tests the newest TRX log under <paramref name="resultsDirectory"/> reports. Empty when the
    /// run wrote none, or wrote one that is not XML.
    /// </summary>
    /// <remarks>
    /// Empty rather than an error, because a run that wrote no log has still said something: the
    /// caller looks each declared test up in what this returns, and finds every one of them missing.
    /// </remarks>
    public static IReadOnlyList<ExecutedTest> ReadLatest(string resultsDirectory)
    {
        if (!Directory.Exists(resultsDirectory))
        {
            return [];
        }

        var trx = Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (trx is null)
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(trx);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        return document.Descendants(ns + "UnitTestResult")
            .Select(element => new ExecutedTest(
                (string?)element.Attribute("testName") ?? string.Empty,
                (string?)element.Attribute("outcome") ?? string.Empty,
                (string?)element.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "Message")
                    ?? string.Empty))
            .ToList();
    }
}
