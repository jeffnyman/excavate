using Excavate.Core.Dos;
using Excavate.Core.Processors;

namespace Excavate.Core.Dpmi;

/// <summary>
/// A DPMI 0.9 host in C#: what lets a DOS extender run its program in 32-bit protected mode.
/// <i>Wing Commander III</i>'s loader and W.C.A.T.'s launcher (PMODE/W) both look for one first,
/// with INT 2Fh function 1687h, and use it if it's there.
/// </summary>
/// <remarks>
/// <para>
/// The program, the host's "client", runs at privilege level 3 with I/O privilege level 3, so its
/// own <c>CLI</c>, <c>STI</c>, <c>IN</c> and <c>OUT</c> work as in real mode. Its segments are
/// descriptors in a local table the host keeps; INT 31h asks for them, for memory, and for calls
/// down to real-mode DOS and the BIOS.
/// </para>
/// <para>
/// The host switches modes without running any code of its own: it saves the processor's whole
/// state (<see cref="Intel386.Context"/>), sets up the other mode's, and when the code it called
/// comes back to one of its stubs, puts the saved state back. Each switch it's in the middle of
/// is a <see cref="Pending"/> entry, newest last, so calls down and back up can nest.
/// </para>
/// <para>
/// Interrupts have three paths. A program's <c>INT</c> goes through the interrupt table to its
/// own handler, if it set one, or to the host's, which reflects it down to the real-mode vector.
/// A hardware interrupt in protected mode is delivered by the host, through its own copy of the
/// program's vectors, since the table's entries 8 to 15 belong to exceptions. And a hardware
/// interrupt in real mode that the program hooked in protected mode comes back up to it through a
/// real-mode stub put in its vector.
/// </para>
/// </remarks>
internal sealed partial class DpmiHost(Machine machine, DosServices dos)
{
    /// <summary>The first and last of the host's service numbers for <see cref="Intel386.HostCall"/>.</summary>
    internal const byte FirstService = 0xD0, LastService = 0xE7;

    // real-mode services, in the BIOS segment
    private const byte EntryService = 0xD0, RealReturnService = 0xD1, RawToProtectedService = 0xD2, UpService = 0xD3, CallbackService = 0xD4;

    // protected-mode services, in the host's code segment
    private const byte InterruptService = 0xE0, ExceptionService = 0xE1, UpReturnService = 0xE2, ExceptionReturnService = 0xE3;
    private const byte CallbackReturnService = 0xE4, RawToRealService = 0xE5, DefaultExceptionService = 0xE6;

    // ---- where everything is -----------------------------------------------------------------

    // the host's own area, just above the high memory area; offsets in it are offsets in its
    // segments, which all start here
    private const uint HostBase = 0x11_0000;
    private const uint GdtAt = 0x0000;
    private const uint GdtLimit = 0x37;
    private const uint IdtAt = 0x0100;
    private const uint TssAt = 0x0900;

    // each vector's default handler at level 3, eight bytes apart; each exception's at level 0
    private const uint InterruptStubs = 0x1000;
    private const uint ExceptionStubs = 0x1800;
    private const uint UpReturn = 0x1900;
    private const uint ExceptionReturn = 0x1908;
    private const uint CallbackReturn = 0x1910;
    private const uint RawToReal = 0x1918;
    private const uint SaveRestoreProtected = 0x1920;
    private const uint DefaultException = 0x1928;

    // the level 0 stack exceptions arrive on, and the "locked" level 3 stack the program's
    // handlers run on when the host calls them from real mode
    private const uint Ring0StackTop = 0x4000;
    private const uint LockedStackTop = 0x8000;

    private const uint LdtAt = 0x1_0000;
    private const int LdtEntries = 0x2000;

    // the first descriptors of the local table, for function 000Dh alone
    private const int ReservedDescriptors = 16;

    /// <summary>The start of the extended memory the program allocates from.</summary>
    private const uint PoolStart = 0x13_0000;

    // the global table's selectors: code and data at level 0 and level 3, the task state segment
    // and the local table
    private const ushort HostCode0 = 0x08, HostData0 = 0x10, HostCode3 = 0x1B, HostStack3 = 0x23, TaskSelector = 0x28, LdtSelector = 0x30;

    // the real-mode stubs, in the BIOS segment
    private const ushort EntryStub = 0x0600;
    private const ushort RealReturnStub = 0x0604;
    private const ushort RawToProtectedStub = 0x0608;
    private const ushort SaveRestoreReal = 0x060C;
    private const ushort UpStubs = 0x0700;
    private const ushort CallbackStubs = 0x0B00;
    private const int Callbacks = 16;

