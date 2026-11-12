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
        if (TestData.Game("guardken/guardken") is not { } folder)
        {
            return;
        }

        // what the game leaves when it ends with you on the plane: the token, the sound off,
        // and none of your agents left to find
        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 300_000 };
        byte[] left = [.. Token.Select(c => (byte)c), 0, 0];
        for (int i = 0; i < left.Length; i++)
        {
            machine.Write(0x4F0 + (uint)i, left[i]);
        }

        machine.Load("RUND.EXE", "Z k0");
        machine.Run(machine.InstructionsPerSecond * 20);
        Assert.Contains("Dave Powers thought it extremely strange", Screen(machine));

        // and it clears the area, so it's good once
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(0, machine.Read(0x4F0 + (uint)i)));

        Press(machine, 0x1C);
        machine.Run(machine.InstructionsPerSecond * 10);
        string screen = Screen(machine);
        Assert.Contains("Time Remaining Until Flight Reaches Dallas: 15 Minutes", screen);
        Assert.Contains("Well Mr. Cooper, you've convinced", screen);
        Screenshot.WritePng(machine, Path.Combine(Shots, "kennedy-air-force-one.png"));
    }

    private static void Press(Machine machine, byte scanCode)
    {
        machine.PressKey(scanCode);
        machine.Run(machine.InstructionsPerSecond / 20);
        machine.ReleaseKey(scanCode);
    }

    private static string Screen(Machine machine) => string.Join('\n', Enumerable.Range(0, 25).Select(row => new string(
        Enumerable.Range(0, 80).Select(column => (char)machine.Read(0xB_8000 + (uint)((row * 160) + (column * 2)))).ToArray())));
}
