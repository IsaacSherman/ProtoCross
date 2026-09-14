using System.Collections.Concurrent;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The descriptor loaders this server compiles through, and the one cache they all share.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pool at all.</b> <c>CompilationOptions</c> has no protoc of its own: which executable runs
/// can only reach a compilation through its loader, and a loader is also where the descriptor cache
/// lives. A server that passed no loader would get a fresh, uncached one built inside every
/// compilation -- which is #48 undone for the common case, since the common case is a user who never
/// set <c>protolang.protocPath</c> at all. So a loader is always supplied, and it always carries
/// <see cref="Cache"/>.
/// </para>
/// <para>
/// <b>Successes are kept and failures are not.</b> Building a loader locates protoc and then stats
/// the directories beside it looking for the well-known schemas, which is work worth doing once. A
/// failure is a different thing: it means protoc is not installed yet, and remembering that would mean
/// installing protoc had no effect until the editor was restarted. Retrying costs a <c>PATH</c> and
/// package-cache probe on each compile of a workspace that cannot compile anyway, and buys a server
/// that heals by itself.
/// </para>
/// </remarks>
public sealed class LoaderPool(ServerLog log)
{
    /// <summary>The key a loader with no stated protoc is filed under.</summary>
    private const string Located = "<located>";

    private readonly ConcurrentDictionary<string, DescriptorLoader> _loaders = new(StringComparer.Ordinal);

    private int _reportedMissing;

    /// <summary>Told once, the first time no protoc can be found at all.</summary>
    /// <remarks>
    /// Once, because the retry policy above means this is discovered again on every compile, and a
    /// notification per keystroke is not a notification. The message is <see cref="Missing"/>'s, which
    /// names everywhere the server looked and where protoc can be had -- the things a user actually
    /// needs in order to fix it.
    /// </remarks>
    public Action<string>? OnProtocMissing { get; init; }

    /// <summary>What a user is told when discovery finds no protoc.</summary>
    /// <remarks>
    /// Settable because half of it is the client's to supply and arrives at <c>initialize</c>, after
    /// this pool exists: which <c>PATH</c> entries it removed before starting the server. Volatile
    /// because it is written on the worker that reads the wire and read by whichever compile or status
    /// report discovers the absence.
    /// </remarks>
    public MissingProtoc Missing
    {
        get => Volatile.Read(ref _missing);
        set => Volatile.Write(ref _missing, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private MissingProtoc _missing = MissingProtoc.NothingRemoved;

    /// <summary>How a loader is built when nothing names a protoc.</summary>
    /// <remarks>
    /// The seam <see cref="DocumentSemantics.Compile"/> is for the compile. Whether a protoc can be
    /// found is a fact about the machine, and on a developer's machine the answer is nearly always yes:
    /// the package cache the repository itself restores holds one, and on Windows the profile directory
    /// that cache lives under cannot be redirected by an environment variable. A test of what a user with
    /// no protoc is told has nowhere else to stand.
    /// </remarks>
    public Func<DescriptorLoaderOptions, DescriptorLoader> Locate { get; set; } = DescriptorLoader.CreateDefault;

    /// <summary>Why no loader could be built for <paramref name="protocPath"/>, as a user should read it.</summary>
    /// <remarks>
    /// Only discovery gets the editor's account. A protoc a setting named and that could not be prepared
    /// is a different failure with a different remedy, and its own message says what went wrong with
    /// that file.
    /// </remarks>
    public string Explain(string? protocPath, DescriptorLoadException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return protocPath is null ? Missing.Describe() : failure.Message;
    }

    /// <summary>
    /// One cache for the whole server, so two documents importing one schema load it once.
    /// </summary>
    /// <remarks>
    /// Shared across loaders as well as across documents. Its keys already name the protoc that ran,
    /// so entries built under one executable are simply never matched by another -- there is nothing
    /// to partition by hand.
    /// </remarks>
    public DescriptorCache Cache { get; } = new();

    /// <summary>Everything else every loader in this pool is built with.</summary>
    /// <remarks>
    /// The supervision settings were written into <see cref="TryGet"/> and could not be reached from
    /// outside it, which meant the server ran on the loader's defaults and had no way to say
    /// otherwise -- not the budget protoc gets, and not where its scratch files go. #57 pins the
    /// first against measurement and needs somewhere to pin it to; a session that wants to prove it
    /// leaked nothing needs the second. The cache is not among them: there is one for the whole
    /// server, and a pool serving loaders that each cached somewhere else would be the bug this type
    /// exists to prevent.
    /// </remarks>
    public DescriptorLoaderOptions Options { get; init; } = new();

    /// <summary>
    /// The loader for <paramref name="protocPath"/>, or the reason there cannot be one.
    /// </summary>
    /// <param name="protocPath">
    /// The protoc a setting or the environment named, or null to let <c>ProtocLocator</c> find one.
    /// </param>
    public bool TryGet(string? protocPath, out DescriptorLoader? loader, out DescriptorLoadException? failure)
    {
        failure = null;

        var key = protocPath is null ? Located : PathIdentity.KeyFor(protocPath);

        if (_loaders.TryGetValue(key, out loader))
        {
            return true;
        }

        try
        {
            var options = Options with { Cache = Cache };

            loader = protocPath is null
                ? Locate(options)
                : new DescriptorLoader(protocPath, options);

            log.Info($"Compiling schemas with '{loader.ProtocPath}'.");
        }
        catch (DescriptorLoadException ex)
        {
            loader = null;
            failure = ex;

            if (Interlocked.Exchange(ref _reportedMissing, 1) == 0)
            {
                log.Warning(ex.Message);
                OnProtocMissing?.Invoke(Explain(protocPath, ex));
            }
            else
            {
                log.Trace(ex.Message);
            }

            return false;
        }

        // Whichever instance wins the race is the one everybody uses, so two documents opened at once
        // cannot end up with a cache each.
        loader = _loaders.GetOrAdd(key, loader);

        return true;
    }
}
