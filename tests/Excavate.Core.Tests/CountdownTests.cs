namespace Excavate.Core.Tests;

/// <summary>
/// <i>Countdown</i> (Access Software, 1990), from the player's copy: its setup questions, its
/// introduction in the VGA's 256 colors with the scream it plays on the PC speaker by RealSound,
/// and its first room, the cell Mason wakes up in. The pictures go to <c>screenshots</c> beside
/// the tests. Without the game (in EXCAVATE_GAMES, or _excavating/countdwn/countdwn/count above
/// the tests), these pass without checking.
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
}
