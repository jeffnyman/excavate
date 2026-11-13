namespace Excavate.Core.Tests;

/// <summary>
/// <i>Guardians of Infinity: To Save Kennedy</i> (Paragon Software, 1988), from the player's
/// copy, started as it was: by its batch file, and its slide show and its ending by their own
/// programs. The pictures go to <c>screenshots</c> beside the tests. Without the game (in
/// EXCAVATE_GAMES, or _excavating/guardken/guardken above the tests), these pass without
/// checking.
/// </summary>
public sealed class KennedyTests : IDisposable
{
    // what the game leaves in the BIOS's inter-application area at 0000:04F0 for its ending
    private const string Token = "1012ABACAB";

    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    private static string Shots
    {
        get
        {
            string shots = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(shots);
            return shots;
        }
    }

    [Fact]
    public void The_timeship_arrives_on_a_PC_with_no_network_and_no_multitasker()
    {
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.Load("KENNEDY.BAT");
        machine.Run(machine.InstructionsPerSecond * 20);

        // its library asks for NetWare's shell and for a multitasker's video buffer, and gets
        // neither, as on a PC without them
        Assert.Contains("21/EA NetWare's shell (AL=01h): none", _log);
        Assert.Contains("10/FE no multitasker's video buffer", _log);

        string screen = Screen(machine);
        Assert.Contains("the timeship has arrived", screen);
        Assert.Contains("Kennedy: New York City, New York", screen);
        Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-start.png"));
    }

    [Fact]
    public void The_slide_show_tells_the_story_so_far_a_key_a_page()
    {
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.Load("KSHOW.EXE");

        // its title picture, then a page of text that waits for a key
        machine.Run(machine.InstructionsPerSecond * 15);
        Assert.Contains("21/3D open jfkt1 (read): handle 5, 24178 bytes", _log);
        Assert.Contains("21/3D open SHOW.DAT (read): handle 5, 5252 bytes", _log);
        Assert.Equal(0x0D, machine.VideoMode);

        // its pictures come from P, one after another in it, at the offsets SHOW.DAT gives
        for (int page = 1; page <= 40 && !_log.Contains("exit 3"); page++)
        {
            Press(machine, 0x39);
            machine.Run(machine.InstructionsPerSecond * 6);
            if (page == 4)
            {
                Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-show.png"));
            }
        }

        Assert.Contains("21/42 seek 5 (P) to 223106", _log);
        Assert.Contains(_log, line => line.StartsWith("console: ") && line.EndsWith("END OF ENTRY."));
        Assert.Contains("exit 3", _log);
    }

    [Fact]
    public void The_ending_wont_run_but_after_the_game()
    {
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return;
        }

        // started as the batch file starts it, but without the game before it
        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.Load("RUND.EXE", "Z k0");
        machine.Run(machine.InstructionsPerSecond * 5);

