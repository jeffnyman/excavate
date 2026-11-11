using System.Text;

namespace Excavate.Core.Dos;

/// <summary>
/// The part of COMMAND.COM that runs batch files: enough of its language for the batch files
/// W.C.A.T. starts its launcher and the games with. Each line is a command built into the shell
/// (ECHO, CD, IF, GOTO, CALL, DEL and a few more) or a program to run; when a program ends, the
/// batch file carries on from the line after it.
/// </summary>
/// <remarks>
/// <para>
/// The shell lives in C#, as DOSBox's does, not in the machine's memory: between programs no
/// code runs, the machine just takes the next line. Like COMMAND.COM it reads a batch file
/// again each time it's started, so a file a program writes (W.C.A.T.'s launcher writes
/// <c>ACTION.BAT</c>) is read as it is when it's run, not as it was when the first batch began.
/// </para>
/// <para>
/// What it doesn't do: redirection and pipes, FOR, SHIFT, SET, PAUSE and CHOICE, and anything
/// that waits for a key. A line it doesn't know and that names no program gets DOS's "Bad
/// command or file name", in the log, and the batch file carries on, as it would.
/// </para>
/// </remarks>
internal sealed class CommandShell(DosServices dos, DosDrive drive, Machine machine)
{
    /// <summary>A batch file being run: its lines, the next to run, and its arguments, %0 to %9.</summary>
    private sealed class Frame
    {
        public string[] Lines { get; init; } = [];

        public int Next { get; set; }

        public string[] Arguments { get; init; } = [];
    }

    // the batch files being run, innermost last: CALL starts one inside another
    private readonly List<Frame> _frames = [];

    private bool _echo = true;

    /// <summary>Whether a batch file is running, with lines still to come.</summary>
    public bool Running => _frames.Count > 0;

    /// <summary>Starts a batch file, by its path on drive C: and its arguments.</summary>
    public void Start(string[] parts, string arguments) => _frames.Add(Read(parts, arguments));

    /// <summary>
    /// Runs lines until one starts a program, which the machine then runs; false when the batch
    /// files have all ended instead.
    /// </summary>
    public bool Continue()
    {
        while (_frames.Count > 0)
        {
            var frame = _frames[^1];
            if (frame.Next >= frame.Lines.Length)
            {
                _frames.RemoveAt(_frames.Count - 1);
                continue;
            }

            string line = frame.Lines[frame.Next++];
            if (Execute(line, frame))
            {
                return true;
            }
        }

        return false;
    }

    private Frame Read(string[] parts, string arguments)
    {
        string file = drive.FindFile(parts) ?? throw new FileNotFoundException($"C:\\{DosDrive.Join(parts)} isn't on drive C:.");

        // DOS text: lines ended by CR LF (or a lone LF), and the file by Ctrl+Z if it has one
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
        int end = text.IndexOf('\x1A');
        if (end >= 0)
        {
            text = text[..end];
        }

        string[] words = arguments.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        return new Frame
        {
            Lines = text.Replace("\r\n", "\n").Split('\n'),
            Arguments = ["C:\\" + DosDrive.Join(parts), .. words],
        };
    }

    /// <summary>Runs one line; true when it started a program.</summary>
    private bool Execute(string line, Frame frame)
    {
        line = Expand(line, frame).Trim();
        bool quiet = line.StartsWith('@');
        if (quiet)
        {
            line = line[1..].TrimStart();
        }

        if (line.Length == 0 || line.StartsWith(':'))
        {
            return false;
        }

        if (_echo && !quiet)
        {
            Say($"C:\\{drive.CurrentDirectory}>{line}");
        }

        machine.Trace($"batch: {line}");
        return Command(line, frame);
    }

