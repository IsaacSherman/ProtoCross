using System.Text.Json;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using ProtoLang.Tests.Harness;
using Xunit;
using DiagnosticSeverity = ProtoLang.LanguageServer.Protocol.Lsp.DiagnosticSeverity;
using WorkspaceFolder = ProtoLang.LanguageServer.Workspace.WorkspaceFolder;

namespace ProtoLang.Tests;

/// <summary>
/// Workspace trust (spec 10.4.1): what a repository nobody has trusted may configure, and what the
/// user is told about what it may not.
/// </summary>
/// <remarks>
/// The property the rest exist for is the one #55 says to test directly -- a repository whose committed
/// settings name a protoc does not get that program run before the user trusts it. It is asserted on
/// a file the program writes when it starts, not on anything the server says, because a server that
/// ran it and then threw the answer away would say exactly the right things.
/// </remarks>
public class WorkspaceTrustTests
{
    /// <summary>
    /// A file standing where a protoc could be named. Resolution checks that a named protoc exists and
    /// never starts it, so this needs to be nothing more than present.
    /// </summary>
    private static string FileNamedProtoc()
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), OperatingSystem.IsWindows() ? "protoc.exe" : "protoc");
        File.WriteAllText(path, string.Empty);

        return path;
    }

    /// <summary>A workspace whose one folder states <paramref name="settings"/>, and a document in it.</summary>
    private static (WorkspaceConfiguration Configuration, DocumentUri Document) Folder(
        ProtoLangSettings settings,
        WorkspaceTrust trust)
    {
        var directory = TestPaths.CreateTempDirectory();
        var configuration = WorkspaceConfiguration.Empty with
        {
            Folders = [WorkspaceFolder.FromPath(directory, settings: settings)],
            ReadEnvironmentVariable = _ => null,
            Trust = trust,
        };

        return (configuration, DocumentUri.FromPath(Path.Combine(directory, "source.protolang")));
    }

    /// <summary>The same workspace, with <paramref name="settings"/> stated at the scope named.</summary>
    private static (WorkspaceConfiguration Configuration, DocumentUri Document) Stating(
        ConfigurationSource scope,
        ProtoLangSettings settings,
        WorkspaceTrust trust)
    {
        var (configuration, document) = Folder(scope is ConfigurationSource.FolderSetting ? settings : ProtoLangSettings.None, trust);

        return scope switch
        {
            ConfigurationSource.FolderSetting => (configuration, document),
            ConfigurationSource.WorkspaceSetting => (configuration with { Workspace = settings }, document),
            ConfigurationSource.UserSetting => (configuration with { User = settings }, document),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Not a scope a setting is written at."),
        };
    }

    private static ConfigurationSync Sync(Action<string>? announced = null)
    {
        var log = new ServerLog { Mirror = TextWriter.Null };

        return new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log)
        {
            OnSettingsWithheld = announced,
        };
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, LspJson.Options);

    /// <summary>Settings naming <paramref name="protoc"/> at every scope the client is asked about.</summary>
    private static Func<ConfigurationParams, object?> Naming(string protoc)
        => parameters => parameters.Items
            .Select(_ => new Dictionary<string, object?> { ["protocPath"] = protoc })
            .ToList();

    private static DidOpenTextDocumentParams Open(string uri, string text) => new()
    {
        TextDocument = new TextDocumentItem { Uri = uri, LanguageId = "protolang", Version = 1, Text = text },
    };

    /// <summary>A document that reaches protoc: it imports a fixture schema from beside itself.</summary>
    private const string Importing =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn doubled() -> int64 {
                return count * 2;
            }
        }
        """;

    private static async Task<StatusResult> Status(LanguageServerClient client, string? document = null)
    {
        var parameters = document is null
            ? new StatusParams()
            : new StatusParams(new TextDocumentIdentifier { Uri = document });

        return (await client.RequestAsync(Methods.Status, parameters)).Deserialize<StatusResult>(LspJson.Options)!;
    }

    private static IEnumerable<StatusFactResult> Facts(StatusResult status, string label)
        => status.Sections.SelectMany(section => section.Facts).Where(fact => fact.Label == label);

    // ------------------------------------------------------- what an untrusted workspace may not name

    /// <summary>
    /// A protoc named by a scope a repository can write is not used while the workspace is untrusted.
    /// </summary>
    /// <remarks>
    /// Both scopes a committed settings file can reach. The trusted resolution is asserted first, as a
    /// precondition: a fixture naming a protoc that would not have been used anyway would make the
    /// untrusted answer pass for the wrong reason.
    /// </remarks>
    [Theory]
    [InlineData(ConfigurationSource.FolderSetting)]
    [InlineData(ConfigurationSource.WorkspaceSetting)]
    public void AnUntrustedWorkspaceDoesNotUseAProtocItsOwnSettingsName(ConfigurationSource scope)
    {
        var protoc = FileNamedProtoc();
        var (configuration, document) = Stating(scope, new ProtoLangSettings { ProtocPath = protoc }, WorkspaceTrust.Untrusted);

        Assert.True(
            configuration.WithTrust(WorkspaceTrust.Trusted).Resolve(document).ProtocPath == protoc,
            "the fixture must name a protoc a trusted workspace would use, or withholding it proves nothing");

        var resolved = configuration.Resolve(document);

        Assert.Null(resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Discovery, resolved.ProtocPathSource);
    }

    /// <summary>A client that never said anything about trust keeps every setting it sent.</summary>
    /// <remarks>
    /// Spec 10.4.1's default, and the reason no client written before #55 changes behaviour.
    /// </remarks>
    [Fact]
    public void AWorkspaceWhoseTrustWasNeverReportedUsesTheProtocItsSettingsName()
    {
        var protoc = FileNamedProtoc();
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = protoc }, WorkspaceTrust.NotReported);

        Assert.Equal(protoc, configuration.Resolve(document).ProtocPath);
    }

    /// <summary>User scope is never withheld, because a repository cannot write it.</summary>
    [Fact]
    public void AProtocNamedAtUserScopeIsUsedInAnUntrustedWorkspace()
    {
        var protoc = FileNamedProtoc();
        var (configuration, document) = Stating(
            ConfigurationSource.UserSetting, new ProtoLangSettings { ProtocPath = protoc }, WorkspaceTrust.Untrusted);

        var resolved = configuration.Resolve(document);

        Assert.Equal(protoc, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.UserSetting, resolved.ProtocPathSource);
    }

    /// <summary>
    /// Withholding removes a candidate and leaves the order alone: the next source is the one the
    /// ordinary precedence would have reached.
    /// </summary>
    [Fact]
    public void AWithheldProtocFallsThroughToTheNextSourceInTheOrdinaryOrder()
    {
        var committed = FileNamedProtoc();
        var machine = FileNamedProtoc();
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = committed }, WorkspaceTrust.Untrusted);

        configuration = configuration with
        {
            ReadEnvironmentVariable = name => name == Binding.ProtocLocator.OverrideEnvironmentVariable ? machine : null,
        };

        var resolved = configuration.Resolve(document);

        Assert.Equal(machine, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Environment, resolved.ProtocPathSource);
    }

    /// <summary>
    /// The settings that do not require trust resolve exactly as they would in a trusted workspace.
    /// </summary>
    /// <remarks>
    /// The degraded experience #55 asks to keep usable. An untrusted repository that relies on an
    /// include path and a named policy file still gets both, so its imports still resolve and its
    /// documents still mean what they mean in the build.
    /// </remarks>
    [Fact]
    public void SettingsThatDoNotRequireTrustAreUsedInAnUntrustedWorkspace()
    {
        var directory = TestPaths.CreateTempDirectory();
        var schemas = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;
        var policy = Path.Combine(directory, "policy.xml");
        File.WriteAllText(policy, "<ProtoLang></ProtoLang>");

        var settings = new ProtoLangSettings { IncludePaths = [schemas], ConfigPath = policy, ProtocPath = FileNamedProtoc() };
        var (configuration, document) = Folder(settings, WorkspaceTrust.Untrusted);

        var untrusted = configuration.Resolve(document);
        var trusted = configuration.WithTrust(WorkspaceTrust.Trusted).Resolve(document);

        Assert.Equal(trusted.IncludeDirectories, untrusted.IncludeDirectories);
        Assert.Equal(trusted.ConfigPath, untrusted.ConfigPath);
        Assert.Equal(trusted.Config, untrusted.Config);
        Assert.NotEmpty(untrusted.IncludeDirectories);
        Assert.Equal(policy, untrusted.ConfigPath);
    }

    /// <summary>Import completion withholds exactly what compilation withholds.</summary>
    /// <remarks>
    /// The two resolve through different entry points, and the one that was not updated is the one
    /// that would go on naming the committed protoc -- to a directory stat rather than a process, but
    /// still to a different answer than the compilation beside it.
    /// </remarks>
    [Fact]
    public void ImportCompletionWithholdsWhatCompilationWithholds()
    {
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = FileNamedProtoc() }, WorkspaceTrust.Untrusted);

        Assert.Equal(configuration.Resolve(document).ProtocPath, configuration.ResolveImportRoots(document).ProtocPath);
        Assert.Null(configuration.ResolveImportRoots(document).ProtocPath);
    }

    // ------------------------------------------------------- saying what was withheld

    /// <summary>A withheld setting says what was written and at which scope.</summary>
    [Fact]
    public void AWithheldSettingSaysWhatWasWrittenAndWhere()
    {
        var protoc = FileNamedProtoc();
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = protoc }, WorkspaceTrust.Untrusted);

        var withheld = Assert.Single(configuration.Resolve(document).Withheld);

        Assert.Equal(ProtoLangSettings.ProtocPathKey, withheld.Key);
        Assert.Equal([protoc], withheld.AsWritten);
        Assert.Equal(ConfigurationSource.FolderSetting, withheld.Source);
    }

    /// <summary>
    /// A document's resolved protoc says a setting was withheld, rather than that nothing named one.
    /// </summary>
    /// <remarks>
    /// The configuration section of a status report is read from this, and "not stated" beside a
    /// setting the user can see in their own settings file tells them it does not exist.
    /// </remarks>
    [Fact]
    public void AWithheldProtocIsNotDescribedAsNeverStated()
    {
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = FileNamedProtoc() }, WorkspaceTrust.Untrusted);

        var protoc = Assert.Single(configuration.Resolve(document).Describe(), fact => fact.Setting == "protoc");

        Assert.Contains("withheld", protoc.Value, StringComparison.Ordinal);
    }

    /// <summary>Nothing is reported withheld from a workspace the user trusts.</summary>
    [Fact]
    public void NothingIsWithheldFromATrustedWorkspace()
    {
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = FileNamedProtoc() }, WorkspaceTrust.Trusted);

        Assert.Empty(configuration.Resolve(document).Withheld);
        Assert.Empty(configuration.WithheldSettings());
    }

    /// <summary>What the workspace withholds covers every folder and the workspace scope, not one document.</summary>
    [Fact]
    public void WhatTheWorkspaceWithholdsCoversEveryScopeARepositoryCanWrite()
    {
        var first = WorkspaceFolder.FromPath(TestPaths.CreateTempDirectory(), settings: new ProtoLangSettings { ProtocPath = FileNamedProtoc() });
        var second = WorkspaceFolder.FromPath(TestPaths.CreateTempDirectory(), settings: new ProtoLangSettings { ProtocPath = FileNamedProtoc() });

        var configuration = WorkspaceConfiguration.Empty with
        {
            Folders = [first, second],
            Workspace = new ProtoLangSettings { ProtocPath = FileNamedProtoc() },
            User = new ProtoLangSettings { ProtocPath = FileNamedProtoc() },
            Trust = WorkspaceTrust.Untrusted,
        };

        var sources = configuration.WithheldSettings().Select(setting => setting.Source).ToList();

        Assert.Equal(
            [ConfigurationSource.FolderSetting, ConfigurationSource.FolderSetting, ConfigurationSource.WorkspaceSetting],
            sources);
    }

    /// <summary>The user is told about withheld settings once, however often settings are applied.</summary>
    [Fact]
    public void WithheldSettingsAreAnnouncedOnce()
    {
        var announcements = new List<string>();
        var sync = Sync(announcements.Add);
        var protoc = FileNamedProtoc();

        sync.SetTrust(WorkspaceTrust.Untrusted);
        sync.ApplyPush(Json(new Dictionary<string, object?> { ["protocPath"] = protoc }));
        sync.ApplyPush(Json(new Dictionary<string, object?> { ["protocPath"] = protoc }));
        sync.ApplyPush(Json(new Dictionary<string, object?> { ["protocPath"] = FileNamedProtoc() }));

        var announcement = Assert.Single(announcements);

        Assert.Contains(ProtoLangSettings.ProtocPathKey, announcement, StringComparison.Ordinal);
        Assert.Contains(protoc, announcement, StringComparison.Ordinal);
    }

    /// <summary>An untrusted workspace that states nothing requiring trust is not warned about.</summary>
    [Fact]
    public void NothingIsAnnouncedWhenNothingIsWithheld()
    {
        var announcements = new List<string>();
        var sync = Sync(announcements.Add);

        sync.SetTrust(WorkspaceTrust.Untrusted);
        sync.ApplyPush(Json(new Dictionary<string, object?> { ["includePaths"] = new[] { TestPaths.CreateTempDirectory() } }));

        Assert.Empty(announcements);
    }

    // ------------------------------------------------------- changing trust

    /// <summary>
    /// A document resolved before trust was granted does not compile the way one resolved after does.
    /// </summary>
    /// <remarks>
    /// This is the whole mechanism by which a grant takes effect without a reload: the held
    /// compilation and the scheduled one are both judged by this comparison, so if it answered yes the
    /// session would go on compiling against the protoc it located until something unrelated changed.
    /// </remarks>
    [Fact]
    public void AResolutionFromBeforeTrustWasGrantedDoesNotCompileLikeOneFromAfter()
    {
        var (configuration, document) = Folder(new ProtoLangSettings { ProtocPath = FileNamedProtoc() }, WorkspaceTrust.Untrusted);

        var before = configuration.Resolve(document);
        var granted = configuration.WithTrust(WorkspaceTrust.Trusted);

        Assert.True(granted.Generation > configuration.Generation, "a change in trust must be a new generation");
        Assert.False(before.CompilesTheSameWayAs(granted.Resolve(document)));
    }

    /// <summary>Reporting the trust state already in force changes nothing.</summary>
    [Fact]
    public void RestatingTheTrustAlreadyInForceChangesNothing()
    {
        var sync = Sync();

        Assert.True(sync.SetTrust(WorkspaceTrust.Untrusted));

        var generation = sync.Current.Generation;

        Assert.False(sync.SetTrust(WorkspaceTrust.Untrusted));
        Assert.Equal(generation, sync.Current.Generation);
    }

    // ------------------------------------------------------- the named set

    /// <summary>Every setting the server reads is classified, with a reason, exactly once.</summary>
    /// <remarks>
    /// A sweep over what <see cref="ProtoLangSettings.Read"/> actually accepts, rather than over the
    /// table, so a key readable by some route that bypassed the table would fail here as unclassified.
    /// </remarks>
    [Fact]
    public void EverySettingTheServerReadsIsClassifiedWithAReason()
    {
        var definitions = ProtoLangSettings.Definitions;

        Assert.Equal(ProtoLangSettings.Keys, definitions.Select(definition => definition.Key));
        Assert.Equal(definitions.Count, definitions.Select(definition => definition.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(definitions, definition =>
        {
            Assert.True(Enum.IsDefined(definition.Trust), $"{definition.Key} must be classified");
            Assert.False(string.IsNullOrWhiteSpace(definition.Because), $"{definition.Key} must say why it is classified as it is");
        });

        foreach (var key in ProtoLangSettings.Keys)
        {
            var diagnostics = new DiagnosticBag();
            ProtoLangSettings.Read([new SettingValue(key, "value")], ConfigurationSource.FolderSetting, diagnostics);

            Assert.True(diagnostics.Count == 0, $"{key} is classified and must also be read");
        }
    }

    /// <summary>
    /// The protoc path is the one setting that requires trust: the decision #55 recorded, pinned by
    /// name so that reclassifying anything fails a test that says what was decided.
    /// </summary>
    [Fact]
    public void OnlyTheProtocPathRequiresTrust()
        => Assert.Equal([ProtoLangSettings.ProtocPathKey], ProtoLangSettings.RestrictedKeys);

    /// <summary>
    /// Withholding removes every setting that requires trust and touches nothing else.
    /// </summary>
    /// <remarks>
    /// A sweep over the whole table with a scope that states every setting, so a future restricted
    /// setting whose row forgot how to clear itself fails here rather than in a support request.
    /// </remarks>
    [Fact]
    public void WithholdingRemovesExactlyTheSettingsThatRequireTrust()
    {
        var everything = ProtoLangSettings.Read(
            ProtoLangSettings.Keys.Select(key => new SettingValue(key, $"stated for {key}")),
            ConfigurationSource.FolderSetting,
            new DiagnosticBag());

        var kept = everything.WithoutRestricted(out var withheld);

        foreach (var definition in ProtoLangSettings.Definitions)
        {
            var original = everything.ValuesOf(definition.Key);

            Assert.True(original.Count > 0, $"the fixture must state {definition.Key}");

            if (definition.Trust is SettingTrust.RequiresTrust)
            {
                Assert.Empty(kept.ValuesOf(definition.Key));
                Assert.Contains(withheld, value => value.Key == definition.Key && value.Values.SequenceEqual(original));
            }
            else
            {
                Assert.Equal(original, kept.ValuesOf(definition.Key));
                Assert.DoesNotContain(withheld, value => value.Key == definition.Key);
            }
        }
    }

    // ------------------------------------------------------- over the wire

    /// <summary>
    /// A committed protoc does not run before trust is granted: not to compile a document, and not to
    /// be asked its version by a status report.
    /// </summary>
    /// <remarks>
    /// The acceptance criterion #55 names, asserted on the file the program writes when it starts. The
    /// diagnostics are waited for only as proof that a compilation of the document really happened --
    /// under the protoc the machine supplies -- so the absence of the file is an absence after the
    /// fact rather than before it.
    /// </remarks>
    [Fact]
    public async Task ACommittedProtocDoesNotRunBeforeTrustIsGranted()
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        var uri = new Uri(Path.Combine(directory, "source.protolang")).AbsoluteUri;
        var marker = Path.Combine(TestPaths.CreateTempDirectory(), "ran");

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            settings: Naming(StandInProtoc.Tattletale(marker)),
            initializationOptions: new { workspaceTrusted = false });

        client.Notify(Methods.DidOpen, Open(uri, Importing));
        await client.DiagnosticsAsync(uri);

        await Status(client, uri);

        Assert.False(File.Exists(marker), "a protoc named by an untrusted workspace's settings must not have been started");
    }

    /// <summary>Granting trust puts the withheld protoc into effect, with no restart and no reload.</summary>
    /// <remarks>
    /// Also the teeth of the test above: the same fixture, the same session, and the one difference is
    /// the notification -- so the file appearing here is what shows its absence there meant something.
    /// </remarks>
    [Fact]
    public async Task GrantingTrustRunsTheNamedProtocWithoutARestart()
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        var uri = new Uri(Path.Combine(directory, "source.protolang")).AbsoluteUri;
        var marker = Path.Combine(TestPaths.CreateTempDirectory(), "ran");

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            settings: Naming(StandInProtoc.Tattletale(marker)),
            initializationOptions: new { workspaceTrusted = false });

        client.Notify(Methods.DidOpen, Open(uri, Importing));
        await client.DiagnosticsAsync(uri);

        Assert.False(File.Exists(marker), "nothing should have run before trust was granted");

        client.Notify(Methods.DidChangeWorkspaceTrust, new WorkspaceTrustParams(Trusted: true));

        // The stand-in fails, so the compilation it serves publishes an error the located protoc did not.
        await client.DiagnosticsAsync(uri, published => published.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.True(File.Exists(marker), "granting trust must put the named protoc into effect without a restart");
    }

    /// <summary>The user is told, as a warning message, which setting is withheld and how to use it.</summary>
    [Fact]
    public async Task AWithheldProtocIsAnnouncedToTheUser()
    {
        var directory = TestPaths.CreateTempDirectory();
        var protoc = FileNamedProtoc();

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            settings: Naming(protoc),
            initializationOptions: new { workspaceTrusted = false });

        var message = await client.WaitForAsync(
            candidate => candidate.IsNotification
                && candidate.Method == Methods.ShowMessage
                && LspJson.Read<ShowMessageParams>(candidate.Params)?.Type == (int)LogLevel.Warning,
            "a warning about the withheld protoc");

        var shown = LspJson.Read<ShowMessageParams>(message.Params)!;

        Assert.Contains(ProtoLangSettings.ProtocPathKey, shown.Message, StringComparison.Ordinal);
        Assert.Contains(protoc, shown.Message, StringComparison.Ordinal);
        Assert.Contains("Trust the workspace", shown.Message, StringComparison.Ordinal);
    }

    /// <summary>The trust state in force is the one the client reported, and silence is said to be silence.</summary>
    [Theory]
    [InlineData("true", WorkspaceTrust.Trusted)]
    [InlineData("false", WorkspaceTrust.Untrusted)]
    [InlineData("\"false\"", WorkspaceTrust.NotReported)]
    [InlineData(null, WorkspaceTrust.NotReported)]
    public async Task TheTrustStateInForceIsTheOneTheClientReported(string? stated, WorkspaceTrust expected)
    {
        object? options = stated is null
            ? null
            : JsonDocument.Parse($"{{\"workspaceTrusted\": {stated}}}").RootElement.Clone();

        await using var client = await LanguageServerClient.StartAsync(initializationOptions: options);

        var status = await Status(client);

        Assert.Equal(expected.Describe(), Assert.Single(Facts(status, "workspace trust")).Value);
    }

    /// <summary>A trust notification that does not say which way trust went changes nothing.</summary>
    /// <remarks>
    /// Started trusted, so the failure being guarded against -- a missing member read as
    /// <c>false</c> -- is the one that would show. The status request is the sync point: it is read
    /// after the notification, which is handled in order.
    /// </remarks>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isTrusted\": false}")]
    [InlineData("{\"trusted\": null}")]
    public async Task ATrustNotificationThatSaysNothingLeavesTrustAsItWas(string parameters)
    {
        await using var client = await LanguageServerClient.StartAsync(initializationOptions: new { workspaceTrusted = true });

        client.Notify(Methods.DidChangeWorkspaceTrust, JsonDocument.Parse(parameters).RootElement.Clone());

        var status = await Status(client);

        Assert.Equal(WorkspaceTrust.Trusted.Describe(), Assert.Single(Facts(status, "workspace trust")).Value);
    }

    /// <summary>The status report names what trust is withholding, and what trust would permit.</summary>
    [Fact]
    public async Task TheStatusReportNamesWhatTrustIsWithholding()
    {
        var directory = TestPaths.CreateTempDirectory();
        var protoc = FileNamedProtoc();

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            settings: Naming(protoc),
            initializationOptions: new { workspaceTrusted = false });

        var status = await Status(client);

        Assert.Equal(ProtoLangSettings.RestrictedKeys, Facts(status, "requires trust").Select(fact => fact.Value));
        Assert.Contains(Facts(status, "withheld"), fact => fact.Value.Contains(protoc, StringComparison.Ordinal));
    }
}
