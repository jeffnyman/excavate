using Excavate.Core;

namespace Excavate.Player;

/// <summary>
/// A track of a game's disk that the original disk had more sectors on than a standard one, and
/// an image of it lost: the game's copy protection, which the player puts back.
/// </summary>
/// <param name="Disk">The disk, counting from 0.</param>
/// <param name="Cylinder">The track's cylinder.</param>
/// <param name="Head">The track's side.</param>
/// <param name="First">The first of the extra sectors' numbers.</param>
/// <param name="Last">The last.</param>
internal sealed record ExtraSectors(int Disk, int Cylinder, int Head, int First, int Last);

/// <summary>
/// A place a game asks for another disk, which the player answers for you: where it waits for
/// a key, and which disk it wants. Offsets are in the game's program image.
/// </summary>
/// <param name="KeyWait">The instruction after the <c>INT 16h</c> it waits for a key with.</param>
/// <param name="Caller">The instruction after the prompt's call to the routine that waits.</param>
/// <param name="Disk">The disk it asks for, counting from 0, when it's always the same one.</param>
/// <param name="DigitAt">Otherwise, the byte holding the digit it shows for the disk.</param>
/// <param name="Digits">What that byte holds for each disk, disk 1's first.</param>
internal sealed record DiskPrompt(int KeyWait, int Caller, int Disk = 0, int DigitAt = -1, string Digits = "");

/// <summary>
/// How a game played from its floppy disks has them: the images in its folder are its disks,
/// the one with a file named here first and the rest in the order of their names.
/// </summary>
/// <param name="FirstDisk">A file in the first disk's root directory, which tells it from the others.</param>
/// <param name="Protection">The extra sectors to put back on the disks.</param>
/// <param name="Prompts">Where it asks for another disk, for the player to answer.</param>
internal sealed record DiskSet(string FirstDisk, ExtraSectors[] Protection, DiskPrompt[]? Prompts = null);

/// <summary>
/// A game the player knows by a short name: what it is, how its folder is recognized, and what
/// to run in it.
/// </summary>
/// <param name="Key">Its name in the settings and on the command line, as in <c>portal</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="Year">The year it came out.</param>
/// <param name="Markers">
/// Files that identify its folder: any one of them there, whatever its case, or in the root
/// directory of a disk image there, and the folder is taken to be this game's.
/// </param>
/// <param name="Program">
/// The program to run, the game's own (never a launcher added to a collection later), on drive
/// C: or, as <c>A:\NAME.EXE</c>, from its first disk; null for a game that boots from its own
/// disks, with no DOS program to run.
/// </param>
/// <param name="Cycles">
/// Its speed to start from, in instructions a millisecond. Provisional until the game is played
/// through: each is a first guess from the machine the game was written for.
/// </param>
/// <param name="Note">Where the game stands in Excavate, said in the list and the menu.</param>
/// <param name="Video">The display adapter it was written for.</param>
/// <param name="Disks">For a game played from its floppy disks, how it has them; otherwise null.</param>
/// <param name="MouseJoystick">
/// Whether the mouse is also its joystick: for a game with a joystick and no mouse, whose stick
/// only ever says which way.
/// </param>
internal sealed record KnownGame(
    string Key, string Title, int Year, string[] Markers, string? Program, int Cycles, string Note,
    VideoAdapter Video = VideoAdapter.Vga, DiskSet? Disks = null, bool MouseJoystick = false);

/// <summary>A game to play: its folder, the program in it to run, and how fast to run it.</summary>
/// <param name="Folder">The game's folder, which becomes drive C:.</param>
/// <param name="Program">The program to run, as DOS is given it.</param>
/// <param name="Title">Its title.</param>
/// <param name="Cycles">Its speed, in instructions a millisecond.</param>
/// <param name="Known">The game the player knows it as, or null for a program it doesn't know.</param>
internal sealed record Game(string Folder, string Program, string Title, int Cycles, KnownGame? Known)
{
    /// <summary>The display adapter to build the machine with.</summary>
    public VideoAdapter Video => Known?.Video ?? VideoAdapter.Vga;

    /// <summary>The images of its floppy disks, disk 1 first; none for a game played from drive C:.</summary>
    public string[] Disks => Known?.Disks is { } set ? Games.DisksIn(Folder, set) : [];
}

