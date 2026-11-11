using Excavate.Core.Processors;

namespace Excavate.Core.Tests;

/// <summary>
/// The 386's protected mode, by small programs assembled by hand: each sets up descriptor tables
/// in memory, runs, and leaves its results at 5000h. The SingleStepTests suite is real mode's
/// alone, so these are the checks protected mode has.
/// </summary>
/// <remarks>
/// The global descriptor table, at 1000h, is the same for every test:
/// 08h flat 32-bit code and 10h flat data, both privilege 0; 18h and 20h the same in 16 bits
/// with a 64 KB limit; 28h and 30h flat code and data at privilege 3; 38h a 32-bit task state
/// segment at 3000h; 40h flat read-only data; 48h a 16-bit expand-down segment at 20000h whose
/// limit is FFFh; and 50h a data segment marked not present. The interrupt table is at 2000h.
/// </remarks>
public sealed class ProtectedModeTests
{
    private const uint Results = 0x5000;

    private readonly FlatBus _bus = new();

    public ProtectedModeTests()
    {
        byte[][] gdt =
        [
            new byte[8],
            Segment(0, 0xFFFF_FFFF, 0x9A, big: true),
            Segment(0, 0xFFFF_FFFF, 0x92, big: true),
            Segment(0, 0xFFFF, 0x9A, big: false),
            Segment(0, 0xFFFF, 0x92, big: false),
            Segment(0, 0xFFFF_FFFF, 0xFA, big: true),
            Segment(0, 0xFFFF_FFFF, 0xF2, big: true),
            Segment(0x3000, 0x67, 0x89, big: false),
            Segment(0, 0xFFFF_FFFF, 0x90, big: true),
            Segment(0x2_0000, 0x0FFF, 0x96, big: false),
            Segment(0, 0xFFFF, 0x12, big: false),
        ];
        for (int i = 0; i < gdt.Length; i++)
        {
            gdt[i].CopyTo(_bus.Memory, 0x1000 + (i * 8));
        }

        // the task's privilege-0 stack: SS0:ESP0 = 10h:80000h; no I/O permission bitmap
        Dword(0x3004, 0x8_0000);
        Dword(0x3008, 0x10);
        _bus.Memory[0x3066] = 0x68;
    }

    private const ushort GdtLimit = (11 * 8) - 1;

    private const ushort IdtLimit = (0x40 * 8) - 1;

    [Fact]
    public void A_program_switches_to_protected_mode_and_back()
    {
        _bus.Memory[0x0F00] = GdtLimit & 0xFF;
        _bus.Memory[0x0F01] = GdtLimit >> 8;
        Dword(0x0F02, 0x1000);
        Dword(0x20_0000, 0x600D_F00D);

        // real mode: load the table, set PE, and jump into 32-bit code
        Code(0x0600)
            .Op(0x0F, 0x01, 0x16).W(0x0F00)     // lgdt [0F00h]
            .Op(0x0F, 0x20, 0xC0)               // mov eax, cr0
            .Op(0x0C, 0x01)                     // or al, 1
            .Op(0x0F, 0x22, 0xC0)               // mov cr0, eax
            .Op(0x66, 0xEA).D(0x1_0000).W(0x08) // jmp dword 08h:10000h
            .Load(_bus);

        // 32-bit: flat data and a stack above 64 KB, so ESP is the stack pointer, not SP
        Code(0x1_0000)
            .Op(0x66, 0xB8).W(0x10)             // mov ax, 10h
            .Op(0x8E, 0xD8)                     // mov ds, ax
            .Op(0x8E, 0xD0)                     // mov ss, ax
            .Op(0x8E, 0xE0)                     // mov fs, ax
            .Op(0xBC).D(0x9_0000)               // mov esp, 90000h
            .Op(0x68).D(0xCAFE_BABE)            // push CAFEBABEh
            .Op(0xA1).D(0x8_FFFC)               // mov eax, [8FFFCh]
            .Op(0xA3).D(Results + 4)            // mov [5004h], eax
            .Op(0xEA).D(0x0800).W(0x18)         // jmp 18h:0800h, 16-bit code
            .Load(_bus);

        // 16-bit protected mode: clear PE, then a real-mode far jump
        Code(0x0800)
            .Op(0x0F, 0x20, 0xC0)               // mov eax, cr0
            .Op(0x24, 0xFE)                     // and al, FEh
            .Op(0x0F, 0x22, 0xC0)               // mov cr0, eax
            .Op(0xEA).W(0x0700).W(0x0000)       // jmp 0000h:0700h
            .Load(_bus);

        // real mode again, but FS keeps the 4 GB limit it had: an address past 1 MB works
        Code(0x0700)
            .Op(0x31, 0xC0)                     // xor ax, ax
            .Op(0x8E, 0xD8)                     // mov ds, ax
            .Op(0x64, 0x67, 0x66, 0xA1).D(0x20_0000) // mov eax, fs:[200000h]
            .Op(0x66, 0xA3).W((ushort)Results)  // mov [5000h], eax
            .Op(0xF4)                           // hlt
            .Load(_bus);

        var cpu = new Intel386(_bus) { Cs = 0, Eip = 0x0600 };
        RunToHalt(cpu);

        Assert.False(cpu.ProtectedMode);
        Assert.Equal(0xCAFE_BABEu, Result(4));
        Assert.Equal(0x600D_F00Du, Result(0));
        Assert.Equal(0x8_FFFCu, cpu.Esp);
    }