    // the host's real-mode stack, in the conventional memory the program gives it on entry
    private const ushort RealStackParagraphs = 0x0200;
    private const ushort RealStackTop = 0x1FFE;

    // the flags: carry, interrupts, trap, the I/O privilege level, nested task; and the
    // arithmetic ones a reflected interrupt hands back
    private const uint Carry = 0x0001, InterruptFlag = 0x0200, TrapFlag = 0x0100, Iopl3 = 0x3000, NestedTask = 0x4000;
    private const uint ArithmeticFlags = 0x08D5;

    // the vectors whose real-mode interrupts come up to a protected-mode handler: the hardware
    // interrupts, the timer tick, Ctrl+Break and the critical error handler
    private static bool ComesUp(int vector) => vector is (>= 0x08 and <= 0x0F) or (>= 0x70 and <= 0x77) or 0x1C or 0x23 or 0x24;

    // ---- the state ---------------------------------------------------------------------------

    private bool _active;
    private bool _client32;
    private ushort _realStackSegment;
    private ushort _realStackSelector;
    private readonly bool[] _ldtUsed = new bool[LdtEntries];
    private readonly Dictionary<ushort, ushort> _segmentSelectors = [];
    private readonly FarPointer[] _vectors = new FarPointer[256];
    private readonly FarPointer[] _exceptionHandlers = new FarPointer[32];

    // for each vector whose up stub the host put in the real-mode table, the vector it replaced
    private readonly uint[] _chained = new uint[256];

    // how deep in reflecting each vector down the host is: an up stub reached meanwhile passes
    // the interrupt on instead of bringing it straight back up
    private readonly int[] _reflecting = new int[256];

    private readonly List<Pending> _pending = [];
    private readonly Callback?[] _callbacks = new Callback?[Callbacks];

    /// <summary>A protected-mode address: a selector and an offset.</summary>
    private readonly record struct FarPointer(ushort Selector, uint Offset);

    private enum PendingKind : byte
    {
        // INT 31h functions 0300h to 0302h: a call down to real mode for the program
        RealCall,

        // an interrupt reflected down to real mode by the host's default handler
        Reflection,

        // a real-mode interrupt brought up to the program's protected-mode handler
        Up,

        // a real-mode callback into the program
        Callback,
    }

    /// <summary>A mode switch in progress: the state to go back to, and what to do then.</summary>
    private sealed class Pending
    {
        public PendingKind Kind;
        public Intel386.Context Saved = null!;
        public int Vector;
        public uint Structure;
    }

    /// <summary>A real-mode callback: the program's procedure, and its register structure.</summary>
    private sealed class Callback
    {
        public FarPointer Procedure;
        public FarPointer Structure;
    }

    private Intel386 Cpu => machine.Cpu;

    /// <summary>Whether a program has entered protected mode through the host.</summary>
    public bool Active => _active;

    // ---- setting up --------------------------------------------------------------------------

    /// <summary>Writes the real-mode stubs into the BIOS area.</summary>
    public void Install()
    {
        uint bios = (uint)Machine.BiosSegment << 4;
        machine.WriteRom(bios + EntryStub, [0x0F, 0xFF, EntryService]);
        machine.WriteRom(bios + RealReturnStub, [0x0F, 0xFF, RealReturnService]);
        machine.WriteRom(bios + RawToProtectedStub, [0x0F, 0xFF, RawToProtectedService]);
        machine.WriteRom(bios + SaveRestoreReal, [0xCB]);
        for (int vector = 0; vector < 256; vector++)
        {
            machine.WriteRom(bios + UpStubs + (uint)(vector * 4), [0x0F, 0xFF, UpService, 0xCF]);
        }

        for (int i = 0; i < Callbacks; i++)
        {
            machine.WriteRom(bios + CallbackStubs + (uint)(i * 4), [0x0F, 0xFF, CallbackService]);
        }
    }

    /// <summary>
    /// INT 2Fh function 1687h: the host is here. It answers with what it supports (32-bit
    /// programs, a 386, DPMI 0.9), the conventional memory it wants for itself, and the address
    /// a program calls to switch to protected mode.
    /// </summary>
    public void Detect()
    {
        Cpu.Eax &= 0xFFFF_0000;
        Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | 0x0001;
        Cpu.Ecx = (Cpu.Ecx & 0xFFFF_FF00) | 0x03;
        Cpu.Edx = (Cpu.Edx & 0xFFFF_0000) | 0x005A;
        Cpu.Esi = (Cpu.Esi & 0xFFFF_0000) | RealStackParagraphs;
        Cpu.Es = Machine.BiosSegment;
        Cpu.Edi = (Cpu.Edi & 0xFFFF_0000) | EntryStub;
        machine.Trace("2F/1687 DPMI host: 32-bit, version 0.90");
    }

