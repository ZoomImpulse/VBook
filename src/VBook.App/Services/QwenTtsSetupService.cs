using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VBook.Core.Providers;

namespace VBook.App.Services;

public sealed class QwenTtsSetupService
{
    private const string RuntimeFolderName = "qwen-runtime";
    private const long RuntimeAndTemporarySpace = 6_000_000_000;
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly QwenTtsModelAssetManager _models;
    private readonly string _applicationDataDirectory;
    private readonly string _runtimeDirectory;
    private readonly string _workerScript;
    private readonly SemaphoreSlim _setupLock = new(1, 1);

    public QwenTtsSetupService(
        QwenTtsModelAssetManager models,
        string applicationDataDirectory,
        string workerScript)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerScript);
        _applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        _runtimeDirectory = Path.Combine(_applicationDataDirectory, RuntimeFolderName);
        _workerScript = Path.GetFullPath(workerScript);
    }

    public bool IsInstalled =>
        File.Exists(RuntimePythonPath) &&
        File.Exists(Path.Combine(_runtimeDirectory, "qwen-runtime.json")) &&
        _models.IsInstalled &&
        File.Exists(_workerScript);

    public string CurrentDescription => IsInstalled
        ? "Qwen3-TTS VoiceDesign and Base cloning are ready on the NVIDIA CUDA runtime. Models and synthesis remain local."
        : $"Qwen3-TTS needs about {QwenTtsModelAssetManager.DownloadSize / (1024d * 1024d * 1024d):0.0} GiB for its pinned VoiceDesign and Base models, a CUDA runtime, and an NVIDIA GPU. Setup downloads only after you choose Set up Qwen.";

    public async Task InstallAsync(
        IProgress<QwenTtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _setupLock.WaitAsync(cancellationToken);
        string? stagedRuntime = null;
        try
        {
            Directory.CreateDirectory(_applicationDataDirectory);
            EnsureFreeSpace();
            if (!File.Exists(Path.Combine(_runtimeDirectory, "qwen-runtime.json")))
            {
                var python = await FindPythonAsync(cancellationToken);
                stagedRuntime = Path.Combine(
                    _applicationDataDirectory,
                    $"qwen-runtime-staging-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagedRuntime);
                progress?.Report(new QwenTtsSetupProgress("Creating isolated Python environment"));
                await RunProcessAsync(
                    python.Executable,
                    [.. python.PrefixArguments, "-m", "venv", stagedRuntime],
                    stagedRuntime,
                    progress,
                    "Creating isolated Python environment",
                    cancellationToken);

                var stagedPython = GetPythonExecutable(stagedRuntime);
                progress?.Report(new QwenTtsSetupProgress("Installing pinned CUDA-enabled PyTorch 2.8.0"));
                await RunProcessAsync(
                    stagedPython,
                    [
                        "-m", "pip", "install", "--no-cache-dir", "--disable-pip-version-check", "--no-input",
                        "torch==2.8.0", "torchaudio==2.8.0", "--index-url",
                        "https://download.pytorch.org/whl/cu128"
                    ],
                    stagedRuntime,
                    progress,
                    "Installing CUDA-enabled PyTorch",
                    cancellationToken);
                progress?.Report(new QwenTtsSetupProgress("Installing pinned Qwen3-TTS 0.1.1"));
                await RunProcessAsync(
                    stagedPython,
                    [
                        "-m", "pip", "install", "--no-cache-dir", "--disable-pip-version-check", "--no-input",
                        "qwen-tts==0.1.1"
                    ],
                    stagedRuntime,
                    progress,
                    "Installing Qwen3-TTS",
                    cancellationToken);
                progress?.Report(new QwenTtsSetupProgress("Checking Python, PyTorch, and CUDA"));
                await RunProcessAsync(
                    stagedPython,
                    [
                        "-c",
                        "import sys, torch, qwen_tts; " +
                        "assert sys.version_info >= (3, 11) and sys.version_info < (3, 14), sys.version; " +
                        "assert torch.__version__.startswith('2.8.0'), torch.__version__; " +
                        "assert torch.cuda.is_available(), 'CUDA is unavailable'; " +
                        "print(torch.cuda.get_device_name(0))"
                    ],
                    stagedRuntime,
                    progress,
                    "Checking CUDA",
                    cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(stagedRuntime, "qwen-runtime.json"),
                    JsonSerializer.Serialize(new
                    {
                        Version = 2,
                        Python = "3.11-3.13",
                        PyTorch = "2.8.0+cu128",
                        QwenTts = "0.1.1",
                        VoiceDesignModelRevision = QwenTtsModelAssetManager.VoiceDesignModelRevision,
                        BaseModelRevision = QwenTtsModelAssetManager.BaseModelRevision
                    }),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(_runtimeDirectory))
                {
                    Directory.Delete(_runtimeDirectory, recursive: true);
                }

                Directory.Move(stagedRuntime, _runtimeDirectory);
                stagedRuntime = null;
            }

            if (!_models.IsInstalled)
            {
                await _models.InstallAsync(progress, cancellationToken);
            }

            progress?.Report(new QwenTtsSetupProgress("Qwen model and isolated CUDA runtime are installed"));
        }
        finally
        {
            if (stagedRuntime is not null && Directory.Exists(stagedRuntime))
            {
                Directory.Delete(stagedRuntime, recursive: true);
            }

            _setupLock.Release();
        }
    }

    public ITtsProvider CreateProvider()
    {
        var models = _models.TryGetInstalledFiles();
        if (!File.Exists(RuntimePythonPath) ||
            !File.Exists(Path.Combine(_runtimeDirectory, "qwen-runtime.json")) ||
            models is null ||
            !File.Exists(_workerScript))
        {
            throw new InvalidOperationException(
                "Set up the Qwen VoiceDesign and Base models and CUDA runtime before selecting it.");
        }

        return new QwenTtsProvider(new QwenTtsProcessRunner(
            RuntimePythonPath,
            _workerScript,
            models.VoiceDesignDirectory,
            models.BaseDirectory));
    }

    private string RuntimePythonPath => GetPythonExecutable(_runtimeDirectory);

    private void EnsureFreeSpace()
    {
        var root = Path.GetPathRoot(_applicationDataDirectory)!;
        var drive = new DriveInfo(root);
        var needed = QwenTtsModelAssetManager.DownloadSize + RuntimeAndTemporarySpace + 1_000_000_000;
        if (drive.AvailableFreeSpace < needed)
        {
            throw new IOException(
                $"Qwen setup needs at least {needed / (1024d * 1024d * 1024d):0.0} GiB free on {drive.Name}.");
        }
    }

    private static string GetPythonExecutable(string environmentPath) =>
        Path.Combine(environmentPath, "Scripts", "python.exe");

    private static async Task<PythonCommand> FindPythonAsync(CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("VBOOK_QWEN_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var version = await TryGetPythonVersionAsync(configured, [], cancellationToken);
            if (version is not null)
            {
                return new PythonCommand(configured, []);
            }

            throw new InvalidOperationException(
                $"VBOOK_QWEN_PYTHON points to an incompatible interpreter: '{configured}'. Use 64-bit Python 3.11–3.13.");
        }

        var launcher = await TryResolvePythonLauncherAsync(cancellationToken);
        if (launcher is not null)
        {
            return launcher;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python311", "python.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) &&
                await TryGetPythonVersionAsync(candidate, [], cancellationToken) is not null)
            {
                return new PythonCommand(candidate, []);
            }
        }

        throw new InvalidOperationException(
            "Qwen setup needs 64-bit Python 3.11, 3.12, or 3.13. Install one, or set VBOOK_QWEN_PYTHON to its python.exe path, then retry.");
    }

    private static async Task<PythonCommand?> TryResolvePythonLauncherAsync(CancellationToken cancellationToken)
    {
        foreach (var minor in new[] { 13, 12, 11 })
        {
            var prefix = new[] { $"-3.{minor}" };
            try
            {
                var output = await RunProcessCaptureAsync(
                    "py",
                    [.. prefix, "-c", "import sys; print(sys.executable)"],
                    cancellationToken);
                var executable = output.Trim();
                if (File.Exists(executable) &&
                    await TryGetPythonVersionAsync(executable, [], cancellationToken) is not null)
                {
                    return new PythonCommand(executable, []);
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                continue;
            }
        }

        return null;
    }

    private static async Task<Version?> TryGetPythonVersionAsync(
        string executable,
        IReadOnlyList<string> prefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunProcessCaptureAsync(
                executable,
                [.. prefix, "-c", "import struct,sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}'); print(struct.calcsize('P')*8)"],
                cancellationToken);
            var values = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2 ||
                !Version.TryParse(values[0], out var version) ||
                version.Major != 3 ||
                version.Minor is < 11 or > 13 ||
                values[1] != "64")
            {
                return null;
            }

            return version;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IProgress<QwenTtsSetupProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo(executable, arguments, workingDirectory);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}'.");
        }

        var outputTask = DrainOutputAsync(process.StandardOutput, progress, phase, CancellationToken.None);
        var errorTask = DrainOutputAsync(process.StandardError, progress, phase, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{phase} failed with exit code {process.ExitCode}. Review the setup output and retry.");
        }
    }

    private static async Task DrainOutputAsync(
        StreamReader reader,
        IProgress<QwenTtsSetupProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                progress?.Report(new QwenTtsSetupProgress($"{phase}: {trimmed}"));
            }
        }
    }

    private static async Task<string> RunProcessCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateProcessStartInfo(executable, arguments, Environment.CurrentDirectory)
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}'.");
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await output;
        var standardError = await error;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(standardError);
        }

        return standardOutput;
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PIP_PROGRESS_BAR"] = "off";
        return startInfo;
    }

    private sealed record PythonCommand(string Executable, IReadOnlyList<string> PrefixArguments);
}