        Assert.Contains("console: Bad command or file name", _log);
        Assert.Contains("exit 1", _log);
    }

    [Fact]
    public void After_the_game_the_ending_puts_you_on_Air_Force_One()
    {
        if (OnAirForceOne() is not { } machine)
        {
            return;
        }

        // the token is gone, so it's good once
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(0, machine.Read(0x4F0 + (uint)i)));

        string screen = Screen(machine);
        Assert.Contains("Time Remaining Until Flight Reaches Dallas: 15 Minutes", screen);
        Assert.Contains("Well Mr. Cooper, you've convinced", screen);
        Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-air-force-one.png"));
    }

    [Fact]
    public void The_gunman_on_the_grassy_knoll_becomes_Oswald_before_Kennedy_hears_him()
    {
        if (OnAirForceOne() is not { } machine)
        {
            return;
        }

        // "gunman" is one of the words for "oswald", and is turned into it before the patterns
        // are matched, so the patterns with "gunman" and "gunmen" in them never match: Powers
        // answers as when nothing does
        Say(machine, "a gunman on the grassy knoll");
        Assert.Contains("You have indicated that John's life is in danger.", Screen(machine));
    }

    [Fact]
    public void Three_lines_convince_Kennedy()
    {
        if (OnAirForceOne() is not { } machine)
        {
            return;
        }

        Say(machine, "i am from the future");
        Assert.Contains("Convince me you are from the future.", Screen(machine));

        // three points: something he said that only Powers, O'Donnell and he heard
        Say(machine, "westward look the land is bright");
        Assert.Contains("Only Dave, Ken, and I could", Screen(machine));
        Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-westward.png"));

        // and three more, six: he turns back for Washington, and the ending goes on to the
        // message from the first Adam Cooper
        Say(machine, "krushchev letter knot of war");
        Assert.Contains("You are stunned and you should be.", Screen(machine));
    }

    // the ending, given the token the game leaves when you've won your way onto the plane (the
    // sound off, and none of your agents left to find), at Kennedy's first question
    private Machine? OnAirForceOne()
    {
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return null;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        byte[] left = [.. Token.Select(c => (byte)c), 0, 0];
        for (int i = 0; i < left.Length; i++)
        {
            machine.Write(0x4F0 + (uint)i, left[i]);
        }

        machine.Load("RUND.EXE", "Z k0");
        machine.Run(machine.InstructionsPerSecond * 20);
        Assert.Contains("Dave Powers thought it extremely strange", Screen(machine));
        Press(machine, 0x1C);
        machine.Run(machine.InstructionsPerSecond * 10);
        return machine;
    }

    // the week, as the agents ask it: each prompt the game shows, and what's typed to it.
    // "@ship:" waits for an agent to be back at the timeship; an empty answer only waits.
    private static readonly (string When, string Type)[] Week =
    [
        // Friday: Steel robs a bank in Fort Worth for the team's money
        ("@ship:Steel", "steel, steal money from a bank in fort worth"),
        ("What do you want me to steal?", "money"),
        ("go to get the money?", "bank"),
        ("When would you like me to be there?", "now"),
        ("steal a bank's money as soon as", "yes"),
        ("I've got over $51,000", ""),

        // Saturday: and the State Department for credentials, the secretaries ask for them
        ("@ship:Steel", "steel, steal credentials"),
        ("Where should I", "state department"),
        ("What city would you like me to go to?", "washington"),
        ("When would you like me to be there?", "now"),
        ("steal credentials as soon as", "yes"),
        ("five sets of State", ""),

        // Sunday: Bridgette to Carl Kaysen; Stein to Arthur Sylvester, which lets Czernak's
        // name open Gilpatric's door; Czernak to Roswell Gilpatric; Blue to Nettie Carlson
        ("@ship:Steel", "bridgette, see carl kaysen"),
        ("What's he do that would make", "he is special assistant to the deputy national security adviser"),
        ("Should I tell him I'm from the", "no"),
        ("What city would you like me to go to?", "washington"),
        ("Where in Washington, DC do you want me to go?", "workplace"),
        ("When would you like me to be there?", "tuesday at 9 am"),
        ("see Carl Kaysen at his", "yes"),
        ("@ship:Stein", "stein, see arthur sylvester"),
        ("What does he do in", "assistant secretary of defense for public affairs"),
        ("tell this fellow I'm from the", "no"),
        ("What city would you like me to go to?", "washington"),
        ("Where in Washington, DC do you want me to go?", "home"),
        ("When would you like me to be there?", "monday at 7 am"),
        ("see Arthur Sylvester at his", "yes"),
        ("@ship:Czernak", "czernak, see roswell gilpatric"),
        ("think I should tell him I'm from the future?", "no"),
        ("What city would you like me to go to?", "washington"),
        ("Where in Washington, DC do you want me to go?", "home"),
        ("When would you like me to be there?", "monday at 8 pm"),
        ("see Roswell Gilpatric at his", "yes"),
        ("@ship:Blue", "blue, see nettie carlson"),
        ("Who in the hell is that?", "she is evelyn lincoln's aunt"),
        ("Why is some secretary's aunt", "she will meet president kennedy in the oval office on wednesday november 20"),
        ("What city would you like me to go to?", "washington"),
        ("Where in Washington, DC do you want me to go?", "hotel"),
        ("When would you like me to be there?", "tuesday at 3 pm"),
        ("Should I tell her I'm from 2087?", "no"),
        ("What else can I tell Nettie Carlson", "the president is in danger in dallas"),
        ("see Nettie Carlson at her", "yes"),

        // Monday: Gilpatric sends Czernak on to Adam Yarmolinsky, who's home that night
        ("someone else in the Defense Department?", "no"),
        ("Should I try to meet", "yes"),
        ("Should I tell Yarmolinsky I'm from the future?", "no"),
        ("When would you like me to be there?", "monday at 11 pm"),
        ("see Adam Yarmolinsky at his", "yes"),

        // Tuesday: on to John McNaughton, and this time from the future
        ("Should I meet", "yes, and tell him you are from the future"),
        ("What should I say to McNaughton", "tell him about the cuban missile crisis"),

        // Kaysen sends Bridgette to William Sullivan, but has the FBI waiting for her there;
        // Steel holds the agents in their car
        ("should risk seeing one of them?", "yes"),
        ("Should I see Sullivan, then?", "yes"),
        ("No sense pushing my luck.", "no"),
        ("What is your plan for stopping those FBI agents", "steel will stop the fbi agents"),
        ("When would you like me to be there?", "wednesday at 1 pm"),
        ("see William Sullivan at his", "yes"),
        ("@ship:Steel", "steel, stop the fbi agents"),
        ("Can I use lethal force if necessary?", "no"),
        ("non-violently subdue the agents?", "yes"),
        ("What's the location for their scenario?", "sullivan's office"),
        ("When would you like me to be there?", "wednesday at 10 am"),

        // Nettie Carlson sends Blue to Abe Lincoln, Evelyn's husband, on Thursday
        ("should I see Abe", "yes"),
        ("tell Abe Lincoln I'm from the future", "yes"),
        ("What evidence could I present", "evelyn lincoln will locate the rough drafts of profiles in courage at midnight on december 7 1957 for senator kennedy and his book"),

        // Wednesday: Sullivan sends Bridgette on to Theodore Sorensen
        ("Should I attend the", "yes"),
        ("should I tell him I'm from the future?", "yes"),
        ("What can I say to convince Sorensen", "tell him about the cuban missile crisis and the bay of pigs"),

        // McNaughton gets Czernak a meeting with Robert McNamara
        ("confront McNamara and hopefully", "yes"),
        ("should I say to convince the Defense Secretary", "draw the penta axial hypergraph and explain why a nuclear first strike in europe is immoral"),

        // Thursday night, once Abe Lincoln has spoken to his wife: Stein to Evelyn Lincoln, who's
        // in Fort Worth with the President on Friday morning
        ("Adam, great news.  I met with Abe Lincoln", ""),
        ("@ship:Stein", "stein, see evelyn lincoln"),
        ("Did someone get", "yes"),
        ("What city would you like me to go to?", "fort worth"),
        ("Where in Fort Worth do you want me to go?", "hotel"),
        ("When would you like me to be there?", "friday at 7 am"),
        ("Should I tell Evelyn Lincoln I'm from the future?", "yes"),
        ("What very personal information", "on thursday evening november 14 caroline kennedy recited the nursery rhyme star light star bright in the oval office, and danger"),
        ("see Evelyn Lincoln at her Fort", "yes"),
    ];

    [Fact]
    public void Three_warnings_to_Dave_Powers_win_the_week()
    {
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.Load("KENNEDY.BAT");
        machine.Run(machine.InstructionsPerSecond * 30);

        // the computer stabilizes bio-stasis levels a while, then the agents' windows open
        for (int key = 0; key < 10 && !Screen(machine).Contains("Blue          Stein", StringComparison.Ordinal); key++)
        {
            Press(machine, 0x39);
            machine.Run(machine.InstructionsPerSecond * 20);
        }

        var seen = new List<string>();
        int step = 0;
        try
        {
            Play(machine, seen, ref step);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(Shots, "kennedy-week.log"), seen.Append($"--- step {step} of {Week.Length}").Concat(_log));
        }

        Assert.Equal(Week.Length, step);

        // the game left its token and exited with code 2, the batch file ran the ending, and
        // the ending found the token: Air Force One, with nothing of the test's own help
        Assert.Contains("exit 2", _log);
        Assert.Contains("Dave Powers thought it extremely strange", Screen(machine));
        Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-won.png"));
    }

    // plays the week: answers each prompt of it as it comes, sends agents done with a mission
    // home, and otherwise waits for something to happen
    private static void Play(Machine machine, List<string> seen, ref int step)
    {
        for (int turn = 0; turn < 3000 && !Screen(machine).Contains("Dave Powers thought", StringComparison.Ordinal); turn++)
        {
            // the screen as text, its box drawing as spaces
            string[] rows = [.. Screen(machine).Split('\n').Select(row => new string([.. row.Select(c => c is < ' ' or > '~' ? ' ' : c)]))];
            string top = string.Join('\n', rows[..10]);
            if (seen.Count == 0 || seen[^1] != top)
            {
                seen.Add(top);
            }

            // an agent done with a mission keeps calling for orders: back to the ship
            if (rows.Any(row => row.Contains("What do you want me to do now?", StringComparison.Ordinal)))
            {
                Type(machine, "return to the ship");
                Answer(machine, "When would you like me to be there?", "now");
                Answer(machine, "return to you at the ship", "yes");
                continue;
            }

            // the next prompt, or one a little further on, when the game doesn't need to ask
            // what an order has already said
            int next = -1;
            for (int n = step; n < Math.Min(step + 3, Week.Length); n++)
            {
                if (Showing(rows, Week[n].When))
                {
                    next = n;
                    break;
                }

                // only a question can be skipped, not a wait
                if (Week[n].Type.Length == 0 || Week[n].When.StartsWith("@ship:", StringComparison.Ordinal))
                {
                    break;
                }
            }
            if (next >= 0)
            {
                if (Week[next].Type.Length > 0)
                {
                    Type(machine, Week[next].Type);
                    seen.Add($">>> {Week[next].Type}\n{string.Join('\n', rows)}");
                }

                step = next + 1;
                machine.Run(machine.InstructionsPerSecond * 8);
                continue;
            }

            // Steel holds the FBI agents to the end
            if (rows.Any(row => row.Contains("let those FBI agents go", StringComparison.Ordinal)) && rows.Any(row => row.TrimEnd().EndsWith('?')))
            {
                Type(machine, "no");
                machine.Run(machine.InstructionsPerSecond * 2);
                continue;
            }

            // a message waiting for a key, or nothing happening: wait for something to
            string before = Below(machine);
            Press(machine, 0x39);
            machine.Run(machine.InstructionsPerSecond * 2);
            if (Below(machine) == before)
            {
                machine.PressKey(0x1D);
                Press(machine, 0x11);
                machine.ReleaseKey(0x1D);
                machine.Run(machine.InstructionsPerSecond * 4);
            }
        }

    }

    // whether the screen shows a prompt, or "@ship:" an agent at the ship (nothing in the
    // status line under their name)
    private static bool Showing(string[] rows, string when)
    {
        if (!when.StartsWith("@ship:", StringComparison.Ordinal))
        {
            // on a line, or wrapped across lines
            string text = string.Join(' ', rows.Select(row => string.Join(' ', row.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
            return rows.Any(row => row.Contains(when, StringComparison.Ordinal))
                || text.Contains(string.Join(' ', when.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);
        }

        string agent = when[6..];
        int names = Array.FindIndex(rows, row => row.Contains("Steel", StringComparison.Ordinal) && row.Contains("Czernak", StringComparison.Ordinal));
        if (names < 0 || names + 1 >= rows.Length)
        {
            return false;
        }

        int column = rows[names].IndexOf(agent, StringComparison.Ordinal);
        bool away = rows[(names + 1)..Math.Min(names + 3, rows.Length)].Any(row =>
            !string.IsNullOrWhiteSpace(row[Math.Max(0, column - 3)..Math.Min(row.Length, column + agent.Length + 3)]));
        // and no agent's line open, which would take the order as an answer
        return !away && rows.Any(row => row.TrimStart().StartsWith('>')) && !rows.Any(row => row.Contains("COMM:", StringComparison.Ordinal));
    }

    // the screen but its top line, whose ship status runs as a ticker
    private static string Below(Machine machine) => Screen(machine)[81..];

    // waits for a prompt, a while, and answers it
    private static void Answer(Machine machine, string when, string type)
    {
        for (int tries = 0; tries < 10; tries++)
        {
            machine.Run(machine.InstructionsPerSecond * 2);
            if (Screen(machine).Contains(when, StringComparison.Ordinal))
            {
                Type(machine, type);
                return;
            }
        }
    }

    private static readonly byte[] Letters =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    ];

    // types what you'd type in the dialog, and Enter at the end ('|' alone is Enter)
    private static void Type(Machine machine, string text)
    {
        foreach (char c in text + "|")
        {
            byte code = c switch
            {
                '|' => 0x1C,
                ' ' => 0x39,
                '\'' => 0x28,
                ',' => 0x33,
                '-' => 0x0C,
                >= 'a' and <= 'z' => Letters[c - 'a'],
                >= '1' and <= '9' => (byte)(c - '1' + 2),
                '0' => 0x0B,
                _ => throw new ArgumentException($"no key for '{c}'"),
            };
            Press(machine, code);
            machine.Run(machine.InstructionsPerSecond / 10);
        }
    }

    // says a line to Kennedy, then reads what's said back: a key after each speaker's line, until
    // the prompt is back
    private static void Say(Machine machine, string line)
    {
        Type(machine, line);
        for (int keys = 0; keys < 10; keys++)
        {
            machine.Run(machine.InstructionsPerSecond * 4);
            if (Prompting(machine))
            {
                return;
            }

            Press(machine, 0x39);
        }
    }

    // the prompt, alone, on the last line of your window
    private static bool Prompting(Machine machine) =>
        Screen(machine).Split('\n')[22].StartsWith(" \u00B3>   ", StringComparison.Ordinal);

    private static void Press(Machine machine, byte scanCode)
    {
        machine.PressKey(scanCode);
        machine.Run(machine.InstructionsPerSecond / 20);
        machine.ReleaseKey(scanCode);
    }

    private static string Screen(Machine machine) => string.Join('\n', Enumerable.Range(0, 25).Select(row => new string(
        Enumerable.Range(0, 80).Select(column => (char)machine.Read(0xB_8000 + (uint)((row * 160) + (column * 2)))).ToArray())));
}
