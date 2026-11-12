namespace Excavate.Core.Tests;

/// <summary>
/// <i>Portal</i> (Activision, 1986), from the player's own disk images: the original program,
/// run from disk 1 on a CGA, with the protection track its dump lost put back. The pictures go
/// to <c>screenshots</c> beside the tests. Without the disks (in EXCAVATE_GAMES, or
/// _excavating/portal/portal above the tests), these pass without checking.
/// </summary>
public sealed partial class PortalTests : IDisposable
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

    /// <summary>The game, loaded from disk 1 at the speed the player gives it, with its protection track or without.</summary>
    private Machine? Start(bool protection = true)
    {
        if (TestData.Game("portal/portal") is not { } folder)
        {
            return null;
        }

        var disks = new[] { "DISK1.DAT", "DISK2.DAT", "DISK3.DAT" }.Select(name => Path.Combine(folder, name)).ToArray();
        var machine = new Machine(folder, _saves, VideoAdapter.Cga) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        machine.InsertFloppies(disks);
        if (protection)
        {
            machine.RestoreSectors(0, 9, 0, 10, 16);
        }

        machine.Load(@"A:\PORTAL.EXE");
        return machine;
    }

    // the scan codes of A to Z
    private static readonly byte[] Letters =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    ];

    /// <summary>
    /// Types keys a second apart, as a person would: the game empties the keyboard's buffer
    /// before each prompt, so keys typed while it draws are lost, as on the PC. Enter is '|',
    /// and the arrows are '^', 'v', '&lt;' and '&gt;'.
    /// </summary>
    private static void Type(Machine machine, string text)
    {
        foreach (char c in text)
        {
            byte code = c switch
            {
                '|' => 0x1C,
                ' ' => 0x39,
                '^' => 0x48,
                'v' => 0x50,
                '<' => 0x4B,
                '>' => 0x4D,
                >= 'A' and <= 'Z' => Letters[c - 'A'],
                _ => throw new ArgumentException($"no key for '{c}'"),
            };
            machine.PressKey(code);
            machine.Run(machine.InstructionsPerSecond / 20);
            machine.ReleaseKey(code);
            machine.Run(machine.InstructionsPerSecond);
        }
    }

    /// <summary>
    /// Gives a name at the login prompt, then says no joystick once the question is on the
    /// screen, and waits through the retina scan to Central Processing.
    /// </summary>
    private static void LogIn(Machine machine)
    {
        machine.Run(machine.InstructionsPerSecond * 20);
        Type(machine, "EXCAVATE|");
        machine.Run(machine.InstructionsPerSecond * 5);
        Type(machine, "N");
        machine.Run(machine.InstructionsPerSecond * 60);
    }

    private static int Colors(Machine machine) => Picture(machine).Distinct().Count();

    [Fact]
    public void It_finds_its_protection_track_and_asks_who_you_are()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        machine.Run(machine.InstructionsPerSecond * 20);
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-login.png"));

        // sectors 16 down to 1 of track 9 verified, sector 9 read for its key, and the key's
        // hash, less 859Ch, is the BIOS call for mode 4
        Assert.Contains("13/04 verify 1 at 9/0/16, disk 1: 1 done, status 00h", _log);
        Assert.Contains("13/02 read 1 at 9/0/9, disk 1: 1 done, status 00h", _log);
        Assert.Contains("10/00 set video mode 04h (CGA)", _log);
        Assert.Equal(4, machine.VideoMode);

        // the login screen: its own font from the BIOS's, in cyan, magenta, light gray and black
        Assert.Equal(4, Colors(machine));
        Assert.False(machine.Exited);
    }

    [Fact]
    public void Without_its_protection_track_it_never_sets_its_mode()
    {
        if (Start(protection: false) is not { } machine)
        {
            return;
        }

        machine.Run(machine.InstructionsPerSecond * 5);

        // the verify of sector 16 fails, the routine returns 0Ah, and the INT 10h it becomes is
        // nothing the BIOS does: the screen stays in text
        Assert.Contains("13/04 verify 1 at 9/0/16, disk 1: 0 done, status 04h", _log);
        Assert.DoesNotContain("10/00 set video mode 04h (CGA)", _log);
        Assert.Equal(3, machine.VideoMode);
    }

    [Fact]
    public void It_logs_in_and_reaches_central_processing()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        LogIn(machine);
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-central-processing.png"));
        machine.Composite = true;
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-central-processing-composite.png"));

        // the retina scan and Central Processing come from cylinders 20 to 37 of disk 1
        Assert.Contains("13/02 read 1 at 34/1/1, disk 1: 1 done, status 00h", _log);
        Assert.Contains("13/02 read 1 at 24/0/8, disk 1: 1 done, status 00h", _log);
        Assert.False(machine.Exited);

        // its stripes, which a composite monitor shows as colors of their own
        Assert.True(Colors(machine) > 16, $"only {Colors(machine)} colors");
    }

    /// <summary>The picture, to tell one screen from another.</summary>
    private static uint[] Picture(Machine machine)
    {
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        return pixels;
    }

    [Fact]
    public void Its_arrow_keys_move_the_highlight_and_space_chooses()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        LogIn(machine);

        // Space goes into Central Processing, and Down then moves the highlight on, each
        // changing the picture: the joystick, chosen at the start, only sends these same keys
        var map = Picture(machine);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 5);
        var inside = Picture(machine);
        Assert.NotEqual(map, inside);
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-inside-central-processing.png"));

        Type(machine, "v");
        machine.Run(machine.InstructionsPerSecond * 5);
        Assert.NotEqual(inside, Picture(machine));
        Assert.False(machine.Exited);
    }

    [Fact]
    public void Its_help_button_is_the_choice_named_homer_messages()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // into Central Processing, down to the bottom row's first button, two to the right,
        // and chosen: Worldnet's help, from Homer
        LogIn(machine);
        foreach (char key in " vvvv>>")
        {
            // each key once the last has been drawn
            Type(machine, key.ToString());
            machine.Run(machine.InstructionsPerSecond * 4);
        }

        var highlighted = Picture(machine);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 8);
        Assert.NotEqual(highlighted, Picture(machine));
        Assert.False(machine.Exited);
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-help.png"));
    }

    /// <summary>
    /// Pushes the stick, or presses its button, until the game has read the port and a little
    /// longer, then lets go, and gives the game time to draw: as the player does for the mouse.
    /// </summary>
    private static void Stick(Machine machine, int axis, int direction, bool button = false)
    {
        long readings = machine.JoystickReadings;
        if (button)
        {
            machine.SetJoystickButton(0, true);
        }
        else
        {
            machine.SetJoystickAxis(axis, direction);
        }

        for (int wait = 0; wait < 100 && machine.JoystickReadings == readings; wait++)
        {
            machine.Run(machine.InstructionsPerSecond / 10);
        }

        machine.Run(machine.InstructionsPerSecond / 50);
        machine.SetJoystickButton(0, false);
        machine.SetJoystickAxis(axis, 0);
        machine.Run(machine.InstructionsPerSecond * 4);
    }

    [Fact]
    public void With_a_joystick_it_moves_by_the_stick_and_not_the_keys()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // a stick in the port, and yes to the question; its center is read then
        machine.Joysticks = 1;
        machine.Run(machine.InstructionsPerSecond * 20);
        Type(machine, "EXCAVATE|");
        machine.Run(machine.InstructionsPerSecond * 5);
        Type(machine, "Y");
        machine.Run(machine.InstructionsPerSecond * 60);

        // Space on the keyboard does nothing now
        var map = Picture(machine);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 4);
        Assert.Equal(map, Picture(machine));

        // the button goes into Central Processing, and the stick down moves the highlight
        Stick(machine, 0, 0, button: true);
        var inside = Picture(machine);
        Assert.NotEqual(map, inside);
        Stick(machine, 1, 1);
        Assert.NotEqual(inside, Picture(machine));
        Assert.False(machine.Exited);
    }

    /// <summary>
    /// Moves the story on to a level, as finishing the one before does: its number, at 213h in
    /// the game's state, and the flag at 215h that has it check, at the next dataspace, that
    /// the level's disk is in. Levels 2 and 3 are on disk 2, and 4 and 5 on disk 3.
    /// </summary>
    private static void ToLevel(Machine machine, int level)
    {
        uint state = (machine.ProgramSegment + 0x584u) << 4;
        machine.Write(state + 0x213, (byte)level);
        machine.Write(state + 0x215, 1);
    }

    [Fact]
    public void It_asks_for_the_disk_its_level_is_on()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        LogIn(machine);
        ToLevel(machine, 2);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 6);
        var prompt = Picture(machine);
        Screenshot.WritePng(machine, Path.Combine(Shots, "portal-disk-prompt.png"));

        // it read disk 1's label, found it isn't disk 2, and asks; a key with disk 1 still in
        // has it read the label again and ask again
        Assert.Contains("13/02 read 1 at 34/0/8, disk 1: 1 done, status 00h", _log);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 6);
        Assert.Equal(prompt, Picture(machine));

        // with disk 2 in, a key, and it carries on from disk 2
        int mark = _log.Count;
        machine.Floppy = 1;
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 6);
        Assert.Contains("13/02 read 1 at 34/0/8, disk 2: 1 done, status 00h", _log.Skip(mark));
        Assert.NotEqual(prompt, Picture(machine));
        Assert.False(machine.Exited);
    }

    [Theory]
    [InlineData(2, 2, "34/0/8")]
    [InlineData(4, 3, "36/1/8")]
    public void Its_prompts_for_a_disk_can_be_answered_for_the_player(int level, int disk, string label)
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // the two prompts, as the player knows them: the level's disk, by the digit it keeps
        // (made 4 for disk 3 once it's shown), and disk 1
        machine.AnswerDiskPrompt(0x10970, 0x11EFA, 0x2F67, "124");
        machine.AnswerDiskPrompt(0x10970, 0x1236B, 0);
        LogIn(machine);
        ToLevel(machine, level);
        Type(machine, " ");
        machine.Run(machine.InstructionsPerSecond * 6);

        // asked once, answered with the level's disk, and its label read from that: disk 2's
        // at 34/0/8, disk 3's at 36/1/8
        Assert.Single(_log, line => line.StartsWith("disk prompt", StringComparison.Ordinal));
        Assert.Contains($"disk prompt: disk {disk} put in drive A:, and Space pressed", _log);
        Assert.Contains($"13/02 read 1 at {label}, disk {disk}: 1 done, status 00h", _log);
        Assert.Equal(disk - 1, machine.Floppy);
        Assert.False(machine.Exited);
    }
}