    /// <summary>
    /// The program has ended: the host lets it go, and the real-mode vectors it put its stubs in
    /// get back what they had, so the next program finds the host free and the vectors as DOS left them.
    /// </summary>
    public void Release()
    {
        if (!_active)
        {
            return;
        }

        for (int vector = 0; vector < 256; vector++)
        {
            if (ComesUp(vector) && RealVector(vector) == (((uint)Machine.BiosSegment << 16) | (uint)(UpStubs + (vector * 4))))
            {
                SetRealVector(vector, _chained[vector]);
            }
        }

        _pending.Clear();
        _active = false;
        machine.Trace("DPMI: the program has ended");
    }

    /// <summary>A service from one of the host's stubs.</summary>
    public void Call(byte service)
    {
        switch (service)
        {
            case EntryService: Enter(); break;
            case RealReturnService: RealReturned(); break;
            case RawToProtectedService: RawToProtected(); break;
            case UpService: Up(); break;
            case CallbackService: CallbackEntered(); break;
            case InterruptService: DefaultInterrupt(); break;
            case ExceptionService: Exception(); break;
            case UpReturnService: UpReturned(); break;
            case ExceptionReturnService: ExceptionReturned(); break;
            case CallbackReturnService: CallbackReturned(); break;
            case RawToRealService: RawToRealMode(); break;
            case DefaultExceptionService: DefaultExceptionReached(); break;
            default: throw new NotSupportedException($"No DPMI service {service:X2}h.");
        }
    }

    /// <summary>
    /// The mode switch, called far from real mode with AX bit 0 set for a 32-bit program and ES
    /// the memory it set aside for the host. The program comes back in protected mode at the
    /// instruction after its call, with selectors for its code, data and stack segments and its
    /// PSP, and its environment's segment in the PSP made a selector too.
    /// </summary>
    private void Enter()
    {
        uint sp = Cpu.Esp & 0xFFFF;
        ushort ss = Cpu.Ss;
        ushort returnIp = machine.ReadWord(ss, sp);
        ushort returnCs = machine.ReadWord(ss, sp + 2);
        Cpu.Esp = (Cpu.Esp & 0xFFFF_0000) | ((sp + 4) & 0xFFFF);

        if (_active)
        {
            // one program at a time
            machine.Trace("DPMI entry: a program is already in protected mode");
            Cpu.Cs = returnCs;
            Cpu.Eip = returnIp;
            Cpu.Eflags |= Carry;
            return;
        }

        _client32 = (Cpu.Eax & 1) != 0;
        _realStackSegment = Cpu.Es;

        // the host's tables are past the first megabyte
        machine.A20 = true;
        BuildTables();

        // the program's segments as descriptors: code, data, stack, and the PSP
        ushort code = NewDescriptor((uint)returnCs << 4, 0xFFFF, 0xFA, false);
        ushort data = NewDescriptor((uint)Cpu.Ds << 4, 0xFFFF, 0xF2, false);
        ushort stack = Cpu.Ss == Cpu.Ds ? data : NewDescriptor((uint)Cpu.Ss << 4, 0xFFFF, 0xF2, false);
        ushort psp = dos.Psp;
        ushort pspSelector = NewDescriptor((uint)psp << 4, 0xFF, 0xF2, false);
        ushort environment = machine.ReadWord(psp, 0x2C);
        if (environment != 0)
        {
            uint size = (uint)machine.ReadWord((ushort)(environment - 1), 3) * 16;
            machine.WriteWord(psp, 0x2C, NewDescriptor((uint)environment << 4, Math.Max(size, 1) - 1, 0xF2, false));
        }

        _realStackSelector = NewDescriptor(0, 0xFFFF, 0xF2, false);

        uint flags = Cpu.Eflags;
        Cpu.Gdt = (HostBase + GdtAt, GdtLimit);
        EnterProtectedMode();
        Cpu.LoadSystemTables(LdtSelector, TaskSelector);
        Load(Intel386.CS, code);
        Load(Intel386.SS, stack);
        Load(Intel386.DS, data);
        Load(Intel386.ES, pspSelector);
        Load(Intel386.FS, 0);
        Load(Intel386.GS, 0);
        Cpu.Eip = returnIp;
        Cpu.Esp &= 0xFFFF;
        Cpu.Eflags = (flags & ~(Carry | NestedTask)) | Iopl3;
        _active = true;
        machine.Trace($"DPMI entry: {(_client32 ? "32" : "16")}-bit program in protected mode, CS {code:X4} DS {data:X4} SS {stack:X4} PSP {pspSelector:X4}");
    }

