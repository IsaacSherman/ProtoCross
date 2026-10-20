using System.Text.RegularExpressions;
using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Preflight accepts config packages in every nested layout CMake itself searches.</summary>
public class CMakeConfigPackageReviewTests
{
    [Theory]
    [InlineData("Protobuf-6.33/cmake/protobuf", 3, 25)]
    [InlineData("Protobuf-6.33/lib/cmake/protobuf", 3, 20)]
    [InlineData("Protobuf-6.33/lib/protobuf/cmake", 3, 20)]
    public void AConfigPackageThatCMakeFindsUnderANamedPrefixIsAccepted(string layout, int major, int minor)
    {
        var cmake = TestToolchain.RequireCMake();
        var versionStart = ProcessRunner.Create(cmake);
        versionStart.ArgumentList.Add("--version");
        var version = ProcessRunner.Run(versionStart);
        Assert.True(version.ExitCode == 0, version.Output);
        var versionText = Regex.Match(version.Output, @"cmake version (\d+\.\d+\.\d+)").Groups[1].Value;
        Assert.NotEmpty(versionText);
        if (Version.Parse(versionText) < new Version(major, minor))
        {
            Assert.Skip($"CMake {major}.{minor} is needed for the '{layout}' search layout.");
        }

        var prefix = TestPaths.CreateTempDirectory();
        var include = Path.Combine(prefix, "include");
        var headers = Directory.CreateDirectory(Path.Combine(include, "google", "protobuf")).FullName;
        File.WriteAllText(Path.Combine(headers, "message.h"), "// header discovery fixture\n");
        var package = Directory.CreateDirectory(Path.Combine(prefix, layout)).FullName;
        File.WriteAllText(Path.Combine(package, "ProtobufConfig.cmake"), "set(Protobuf_FOUND TRUE)\n");

        var project = TestPaths.CreateTempDirectory();
        var build = Path.Combine(project, "build");
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.20)
            project(ConfigLayoutReview NONE)
            find_package(Protobuf CONFIG REQUIRED PATHS "${REVIEW_PREFIX}" NO_DEFAULT_PATH)
            file(WRITE "${CMAKE_BINARY_DIR}/selected-package.txt" "${Protobuf_DIR}")
            """);
        var start = ProcessRunner.Create(cmake, project);
        foreach (var argument in new[] { "-S", project, "-B", build, "-DREVIEW_PREFIX=" + prefix })
        {
            start.ArgumentList.Add(argument);
        }

        var configured = ProcessRunner.Run(start);
        Assert.True(configured.ExitCode == 0, configured.Output);
        var selected = File.ReadAllText(Path.Combine(build, "selected-package.txt"));
        Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(selected));

        var lookup = ToolLocator.FindBuildableProtobufCpp(project, configuredInclude: include);
        Assert.True(lookup.Found,
            $"CMake found '{selected}', but preflight rejected the same prefix: {lookup.Missing}");
    }
}
