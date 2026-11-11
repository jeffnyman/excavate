namespace Excavate.Core.Tests;

/// <summary>
/// Batch files, as W.C.A.T. starts its launcher and the games with them: lines run one by one,
/// programs started and carried on from when they end, and the built-in commands its batch
/// files use.
/// </summary>
public sealed class BatchTests : IDisposable
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
    /// A program that writes its command tail to the console and exits with a code:
    /// mov ah, 40h; mov bx, 1; mov cl, [80h]; xor ch, ch; mov dx, 81h; int 21h; mov ax, 4Cxxh; int 21h.
    /// </summary>
    private static byte[] Echoes(byte code) => DosTests.Exe(
    [
        0xB4, 0x40, 0xBB, 0x01, 0x00, 0x8A, 0x0E, 0x80, 0x00, 0x30, 0xED, 0xBA, 0x81, 0x00, 0xCD, 0x21,
        0xB8, code, 0x4C, 0xCD, 0x21,
    ]);

    [Fact]
    public void Programs_run_one_after_another_with_their_arguments()
    {
        Write("FIRST.EXE", Echoes(1));
        Write("SECOND.EXE", Echoes(2));
        var machine = Run("@echo off\r\nFIRST one\r\necho between\r\nSECOND.EXE two three\r\n");

        Assert.Equal(["console:  one", "console: between", "console:  two three"], Console());
        Assert.Equal(2, machine.ExitCode);
    }

    [Fact]
    public void Conditions_jumps_directories_and_calls_work_as_in_DOS()
    {
        Write(@"SUB\PROG.EXE", Echoes(3));
        Write(@"SUB\GONE.TXT", "x"u8.ToArray());
        Write("INNER.BAT", "echo %1 inside\r\n"u8.ToArray());
        Run("""
            @echo off
            CD SUB
            IF EXIST PROG.EXE GOTO :FOUND
            echo missing
            :FOUND
            PROG run
            IF ERRORLEVEL 3 echo three or more
            IF NOT ERRORLEVEL 4 echo below four
            IF ERRORLEVEL 4 echo wrong
            CD ..
            CALL INNER.BAT two
            echo.
            echo back
            DEL SUB\GONE.TXT
            IF NOT EXIST SUB\GONE.TXT echo deleted
            """);

        Assert.Equal(
            ["console:  run", "console: three or more", "console: below four", "console: two inside", "console: back", "console: deleted"],
            Console());
        Assert.True(File.Exists(Path.Combine(_game, "SUB", "GONE.TXT")), "the game's folder isn't changed");
    }

    [Fact]
    public void A_batch_file_a_program_writes_is_read_when_it_runs()
    {
        // W.C.A.T.'s launcher writes ACTION.BAT and exits without closing it; DOS closes it, and
        // the batch file that ran the launcher then calls it:
        //   push cs; pop ds; mov ah, 3Ch; xor cx, cx; mov dx, name; int 21h
        //   mov bx, ax; mov ah, 40h; mov cx, 15; mov dx, data; int 21h; mov ax, 4C00h; int 21h
        Write("WRITER.EXE", DosTests.Exe(
        [
            0x0E, 0x1F, 0xB4, 0x3C, 0x31, 0xC9, 0xBA, 0x1C, 0x00, 0xCD, 0x21,
            0x89, 0xC3, 0xB4, 0x40, 0xB9, 0x0F, 0x00, 0xBA, 0x26, 0x00, 0xCD, 0x21,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
            .. "LATER.BAT\0"u8,
            .. "@echo written\r\n"u8,
        ]));
        Run("@echo off\r\nWRITER\r\nIF EXIST LATER.BAT CALL LATER.BAT\r\necho done\r\n");

        Assert.Equal(["console: written", "console: done"], Console());
        Assert.False(File.Exists(Path.Combine(_game, "LATER.BAT")));
    }

    [Fact]
    public void A_mounted_folder_is_a_directory_of_the_drive_that_is_never_written()
    {
        // as the player puts a game's own data where W.C.A.T. expects it, beside its launcher
        string data = Directory.CreateTempSubdirectory("excavate-data").FullName;
        try
        {
            File.WriteAllText(Path.Combine(data, "BRIEFING.VGA"), "x");
            Write("RUN.BAT", "@echo off\r\nCD GAMEWC1\r\nIF EXIST BRIEFING.VGA echo found\r\nDEL BRIEFING.VGA\r\nIF NOT EXIST BRIEFING.VGA echo gone\r\n"u8.ToArray());
            var machine = new Machine(_game, _saves) { Log = _log.Add };
            machine.Mount(@"\GAMEWC1", data);
            machine.Load("RUN.BAT");

            Assert.True(machine.Exited);
            Assert.Equal(["console: found", "console: gone"], Console());
            Assert.True(File.Exists(Path.Combine(data, "BRIEFING.VGA")));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_batch_file_with_no_program_ends_at_once()
    {
        Write("RUN.BAT", "@echo off\r\nNOWHERE\r\necho after\r\n"u8.ToArray());
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("RUN.BAT");

        Assert.True(machine.Exited);
        Assert.Equal(["console: Bad command or file name", "console: after"], Console());
    }

    [Fact]
    public void A_missing_label_ends_the_batch_file()
    {
        Run("@echo off\r\nGOTO :NOWHERE\r\necho skipped\r\n");

        Assert.Equal(["console: Label not found"], Console());
    }

    private Machine Run(string batch)
    {
        Write("RUN.BAT", System.Text.Encoding.ASCII.GetBytes(batch.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("RUN.BAT");
        machine.Run(1_000_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        return machine;
    }

    private void Write(string name, byte[] contents)
    {
        string path = Path.Combine(_game, name.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private string[] Console() => [.. _log.Where(line => line.StartsWith("console:", StringComparison.Ordinal))];
}