    /// <summary>
    /// Fills in the global table, the interrupt table, the task state segment and the stubs, and
    /// starts the program's local table and vectors afresh.
    /// </summary>
    private void BuildTables()
    {
        for (uint at = 0; at < PoolStart - HostBase; at++)
        {
            machine.Write(HostBase + at, 0);
        }

        WriteDescriptor(HostBase + GdtAt + HostCode0, HostBase, 0xFFFF, 0x9B, 0x40);
        WriteDescriptor(HostBase + GdtAt + HostData0, HostBase, 0xFFFF, 0x93, 0x40);
        WriteDescriptor(HostBase + GdtAt + (HostCode3 & ~3u), HostBase, 0xFFFF, 0xFB, 0x40);
        WriteDescriptor(HostBase + GdtAt + (HostStack3 & ~3u), HostBase, 0xFFFF, 0xF3, 0x40);
        WriteDescriptor(HostBase + GdtAt + TaskSelector, HostBase + TssAt, 0x67, 0x89, 0);
        WriteDescriptor(HostBase + GdtAt + LdtSelector, HostBase + LdtAt, (LdtEntries * 8) - 1, 0x82, 0);

        // the task state segment: only its level 0 stack is used. The I/O permission bitmap is
        // past its end, which leaves the ports to the I/O privilege level
        Write32(HostBase + TssAt + 4, Ring0StackTop);
        Write32(HostBase + TssAt + 8, HostData0);
        Write16(HostBase + TssAt + 0x66, 0x68);

        // the stubs: each vector's default handler, ending in an IRET the size of the program's,
        // and each exception's entry at level 0
        byte[] iret = _client32 ? [0xCF] : [0x66, 0xCF];
        byte[] retf = _client32 ? [0xCB] : [0x66, 0xCB];
        for (uint vector = 0; vector < 256; vector++)
        {
            WriteBytes(HostBase + InterruptStubs + (vector * 8), [0x0F, 0xFF, InterruptService, .. iret]);
            _vectors[vector] = new FarPointer(HostCode3, InterruptStubs + (vector * 8));
            _chained[vector] = 0;
            _reflecting[vector] = 0;
        }

        for (uint vector = 0; vector < 32; vector++)
        {
            WriteBytes(HostBase + ExceptionStubs + (vector * 8), [0x0F, 0xFF, ExceptionService, 0xCF]);
            _exceptionHandlers[vector] = new FarPointer(HostCode3, DefaultException);
        }

        WriteBytes(HostBase + UpReturn, [0x0F, 0xFF, UpReturnService]);
        WriteBytes(HostBase + ExceptionReturn, [0x0F, 0xFF, ExceptionReturnService]);
        WriteBytes(HostBase + CallbackReturn, [0x0F, 0xFF, CallbackReturnService]);
        WriteBytes(HostBase + RawToReal, [0x0F, 0xFF, RawToRealService]);
        WriteBytes(HostBase + SaveRestoreProtected, retf);
        WriteBytes(HostBase + DefaultException, [0x0F, 0xFF, DefaultExceptionService]);

        // the interrupt table: exceptions to level 0, where the host sorts them out, and the
        // rest to the default handlers, which a program at level 3 may call
        for (int vector = 0; vector < 256; vector++)
        {
            WriteGate(vector);
        }

        Array.Clear(_ldtUsed);
        _segmentSelectors.Clear();
        _pending.Clear();
        Array.Clear(_callbacks);
        _blocks.Clear();
        _dosBlocks.Clear();
    }

    /// <summary>
    /// A vector's gate: an exception's goes to the host at level 0; any other goes to the
    /// program's handler, or the host's default one, and a program's <c>INT</c> may use it.
    /// </summary>
    private void WriteGate(int vector)
    {
        uint at = HostBase + IdtAt + ((uint)vector * 8);
        if (vector < 0x20)
        {
            WriteGate(at, HostCode0, ExceptionStubs + ((uint)vector * 8), 0, true);
        }
        else
        {
            WriteGate(at, _vectors[vector].Selector, _vectors[vector].Offset, 3, _client32);
        }
    }

    private void Load(int segment, ushort selector)
    {
        if (!Cpu.TryLoadSelector(segment, selector))
        {
            throw new NotSupportedException($"The DPMI host couldn't load selector {selector:X4}h.");
        }
    }

    // ---- switching modes ---------------------------------------------------------------------