    /// <summary>A command, from an IF's as well as from a line; true when it started a program.</summary>
    private bool Command(string line, Frame frame)
    {
        if (Word(line, "REM", out _))
        {
            return false;
        }

        if (Word(line, "ECHO", out string rest))
        {
            Echo(rest);
            return false;
        }

        if (Word(line, "CD", out rest) || Word(line, "CHDIR", out rest))
        {
            ChangeDirectory(rest.Trim());
            return false;
        }

        if (Word(line, "IF", out rest))
        {
            return If(rest, frame);
        }

        if (Word(line, "GOTO", out rest))
        {
            GoTo(rest.Trim(), frame);
            return false;
        }

        if (Word(line, "CALL", out rest))
        {
            return Run(rest.Trim(), call: true);
        }

        if (Word(line, "DEL", out rest) || Word(line, "ERASE", out rest))
        {
            Delete(rest.Trim());
            return false;
        }

        if (Word(line, "CLS", out _))
        {
            // the screen's not shown between programs, so there's nothing to clear
            return false;
        }

        if (Word(line, "EXIT", out _))
        {
            _frames.Clear();
            return false;
        }

        if (Word(line, "LOADFIX", out rest))
        {
            // loads a program above the first 64 KB, which every program here is: run it as it is
            rest = rest.TrimStart();
            if (rest.StartsWith('-'))
            {
                int space = rest.IndexOfAny([' ', '\t']);
                rest = space < 0 ? "" : rest[space..].TrimStart();
            }

            return rest.Length > 0 && Run(rest, call: false);
        }

        if (line.Length == 2 && line[1] == ':')
        {
            // changing drives: C: is the only one
            if (char.ToUpperInvariant(line[0]) != 'C')
            {
                Say("Invalid drive specification");
            }

            return false;
        }

        return Run(line, call: false);
    }

    /// <summary>
    /// Whether a line starts with a command's name, and what follows it. The name ends where a
    /// space or a separator does, so <c>ECHO.</c> and <c>CD..</c> are ECHO and CD.
    /// </summary>
    private static bool Word(string line, string word, out string rest)
    {
        rest = "";
        if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (line.Length > word.Length && line[word.Length] is not (' ' or '\t' or '.' or '\\' or '/' or ':' or ';' or ',' or '='))
        {
            return false;
        }

        rest = line[word.Length..];
        return true;
    }

