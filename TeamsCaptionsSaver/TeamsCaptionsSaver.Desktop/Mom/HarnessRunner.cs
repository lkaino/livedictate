using System.Diagnostics;
using System.IO;
using System.Text;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>Runs a harness (a command-line AI agent) and streams its output.</summary>
public static class HarnessRunner
{
    /// <summary>
    /// Runs <paramref name="harness"/> in <paramref name="workDir"/> with its placeholders filled from
    /// <paramref name="values"/>. Returns the exit code. Cancelling kills the agent and its children.
    /// </summary>
    public static async Task<int> RunAsync(HarnessConfig harness, string workDir,
        IReadOnlyDictionary<string, string> values, Action<string> onOutput, CancellationToken cancel)
    {
        var args = SplitArguments(harness.Arguments).Select(a => Fill(a, values)).ToList();
        var exe = ResolveCommand(harness.Command)
            ?? throw new FileNotFoundException($"'{harness.Command}' wasn't found. Install it, or put its full path in the harness settings.");

        var info = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (Path.GetExtension(exe).ToLowerInvariant() is ".cmd" or ".bat")
        {
            // npm installs commands as .cmd scripts, which only cmd.exe can run.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"{string.Join(" ", new[] { exe }.Concat(args).Select(Quote))}\"";
        }
        else
        {
            info.FileName = exe;
            foreach (var a in args) info.ArgumentList.Add(a);
        }

        onOutput($"> {harness.Command} {string.Join(" ", args.Select(Quote))}");
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        process.Start();
        process.StandardInput.Close(); // never wait for typed input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancel.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        process.WaitForExit(); // flushes the last output lines
        cancel.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>Splits a command line at spaces; double quotes group words and are removed.</summary>
    public static List<string> SplitArguments(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values) template = template.Replace("{" + key + "}", value);
        return template;
    }

    /// <summary>Quotes an argument the way Windows programs split their command line.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Finds a command on the PATH the way the command prompt does (trying .exe, .cmd, ...).</summary>
    public static string? ResolveCommand(string command)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0) return null;
        var extensions = Path.HasExtension(command) ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("");

        IEnumerable<string> folders = Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in folders)
            foreach (var ext in extensions)
            {
                var path = folder.Length == 0 ? command + ext : Path.Combine(folder.Trim('"'), command + ext);
                // On Windows an extensionless file next to copilot.cmd is a shell script for Git Bash; skip it.
                if (File.Exists(path) && (ext.Length > 0 || Path.HasExtension(command) || !OperatingSystem.IsWindows()))
                    return Path.GetFullPath(path);
            }
        return null;
    }
}
