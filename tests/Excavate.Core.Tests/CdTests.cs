using System.Buffers.Binary;
using System.Text;

namespace Excavate.Core.Tests;

/// <summary>
/// The CD: an ISO 9660 image as drive D:, built here a few sectors long, with a program run from
/// it, its files read through DOS, and MSCDEX's answers; and GOG's own image of Wing Commander
/// III, when it's there.
/// </summary>
public sealed class CdTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    /// <summary>
    /// A program that checks it's on D:, reads DATA.TXT from there to the console, fails to create
    /// it again (a CD is read-only), and finds MSCDEX with one CD drive, D:; it exits with 0, or 1
    /// at the first thing that didn't hold:
    ///   push cs; pop ds; mov ah, 19h; int 21h; cmp al, 3; jne fail
    ///   mov ax, 3D00h; mov dx, name; int 21h; jc fail
    ///   mov bx, ax; mov ah, 3Fh; mov cx, 100; mov dx, buf; int 21h; jc fail
    ///   mov cx, ax; mov ah, 40h; mov bx, 1; mov dx, buf; int 21h
    ///   mov ah, 3Ch; xor cx, cx; mov dx, name; int 21h; jnc fail
    ///   mov ax, 1500h; xor bx, bx; int 2Fh; cmp bx, 1; jne fail; cmp cx, 3; jne fail
    ///   mov ax, 4C00h; int 21h
    ///   fail: mov ax, 4C01h; int 21h
    /// </summary>
    private static readonly byte[] Reader = DosTests.Exe(
    [
        0x0E, 0x1F, 0xB4, 0x19, 0xCD, 0x21, 0x3C, 0x03, 0x75, 0x45, 0xB8, 0x00, 0x3D, 0xBA, 0x54, 0x00,
        0xCD, 0x21, 0x72, 0x3B, 0x89, 0xC3, 0xB4, 0x3F, 0xB9, 0x64, 0x00, 0xBA, 0x5D, 0x00, 0xCD, 0x21,
        0x72, 0x2D, 0x89, 0xC1, 0xB4, 0x40, 0xBB, 0x01, 0x00, 0xBA, 0x5D, 0x00, 0xCD, 0x21, 0xB4, 0x3C,
        0x31, 0xC9, 0xBA, 0x54, 0x00, 0xCD, 0x21, 0x73, 0x16, 0xB8, 0x00, 0x15, 0x31, 0xDB, 0xCD, 0x2F,
        0x83, 0xFB, 0x01, 0x75, 0x0A, 0x83, 0xF9, 0x03, 0x75, 0x05, 0xB8, 0x00, 0x4C, 0xCD, 0x21, 0xB8,
        0x01, 0x4C, 0xCD, 0x21,
        .. "DATA.TXT\0"u8,
    ]);

    [Fact]
    public void A_program_runs_from_the_CD_and_reads_it()
    {
        string image = Image(("READER.EXE", Reader), ("DATA.TXT", "from the disc\r\n"u8.ToArray()));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        Assert.Equal("TEST_CD", machine.InsertCd(image));
        machine.Load(@"D:\READER.EXE");
        machine.Run(100_000);

        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal(0, machine.ExitCode);
        Assert.Contains("console: from the disc", _log);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_saves));
    }

    [Fact]
    public void A_state_holds_the_CD()
    {
        // saved while the program has a file on the CD open, with the file's time from the
        // disc's directory: a machine with the same CD loads it and reads on
        string image = Image(("READER.EXE", Reader), ("DATA.TXT", "from the disc\r\n"u8.ToArray()));
        var opened = new List<string>();
        var machine = new Machine(_game, _saves) { Log = opened.Add };
        machine.InsertCd(image);
        machine.Load(@"D:\READER.EXE");
        while (!opened.Any(line => line.StartsWith("21/3D open D:", StringComparison.Ordinal) || line.Contains("DATA.TXT", StringComparison.Ordinal)))
        {
            machine.Run(1);
        }

        Assert.False(machine.Exited);
        using var state = new MemoryStream();
        machine.SaveState(state);
        machine.CloseFiles();

        var loaded = new Machine(_game, _saves) { Log = _log.Add };
        loaded.InsertCd(image);
        state.Position = 0;
        loaded.LoadState(state);
        loaded.Run(100_000);
        Assert.True(loaded.Exited, string.Join('\n', _log));
        Assert.Equal(0, loaded.ExitCode);
        Assert.Contains("console: from the disc", _log);
    }

    [Fact]
    public void Without_a_CD_there_is_no_MSCDEX_and_no_drive_D()
    {
        // the same program from C:: the current drive isn't D:, so it fails at once
        File.WriteAllBytes(Path.Combine(_game, "READER.EXE"), Reader);
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("READER.EXE");
        machine.Run(100_000);

        Assert.Equal(1, machine.ExitCode);
        Assert.Throws<FileNotFoundException>(() => new Machine(_game, _saves).Load(@"D:\READER.EXE"));
    }

    /// <summary>
    /// An ISO 9660 image of files in its root: the system area, the primary volume descriptor at
    /// sector 16, the terminator, the root directory at 18, and the files after it, a sector or
    /// more each.
    /// </summary>
    private string Image(params (string Name, byte[] Data)[] files)
    {
        const int Sector = 2048;
        int firstFile = 19;
        var sectors = new List<int>();
        int next = firstFile;
        foreach (var (_, data) in files)
        {
            sectors.Add(next);
            next += Math.Max(1, (data.Length + Sector - 1) / Sector);
        }

        var image = new byte[next * Sector];

        static byte[] Record(int sector, int size, bool directory, byte[] name)
        {
            int length = 33 + name.Length + ((name.Length % 2 == 0) ? 1 : 0);
            var record = new byte[length];
            record[0] = (byte)length;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)sector);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), (uint)sector);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(14), (uint)size);
            record[18] = 94;                      // 1994
            record[19] = 11;
            record[20] = 1;
            record[25] = (byte)(directory ? 0x02 : 0x00);
            record[28] = 1;                       // volume sequence number
            record[32] = (byte)name.Length;
            name.CopyTo(record, 33);
            return record;
        }

        // the primary volume descriptor, and the set's terminator
        var descriptor = image.AsSpan(16 * Sector, Sector);
        descriptor[0] = 1;
        "CD001"u8.CopyTo(descriptor[1..]);
        descriptor[6] = 1;
        Encoding.ASCII.GetBytes("TEST_CD".PadRight(32)).CopyTo(descriptor[40..]);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[80..], (uint)next);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[128..], Sector);
        Record(18, Sector, true, [0]).CopyTo(descriptor[156..]);
        var terminator = image.AsSpan(17 * Sector, Sector);
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator[1..]);

        // the root: itself, its parent, then the files, named as DOS names them with ";1" after
        int at = 18 * Sector;
        foreach (var record in new[] { Record(18, Sector, true, [0]), Record(18, Sector, true, [1]) }
            .Concat(files.Select((file, i) => Record(sectors[i], file.Data.Length, false, Encoding.ASCII.GetBytes(file.Name + ";1")))))
        {
            record.CopyTo(image, at);
            at += record.Length;
        }

        for (int i = 0; i < files.Length; i++)
        {
            files[i].Data.CopyTo(image, sectors[i] * Sector);
        }

        string path = Path.Combine(_game, "TEST.ISO");
        File.WriteAllBytes(path, image);
        return path;
    }
}
