namespace Excavate.Core.Tests;

/// <summary>
/// The DOS layer, with small programs assembled by hand: each is an MZ executable written to a
/// temporary drive C:, loaded and run as a game would be.
/// </summary>
public sealed class DosTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    [Fact]
    public void A_program_prints_and_exits_with_its_code()
    {
        // push cs; pop ds; mov dx, message; mov ah, 9; int 21h; mov ax, 4C07h; int 21h
        var machine = Start(
        [
            0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x07, 0x4C, 0xCD, 0x21,
            .. "Hello, Excavate\r\n$"u8,
        ]);

        Assert.Contains("console: Hello, Excavate", _log);
        Assert.Equal(7, machine.ExitCode);
    }

    [Fact]
    public void A_program_shrinks_its_block_and_allocates_another()
    {
        // a program gets all free memory, so it must give some back before it can allocate:
        //   mov ah, 4Ah; mov bx, 100h; int 21h; jc fail
        //   mov ah, 48h; mov bx, 1000h; int 21h; jc fail
        //   mov ax, 4C00h; int 21h
        //   fail: mov ax, 4C01h; int 21h
        var machine = Start(
        [
            0xB4, 0x4A, 0xBB, 0x00, 0x01, 0xCD, 0x21, 0x72, 0x0E,
            0xB4, 0x48, 0xBB, 0x00, 0x10, 0xCD, 0x21, 0x72, 0x05,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
            0xB8, 0x01, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0, machine.ExitCode);
    }

    [Fact]
    public void Writing_a_file_leaves_the_game_folder_untouched()
    {
        File.WriteAllText(Path.Combine(_game, "SAVE.DAT"), "original");

        //   push cs; pop ds
        //   mov ax, 3D01h; mov dx, name; int 21h     open SAVE.DAT for writing
        //   mov bx, ax; mov ah, 40h; mov cx, 3; mov dx, data; int 21h
        //   mov ah, 3Eh; int 21h; mov ax, 4C00h; int 21h
        var machine = Start(
        [
            0x0E, 0x1F,
            0xB8, 0x01, 0x3D, 0xBA, 0x1F, 0x00, 0xCD, 0x21,
            0x89, 0xC3, 0xB4, 0x40, 0xB9, 0x03, 0x00, 0xBA, 0x28, 0x00, 0xCD, 0x21,
            0xB4, 0x3E, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21,
            .. "SAVE.DAT\0"u8,
            .. "new"u8,
        ]);

        Assert.Equal(0, machine.ExitCode);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_game, "SAVE.DAT")));
        Assert.Equal("newginal", File.ReadAllText(Path.Combine(_saves, "SAVE.DAT")));
    }

    [Fact]
    public void The_time_set_is_the_BIOSs_tick_count()
    {
        // 13:45:00.00: mov cx, 0D2Dh; mov dx, 0; mov ah, 2Dh; int 21h; then read it back and
        // exit with the minutes: mov ah, 2Ch; int 21h; mov al, cl; mov ah, 4Ch; int 21h
        var machine = Start(
        [
            0xB9, 0x2D, 0x0D, 0xBA, 0x00, 0x00, 0xB4, 0x2D, 0xCD, 0x21,
            0xB4, 0x2C, 0xCD, 0x21, 0x88, 0xC8, 0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(45, machine.ExitCode);
        uint ticks = machine.Read(0x46C) | ((uint)machine.Read(0x46D) << 8) | ((uint)machine.Read(0x46E) << 16);
        Assert.InRange(ticks, (uint)(13.75 * 3600 * 18.2065) - 2, (uint)(13.75 * 3600 * 18.2065) + 2);
    }

    [Fact]
    public void The_date_set_moves_on_when_the_time_passes_midnight()
    {
        // June 22, 1996: mov cx, 1996; mov dx, 0616h; mov ah, 2Bh; int 21h; and 23:59:59.50:
        // mov cx, 173Bh; mov dx, 3B32h; mov ah, 2Dh; int 21h. Then a wait past midnight
        // (mov cx, 8000h; loop $), and exit with the day: mov ah, 2Ah; int 21h; mov al, dl;
        // mov ah, 4Ch; int 21h
        var machine = Start(
        [
            0xB9, 0xCC, 0x07, 0xBA, 0x16, 0x06, 0xB4, 0x2B, 0xCD, 0x21,
            0xB9, 0x3B, 0x17, 0xBA, 0x32, 0x3B, 0xB4, 0x2D, 0xCD, 0x21,
            0xB9, 0x00, 0x80, 0xE2, 0xFE,
            0xB4, 0x2A, 0xCD, 0x21, 0x88, 0xD0, 0xB4, 0x4C, 0xCD, 0x21,
        ],
        machine => machine.InstructionsPerSecond = 50_000);

        Assert.Equal(23, machine.ExitCode);
        Assert.Contains("21/2B date set to 1996-06-22", _log);
    }

    [Theory]
    [InlineData(0x2D, 0x1800, 0x0000)]  // 24:00
    [InlineData(0x2D, 0x0C3C, 0x0000)]  // 12:60
    [InlineData(0x2B, 1996, 0x021E)]    // February 30
    [InlineData(0x2B, 1979, 0x0101)]    // before DOS's first year
    public void A_date_or_time_that_isnt_one_is_turned_down(int function, int cx, int dx)
    {
        // mov cx, cx; mov dx, dx; mov ah, function; int 21h; mov ah, 4Ch; int 21h
        var machine = Start(
        [
            0xB9, (byte)cx, (byte)(cx >> 8), 0xBA, (byte)dx, (byte)(dx >> 8), 0xB4, (byte)function, 0xCD, 0x21,
            0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0xFF, machine.ExitCode);
    }

    [Fact]
    public void Netwares_shell_isnt_there_and_dos_answers_as_for_any_function_it_lacks()
    {
        // Guardians of Infinity, on C-Worthy, asks for NetWare's shell's version:
        //   mov ax, 0EA01h; int 21h; mov ah, 4Ch; int 21h      exits with what AL was left
        var machine = Start([0xB8, 0x01, 0xEA, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21]);

        Assert.Equal(0, machine.ExitCode);
    }

    [Fact]
    public void Without_a_multitasker_the_video_buffer_is_the_screen()
    {
        // it asks TopView's call for a buffer to write the screen in, given its own address:
        //   mov ah, 0FEh; mov di, 1234h; int 10h; mov ax, di; mov ah, 4Ch; int 21h
        var machine = Start([0xB4, 0xFE, 0xBF, 0x34, 0x12, 0xCD, 0x10, 0x89, 0xF8, 0xB4, 0x4C, 0xCD, 0x21]);

        Assert.Equal(0x34, machine.ExitCode);
    }

    [Theory]
    [InlineData("PHOTO00 .PCC", "PHOTO00.PCC")]
    [InlineData("MUG003  .PCC", "MUG003.PCC")]
    [InlineData("SA  ", "SA")]
    [InlineData(@"DATA\NOTE .  ", @"DATA\NOTE")]
    public void A_name_is_found_without_the_spaces_DOS_pads_it_with(string path, string found)
    {
        Assert.Equal(found, Dos.DosDrive.Join(Dos.DosDrive.Resolve(path, "")!));
    }

    [Fact]
    public void A_directory_is_made_in_the_save_folder_and_removed()
    {
        // Wing Commander III makes C:\WC3 for its settings and saves:
        //   push cs; pop ds
        //   mov ah, 39h; mov dx, name; int 21h; jc fail        made
        //   mov ah, 39h; mov dx, name; int 21h; jnc fail       not again:
        //   cmp ax, 5; jne fail                                access denied
        //   mov ah, 3Ah; mov dx, name; int 21h; jc fail        removed
        //   mov ax, 4C00h; int 21h
        //   fail: mov ax, 4C01h; int 21h
        var machine = Start(
        [
            0x0E, 0x1F,
            0xB4, 0x39, 0xBA, 0x2C, 0x00, 0xCD, 0x21, 0x72, 0x1C,
            0xB4, 0x39, 0xBA, 0x2C, 0x00, 0xCD, 0x21, 0x73, 0x13,
            0x3D, 0x05, 0x00, 0x75, 0x0E,
            0xB4, 0x3A, 0xBA, 0x2C, 0x00, 0xCD, 0x21, 0x72, 0x05,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
            0xB8, 0x01, 0x4C, 0xCD, 0x21,
            .. "C:\\WC3\0"u8,
        ]);

        Assert.Equal(0, machine.ExitCode);
        Assert.Contains(@"21/39 make directory C:\WC3: made", _log);
        Assert.False(Directory.Exists(Path.Combine(_saves, "WC3")));
        Assert.False(Directory.Exists(Path.Combine(_game, "WC3")));
    }

    [Fact]
    public void The_expanded_memory_manager_is_found_by_its_name()
    {
        //   mov ax, 3567h; int 21h            ES:BX = the INT 67h vector
        //   mov di, 0Ah; mov si, name; push cs; pop ds; mov cx, 8; repe cmpsb
        //   jne fail; mov ax, 4C00h; int 21h; fail: mov ax, 4C01h; int 21h
        var machine = Start(
        [
            0xB8, 0x67, 0x35, 0xCD, 0x21,
            0xBF, 0x0A, 0x00, 0xBE, 0x1E, 0x00, 0x0E, 0x1F, 0xB9, 0x08, 0x00, 0xF3, 0xA6,
            0x75, 0x05, 0xB8, 0x00, 0x4C, 0xCD, 0x21,
            0xB8, 0x01, 0x4C, 0xCD, 0x21,
            .. "EMMXXXX0"u8,
        ]);

        Assert.Equal(0, machine.ExitCode);
    }

    [Fact]
    public void A_key_pressed_reaches_the_program_through_the_bios()
    {
        // mov ah, 0; int 16h; mov ah, 4Ch; int 21h: exits with the character of the key
        var machine = Start([0xB4, 0x00, 0xCD, 0x16, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            // shift down, A down and up, shift up: the keyboard controller's IRQ 1, the BIOS's
            // handler and its buffer turn it into a capital A for INT 16h
            m.PressKey(0x2A);
            m.PressKey(0x1E);
            m.ReleaseKey(0x1E);
            m.ReleaseKey(0x2A);
        });

        Assert.Equal('A', machine.ExitCode);
    }

    [Fact]
    public void Dos_reads_a_key_through_the_bios_and_an_arrow_as_two()
    {
        // mov ah, 8; int 21h; mov ah, 4Ch; int 21h: the key's character is the exit code
        var machine = Start([0xB4, 0x08, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            m.PressKey(0x30);
            m.ReleaseKey(0x30);
        });
        Assert.Equal('b', machine.ExitCode);

        // the Up arrow of the AT's own block comes as a zero, then its scan code: mov ah, 7;
        // int 21h; mov bl, al; mov ah, 7; int 21h; or al, bl; mov ah, 4Ch; int 21h
        machine = Start([0xB4, 0x07, 0xCD, 0x21, 0x88, 0xC3, 0xB4, 0x07, 0xCD, 0x21, 0x08, 0xD8, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            m.PressKey(0x48, extended: true);
            m.ReleaseKey(0x48, extended: true);
        });
        Assert.Equal(0x48, machine.ExitCode);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0xFF)]
    public void Dos_says_whether_a_key_is_waiting(bool press, int answer)
    {
        // mov ah, 0Bh; int 21h; mov ah, 4Ch; int 21h
        var machine = Start([0xB4, 0x0B, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            if (press)
            {
                m.PressKey(0x1E);
                m.ReleaseKey(0x1E);
            }
        });

        Assert.Equal(answer, machine.ExitCode);
    }

    [Fact]
    public void The_bios_reports_the_keyboard_intercept_in_its_configuration()
    {
        // mov ah, 0C0h; int 15h; jc fail; mov al, es:[bx+5]; mov ah, 4Ch; int 21h;
        // fail: mov ax, 4CFFh; int 21h. The first feature byte is the exit code.
        var machine = Start([0xB4, 0xC0, 0xCD, 0x15, 0x72, 0x08, 0x26, 0x8A, 0x47, 0x05, 0xB4, 0x4C, 0xCD, 0x21, 0xB8, 0xFF, 0x4C, 0xCD, 0x21]);
        Assert.Equal(0x70, machine.ExitCode);
        Assert.NotEqual(0, machine.ExitCode & 0x10); // INT 09h calls INT 15h function 4Fh
    }

    [Fact]
    public void A_program_on_the_keyboard_intercept_can_keep_a_key_from_the_bios()
    {
        // hook INT 15h with a handler that keeps A (scan code 1Eh) and passes the rest on, then
        // read a key with INT 16h: A and then B are pressed, and B is the key the BIOS has
        //
        //   push cs; pop ds; mov dx, handler; mov ax, 2515h; int 21h
        //   mov ah, 0; int 16h; mov ah, 4Ch; int 21h
        // handler:
        //   cmp ah, 4Fh; jne pass; cmp al, 1Eh; jne pass; clc; retf 2
        // pass:
        //   stc; retf 2
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(
        [
            0x0E, 0x1F, 0xBA, 0x12, 0x00, 0xB8, 0x15, 0x25, 0xCD, 0x21,
            0xB4, 0x00, 0xCD, 0x16, 0xB4, 0x4C, 0xCD, 0x21,
            0x80, 0xFC, 0x4F, 0x75, 0x08, 0x3C, 0x1E, 0x75, 0x04, 0xF8, 0xCA, 0x02, 0x00,
            0xF9, 0xCA, 0x02, 0x00,
        ]));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        machine.Run(10_000);
        Assert.False(machine.Exited);

        machine.PressKey(0x1E);
        machine.ReleaseKey(0x1E);
        machine.Run(10_000);
        Assert.False(machine.Exited);

        machine.PressKey(0x30);
        machine.ReleaseKey(0x30);
        machine.Run(10_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal('b', machine.ExitCode);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x02)]
    public void The_mouse_driver_is_there_only_with_a_mouse(bool mouse, int buttons)
    {
        // xor bx, bx; xor ax, ax; int 33h; mov al, bl; mov ah, 4Ch; int 21h: the buttons it
        // reports are the exit code, none without a mouse
        var machine = Start([0x31, 0xDB, 0x31, 0xC0, 0xCD, 0x33, 0x88, 0xD8, 0xB4, 0x4C, 0xCD, 0x21], m => m.Mouse = mouse);
        Assert.Equal(buttons, machine.ExitCode);
    }

    [Theory]
    [InlineData(0xC8, 50)]  // mov al, cl: the column, in the driver's 640
    [InlineData(0xD0, 30)]  // mov al, dl: the row, moving half as fast as the column
    public void The_mouse_driver_follows_the_mouse(byte register, int expected)
    {
        // mov ax, 3; int 33h; mov al, cl (or dl); mov ah, 4Ch; int 21h
        var machine = Start([0xB8, 0x03, 0x00, 0xCD, 0x33, 0x88, register, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            m.Mouse = true;
            m.MoveMouse(-5000, -5000); // into the top left corner, which holds it
            m.MoveMouse(50, 60);
        });

        Assert.Equal(expected, machine.ExitCode);
    }

    [Fact]
    public void The_mouse_driver_counts_presses_and_motion()
    {
        // mov ax, 5; xor bx, bx; int 33h; mov al, bl; mov ah, 4Ch; int 21h: presses of the left
        // button since last asked
        var machine = Start([0xB8, 0x05, 0x00, 0x31, 0xDB, 0xCD, 0x33, 0x88, 0xD8, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            m.Mouse = true;
            for (int i = 0; i < 3; i++)
            {
                m.SetMouseButton(0, true);
                m.SetMouseButton(0, false);
            }
        });
        Assert.Equal(3, machine.ExitCode);

        // mov ax, 0Bh; int 33h; mov al, cl; mov ah, 4Ch; int 21h: the motion in mickeys, which
        // the driver's ratio doesn't touch
        machine = Start([0xB8, 0x0B, 0x00, 0xCD, 0x33, 0x88, 0xC8, 0xB4, 0x4C, 0xCD, 0x21], m =>
        {
            m.Mouse = true;
            m.MoveMouse(7, -3);
            m.MoveMouse(5, 0);
        });
        Assert.Equal(12, machine.ExitCode);
    }

    [Fact]
    public void The_mickeys_across_follow_the_area_the_program_sets()
    {
        // Wing Commander III's area, 1280 by 480:
        //   mov ax, 7; xor cx, cx; mov dx, 4FEh; int 33h; mov ax, 8; xor cx, cx; mov dx, 1DFh; int 33h
        //   mov ah, 4Ch; int 21h
        var machine = new Machine(_game, _saves) { Mouse = true };
        Assert.Equal((640, 400), machine.MickeysAcross);

        machine = Start(
            [0xB8, 0x07, 0x00, 0x31, 0xC9, 0xBA, 0xFE, 0x04, 0xCD, 0x33, 0xB8, 0x08, 0x00, 0x31, 0xC9, 0xBA, 0xDF, 0x01, 0xCD, 0x33, 0xB4, 0x4C, 0xCD, 0x21],
            m => m.Mouse = true);
        Assert.Equal((1279, 960), machine.MickeysAcross);
    }

    [Fact]
    public void A_repeated_string_instruction_takes_time_for_each_repetition()
    {
        // mov ax, 9000h; mov es, ax; xor di, di; mov cx, 4000; rep stosb; mov ah, 4Ch; int 21h:
        // a few instructions, but 4,000 repetitions, each a step of time, as DOSBox counts them
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(
            [0xB8, 0x00, 0x90, 0x8E, 0xC0, 0x31, 0xFF, 0xB9, 0xA0, 0x0F, 0xF3, 0xAA, 0xB4, 0x4C, 0xCD, 0x21]));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        machine.Run(2_000);
        Assert.False(machine.Exited);

        machine.Run(3_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal(4000u, machine.Cpu.Edi & 0xFFFF);
    }

    [Fact]
    public void The_second_interrupt_controller_lets_only_the_mouse_through()
    {
        // in al, 0A1h; mov ah, 4Ch; int 21h: its mask, with only IRQ 12's bit, 4, clear
        var machine = Start([0xE4, 0xA1, 0xB4, 0x4C, 0xCD, 0x21]);
        Assert.Equal(0xEF, machine.ExitCode);
    }

    [Theory]
    [InlineData(0x06, 0x02)]    // AL: the events, the left button's press
    [InlineData(0x1E, 0x01)]    // BL: the buttons held, the left
    [InlineData(0x0E, 50)]      // CL: the column
    public void The_mouse_driver_calls_the_programs_handler(byte register, int expected)
    {
        // as Wing Commander III has the driver tell it of the mouse, through a handler:
        //   push cs; pop es; mov dx, handler; mov cx, 2 (the left button's press); mov ax, 0Ch; int 33h; sti
        //   wait: mov al, cs:[seen]; or al, al; jz wait; mov ah, 4Ch; int 21h
        //   handler: mov cs:[seen], al (or bl, or cl); retf
        //   seen: db 0
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(
        [
            0x0E, 0x07, 0xBA, 0x1A, 0x00, 0xB9, 0x02, 0x00, 0xB8, 0x0C, 0x00, 0xCD, 0x33, 0xFB,
            0x2E, 0xA0, 0x20, 0x00, 0x08, 0xC0, 0x74, 0xF8, 0xB4, 0x4C, 0xCD, 0x21,
            0x2E, 0x88, register, 0x20, 0x00, 0xCB,
            0x00,
        ]));
        var machine = new Machine(_game, _saves) { Log = _log.Add, Mouse = true };
        machine.MoveMouse(-5000, -5000);
        machine.MoveMouse(50, 0);
        machine.Load("TEST.EXE");
        machine.Run(10_000);
        Assert.False(machine.Exited);

        // moving isn't among the events it asked for
        machine.MoveMouse(0, 4);
        machine.Run(10_000);
        Assert.False(machine.Exited);

        machine.SetMouseButton(0, true);
        machine.Run(10_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal(expected, machine.ExitCode);
    }

    [Fact]
    public void Waiting_for_a_key_works_through_a_far_call_with_interrupts_off()
    {
        // as Wing Commander's own INT 21h handler passes a call on to DOS's:
        //   cli; mov ah, 7; pushf; call far F000:0184 (DOS's stub); mov ah, 4Ch; int 21h
        // DOS waits for the key with interrupts on, and returns where the far call does
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(
        [
            0xFA, 0xB4, 0x07, 0x9C, 0x9A, 0x84, 0x01, 0x00, 0xF0, 0xB4, 0x4C, 0xCD, 0x21,
        ]));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        machine.Run(100_000);
        Assert.False(machine.Exited);

        machine.PressKey(0x1E); // A
        machine.Run(100_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal('a', machine.ExitCode);
    }

    [Fact]
    public void Polling_for_a_key_with_interrupts_off_still_gets_the_keyboards_interrupt()
    {
        // as Portal waits for a key:
        //   cli; again: mov ah, 1; int 16h; jz again; mov ah, 0; int 16h; mov ah, 4Ch; int 21h
        // IBM's BIOS turns interrupts on inside INT 16h, so the key comes in while it polls
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(
        [
            0xFA, 0xB4, 0x01, 0xCD, 0x16, 0x74, 0xFA, 0xB4, 0x00, 0xCD, 0x16, 0xB4, 0x4C, 0xCD, 0x21,
        ]));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        machine.Run(100_000);
        Assert.False(machine.Exited);

        machine.PressKey(0x30); // B
        machine.Run(100_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        Assert.Equal('b', machine.ExitCode);
    }

    private Machine Start(byte[] code, Action<Machine>? before = null)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe(code));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        before?.Invoke(machine);
        machine.Run(100_000);
        Assert.True(machine.Exited, string.Join('\n', _log));
        return machine;
    }

    /// <summary>
    /// An MZ executable around code that starts at its first byte: a two-paragraph header, no
    /// relocations, and a stack at the top of a 64 KB segment of its own.
    /// </summary>
    internal static byte[] Exe(byte[] code)
    {
        int total = 32 + code.Length;
        var exe = new byte[total];
        void Word(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(exe, at);
        exe[0] = (byte)'M';
        exe[1] = (byte)'Z';
        Word(0x02, total % 512);
        Word(0x04, (total + 511) / 512);
        Word(0x08, 2);                          // header paragraphs
        Word(0x0A, 0x2000);                     // at least 128 KB beyond the image
        Word(0x0C, 0xFFFF);                     // at most everything
        Word(0x0E, 0x1000);                     // SS, relative to the image
        Word(0x10, 0xFFFE);                     // SP
        Word(0x18, 0x1C);                       // where relocations would be
        code.CopyTo(exe, 32);
        return exe;
    }
}
