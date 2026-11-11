using Excavate.Core.Devices;
using Excavate.Core.Dos;
using Excavate.Core.Dpmi;
using Excavate.Core.Processors;

namespace Excavate.Core;

/// <summary>
/// The PC a game runs on: a 386, memory, the I/O ports, and a BIOS, DOS, an expanded-memory
/// manager and a DPMI host for protected-mode programs, all answered in C# instead of loaded as
/// code.
/// </summary>
/// <remarks>
/// <para>
/// The memory map is the PC's. The first 640 KB is conventional memory, holding the interrupt
/// table, the BIOS data area, DOS's memory arena and the program. The video memory sits at
/// A0000h, expanded memory's 64 KB page frame at E0000h, and the BIOS at F0000h. The memory past
/// 1 MB, to 16 MB, is reachable only while the A20 gate is open; while it's closed, addresses wrap
/// at 1 MB as on the 8086. The DPMI host keeps its tables just past the first 64 KB of it, and
/// hands out the rest to protected-mode programs.
/// </para>
/// <para>
/// Nothing in the machine is loaded from a real BIOS or DOS. Each interrupt they would answer
/// points at a four-byte stub in the BIOS area: the bytes <c>0F FF</c> and a service number,
/// which call into C# (see <see cref="Intel386.HostCall"/>), then <c>IRET</c>. A program sees
/// ordinary interrupt vectors, and can replace them or chain to them as it would on a real PC.
/// </para>
/// </remarks>
public sealed partial class Machine : ICpuBus
{
    /// <summary>The segment of the BIOS area, where the service stubs live.</summary>
    internal const ushort BiosSegment = 0xF000;

    // the repetitions of a string instruction between chances for an interrupt: at 30 million
    // instructions a second, a few hundredths of a millisecond
    private const int RepeatLimit = 1024;

    /// <summary>
    /// The machine's memory: 16 MB, enough for the protected-mode games (<i>Wing Commander III</i>
    /// asks for 8). Past the first megabyte it's reachable only while the A20 gate is open.
    /// </summary>
    internal const uint MemorySize = 0x100_0000;

    private const uint PageFrame = 0xE_0000;
    private const uint VideoMemory = 0xA_0000;

    private readonly byte[] _memory = new byte[MemorySize];

    // for each 4 KB page, whether it reads, and writes, as plain memory, which the processor
    // reaches directly: not the video memory, the EMS page frame or (for writing) the BIOS's ROM,
    // nor past 1 MB with the A20 gate closed, where addresses wrap
    [NotSaved]
    private readonly bool[] _plainRead = new bool[MemorySize >> 12], _plainWrite = new bool[MemorySize >> 12];
    private bool _a20;
    private readonly ExpandedMemory _ems;
    private readonly DosDrive _drive;
    private readonly DosServices _dos;
    private readonly DpmiHost _dpmi;
    private readonly InterruptController _pic;

    // the second interrupt controller, for IRQ 8 to 15, with only the mouse's IRQ 12 let through
    // at the start, as the mouse driver leaves it
    private readonly InterruptController _pic2 = new(vectorBase: 0x70, mask: 0xEF);
    private readonly IntervalTimer _timer = new();
    private readonly Vga _vga = new();
    private readonly KeyboardController _keyboard = new();
    private readonly GamePort _gamePort = new();

    // the time, counted in instructions, with those the processor would have run while halted
    private long _time;

    // when counter 0 next raises IRQ 0, in timer ticks
    private long _nextIrq0 = 0x10000;

    // port 61h: the speaker's gate and data bits as last written
    private byte _portB;

    /// <summary>
    /// Makes a machine that runs programs from a game's folder, mounted as drive C:. The folder
    /// is only read: anything a program writes, saved games included, goes to the save folder,
    /// and files there take the place of the game folder's from then on.
    /// </summary>
    public Machine(string gameFolder, string saveFolder)
    {
        Cpu = new Intel386(this) { HostCall = HostCall, RepeatLimit = RepeatLimit };
        _pic = new InterruptController(second: _pic2);
        MapMemory();
        _timer.Reloaded0 += () => _nextIrq0 = _timer.Start0 + _timer.Period0;
        _keyboard.Interrupt += () => _pic.Raise(1);
        _ems = new ExpandedMemory(this);
        _drive = new DosDrive(gameFolder, saveFolder);
        _dos = new DosServices(this, _drive);
        _dpmi = new DpmiHost(this, _dos);
        InstallBios();
        _ems.Install();
        _dpmi.Install();
        InstallSound();
    }