/// <summary>
/// The games the player knows, and how it finds one from what's given on the command line: a
/// short name, a folder, or a program.
/// </summary>
/// <remarks>
/// <para>
/// The games are played from copies the player has: none are included. A game's folder is given
/// once and remembered, and its short name finds it after that.
/// </para>
/// <para>
/// Each game runs its own program. Collections often add launchers, menus and loaders of their
/// own beside a game's files; those are left alone, and anything they did that the game needs is
/// done by the player instead.
/// </para>
/// </remarks>
internal static class Games
{
    // an 8088 at 4.77 MHz, a Tandy 1000 at 7.16 MHz, an AT, and a 386 or better
    private const int Pc = 300, Tandy = 600, At = 3000, Fast = 30000;

    private const string Untried = "not yet tried";

    /// <summary>Every game the player knows, in the order the menu shows them: by year.</summary>
    public static readonly KnownGame[] Known =
    [
        // Portal: an 8088's pace, since it times its pauses by counting loops; its disk 1's
        // track 9 had sectors 1 to 16, of which a dump keeps nine; its two prompts for a disk,
        // both waiting in its key routine: for the story's level's disk, whose digit it keeps
        // at 2F67h (with 3 made 4 once it's shown), and for disk 1, before restoring a saved
        // position; and its joystick, if you say you have one, is only ever four arrows and a
        // button, which the mouse can be
        new("portal", "Portal", 1986, ["PORTAL.EXE"], @"A:\PORTAL.EXE", Pc, "new: it plays to Central Processing, and further is untried",
            VideoAdapter.Cga,
            new DiskSet("PORTAL.EXE", [new ExtraSectors(0, 9, 0, 10, 16)],
                [new DiskPrompt(0x10970, 0x11EFA, DigitAt: 0x2F67, Digits: "124"), new DiskPrompt(0x10970, 0x1236B, Disk: 0)]),
            MouseJoystick: true),
        new("metropolis", "Metropolis", 1987, ["METRO.SCR"], null, Tandy, "boots from its own disks, which Excavate doesn't do yet"),
        new("kennedy", "Guardians of Infinity: To Save Kennedy", 1988, ["GOI.EXE"], "KENNEDY.BAT", Pc, Untried),
        new("president", "The President Is Missing", 1988, ["PRES.EXE"], "PRES.EXE", Pc, Untried),
        new("guilty", "Presumed Guilty", 1989, ["PGMAIN.EXE"], "PGMAIN.EXE", Pc, Untried),
        new("omnicron", "The Omnicron Conspiracy", 1989, ["OMNICRON.EXE"], "OMNICRON.EXE", At, Untried),
        new("countdown", "Countdown", 1990, ["COUNT.EXE"], "COUNT.EXE", At, Untried),
        new("orion", "The Orion Conspiracy", 1995, ["ORION.BAT"], "ORION.BAT", Fast, Untried),
    ];

    /// <summary>The speed of a program the player doesn't know: an AT's.</summary>
    public const int DefaultCycles = At;

