using Excavate.Core.Processors;

namespace Excavate.Core.Dpmi;

// INT 31h, the DPMI services: descriptors, memory, calls down to real mode and back, and the
// interrupt vectors. After the DPMI 0.9 specification; the error codes are DPMI 1.0's, which
// 0.9 programs ignore.
internal sealed partial class DpmiHost
{
    private static class Error
    {
        public const ushort Unsupported = 0x8001;
        public const ushort DescriptorUnavailable = 0x8011;
        public const ushort LinearMemoryUnavailable = 0x8012;
        public const ushort CallbackUnavailable = 0x8015;
        public const ushort InvalidValue = 0x8021;
        public const ushort InvalidSelector = 0x8022;
        public const ushort InvalidHandle = 0x8023;
        public const ushort InvalidCallback = 0x8024;
    }

    /// <summary>A block of extended memory the program allocated: its handle, linear address and size.</summary>
    private sealed class MemoryBlock
    {
        public uint Handle;
        public uint Address;
        public uint Size;
    }

    // the extended memory blocks, in address order, and the next handle
    private readonly List<MemoryBlock> _blocks = [];
    private uint _nextHandle = 1;

    // each conventional memory block's first selector, with its segment and how many selectors
    private readonly Dictionary<ushort, (ushort Segment, int Count)> _dosBlocks = [];

    private ushort Ax => (ushort)Cpu.Eax;

    private ushort Bx => (ushort)Cpu.Ebx;

    private ushort Cx => (ushort)Cpu.Ecx;

    private ushort Dx => (ushort)Cpu.Edx;

    private byte Bl => (byte)Cpu.Ebx;