    /// <summary>
    /// Switches the processor to real mode, with the real-mode interrupt table at address 0: the
    /// interrupt table register stays where it was through a switch, so a host sets it each way.
    /// </summary>
    private void EnterRealMode()
    {
        Cpu.EnterRealMode();
        Cpu.Idt = (0, 0x3FF);
    }

    /// <summary>Switches the processor to protected mode, with the host's interrupt table.</summary>
    private void EnterProtectedMode()
    {
        Cpu.EnterProtectedMode();
        Cpu.Idt = (HostBase + IdtAt, (256 * 8) - 1);
    }

    /// <summary>Saves the processor's state, to come back to when the code about to run returns to a stub.</summary>
    private Pending Save(PendingKind kind, int vector = 0, uint structure = 0)
    {
        var pending = new Pending { Kind = kind, Saved = Cpu.SaveContext(), Vector = vector, Structure = structure };
        _pending.Add(pending);
        return pending;
    }

    /// <summary>The newest switch in progress, which must be of the kind a stub expects.</summary>
    private Pending Finish(PendingKind kind)
    {
        if (_pending.Count == 0 || _pending[^1].Kind != kind)
        {
            throw new NotSupportedException($"The DPMI host was returned to for a {kind} it wasn't making.");
        }

        var pending = _pending[^1];
        _pending.RemoveAt(_pending.Count - 1);
        return pending;
    }

    /// <summary>
    /// Switches to real mode on the host's real-mode stack: below wherever an earlier switch
    /// up from it left off, so that switches can nest.
    /// </summary>
    private void SwitchDown(uint flags)
    {
        ushort sp = RealStackTop;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var saved = _pending[i].Saved;
            if (!saved.ProtectedMode && saved.Ss == _realStackSegment)
            {
                sp = (ushort)((saved.Esp - 2) & 0xFFFE);
                break;
            }
        }

