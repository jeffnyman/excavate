namespace Excavate.Player.Tests;

/// <summary>
/// Profiles and the command line, with no game needed: a temporary folder holds a stand-in
/// <c>COUNT.EXE</c>, a few bytes that end at once, and the settings live in a temporary file.
/// </summary>
public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("excavate-player").FullName;

    public ProfileTests()
    {
        Countdown = Directory.CreateDirectory(Path.Combine(_root, "Countdown")).FullName;
        File.WriteAllBytes(Path.Combine(Countdown, "COUNT.EXE"), Exe([0xB8, 0x00, 0x4C, 0xCD, 0x21])); // mov ax, 4C00h; int 21h
        SettingsPath = Path.Combine(_root, "home", "excavate.json");
        Saves = Path.Combine(_root, "saves");
    }

    private static KnownGame CountdownGame => Games.Named("countdown")!;

    private string Countdown { get; }

    private string SettingsPath { get; }

    private string Saves { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private int Run(params string[] args)
        => Program.Run([.. args, "--settings", SettingsPath, "--saves", Saves, "--headless", "--seconds", "1"]);

    [Fact]
    public void No_settings_file_is_empty_settings()
    {
        var settings = Settings.Load(SettingsPath);
        Assert.Empty(settings.Folders);
        Assert.Null(settings.FolderOf(CountdownGame));
        Assert.True(settings.ProfileOf(CountdownGame).IsEmpty);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Settings_are_kept_and_read_back_whatever_the_case_of_their_names()
    {
        var settings = Settings.Load(SettingsPath);
        Assert.True(settings.Remember(CountdownGame, Countdown));
        Assert.False(settings.Remember(CountdownGame, Countdown));
        settings.Profiles["COUNTDOWN"] = new Profile { Scale = 4, Joystick = false };
        settings.Save();

        var read = Settings.Load(SettingsPath);
        Assert.Equal(Countdown, read.FolderOf(CountdownGame));
        var profile = read.ProfileOf(CountdownGame);
        Assert.Equal(4, profile.Scale);
        Assert.False(profile.Joystick);
        Assert.Null(profile.Cycles);
        Assert.Equal("no joystick, scale 4", profile.ToString());

        // plain JSON
        Assert.Contains("\"scale\": 4", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Settings_that_cannot_be_read_say_so()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not settings");
        var ex = Assert.Throws<InvalidDataException>(() => Settings.Load(SettingsPath));
        Assert.Contains(SettingsPath, ex.Message);
    }

    [Fact]
    public void A_profile_takes_only_what_another_sets()
    {
        var profile = new Profile { Cycles = 4000, Sound = false };
        profile.Take(new Profile { Scale = 2 });
        Assert.Equal(4000, profile.Cycles);
        Assert.False(profile.Sound);
        Assert.Equal(2, profile.Scale);
    }

    [Theory]
    [InlineData("portal", "Portal")]
    [InlineData("PORTAL", "Portal")]
    [InlineData("countdown", "Countdown")]
    [InlineData("orion", "The Orion Conspiracy")]
    public void Games_are_known_by_their_short_names(string name, string title)
        => Assert.Equal(title, Games.Named(name)!.Title);

    [Fact]
    public void Every_game_has_a_short_name_of_its_own()
        => Assert.Equal(Games.Known.Length, Games.Known.Select(game => game.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void A_folder_is_known_by_its_files()
    {
        Assert.Equal(CountdownGame, Games.KnownIn(Countdown));
        Assert.Null(Games.KnownIn(_root));

        var game = Games.Find(Countdown, null);
        Assert.Equal("COUNT.EXE", game.Program);
        Assert.Equal(CountdownGame, game.Known);
        Assert.Equal(CountdownGame.Cycles, game.Cycles);
        Assert.Throws<FileNotFoundException>(() => Games.In(Countdown, Games.Named("orion")!));
    }

    /// <summary>A 360K image with names in its root directory, after a deleted entry that doesn't count.</summary>
    private static string Image(string folder, string name, params string[] files)
    {
        var image = new byte[Games.FloppySize];
        int root = 5 * 512;
        "XOTHER  EXE"u8.CopyTo(image.AsSpan(root));
        image[root] = 0xE5;
        for (int i = 0; i < files.Length; i++)
        {
            System.Text.Encoding.ASCII.GetBytes(files[i]).CopyTo(image.AsSpan(root + (32 * (i + 1))));
        }

        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    [Fact]
    public void A_game_played_from_its_disks_is_known_by_what_is_on_them()
    {
        // Portal's disks, whatever they're called: the first has PORTAL.EXE, the others only data
        string folder = Directory.CreateDirectory(Path.Combine(_root, "Portal")).FullName;
        string second = Image(folder, "A.IMG");
        string first = Image(folder, "B.IMG", "PORTAL  EXE");
        string third = Image(folder, "C.IMG");
        Assert.Equal(["PORTAL.EXE"], Games.NamesOn(first));

        var portal = Games.Named("portal")!;
        Assert.Equal(portal, Games.KnownIn(folder));

        // the disk with the program first, then the rest by name
        var game = Games.In(folder, portal);
        Assert.Equal([first, second, third], game.Disks);
        Assert.Equal(@"A:\PORTAL.EXE", game.Program);
        Assert.Equal(Core.VideoAdapter.Cga, game.Video);
    }

    [Fact]
    public void A_game_that_boots_from_its_disks_is_known_but_not_played_yet()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "Metropolis")).FullName;
        Image(folder, "METROPOA.IMA", "METRO   SCR");
        var metropolis = Games.Named("metropolis")!;
        Assert.Equal(metropolis, Games.KnownIn(folder));
        var ex = Assert.Throws<NotSupportedException>(() => Games.In(folder, metropolis));
        Assert.Contains("boots from its own disks", ex.Message);
    }

    [Fact]
    public void A_game_on_drive_C_has_no_disks_and_a_vga()
    {
        var game = Games.Find(Countdown, null);
        Assert.Empty(game.Disks);
        Assert.Equal(Core.VideoAdapter.Vga, game.Video);
    }

    [Fact]
    public void A_folder_played_once_is_remembered_for_its_short_name()
    {
        // a game that isn't set up can't be played by its short name
        var ex = Assert.Throws<FileNotFoundException>(() => Run("countdown"));
        Assert.Contains("isn't set up", ex.Message);

        // played by its folder, it's remembered, and then by its short name
        Assert.Equal(0, Run(Countdown));
        Assert.Equal(Countdown, Settings.Load(SettingsPath).FolderOf(CountdownGame));
        Assert.Equal(0, Run("countdown"));

        // and forgotten, with --list, so nothing plays
        Assert.Equal(0, Run("countdown", "--forget-folder", "--list"));
        Assert.Null(Settings.Load(SettingsPath).FolderOf(CountdownGame));

        // only by a short name: a folder given would be remembered again at once
        Assert.Contains("short name", Assert.ThrowsAny<Exception>(() => Run(Countdown, "--forget-folder")).Message);
    }

    [Fact]
    public void Another_program_in_a_games_folder_is_run_by_the_games_short_name()
    {
        // SETUP.EXE leaves a file named RAN behind: push cs; pop ds; mov dx, 10h; mov ah, 3Ch;
        // xor cx, cx; int 21h; mov ax, 4C00h; int 21h; "RAN"
        File.WriteAllBytes(Path.Combine(Countdown, "SETUP.EXE"), Exe(
        [
            0x0E, 0x1F, 0xBA, 0x10, 0x00, 0xB4, 0x3C, 0x31, 0xC9, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21,
            .. "RAN\0"u8,
        ]));
        Assert.Equal(0, Run(Countdown));

        Assert.Equal(0, Run("countdown", "SETUP.EXE"));
        Assert.True(File.Exists(Path.Combine(Saves, "RAN")));
        Assert.Contains("no NOTHERE.EXE", Assert.Throws<FileNotFoundException>(() => Run("countdown", "NOTHERE.EXE")).Message);
    }

    [Fact]
    public void Options_are_kept_only_when_asked_and_forgotten_when_asked()
    {
        Assert.Equal(0, Run(Countdown, "--cycles", "5000"));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(CountdownGame).IsEmpty);

        Assert.Equal(0, Run("countdown", "--cycles", "5000", "--no-sound", "--remember"));
        var profile = Settings.Load(SettingsPath).ProfileOf(CountdownGame);
        Assert.Equal(5000, profile.Cycles);
        Assert.False(profile.Sound);

        // what's remembered is added to, not replaced
        Assert.Equal(0, Run("countdown", "--scale", "5", "--remember"));
        profile = Settings.Load(SettingsPath).ProfileOf(CountdownGame);
        Assert.Equal(5000, profile.Cycles);
        Assert.Equal(5, profile.Scale);

        Assert.Equal(0, Run("countdown", "--mouse-speed", "50", "--remember"));
        profile = Settings.Load(SettingsPath).ProfileOf(CountdownGame);
        Assert.Equal(50, profile.MouseSpeed);
        Assert.Equal("cycles 5000, no sound, mouse speed 50%, scale 5", profile.ToString());
        Assert.ThrowsAny<Exception>(() => Run("countdown", "--mouse-speed", "5"));

        Assert.Equal(0, Run("countdown", "--forget"));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(CountdownGame).IsEmpty);
        Assert.Equal(Countdown, Settings.Load(SettingsPath).FolderOf(CountdownGame));
    }

    [Fact]
    public void A_saved_state_is_resumed_from_its_file()
    {
        // one for each program, beside the saves rather than in them, where the game would see it
        var game = Games.Find(Countdown, null);
        Assert.Equal(Path.Combine(Games.Home, "states", "countdown", "COUNT.state"), Games.StateFile(game));

        string state = Path.Combine(_root, "count.state");
        var ex = Assert.Throws<FileNotFoundException>(() => Run(Countdown, "--resume", "--state", state));
        Assert.Contains("no state saved", ex.Message);

        // a state of the program as loaded, before its first instruction
        var machine = new Core.Machine(Countdown, Saves);
        machine.Load("COUNT.EXE");
        using (var file = File.Create(state))
        {
            machine.SaveState(file);
        }

        Assert.Equal(0, Run("countdown", "--resume", "--state", state));

        File.WriteAllText(state, "not a state");
        Assert.Throws<InvalidDataException>(() => Run("countdown", "--resume", "--state", state));
    }

    [Fact]
    public void The_menu_lists_the_games_and_what_is_set_up()
    {
        var settings = Settings.Load(SettingsPath);
        settings.Remember(CountdownGame, Countdown);
        string picture = Path.Combine(AppContext.BaseDirectory, "screenshots", "menu.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        Sdl.SdlMenu.Snapshot(settings, picture, selected: Array.IndexOf(Games.Known, CountdownGame), message: "Countdown is set up");

        // a 32-bit BMP: its pixels start where its header says
        byte[] bmp = File.ReadAllBytes(picture);
        Assert.Equal((byte)'B', bmp[0]);
        int start = BitConverter.ToInt32(bmp, 10);
        var colors = new HashSet<int>();
        for (int i = start; i + 4 <= bmp.Length; i += 4)
        {
            colors.Add(BitConverter.ToInt32(bmp, i) & 0xFFFFFF);
        }

        // the background, the bar, and text in four colors at least
        Assert.True(colors.Count >= 6, $"only {colors.Count} colors");
    }

    /// <summary>An MZ executable around code that starts at its first byte, as in the core's DOS tests.</summary>
    internal static byte[] Exe(byte[] code)
    {
        int total = 32 + code.Length;
        var exe = new byte[total];
        void Word(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(exe, at);
        exe[0] = (byte)'M';
        exe[1] = (byte)'Z';
        Word(0x02, total % 512);
        Word(0x04, (total + 511) / 512);
        Word(0x08, 2);
        Word(0x0A, 0x1000);
        Word(0x0C, 0xFFFF);
        Word(0x0E, 0x1000);
        Word(0x10, 0xFFFE);
        Word(0x18, 0x1C);
        code.CopyTo(exe, 32);
        return exe;
    }
}
