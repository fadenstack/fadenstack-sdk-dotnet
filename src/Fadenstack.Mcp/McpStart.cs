using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fadenstack.Mcp;

/// <summary>Why a stdio server was not started, before anything ran (sdk 0.3.1). A person-facing text belongs to the app (five languages).</summary>
public enum McpStartRefusal
{
    /// <summary>
    /// The program is a <c>.cmd</c> or <c>.bat</c> file, which Windows runs only through <c>cmd.exe</c>, and an argument (or the
    /// file's own path) holds a character <c>cmd.exe</c> reads as a command: <c>&amp; | &lt; &gt; ^ % ! " ( )</c> or a control character.
    /// </summary>
    ShellCharacter,
}

/// <summary>A stdio server was refused before its program ran; <see cref="Reason"/> says why. The message names no argument.</summary>
public sealed class McpStartRefusedException : InvalidOperationException
{
    public McpStartRefusedException(McpStartRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public McpStartRefusal Reason { get; }
}

/// <summary>What starts: the file and the command line it gets. On Windows a program starts without a shell, except a batch file.</summary>
internal sealed record McpStartPlan(string FileName, string Arguments, bool ThroughCmd);

/// <summary>
/// How a stdio server is started (sdk 0.3.1; OWASP ASVS 5.0 V1 injection, V13 configuration). The command is resolved to one
/// absolute file first (<see cref="McpProgramPinner.Resolve(string, string?, string?)"/>), and that file is what starts, with the
/// arguments as a list: on Linux and macOS no shell is involved, and on Windows a <c>.exe</c> starts with
/// <c>CreateProcess</c> and a properly quoted command line. Only a <c>.cmd</c> or <c>.bat</c> file needs <c>cmd.exe</c>; its
/// arguments may then hold none of the characters <c>cmd.exe</c> reads as commands, or the server is not started.
/// </summary>
internal static class McpStart
{
    /// <summary>The transport for a stdio server whose program is <paramref name="program"/> (an absolute path).</summary>
    internal static IClientTransport Open(string label, string? program, IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment, string? workingDirectory, bool windows)
    {
        if (program is null)
        {
            throw new FileNotFoundException("The program was not found: check the command and the PATH.");
        }
        if (!windows)
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = label,
                Command = program,
                Arguments = [.. arguments],
                EnvironmentVariables = environment is null ? null : new Dictionary<string, string?>(environment),
                WorkingDirectory = workingDirectory,
            });
        }
        var plan = Plan(program, arguments, CmdPath());
        return new McpProcessTransport(label, StartInfo(plan, environment, workingDirectory));
    }

    /// <summary>
    /// The file and command line that start <paramref name="program"/> on Windows: a <c>.cmd</c>/<c>.bat</c> goes through
    /// <paramref name="cmdPath"/> with <c>/d /s /c</c>, after the check of every argument; anything else starts as itself with a
    /// quoted command line.
    /// </summary>
    internal static McpStartPlan Plan(string program, IReadOnlyList<string> arguments, string cmdPath)
    {
        var dot = program.LastIndexOf('.');
        var extension = dot > program.LastIndexOfAny(['/', '\\']) ? program.Substring(dot) : ""; // not Path.GetExtension: .NET Framework throws on a quote
        var batch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        if (!batch)
        {
            return new McpStartPlan(program, CommandLine(arguments), ThroughCmd: false);
        }
        // Inside quotes, cmd.exe still expands %, and a quote ends them: nothing else in a path is read as a command.
        if (program.IndexOf('%') >= 0 || program.IndexOf('"') >= 0 || HasControl(program) || arguments.Any(HasCmdCharacter))
        {
            throw new McpStartRefusedException(McpStartRefusal.ShellCharacter,
                "A .cmd or .bat program runs through cmd.exe, which reads some characters in an argument as commands, so it was not started.");
        }
        var line = new StringBuilder("/d /s /c \"\"").Append(program).Append('"');
        foreach (var argument in arguments)
        {
            AppendArgument(line, argument);
        }
        return new McpStartPlan(cmdPath, line.Append('"').ToString(), ThroughCmd: true);
    }

    /// <summary>The arguments as one Windows command line (the rules of .NET's <c>PasteArguments</c>).</summary>
    internal static string CommandLine(IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        foreach (var argument in arguments)
        {
            AppendArgument(line, argument);
        }
        return line.Length == 0 ? "" : line.ToString(1, line.Length - 1);
    }

    // An argument that is empty or holds white space or a quote is quoted: the backslashes before a quote (or before the closing
    // quote) are doubled, a quote is escaped. This is what CommandLineToArgvW and the C runtime read back.
    private static void AppendArgument(StringBuilder line, string argument)
    {
        line.Append(' ');
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }
        line.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var slashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    slashes++;
                }
                if (i == argument.Length)
                {
                    line.Append('\\', slashes * 2);
                }
                else if (argument[i] == '"')
                {
                    line.Append('\\', slashes * 2 + 1).Append('"');
                    i++;
                }
                else
                {
                    line.Append('\\', slashes);
                }
                continue;
            }
            if (c == '"')
            {
                line.Append('\\').Append('"');
                continue;
            }
            line.Append(c);
        }
        line.Append('"');
    }

    /// <summary>Whether <paramref name="argument"/> holds a character <c>cmd.exe</c> reads as a command, or a control character.</summary>
    internal static bool HasCmdCharacter(string argument) => argument.IndexOfAny(CmdCharacters) >= 0 || HasControl(argument);

    private static readonly char[] CmdCharacters = ['&', '|', '<', '>', '^', '%', '!', '"', '(', ')'];

    private static bool HasControl(string text) => text.Any(c => c < ' ' || c == '\u007f');

    /// <summary><c>cmd.exe</c> in the system folder, never found through the path or the working folder.</summary>
    private static string CmdPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    internal static ProcessStartInfo StartInfo(McpStartPlan plan, IDictionary<string, string?>? environment, string? workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
        }
        return info;
    }
}