    private void SetAx(uint value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | (value & 0xFFFF);

    private void SetBx(uint value) => Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | (value & 0xFFFF);

    private void SetCx(uint value) => Cpu.Ecx = (Cpu.Ecx & 0xFFFF_0000) | (value & 0xFFFF);

    private void SetDx(uint value) => Cpu.Edx = (Cpu.Edx & 0xFFFF_0000) | (value & 0xFFFF);

    private void SetSi(uint value) => Cpu.Esi = (Cpu.Esi & 0xFFFF_0000) | (value & 0xFFFF);

    private void SetDi(uint value) => Cpu.Edi = (Cpu.Edi & 0xFFFF_0000) | (value & 0xFFFF);

    /// <summary>An offset register as the program uses it: all 32 bits for a 32-bit program, the low 16 otherwise.</summary>
    private uint Offset(uint register) => _client32 ? register : register & 0xFFFF;

    /// <summary>Sets an offset register, all of it for a 32-bit program.</summary>
    private uint SetOffset(uint register, uint value) => _client32 ? value : (register & 0xFFFF_0000) | (value & 0xFFFF);

    /// <summary>The linear address of ES:(E)DI.</summary>
    private uint EsDi => Cpu.SegmentBase(Intel386.ES) + Offset(Cpu.Edi);

    /// <summary>INT 31h, from its default handler. Carry clear for success; set, with an error code in AX, for failure.</summary>
    private void Services()
    {
        ushort function = Ax;
        ushort? error = function switch
        {
            0x0000 => AllocateDescriptors(),
            0x0001 => FreeDescriptor(),
            0x0002 => SegmentToDescriptor(),
            0x0003 => Done(() => SetAx(8)),
            0x0004 or 0x0005 => null,
            0x0006 => GetBase(),
            0x0007 => SetBase(),
            0x0008 => SetLimit(),
            0x0009 => SetAccessRights(),
            0x000A => CreateAlias(),
            0x000B => GetDescriptor(),
            0x000C => SetDescriptor(),
            0x000D => AllocateSpecificDescriptor(),
            0x0100 => AllocateDosMemory(),
            0x0101 => FreeDosMemory(),
            0x0102 => ResizeDosMemory(),
            0x0200 => Done(() => (Cpu.Ecx, Cpu.Edx) = SplitRealVector(Bl)),
            0x0201 => Done(() => SetRealVector(Bl, ((uint)Cx << 16) | Dx)),
            0x0202 => GetExceptionHandler(),
            0x0203 => SetExceptionHandler(),
            0x0204 => Done(() => SetPointer(_vectors[Bl])),
            0x0205 => Done(() => SetVector(Bl, new FarPointer(Cx, Offset(Cpu.Edx)))),
            0x0300 or 0x0301 or 0x0302 => CallRealMode(function),
            0x0303 => AllocateCallback(),
            0x0304 => FreeCallback(),
            0x0305 => Done(StateSaveAddresses),
            0x0306 => Done(RawSwitchAddresses),
            0x0400 => Done(Version),
            0x0500 => Done(FreeMemoryInformation),
            0x0501 => AllocateMemory(),
            0x0502 => FreeMemory(),
            0x0503 => ResizeMemory(),
            0x0600 or 0x0601 or 0x0602 or 0x0603 or 0x0702 or 0x0703 or 0x0801 => null,
            0x0604 => Done(() => { SetBx(0); SetCx(0x1000); }),
            0x0800 => null,
            0x0900 or 0x0901 or 0x0902 => Done(() => VirtualInterrupts(function)),
            _ => Error.Unsupported,
        };

        if (function is >= 0x0300 and <= 0x0302 && error is null)
        {
            // gone down to real mode: the carry is set when it comes back
            return;
        }

        if (function != 0x0400 && function is not (>= 0x0900 and <= 0x0902))
        {
            machine.Trace($"31/{function:X4} bx {Bx:X4} cx {Cx:X4} dx {Dx:X4}: {(error is { } e ? $"failed, {e:X4}h" : "done")}");
        }

        if (error is { } code)
        {
            SetAx(code);
        }

        SetReturnCarry(error is not null);
    }

    private static ushort? Done(Action action)
    {
        action();
        return null;
    }

    private void SetPointer(FarPointer pointer)
    {
        SetCx(pointer.Selector);
        Cpu.Edx = SetOffset(Cpu.Edx, pointer.Offset);
    }

    private (uint Cx, uint Dx) SplitRealVector(int vector)
    {
        uint value = RealVector(vector);
        return ((Cpu.Ecx & 0xFFFF_0000) | (value >> 16), (Cpu.Edx & 0xFFFF_0000) | (value & 0xFFFF));
    }

    /// <summary>Function 0400h: DPMI 0.90, 32-bit programs, a 386, and the interrupt controllers' vectors as real mode has them.</summary>
    private void Version()
    {
        SetAx(0x005A);
        SetBx(0x0001);
        Cpu.Ecx = (Cpu.Ecx & 0xFFFF_FF00) | 0x03;
        SetDx(0x0870);
    }

    /// <summary>Functions 0900h to 0902h: the interrupt flag the program will return with, cleared, set or read; AL is what it was.</summary>
    private void VirtualInterrupts(ushort function)
    {
        uint at = FrameFlags();
        uint flags = ReadFrameValue(at);
        Cpu.Eax = (Cpu.Eax & 0xFFFF_FF00) | ((flags & InterruptFlag) != 0 ? 1u : 0u);
        if (function == 0x0900)
        {
            WriteFrameValue(at, flags & ~InterruptFlag);
        }
        else if (function == 0x0901)
        {
            WriteFrameValue(at, flags | InterruptFlag);
        }
    }

    // ---- descriptors -------------------------------------------------------------------------

    private static ushort SelectorOf(int index) => (ushort)((index << 3) | 4 | 3);

    private static uint LdtEntry(int index) => HostBase + LdtAt + ((uint)index * 8);

    /// <summary>The index of one of the program's selectors in the local table, if it's one the host gave out.</summary>
    private int? IndexOf(ushort selector)
    {
        int index = selector >> 3;
        return (selector & 4) != 0 && index < LdtEntries && _ldtUsed[index] ? index : null;
    }

    /// <summary>Takes the first run of free descriptors long enough, past the reserved ones; null if there's none.</summary>
    private int? TakeDescriptors(int count)
    {
        int run = 0;
        for (int index = ReservedDescriptors; index < LdtEntries; index++)
        {
            run = _ldtUsed[index] ? 0 : run + 1;
            if (run == count)
            {
                int first = index - count + 1;
                for (int i = first; i <= index; i++)
                {
                    _ldtUsed[i] = true;
                }

                return first;
            }
        }

        return null;
    }

    /// <summary>A new descriptor for the program, at level 3; its selector.</summary>
    private ushort NewDescriptor(uint baseAddress, uint limit, byte access, bool big)
    {
        int index = TakeDescriptors(1) ?? throw new NotSupportedException("The DPMI host's local descriptor table is full.");
        WriteDescriptor(LdtEntry(index), baseAddress, limit, access, big ? (byte)0x40 : (byte)0);
        return SelectorOf(index);
    }

    /// <summary>
    /// Writes a segment descriptor: a limit past 1 MB is counted in 4 KB pages, setting the
    /// granularity bit, and the flags' high nibble gives the big and available bits.
    /// </summary>
    private void WriteDescriptor(uint at, uint baseAddress, uint limit, byte access, byte flags)
    {
        if (limit > 0xF_FFFF)
        {
            limit >>= 12;
            flags |= 0x80;
        }

        Write32(at, (baseAddress << 16) | (limit & 0xFFFF));
        Write32(at + 4, (baseAddress & 0xFF00_0000) | ((baseAddress >> 16) & 0xFF) | ((uint)access << 8) | (limit & 0xF_0000) | ((uint)(flags & 0xF0) << 16));
    }

    private void WriteGate(uint at, ushort selector, uint offset, int dpl, bool gate32)
    {
        uint type = gate32 ? 0xEu : 0x6u;
        Write32(at, ((uint)selector << 16) | (offset & 0xFFFF));
        Write32(at + 4, (offset & 0xFFFF_0000) | 0x8000 | ((uint)dpl << 13) | (type << 8));
    }

    /// <summary>Where a selector's descriptor is, in the global table or the local one.</summary>
    private static uint DescriptorAt(ushort selector)
        => ((selector & 4) != 0 ? HostBase + LdtAt : HostBase + GdtAt) + (uint)(selector & 0xFFF8);

    private uint DescriptorBase(ushort selector)
    {
        uint at = DescriptorAt(selector);
        return (Read32(at) >> 16) | ((uint)machine.Read(at + 4) << 16) | ((uint)machine.Read(at + 7) << 24);
    }

    private bool DescriptorBig(ushort selector) => (machine.Read(DescriptorAt(selector) + 6) & 0x40) != 0;

    private uint DescriptorLimit(int index)
    {
        uint at = LdtEntry(index);
        uint limit = Read16(at) | ((uint)(machine.Read(at + 6) & 0x0F) << 16);
        return (machine.Read(at + 6) & 0x80) != 0 ? (limit << 12) | 0xFFF : limit;
    }

    private void SetDescriptorBase(int index, uint baseAddress)
    {
        uint at = LdtEntry(index);
        Write16(at + 2, baseAddress);
        machine.Write(at + 4, (byte)(baseAddress >> 16));
        machine.Write(at + 7, (byte)(baseAddress >> 24));
    }

    private void SetDescriptorLimit(int index, uint limit)
    {
        uint at = LdtEntry(index);
        byte flags = (byte)(machine.Read(at + 6) & 0x70);
        if (limit > 0xF_FFFF)
        {
            limit >>= 12;
            flags |= 0x80;
        }

        Write16(at, limit);
        machine.Write(at + 6, (byte)(flags | ((limit >> 16) & 0x0F)));
    }

    /// <summary>
    /// After a descriptor changes, the segment registers holding it load it again, as DPMI hosts
    /// do, so the program sees the change at once; one that no longer loads is made null.
    /// </summary>
    private void Reload(ushort selector)
    {
        foreach (int segment in (ReadOnlySpan<int>)[Intel386.DS, Intel386.ES, Intel386.FS, Intel386.GS, Intel386.SS])
        {
            ushort held = segment switch
            {
                Intel386.DS => Cpu.Ds,
                Intel386.ES => Cpu.Es,
                Intel386.FS => Cpu.Fs,
                Intel386.GS => Cpu.Gs,
                _ => Cpu.Ss,
            };

            if ((held & 0xFFFC) == (selector & 0xFFFC) && !Cpu.TryLoadSelector(segment, held) && segment != Intel386.SS)
            {
                Cpu.TryLoadSelector(segment, 0);
            }
        }
    }

    /// <summary>Whether access rights are ones a program may set: a code or data segment at level 3.</summary>
    private static bool Allowed(byte access) => (access & 0x10) != 0 && ((access >> 5) & 3) == 3;

    /// <summary>Function 0000h: CX descriptors, data segments with base and limit 0; AX the first selector.</summary>
    private ushort? AllocateDescriptors()
    {
        int count = Cx;
        if (count == 0 || TakeDescriptors(count) is not { } first)
        {
            return Error.DescriptorUnavailable;
        }

        for (int i = 0; i < count; i++)
        {
            WriteDescriptor(LdtEntry(first + i), 0, 0, 0xF2, _client32 ? (byte)0x40 : (byte)0);
        }

        SetAx(SelectorOf(first));
        return null;
    }

    /// <summary>Function 0001h: frees the descriptor BX, and empties any segment register holding it.</summary>
    private ushort? FreeDescriptor()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        _ldtUsed[index] = false;
        Write32(LdtEntry(index), 0);
        Write32(LdtEntry(index) + 4, 0);
        Reload(Bx);
        return null;
    }

