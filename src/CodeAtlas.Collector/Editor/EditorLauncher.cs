using System.Diagnostics;

namespace CodeAtlas.Collector.Editor;

/// <summary>
/// Opens a resolved source location in the developer's editor. The executable is fixed at start-up and arguments are
/// passed as an argument list (no shell), so file names can never run as commands.
/// </summary>
public sealed class EditorLauncher
{
    public required string Kind { get; init; }
    public required string? Executable { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public bool Available => Kind != "none" && Executable is not null && File.Exists(Executable);

    public static EditorLauncher From(CollectorOptions options, int timeoutSeconds = 10)
    {
        var name = options.EditorCommand ?? options.EditorKind switch { "rider" => "rider", "vscode" => "code", "none" => null, _ => "code-server" };
        string? resolved = name is null ? null
            : Path.IsPathRooted(name) ? name
            : FindOnPath(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), OperatingSystem.IsWindows());
        return new EditorLauncher { Kind = options.EditorKind, Executable = resolved, Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)) };
    }

    /// <summary>
    /// Finds a command on PATH. On Windows only names with a PATHEXT extension are runnable (VS Code's bin folder has an
    /// extensionless bash script next to code.cmd), in PATHEXT order.
    /// </summary>
    internal static string? FindOnPath(string name, string? path, string? pathExt, bool windows)
    {
        var candidates = windows && !Path.HasExtension(name)
            ? (pathExt ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => name + ext.ToLowerInvariant()).ToArray()
            : [name];
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var candidate in candidates)
            if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
        return null;
    }

    public async Task<(bool Ok, string Message)> OpenAsync(string file, int line)
    {
        if (!Available) return (false, $"No editor is available (kind '{Kind}'). Copy the path instead: {file}:{line}");
        var start = new ProcessStartInfo(Executable!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in Kind == "rider" ? ["--line", line.ToString(), file] : new[] { "-r", "-g", $"{file}:{line}" })
            start.ArgumentList.Add(argument);

        Process process;
        try { process = Process.Start(start)!; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"The editor command could not be started ({e.Message}). Copy the path instead: {file}:{line}");
        }
        using var started = process;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (false, $"The editor command did not finish within {Timeout.TotalSeconds:0} s.");
        }
        return process.ExitCode == 0
            ? (true, $"Opened {Path.GetFileName(file)}:{line} in {Kind}.")
            : (false, $"The editor exited with code {process.ExitCode}: {(await stderr).Trim()}");
    }
}
