using System.Globalization;
using ProtoCross.Backend;
using ProtoCross.Toolchain;

namespace ProtoCross.Tests.Harness;

/// <summary>The outcome of building and running one generated C++ test driver.</summary>
/// <param name="ExitCode">
/// The driver's own exit code, or <see cref="ProcessResult.NotRun"/> when it never got that far
/// because the compile or link step failed.
/// </param>
internal sealed record CppProgramResult(string DriverSource, int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>How the driver failed, whatever its lines said: null when it built, ran and exited 0.</summary>
    public string? Failure => ExitCode switch
    {
        0 => null,
        ProcessResult.NotRun => $"the driver {DriverSource} did not build",
        var code => $"the driver {DriverSource} exited with code {code}",
    };
}

/// <summary>
/// A throwaway C++ workspace that compiles ProtoCross's generated header and generated test driver
/// together with protoc's C++ output, then runs the driver.
/// </summary>
/// <remarks>
/// Several drivers share one protobuf schema, so the generated <c>.pb.cc</c> is compiled once into a
/// library every driver links. That compile is by far the most expensive step, and repeating it per
/// driver would dominate the suite's runtime.
/// </remarks>
internal sealed class CppTestWorkspace
{
    private const string SharedTarget = "protocross_shared";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(15);

    private CppTestWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public static CppTestWorkspace Create(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protocross-" + label, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new CppTestWorkspace(directory);
    }