    /// <summary>Function 0002h: a descriptor for the real-mode segment BX, 64 KB of data, the same one each time.</summary>
    private ushort? SegmentToDescriptor()
    {
        if (!_segmentSelectors.TryGetValue(Bx, out ushort selector))
        {
            if (TakeDescriptors(1) is not { } index)
            {
                return Error.DescriptorUnavailable;
            }

            selector = SelectorOf(index);
            WriteDescriptor(LdtEntry(index), (uint)Bx << 4, 0xFFFF, 0xF2, 0);
            _segmentSelectors[Bx] = selector;
        }

        SetAx(selector);
        return null;
    }

    /// <summary>Function 0006h: the base of BX in CX:DX.</summary>
    private ushort? GetBase()
    {
        if (IndexOf(Bx) is null)
        {
            return Error.InvalidSelector;
        }

        uint baseAddress = DescriptorBase(Bx);
        SetCx(baseAddress >> 16);
        SetDx(baseAddress);
        return null;
    }

    /// <summary>Function 0007h: sets the base of BX to CX:DX.</summary>
    private ushort? SetBase()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        SetDescriptorBase(index, ((uint)Cx << 16) | Dx);
        Reload(Bx);
        return null;
    }

    /// <summary>Function 0008h: sets the limit of BX to CX:DX; past 1 MB it's counted in pages.</summary>
    private ushort? SetLimit()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        SetDescriptorLimit(index, ((uint)Cx << 16) | Dx);
        Reload(Bx);
        return null;
    }

    /// <summary>Function 0009h: sets the access rights of BX: CL the access byte, CH's high nibble the granularity, big and available bits.</summary>
    private ushort? SetAccessRights()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        byte access = (byte)Cx;
        if (!Allowed(access))
        {
            return Error.InvalidValue;
        }

        uint at = LdtEntry(index);
        machine.Write(at + 5, access);
        machine.Write(at + 6, (byte)((machine.Read(at + 6) & 0x0F) | ((Cx >> 8) & 0xD0)));
        Reload(Bx);
        return null;
    }

    /// <summary>Function 000Ah: a writable data descriptor over the same memory as the code segment BX.</summary>
    private ushort? CreateAlias()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        if (TakeDescriptors(1) is not { } alias)
        {
            return Error.DescriptorUnavailable;
        }

        uint from = LdtEntry(index), to = LdtEntry(alias);
        Write32(to, Read32(from));
        Write32(to + 4, Read32(from + 4));
        machine.Write(to + 5, 0xF2);
        SetAx(SelectorOf(alias));
        return null;
    }

    /// <summary>Function 000Bh: copies the descriptor BX to ES:(E)DI.</summary>
    private ushort? GetDescriptor()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        uint from = LdtEntry(index), to = EsDi;
        Write32(to, Read32(from));
        Write32(to + 4, Read32(from + 4));
        return null;
    }

    /// <summary>Function 000Ch: sets the descriptor BX from ES:(E)DI.</summary>
    private ushort? SetDescriptor()
    {
        if (IndexOf(Bx) is not { } index)
        {
            return Error.InvalidSelector;
        }

        uint from = EsDi, to = LdtEntry(index);
        if (!Allowed(machine.Read(from + 5)))
        {
            return Error.InvalidValue;
        }

        Write32(to, Read32(from));
        Write32(to + 4, Read32(from + 4));
        Reload(Bx);
        return null;
    }

    /// <summary>Function 000Dh: one of the first 16 descriptors, the one BX names.</summary>
    private ushort? AllocateSpecificDescriptor()
    {
        int index = Bx >> 3;
        if ((Bx & 4) == 0 || index >= ReservedDescriptors)
        {
            return Error.InvalidSelector;
        }

        if (_ldtUsed[index])
        {
            return Error.DescriptorUnavailable;
        }

        _ldtUsed[index] = true;
        WriteDescriptor(LdtEntry(index), 0, 0, 0xF2, _client32 ? (byte)0x40 : (byte)0);
        return null;
    }

    // ---- conventional memory -----------------------------------------------------------------

    /// <summary>
    /// Function 0100h: BX paragraphs of conventional memory from DOS, AX its segment and DX a
    /// selector for it. A block past 64 KB gets a selector for each 64 KB, the first one
    /// covering it all.
    /// </summary>
    private ushort? AllocateDosMemory()
    {
        int paragraphs = Bx;
        if (dos.AllocateBlock(paragraphs, out int largest) is not { } segment)
        {
            SetBx((uint)largest);
            return 0x0008;
        }

        int count = Math.Max(1, (paragraphs + 0xFFF) / 0x1000);
        if (TakeDescriptors(count) is not { } first)
        {
            dos.FreeBlock(segment);
            return Error.DescriptorUnavailable;
        }

        SetDosDescriptors(first, count, segment, paragraphs);
        _dosBlocks[SelectorOf(first)] = (segment, count);
        SetAx(segment);
        SetDx(SelectorOf(first));
        return null;
    }

    private void SetDosDescriptors(int first, int count, ushort segment, int paragraphs)
    {
        uint size = (uint)paragraphs * 16;
        for (int i = 0; i < count; i++)
        {
            uint offset = (uint)i * 0x1_0000;
            uint limit = i == 0 ? size - 1 : Math.Min(size - offset, 0x1_0000) - 1;
            WriteDescriptor(LdtEntry(first + i), ((uint)segment << 4) + offset, Math.Max(limit, 0), 0xF2, 0);
        }
    }

    /// <summary>Function 0101h: frees the conventional memory block whose selector is DX.</summary>
    private ushort? FreeDosMemory()
    {
        if (!_dosBlocks.Remove(Dx, out var block))
        {
            return Error.InvalidSelector;
        }

        dos.FreeBlock(block.Segment);
        int first = Dx >> 3;
        for (int i = 0; i < block.Count; i++)
        {
            _ldtUsed[first + i] = false;
        }

        return null;
    }

    /// <summary>Function 0102h: resizes the conventional memory block whose selector is DX to BX paragraphs, in place.</summary>
    private ushort? ResizeDosMemory()
    {
        if (!_dosBlocks.TryGetValue(Dx, out var block))
        {
            return Error.InvalidSelector;
        }

        int paragraphs = Bx;
        int count = Math.Max(1, (paragraphs + 0xFFF) / 0x1000);
        if (count > block.Count)
        {
            return Error.DescriptorUnavailable;
        }

        if (dos.ResizeBlock(block.Segment, paragraphs) is { } most)
        {
            SetBx((uint)most);
            return 0x0008;
        }

        SetDosDescriptors(Dx >> 3, count, block.Segment, paragraphs);
        return null;
    }

    // ---- exception handlers ------------------------------------------------------------------

    /// <summary>Function 0202h: the handler for exception BL, in CX:(E)DX.</summary>
    private ushort? GetExceptionHandler()
    {
        if (Bl >= 0x20)
        {
            return Error.InvalidValue;
        }

        SetPointer(_exceptionHandlers[Bl]);
        return null;
    }

    /// <summary>Function 0203h: sets the handler for exception BL to CX:(E)DX.</summary>
    private ushort? SetExceptionHandler()
    {
        if (Bl >= 0x20)
        {
            return Error.InvalidValue;
        }

        _exceptionHandlers[Bl] = new FarPointer(Cx, Offset(Cpu.Edx));
        return null;
    }

    // ---- calls down to real mode -------------------------------------------------------------

    // the real-mode register structure: EDI, ESI, EBP, a reserved dword, EBX, EDX, ECX and EAX,
    // then the flags, ES, DS, FS, GS, IP, CS, SP and SS as words
    private const uint StructFlags = 0x20, StructEs = 0x22, StructDs = 0x24, StructFs = 0x26, StructGs = 0x28;
    private const uint StructIp = 0x2A, StructCs = 0x2C, StructSp = 0x2E, StructSs = 0x30;

    /// <summary>
    /// Functions 0300h to 0302h: an interrupt (BL) or a far procedure, called in real mode with
    /// the registers in the structure at ES:(E)DI and CX words copied from the program's stack.
    /// The structure gets the registers it comes back with.
    /// </summary>
    private ushort? CallRealMode(ushort function)
    {
        uint structure = EsDi;
        int words = Cx;
        int vector = Bl;
        uint parameters = Cpu.SegmentBase(Intel386.SS) + StackPointer() + (3 * (_client32 ? 4u : 2u));
        uint clientFlags = ReadFrameValue(FrameFlags());
        string target = function == 0x0300 ? $"INT {vector:X2}h" : $"{Read16(structure + StructCs):X4}:{Read16(structure + StructIp):X4}";
        // DOS's keyboard checks, which a game polls thousands of times a second (Wing Commander
        // III's movies do, with 0C0Bh), are left out, as DOS leaves them out of its own log
        byte ah = (byte)(Read16(structure + 0x1C) >> 8);
        if (function != 0x0300 || vector != 0x21 || ah is not (0x06 or 0x0B or 0x0C))
        {
            machine.Trace($"31/{function:X4} {target} ax {Read16(structure + 0x1C):X4} bx {Read16(structure + 0x10):X4} " +
                $"cx {Read16(structure + 0x18):X4} dx {Read16(structure + 0x14):X4} ds {Read16(structure + StructDs):X4} es {Read16(structure + StructEs):X4}");
        }

        Save(PendingKind.RealCall, vector, structure);
        uint flags = (Read16(structure + StructFlags) & ~InterruptFlag) | (clientFlags & InterruptFlag);
        SwitchDown(flags);
        LoadRealRegisters(structure);
        ushort ss = (ushort)Read16(structure + StructSs), sp = (ushort)Read16(structure + StructSp);
        if (ss != 0 || sp != 0)
        {
            Cpu.Ss = ss;
            Cpu.Esp = sp;
        }

        for (int i = words - 1; i >= 0; i--)
        {
            RealPush((ushort)Read16(parameters + ((uint)i * 2)));
        }

        if (function == 0x0300)
        {
            RealInterrupt(vector);
            return null;
        }

        if (function == 0x0302)
        {
            RealPush((ushort)Cpu.Eflags);
        }

        RealPush(Machine.BiosSegment);
        RealPush(RealReturnStub);
        Cpu.Cs = (ushort)Read16(structure + StructCs);
        Cpu.Eip = Read16(structure + StructIp);
        if (function == 0x0302)
        {
            Cpu.Eflags &= ~(InterruptFlag | TrapFlag);
        }

        return null;
    }

    /// <summary>The general and data segment registers from a register structure.</summary>
    private void LoadRealRegisters(uint structure)
    {
        Cpu.Edi = Read32(structure);
        Cpu.Esi = Read32(structure + 4);
        Cpu.Ebp = Read32(structure + 8);
        Cpu.Ebx = Read32(structure + 0x10);
        Cpu.Edx = Read32(structure + 0x14);
        Cpu.Ecx = Read32(structure + 0x18);
        Cpu.Eax = Read32(structure + 0x1C);
        Cpu.Es = (ushort)Read16(structure + StructEs);
        Cpu.Ds = (ushort)Read16(structure + StructDs);
        Cpu.Fs = (ushort)Read16(structure + StructFs);
        Cpu.Gs = (ushort)Read16(structure + StructGs);
    }

    /// <summary>The real-mode registers into a register structure.</summary>
    private void StoreRealRegisters(uint structure)
    {
        Write32(structure, Cpu.Edi);
        Write32(structure + 4, Cpu.Esi);
        Write32(structure + 8, Cpu.Ebp);
        Write32(structure + 0x10, Cpu.Ebx);
        Write32(structure + 0x14, Cpu.Edx);
        Write32(structure + 0x18, Cpu.Ecx);
        Write32(structure + 0x1C, Cpu.Eax);
        Write16(structure + StructFlags, Cpu.Eflags);
        Write16(structure + StructEs, Cpu.Es);
        Write16(structure + StructDs, Cpu.Ds);
        Write16(structure + StructFs, Cpu.Fs);
        Write16(structure + StructGs, Cpu.Gs);
    }

    /// <summary>The real-mode call is done: its registers into the structure, and back to the program.</summary>
    private void RealCallReturned(Pending pending)
    {
        StoreRealRegisters(pending.Structure);
        Cpu.RestoreContext(pending.Saved);
        SetReturnCarry(false);
    }

    // ---- real-mode callbacks -----------------------------------------------------------------

    /// <summary>
    /// Function 0303h: a real-mode address (CX:DX) that calls the program's procedure at DS:(E)SI,
    /// with the registers in the structure at ES:(E)DI.
    /// </summary>
    private ushort? AllocateCallback()
    {
        int index = Array.IndexOf(_callbacks, null);
        if (index < 0)
        {
            return Error.CallbackUnavailable;
        }

        _callbacks[index] = new Callback
        {
            Procedure = new FarPointer(Cpu.Ds, Offset(Cpu.Esi)),
            Structure = new FarPointer(Cpu.Es, Offset(Cpu.Edi)),
        };
        SetCx(Machine.BiosSegment);
        SetDx((uint)(CallbackStubs + (index * 4)));
        return null;
    }

    /// <summary>Function 0304h: frees the callback at CX:DX.</summary>
    private ushort? FreeCallback()
    {
        int index = (Dx - CallbackStubs) / 4;
        if (Cx != Machine.BiosSegment || Dx < CallbackStubs || index >= Callbacks || (Dx - CallbackStubs) % 4 != 0 || _callbacks[index] is null)
        {
            return Error.InvalidCallback;
        }

        _callbacks[index] = null;
        return null;
    }

    /// <summary>
    /// A real-mode callback called: up to the program's procedure with DS:(E)SI at the real-mode
    /// stack, ES:(E)DI at the register structure, and interrupts off. It returns with IRET.
    /// </summary>
    private void CallbackEntered()
    {
        int index = (int)((Cpu.Eip - 3 - CallbackStubs) / 4);
        var callback = _callbacks[index] ?? throw new NotSupportedException($"Real-mode callback {index} was called after it was freed.");
        uint structure = DescriptorBase(callback.Structure.Selector) + callback.Structure.Offset;
        StoreRealRegisters(structure);
        Write16(structure + StructIp, CallbackStubs + (uint)(index * 4));
        Write16(structure + StructCs, Machine.BiosSegment);
        Write16(structure + StructSp, Cpu.Esp);
        Write16(structure + StructSs, Cpu.Ss);
        uint stack = (uint)Cpu.Ss << 4;
        uint sp = Cpu.Esp & 0xFFFF;

        Save(PendingKind.Callback, index, structure);
        SwitchUp(CallbackReturn);
        SetDescriptorBase(_realStackSelector >> 3, stack);
        Load(Intel386.DS, _realStackSelector);
        Load(Intel386.ES, callback.Structure.Selector);
        Cpu.Esi = sp;
        Cpu.Edi = callback.Structure.Offset;
        Cpu.InterruptThrough(callback.Procedure.Selector, callback.Procedure.Offset, _client32);
    }

    /// <summary>The callback's procedure is done: back to real mode with the registers in the structure at ES:(E)DI.</summary>
    private void CallbackReturned()
    {
        uint structure = EsDi;
        Finish(PendingKind.Callback);
        uint flags = Read16(structure + StructFlags);
        EnterRealMode();
        LoadRealRegisters(structure);
        Cpu.Ss = (ushort)Read16(structure + StructSs);
        Cpu.Esp = Read16(structure + StructSp);
        Cpu.Cs = (ushort)Read16(structure + StructCs);
        Cpu.Eip = Read16(structure + StructIp);
        Cpu.Eflags = flags;
    }

    // ---- the raw mode switch -----------------------------------------------------------------

    /// <summary>Function 0305h: no state needs saving, so the routines to save it only return.</summary>
    private void StateSaveAddresses()
    {
        SetAx(0);
        SetBx(Machine.BiosSegment);
        SetCx(SaveRestoreReal);
        SetSi(HostCode3);
        Cpu.Edi = SetOffset(Cpu.Edi, SaveRestoreProtected);
    }

    /// <summary>Function 0306h: the addresses to jump to to switch modes directly, BX:CX from real mode and SI:(E)DI from protected.</summary>
    private void RawSwitchAddresses()
    {
        SetBx(Machine.BiosSegment);
        SetCx(RawToProtectedStub);
        SetSi(HostCode3);
        Cpu.Edi = SetOffset(Cpu.Edi, RawToReal);
    }

    /// <summary>
    /// The raw switch from real mode: DS from AX, ES from CX, SS:(E)SP from DX:(E)BX and CS:(E)IP
    /// from SI:(E)DI, all selectors; FS and GS null.
    /// </summary>
    private void RawToProtected()
    {
        ushort ds = Ax, es = Cx, ss = Dx, cs = (ushort)Cpu.Esi;
        uint esp = Offset(Cpu.Ebx), eip = Offset(Cpu.Edi);
        uint flags = Cpu.Eflags;
        EnterProtectedMode();
        Load(Intel386.CS, cs);
        Load(Intel386.SS, ss);
        Load(Intel386.DS, ds);
        Load(Intel386.ES, es);
        Load(Intel386.FS, 0);
        Load(Intel386.GS, 0);
        Cpu.Esp = esp;
        Cpu.Eip = eip;
        Cpu.Eflags = (flags & ~NestedTask) | Iopl3;
    }

    /// <summary>The raw switch from protected mode: the same registers, holding real-mode segments.</summary>
    private void RawToRealMode()
    {
        ushort ds = Ax, es = Cx, ss = Dx, cs = (ushort)Cpu.Esi;
        uint sp = Bx, ip = (ushort)Cpu.Edi;
        uint flags = Cpu.Eflags;
        EnterRealMode();
        Cpu.Ds = ds;
        Cpu.Es = es;
        Cpu.Ss = ss;
        Cpu.Cs = cs;
        Cpu.Esp = sp;
        Cpu.Eip = ip;
        Cpu.Eflags = flags & 0x0FD5;
    }

    // ---- extended memory ---------------------------------------------------------------------

    private static uint PageUp(uint size) => (size + 0xFFF) & ~0xFFFu;

    /// <summary>The first gap of the size at or after the pool's start, between the blocks; null if none fits.</summary>
    private uint? FindGap(uint size, MemoryBlock? ignoring = null)
    {
        uint at = PoolStart;
        foreach (var block in _blocks)
        {
            if (block == ignoring)
            {
                continue;
            }

            if (block.Address - at >= size)
            {
                return at;
            }

            at = block.Address + block.Size;
        }

        return Machine.MemorySize - at >= size ? at : null;
    }

    private uint FreeTotal() => Machine.MemorySize - PoolStart - (uint)_blocks.Sum(b => (long)b.Size);

    private uint LargestGap()
    {
        uint largest = 0, at = PoolStart;
        foreach (var block in _blocks)
        {
            largest = Math.Max(largest, block.Address - at);
            at = block.Address + block.Size;
        }

        return Math.Max(largest, Machine.MemorySize - at);
    }

    private void AddBlock(MemoryBlock block)
    {
        int index = _blocks.FindIndex(b => b.Address > block.Address);
        _blocks.Insert(index < 0 ? _blocks.Count : index, block);
    }

    /// <summary>Function 0500h: the free memory, in the 48-byte structure at ES:(E)DI; what isn't known is FFFFFFFFh.</summary>
    private void FreeMemoryInformation()
    {
        uint at = EsDi;
        uint pages = FreeTotal() / 0x1000, total = (Machine.MemorySize - PoolStart) / 0x1000, largest = LargestGap();
        uint[] values = [largest, largest / 0x1000, largest / 0x1000, total, pages, pages, total, pages, 0];
        for (uint i = 0; i < 12; i++)
        {
            Write32(at + (i * 4), i < values.Length ? values[i] : 0xFFFF_FFFF);
        }
    }

    /// <summary>Function 0501h: BX:CX bytes of extended memory; its linear address in BX:CX and its handle in SI:DI.</summary>
    private ushort? AllocateMemory()
    {
        uint size = PageUp(((uint)Bx << 16) | Cx);
        if (size == 0)
        {
            return Error.InvalidValue;
        }

        if (FindGap(size) is not { } address)
        {
            return Error.LinearMemoryUnavailable;
        }

        var block = new MemoryBlock { Handle = _nextHandle++, Address = address, Size = size };
        AddBlock(block);
        SetBx(address >> 16);
        SetCx(address);
        SetSi(block.Handle >> 16);
        SetDi(block.Handle);
        machine.Trace($"31/0501 {size} bytes at {address:X8}h, handle {block.Handle}");
        return null;
    }

    private MemoryBlock? BlockFromSiDi()
    {
        uint handle = ((uint)(ushort)Cpu.Esi << 16) | (ushort)Cpu.Edi;
        return _blocks.Find(b => b.Handle == handle);
    }

    /// <summary>Function 0502h: frees the block whose handle is SI:DI.</summary>
    private ushort? FreeMemory()
    {
        if (BlockFromSiDi() is not { } block)
        {
            return Error.InvalidHandle;
        }

        _blocks.Remove(block);
        return null;
    }

    /// <summary>
    /// Function 0503h: resizes the block whose handle is SI:DI to BX:CX bytes, moving it if it
    /// can't grow where it is; its address in BX:CX and its handle in SI:DI, the same.
    /// </summary>
    private ushort? ResizeMemory()
    {
        if (BlockFromSiDi() is not { } block)
        {
            return Error.InvalidHandle;
        }

        uint size = PageUp(((uint)Bx << 16) | Cx);
        if (size == 0)
        {
            return Error.InvalidValue;
        }

        int index = _blocks.IndexOf(block);
        uint end = index + 1 < _blocks.Count ? _blocks[index + 1].Address : Machine.MemorySize;
        if (end - block.Address < size)
        {
            if (FindGap(size, block) is not { } address)
            {
                return Error.LinearMemoryUnavailable;
            }

            machine.MoveMemory(block.Address, address, Math.Min(block.Size, size));
            _blocks.Remove(block);
            block.Address = address;
            AddBlock(block);
        }

        block.Size = size;
        SetBx(block.Address >> 16);
        SetCx(block.Address);
        return null;
    }
}
