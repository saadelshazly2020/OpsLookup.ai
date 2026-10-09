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

        _pythonDir = Path.GetFullPath(
            configuration["Python:Directory"]
            ?? Path.Combine(AppContext.BaseDirectory, "Python"));

        _venvDir = Path.GetFullPath(
            configuration["Python:Venv"] ?? Path.Combine(_pythonDir, ".venv"));

        _basePythonDir = ResolveBasePythonDir(_venvDir);
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
    private static PyObject ToPy(object value) => value switch
    {
        string s => new PyString(s),
        int i => new PyInt(i),
        long l => new PyInt(l),
        double d => new PyFloat(d),
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Unsupported argument type for the Python bridge."),
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
    public Task<ChatOutcome> ChatAsync(
        string ticketId,
        string message,
        string? operatorId)
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(StartupError ?? "Python host is not ready");
        }

        return RunAsync(() => ParseOutcome(
            ticketId,
            InvokeString(_runTurn!, ticketId, message, operatorId ?? string.Empty)));
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

        return new ChatOutcome(
            ThreadId: document.RootElement.GetProperty("threadId").GetString() ?? ticketId,
            Reply: document.RootElement.GetProperty("reply").GetString() ?? string.Empty,
            ToolCalls: document.RootElement.GetProperty("toolCalls").GetInt32(),
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
    bool Error);