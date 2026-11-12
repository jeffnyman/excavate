namespace Excavate.Core.Tests;

/// <summary>
/// <i>The President Is Missing</i> (Cosmi, 1988), from the player's copy: its setup, with the
/// bytes a crack changed put back, and the game on the EGA. The pictures go to
/// <c>screenshots</c> beside the tests. Without the game (in EXCAVATE_GAMES, or
/// _excavating/presmissing/presmis above the tests), these pass without checking.
/// </summary>
public sealed class PresidentTests : IDisposable
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

    /// <summary>A program of the game's, loaded at an 8088's pace, with setup's two bytes put back or left cracked.</summary>
    private Machine? Start(string program, bool restore = true)
    {
        if (TestData.Game("presmissing/presmis") is not { } folder)
        {
            return null;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        if (restore)
        {
            machine.RestoreProgramBytes("SU.EXE", 0xAB9, [0x90, 0x90], [0x75, 0x03]);
        }

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
                '.' => 0x34,
                'v' => 0x50,
                '!' => 0x44,
                >= 'A' and <= 'Z' => Letters[c - 'A'],
                >= '1' and <= '9' => (byte)(c - '1' + 2),
                '0' => 0x0B,
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

            machine.Run(machine.InstructionsPerSecond / 5);
        }

        machine.Run(machine.InstructionsPerSecond * 3);
    }

    // the text screen, row by row
    private static string Screen(Machine machine)
        => string.Join('\n', Enumerable.Range(0, 25).Select(row => new string(
            Enumerable.Range(0, 80).Select(column => (char)machine.Read(0xB_8000 + (uint)((row * 160) + (column * 2)))).ToArray())));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Setup_takes_only_the_supervisor_it_shipped_with_unless_cracked(bool restore)
    {
        if (Start("SU.EXE", restore) is not { } machine)
        {
            return;
        }

        // a hard disk, drive C:, a code name, and the supervisor's name of eXoDOS's profile
        Type(machine, "H|C|AGENT|SCIN|");

        Assert.Contains(restore ? "Supervisor's name" : "Your last name", Screen(machine));
        Assert.Equal(restore, _log.Contains("SU.EXE: 2 bytes at AB9h put back as they shipped"));
    }

    [Fact]
    public void Setup_makes_a_profile_the_game_logs_on_with()
    {
        if (Start("SU.EXE") is not { } machine)
        {
            return;
        }

        Type(machine, "H|C|AGENT|SCIN|WILLIAM H. SULLENS|DOE|JANE|010255|SMITH|ELVIS|");

        // setup ends with the drive's letter as its exit code, for SETUP.BAT; the profile goes
        // to the save folder, the game's own left as it was
        Assert.True(machine.Exited);
        Assert.Equal('C', machine.ExitCode);
        Assert.StartsWith("H\0C\0AGENT\0WILLIAM H. SULLENS\0DOE\0JANE\0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_saves, "SA"))));

        // the game, which asks only for the code name and the supervisor's
        var game = Start("PRES.EXE")!;
        Type(game, "1AGENT|WILLIAM H. SULLENS| ");
        Assert.Contains("SPECIAL INVESTIGATIONS SYSTEM - BASE", Screen(game));
    }

    [Fact]
    public void The_game_runs_on_its_own_clock_and_puts_the_real_one_back()
    {
        if (Start("PRES.EXE") is not { } machine)
        {
            return;
        }

        // logged on, the PC's date is the story's, from the end of the profile
        Type(machine, "2UNDERDOGS|SCIN| ");
        Assert.Contains("JUN 22,1996", Screen(machine));

        // logged off, it's back to the real one, and the story's clock is saved in the profile:
        // after the answers and a '~', the date (DX, then CX for the year) and the time
        Type(machine, "vvvv!Y");
        Assert.True(machine.Exited);
        Assert.Contains($"21/2B date set to {DateTime.Today:yyyy-MM-dd}", _log);
        var profile = File.ReadAllBytes(Path.Combine(_saves, "SA"));
        int clock = Array.IndexOf(profile, (byte)'~') + 1;
        Assert.Equal([0x16, 0x06, 0xCC, 0x07], profile[clock..(clock + 4)]);
    }

    [Fact]
    public void The_EGA_shows_a_photo_at_640_by_350()
    {
        if (Start("PRES.EXE") is not { } machine)
        {
            return;
        }

        // the EGA, eXoDOS's profile, past the credits; then the data base, its photographs,
        // and the first of them
        Type(machine, "2UNDERDOGS|SCIN| v!v!!");

        Assert.Equal(0x10, machine.VideoMode);
        Assert.Equal((640, 350), (machine.DisplayWidth, machine.DisplayHeight));
        Assert.Contains("21/3D open PHOTO00 .PCC (read): handle 5, 22007 bytes", _log);
        Screenshot.WritePng(machine, Path.Combine(Shots, "president-photo.png"));
    }
}