    public void Write(IEnumerable<GeneratedFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(Directory, file.RelativePath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Contents);
        }
    }

    public ProcessResult GenerateProtobuf(string protoc, string protoPath, params string[] protoFiles)
        => TestToolchain.RunProtoc(protoc, "cpp_out", protoPath, Directory, protoFiles);

    /// <summary>Parses a source file without producing an object, to check the emitted C++ is valid.</summary>
    public ProcessResult RunSyntaxOnly(CppCompiler compiler, string sourcePath, string protobufInclude)
    {
        switch (compiler.Kind)
        {
            case CppCompilerKind.ClangOrGcc:
            {
                var startInfo = ProcessRunner.Create(compiler.FileName, Directory);
                startInfo.ArgumentList.Add("-std=c++20");
                startInfo.ArgumentList.Add("-fsyntax-only");
                startInfo.ArgumentList.Add($"-I{Directory}");
                startInfo.ArgumentList.Add($"-I{protobufInclude}");
                startInfo.ArgumentList.Add(sourcePath);
                return ProcessRunner.Run(startInfo, BuildTimeout);
            }

            case CppCompilerKind.Msvc:
            {
                string[] arguments =
                [
                    "/nologo", "/std:c++20", "/Zc:__cplusplus", "/EHsc", "/Zs",
                    $"/I{Directory}", $"/I{protobufInclude}", sourcePath,
                ];

                if (compiler.VisualStudioDevCommand is null)
                {
                    var startInfo = ProcessRunner.Create(compiler.FileName, Directory);
                    foreach (var argument in arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }

                    return ProcessRunner.Run(startInfo, BuildTimeout);
                }

                var responsePath = Path.Combine(Directory, "msvc-syntax.rsp");
                File.WriteAllLines(responsePath, arguments.Select(ProcessRunner.QuoteRspArgument));

                return ProcessRunner.RunCmdScript(
                    Path.Combine(Directory, "msvc-syntax.cmd"),
                    [
                        "@echo off",
                        $"call \"{compiler.VisualStudioDevCommand}\" -arch=x64 -host_arch=x64",
                        "if errorlevel 1 exit /b %errorlevel%",
                        $"cl.exe @{ProcessRunner.QuoteForCmd(responsePath)}",
                    ],
                    Directory,
                    BuildTimeout);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(compiler), compiler.Kind, "Unknown compiler kind.");
        }
    }

    /// <summary>
    /// Builds the shared sources once and one executable per driver with CMake, against protobuf's CMake
    /// package, then runs each driver that built.
    /// </summary>
    /// <param name="driverSources">Generated <c>.tests.cc</c> file names, relative to the workspace.</param>
    /// <param name="sharedSources">
    /// Sources every driver links, relative to the workspace: protoc's <c>.pb.cc</c> files, usually.
    /// </param>
    /// <remarks>
    /// <para>
    /// CMake, as <c>protocross test</c> builds the project <c>--scaffold</c> writes, rather than a compile line
    /// of the harness's own. The link is the hard part: protobuf 22 and later split Abseil and
    /// <c>utf8_range</c> out of <c>libprotobuf</c>, and which libraries a program needs varies with the
    /// version, the platform, and whether the install is static or shared. The harness once linked vcpkg's
    /// Windows libraries by name through MSVC and <c>-lprotobuf</c> alone everywhere else, and the second
    /// never ran, because no POSIX install was ever found linkable (#18). <c>protobuf::libprotobuf</c>
    /// carries its own dependencies on every platform, so one build serves them all, and an install the
    /// suite can link is exactly one <c>protocross test</c> can build.
    /// </para>
    /// <para>
    /// The shared sources are one static library, built before any driver, because protoc's output is by
    /// far the most expensive thing here and nothing links without it. The drivers are then built in one
    /// step and run here rather than by ctest, so that each keeps its own output and exit code. Only a
    /// driver that step left unbuilt is built again on its own, so a compile error is reported against the
    /// driver that has it rather than against every driver a stopped build never reached.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CppProgramResult> BuildAndRun(
        ProtobufCppInstall protobuf,
        IReadOnlyList<string> driverSources,
        IReadOnlyList<string> sharedSources)
    {
        var cmakeLookup = ToolLocator.FindCMake();
        if (!cmakeLookup.Found)
        {
            return NotBuilt(driverSources, cmakeLookup.Missing);
        }

        var cmake = cmakeLookup.Tool;
        File.WriteAllText(Path.Combine(Directory, "CMakeLists.txt"), BuildProject(driverSources, sharedSources));

        var configured = RunCMake(
            cmake, "-S", Directory, "-B", BuildDirectory, "-DCMAKE_PREFIX_PATH=" + protobuf.Prefix, "-DCMAKE_BUILD_TYPE=Debug");
        if (configured.ExitCode != 0)
        {
            return NotBuilt(driverSources, configured.Output);
        }

        if (sharedSources.Count > 0 && Build(cmake, "--target", SharedTarget) is { ExitCode: not 0 } shared)
        {
            return NotBuilt(driverSources, shared.Output);
        }

        var built = Build(cmake);
        return driverSources.Select((driver, index) => Run(cmake, driver, index, built)).ToList();
    }

    private string BuildDirectory => Path.Combine(Directory, "build");

    private static string DriverTarget(int index) => $"protocross_driver_{index}";

    /// <summary>Where a driver's executable is put, for single- and multi-configuration generators alike.</summary>
    private string ExecutableOf(int index)
        => Path.Combine(BuildDirectory, "bin", "Debug", DriverTarget(index) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    /// <summary>Runs one driver, building it on its own first where the build of every driver did not.</summary>
    private CppProgramResult Run(string cmake, string driver, int index, ProcessResult built)
    {
        if (built.ExitCode != 0 && !File.Exists(ExecutableOf(index))
            && Build(cmake, "--target", DriverTarget(index)) is { ExitCode: not 0 } alone)
        {
            return new CppProgramResult(driver, ProcessResult.NotRun, alone.Output);
        }

        if (!File.Exists(ExecutableOf(index)))
        {
            return new CppProgramResult(driver, ProcessResult.NotRun, built.Output);
        }

        var ran = ProcessRunner.Run(ProcessRunner.Create(ExecutableOf(index), Directory), BuildTimeout);
        return new CppProgramResult(driver, ran.ExitCode, ran.Output);
    }

    /// <summary>Builds the workspace's project, or the targets <paramref name="targetArguments"/> name.</summary>
    /// <remarks>
    /// The job count is stated because <c>--parallel</c> alone hands make a bare <c>-j</c>, which runs every
    /// compile at once: the conformance vectors then started a compiler per schema and per driver together
    /// and ran a 16 GB Linux machine out of memory. MSBuild and <c>/MP</c> already stop at the processor count.
    /// </remarks>
    private ProcessResult Build(string cmake, params string[] targetArguments)
        => RunCMake(
            cmake,
            [
                "--build", BuildDirectory, "--config", "Debug",
                "--parallel", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
                .. targetArguments,
            ]);

    private ProcessResult RunCMake(string cmake, params string[] arguments)
    {
        var startInfo = ProcessRunner.Create(cmake, Directory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Visual Studio's generator builds with MSBuild, which the test host's own MSBuild variables mislead.
        ProcessRunner.ScrubMsBuildEnvironment(startInfo);

        // A parallel MSBuild leaves its worker nodes running for reuse, and they hold this build's output
        // pipe open, so reading the output would wait for them to time out rather than for the build.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return ProcessRunner.Run(startInfo, BuildTimeout);
    }

    /// <summary>
    /// The CMake project for one workspace: the shared sources as a library linking
    /// <c>protobuf::libprotobuf</c>, and an executable per driver linking that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settings that matter are the ones <c>CppTestProject</c> writes for the same reasons: C++20,
    /// <c>/Zc:__cplusplus</c> so MSVC reports it, <c>find_package(Protobuf CONFIG)</c> so the dependencies come
    /// with the library, and the runtime DLLs copied beside each executable, which matters only on Windows.
    /// </para>
    /// <para>
    /// <c>/MP</c> because MSBuild compiles a project's files one at a time without it, and the shared library
    /// holds every schema's <c>.pb.cc</c>; a Makefile build parallelizes by file already. Executables go to
    /// one directory named for the configuration under either kind of generator, because a generator
    /// expression in it stops a multi-configuration generator adding its own.
    /// </para>
    /// </remarks>
    private static string BuildProject(IReadOnlyList<string> driverSources, IReadOnlyList<string> sharedSources)
    {
        var lines = new List<string>
        {
            "cmake_minimum_required(VERSION 3.21)",
            "project(protocross_harness CXX)",
            "set(CMAKE_CXX_STANDARD 20)",
            "set(CMAKE_CXX_STANDARD_REQUIRED ON)",
            "if(MSVC)",
            "    add_compile_options(/Zc:__cplusplus /MP)",
            "endif()",
            "find_package(Protobuf CONFIG REQUIRED)",
            "set(CMAKE_RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_BINARY_DIR}/bin/$<CONFIG>\")",
        };

        var linked = "protobuf::libprotobuf";
        if (sharedSources.Count > 0)
        {
            lines.Add($"add_library({SharedTarget} STATIC {string.Join(' ', sharedSources.Select(Quote))})");
            lines.Add($"target_include_directories({SharedTarget} PUBLIC \"${{CMAKE_CURRENT_SOURCE_DIR}}\")");
            lines.Add($"target_link_libraries({SharedTarget} PUBLIC protobuf::libprotobuf)");
            linked = SharedTarget;
        }

        for (var index = 0; index < driverSources.Count; index++)
        {
            var target = DriverTarget(index);
            lines.Add($"add_executable({target} {Quote(driverSources[index])})");
            lines.Add($"target_include_directories({target} PRIVATE \"${{CMAKE_CURRENT_SOURCE_DIR}}\")");
            lines.Add($"target_link_libraries({target} PRIVATE {linked})");
            lines.Add($"add_custom_command(TARGET {target} POST_BUILD");
            lines.Add($"    COMMAND \"${{CMAKE_COMMAND}}\" -E copy -t \"$<TARGET_FILE_DIR:{target}>\" \"$<TARGET_RUNTIME_DLLS:{target}>\"");
            lines.Add("    COMMAND_EXPAND_LISTS)");
        }

        return string.Join('\n', lines) + "\n";

        static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";
    }

    private static List<CppProgramResult> NotBuilt(IReadOnlyList<string> driverSources, string output)
        => [.. driverSources.Select(driver => new CppProgramResult(driver, ProcessResult.NotRun, output))];
}
