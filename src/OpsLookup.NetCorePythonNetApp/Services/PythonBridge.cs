using System.Collections.Concurrent;
using System.Text.Json;
using Python.Runtime;

namespace OpsLookup.NetCorePythonNetApp.Services;

/// <summary>
/// Owns the embedded Python interpreter and calls into <c>Python/host.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// All interpreter work runs on one dedicated thread, and the engine is
/// initialised on that same thread. That is deliberate. pythonnet only attaches
/// the thread that called <c>PythonEngine.Initialize</c>; a thread-pool thread
/// calling <c>Py.GIL()</c> simply blocks, which showed up as a silent startup
/// hang. A single worker sidesteps that and serialises GIL access by
/// construction.
/// </para>
/// <para>
/// Python runs each turn synchronously on that thread via <c>asyncio.run</c>, so
/// there is no background Python thread and no loop lifetime to manage.
/// </para>
/// </remarks>
public sealed class PythonBridge : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly string _pythonDir;
    private readonly string _venvDir;
    private readonly string _basePythonDir;
    private readonly ILogger<PythonBridge> _logger;

    private readonly BlockingCollection<Action> _queue = new();
    private Thread? _worker;

    private PyObject? _host;
    private PyObject? _warmup;
    private PyObject? _runTurn;
    private PyObject? _apiReachable;
    private PyObject? _diagnostics;

    public PythonBridge(IConfiguration configuration, ILogger<PythonBridge> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _pythonDir = ResolveAgainstApp(configuration["Python:Directory"])
            ?? Path.Combine(AppContext.BaseDirectory, "Python");

        _venvDir = ResolveAgainstApp(configuration["Python:Venv"])
            ?? Path.Combine(_pythonDir, ".venv");

        _basePythonDir = ResolveBasePythonDir(_venvDir);
    }

    /// <summary>
    /// Turns a configured path into an absolute one. Relative values are anchored
    /// to the application directory, falling back to the process working
    /// directory when that is where the file actually lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under IIS the working directory is chosen by the host and is usually not
    /// the deployed folder, so a bare relative path could resolve to somewhere
    /// else entirely — it happened to work here only because this host happened
    /// to set the working directory to the application folder, which is not
    /// something to rely on. Rooted values are honoured unchanged, which is what
    /// makes an absolute path on the server work.
    /// </para>
    /// <para>
    /// The working-directory fallback exists for <c>dotnet run</c>, where the
    /// venv sits beside the project file rather than beside the built binary.
    /// </para>
    /// </summary>
    /// <returns>
    /// Null for an absent or blank value so the caller can apply its own default.
    /// </returns>
    private static string? ResolveAgainstApp(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var value = configured.Trim();
        if (Path.IsPathRooted(value))
        {
            return Path.GetFullPath(value);
        }

        bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

        var againstApp = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        if (Exists(againstApp))
        {
            return againstApp;
        }

        var againstWorkingDir =
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, value));
        if (Exists(againstWorkingDir))
        {
            return againstWorkingDir;
        }

        // Neither exists. Return the application-relative path anyway, so the
        // error message names the location the host actually looked at first
        // rather than something vaguer.
        return againstApp;
    }

    public string PythonDirectory => _pythonDir;

    public bool IsReady { get; private set; }

    public string? StartupError { get; private set; }

    /// <summary>Records why startup failed, so /api/health can report it.</summary>
    public void RecordStartupError(string message) => StartupError ??= message;

    // ------------------------------------------------------------------ discovery
    /// <summary>
    /// A Windows virtualenv holds no python DLL of its own. pyvenv.cfg points at
    /// the base installation, and that is where python3XX.dll lives.
    /// </summary>
    private static string ResolveBasePythonDir(string venvDir)
    {
        var config = Path.Combine(venvDir, "pyvenv.cfg");
        if (File.Exists(config))
        {
            foreach (var line in File.ReadLines(config))
            {
                if (!line.StartsWith("home", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(line.IndexOf('=') + 1)..].Trim();
                if (Directory.Exists(value))
                {
                    return Path.GetFullPath(value);
                }
            }
        }

        var venvScripts = Path.Combine(venvDir, "Scripts");
        if (Directory.Exists(venvScripts))
        {
            var dll = Directory.EnumerateFiles(venvScripts, "python3*.dll").FirstOrDefault();
            if (dll is not null)
            {
                return Path.GetDirectoryName(dll)!;
            }
        }

        return venvDir;
    }

    /// <summary>
    /// pythonnet 3.x will not initialise without an explicit runtime DLL. On
    /// Windows the versioned one (python314.dll) is in the base install;
    /// python3.dll is only a small forwarder, so match a digit after "python3".
    /// </summary>
    private string ResolveRuntimeDll()
    {
        var configured = _configuration["Python:RuntimeDll"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            throw new FileNotFoundException(
                $"Python:RuntimeDll points at {configured}, which does not exist.");
        }

        if (!Directory.Exists(_basePythonDir))
        {
            throw new DirectoryNotFoundException(
                $"Python home {_basePythonDir} does not exist. " +
                "Set Python:Venv or Python:RuntimeDll in configuration.");
        }

        var candidate = Directory
            .EnumerateFiles(_basePythonDir, "python3*.dll")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.Length > "python3".Length
                    && name["python3".Length..].All(char.IsDigit);
            })
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();

        return candidate ?? throw new FileNotFoundException(
            $"No versioned python DLL found in {_basePythonDir}. " +
            "Set Python:RuntimeDll in configuration.");
    }

    // -------------------------------------------------------------- py marshalling
    private static PyObject ToPy(object? value) => value switch
    {
        // PyNone, not null. A C# null passed straight through surfaces inside
        // Python as an opaque "unsupported type" error at the call site rather
        // than as the argument it actually is. host.run_turn expects real
        // strings, so this is a guard rather than a supported path.
        null => throw new ArgumentNullException(
            nameof(value), "Cannot pass null across the Python bridge."),
        string s => new PyString(s),
        int i => new PyInt(i),
        long l => new PyInt(l),
        double d => new PyFloat(d),
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, $"Unsupported argument type for the Python bridge: {value.GetType()}."),
    };

    private static string InvokeString(PyObject function, params object[] args)
    {
        var pyArgs = args.Select(ToPy).ToArray();
        try
        {
            using var result = function.Invoke(pyArgs);
            return result.As<string>() ?? string.Empty;
        }
        finally
        {
            foreach (var arg in pyArgs)
            {
                arg.Dispose();
            }
        }
    }

    // -------------------------------------------------------------------- worker
    private void WorkerLoop()
    {
        try
        {
            EnsureInterpreterInitialised();
            ImportHost();
        }
        catch (Exception ex)
        {
            RecordStartupError($"interpreter init failed: {ex.Message}");
            _logger.LogError(ex, "python interpreter initialisation failed");
            return;
        }

        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "python worker task failed");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="call" /> on the Python worker thread with the GIL held.
    /// The GIL is acquired and released on that one thread, never across an await.
    /// </summary>
    private Task<T> RunAsync<T>(Func<T> call)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (_worker is null)
        {
            completion.TrySetException(
                new InvalidOperationException("Python worker is not running."));
            return completion.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    using var gil = Py.GIL();
                    completion.TrySetResult(call());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }

    private void EnsureInterpreterInitialised()
    {
        if (PythonEngine.IsInitialized)
        {
            return;
        }

        // Before anything else. host.py reads OPSLOOKUP_DATA_DIR at import time to
        // place its log file and SQLite checkpointer, and a value set afterwards
        // would arrive too late -- the paths would already be fixed. Python runs
        // in-process, so it inherits this process's environment.
        ApplyDataDirectory();

        var sitePackages = Path.Combine(_venvDir, "Lib", "site-packages");
        var runtimeDll = ResolveRuntimeDll();

        Runtime.PythonDLL = runtimeDll;
        PythonEngine.PythonHome = _basePythonDir;

        // Order matters. The venv's site-packages must win so langgraph resolves
        // from this app's own environment, but the interpreter still needs the
        // base install's Lib for the standard library -- a virtualenv has no copy
        // of encodings, and omitting it fails with "Failed to import encodings".
        PythonEngine.PythonPath = string.Join(
            Path.PathSeparator,
            sitePackages,
            Path.Combine(_basePythonDir, "Lib"),
            Path.Combine(_basePythonDir, "DLLs"),
            _basePythonDir,
            _venvDir);

        PythonEngine.Initialize();

        _logger.LogInformation(
            "PythonEngine initialised: dll={Dll} home={Home} sitePackages={SitePackages}",
            runtimeDll, _basePythonDir, sitePackages);
    }

    /// <summary>
    /// Decides where Python writes its log file and SQLite checkpointer, and
    /// publishes the answer as <c>OPSLOOKUP_DATA_DIR</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application directory is a poor home for runtime state. Under IIS the
    /// app pool identity has no write access to it, so the log file and
    /// checkpointer silently fail to appear -- and worse, every deployment
    /// overwrites them along with the code they describe.
    /// </para>
    /// <para>
    /// This runs unelevated on purpose. Setting an app-pool environment variable
    /// needs administrator rights, which would make the data directory
    /// configurable only by someone who could not test it. Setting it here means
    /// the working path is the default and no manual step is needed at all.
    /// An explicit <c>OPSLOOKUP_DATA_DIR</c> still wins, for a deployment that
    /// puts it on real storage.
    /// </para>
    /// <para>
    /// If none of the candidates can be created the last resort is the temp
    /// folder. That is not a good home -- it can be cleaned -- but it is far
    /// better than the web root, and the alternative is a process that cannot
    /// write its own diagnostics.
    /// </para>
    /// </remarks>
    private void ApplyDataDirectory()
    {
        var existing = Environment.GetEnvironmentVariable("OPSLOOKUP_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            _logger.LogInformation("Data directory from configuration: {DataDir}", existing);
            EnsureDirectoryUsable(existing);
            return;
        }

        var root = _configuration["Python:DataRoot"];
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(root);
        }
        else
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(programData))
            {
                candidates.Add(Path.Combine(programData, "AI.TicketingAssistant", "data"));
            }
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "AI.TicketingAssistant", "data"));

        foreach (var candidate in candidates)
        {
            if (!EnsureDirectoryUsable(candidate))
            {
                continue;
            }

            Environment.SetEnvironmentVariable("OPSLOOKUP_DATA_DIR", candidate);
            _logger.LogInformation("Data directory set to {DataDir}", candidate);
            return;
        }

        // Nothing worked. Leave the variable unset so host.py falls back to its
        // own default, and say so -- this is exactly the kind of thing that is
        // invisible until someone goes looking for a missing log file.
        _logger.LogWarning(
            "No writable data directory; Python will fall back to its own directory. " +
            "Logs and checkpoints may not be written.");
    }

    /// <summary>
    /// Creates <paramref name="path"/> if needed and verifies it can actually be
    /// written to, returning false rather than throwing.
    /// </summary>
    private bool EnsureDirectoryUsable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // A directory that exists is not necessarily writable -- the app pool
            // identity may be able to see it but not create files in it. Probing
            // with a real file is the only way to know, and doing it here means
            // the failure names the directory instead of surfacing later as a
            // SQLite error inside Python.
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Data directory {DataDir} is not usable: {Reason}", path, ex.Message);
            return false;
        }
    }

    private void ImportHost()
    {
        using var gil = Py.GIL();

        // Explicit PyObject calls rather than `dynamic`: a Python list has no
        // Contains method -- that is LINQ -- so dynamic lookup raises
        // AttributeError instead of falling back to __contains__.
        using (var sys = Py.Import("sys"))
        using (var sysPath = sys.GetAttr("path"))
        using (var insert = sysPath.GetAttr("insert"))
        using (var zero = new PyInt(0))
        using (var pythonDir = new PyString(_pythonDir))
        using (insert.Invoke(zero, pythonDir))
        {
            // sys.path now resolves this app's modules first.
        }

        _host = Py.Import("host");

        _warmup = _host.GetAttr("warmup");
        _runTurn = _host.GetAttr("run_turn");
        _apiReachable = _host.GetAttr("api_reachable");
        _diagnostics = _host.GetAttr("diagnostics");

        _logger.LogInformation("python module 'host' imported from {Path}", _pythonDir);
    }

    // ------------------------------------------------------------------ lifecycle
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "python-worker",
        };
        _worker.Start();

        var failure = await RunAsync(() => InvokeString(_warmup!));

        if (!string.IsNullOrWhiteSpace(failure))
        {
            RecordStartupError($"Python host failed to warm up: {failure}");
            throw new InvalidOperationException(StartupError);
        }

        IsReady = true;
        _logger.LogInformation(
            "python bridge ready: pythonDir={PythonDir} base={Base} venv={Venv}",
            _pythonDir, _basePythonDir, _venvDir);
    }

    // ------------------------------------------------------------------ chat
    public async Task<ChatOutcome> ChatAsync(
        string ticketId,
        string message,
        string? operatorId,
        string? locale,
        string apiBaseUrl,
        string apiToken)
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(StartupError ?? "Python host is not ready");
        }

        // The API address and token are handed to Python per turn rather than
        // read from its .env. That keeps credential acquisition in the host: the
        // agent receives a short-lived string and has no way to mint another.
        //
        // Locale selects the system prompt so the reply matches the page.
        return await RunAsync(() => ParseOutcome(
            ticketId,
            InvokeString(
                _runTurn!,
                ticketId,
                message,
                operatorId ?? string.Empty,
                locale ?? string.Empty,
                apiBaseUrl,
                apiToken)));
    }

    private static ChatOutcome ParseOutcome(string ticketId, string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.TryGetProperty("error", out var error))
        {
            return new ChatOutcome(
                ThreadId: ticketId,
                Reply: error.GetString() ?? "The agent could not complete this request.",
                ToolCalls: 0,
                Error: true);
        }

        // ToolCalls counts invocations, not successes, so a turn whose lookup
        // failed still reports 1. LookupFailed is what stops the UI claiming
        // the answer was read from the database when it was not.
        return new ChatOutcome(
            ThreadId: document.RootElement.GetProperty("threadId").GetString() ?? ticketId,
            Reply: document.RootElement.GetProperty("reply").GetString() ?? string.Empty,
            ToolCalls: document.RootElement.GetProperty("toolCalls").GetInt32(),
            LookupFailed: document.RootElement.TryGetProperty("lookupFailed", out var failed)
                && failed.ValueKind is JsonValueKind.True,
            Error: false);
    }

    public Task<bool> ApiReachableAsync() => RunAsync(
        () => string.Equals(
            InvokeString(_apiReachable!), "true", StringComparison.OrdinalIgnoreCase));

    public Task<string> DiagnosticsAsync() => RunAsync(() => InvokeString(_diagnostics!));

    public ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(5));

        _warmup?.Dispose();
        _runTurn?.Dispose();
        _apiReachable?.Dispose();
        _diagnostics?.Dispose();
        _host?.Dispose();
        _queue.Dispose();

        return ValueTask.CompletedTask;
    }
}

public sealed record ChatOutcome(
    string ThreadId,
    string Reply,
    int ToolCalls,
    bool LookupFailed = false,
    bool Error = false);