    /// <summary>The processor.</summary>
    public Intel386 Cpu { get; }

    /// <summary>
    /// A line for each BIOS, DOS and EMS service a program calls, and each I/O port it uses that
    /// nothing answers yet. It's the record of what a game needs from the machine.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Whether the program has ended, by its DOS call or an <c>INT 20h</c>; or, for a batch file,
    /// whether the batch file has, after its last program.
    /// </summary>
    public bool Exited { get; internal set; }

    /// <summary>The program's exit code, once it has ended: for a batch file, its last program's.</summary>
    public int ExitCode { get; internal set; }

    /// <summary>The video mode the program last chose through the BIOS; 3, 80 by 25 text, at the start.</summary>
    public int VideoMode => _vga.Mode;

    /// <summary>The width of the picture the display shows, in pixels.</summary>
    public int DisplayWidth => _vga.Size.Width;

    /// <summary>The height of the picture the display shows, in pixels.</summary>
    public int DisplayHeight => _vga.Size.Height;

    /// <summary>
    /// Draws the picture the display shows, <see cref="DisplayWidth"/> by <see cref="DisplayHeight"/>
    /// pixels of 32-bit ARGB, row by row. Text modes are drawn black for now.
    /// </summary>
    public void Render(Span<uint> pixels) => _vga.Render(pixels);

    /// <summary>
    /// How fast the machine runs, in instructions per second. Time in the machine (the timer's
    /// count, and so the game's pace) follows the instructions run, so this is the emulated
    /// processor's speed. DOSBox's "cycles" setting is roughly this in thousands: GOG runs
    /// Wing Commander at 4000.
    /// </summary>
    public int InstructionsPerSecond { get; set; } = 4_000_000;

    /// <summary>The emulated time since the machine started.</summary>
    public TimeSpan Time => TimeSpan.FromSeconds((double)_time / InstructionsPerSecond);

    /// <summary>
    /// How many analog joysticks are plugged into the game port: 0, 1 or 2. Games look for them
    /// when they start, so this is best set before running.
    /// </summary>
    public int Joysticks
    {
        get => _gamePort.Sticks;
        set => _gamePort.Sticks = Math.Clamp(value, 0, 2);
    }

    /// <summary>
    /// Moves a joystick axis: 0 and 1 are the first stick's x and y, 2 and 3 the second's. The
    /// value runs from -1 (left, or up) through 0 (centered) to 1 (right, or down).
    /// </summary>
    public void SetJoystickAxis(int axis, float value) => _gamePort.SetAxis(axis & 3, value);

    /// <summary>Presses or lets go of a joystick button: 0 and 1 are the first stick's, 2 and 3 the second's.</summary>
    public void SetJoystickButton(int button, bool down) => _gamePort.SetButton(button & 3, down);

    /// <summary>Whether the A20 gate is open, so that addresses past 1 MB reach the high memory area.</summary>
    public bool A20
    {
        get => _a20;
        set
        {
            _a20 = value;
            MapMemory();
        }
    }

    /// <summary>
    /// Puts a host folder on a directory of drive C:, read-only, as the game's folder is: what a
    /// program writes there goes to the save folder. For a game whose data is expected beside
    /// another program, as W.C.A.T. expects the game's beside its launcher, without copying it.
    /// </summary>
    /// <param name="directory">The directory, from the root, as in <c>\GAMEWC1</c>.</param>
    /// <param name="folder">The host folder that appears there.</param>
    public void Mount(string directory, string folder)
    {
        if (_drive.Parse(directory.StartsWith('\\') ? directory : "\\" + directory) is not { Length: > 0 } parts)
        {
            throw new ArgumentException($"{directory} isn't a directory of drive C:.", nameof(directory));
        }

        _drive.Mount(parts, Path.GetFullPath(folder));
    }

