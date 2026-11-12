namespace Excavate.Core.Tests;

/// <summary>
/// Text: the video BIOS's cursor, writing, reading and scrolling, DOS's console going to the
/// screen, the text modes drawn, and line input from the keyboard; on the VGA and the CGA.
/// </summary>
public sealed class TextTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    private Machine Load(VideoAdapter video, byte[] code)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), DosTests.Exe(code));
        var machine = new Machine(_game, _saves, video);
        machine.Load("TEST.EXE");
        return machine;
    }

    private Machine Run(VideoAdapter video, byte[] code, long instructions = 1_000_000)
    {
        var machine = Load(video, code);
        machine.Run(instructions);
        Assert.True(machine.Exited);
        return machine;
    }

    // the character and attribute of a cell of the first page of 80-column text
    private static (char Character, byte Attribute) Cell(Machine machine, int row, int column)
    {
        uint at = 0xB_8000 + (uint)(((row * 80) + column) * 2);
        return ((char)machine.Read(at), machine.Read(at + 1));
    }

    private static uint[] Picture(Machine machine)
    {
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        return pixels;
    }

    // the colors in a character cell of the picture, 8 dots across, 16 lines down on the VGA
    // (8 on the CGA, each drawn twice)
    private static HashSet<uint> CellColors(Machine machine, int row, int column)
    {
        var picture = Picture(machine);
        var colors = new HashSet<uint>();
        for (int y = row * 16; y < (row + 1) * 16; y++)
        {
            for (int x = column * 8; x < (column + 1) * 8; x++)
            {
                colors.Add(picture[(y * 640) + x] & 0xFF_FFFF);
            }
        }

        return colors;
    }

    [Theory]
    [InlineData(VideoAdapter.Vga)]
    [InlineData(VideoAdapter.Cga)]
    public void DOS_output_is_written_to_the_screen_and_drawn(VideoAdapter video)
    {
        // push cs; pop ds; mov dx, 0Eh; mov ah, 9; int 21h; mov ax, 4C00h; int 21h; "HI\r\nAB$"
        var machine = Run(video, [0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21, .. "HI\r\nAB$"u8]);

        Assert.Equal(('H', (byte)0x07), Cell(machine, 0, 0));
        Assert.Equal(('I', (byte)0x07), Cell(machine, 0, 1));
        Assert.Equal(('A', (byte)0x07), Cell(machine, 1, 0));
        Assert.Equal(('B', (byte)0x07), Cell(machine, 1, 1));

        // the cursor after it, in the BIOS's data: column 2 of row 1
        Assert.Equal(2, machine.Read(0x450));
        Assert.Equal(1, machine.Read(0x451));

        // light gray on black, in the font; the cell after the text is blank
        Assert.Equal([0x000000u, 0xAAAAAAu], CellColors(machine, 0, 0).Order());
        Assert.Equal([0x000000u], CellColors(machine, 0, 2));
    }

    [Theory]
    [InlineData(VideoAdapter.Vga)]
    [InlineData(VideoAdapter.Cga)]
    public void The_teletype_scrolls_the_screen_up_past_the_last_row(VideoAdapter video)
    {
        // the cursor to row 24 (mov ah, 2; mov bh, 0; mov dx, 1800h; int 10h), an X there
        // (mov ax, 0E58h; int 10h), and a line feed (mov ax, 0E0Ah; int 10h)
        var machine = Run(video,
        [
            0xB4, 0x02, 0xB7, 0x00, 0xBA, 0x00, 0x18, 0xCD, 0x10,
            0xB8, 0x58, 0x0E, 0xCD, 0x10,
            0xB8, 0x0A, 0x0E, 0xCD, 0x10,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(('X', (byte)0x07), Cell(machine, 23, 0));
        Assert.Equal((' ', (byte)0x07), Cell(machine, 24, 0));
        Assert.Equal(24, machine.Read(0x451));
    }

    [Theory]
    [InlineData(VideoAdapter.Vga)]
    [InlineData(VideoAdapter.Cga)]
    public void Characters_are_written_with_an_attribute_read_back_and_cleared_in_a_window(VideoAdapter video)
    {
        var machine = Run(video,
        [
            // three Zs in yellow on blue: mov ah, 9; mov al, 'Z'; mov bh, 0; mov bl, 1Eh; mov cx, 3; int 10h
            0xB4, 0x09, 0xB0, 0x5A, 0xB7, 0x00, 0xB3, 0x1E, 0xB9, 0x03, 0x00, 0xCD, 0x10,

            // the one at the cursor read back: mov ah, 8; mov bh, 0; int 10h; push ax
            0xB4, 0x08, 0xB7, 0x00, 0xCD, 0x10, 0x50,

            // the window of rows 0 and 1, columns 0 and 1, cleared in white on red:
            // mov ax, 0600h; mov bh, 4Fh; mov cx, 0; mov dx, 0101h; int 10h
            0xB8, 0x00, 0x06, 0xB7, 0x4F, 0xB9, 0x00, 0x00, 0xBA, 0x01, 0x01, 0xCD, 0x10,

            // exit with the attribute read: pop ax; mov al, ah; mov ah, 4Ch; int 21h
            0x58, 0x88, 0xE0, 0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0x1E, machine.ExitCode);
        Assert.Equal((' ', (byte)0x4F), Cell(machine, 0, 0));
        Assert.Equal((' ', (byte)0x4F), Cell(machine, 1, 1));
        Assert.Equal(('Z', (byte)0x1E), Cell(machine, 0, 2));
        Assert.Equal((' ', (byte)0x07), Cell(machine, 0, 3));

        // the Z drawn in yellow on blue
        Assert.Equal([0x0000AAu, 0xFFFF55u], CellColors(machine, 0, 2).Order());
    }

    [Fact]
    public void A_line_is_typed_with_backspace_and_kept_to_the_buffers_size()
    {
        // push cs; pop ds; mov dx, 10h; mov ah, 0Ah; int 21h; mov al, [11h]; mov ah, 4Ch; int 21h;
        // the buffer: room for five, Enter's carriage return among them
        var machine = Load(VideoAdapter.Vga,
        [
            0x0E, 0x1F, 0xBA, 0x10, 0x00, 0xB4, 0x0A, 0xCD, 0x21, 0xA0, 0x11, 0x00, 0xB4, 0x4C, 0xCD, 0x21,
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ]);
        machine.Run(100_000);

        // a, b, Backspace, then c, d, e and f, which there's no room for, and Enter
        foreach (byte key in new byte[] { 0x1E, 0x30, 0x0E, 0x2E, 0x20, 0x12, 0x21, 0x1C })
        {
            machine.PressKey(key);
            machine.Run(20_000);
            machine.ReleaseKey(key);
            machine.Run(20_000);
        }

        // the line's length, as the program exits with it, and the line echoed
        Assert.True(machine.Exited);
        Assert.Equal(4, machine.ExitCode);
        Assert.Equal("acde ", new string(Enumerable.Range(0, 5).Select(column => Cell(machine, 0, column).Character).ToArray()));
    }

    [Fact]
    public void A_timer_hook_that_changes_AX_leaves_the_program_it_interrupts_as_it_was()
    {
        // the hook, at 1Ah: mov ax, 0; iret; set as INT 1Ch with DS:DX (push cs; pop ds;
        // mov dx, 1Ah; mov ax, 251Ch; int 21h). Then AX is 4C2Ah through a long wait (mov si, 20;
        // again: mov cx, 0; loop $; dec si; jnz again), and int 21h exits with code 42 if no
        // tick changed it, or ends with 0 (function 0) if one did
        var machine = Run(VideoAdapter.Vga,
        [
            0x0E, 0x1F, 0xBA, 0x1A, 0x00, 0xB8, 0x1C, 0x25, 0xCD, 0x21,
            0xB8, 0x2A, 0x4C, 0xBE, 0x14, 0x00,
            0xB9, 0x00, 0x00, 0xE2, 0xFE, 0x4E, 0x75, 0xF8,
            0xCD, 0x21,
            0xB8, 0x00, 0x00, 0xCF,
        ],
        5_000_000);

        Assert.Equal(42, machine.ExitCode);
    }

    [Fact]
    public void The_tick_count_can_be_set_and_an_unknown_keyboard_function_changes_nothing()
    {
        // mov ah, 1; mov cx, 12h; mov dx, 3456h; int 1Ah; mov ax, 0A33h; int 16h; mov ah, 4Ch; int 21h
        var machine = Run(VideoAdapter.Vga,
        [
            0xB4, 0x01, 0xB9, 0x12, 0x00, 0xBA, 0x56, 0x34, 0xCD, 0x1A,
            0xB8, 0x33, 0x0A, 0xCD, 0x16, 0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0x33, machine.ExitCode);
        Assert.Equal(0x0012, machine.Read(0x46E) | (machine.Read(0x46F) << 8));
        Assert.InRange(machine.Read(0x46C) | (machine.Read(0x46D) << 8), 0x3456, 0x3460);
    }
}