    public static TheoryData<string, byte[], int, uint, uint> Faults => new()
    {
        // a selector past the end of the table: the selector is the error code
        { "outside the table", [0x66, 0xB8, 0xF8, 0x01, 0x8E, 0xD8], 13, 0x1F8, 0x1_0004 },

        // writing through a read-only segment
        { "read-only", [0x66, 0xB8, 0x40, 0x00, 0x8E, 0xC0, 0x26, 0xA3, 0x00, 0x01, 0x00, 0x00], 13, 0, 0x1_0006 },

        // a descriptor not present
        { "not present", [0x66, 0xB8, 0x50, 0x00, 0x8E, 0xD8], 11, 0x50, 0x1_0004 },

        // an expand-down segment holds the offsets above its limit, not those below
        { "below expand-down", [0x66, 0xB8, 0x48, 0x00, 0x8E, 0xC0, 0x26, 0xA1, 0x00, 0x18, 0x00, 0x00, 0x26, 0xA1, 0x00, 0x08, 0x00, 0x00], 13, 0, 0x1_000C },

        // a stack at another privilege
        { "outer stack", [0x66, 0xB8, 0x30, 0x00, 0x8E, 0xD0], 13, 0x30, 0x1_0004 },

        // SS can't be null; DS can, but then can't be used
        { "null stack", [0x66, 0x31, 0xC0, 0x8E, 0xD0], 13, 0, 0x1_0003 },
        { "null data", [0x66, 0x31, 0xC0, 0x8E, 0xD8, 0xA1, 0x00, 0x01, 0x00, 0x00], 13, 0, 0x1_0005 },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void Faults_push_their_error_code_and_the_faulting_instruction(string name, byte[] program, int vector, uint errorCode, uint at)
    {
        // each handler notes its vector, the error code and the return address, through SS,
        // since DS may be the fault
        foreach (int handled in new[] { 11, 12, 13 })
        {
            uint handler = 0x1_1000 + ((uint)handled * 0x40);
            Gate(handled, 0x08, handler, 0x8E);
            Code(handler)
                .Op(0x36, 0xC7, 0x05).D(Results + 8).D((uint)handled) // mov dword ss:[5008h], vector
                .Op(0x58)                                             // pop eax: the error code
                .Op(0x36, 0xA3).D(Results)                            // mov ss:[5000h], eax
                .Op(0x58)                                             // pop eax: the return address
                .Op(0x36, 0xA3).D(Results + 4)                        // mov ss:[5004h], eax
                .Op(0xF4)
                .Load(_bus);
        }

        Code(0x1_0000).Op(program).Op(0xF4).Load(_bus);
        RunToHalt(Protected());

        Assert.True((uint)vector == Result(8), $"{name}: vector {Result(8)}");
        Assert.Equal(errorCode, Result(0));
        Assert.Equal(at, Result(4));
    }

    [Theory]
    [InlineData(0x8E, false)] // an interrupt gate turns interrupts off
    [InlineData(0x8F, true)]  // a trap gate leaves them
    public void An_interrupt_gate_turns_interrupts_off_and_IRETD_restores_them(byte gateAccess, bool onInside)
    {
        Gate(0x21, 0x08, 0x1_2000, gateAccess);
        Code(0x1_2000)
            .Op(0x9C, 0x58)                     // pushfd; pop eax
            .Op(0xA3).D(Results)                // mov [5000h], eax
            .Op(0xCF)                           // iretd
            .Load(_bus);
        Code(0x1_0000)
            .Op(0xFB)                           // sti
            .Op(0xCD, 0x21)                     // int 21h
            .Op(0x9C, 0x58)                     // pushfd; pop eax
            .Op(0xA3).D(Results + 4)            // mov [5004h], eax
            .Op(0xF4)
            .Load(_bus);
        RunToHalt(Protected());

        Assert.Equal(onInside, (Result(0) & 0x200) != 0);
        Assert.NotEqual(0u, Result(4) & 0x200);
    }

    [Fact]
    public void Privilege_drops_to_3_and_an_interrupt_gate_brings_it_back_on_the_inner_stack()
    {
        Gate(0x30, 0x08, 0x1_3000, 0xEE);   // privilege 3 may use it
        Gate(13, 0x08, 0x1_4000, 0x8E);

        // privilege 0: load the task register, then "return" to privilege 3
        Code(0x1_0000)
            .Op(0x66, 0xB8).W(0x38)             // mov ax, 38h
            .Op(0x0F, 0x00, 0xD8)               // ltr ax
            .Op(0x6A, 0x33)                     // push 33h: SS
            .Op(0x68).D(0x7_0000)               // push 70000h: ESP
            .Op(0x6A, 0x2B)                     // push 2Bh: CS
            .Op(0x68).D(0x1_5000)               // push 15000h: EIP
            .Op(0xCB)                           // retf
            .Load(_bus);

        // privilege 3: DS was privilege 0's, so the return made it null
        Code(0x1_5000)
            .Op(0x8C, 0xD8)                     // mov eax, ds
            .Op(0x36, 0xA3).D(Results)          // mov ss:[5000h], eax
            .Op(0xCD, 0x30)                     // int 30h
            .Op(0x36, 0xA3).D(Results + 0x10)   // mov ss:[5010h], eax
            .Op(0xFA)                           // cli: not at this privilege
            .Load(_bus);

        // the gate's handler, at privilege 0 on the task's stack, with the old SS:ESP on it
        Code(0x1_3000)
            .Op(0x36, 0x89, 0x25).D(Results + 4) // mov ss:[5004h], esp
            .Op(0x8B, 0x44, 0x24, 0x0C)         // mov eax, [esp+12]: the old ESP
            .Op(0x36, 0xA3).D(Results + 8)
            .Op(0x8B, 0x44, 0x24, 0x10)         // mov eax, [esp+16]: the old SS
            .Op(0x36, 0xA3).D(Results + 0xC)
            .Op(0xB8).D(0xDEAD_BEEF)            // mov eax, DEADBEEFh
            .Op(0xCF)                           // iretd, back to privilege 3
            .Load(_bus);

        // the CLI's general protection fault
        Code(0x1_4000)
            .Op(0x58)                           // pop eax: the error code
            .Op(0x36, 0xA3).D(Results + 0x14)
            .Op(0x8B, 0x44, 0x24, 0x04)         // mov eax, [esp+4]: the faulting CS
            .Op(0x36, 0xA3).D(Results + 0x18)
            .Op(0xF4)
            .Load(_bus);

        var cpu = Protected();
        RunToHalt(cpu);

        Assert.Equal(0u, Result(0));
        Assert.Equal(0x8_0000u - 20, Result(4));
        Assert.Equal(0x7_0000u, Result(8));
        Assert.Equal(0x33u, Result(0xC));
        Assert.Equal(0xDEAD_BEEFu, Result(0x10));
        Assert.Equal(0u, Result(0x14));
        Assert.Equal(0x2Bu, Result(0x18));
        Assert.Equal(0, cpu.Cpl);
    }

    [Fact]
    public void The_system_instructions_report_the_tables_and_descriptors()
    {
        Code(0x1_0000)
            .Op(0xB9).D(0x30)                   // mov ecx, 30h
            .Op(0x0F, 0x02, 0xC1)               // lar eax, ecx
            .Op(0xA3).D(Results)
            .Op(0x0F, 0x03, 0xC1)               // lsl eax, ecx
            .Op(0xA3).D(Results + 4)
            .Op(0xB9).D(0x1F8)                  // mov ecx, 1F8h: outside the table
            .Op(0x0F, 0x02, 0xC1)               // lar eax, ecx
            .Op(0x0F, 0x94, 0x05).D(Results + 8) // setz [5008h]
            .Op(0xB9).D(0x40)                   // mov ecx, 40h: read-only data
            .Op(0x0F, 0x00, 0xE1)               // verr cx
            .Op(0x0F, 0x94, 0x05).D(Results + 9)
            .Op(0x0F, 0x00, 0xE9)               // verw cx
            .Op(0x0F, 0x94, 0x05).D(Results + 0xA)
            .Op(0x66, 0xB9).W(0x10)             // mov cx, 10h
            .Op(0x66, 0xBA).W(0x03)             // mov dx, 3
            .Op(0x63, 0xD1)                     // arpl cx, dx
            .Op(0x0F, 0x94, 0x05).D(Results + 0xB)
            .Op(0x66, 0x89, 0x0D).D(Results + 0xC) // mov [500Ch], cx
            .Op(0x0F, 0x01, 0x05).D(Results + 0x10) // sgdt [5010h]
            .Op(0x0F, 0x01, 0x0D).D(Results + 0x18) // sidt [5018h]
            .Op(0x0F, 0x01, 0xE0)               // smsw eax
            .Op(0xA3).D(Results + 0x20)
            .Op(0xF4)
            .Load(_bus);
        RunToHalt(Protected());

        Assert.Equal(0x00CF_F200u, Result(0));
        Assert.Equal(0xFFFF_FFFFu, Result(4));
        Assert.Equal(0, _bus.Memory[Results + 8]);
        Assert.Equal(1, _bus.Memory[Results + 9]);
        Assert.Equal(0, _bus.Memory[Results + 0xA]);
        Assert.Equal(1, _bus.Memory[Results + 0xB]);
        Assert.Equal(0x13u, Result(0xC) & 0xFFFF);
        Assert.Equal((uint)GdtLimit, Result(0x10) & 0xFFFF);
        Assert.Equal(0x1000u, Result(0x12));
        Assert.Equal((uint)IdtLimit, Result(0x18) & 0xFFFF);
        Assert.Equal(0x2000u, Result(0x1A));
        Assert.Equal(0x11u, Result(0x20));
    }

    [Fact]
    public void Code_in_a_16_bit_segment_defaults_to_16_bits()
    {
        Code(0x1_0000).Op(0xEA).D(0x0800).W(0x18).Load(_bus); // jmp 18h:0800h
        Code(0x0800)
            .Op(0xB8).W(0xBEEF)                 // mov ax, BEEFh
            .Op(0xA3).W((ushort)(Results + 4))  // mov [5004h], ax
            .Op(0x66, 0xB8).D(0x1234_5678)      // mov eax, 12345678h
            .Op(0x66, 0xA3).W((ushort)Results)  // mov [5000h], eax
            .Op(0xF4)
            .Load(_bus);
        RunToHalt(Protected());

        Assert.Equal(0x1234_5678u, Result(0));
        Assert.Equal(0xBEEFu, Result(4) & 0xFFFF);
    }

    [Fact]
    public void ARPL_and_the_descriptor_instructions_are_invalid_in_real_mode()
    {
        // real mode's vector 6, in the table at 0, to 0000:0700h
        _bus.Memory[6 * 4] = 0x00;
        _bus.Memory[(6 * 4) + 1] = 0x07;
        Code(0x0600).Op(0x63, 0xD1).Load(_bus);   // arpl cx, dx
        Code(0x0700).Op(0xF4).Load(_bus);        // the invalid opcode's handler, at 0000:0700h
        var cpu = new Intel386(_bus) { Cs = 0, Eip = 0x0600, Ss = 0, Esp = 0x7000 };
        RunToHalt(cpu);
        Assert.Equal(6, cpu.LastFault);
    }

    // ---- the machine around the processor ----------------------------------------------------

    /// <summary>The processor in protected mode at privilege 0, flat 32-bit, at 10000h.</summary>
    private Intel386 Protected()
    {
        var cpu = new Intel386(_bus)
        {
            Gdt = (0x1000, GdtLimit),
            Idt = (0x2000, IdtLimit),
            Cr0 = 0x11,
        };
        cpu.Cs = 0x08;
        cpu.Ds = cpu.Es = cpu.Fs = cpu.Gs = cpu.Ss = 0x10;
        cpu.Esp = 0x9_0000;
        cpu.Eip = 0x1_0000;
        return cpu;
    }

    private static void RunToHalt(Intel386 cpu)
    {
        for (int i = 0; i < 10_000 && !cpu.Halted; i++)
        {
            cpu.Step();
        }

        Assert.True(cpu.Halted, $"still running at {cpu.Cs:X4}:{cpu.Eip:X8}, last fault {cpu.LastFault}");
    }

    private uint Result(uint offset) => BitConverter.ToUInt32(_bus.Memory, (int)(Results + offset));

    private void Dword(uint address, uint value) => BitConverter.GetBytes(value).CopyTo(_bus.Memory, (int)address);

    private void Gate(int vector, ushort selector, uint offset, byte access)
    {
        uint low = (offset & 0xFFFF) | ((uint)selector << 16);
        uint high = (offset & 0xFFFF_0000) | ((uint)access << 8);
        Dword(0x2000 + ((uint)vector * 8), low);
        Dword(0x2004 + ((uint)vector * 8), high);
    }

    /// <summary>A code or data segment's descriptor; a limit past 1 MB is counted in 4 KB pages.</summary>
    private static byte[] Segment(uint segmentBase, uint limit, byte access, bool big)
    {
        bool pages = limit > 0xF_FFFF;
        uint raw = pages ? limit >> 12 : limit;
        uint low = (raw & 0xFFFF) | ((segmentBase & 0xFFFF) << 16);
        uint high = ((segmentBase >> 16) & 0xFF) | ((uint)access << 8) | (raw & 0xF_0000)
            | (big ? 0x40_0000u : 0) | (pages ? 0x80_0000u : 0) | (segmentBase & 0xFF00_0000);
        return [.. BitConverter.GetBytes(low), .. BitConverter.GetBytes(high)];
    }

    private static Assembly Code(uint origin) => new(origin);

    /// <summary>Machine code, written byte by byte at an address.</summary>
    private sealed class Assembly(uint origin)
    {
        private readonly List<byte> _bytes = [];

        public Assembly Op(params byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Assembly W(ushort value) => Op(BitConverter.GetBytes(value));

        public Assembly D(uint value) => Op(BitConverter.GetBytes(value));

        public void Load(FlatBus bus) => _bytes.CopyTo(bus.Memory, (int)origin);
    }

    /// <summary>4 MB of memory, with nothing on the ports.</summary>
    private sealed class FlatBus : ICpuBus
    {
        public byte[] Memory { get; } = new byte[0x40_0000];

        public byte Read(uint address) => Memory[address];

        public void Write(uint address, byte value) => Memory[address] = value;

        public byte In(ushort port) => 0xFF;

        public void Out(ushort port, byte value)
        {
        }
    }
}
