namespace Excavate.Core.Tests;

/// <summary>
/// <i>Portal</i> (Activision, 1986), from the player's own disk images: the original program,
/// run from disk 1 on a CGA, with the protection track its dump lost put back. The pictures go
/// to <c>screenshots</c> beside the tests. Without the disks (in EXCAVATE_GAMES, or
/// _excavating/portal/portal above the tests), these pass without checking.
/// </summary>
public sealed class PortalTests : IDisposable
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
        var machine = new Machine(folder, _saves, VideoAdapter.Cga) { Log = _log.Add, InstructionsPerSecond = 600_000 };
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
    /// before each prompt, so keys typed while it draws are lost, as on the PC. Enter is '|'.
    /// </summary>
    private static void Type(Machine machine, string text)
    {
        foreach (char c in text)
        {
            byte code = c switch
            {
                '|' => 0x1C,
                ' ' => 0x39,
                >= 'A' and <= 'Z' => Letters[c - 'A'],
                _ => throw new ArgumentException($"no key for '{c}'"),
            };
            machine.PressKey(code);
            machine.Run(machine.InstructionsPerSecond / 20);
            machine.ReleaseKey(code);
            machine.Run(machine.InstructionsPerSecond);
        }
    }

    private static int Colors(Machine machine)
    {
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        return pixels.Distinct().Count();
    }

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

        machine.Run(machine.InstructionsPerSecond * 20);

        // a name, Enter, and no joystick
        Type(machine, "EXCAVATE|N");
        machine.Run(machine.InstructionsPerSecond * 60);
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
}
