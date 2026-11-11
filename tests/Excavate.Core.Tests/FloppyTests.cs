namespace Excavate.Core.Tests;

/// <summary>
/// The floppy drive and INT 13h, with small programs assembled by hand run from a 360K disk image
/// made here: a DOS boot sector, two FATs, a root directory holding the program, and the rest
/// filled with each sector's own address so a read can be checked.
/// </summary>
public sealed class FloppyTests : IDisposable
{
    private const int Sector = 512;

    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    /// <summary>
    /// A 360K disk with a program in its root, TEST.EXE, in the clusters from 2. Every sector past
    /// the program's starts with its cylinder, head and sector number.
    /// </summary>
    private string Disk(byte[] program, string name = "DISK1.IMA")
    {
        var image = new byte[368_640];
        for (int cylinder = 0; cylinder < 40; cylinder++)
        {
            for (int head = 0; head < 2; head++)
            {
                for (int sector = 1; sector <= 9; sector++)
                {
                    int at = (((cylinder * 2) + head) * 9 + (sector - 1)) * Sector;
                    image[at] = (byte)cylinder;
                    image[at + 1] = (byte)head;
                    image[at + 2] = (byte)sector;
                }
            }
        }

        // the boot sector's BIOS parameter block, as DOS 2.0 writes it for 360K
        byte[] boot = [0xEB, 0x34, 0x90, .. "EXCAVATE"u8, 0x00, 0x02, 2, 1, 0, 2, 112, 0, 0xD0, 0x02, 0xFD, 2, 0, 9, 0, 2, 0];
        Array.Clear(image, 0, Sector);
        boot.CopyTo(image, 0);

        // the FATs: the media byte, then a chain through as many clusters as the program needs
        int clusters = Math.Max(1, (program.Length + 1023) / 1024);
        foreach (int fat in new[] { 1, 3 })
        {
            int at = fat * Sector;
            Array.Clear(image, at, 2 * Sector);
            image[at] = 0xFD;
            image[at + 1] = 0xFF;
            image[at + 2] = 0xFF;
            for (int c = 2; c < 2 + clusters; c++)
            {
                int next = c == 1 + clusters ? 0xFFF : c + 1;
                int offset = at + (c * 3 / 2);
                if ((c & 1) == 0)
                {
                    image[offset] = (byte)next;
                    image[offset + 1] = (byte)((image[offset + 1] & 0xF0) | (next >> 8));
                }
                else
                {
                    image[offset] = (byte)((image[offset] & 0x0F) | ((next & 0x0F) << 4));
                    image[offset + 1] = (byte)(next >> 4);
                }
            }
        }

        // the root directory, from sector 5: the program, and nothing after it
        Array.Clear(image, 5 * Sector, 7 * Sector);
        "TEST    EXE"u8.CopyTo(image.AsSpan(5 * Sector));
        image[(5 * Sector) + 26] = 2;
        BitConverter.GetBytes(program.Length).CopyTo(image, (5 * Sector) + 28);

        // the data, from sector 12
        Array.Clear(image, 12 * Sector, clusters * 1024);
        program.CopyTo(image, 12 * Sector);

        string path = Path.Combine(_game, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    private Machine Run(byte[] code, Action<Machine>? before = null)
    {
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.InsertFloppies([Disk(DosTests.Exe(code))]);
        before?.Invoke(machine);
        machine.Load(@"A:\TEST.EXE");
        machine.Run(100_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        return machine;
    }

    // mov ah, 4Ch; int 21h: exit with AL as the code
    private static readonly byte[] ExitWithAl = [0xB4, 0x4C, 0xCD, 0x21];

    // mov al, ah; mov ah, 4Ch; int 21h: exit with INT 13h's status as the code
    private static readonly byte[] ExitWithStatus = [0x88, 0xE0, .. ExitWithAl];

    [Fact]
    public void A_program_runs_from_the_disk_in_drive_A_which_is_then_the_current_drive()
    {
        // mov ah, 19h; int 21h: the current drive, 0 for A:
        var machine = Run([0xB4, 0x19, 0xCD, 0x21, .. ExitWithAl]);
        Assert.Equal(0, machine.ExitCode);
        Assert.Contains(_log, line => line.StartsWith(@"loaded A:\TEST.EXE at ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_that_is_not_on_the_disk_is_not_found()
    {
        var machine = new Machine(_game, _saves);
        machine.InsertFloppies([Disk(DosTests.Exe([.. ExitWithAl]))]);
        Assert.Throws<FileNotFoundException>(() => machine.Load(@"A:\OTHER.EXE"));
        machine.Floppy = -1;
        Assert.Throws<FileNotFoundException>(() => machine.Load(@"A:\TEST.EXE"));
    }

    [Fact]
    public void A_sector_is_read_by_its_cylinder_head_and_number()
    {
        // push cs; pop es; mov bx, 100h; mov ax, 0201h (read 1); mov cx, 0103h (cylinder 1,
        // sector 3); mov dx, 0100h (head 1, drive A:); int 13h; jc fail;
        // mov al, es:[102h] (the sector's own number); exit; fail: exit 0FFh
        var machine = Run(
        [
            0x0E, 0x07, 0xBB, 0x00, 0x01, 0xB8, 0x01, 0x02, 0xB9, 0x03, 0x01, 0xBA, 0x00, 0x01,
            0xCD, 0x13, 0x72, 0x08, 0x26, 0xA0, 0x02, 0x01, .. ExitWithAl, 0xB8, 0xFF, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(3, machine.ExitCode);
        Assert.Contains("13/02 read 1 at 1/1/3, disk 1: 1 done, status 00h", _log);
    }

    // mov ax, 0401h (verify 1); mov cx, 0910h (cylinder 9, sector 16); mov dx, 0 (head 0, A:); int 13h
    private static readonly byte[] VerifySixteenOnTrackNine = [0xB8, 0x01, 0x04, 0xB9, 0x10, 0x09, 0xBA, 0x00, 0x00, 0xCD, 0x13];

    [Fact]
    public void A_sector_past_a_tracks_nine_is_not_found_on_an_image()
        => Assert.Equal(0x04, Run([.. VerifySixteenOnTrackNine, .. ExitWithStatus]).ExitCode);

    [Fact]
    public void Sectors_restored_to_a_track_are_found_as_the_original_disk_had_them()
        => Assert.Equal(0x00, Run([.. VerifySixteenOnTrackNine, .. ExitWithStatus], machine => machine.RestoreSectors(0, 9, 0, 10, 16)).ExitCode);

    [Fact]
    public void An_empty_drive_is_not_ready()
    {
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.InsertFloppies([Disk(DosTests.Exe([.. VerifySixteenOnTrackNine, .. ExitWithStatus]))]);
        machine.Load(@"A:\TEST.EXE");
        machine.Floppy = -1;
        machine.Run(100_000);
        Assert.Equal(0x80, machine.ExitCode);
    }

    [Fact]
    public void A_write_goes_to_the_disks_copy_in_the_save_folder_never_to_the_image()
    {
        // push cs; pop es; mov di, 100h; mov cx, 512; mov al, 5Ah; rep stosb (a sector of 5Ah);
        // mov bx, 100h; mov ax, 0301h (write 1); mov cx, 0205h (cylinder 2, sector 5);
        // mov dx, 0100h (head 1); int 13h; exit with the status
        byte[] code =
        [
            0x0E, 0x07, 0xBF, 0x00, 0x01, 0xB9, 0x00, 0x02, 0xB0, 0x5A, 0xF3, 0xAA, 0xBB, 0x00, 0x01,
            0xB8, 0x01, 0x03, 0xB9, 0x05, 0x02, 0xBA, 0x00, 0x01, 0xCD, 0x13, .. ExitWithStatus,
        ];

        var machine = Run(code);
        Assert.Equal(0, machine.ExitCode);

        int at = (((2 * 2) + 1) * 9 + 4) * Sector;
        string image = Path.Combine(_game, "DISK1.IMA");
        Assert.Equal(2, File.ReadAllBytes(image)[at]);
        Assert.Equal(0x5A, File.ReadAllBytes(Path.Combine(_saves, "DISK1.IMA"))[at]);
    }

    [Fact]
    public void The_drive_reports_a_360K_drive_and_its_parameter_table()
    {
        // mov ah, 08h; mov dl, 0; int 13h; exit with the last sector of a track (CL)
        var machine = Run([0xB4, 0x08, 0xB2, 0x00, 0xCD, 0x13, 0x88, 0xC8, .. ExitWithAl]);
        Assert.Equal(9, machine.ExitCode);

        // INT 1Eh points at the table: 512-byte sectors, nine to a track, F6h to format with
        uint table = ((uint)machine.ReadWord(0, 0x7A) << 4) + machine.ReadWord(0, 0x78);
        Assert.Equal(2, machine.Read(table + 3));
        Assert.Equal(9, machine.Read(table + 4));
        Assert.Equal(0xF6, machine.Read(table + 8));
    }

    [Fact]
    public void Disks_go_in_and_out_of_the_drive_and_a_change_is_reported()
    {
        // mov ah, 16h; mov dl, 0; int 13h; exit with the status
        byte[] changeLine = [0xB4, 0x16, 0xB2, 0x00, 0xCD, 0x13, .. ExitWithStatus];
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.InsertFloppies([Disk(DosTests.Exe(changeLine)), Disk(DosTests.Exe(changeLine), "DISK2.IMA")]);
        Assert.Equal(2, machine.Floppies);
        Assert.Equal(0, machine.Floppy);
        Assert.Equal("DISK1.IMA", machine.FloppyName);

        machine.Floppy = 1;
        Assert.Equal("DISK2.IMA", machine.FloppyName);
        Assert.Contains("drive A: disk 2, DISK2.IMA", _log);
        machine.Load(@"A:\TEST.EXE");
        machine.Run(100_000);
        Assert.Equal(0x06, machine.ExitCode);
    }

    [Fact]
    public void An_image_that_is_no_floppys_size_is_refused()
    {
        string path = Path.Combine(_game, "ODD.IMG");
        File.WriteAllBytes(path, new byte[1000]);
        Assert.Throws<InvalidDataException>(() => new Machine(_game, _saves).InsertFloppies([path]));
    }
}