        EnterRealMode();
        Cpu.Ss = _realStackSegment;
        Cpu.Esp = sp;
        Cpu.Eflags = flags & 0x0FD5;
    }

    /// <summary>
    /// Switches to protected mode on the host's locked stack, at one of its level 3 stubs, for a
    /// call into the program that will return there.
    /// </summary>
    private void SwitchUp(uint stub)
    {
        uint esp = LockedStack();
        uint flags = Cpu.Eflags;
        EnterProtectedMode();
        Load(Intel386.CS, HostCode3);
        Load(Intel386.SS, HostStack3);
        Load(Intel386.DS, 0);
        Load(Intel386.ES, 0);
        Load(Intel386.FS, 0);
        Load(Intel386.GS, 0);
        Cpu.Eip = stub;
        Cpu.Esp = esp;
        Cpu.Eflags = (flags & ArithmeticFlags) | Iopl3;
    }

    /// <summary>
    /// The top of the free part of the locked stack: below the program, if it's on it now, or
    /// below wherever an earlier switch down from it left off.
    /// </summary>
    private uint LockedStack(ushort ss = 0, uint esp = 0)
    {
        if (ss == HostStack3)
        {
            return (esp - 4) & ~3u;
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var saved = _pending[i].Saved;
            if (saved.ProtectedMode && saved.Ss == HostStack3)
            {
                return (saved.Esp - 4) & ~3u;
            }
        }

        return LockedStackTop;
    }

    private void RealPush(ushort value)
    {
        uint sp = (Cpu.Esp - 2) & 0xFFFF;
        Cpu.Esp = (Cpu.Esp & 0xFFFF_0000) | sp;
        machine.WriteWord(Cpu.Ss, sp, value);
    }

    /// <summary>Starts a real-mode interrupt handler from C#, as <c>INT</c> would, returning to the host's stub.</summary>
    private void RealInterrupt(int vector)
    {
        RealPush((ushort)Cpu.Eflags);
        RealPush(Machine.BiosSegment);
        RealPush(RealReturnStub);
        Cpu.Cs = machine.ReadWord(0, ((uint)vector * 4) + 2);
        Cpu.Eip = machine.ReadWord(0, (uint)vector * 4);
        Cpu.Eflags &= ~(InterruptFlag | TrapFlag);
    }

    /// <summary>A real-mode stub reached by a far call or interrupt, returning to the host.</summary>
    private void RealReturned()
    {
        if (_pending.Count == 0)
        {
            throw new NotSupportedException("The DPMI host was returned to from real mode with nothing to go back to.");
        }

        if (_pending[^1].Kind == PendingKind.RealCall)
        {
            RealCallReturned(Finish(PendingKind.RealCall));
            return;
        }

        // the default handler's reflection: the registers and the arithmetic flags come back up
        var pending = Finish(PendingKind.Reflection);
        _reflecting[pending.Vector]--;
        uint eax = Cpu.Eax, ebx = Cpu.Ebx, ecx = Cpu.Ecx, edx = Cpu.Edx, esi = Cpu.Esi, edi = Cpu.Edi, ebp = Cpu.Ebp;
        uint flags = Cpu.Eflags;
        Cpu.RestoreContext(pending.Saved);
        (Cpu.Eax, Cpu.Ebx, Cpu.Ecx, Cpu.Edx, Cpu.Esi, Cpu.Edi, Cpu.Ebp) = (eax, ebx, ecx, edx, esi, edi, ebp);
        uint frame = FrameFlags();
        WriteFrameValue(frame, (ReadFrameValue(frame) & ~ArithmeticFlags) | (flags & ArithmeticFlags));
    }

    // ---- interrupts --------------------------------------------------------------------------

    /// <summary>
    /// A hardware interrupt while the processor is in protected mode: to the program's handler
    /// for it, or the host's default one, which reflects it down to real mode.
    /// </summary>
    public void HardwareInterrupt(int vector)
    {
        var handler = _vectors[vector];
        Cpu.InterruptThrough(handler.Selector, handler.Offset, _client32);
    }

    /// <summary>
    /// The host's default handler for a vector, at level 3 with the interrupt's frame on the
    /// program's stack: INT 31h's services, and for anything else a reflection down to real mode.
    /// </summary>
    private void DefaultInterrupt()
    {
        int vector = (int)((Cpu.Eip - 3 - InterruptStubs) / 8);
        if (vector == 0x31)
        {
            Services();
            return;
        }

        if (vector == 0x2F && (ushort)Cpu.Eax == 0x1686)
        {
            // "which mode is the processor in?": protected
            Cpu.Eax &= 0xFFFF_0000;
            return;
        }

        Reflect(vector);
    }

    /// <summary>
    /// Reflects an interrupt down to its real-mode vector with the program's registers, on the
    /// host's real-mode stack. The registers and flags it comes back with are the program's.
    /// </summary>
    private void Reflect(int vector)
    {
        uint flags = ReadFrameValue(FrameFlags());
        Save(PendingKind.Reflection, vector);
        _reflecting[vector]++;
        SwitchDown(flags);
        Cpu.Ds = 0;
        Cpu.Es = 0;
        Cpu.Fs = 0;
        Cpu.Gs = 0;
        RealInterrupt(vector);
    }

    /// <summary>
    /// The stub in a vector's real-mode entry while the program has a protected-mode handler for
    /// it: the interrupt goes up to that handler, unless the host is reflecting the vector down
    /// already, or the handler has gone; then it passes on to the vector the stub replaced.
    /// </summary>
    private void Up()
    {
        int vector = (int)((Cpu.Eip - 3 - UpStubs) / 4);
        var handler = _vectors[vector];
        if (_reflecting[vector] > 0 || IsDefault(vector, handler))
        {
            Cpu.Cs = (ushort)(_chained[vector] >> 16);
            Cpu.Eip = (ushort)_chained[vector];
            return;
        }

        Save(PendingKind.Up, vector);
        SwitchUp(UpReturn);
        Cpu.InterruptThrough(handler.Selector, handler.Offset, _client32);
    }

    /// <summary>The program's handler has returned from an interrupt brought up from real mode: back down, to the stub's IRET.</summary>
    private void UpReturned() => Cpu.RestoreContext(Finish(PendingKind.Up).Saved);

    private bool IsDefault(int vector, FarPointer handler)
        => handler.Selector == HostCode3 && handler.Offset == InterruptStubs + ((uint)vector * 8);

    /// <summary>
    /// A new protected-mode handler for a vector. A vector at or above 20h gets it in its gate, so
    /// a program's <c>INT</c> goes straight there; one that real-mode interrupts come up for gets
    /// the host's up stub in its real-mode entry while the handler is the program's own.
    /// </summary>
    private void SetVector(int vector, FarPointer handler)
    {
        _vectors[vector] = handler;
        if (vector >= 0x20)
        {
            WriteGate(vector);
        }

        if (!ComesUp(vector))
        {
            return;
        }

        uint stub = ((uint)Machine.BiosSegment << 16) | (uint)(UpStubs + (vector * 4));
        uint current = RealVector(vector);
        if (!IsDefault(vector, handler) && current != stub)
        {
            _chained[vector] = current;
            SetRealVector(vector, stub);
        }
        else if (IsDefault(vector, handler) && current == stub)
        {
            SetRealVector(vector, _chained[vector]);
        }
    }

    private uint RealVector(int vector)
        => ((uint)machine.ReadWord(0, ((uint)vector * 4) + 2) << 16) | machine.ReadWord(0, (uint)vector * 4);

    private void SetRealVector(int vector, uint segmentOffset)
    {
        machine.WriteWord(0, (uint)vector * 4, (ushort)segmentOffset);
        machine.WriteWord(0, ((uint)vector * 4) + 2, (ushort)(segmentOffset >> 16));
    }

    // ---- exceptions --------------------------------------------------------------------------

    /// <summary>
    /// An exception, at level 0 on the host's stack, with the program's SS, ESP, EFLAGS, CS and
    /// EIP (and an error code, for some) pushed. A program's <c>INT</c> to a vector below 20h
    /// arrives here too, as a general protection fault, since those gates are level 0's.
    /// </summary>
    private void Exception()
    {
        int vector = (int)((Cpu.Eip - 3 - ExceptionStubs) / 8);
        bool hasError = vector is 8 or (>= 10 and <= 14) or 17;
        uint at = HostBase + Cpu.Esp;
        uint error = hasError ? Read32(at) : 0;
        uint frame = hasError ? at + 4 : at;
        uint eip = Read32(frame), flags = Read32(frame + 8), esp = Read32(frame + 12);
        ushort cs = (ushort)Read32(frame + 4), ss = (ushort)Read32(frame + 16);
        if ((cs & 3) == 0)
        {
            throw new NotSupportedException($"The DPMI host faulted: exception {vector:X2}h, error {error:X4}h, at {cs:X4}:{eip:X8}.");
        }

        if (hasError)
        {
            // the error code off the stack, so the frame is the one IRET takes
            Cpu.Esp += 4;
        }

        uint frameAt = HostBase + Cpu.Esp;
        byte opcode = machine.Read(DescriptorBase(cs) + eip);

        if (vector == 13 && (error & 3) == 2 && opcode is 0xCD or 0xCC or 0xCE)
        {
            // a program's INT n below 20h: as if it had gone through the gate to the handler
            int number = (int)(error >> 3);
            uint returnIp = eip + (opcode == 0xCD ? 2u : 1u);
            PushInterruptFrame(frameAt, ss, esp, flags, cs, returnIp, _vectors[number]);
            return;
        }

        if (vector == 13 && error == 0 && opcode == 0xF4)
        {
            // HLT, which level 3 may not run: the program waits for the next interrupt
            ResumeProgram(eip + 1, cs, flags, esp, ss);
            Cpu.Halted = (flags & InterruptFlag) != 0;
            return;
        }

        var handler = _exceptionHandlers[vector];
        machine.Trace($"DPMI exception {vector:X2}h, error {error:X4}h, at {cs:X4}:{eip:X8}, stack {ss:X4}:{esp:X8}: to {handler.Selector:X4}:{handler.Offset:X8}");
        if (handler.Selector == HostCode3 && handler.Offset == DefaultException)
        {
            if (vector < 8 && !IsDefault(vector, _vectors[vector]))
            {
                // an exception the program handles as an interrupt
                PushInterruptFrame(frameAt, ss, esp, flags, cs, eip, _vectors[vector]);
                return;
            }

            throw Stopped(vector, error, cs, eip);
        }

        // the program's exception handler, on the locked stack, with the DPMI exception frame:
        // a return to the host, the error code, and where the exception happened
        uint size = _client32 ? 4u : 2u;
        uint locked = LockedStack(ss, esp);
        uint[] values = [ExceptionReturn, HostCode3, error, eip, cs, flags, esp, ss];
        locked -= size * (uint)values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            WriteSized(HostBase + locked + ((uint)i * size), values[i], size);
        }

        WriteFrame(frameAt, handler.Offset, handler.Selector, (flags & ~(InterruptFlag | TrapFlag | NestedTask)) | Iopl3, locked, HostStack3);
    }

    /// <summary>
    /// The program's exception handler has returned, far, to the host: on to where its frame says,
    /// with the registers the handler left.
    /// </summary>
    private void ExceptionReturned()
    {
        uint size = _client32 ? 4u : 2u;
        uint at = Cpu.SegmentBase(Intel386.SS) + StackPointer();
        uint eip = ReadSized(at + size, size), cs = ReadSized(at + (2 * size), size), flags = ReadSized(at + (3 * size), size);
        uint esp = ReadSized(at + (4 * size), size), ss = ReadSized(at + (5 * size), size);
        if (!_client32)
        {
            flags |= Cpu.Eflags & 0xFFFF_0000;
        }

        ResumeProgram(eip, (ushort)cs, flags, esp, (ushort)ss);
    }

    /// <summary>The program jumped to the host's default exception handler: there's nothing more to do for it.</summary>
    private void DefaultExceptionReached()
    {
        uint size = _client32 ? 4u : 2u;
        uint at = Cpu.SegmentBase(Intel386.SS) + StackPointer();
        uint error = ReadSized(at + (2 * size), size), eip = ReadSized(at + (3 * size), size), cs = ReadSized(at + (4 * size), size);
        throw Stopped(-1, error, (ushort)cs, eip);
    }

    private static NotSupportedException Stopped(int vector, uint error, ushort cs, uint eip)
    {
        string which = vector switch
        {
            -1 => "an exception it passed on",
            0 => "a divide error",
            6 => "an invalid opcode",
            12 => "a stack fault",
            13 => "a general protection fault",
            14 => "a page fault",
            _ => $"exception {vector:X2}h",
        };
        return new NotSupportedException($"The program stopped on {which} (error code {error:X4}h) at {cs:X4}:{eip:X8}.");
    }

    /// <summary>Goes on in the program, at level 3, as an IRET to it would.</summary>
    private void ResumeProgram(uint eip, ushort cs, uint flags, uint esp, ushort ss)
    {
        Load(Intel386.CS, cs);
        Load(Intel386.SS, ss);
        Cpu.Esp = esp;
        Cpu.Eip = eip;
        Cpu.Eflags = (flags & ~NestedTask) | Iopl3;
    }

    /// <summary>
    /// Makes the level 0 frame return into a handler as an interrupt would have entered it: the
    /// program's flags, CS and EIP pushed on its own stack, and interrupts off.
    /// </summary>
    private void PushInterruptFrame(uint frameAt, ushort ss, uint esp, uint flags, ushort cs, uint eip, FarPointer handler)
    {
        uint size = _client32 ? 4u : 2u;
        bool big = DescriptorBig(ss);
        uint stackBase = DescriptorBase(ss);
        uint mask = big ? 0xFFFF_FFFF : 0xFFFF;
        uint top = esp;
        foreach (uint value in (ReadOnlySpan<uint>)[flags, cs, eip])
        {
            top -= size;
            WriteSized(stackBase + (top & mask), value, size);
        }

        uint newEsp = big ? top : (esp & 0xFFFF_0000) | (top & 0xFFFF);
        WriteFrame(frameAt, handler.Offset, handler.Selector, flags & ~(InterruptFlag | TrapFlag | NestedTask), newEsp, ss);
    }

    private void WriteFrame(uint at, uint eip, ushort cs, uint flags, uint esp, ushort ss)
    {
        Write32(at, eip);
        Write32(at + 4, cs);
        Write32(at + 8, flags);
        Write32(at + 12, esp);
        Write32(at + 16, ss);
    }

    // ---- the frame of an interrupt the host is answering --------------------------------------

    /// <summary>The stack pointer, all of ESP on a 32-bit stack, SP on a 16-bit one.</summary>
    private uint StackPointer() => Cpu.SegmentBig(Intel386.SS) ? Cpu.Esp : Cpu.Esp & 0xFFFF;

    /// <summary>Where the flags an interrupt pushed are: the third value on the stack.</summary>
    private uint FrameFlags() => Cpu.SegmentBase(Intel386.SS) + StackPointer() + (_client32 ? 8u : 4u);

    private uint ReadFrameValue(uint at) => ReadSized(at, _client32 ? 4u : 2u);

    private void WriteFrameValue(uint at, uint value) => WriteSized(at, value, _client32 ? 4u : 2u);

    /// <summary>Sets or clears carry in the flags the interrupt will return with.</summary>
    private void SetReturnCarry(bool on)
    {
        uint at = FrameFlags();
        uint flags = ReadFrameValue(at);
        WriteFrameValue(at, on ? flags | Carry : flags & ~Carry);
    }

    // ---- memory ------------------------------------------------------------------------------

    private uint Read16(uint at) => (uint)(machine.Read(at) | (machine.Read(at + 1) << 8));

    private uint Read32(uint at) => Read16(at) | (Read16(at + 2) << 16);

    private void Write16(uint at, uint value)
    {
        machine.Write(at, (byte)value);
        machine.Write(at + 1, (byte)(value >> 8));
    }

    private void Write32(uint at, uint value)
    {
        Write16(at, value);
        Write16(at + 2, value >> 16);
    }

    private uint ReadSized(uint at, uint size) => size == 4 ? Read32(at) : Read16(at);

    private void WriteSized(uint at, uint value, uint size)
    {
        if (size == 4)
        {
            Write32(at, value);
        }
        else
        {
            Write16(at, value);
        }
    }

    private void WriteBytes(uint at, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            machine.Write(at + (uint)i, bytes[i]);
        }
    }
}