    /// <summary>%0 to %9 as the batch file's arguments, %NAME% as an environment variable, %% as %.</summary>
    private static string Expand(string line, Frame frame)
    {
        if (!line.Contains('%'))
        {
            return line;
        }

        var text = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '%' || i + 1 >= line.Length)
            {
                text.Append(line[i]);
            }
            else if (line[i + 1] == '%')
            {
                text.Append('%');
                i++;
            }
            else if (char.IsAsciiDigit(line[i + 1]))
            {
                int n = line[i + 1] - '0';
                text.Append(n < frame.Arguments.Length ? frame.Arguments[n] : "");
                i++;
            }
            else if (line.IndexOf('%', i + 1) is int close and > 0)
            {
                text.Append(Variable(line[(i + 1)..close]));
                i = close;
            }
            else
            {
                text.Append(line[i]);
            }
        }

        return text.ToString();
    }

    // the environment programs are given (see DosServices.Load)
    private static string Variable(string name) => name.ToUpperInvariant() switch
    {
        "COMSPEC" => "C:\\COMMAND.COM",
        "PATH" => "C:\\",
        _ => "",
    };

    private void Echo(string rest)
    {
        if (rest.StartsWith('.') || rest.StartsWith(':') || rest.StartsWith('/'))
        {
            // ECHO. prints what follows the dot, or an empty line
            Say(rest[1..]);
            return;
        }

        string text = rest.Length > 0 ? rest[1..] : "";
        switch (text.Trim().ToUpperInvariant())
        {
            case "OFF": _echo = false; break;
            case "ON": _echo = true; break;
            case "": Say(_echo ? "ECHO is on" : "ECHO is off"); break;
            default: Say(text); break;
        }
    }

    private void ChangeDirectory(string path)
    {
        if (path.Length == 0)
        {
            Say($"C:\\{drive.CurrentDirectory}");
        }
        else if (drive.Parse(path) is not { } parts || !drive.ChangeDirectory(parts))
        {
            Say("Invalid directory");
        }
    }

    /// <summary>IF [NOT] EXIST name, IF [NOT] ERRORLEVEL n, IF [NOT] one==two, then a command.</summary>
    private bool If(string rest, Frame frame)
    {
        rest = rest.TrimStart();
        bool not = Word(rest, "NOT", out string afterNot);
        if (not)
        {
            rest = afterNot.TrimStart();
        }

        bool condition;
        string command;
        if (Word(rest, "EXIST", out string afterExist))
        {
            (string name, command) = Split(afterExist.TrimStart());
            condition = Exists(name);
        }
        else if (Word(rest, "ERRORLEVEL", out string afterLevel))
        {
            (string number, command) = Split(afterLevel.TrimStart());
            condition = int.TryParse(number, out int level) && machine.ExitCode >= level;
        }
        else if (rest.IndexOf("==", StringComparison.Ordinal) is int equals and >= 0)
        {
            string left = rest[..equals].Trim();
            (string right, command) = Split(rest[(equals + 2)..].TrimStart());
            condition = left == right;
        }
        else
        {
            Say("Syntax error");
            return false;
        }

        return condition != not && command.Length > 0 && Command(command, frame);

        static (string First, string After) Split(string text)
        {
            int space = text.IndexOfAny([' ', '\t']);
            return space < 0 ? (text, "") : (text[..space], text[space..].TrimStart());
        }
    }

    private bool Exists(string name)
    {
        if (drive.Parse(name) is not { } parts)
        {
            return false;
        }

        return parts.Length > 0 && (parts[^1].Contains('*') || parts[^1].Contains('?'))
            ? drive.List(parts[..^1]).Any(entry => entry is FileInfo && DosServices.Matches(entry.Name.ToUpperInvariant(), parts[^1]))
            : drive.FindFile(parts) is not null;
    }

    private void GoTo(string label, Frame frame)
    {
        label = label.TrimStart(':').Split([' ', '\t'], 2)[0];
        for (int i = 0; i < frame.Lines.Length; i++)
        {
            string line = frame.Lines[i].Trim();
            if (line.StartsWith(':') && string.Equals(line[1..].Split([' ', '\t'], 2)[0], label, StringComparison.OrdinalIgnoreCase))
            {
                frame.Next = i + 1;
                return;
            }
        }

        // as DOS does: say so, and end the batch file
        Say("Label not found");
        frame.Next = frame.Lines.Length;
    }

    private void Delete(string name)
    {
        if (drive.Parse(name) is not { } parts || parts.Length == 0)
        {
            Say("File not found");
            return;
        }

        string[] names = parts[^1].Contains('*') || parts[^1].Contains('?')
            ? [.. drive.List(parts[..^1]).Where(entry => entry is FileInfo && DosServices.Matches(entry.Name.ToUpperInvariant(), parts[^1])).Select(entry => entry.Name.ToUpperInvariant())]
            : [parts[^1]];
        bool any = false;
        foreach (string file in names)
        {
            any |= drive.Delete([.. parts[..^1], file]);
        }

        if (!any)
        {
            Say("File not found");
        }
    }

    /// <summary>
    /// Runs a program, or a batch file: CALL runs a batch file inside this one, and without CALL
    /// it takes this one's place. True when it started a program.
    /// </summary>
    private bool Run(string line, bool call)
    {
        int space = line.IndexOfAny([' ', '\t', '/']);
        string name = space < 0 ? line : line[..space];
        string arguments = space < 0 ? "" : line[space..].TrimStart();
        if (Find(name) is not { } parts)
        {
            Say("Bad command or file name");
            return false;
        }

        if (parts[^1].EndsWith(".BAT", StringComparison.Ordinal))
        {
            if (!call)
            {
                _frames.RemoveAt(_frames.Count - 1);
            }

            _frames.Add(Read(parts, arguments));
            return false;
        }

        dos.LoadProgram(parts, arguments);
        return true;
    }

    /// <summary>
    /// The program a name runs: as it's given, or with .COM, .EXE or .BAT after it, in the
    /// current directory and then, for a bare name, in the root, which is the PATH.
    /// </summary>
    private string[]? Find(string name)
    {
        string[] extensions = Path.HasExtension(name) ? [""] : [".COM", ".EXE", ".BAT"];
        bool bare = !name.Contains('\\') && !name.Contains('/') && !name.Contains(':');
        foreach (string folder in bare ? new[] { "", "\\" } : [""])
        {
            foreach (string extension in extensions)
            {
                if (drive.Parse(folder + name + extension) is { Length: > 0 } parts && drive.FindFile(parts) is not null)
                {
                    return parts;
                }
            }
        }

        return null;
    }

    private void Say(string text) => dos.ConsoleLine(text);
}
