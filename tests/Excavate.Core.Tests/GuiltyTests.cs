namespace Excavate.Core.Tests;

/// <summary>
/// <i>Presumed Guilty</i> (Cosmi, 1989), from the player's copy, Celery Software's release of
/// 1993: its setup, and the game on the EGA, from its title to its logoff. The pictures go to
/// <c>screenshots</c> beside the tests. Without the game (in EXCAVATE_GAMES, or
/// _excavating/presguil/presguil above the tests), these pass without checking.
/// </summary>
public sealed class GuiltyTests : IDisposable
{
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    private static string Shots
    {
        get
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>A program of the game's, loaded at an 8088's pace and run for its first seconds.</summary>
    private Machine? Start(string program)
    {
        if (TestData.Game("presguil/presguil") is not { } folder)
        {
            return null;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.Load(program);
        machine.Run(machine.InstructionsPerSecond * 5);
        return machine;
    }

    // the scan codes of A to Z
    private static readonly byte[] Letters =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    ];

    /// <summary>
    /// Types keys as a person would, then waits for the game to answer. Enter is '|', the down
    /// arrow 'v', and F10 '!'; letters are typed with Shift held, as capitals.
    /// </summary>
    private static void Type(Machine machine, string text)
    {
        foreach (char c in text)
        {
            byte code = c switch
            {
                '|' => 0x1C,
                ' ' => 0x39,
                'v' => 0x50,
                '!' => 0x44,
                >= 'A' and <= 'Z' => Letters[c - 'A'],
                >= '1' and <= '9' => (byte)(c - '1' + 2),
                _ => throw new ArgumentException($"no key for '{c}'"),
            };
            bool shift = c is >= 'A' and <= 'Z';
            if (shift)
            {
                machine.PressKey(0x2A);
            }

            machine.PressKey(code);
            machine.Run(machine.InstructionsPerSecond / 20);
            machine.ReleaseKey(code);
            if (shift)
            {
                machine.ReleaseKey(0x2A);
            }

            machine.Run(machine.InstructionsPerSecond * 3);
        }
    }

    // the text screen, row by row
    private static string Screen(Machine machine)
        => string.Join('\n', Enumerable.Range(0, 25).Select(row => new string(
            Enumerable.Range(0, 80).Select(column => (char)machine.Read(0xB_8000 + (uint)((row * 160) + (column * 2)))).ToArray())));

    [Fact]
    public void Setup_asks_only_your_name_and_tells_you_the_passphrase()
    {
        if (Start("SU.EXE") is not { } machine)
        {
            return;
        }

        Assert.Contains("Memorize your passphrase. It is *IT*RAINS*IN*SPAIN!*", Screen(machine));
        Assert.Contains("Your last name", Screen(machine));

        // setup ends with the drive's letter as its exit code, the President's did; the profile
        // goes to the save folder
        Type(machine, "DOE|JANE|");
        Assert.True(machine.Exited);
        Assert.Equal('C', machine.ExitCode);
        Assert.True(File.Exists(Path.Combine(_saves, "SA")));
    }

    [Fact]
    public void The_game_goes_from_its_title_on_the_EGA_to_its_menu_and_logs_off()
    {
        if (Start("PGMAIN.EXE") is not { } machine)
        {
            return;
        }

        // the EGA, and the title with its credits
        Type(machine, "2");
        Assert.Equal(0x10, machine.VideoMode);
        Assert.Contains("21/3D open PGTITLE .PCC (read): handle 6, 6316 bytes", _log);
        Screenshot.WritePng(machine, Path.Combine(Shots, "guilty-title.png"));

        // past them, no logon: COPNET's main menu, on the story's date
        Type(machine, "   ");
        Assert.Contains("COPNET COMMUNICATIONS", Screen(machine));
        Assert.Contains("JUNE 24, 1996", Screen(machine));

        // logged off, the case is saved: its status, the profile, and the case file
        Type(machine, "vvvv!Y");
        Assert.True(machine.Exited);
        Assert.Contains("USER LOGGED OFF", Screen(machine));
        Assert.All(["STATUS.TBL", "SA", "CF.FIL"], file => Assert.True(File.Exists(Path.Combine(_saves, file)), file));
    }
}
