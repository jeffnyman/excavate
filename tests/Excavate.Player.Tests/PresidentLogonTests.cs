using System.Text;

namespace Excavate.Player.Tests;

/// <summary>What the player says The President Is Missing will log on with, from a stand-in profile.</summary>
public sealed class PresidentLogonTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("excavate-logon").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // a profile as setup writes it: the answers, each ending in a 0, then a '~' and the story's clock
    private string Profile(string folder, string codeName, string supervisor)
    {
        string path = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        File.WriteAllBytes(Path.Combine(path, "SA"), [.. Encoding.ASCII.GetBytes($"H\0C\0{codeName}\0{supervisor}\0DOE\0~"), 0x16, 0x06, 0xCC, 0x07]);
        return path;
    }

    [Fact]
    public void The_profile_in_the_save_folder_is_the_one_the_game_logs_on_with()
    {
        string game = Profile("game", "UNDERDOGS", "SCIN");
        string saves = Path.Combine(_root, "saves");

        Assert.Equal("its profile logs on as UNDERDOGS, supervisor SCIN; excavate president SU.EXE makes another", PresidentLogon.Describe(game, saves));

        Profile("saves", "EXCAVATE", "WILLIAM H. SULLENS");
        Assert.StartsWith("its profile logs on as EXCAVATE, supervisor WILLIAM H. SULLENS;", PresidentLogon.Describe(game, saves));
    }

    [Fact]
    public void Without_a_profile_there_is_nothing_to_say()
        => Assert.Null(PresidentLogon.Describe(Path.Combine(_root, "none"), Path.Combine(_root, "saves")));
}