    /// <summary>
    /// Puts a CD's image, in ISO 9660, in drive D:, read through MSCDEX as a DOS drive. Programs
    /// can be run from it, as <c>D:\WC3.EXE</c>.
    /// </summary>
    /// <param name="image">The image's file, as GOG's <c>DATA.DAT</c>.</param>
    /// <returns>The volume's name, as in <c>WC3_CD1</c>.</returns>
    /// <exception cref="InvalidDataException">The file isn't an ISO 9660 image.</exception>
    public string InsertCd(string image)
    {
        var iso = new IsoImage(image);
        _dos.InsertCd(iso);
        return iso.Label;
    }

    /// <summary>
    /// Loads a program from drive C: as DOS would, with a command tail, and readies the
    /// processor to run it. A batch file (<c>.BAT</c>) is run as COMMAND.COM would, program
    /// after program, and the machine runs on until the batch file ends.
    /// </summary>
    /// <param name="program">The program's name, as in <c>WC.EXE</c>, or a batch file's.</param>
    /// <param name="arguments">What follows the program's name on the command line.</param>
    public void Load(string program, string arguments = "") => _dos.Load(program, arguments);

    /// <summary>
    /// Runs for the given number of instructions' time, or until the program exits or the
    /// processor halts with interrupts off, which nothing can end. A halted processor with
    /// interrupts on waits for the next one, and the time passes. A service the machine doesn't
    /// provide yet throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <returns>Whether the program is still running.</returns>
    public bool Run(long instructions)
    {
        long end = _time + instructions;
        while (!Exited && _time < end)
        {
            // the devices first: what's come due, and what the host did since the last run, a
            // key pressed say, then the processor until something happens
            ServiceDevices();
            if (Cpu.Halted)
            {
                if (!Cpu.InterruptsEnabled)
                {
                    return false;
                }

                // nothing happens until the next interrupt, so the time skips to it: the timer's,
                // or the sound card's
                _time = Math.Min(NextDeviceEvent(), end);
            }
            else
            {
                RunUntilSomethingHappens(Math.Min(end, NextDeviceEvent()));
                if (_dos.BetweenPrograms)
                {
                    // the program ended inside a batch file: on to its next line, which
                    // usually loads the next program over this one's stack and registers
                    _dos.NextCommand();
                }
            }
        }

        UpdateSound();
        return !Exited;
    }

    /// <summary>
    /// Runs instructions until the time given, or until something happens that the devices have
    /// to see to: the program uses an I/O port or calls the BIOS, DOS or the DPMI host, the
    /// processor halts, or an interrupt is waiting with interrupts on. Between those, nothing
    /// outside the processor changes, so the timer, the sound and the interrupt controller are
    /// seen to once for the whole run of instructions rather than after each one, which is where
    /// half the time went.
    /// </summary>
    private void RunUntilSomethingHappens(long until)
    {
        // a repeated string instruction takes a step of time for each repetition
        Cpu.InterruptWaiting = _pic.HasInterrupt;
        _time += Cpu.RunFor(until - _time);
    }

    /// <summary>
    /// The devices' part between runs of instructions: the timer's interrupt and the sound card's
    /// event if they're due, the keyboard's next scan code, and an interrupt for the processor.
    /// </summary>
    private void ServiceDevices()
    {
        UpdateTimer();
        if (_time >= _soundEventAt)
        {
            SoundEvent();
        }

        _keyboard.Update();
        if (_pic.HasInterrupt && Cpu.InterruptsEnabled)
        {
            int vector = _pic.Acknowledge();
            if (Cpu.ProtectedMode && _dpmi.Active)
            {
                // in protected mode the vector's place in the table is an exception's: the
                // DPMI host delivers it to the program's handler, or down to real mode
                _dpmi.HardwareInterrupt(vector);
            }
            else
            {
                Cpu.Interrupt(vector);
            }
        }
    }

