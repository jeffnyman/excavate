namespace Excavate.Core.Tests;

/// <summary>
/// <i>The Omnicron Conspiracy</i> (First Star Software, published by Epyx, 1989), from the
/// player's copy: its loader, which runs the game; the game, which asks for the display and runs
/// its music player for the title; and the start, on the MCGA and the EGA. The pictures go to
/// <c>screenshots</c> beside the tests. Without the game (in EXCAVATE_GAMES, or
/// _excavating/omnicron/omnicron above the tests), these pass without checking.
/// </summary>
public sealed class OmnicronTests : IDisposable
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

    // the scan codes of the keys the start takes
    private const byte Three = 0x04, Four = 0x05, Enter = 0x1C;

    /// <summary>
    /// The game's loader, run at an AT's pace to the game's question about the display, from
    /// midnight, so a run doesn't depend on the time of day.
    /// </summary>
    private Machine? Start()
    {
        if (TestData.Game("omnicron/omnicron") is not { } folder)
        {
            return null;
        }

        var machine = new Machine(folder, _saves) { Log = _log.Add, InstructionsPerSecond = 3_000_000, TickCount = 0 };
        machine.Load("OMNICRON.EXE");
        machine.Run(machine.InstructionsPerSecond * 3);
        return machine;
    }

    /// <summary>Presses a key and lets it go, then waits some seconds for the game to answer.</summary>
    private static void Press(Machine machine, byte code, int seconds = 5)
    {
        machine.PressKey(code);
        machine.Run(machine.InstructionsPerSecond / 20);
        machine.ReleaseKey(code);
        machine.Run(machine.InstructionsPerSecond * seconds);
    }

    /// <summary>The colors of the MCGA's screen at a place: the game draws in the top four bits of each byte.</summary>
    private static int Color(Machine machine, int x, int y) => machine.Read(0xA_0000 + (uint)((y * 320) + x)) >> 4;

    /// <summary>The yellow (14) pixels of a place's name on the bar under the room, on the MCGA.</summary>
    private static int Named(Machine machine) => Count(machine, 100, 220, 160, 169, 14);

    /// <summary>
    /// The cyan (3) pixels behind a message box's "press ENTER key to continue", at the bottom
    /// of the MCGA screen, where the bar of life is otherwise.
    /// </summary>
    private static int Prompted(Machine machine) => Count(machine, 40, 280, 188, 198, 3);

    private static int Count(Machine machine, int left, int right, int top, int bottom, int color)
    {
        int count = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                count += Color(machine, x, y) == color ? 1 : 0;
            }
        }

        return count;
    }

    [Fact]
    public void The_loader_runs_the_game_and_the_game_its_music_player_which_hands_back()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // the loader is the game's first program; the game, SCODE.EXE, is its child
        Assert.Contains(_log, line => line.StartsWith(@"21/4B run C:\SCODE.EXE", StringComparison.Ordinal));
        Assert.Contains(_log, line => line.StartsWith("console: ", StringComparison.Ordinal) && line.Contains("MCGA mode is suggested", StringComparison.Ordinal));

        // the MCGA: the title, while PLAY.EXE plays the theme and waits for Enter, asking DOS
        // whether a key is waiting, which the game's own INT 16h answers
        Press(machine, Four, seconds: 8);
        Assert.Contains(_log, line => line.StartsWith(@"21/4B run C:\PLAY.EXE", StringComparison.Ordinal));
        Assert.Equal(0x13, machine.Read(0x449));
        Screenshot.WritePng(machine, Path.Combine(Shots, "omnicron-title.png"));

        Press(machine, Enter);
        Assert.Contains("back to 0F8Dh", _log);
        Assert.False(machine.Exited);

        // the copyright, then P.A.L. in the bedroom, with H.Q.'s orders in three messages, an
        // Enter each; the second gives Cron's star coordinate
        int messages = 0;
        for (int i = 0; i < 12 && (messages == 0 || Prompted(machine) > 0); i++)
        {
            Press(machine, Enter);
            if (Prompted(machine) > 0 && ++messages == 2)
            {
                Screenshot.WritePng(machine, Path.Combine(Shots, "omnicron-orders.png"));
            }
        }

        Assert.Equal(3, messages);
        Assert.True(Named(machine) > 30, "The bar under the room doesn't name it.");
        Screenshot.WritePng(machine, Path.Combine(Shots, "omnicron-bedroom.png"));
    }

    [Fact]
    public void On_the_EGA_it_draws_on_the_second_page_of_640_by_200()
    {
        if (Start() is not { } machine)
        {
            return;
        }

        // its detection finds a VGA, and skips the EGA test then, so it warns that there's no
        // EGA; Enter takes the EGA anyway
        Press(machine, Three);
        Assert.Contains(_log, line => line.Contains("You do not have an EGA card", StringComparison.Ordinal));
        Press(machine, Enter, seconds: 8);
        Assert.Equal(0x0E, machine.Read(0x449));
        Assert.Equal(1, machine.Read(0x462));
        Screenshot.WritePng(machine, Path.Combine(Shots, "omnicron-ega-title.png"));

        for (int i = 0; i < 7; i++)
        {
            Press(machine, Enter);
        }

        Screenshot.WritePng(machine, Path.Combine(Shots, "omnicron-ega-bedroom.png"));
        Assert.False(machine.Exited);
    }
}