    /// <summary>The game with a short name, or null.</summary>
    public static KnownGame? Named(string name)
        => Known.FirstOrDefault(game => string.Equals(game.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The game whose files are in a folder, or null.</summary>
    public static KnownGame? KnownIn(string folder)
        => Known.FirstOrDefault(game => IsIn(folder, game));

    /// <summary>Whether a known game's files are in a folder, or on a disk image there.</summary>
    public static bool IsIn(string folder, KnownGame game)
        => game.Markers.Any(marker => FileIn(folder, marker) is not null)
            || DiskImagesIn(folder).Any(image => NamesOn(image).Intersect(game.Markers, StringComparer.OrdinalIgnoreCase).Any());

    /// <summary>The size of a 360K floppy's image: 40 tracks, 2 sides, 9 sectors of 512 bytes.</summary>
    public const int FloppySize = 368_640;

    /// <summary>
    /// A game's disks in its folder: the image whose root directory has the file that marks the
    /// first disk, then the others in the order of their names.
    /// </summary>
    public static string[] DisksIn(string folder, DiskSet set)
    {
        var images = DiskImagesIn(folder).ToList();
        string? first = images.FirstOrDefault(image => NamesOn(image).Contains(set.FirstDisk, StringComparer.OrdinalIgnoreCase));
        return first is null ? [.. images] : [first, .. images.Where(image => image != first)];
    }

    /// <summary>The disk images in a folder: every file the size of a 360K floppy, whatever it's called.</summary>
    public static IEnumerable<string> DiskImagesIn(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(file => new FileInfo(file).Length == FloppySize).Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>
    /// The names in a 360K disk image's root directory, as <c>NAME.EXT</c>. On a 360K disk it
    /// starts at sector 5, after the boot sector and two copies of a two-sector FAT, and holds
    /// 112 entries of 32 bytes. Games that boot from their own disks often keep that layout
    /// without a DOS boot sector, so it's read from where it is rather than from the boot
    /// sector's description of the disk.
    /// </summary>
    public static IEnumerable<string> NamesOn(string image)
    {
        const int Root = 5 * 512, Entries = 112;
        var directory = new byte[Entries * 32];
        using (var stream = File.OpenRead(image))
        {
            stream.Position = Root;
            stream.ReadAtLeast(directory, directory.Length, throwOnEndOfStream: false);
        }

        var names = new List<string>();
        for (int at = 0; at < directory.Length; at += 32)
        {
            byte first = directory[at];
            if (first == 0)
            {
                // the end of the directory
                break;
            }

            // a deleted entry, a volume label or a subdirectory, or something that isn't a name
            byte attributes = directory[at + 11];
            if (first == 0xE5 || (attributes & 0x18) != 0 || !directory.AsSpan(at, 11).ToArray().All(c => c is >= 0x20 and < 0x7F))
            {
                continue;
            }

            string name = System.Text.Encoding.ASCII.GetString(directory, at, 8).TrimEnd();
            string extension = System.Text.Encoding.ASCII.GetString(directory, at + 8, 3).TrimEnd();
            names.Add(extension.Length == 0 ? name : $"{name}.{extension}");
        }

        return names;
    }

    /// <summary>A known game in its folder.</summary>
    /// <exception cref="FileNotFoundException">Its files aren't there.</exception>
    /// <exception cref="NotSupportedException">It boots from its own disks, which the player can't yet do.</exception>
    public static Game In(string folder, KnownGame game)
    {
        if (!IsIn(folder, game))
        {
            throw new FileNotFoundException($"no {string.Join(" or ", game.Markers)} in {folder}");
        }

        return game.Program is { } program
            ? new Game(Path.GetFullPath(folder), program, game.Title, game.Cycles, game)
            : throw new NotSupportedException($"{game.Title} {game.Note}.");
    }

    /// <summary>
    /// The game at a path: a program file, or a folder holding a game the player knows, or a
    /// folder and a program named apart.
    /// </summary>
    public static Game Find(string path, string? program)
    {
        string folder;
        if (File.Exists(path))
        {
            folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            program ??= Path.GetFileName(path);
        }
        else if (Directory.Exists(path))
        {
            folder = Path.GetFullPath(path);
        }
        else
        {
            throw new FileNotFoundException($"no game at {path}");
        }

        var known = KnownIn(folder);
        if (program is null)
        {
            return known is not null
                ? In(folder, known)
                : throw new FileNotFoundException($"no game the player knows in {folder}: name its program, as in GAME.EXE");
        }

        if (FileIn(folder, program) is null)
        {
            throw new FileNotFoundException($"no {program} in {folder}");
        }

        return new Game(folder, program.ToUpperInvariant(), known?.Title ?? Path.GetFileNameWithoutExtension(program).ToUpperInvariant(), known?.Cycles ?? DefaultCycles, known);
    }

    /// <summary>
    /// The player's own folder in the user's application data: the settings, the saves and the
    /// saved states go there.
    /// </summary>
    public static string Home
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "Excavate");

    /// <summary>
    /// Where a game's saves go unless told otherwise: a folder of its own under the player's
    /// home, named for the game (or, for a program the player doesn't know, its folder).
    /// </summary>
    public static string SaveFolder(Game game)
    {
        string name = game.Known?.Key ?? string.Concat(Path.GetFileName(game.Folder).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Home, "saves", name);
    }

    /// <summary>
    /// Where a game's saved state goes unless told otherwise: a file for each program, in a
    /// folder named as its save folder is, beside the saves. Not in the save folder itself, which
    /// is the game's drive C:, where the game would see it.
    /// </summary>
    public static string StateFile(Game game)
        => Path.Combine(Home, "states", Path.GetFileName(SaveFolder(game)), Path.ChangeExtension(game.Program, ".state"));

    // a file in a folder, whatever its case
    private static string? FileIn(string folder, string name)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
            : null;
}
