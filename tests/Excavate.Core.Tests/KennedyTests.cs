namespace Excavate.Core.Tests;

/// <summary>
/// <i>Guardians of Infinity: To Save Kennedy</i> (Paragon Software, 1988), from the player's
/// copy, started as it was: by its batch file. The pictures go to <c>screenshots</c> beside the
/// tests. Without the game (in EXCAVATE_GAMES, or _excavating/guardken/guardken above the
/// tests), these pass without checking.
/// </summary>
public sealed class KennedyTests : IDisposable
{
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_saves, recursive: true);

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

        string screen = string.Join('\n', Enumerable.Range(0, 25).Select(row => new string(
            Enumerable.Range(0, 80).Select(column => (char)machine.Read(0xB_8000 + (uint)((row * 160) + (column * 2)))).ToArray())));
        Assert.Contains("the timeship has arrived", screen);
        Assert.Contains("Kennedy: New York City, New York", screen);

        string shots = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(shots);
        Screenshot.WritePng(machine, Path.Combine(shots, "kennedy-start.png"));
    }
}