    /// <summary>When the next device's event is due, in the machine's time: the timer's interrupt, or the sound card's.</summary>
    private long NextDeviceEvent()
    {
        long irq0 = _nextIrq0 == long.MaxValue ? long.MaxValue : (long)Math.Ceiling(_nextIrq0 * InstructionsPerSecond / IntervalTimer.Frequency);
        return Math.Max(_time + 1, Math.Min(irq0, _soundEventAt));
    }

    private long TimerTicks => (long)(_time * IntervalTimer.Frequency / InstructionsPerSecond);

    private void UpdateTimer()
    {
        long now = TimerTicks;
        _timer.Now = now;
        if (now >= _nextIrq0)
        {
            _pic.Raise(0);

            // if interrupts were off for a while, the ticks missed are lost, as on the PC
            long period = _timer.Period0;
            _nextIrq0 = _timer.Periodic0 ? _nextIrq0 + ((((now - _nextIrq0) / period) + 1) * period) : long.MaxValue;
        }
    }

    // ---- the bus -----------------------------------------------------------------------------

    /// <inheritdoc/>
    public byte Read(uint address)
    {
        address = Wrap(address);
        if (address - VideoMemory < 0x2_0000)
        {
            return _vga.Read(address);
        }

        if (address - PageFrame < 0x1_0000)
        {
            return _ems.Read(address - PageFrame);
        }

        return address < MemorySize ? _memory[address] : (byte)0xFF;
    }

    /// <inheritdoc/>
    public void Write(uint address, byte value)
    {
        address = Wrap(address);
        if (address - VideoMemory < 0x2_0000)
        {
            _vga.Write(address, value);
        }
        else if (address - PageFrame < 0x1_0000)
        {
            _ems.Write(address - PageFrame, value);
        }
        else if (address < 0xF_0000 || (address >= 0x10_0000 && address < MemorySize))
        {
            // the BIOS is ROM
            _memory[address] = value;
        }
    }

    /// <inheritdoc/>
    public byte In(ushort port)
    {
        Cpu.Stopping = true;
        _timer.Now = TimerTicks;
        switch (port)
        {
            case 0x20:
            case 0x21:
                return _pic.Read(port);
            case 0xA0:
            case 0xA1:
                return _pic2.Read(port);
            case >= 0x40 and <= 0x43:
                return _timer.Read(port);
            case 0x61:
                // port B: the speaker bits as written, memory refresh toggling every 15 us (which
                // programs time short delays by), and counter 2's output
                return (byte)((_portB & 0x0F) | (((_timer.Now / 18) & 1) != 0 ? 0x10 : 0) | (_timer.Output2 ? 0x20 : 0));
            case 0x60:
            case 0x64:
                return _keyboard.In(port);
            case 0x92:
                return (byte)(A20 ? 0x02 : 0x00);
            case 0x201:
                return _gamePort.Read(Seconds);
            case >= 0x388 and <= 0x38B:
            case >= 0x220 and <= 0x223:
            case 0x228:
            case 0x229:
                // the FM synthesizer: the AdLib's ports, and the Sound Blaster's (its first four
                // reach both banks, and 228h and 229h the first, as on an AdLib)
                return _opl.Read(port, Seconds);
            case 0x330:
            case 0x331:
                return _mpu.Read(port);
            case >= 0x224 and <= 0x22F:
                return _soundBlaster.Read(port);
            case <= 0x0F:
            case 0x81:
            case 0x82:
            case 0x83:
            case 0x87:
                // the DMA controller's addresses and counts move as the sound plays
                UpdateSound();
                return _dma.In(port);
            case >= 0xC0 and <= 0xDF:
            case 0x89:
            case 0x8A:
            case 0x8B:
            case 0x8F:
                // the second DMA controller, for 16-bit transfers, and its page registers
                UpdateSound();
                return _wideDma.In(port);
            case >= 0x3C0 and <= 0x3CF:
            case 0x3D4:
            case 0x3D5:
            case 0x3DA:
                _vga.Time = Seconds;
                return _vga.In(port);
            default:
                LogPort(port, "in");
                return 0xFF;
        }
    }

