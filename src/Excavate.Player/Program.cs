using System.Reflection;
using Excavate.Core;
using Excavate.Player.Sdl;

namespace Excavate.Player;

internal static class Program
{
    // exit codes: done, not found, a mistake on the command line, and the machine or window failed
    private const int Done = 0, NotFound = 1, BadCommandLine = 2, CannotRun = 3;

    /// <summary>A mistake on the command line, said in one line.</summary>
    private sealed class UsageException(string message) : Exception(message);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (UsageException ex)
        {
            Error(ex.Message);
            Console.Error.WriteLine("Run 'excavate --help' for more.");
            return BadCommandLine;
        }
        catch (FileNotFoundException ex)
        {
            Error(ex.Message);
            return NotFound;
        }
        catch (NotSupportedException ex)
        {
            Error(ex.Message);
            return CannotRun;
        }
        catch (Exception ex) when (ex is SdlException or InvalidDataException or IOException)
        {
            Error(ex.Message);
            return CannotRun;
        }
    }

    /// <summary>The command line, run: what <see cref="Main"/> does, without its error reporting.</summary>
    internal static int Run(string[] args)
    {
        string? gameName = null;
        string? program = null;
        string arguments = "";
        string? saves = null;
        string? logPath = null;
        string? screenshot = null;
        string? statePath = null;
        bool resume = false;
        string settingsPath = Settings.DefaultPath;
        double? seconds = null;
        bool headless = false;
        bool list = false;
        bool remember = false;
        bool forget = false;
        bool forgetFolder = false;

        // the game's documents to show rather than play it: 0 for the list, -1 for all of them
        int? documents = null;

        // the options a profile can keep, as given here: they win over the profile for this run
        var given = new Profile();

        int i = 0;

        // the value after an option, which must be there and not be another option
        string Next(string option, string what)
            => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new UsageException($"{option} needs {what}");

        int Number(string option, string what, int min, int max)
        {
            string value = Next(option, what);
            return int.TryParse(value, out int number) && number >= min && number <= max
                ? number
                : throw new UsageException($"{option} needs {what}, not '{value}'");
        }

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "-?":
                    PrintHelp();
                    return Done;
                case "--version":
                    Console.WriteLine($"excavate {Version()}");
                    return Done;
                case "--list":
                    list = true;
                    break;
                case "--settings":
                    settingsPath = Path.GetFullPath(Next("--settings", "a file name"));
                    break;
                case "--remember":
                    remember = true;
                    break;
                case "--forget":
                    forget = true;
                    break;
                case "--forget-folder":
                    forgetFolder = true;
                    break;
                case "--args":
                    arguments = i + 1 < args.Length ? args[++i] : throw new UsageException("--args needs the program's arguments");
                    break;
                case "--saves":
                    saves = Next("--saves", "a folder");
                    break;
                case "--cycles":
                    given.Cycles = Number("--cycles", "instructions a millisecond, as in 3000", 100, 1_000_000);
                    break;
                case "--scale":
                    given.Scale = Number("--scale", "a scale from 1 to 8", 1, 8);
                    break;
                case "--fullscreen":
                    given.Fullscreen = true;
                    break;
                case "--windowed":
                    given.Fullscreen = false;
                    break;
                case "--square-pixels":
                    given.SquarePixels = true;
                    break;
                case "--no-square-pixels":
                    given.SquarePixels = false;
                    break;
                case "--composite" or "--rgb":
                    given.Composite = args[i] == "--composite";
                    break;
                case "--joystick" or "--no-joystick":
                    given.Joystick = args[i] == "--joystick";
                    break;
                case "--mouse" or "--no-mouse":
                    given.Mouse = args[i] == "--mouse";
                    break;
                case "--mouse-speed":
                    given.MouseSpeed = Number("--mouse-speed", "a speed in percent, from 10 to 400", 10, 400);
                    break;
                case "--sound" or "--no-sound":
                    given.Sound = args[i] == "--sound";
                    break;
                case "--state":
                    statePath = Path.GetFullPath(Next("--state", "a file name"));
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--log":
                    logPath = Next("--log", "a file name");
                    break;
                case "--seconds":
                    seconds = Number("--seconds", "a number of seconds", 1, int.MaxValue);
                    break;
                case "--screenshot":
                    screenshot = Next("--screenshot", "a file name");
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--documents":
                    // a number, or all, may follow; anything else is the game's name or an option
                    documents = 0;
                    if (i + 1 < args.Length && args[i + 1] == "all")
                    {
                        documents = -1;
                        i++;
                    }
                    else if (i + 1 < args.Length && int.TryParse(args[i + 1], out int number))
                    {
                        documents = number > 0 ? number : throw new UsageException($"--documents needs a document's number, from 1, or all, not '{args[i + 1]}'");
                        i++;
                    }

                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException($"unknown option {args[i]}");
                    }

                    if (gameName is null)
                    {
                        gameName = args[i];
                    }
                    else
                    {
                        program = program is null ? args[i] : throw new UsageException("one game at a time");
                    }

                    break;
            }
        }

        var settings = Settings.Load(settingsPath);
        bool changed = false;

        if (forgetFolder)
        {
            // only by a short name: a folder given here would be remembered again at once
            var forgetting = (gameName is not null ? Games.Named(gameName) : null)
                ?? throw new UsageException("--forget-folder needs a game's short name, such as portal");
            if (settings.Forget(forgetting) is { } forgotten)
            {
                changed = true;
                Console.WriteLine($"excavate: forgot {forgetting.Title}'s folder, {forgotten}");
            }
            else
            {
                Console.WriteLine($"excavate: no folder was kept for {forgetting.Title}");
            }
        }

        if (list)
        {
            if (changed)
            {
                settings.Save();
            }

            List(settings);
            return Done;
        }

        if (headless && seconds is null)
        {
            throw new UsageException("--headless needs --seconds, or it would never end");
        }

        Game game;
        if (gameName is null)
        {
            if (headless)
            {
                throw new UsageException("--headless needs a game");
            }

            // no game named: the menu, which can also set games up
            if (SdlMenu.Choose(settings) is not { } chosen)
            {
                return Done;
            }

            game = chosen;
        }
        else if (Games.Named(gameName) is { } known)
        {
            string folder = settings.FolderOf(known)
                ?? throw new FileNotFoundException($"{known.Title} isn't set up yet: run excavate \"<its folder>\" once, or drop the folder on the menu's window");

            // the game's own program, or another of its folder's, such as its setup
            game = program is null ? Games.In(folder, known) : Games.Find(folder, program);
        }
        else
        {
            game = Games.Find(gameName, program);
            if (game.Known is { } found)
            {
                // a folder given once is remembered, so the game can be named by its short name
                changed |= settings.Remember(found, game.Folder);
            }
        }

        // the game's profile, with what was given here over it
        var profile = new Profile();
        if (game.Known is { } knownGame)
        {
            if (forget && settings.Profiles.Remove(knownGame.Key))
            {
                changed = true;
            }

            profile.Take(settings.ProfileOf(knownGame));
            if (remember && !given.IsEmpty)
            {
                var kept = settings.ProfileOf(knownGame);
                kept.Take(given);
                settings.Profiles[knownGame.Key] = kept;
                changed = true;
            }
        }
        else if (remember || forget)
        {
            throw new UsageException($"the player keeps no profile for {game.Program}, a program it doesn't know");
        }

        profile.Take(given);
        if (changed)
        {
            settings.Save();
        }

        if (documents is { } which)
        {
            return ShowDocuments(game, which);
        }

        bool sound = profile.Sound ?? true;
        saves ??= Games.SaveFolder(game);
        Directory.CreateDirectory(saves);

        using var log = logPath is null ? null : new StreamWriter(logPath);
        var machine = new Machine(game.Folder, saves, game.Video)
        {
            InstructionsPerSecond = (profile.Cycles ?? game.Cycles) * 1000,
            Log = log is null ? null : log.WriteLine,
            Mouse = (profile.Mouse ?? true) && !headless,
            Composite = profile.Composite ?? false,
        };

        if (game.Disks is { Length: > 0 } disks)
        {
            // the game's floppies by drive A:, disk 1 in it, with the protection the images lost
            machine.InsertFloppies(disks);
            foreach (var extra in game.Known?.Disks?.Protection ?? [])
            {
                if (extra.Disk < disks.Length)
                {
                    machine.RestoreSectors(extra.Disk, extra.Cylinder, extra.Head, extra.First, extra.Last);
                }
            }

            // and its prompts for another disk, answered for the player
            foreach (var prompt in game.Known?.Disks?.Prompts ?? [])
            {
                if (prompt.DigitAt >= 0)
                {
                    machine.AnswerDiskPrompt(prompt.KeyWait, prompt.Caller, prompt.DigitAt, prompt.Digits);
                }
                else
                {
                    machine.AnswerDiskPrompt(prompt.KeyWait, prompt.Caller, prompt.Disk);
                }
            }

            string names = string.Join(", ", disks.Select((disk, n) => $"{n + 1} {Path.GetFileName(disk)}"));
            string prompts = game.Known?.Disks?.Prompts is { Length: > 0 } ? ", and the game's prompts for one are answered for you" : "";
            Console.WriteLine($"excavate: disks {names}; Ctrl+F1 to Ctrl+F{Math.Min(disks.Length, 4)} put one in drive A:{prompts}");
        }

        // what a crack changed in the game's programs, put back as each is loaded
        foreach (var crack in game.Known?.Cracks ?? [])
        {
            machine.RestoreProgramBytes(crack.Program, crack.Offset, crack.Cracked, crack.Original);
        }

        machine.Load(game.Program, arguments);
        statePath ??= Games.StateFile(game);
        if (resume)
        {
            // on from where Ctrl+F5 last saved, the machine as it was then
            if (!File.Exists(statePath))
            {
                throw new FileNotFoundException($"no state saved at {statePath}: Ctrl+F5 saves one while playing");
            }

            using var state = File.OpenRead(statePath);
            machine.LoadState(state);
            Console.WriteLine($"excavate: on from the state saved {File.GetLastWriteTime(statePath):g}");
        }

        Console.WriteLine($"excavate: {game.Title}, {game.Program} at {machine.InstructionsPerSecond / 1000} cycles; saves in {saves}");
        if (game.Known is { } about)
        {
            Console.WriteLine($"excavate: {about.Title}: {about.Note}");
            if (string.Equals(game.Program, about.Program, StringComparison.OrdinalIgnoreCase) && about.Hint?.Invoke(game.Folder, saves) is { } hint)
            {
                Console.WriteLine($"excavate: {hint}");
            }
        }

        bool running;
        try
        {
            if (headless)
            {
                running = machine.Run((long)(seconds!.Value * machine.InstructionsPerSecond));
            }
            else
            {
                var options = new PlayerOptions
                {
                    Title = $"Excavate - {game.Title}",
                    Scale = profile.Scale ?? 3,
                    Fullscreen = profile.Fullscreen ?? false,
                    SquarePixels = profile.SquarePixels ?? false,
                    Joystick = profile.Joystick ?? true,
                    Sound = sound,
                    Mouse = profile.Mouse ?? true,
                    MouseSpeed = (profile.MouseSpeed ?? 100) / 100.0,
                    MouseJoystick = game.Known?.MouseJoystick ?? false,
                    StatePath = statePath,
                    Icon = GameIcons.For(game),
                };

                using var player = new SdlPlayer(machine, options);
                running = player.Run(seconds);
            }
        }
        catch (NotSupportedException ex)
        {
            // something the machine doesn't do yet: where the game was, for the log and the notes
            var cpu = machine.Cpu;
            Error($"{ex.Message} At {cpu.Cs:X4}:{cpu.Eip:X4}, {machine.Time.TotalSeconds:F1} s in.");
            return CannotRun;
        }

        if (screenshot is not null)
        {
            Screenshot.WritePng(machine, screenshot);
        }

        if (machine.Exited)
        {
            Console.WriteLine($"excavate: {game.Program} ended with code {machine.ExitCode}");
            return machine.ExitCode == 0 ? Done : CannotRun;
        }

        if (!running && !machine.Exited)
        {
            Error($"the processor stopped, halted with interrupts off, at {machine.Cpu.Cs:X4}:{machine.Cpu.Eip:X4}");
            return CannotRun;
        }

        return Done;
    }

    /// <summary>
    /// A game's documents, read from its folder: a list of them (0), one by its number, or all of
    /// them (-1).
    /// </summary>
    private static int ShowDocuments(Game game, int which)
    {
        if (game.Known?.Documents is not { } read)
        {
            throw new UsageException($"{game.Title} keeps no documents the player can read");
        }

        var documents = read(game.Folder);
        if (which == 0)
        {
            Console.WriteLine($"{game.Title}: {documents.Count} documents; --documents <n> shows one, --documents all every one");
            string? group = null;
            foreach (var document in documents)
            {
                if (document.Group != group)
                {
                    group = document.Group;
                    Console.WriteLine();
                    Console.WriteLine($"{char.ToUpperInvariant(group[0])}{group[1..]}:");
                }

                Console.WriteLine($"{document.Number,4}  {document.Title}");
            }

            return Done;
        }

        if (which > documents.Count)
        {
            throw new UsageException($"{game.Title} has {documents.Count} documents, not {which}");
        }

        var shown = which < 0 ? documents : [documents[which - 1]];
        for (int n = 0; n < shown.Count; n++)
        {
            if (n > 0)
            {
                Console.WriteLine();
            }

            Console.WriteLine($"--- {shown[n].Number}, from {shown[n].Group}");
            Console.Write(shown[n].Text);
        }

        return Done;
    }

    /// <summary>What's set up: each game, where it is, how it's played, and where it stands.</summary>
    private static void List(Settings settings)
    {
        Console.WriteLine($"settings: {settings.Path}");
        foreach (var game in Games.Known)
        {
            Console.WriteLine();
            string? folder = settings.FolderOf(game);
            Console.WriteLine($"{game.Key}  {game.Title} ({game.Year}): {game.Note}");
            Console.WriteLine($"  {folder ?? $"not set up (run excavate \"<its folder>\" once)"}");
            if (folder is not null)
            {
                Console.WriteLine($"  played with {settings.ProfileOf(game)}");
            }
        }
    }

    private static void PrintHelp()
    {
        string names = string.Join(", ", Games.Known.Select(game => game.Key));
        Console.WriteLine($"""
            excavate {Version()}: plays a chosen set of DOS games, and only those.

            usage: excavate [game] [program] [options]

              [game]             a game's short name ({names});
                                 or its folder, or its program. A folder given once is remembered,
                                 so the short name finds it after. With no game, a menu in a
                                 window lists them.
              [program]          another program in the game's folder, such as its setup
                                 (excavate president SU.EXE)

            options:
              --list             what's set up: each game's folder, and how it's played
              --remember         keep this run's options below as the game's own
              --forget           go back to the defaults for the game
              --forget-folder    forget the folder the game was set up from (with --list, forget
                                 and list rather than play)
              --settings <file>  the settings file (by default excavate.json in the user's
                                 application data, beside the saves)
              --args "<text>"    the program's command line arguments
              --saves <folder>   where the game's writes go (by default a folder of its own in the
                                 user's application data); the game's folder is never written
              --cycles <n>       instructions a millisecond, as DOSBox counts cycles: the game's own
                                 by default
              --scale <n>        the window's first size, n times the picture (3)
              --fullscreen       start in full screen (--windowed: in a window)
              --square-pixels    square pixels, not the 4:3 picture a monitor showed
                                 (--no-square-pixels: 4:3)
              --composite        a CGA game as a composite monitor or television showed it, with
                                 the colors its patterns of dots made (--rgb: an RGB monitor's)
              --no-joystick      no joystick on the game port, even with a gamepad plugged in
              --no-mouse         no mouse
              --no-sound         no sound
              --mouse-speed <n>  the mouse's speed in percent (100): at 100, moving across the
                                 picture moves the game's pointer across its screen
                                 (--joystick, --mouse, --sound: back on)
              --resume           start from the state Ctrl+F5 last saved
              --state <file>     the file Ctrl+F5 saves the state to, Ctrl+F9 and --resume load
                                 it from (by default one for each program, in a folder states
                                 beside the saves)
              --log <file>       write every DOS, BIOS and EMS call, and unanswered port, to a file
              --seconds <n>      stop after n seconds of the game's time
              --screenshot <f>   save the picture as a PNG when stopping
              --headless         no window: run --seconds seconds and stop
              --documents [n]    list the documents a game keeps, rather than play it, or show the
                                 one numbered n, or all of them (The President Is Missing's data
                                 base, its Top Secret files included, and its messages)

            playing:
              the keyboard is the PC's, key for key
              a gamepad is the joystick: the left stick or d-pad moves it, the face buttons are its
              four buttons (plug it in before starting: games look for a joystick as they start)
              the mouse is the PC's once a click in the window captures it; Ctrl+F10 frees it
              F11 full screen, Shift+F11 pixel shape, F12 full speed on and off, Pause pauses
              Ctrl+F5 saves the whole machine's state, Ctrl+F9 loads it back
              Ctrl+F1 to Ctrl+F4 put disk 1 to 4 in drive A:, for a game played from its disks
              Ctrl+F11 switches a CGA game between an RGB and a composite monitor
            """);
    }

    private static string Version()
        => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private static void Error(string message) => Console.Error.WriteLine($"excavate: {message}");
}
