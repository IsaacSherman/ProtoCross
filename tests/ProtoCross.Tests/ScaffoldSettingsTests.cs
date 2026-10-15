using ProtoCross.Backend;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The settings a project or a flag may state about the test projects <c>--scaffold</c> writes (#30,
/// spec 25.3.2): what each accepts, the flag that states it, and how a flag stands over a project.
/// </summary>
public class ScaffoldSettingsTests
{
    public static TheoryData<string, string, bool> Values => new()
    {
        { "CSharp/TargetFramework", "net8.0", true },
        { "CSharp/TargetFramework", "net10.0", true },
        { "CSharp/TargetFramework", "net11.0", true },
        { "CSharp/TargetFramework", "net9.1", false },
        { "CSharp/TargetFramework", "net8.00", false },
        { "CSharp/TargetFramework", "net7.0", false },
        { "CSharp/TargetFramework", "net6.0", false },
        { "CSharp/TargetFramework", "net8", false },
        { "CSharp/TargetFramework", "net8.0.1", false },
        { "CSharp/TargetFramework", "net08.0", false },
        { "CSharp/TargetFramework", "net8.0-windows", false },
        { "CSharp/TargetFramework", "netstandard2.1", false },
        { "CSharp/TargetFramework", "netcoreapp3.1", false },
        { "CSharp/TargetFramework", "net472", false },
        { "CSharp/TargetFramework", "Net8.0", false },
        { "CSharp/TargetFramework", "net99999999999.0", false },
        { "CSharp/TargetFramework", "", false },
        { "CSharp/ProjectName", "BillingTests", true },
        { "CSharp/ProjectName", "Acme.Billing.Tests", true },
        { "CSharp/ProjectName", "acme_billing2", true },
        { "CSharp/ProjectName", "Acme Tests", false },
        { "CSharp/ProjectName", "Acme..Tests", false },
        { "CSharp/ProjectName", "Acme.Tests.", false },
        { "CSharp/ProjectName", "_Tests", false },
        { "CSharp/ProjectName", "2Tests", false },
        { "CSharp/ProjectName", "Billing-Tests", false },
        { "CSharp/ProjectName", "../Tests", false },
        { "CSharp/ProjectName", "", false },
        { "Cpp/ProjectName", "billing_tests", true },
        { "Cpp/ProjectName", "Billing2", true },
        { "Cpp/ProjectName", "billing-tests", false },
        { "Cpp/ProjectName", "billing.tests", false },
        { "Cpp/ProjectName", "billing tests", false },
        { "Cpp/ProjectName", "_billing", false },
        { "Cpp/ProjectName", "", false },
        { "Cpp/TargetPrefix", "billing", true },
        { "Cpp/TargetPrefix", "acme_billing", true },
        { "Cpp/TargetPrefix", "billing-", false },
        { "Cpp/TargetPrefix", "1st", false },
        { "Cpp/TargetPrefix", "a\"b", false },
        { "Cpp/TargetPrefix", "", false },
    };

    /// <summary>
    /// A value is accepted only where its target can use it as written. Nothing is rewritten into a
    /// value that can be, because a test project answering to a name nobody wrote is not the one asked for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Values))]
    public void ASettingAcceptsOnlyWhatItsTargetCanUseAsWritten(string setting, string value, bool accepted)
    {
        var problem = Setting(setting).ProblemWith(value);

        if (accepted)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains($"'{value}'", problem, StringComparison.Ordinal);
        }
    }

    /// <summary>The flags are published names, typed into build scripts, so their spelling is pinned.</summary>
    [Theory]
    [InlineData("CSharp/TargetFramework", "--csharp-target-framework")]
    [InlineData("CSharp/ProjectName", "--csharp-project-name")]
    [InlineData("Cpp/ProjectName", "--cpp-project-name")]
    [InlineData("Cpp/TargetPrefix", "--cpp-target-prefix")]
    public void EachSettingHasTheFlagItsTargetAndAttributeSpell(string setting, string flag)
        => Assert.Equal(flag, Setting(setting).Flag);

    /// <summary>A setting's target is the one <c>-t</c> names, so a flag can be checked against the build's targets.</summary>
    [Fact]
    public void EachSettingBelongsToATargetTheCommandLineNames()
        => Assert.All(ScaffoldSetting.All, setting => Assert.Contains(setting.Target, new[] { "csharp", "cpp" }));

    /// <summary>The oldest framework a setting accepts is the one the pinned test SDK supports.</summary>
    [Fact]
    public void TheOldestFrameworkAcceptedIsTheOneTheHelpNames()
    {
        var setting = ScaffoldSetting.CSharpTargetFramework;
        var oldest = $"net{ScaffoldSetting.OldestDotNet}.0";

        Assert.Null(setting.ProblemWith(oldest));
        Assert.NotNull(setting.ProblemWith($"net{ScaffoldSetting.OldestDotNet - 1}.0"));
        Assert.Contains(oldest, setting.Help, StringComparison.Ordinal);
    }

    /// <summary>A flag replaces the one answer it gives, and the project's other answers stand.</summary>
    [Fact]
    public void AFlagReplacesOnlyTheSettingItStates()
    {
        var project = new ScaffoldSettings { CSharpProjectName = "BillingTests", CppTargetPrefix = "billing" };
        var flags = new ScaffoldSettings { CppTargetPrefix = "acme" };

        var settings = project.OverriddenBy(flags);

        Assert.Equal(new ScaffoldSettings { CSharpProjectName = "BillingTests", CppTargetPrefix = "acme" }, settings);
    }

    /// <summary>Stating nothing changes nothing, so a build with no flags gets exactly what its project says.</summary>
    [Fact]
    public void NoFlagsLeaveTheProjectsSettings()
    {
        var project = new ScaffoldSettings { CSharpTargetFramework = "net8.0", CppProjectName = "billing_tests" };

        Assert.Equal(project, project.OverriddenBy(ScaffoldSettings.Default));
    }

    /// <summary>Each setting reads and writes its own property, and no other.</summary>
    [Fact]
    public void EachSettingReadsBackWhatItWrote()
    {
        foreach (var setting in ScaffoldSetting.All)
        {
            var settings = setting.With(ScaffoldSettings.Default, "value");

            Assert.Equal("value", setting.ValueIn(settings));
            Assert.All(
                ScaffoldSetting.All.Where(other => other != setting),
                other => Assert.Null(other.ValueIn(settings)));
        }
    }

    private static ScaffoldSetting Setting(string name)
        => Assert.Single(ScaffoldSetting.All, setting => setting.ToString() == name);
}