    /// <inheritdoc/>
    public void Out(ushort port, byte value)
    {
        Cpu.Stopping = true;
        _timer.Now = TimerTicks;
        switch (port)
        {
            case 0x20:
            case 0x21:
                _pic.Write(port, value);
                break;
            case 0xA0:
            case 0xA1:
                _pic2.Write(port, value);
                break;
            case >= 0x40 and <= 0x43:
                _timer.Write(port, value);
                break;
            case 0x61:
                _portB = value;
                _timer.Gate2 = (value & 1) != 0;
                break;
            case 0x60:
            case 0x64:
                _keyboard.Out(port, value);
                A20 = _keyboard.A20;
                break;
            case 0x92:
                // the fast A20 gate of later machines
                A20 = (value & 0x02) != 0;
                break;
            case 0x201:
                _gamePort.Write(Seconds);
                break;
            case >= 0x388 and <= 0x38B:
            case >= 0x220 and <= 0x223:
            case 0x228:
            case 0x229:
                UpdateSound();
                _opl.Write(port, value, Seconds);
                break;
            case 0x330:
            case 0x331:
                UpdateSound();
                _mpu.Write(port, value);
                break;
            case >= 0x224 and <= 0x22F:
                UpdateSound();
                _soundBlaster.Write(port, value);
                ScheduleSoundEvent();
                break;
            case <= 0x0F:
            case 0x81:
            case 0x82:
            case 0x83:
            case 0x87:
                UpdateSound();
                _dma.Out(port, value);
                ScheduleSoundEvent();
                break;
            case >= 0xC0 and <= 0xDF:
            case 0x89:
            case 0x8A:
            case 0x8B:
            case 0x8F:
                UpdateSound();
                _wideDma.Out(port, value);
                ScheduleSoundEvent();
                break;
            case >= 0x3C0 and <= 0x3CF:
            case 0x3D4:
            case 0x3D5:
                _vga.Out(port, value);
                break;
            default:
                LogPort(port, $"out {value:X2}");
                break;
        }
    }

    private readonly HashSet<ushort> _portsSeen = [];

    private void LogPort(ushort port, string what)
    {
        // each port once: a game polling one would otherwise fill the log
        if (_portsSeen.Add(port))
        {
            Log?.Invoke($"port {port:X4} {what}: nothing answers yet");
        }
    }

    private uint Wrap(uint address) => A20 ? address : address & 0xF_FFFF;

    /// <summary>Marks the pages of plain memory for the processor, as the A20 gate leaves them.</summary>
    private void MapMemory()
    {
        for (uint page = 0; page < _plainRead.Length; page++)
        {
            uint address = page << 12;
            bool high = address >= 0x10_0000;
            bool plain = (!high || _a20) && address - VideoMemory >= 0x2_0000 && address - PageFrame >= 0x1_0000;
            _plainRead[page] = plain;
            _plainWrite[page] = plain && (high || address < 0xF_0000);
        }

        Cpu.MapMemory(_memory, _plainRead, _plainWrite);
    }

    /// <summary>Copies a block of extended memory, as the DPMI host does when it moves a block it resizes.</summary>
    internal void MoveMemory(uint from, uint to, uint length)
        => _memory.AsSpan((int)from, (int)length).CopyTo(_memory.AsSpan((int)to, (int)length));

    // ---- memory helpers for the services -----------------------------------------------------

    internal byte ReadByte(ushort segment, uint offset) => Read(((uint)segment << 4) + (offset & 0xFFFF));

    internal ushort ReadWord(ushort segment, uint offset) =>
        (ushort)(ReadByte(segment, offset) | (ReadByte(segment, offset + 1) << 8));

    internal void WriteByte(ushort segment, uint offset, byte value) => Write(((uint)segment << 4) + (offset & 0xFFFF), value);

    internal void WriteWord(ushort segment, uint offset, ushort value)
    {
        WriteByte(segment, offset, (byte)value);
        WriteByte(segment, offset + 1, (byte)(value >> 8));
    }

    /// <summary>Writes into the BIOS area, which programs can't.</summary>
    internal void WriteRom(uint address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_memory.AsSpan((int)address));

    /// <summary>A string ending in a zero byte, as DOS takes file names.</summary>
    internal string ReadString(ushort segment, uint offset, int max = 128)
    {
        var chars = new List<char>();
        for (int i = 0; i < max; i++)
        {
            byte b = ReadByte(segment, offset + (uint)i);
            if (b == 0)
            {
                break;
            }

            chars.Add((char)b);
        }

        return new string([.. chars]);
    }