/// <summary>
/// A stdio transport over a process this module started itself (Windows): the MCP SDK's own would hand the command to
/// <c>cmd.exe /c</c> whatever it is. It talks over the process's streams and, when closed, ends the process and what it started.
/// </summary>
internal sealed class McpProcessTransport : IClientTransport
{
    private readonly ProcessStartInfo _info;

    public McpProcessTransport(string name, ProcessStartInfo info)
    {
        Name = name;
        _info = info;
    }

    public string Name { get; }

    /// <summary>What this transport will start (tests).</summary>
    internal ProcessStartInfo StartInfo => _info;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var process = new Process { StartInfo = _info };
        process.ErrorDataReceived += (_, _) => { }; // the pipe is drained, nothing is kept: it could hold secrets
        try
        {
            if (!process.Start())
            {
                throw new IOException("Failed to start MCP server process.");
            }
            process.BeginErrorReadLine();
            cancellationToken.ThrowIfCancellationRequested(); // cancelled while it started: the process is stopped below
            var inner = await new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream)
                .ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new Session(inner, process);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        catch (Exception e)
        {
            Stop(process);
            throw new IOException("Failed to connect transport.", e);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                KillTree(process);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone, or not started
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            var taskkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            using var killer = Process.Start(new ProcessStartInfo(taskkill, "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(5000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // fall through to the process itself
        }
        if (!process.HasExited)
        {
            process.Kill();
        }
    }

    private sealed class Session : ITransport
    {
        private readonly ITransport _inner;
        private readonly Process _process;

        public Session(ITransport inner, Process process)
        {
            _inner = inner;
            _process = process;
        }

        public string? SessionId => _inner.SessionId;

        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => _inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            _inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            try
            {
                _process.StandardInput.Close(); // a server that reads its input ends by itself
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                // closed already
            }
            await Task.Run(() => _process.WaitForExit(3000)).ConfigureAwait(false);
            Stop(_process);
        }
    }
}
