namespace Excavate.Core.Tests;

/// <summary>
/// <i>Countdown</i> (Access Software, 1990), from the player's copy: its setup questions, its
/// introduction in the VGA's 256 colors with the scream it plays on the PC speaker by RealSound,
/// its first room, the cell Mason wakes up in, and his way out of it, by the mouse and the keys.
/// The pictures go to <c>screenshots</c> beside the tests. Without the game (in EXCAVATE_GAMES,
/// or _excavating/countdwn/countdwn/count above the tests), these pass without checking.
/// </summary>
public sealed class CountdownTests : IDisposable
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

    // the scan codes of the keys the start takes: 3 for the keyboard, 1 for RealSound, and Esc
    private const byte Three = 0x04, One = 0x02, Escape = 0x01;

    /// <summary>
    /// The game started at an AT's pace, from midnight, through its two questions: the
    /// keyboard to play with, and RealSound for the sound.
    /// </summary>
    private Machine? Start(List<short>? sound = null)
    {
        if (TestData.Game("countdwn/countdwn/count") is not { } folder)
        {
            return null;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 3_000_000, TickCount = 0 };
        machine.Load("COUNT.EXE");
        Run(machine, 3, sound);
        Press(machine, Three, sound);
        Press(machine, One, sound);
        return machine;
    }

    /// <summary>Presses a key and lets it go, then waits two seconds.</summary>
    private static void Press(Machine machine, byte code, List<short>? sound)
    {
        machine.PressKey(code);
        Run(machine, 0.1, sound);
        machine.ReleaseKey(code);
        Run(machine, 2, sound);
    }

    /// <summary>Runs for some seconds, keeping the sound if asked to.</summary>
    private static void Run(Machine machine, double seconds, List<short>? sound)
    {
        var buffer = new short[16384];
        for (double t = 0; t < seconds; t += 0.05)
        {
            machine.Run(machine.InstructionsPerSecond / 20);
            int count;
            while ((count = machine.ReadSamples(buffer)) > 0)
            {
                sound?.AddRange(buffer.AsSpan(0, count).ToArray());
            }
        }
    }

    [Fact]
    public void Its_introduction_screams_by_RealSound_on_the_speaker()
    {
        var sound = new List<short>();
        if (Start(sound) is not { } machine)
        {
            return;
        }

        Run(machine, 3, sound);
        Assert.Contains("10/00 set video mode 13h", _log);
        Assert.Contains(_log, line => line.StartsWith("21/3D open SND1.CNT", StringComparison.Ordinal));
        Screenshot.WritePng(machine, Path.Combine(Shots, "countdown-intro.png"));

        // RealSound reloads the speaker's counter 16,572 times a second, each count a sample:
        // what comes out must be the scream it carries, not that carrier. An average over 4
        // samples keeps what's below a few kHz and takes most of what's near 16.5 kHz out, so
        // the sound is mostly what the average keeps: the speaker as it was before it was made
        // to count each pulse whole kept about as much as it lost.
        double[] left = sound.Where((_, i) => i % 2 == 0).Select(sample => (double)sample).ToArray();
        // the loudest second, which is the scream
        int tenth = Machine.SampleRate / 10, start = 0;
        double loudest = 0;
        for (int i = 0; i + Machine.SampleRate <= left.Length; i += tenth)
        {
            double power = left.AsSpan(i, Machine.SampleRate).ToArray().Sum(sample => sample * sample);
            if (power > loudest)
            {
                (loudest, start) = (power, i);
            }
        }

        Assert.True(loudest > 0, "There's no sound.");
        double kept = 0, rest = 0;
        for (int i = start + 4; i < Math.Min(left.Length, start + Machine.SampleRate); i++)
        {
            double average = left.AsSpan(i - 3, 4).ToArray().Average();
            kept += average * average;
            rest += (left[i] - average) * (left[i] - average);
        }

        Assert.True(kept > 3 * rest, $"The carrier is too strong: {kept:E2} kept against {rest:E2} left over.");
    }

    [Fact]
    public void It_starts_in_Masons_cell()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // Esc skips the introduction: its text, its title and its credits
        for (int i = 0; i < 3; i++)
        {
            Press(machine, Escape, null);
        }

        Run(machine, 5, null);
        Assert.Contains(_log, line => line.StartsWith("21/3D open MASONCEL.CNT", StringComparison.Ordinal));
        Assert.False(machine.Exited);
        Screenshot.WritePng(machine, Path.Combine(Shots, "countdown-cell.png"));
    }

    [Fact]
    public void Mason_gets_out_of_his_cell_by_the_window()
    {
        if (TestData.Game("countdwn/countdwn/count") is not { } folder)
        {
            return;
        }

        // with the mouse, which the game asks for first, then RealSound, then Esc through the
        // introduction
        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 3_000_000, TickCount = 0, Mouse = true };
        machine.Load("COUNT.EXE");
        Run(machine, 3, null);
        Press(machine, One, null);
        Press(machine, One, null);
        for (int i = 0; i < 6; i++)
        {
            Press(machine, Escape, null);
        }

        Run(machine, 6, null);

        // the cup, by the sink: around the foot of the bed, then GET CUP
        Click(machine, 140, 130, 3);
        Click(machine, 100, 131, 3);
        Command(machine, Get, (82, 128));
        Run(machine, 4, null);

        // USE CUP ON BUG, the beetle on the wall by the door; the message waits for a click
        Command(machine, Use, Inventory, (228, 82));
        Run(machine, 6, null);
        Click(machine, 160, 185, 1);

        // TALK to the guard, when his face is at the door's window
        Click(machine, Talk.X, Talk.Y, 0);
        Assert.True(WaitFor(machine, 196, 58, 220, 300), "The guard never looked in.");
        Click(machine, 192, 50, 0);
        Click(machine, 192, 50, 3);
        Assert.Contains(_log, line => line.StartsWith("21/3D open GUARD.CNT", StringComparison.Ordinal));

        // OFFER him the bug: he's been looking everywhere for a rhinoceros beetle, and sends food;
        // then LEAVE, and the tray comes under the door with a knife under it
        Click(machine, 262, 140, 1);
        Click(machine, Inventory.X, Inventory.Y, 6);
        Click(machine, 160, 180, 2);
        Click(machine, 300, 141, 30);
        Command(machine, Get, (206, 109));
        Run(machine, 5, null);

        // the knife works on the window's screen only from close by, a little out from the back
        // wall (the code wants Mason at 180 across or more, 44 to 64 down); the mouse walks one
        // way and then the other, and stops at the first thing in the way, so the keys: up to the
        // back wall, a little down, and right as far as he goes
        Click(machine, 160, 185, 0);
        Hold(machine, Up, 2);
        Hold(machine, Down, 0.6);
        Hold(machine, Right, 2.5);
        Command(machine, Use, Inventory, (257, 60));
        Run(machine, 12, null);
        Click(machine, 160, 185, 2);
        Click(machine, 160, 185, 2);
        Click(machine, 160, 185, 2);

        // the locker key at the foot of the bed, then out through the window to the ledge
        Command(machine, Get, (115, 117));
        Run(machine, 6, null);
        Click(machine, 160, 185, 1);
        Command(machine, GoTo, (257, 60));
        Run(machine, 8, null);
        Assert.Contains(_log, line => line.StartsWith("21/3D open LEDGE.CNT", StringComparison.Ordinal));
        Assert.False(machine.Exited);
        Screenshot.WritePng(machine, Path.Combine(Shots, "countdown-ledge.png"));
    }

    // the actions on the bar under the room, and the first thing in the inventory
    private static readonly (int X, int Y) Get = (122, 140), Use = (157, 140), GoTo = (192, 140), Talk = (227, 140), Inventory = (8, 149);

    // the scan codes of the arrow keys that walk Mason
    private const byte Up = 0x48, Down = 0x50, Right = 0x4D;

    /// <summary>
    /// An action, then what it acts on, clicked twice: once to put it on the command line and
    /// once to carry it out.
    /// </summary>
    private static void Command(Machine machine, (int X, int Y) action, params (int X, int Y)[] things)
    {
        Click(machine, action.X, action.Y, 1);
        for (int i = 0; i < things.Length; i++)
        {
            Click(machine, things[i].X, things[i].Y, 0);
        }

        var last = things[^1];
        Click(machine, last.X, last.Y, 0);
    }

    /// <summary>
    /// Clicks the left button at a place on the 320 by 200 screen, then waits some seconds. The
    /// mouse goes to the top left corner first, which holds it, and from there across.
    /// </summary>
    private static void Click(Machine machine, int x, int y, double seconds)
    {
        machine.MoveMouse(-20000, -20000);
        machine.Run(machine.InstructionsPerSecond / 50);
        var across = machine.MickeysAcross;
        machine.MoveMouse(x * across.X / 320, y * across.Y / 200);
        machine.Run(machine.InstructionsPerSecond / 10);
        machine.SetMouseButton(0, true);
        machine.Run(machine.InstructionsPerSecond / 10);
        machine.SetMouseButton(0, false);
        machine.Run(machine.InstructionsPerSecond);
        Run(machine, seconds, null);
    }

    /// <summary>Holds a key down for some seconds.</summary>
    private static void Hold(Machine machine, byte code, double seconds)
    {
        machine.PressKey(code);
        machine.Run((long)(machine.InstructionsPerSecond * seconds));
        machine.ReleaseKey(code);
        machine.Run(machine.InstructionsPerSecond / 2);
    }

    /// <summary>Waits, up to some seconds, until a place on the screen is brighter than a level.</summary>
    private static bool WaitFor(Machine machine, int x, int y, int level, int seconds)
    {
        var screen = new uint[320 * 200];
        for (int tenth = 0; tenth < seconds * 10; tenth++)
        {
            machine.Run(machine.InstructionsPerSecond / 10);
            machine.Render(screen);
            uint color = screen[(y * 320) + x];
            if ((((color >> 16) & 255) + ((color >> 8) & 255) + (color & 255)) / 3 > level)
            {
                return true;
            }
        }

        return false;
    }
}