    /// <summary>
    /// Writes bytes from a segment and offset onward, by linear address: a block longer than
    /// 64 KB, like a program's image, carries on past the segment's end instead of wrapping.
    /// </summary>
    internal void WriteBytes(ushort segment, uint offset, ReadOnlySpan<byte> bytes)
    {
        uint address = ((uint)segment << 4) + offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            Write(address + (uint)i, bytes[i]);
        }
    }

    internal void Trace(string line) => Log?.Invoke(line);

    /// <summary>The program has ended: a DPMI host it was using lets it go.</summary>
    internal void ProgramEnded() => _dpmi.Release();

    /// <summary>Closes every file the program has open on the host, for a test that's done with the machine.</summary>
    internal void CloseFiles() => _dos.CloseFiles();

    // ---- the services ------------------------------------------------------------------------

    /// <summary>
    /// Sets or clears carry in the flags an interrupt will return with. A service runs between the
    /// <c>INT</c> and the stub's <c>IRET</c>, which restores the flags the <c>INT</c> pushed, so a
    /// result reported in a flag has to be written into that pushed copy.
    /// </summary>
    internal void SetReturnFlag(uint flag, bool on)
    {
        uint sp = Cpu.Esp & 0xFFFF;
        ushort flags = ReadWord(Cpu.Ss, sp + 4);
        flags = on ? (ushort)(flags | flag) : (ushort)(flags & ~flag);
        WriteWord(Cpu.Ss, sp + 4, flags);
    }

    internal void SetReturnCarry(bool on) => SetReturnFlag(Intel386.Flag.Carry, on);

    internal void SetReturnZero(bool on) => SetReturnFlag(Intel386.Flag.Zero, on);

    /// <summary>
    /// Makes the service run again, for one that must wait (for a key, say): the processor goes
    /// back to the stub's call into C#, with interrupts on, so the keyboard's can come in while
    /// it waits, as they do while real DOS and the BIOS wait. The service is called again and
    /// again, inside its stub, until it can answer.
    /// </summary>
    /// <remarks>
    /// The wait has to stay inside the stub. It once moved the caller's return address back over
    /// the two-byte <c>INT</c>, which is right only when the caller was an <c>INT</c>. <i>Wing
    /// Commander</i> hooks INT 21h and passes DOS calls on with <c>PUSHF</c> and a five-byte
    /// <c>CALL FAR</c>; moved back two bytes, its return landed inside that instruction, and
    /// waiting for a key at its name prompt ran stray code until the game locked up.
    /// </remarks>
    internal void RepeatInterrupt()
    {
        // the stub's 0F FF and service number, just run
        Cpu.Eip -= 3;
        Cpu.Eflags |= Intel386.Flag.Interrupt;
    }

    private void HostCall(byte service)
    {
        Cpu.Stopping = true;
        switch (service)
        {
            case 0x08: TimerTick(); break;
            case 0x09: KeyboardInterrupt(); break;
            case >= 0x0A and <= 0x0F: _pic.EndOfInterrupt(); break;
            case MouseEventService: MouseEvent(); break;
            case >= 0x70 and <= 0x77:
                _pic2.EndOfInterrupt();
                _pic.EndOfInterrupt();
                break;
            case 0x10: VideoService(); break;
            case 0x11: Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | ReadWord(0x40, 0x10); break;
            case 0x12: Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | ReadWord(0x40, 0x13); break;
            case 0x15: SystemService(); break;
            case 0x16: KeyboardService(); break;
            case 0x1A: ClockService(); break;
            case 0x20: _dos.Terminate(0); break;
            case 0x21: _dos.Call(); break;
            case 0x2F: MultiplexService(); break;
            case 0x33: MouseService(); break;
            case 0x67: _ems.Call(); break;
            case >= DpmiHost.FirstService and <= DpmiHost.LastService: _dpmi.Call(service); break;
            default: throw new NotSupportedException($"No service {service:X2}h.");
        }
    }
}
