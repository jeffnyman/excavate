namespace Excavate.Core.Tests;

/// <summary>
/// The DPMI host, with small DPMI programs run as a game would be. Each starts in real mode, finds
/// the host with INT 2Fh function 1687h, gives it the memory it asks for, enters protected mode as
/// a 32-bit program, and ends with exit code 42 if what it checked held, or 255 at the first thing
/// that didn't. Their code stays 16-bit, in the segment the host gives them first, with 32-bit
/// registers and addresses where they're needed.
/// </summary>
/// <remarks>
/// The bytes were assembled with Keystone, an x86 assembler; each line has its instruction beside
/// it, and a note on what it's for.
/// </remarks>
public sealed class DpmiTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;
    private readonly List<string> _log = [];

    /// <summary>
    /// What every program starts with: the exits at fixed places, then the way into protected
    /// mode. Each program's own code follows it.
    /// </summary>
    private static readonly byte[] Prologue =
    [
        0xEB, 0x10,                         // jmp start
        // fail:
        0xB8, 0xFF, 0x4C,                   // mov ax, 0x4CFF    (exit 255)
        0xCD, 0x21,                         // int 0x21
        // done:
        0xB8, 0x2A, 0x4C,                   // mov ax, 0x4C2A    (exit 42)
        0xCD, 0x21,                         // int 0x21
        // entry:
        0x00, 0x00, 0x00, 0x00,             // .long 0
        // rmseg:
        0x00, 0x00,                         // .word 0
        // start:
        0xBB, 0x10, 0x20,                   // mov bx, 0x2010    (keep 128 KB for the program and its stack)
        0xB4, 0x4A,                         // mov ah, 0x4A
        0xCD, 0x21,                         // int 0x21
        0x0E,                               // push cs
        0x1F,                               // pop ds
        0x8C, 0x0E, 0x10, 0x00,             // mov [rmseg], cs
        0xB8, 0x87, 0x16,                   // mov ax, 0x1687    (is there a DPMI host?)
        0xCD, 0x2F,                         // int 0x2F
        0x09, 0xC0,                         // or ax, ax
        0x75, 0xDA,                         // jnz fail
        0x89, 0x3E, 0x0C, 0x00,             // mov [entry], di    (its entry point)
        0x8C, 0x06, 0x0E, 0x00,             // mov [entry+2], es
        0x89, 0xF3,                         // mov bx, si    (the memory it wants for itself)
        0xB4, 0x48,                         // mov ah, 0x48
        0xCD, 0x21,                         // int 0x21
        0x72, 0xCA,                         // jc fail
        0x8E, 0xC0,                         // mov es, ax
        0xB8, 0x01, 0x00,                   // mov ax, 1    (a 32-bit program)
        0xFF, 0x1E, 0x0C, 0x00,             // lcall [entry]
        0x72, 0xBF,                         // jc fail
    ];

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    [Fact]
    public void A_program_enters_protected_mode_at_level_3_and_asks_the_host_its_version()
    {
        // In protected mode at level 3; INT 31h answers DPMI 0.90 on a 386; and INT 10h, below 20h, still
        // reaches the BIOS in real mode, by way of a general protection fault the host turns into an
        // interrupt.
        Run(
        [
            0x8C, 0xC8,                         // mov ax, cs    (level 3)
            0x83, 0xE0, 0x03,                   // and ax, 3
            0x83, 0xF8, 0x03,                   // cmp ax, 3
            0x75, 0xB5,                         // jne fail
            0x0F, 0x01, 0xE0,                   // smsw ax    (in protected mode)
            0xA8, 0x01,                         // test al, 1
            0x74, 0xAE,                         // jz fail
            0xB8, 0x00, 0x04,                   // mov ax, 0x0400    (the host's version)
            0xCD, 0x31,                         // int 0x31
            0x72, 0xA7,                         // jc fail
            0x83, 0xF8, 0x5A,                   // cmp ax, 0x005A    (0.90)
            0x75, 0xA2,                         // jne fail
            0x80, 0xF9, 0x03,                   // cmp cl, 3    (a 386)
            0x75, 0x9D,                         // jne fail
            0xB4, 0x0F,                         // mov ah, 0x0F    (the video mode, from the BIOS in real mode)
            0xCD, 0x10,                         // int 0x10
            0x3C, 0x03,                         // cmp al, 3
            0x75, 0x95,                         // jne fail
            0xEB, 0x98,                         // jmp done
        ]);
    }

    [Fact]
    public void Extended_memory_is_reached_through_descriptors_the_program_sets_up()
    {
        // A block of extended memory, a descriptor based at it, and a flat 4 GB descriptor that sees
        // the same bytes through its linear address.
        Run(
        [
            0xB8, 0x01, 0x05,                   // mov ax, 0x0501    (64 KB of extended memory)
            0xBB, 0x01, 0x00,                   // mov bx, 1
            0x31, 0xC9,                         // xor cx, cx
            0xCD, 0x31,                         // int 0x31
            0x72, 0xB3,                         // jc fail
            0x89, 0x0E, 0xD5, 0x00,             // mov [lin], cx    (its linear address)
            0x89, 0x1E, 0xD7, 0x00,             // mov [lin+2], bx
            0x83, 0xFB, 0x10,                   // cmp bx, 0x10    (past the first megabyte)
            0x72, 0xA6,                         // jb fail
            0x31, 0xC0,                         // xor ax, ax    (two descriptors)
            0xB9, 0x02, 0x00,                   // mov cx, 2
            0xCD, 0x31,                         // int 0x31
            0x72, 0x9D,                         // jc fail
            0xA3, 0xD9, 0x00,                   // mov [sel], ax
            0x89, 0xC3,                         // mov bx, ax    (the first: based at the memory, 64 KB long)
            0x8B, 0x0E, 0xD7, 0x00,             // mov cx, [lin+2]
            0x8B, 0x16, 0xD5, 0x00,             // mov dx, [lin]
            0xB8, 0x07, 0x00,                   // mov ax, 0x0007
            0xCD, 0x31,                         // int 0x31
            0x72, 0x89,                         // jc fail
            0xB8, 0x08, 0x00,                   // mov ax, 0x0008
            0x31, 0xC9,                         // xor cx, cx
            0xBA, 0xFF, 0xFF,                   // mov dx, 0xFFFF
            0xCD, 0x31,                         // int 0x31
            0x66, 0x0F, 0x82, 0x78, 0xFF, 0xFF, 0xFF,// jc fail
            0x8E, 0x06, 0xD9, 0x00,             // mov es, [sel]
            0x26, 0xC7, 0x06, 0x34, 0x12, 0xEF, 0xBE,// mov word ptr es:[0x1234], 0xBEEF
            0xB8, 0x03, 0x00,                   // mov ax, 0x0003    (the second: the next selector, 4 GB from 0)
            0xCD, 0x31,                         // int 0x31
            0x03, 0x06, 0xD9, 0x00,             // add ax, [sel]
            0x89, 0xC3,                         // mov bx, ax
            0xA3, 0xDB, 0x00,                   // mov [flat], ax
            0xB8, 0x08, 0x00,                   // mov ax, 0x0008
            0xB9, 0xFF, 0xFF,                   // mov cx, 0xFFFF
            0xBA, 0xFF, 0xFF,                   // mov dx, 0xFFFF
            0xCD, 0x31,                         // int 0x31
            0x66, 0x0F, 0x82, 0x4D, 0xFF, 0xFF, 0xFF,// jc fail
            0x8E, 0x26, 0xDB, 0x00,             // mov fs, [flat]
            0x66, 0x8B, 0x1E, 0xD5, 0x00,       // mov ebx, [lin]
            0x64, 0x67, 0x81, 0xBB, 0x34, 0x12, 0x00, 0x00, 0xEF, 0xBE,// cmp word ptr fs:[ebx+0x1234], 0xBEEF
            0x66, 0x0F, 0x85, 0x33, 0xFF, 0xFF, 0xFF,// jne fail
            0x66, 0xE9, 0x32, 0xFF, 0xFF, 0xFF, // jmp done
            // lin:
            0x00, 0x00, 0x00, 0x00,             // .long 0
            // sel:
            0x00, 0x00,                         // .word 0
            // flat:
            0x00, 0x00,                         // .word 0
        ]);
    }

    [Fact]
    public void A_program_calls_dos_in_real_mode_through_the_host()
    {
        // Function 0300h: INT 21h function 30h in real mode, its registers in a structure.
        Run(
        [
            0xC7, 0x06, 0x82, 0x00, 0x00, 0x30, // mov word ptr [regs+0x1C], 0x3000    (AH = 30h, the DOS version)
            0x1E,                               // push ds
            0x07,                               // pop es
            0x66, 0xBF, 0x66, 0x00, 0x00, 0x00, // mov edi, regs
            0xB8, 0x00, 0x03,                   // mov ax, 0x0300    (INT 21h in real mode)
            0xBB, 0x21, 0x00,                   // mov bx, 0x21
            0x31, 0xC9,                         // xor cx, cx
            0xCD, 0x31,                         // int 0x31
            0x72, 0xA5,                         // jc fail
            0x80, 0x3E, 0x82, 0x00, 0x05,       // cmp byte ptr [regs+0x1C], 5    (DOS 5)
            0x75, 0x9E,                         // jne fail
            0xEB, 0xA1,                         // jmp done
            // regs:
            .. new byte[50],                    // the real-mode registers: EDI to EAX, the flags, ES to GS, CS:IP and SS:SP
        ]);
    }

    [Fact]
    public void Timer_interrupts_reach_the_programs_handler_in_either_mode()
    {
        // A handler for IRQ 0 counts ticks while the program waits with HLT, which level 3 may not run
        // (the host waits for it), and while it runs a real-mode procedure that waits too: those
        // interrupts come up from real mode to the handler. It passes each on to the host's default,
        // which reflects it down to the BIOS, which ends it.
        Run(TimerProgram);
    }

    [Fact]
    public void A_state_saved_while_the_host_is_down_in_real_mode_carries_on()
    {
        // the timer program saved while its real-mode procedure waits: the host has its saved
        // protected-mode state, the call it's making, and the interrupt vectors' stubs. A new
        // machine loading the state finishes the program
        var machine = Start(TimerProgram);
        while (!machine.Exited && !(_log.Exists(line => line.StartsWith("31/0301", StringComparison.Ordinal)) && machine.Cpu.Halted))
        {
            machine.Run(1_000);
        }

        Assert.False(machine.Exited);
        Assert.False(machine.Cpu.ProtectedMode);
        var state = new MemoryStream();
        machine.SaveState(state);
        state.Position = 0;

        var loaded = new Machine(_game, _saves) { Log = _log.Add };
        loaded.LoadState(state);
        Finish(loaded);
    }

    /// <summary>
    /// The timer program: its handler counts the timer's interrupts, first while it waits in
    /// protected mode, then while a real-mode procedure it calls waits.
    /// </summary>
    private static readonly byte[] TimerProgram =
    [
        0x8C, 0x1E, 0xDF, 0x00,             // mov [dsel], ds
        0xB8, 0x04, 0x02,                   // mov ax, 0x0204    (the timer's handler, to pass interrupts on to)
        0xB3, 0x08,                         // mov bl, 8
        0xCD, 0x31,                         // int 0x31
        0x66, 0x89, 0x16, 0xE5, 0x00,       // mov [old], edx
        0x89, 0x0E, 0xE9, 0x00,             // mov [old+4], cx
        0xB8, 0x05, 0x02,                   // mov ax, 0x0205    (and ours in its place)
        0xB3, 0x08,                         // mov bl, 8
        0x8C, 0xC9,                         // mov cx, cs
        0x66, 0xBA, 0xC8, 0x00, 0x00, 0x00, // mov edx, handler
        0xCD, 0x31,                         // int 0x31
        0x72, 0x9A,                         // jc fail
        0xFB,                               // sti
        // wait:
        0xF4,                               // hlt    (three ticks in protected mode)
        0x83, 0x3E, 0xE1, 0x00, 0x03,       // cmp word ptr [ticks], 3
        0x72, 0xF8,                         // jb wait
        0xA1, 0xE1, 0x00,                   // mov ax, [ticks]
        0xA3, 0xE3, 0x00,                   // mov [before], ax
        0xA1, 0x10, 0x00,                   // mov ax, [rmseg]    (then a real-mode procedure that waits for four interrupts)
        0xA3, 0x17, 0x01,                   // mov [regs+0x2C], ax
        0xC7, 0x06, 0x15, 0x01, 0xD9, 0x00, // mov word ptr [regs+0x2A], rmproc
        0xC7, 0x06, 0x0B, 0x01, 0x02, 0x02, // mov word ptr [regs+0x20], 0x0202
        0x1E,                               // push ds
        0x07,                               // pop es
        0x66, 0xBF, 0xEB, 0x00, 0x00, 0x00, // mov edi, regs
        0xB8, 0x01, 0x03,                   // mov ax, 0x0301
        0x31, 0xDB,                         // xor bx, bx
        0x31, 0xC9,                         // xor cx, cx
        0xCD, 0x31,                         // int 0x31
        0x66, 0x0F, 0x82, 0x61, 0xFF, 0xFF, 0xFF,// jc fail
        0xA1, 0xE1, 0x00,                   // mov ax, [ticks]    (the ticks meanwhile came up to the handler)
        0x2B, 0x06, 0xE3, 0x00,             // sub ax, [before]
        0x83, 0xF8, 0x03,                   // cmp ax, 3
        0x66, 0x0F, 0x82, 0x50, 0xFF, 0xFF, 0xFF,// jb fail
        0xB8, 0x05, 0x02,                   // mov ax, 0x0205    (the old handler back)
        0xB3, 0x08,                         // mov bl, 8
        0x8B, 0x0E, 0xE9, 0x00,             // mov cx, [old+4]
        0x66, 0x8B, 0x16, 0xE5, 0x00,       // mov edx, [old]
        0xCD, 0x31,                         // int 0x31
        0x66, 0xE9, 0x3F, 0xFF, 0xFF, 0xFF, // jmp done
        // handler:
        0x1E,                               // push ds    (the handler: count, and pass the interrupt on)
        0x2E, 0x8E, 0x1E, 0xDF, 0x00,       // mov ds, cs:[dsel]
        0xFF, 0x06, 0xE1, 0x00,             // inc word ptr [ticks]
        0x1F,                               // pop ds
        0x2E, 0x66, 0xFF, 0x2E,             // .byte 0x2E, 0x66, 0xFF, 0x2E    (jmp far cs:[old], a 32-bit pointer)
        0xE5, 0x00,                         // .word old
        // rmproc:
        0xFB,                               // sti    (in real mode)
        0xF4,                               // hlt
        0xF4,                               // hlt
        0xF4,                               // hlt
        0xF4,                               // hlt
        0xCB,                               // .byte 0xCB    (retf)
        // dsel:
        0x00, 0x00,                         // .word 0
        // ticks:
        0x00, 0x00,                         // .word 0
        // before:
        0x00, 0x00,                         // .word 0
        // old:
        0x00, 0x00, 0x00, 0x00,             // .long 0
        0x00, 0x00,                         // .word 0
        // regs:
        .. new byte[50],                    // the real-mode registers: EDI to EAX, the flags, ES to GS, CS:IP and SS:SP
    ];

    [Fact]
    public void A_programs_exception_handler_gets_the_fault_and_resumes_past_it()
    {
        // A general protection fault, from loading a selector past the end of the global table, goes
        // to the program's handler with the selector as its error code; the handler moves the return
        // address past the instruction.
        Run(
        [
            0x8C, 0x1E, 0x8A, 0x00,             // mov [dsel], ds
            0xB8, 0x03, 0x02,                   // mov ax, 0x0203    (a handler for general protection faults)
            0xB3, 0x0D,                         // mov bl, 0x0D
            0x8C, 0xC9,                         // mov cx, cs
            0x66, 0xBA, 0x6E, 0x00, 0x00, 0x00, // mov edx, handler
            0xCD, 0x31,                         // int 0x31
            0x72, 0xAA,                         // jc fail
            0xB8, 0x30, 0x12,                   // mov ax, 0x1230    (a selector past the end of the global table)
            0x8E, 0xC0,                         // mov es, ax
            0x83, 0x3E, 0x8C, 0x00, 0x01,       // cmp word ptr [faults], 1    (the handler saw it, and the error code named the selector)
            0x75, 0x9E,                         // jne fail
            0x81, 0x3E, 0x8E, 0x00, 0x30, 0x12, // cmp word ptr [error], 0x1230
            0x75, 0x96,                         // jne fail
            0xEB, 0x99,                         // jmp done
            // handler:
            0x1E,                               // push ds    (the handler, on the host's stack:)
            0x2E, 0x8E, 0x1E, 0x8A, 0x00,       // mov ds, cs:[dsel]
            0xFF, 0x06, 0x8C, 0x00,             // inc word ptr [faults]
            0x67, 0x8B, 0x44, 0x24, 0x0A,       // mov ax, [esp+2+8]    (the error code, past DS and the return address)
            0xA3, 0x8E, 0x00,                   // mov [error], ax
            0x1F,                               // pop ds
            0x67, 0x66, 0x83, 0x44, 0x24, 0x0C, 0x02,// add dword ptr [esp+0x0C], 2    (on past the two-byte MOV ES)
            0x66, 0xCB,                         // .byte 0x66, 0xCB    (retf, 32-bit, to the host)
            // dsel:
            0x00, 0x00,                         // .word 0
            // faults:
            0x00, 0x00,                         // .word 0
            // error:
            0x00, 0x00,                         // .word 0
        ]);
    }

    [Fact]
    public void Real_mode_code_calls_back_into_the_program()
    {
        // A real-mode callback (function 0303h), called far by real-mode code, runs the program's
        // procedure in protected mode with the real-mode registers, and returns with the ones it set.
        Run(
        [
            0x1E,                               // push ds    (a real-mode callback to the procedure below)
            0x07,                               // pop es
            0x66, 0xBF, 0xD5, 0x00, 0x00, 0x00, // mov edi, cbregs
            0x1E,                               // push ds
            0x0E,                               // push cs
            0x1F,                               // pop ds
            0x66, 0xBE, 0x80, 0x00, 0x00, 0x00, // mov esi, procedure
            0xB8, 0x03, 0x03,                   // mov ax, 0x0303
            0xCD, 0x31,                         // int 0x31
            0x1F,                               // pop ds
            0x72, 0xA6,                         // jc fail
            0x89, 0x0E, 0xCF, 0x00,             // mov [regs+0x2C], cx    (called far from real mode)
            0x89, 0x16, 0xCD, 0x00,             // mov [regs+0x2A], dx
            0x1E,                               // push ds
            0x07,                               // pop es
            0x66, 0xBF, 0xA3, 0x00, 0x00, 0x00, // mov edi, regs
            0xB8, 0x01, 0x03,                   // mov ax, 0x0301
            0x31, 0xDB,                         // xor bx, bx
            0x31, 0xC9,                         // xor cx, cx
            0xCD, 0x31,                         // int 0x31
            0x72, 0x8B,                         // jc fail
            0x83, 0x3E, 0xBF, 0x00, 0x77,       // cmp word ptr [regs+0x1C], 0x77    (with the AX the procedure gave it)
            0x75, 0x84,                         // jne fail
            0xEB, 0x87,                         // jmp done
            // procedure:
            0x67, 0x8B, 0x06,                   // mov ax, [esi]    (DS:ESI the real-mode stack, ES:EDI the registers)
            0x26, 0x67, 0x89, 0x47, 0x2A,       // mov es:[edi+0x2A], ax    (returning as RETF would)
            0x67, 0x8B, 0x46, 0x02,             // mov ax, [esi+2]
            0x26, 0x67, 0x89, 0x47, 0x2C,       // mov es:[edi+0x2C], ax
            0x26, 0x67, 0x83, 0x47, 0x2E, 0x04, // add word ptr es:[edi+0x2E], 4
            0x26, 0x67, 0x66, 0xC7, 0x47, 0x1C, 0x77, 0x00, 0x00, 0x00,// mov dword ptr es:[edi+0x1C], 0x77
            0x66, 0xCF,                         // iretd
            // regs:
            .. new byte[50],                    // the real-mode registers: EDI to EAX, the flags, ES to GS, CS:IP and SS:SP
            // cbregs:
            .. new byte[50],                    // the real-mode registers: EDI to EAX, the flags, ES to GS, CS:IP and SS:SP
        ]);
    }

    /// <summary>Runs the prologue and a program's own code, which must exit with 42.</summary>
    private void Run(byte[] code) => Finish(Start(code));

    private Machine Start(byte[] code)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), Exe([.. Prologue, .. code]));
        var machine = new Machine(_game, _saves) { Log = _log.Add };
        machine.Load("TEST.EXE");
        return machine;
    }

    private void Finish(Machine machine)
    {
        machine.Run(20_000_000);
        string log = string.Join(Environment.NewLine, _log);
        Assert.True(machine.Exited && machine.ExitCode == 42, $"exit {machine.ExitCode}, exited {machine.Exited}: {log}");
        Assert.Contains("DPMI: the program has ended", _log);
    }

    private static byte[] Exe(byte[] code)
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
