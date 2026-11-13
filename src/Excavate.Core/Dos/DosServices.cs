using System.Buffers.Binary;
using System.Text;
using Excavate.Core.Disks;

namespace Excavate.Core.Dos;

/// <summary>
/// DOS, answered in C#: the program loader, the memory arena, files on drive C:, and the rest of
/// INT 21h that games use. It behaves as MS-DOS 5.0 reports itself to programs.
/// </summary>
internal sealed partial class DosServices(Machine machine, DosDrive drive)
{
    /// <summary>
    /// The first paragraph of the memory arena. Real DOS, with its drivers and buffers, leaves
    /// programs from roughly 0800h to 1500h depending on the configuration; DOSBox, with almost
    /// nothing resident, from about 01A0h. Both Wing Commander games are known to need loading
    /// higher than DOSBox does (GOG starts them with LOADFIX), so the arena starts at 40 KB,
    /// which still leaves 600 KB free, as on a well-tuned DOS 5 machine.
    /// </summary>
    private const ushort ArenaStart = 0x0A00;

    /// <summary>The paragraph past conventional memory, where video memory begins.</summary>
    private const ushort ArenaEnd = 0xA000;

    private ushort _psp;
    private ushort _dtaSegment;
    private ushort _dtaOffset;
    private int _lastError;
    private readonly StringBuilder _console = new();

    private readonly Registers _registers = new(machine);

    // ---- loading a program -------------------------------------------------------------------

    // the batch files being run, made when the first is
    private CommandShell? _shell;

    // a program has ended while a batch file runs: the next line is to be run
    private bool _betweenPrograms;

    /// <summary>
    /// Loads a program from drive C: or the CD, or starts a batch file, which loads the first
    /// program it names. When a batch file runs none, the machine has ended before it started.
    /// A program named with its drive's letter is run from that drive, as if its letter had been
    /// typed first: <c>D:\WC3.EXE</c> leaves D: the current drive, as GOG's setup does.
    /// </summary>
    public void Load(string program, string arguments)
    {
        if (program.Length >= 2 && char.ToUpperInvariant(program[0]) == 'A' && program[1] == ':')
        {
            LoadFromFloppy(program, arguments);
            return;
        }

        var location = Locate(program) ?? throw new FileNotFoundException($"{program} isn't on a drive.", program);
        if (program.Length >= 2 && program[1] == ':')
        {
            _currentDrive = location.Cd ? DriveD : DriveC;
        }

        if (!location.Cd && location.Parts.Length > 0 && location.Parts[^1].EndsWith(".BAT", StringComparison.Ordinal))
        {
            if (drive.FindFile(location.Parts) is null)
            {
                throw new FileNotFoundException($"{program} isn't on drive C:.", program);
            }

            _shell ??= new CommandShell(this, drive, machine);
            _shell.Start(location.Parts, arguments);
            NextCommand();
            return;
        }

        LoadProgram(location, arguments);
    }

    /// <summary>Whether a program has ended and a batch file has more lines to run.</summary>
    internal bool BetweenPrograms => _betweenPrograms;

    /// <summary>
    /// Runs the batch file on, to the next program it starts; when there's none, the machine has
    /// ended, with the last program's exit code.
    /// </summary>
    internal void NextCommand()
    {
        _betweenPrograms = false;
        if (_shell?.Continue() != true)
        {
            FlushConsole();
            machine.Exited = true;
        }
    }

    /// <summary>Loads a program by its path on drive C:, and readies the processor to run it.</summary>
    internal void LoadProgram(string[] parts, string arguments) => LoadProgram(new Location(false, parts), arguments);

    /// <summary>
    /// Loads a program from the disk in drive A:, as it was run from a game's floppy, which
    /// leaves A: the current drive. The disk is read as DOS reads a FAT12 disk; DOS's file calls
    /// don't reach it yet, so a program that then opens files on A: finds none there.
    /// </summary>
    private void LoadFromFloppy(string program, string arguments)
    {
        var disk = machine.DiskInDrive ?? throw new FileNotFoundException($"There's no disk in drive A: to run {program} from.", program);
        var fat = new FatImage(disk);
        if (DosDrive.Resolve(program[2..], "") is not { Length: > 0 } parts || fat.Find(parts) is not { IsDirectory: false } entry)
        {
            throw new FileNotFoundException($"{program} isn't on the disk in drive A:, {disk.Name}.", program);
        }

        InitializeArena();
        _currentDrive = DriveA;
        LoadImage(@"A:\" + DosDrive.Join(parts), parts[^1], fat.Read(entry), arguments);
    }

    /// <summary>Loads a program by its place on a drive, and readies the processor to run it.</summary>
    private void LoadProgram(Location location, string arguments)
    {
        InitializeArena();

        string dosPath = location.Name;
        byte[] image;
        if (location.Cd)
        {
            var entry = CdFile(location.Parts) ?? throw new FileNotFoundException($"{dosPath} isn't on the CD.", dosPath);
            using var stream = _cd!.Open();
            stream.Position = entry.Sector * IsoImage.SectorSize;
            image = new byte[entry.Size];
            stream.ReadExactly(image);
        }
        else
        {
            string file = drive.FindFile(location.Parts) ?? throw new FileNotFoundException($"{dosPath} isn't on drive C:.", dosPath);
            image = File.ReadAllBytes(file);
        }

        LoadImage(dosPath, location.Parts[^1], image, arguments);
    }

    /// <summary>Loads a program's file, read from wherever it is, with its environment, and readies the processor to run it.</summary>
    private void LoadImage(string dosPath, string program, byte[] image, string arguments)
    {
        ushort environmentSegment = BuildEnvironment(["COMSPEC=C:\\COMMAND.COM", "PATH=C:\\"], dosPath);

        if (image.Length < 0x1C || image[0] != 'M' || image[1] != 'Z')
        {
            throw new NotSupportedException($"{program} isn't an MZ executable; .COM programs aren't loaded yet.");
        }

        string tail = arguments.Length > 0 ? " " + arguments : "";
        if (!TryLoadExe(image, program, environmentSegment, tail, parent: 0))
        {
            throw new InvalidOperationException($"{program} needs more memory than is free.");
        }

        machine.Trace($"loaded {dosPath} at {_psp:X4}h, {arguments}");
    }

    /// <summary>
    /// A program's environment: its variables, then a word of 1 and the program's full path, by
    /// which programs find the folder they were run from.
    /// </summary>
    private ushort BuildEnvironment(IEnumerable<string> variables, string dosPath)
    {
        var environment = new List<byte>();
        foreach (string variable in variables)
        {
            environment.AddRange(Encoding.ASCII.GetBytes(variable));
            environment.Add(0);
        }

        environment.AddRange([0, 1, 0]);
        environment.AddRange(Encoding.ASCII.GetBytes(dosPath));
        environment.Add(0);
        ushort segment = Allocate((environment.Count + 15) / 16, 0) ?? throw new InvalidOperationException("No memory for the environment.");
        machine.WriteBytes(segment, 0, environment.ToArray());
        return segment;
    }

    /// <summary>
    /// Loads an MZ executable. Its header gives the size of the image, the relocations to apply,
    /// the memory it needs beyond the image, and its starting CS:IP and SS:SP, all relative to
    /// the segment the image lands at, just past the 256-byte program segment prefix. False,
    /// with nothing loaded, when there isn't the memory for it.
    /// </summary>
    /// <param name="file">The program's file.</param>
    /// <param name="program">Its name.</param>
    /// <param name="environment">Its environment's segment.</param>
    /// <param name="tail">Its command tail, as the PSP holds it: what followed its name, from the space before the arguments.</param>
    /// <param name="parent">The PSP of the program that ran it, or 0 for one run by the shell.</param>
    private bool TryLoadExe(byte[] file, string program, ushort environment, string tail, ushort parent)
    {
        string name = Path.GetFileNameWithoutExtension(program);
        ushort Word(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset));

        int lastPage = Word(0x02), pages = Word(0x04), relocations = Word(0x06), headerParagraphs = Word(0x08);
        int minimum = Word(0x0A), maximum = Word(0x0C);
        int fileSize = lastPage == 0 ? pages * 512 : ((pages - 1) * 512) + lastPage;
        int imageStart = headerParagraphs * 16;
        int imageSize = Math.Min(fileSize, file.Length) - imageStart;
        int imageParagraphs = (imageSize + 15) / 16;

        // the program takes the largest block there is, up to what its header asks for at most
        int largest = LargestFree();
        int needed = 0x10 + imageParagraphs + minimum;
        if (largest < needed)
        {
            machine.Trace($"{program} needs {needed * 16} bytes; {largest * 16} are free");
            return false;
        }

        int size = Math.Min(largest, 0x10 + imageParagraphs + maximum);
        ushort psp = Allocate(size, 0)!.Value;
        ushort loadSegment = (ushort)(psp + 0x10);
        machine.WriteBytes(loadSegment, 0, file.AsSpan(imageStart, imageSize));

        // each relocation is a far pointer to a word in the image that holds a segment, relative
        // to the image's start: the load segment is added to it
        int table = Word(0x18);
        for (int i = 0; i < relocations; i++)
        {
            ushort offset = Word(table + (4 * i)), segment = Word(table + (4 * i) + 2);
            ushort at = (ushort)(loadSegment + segment);
            machine.WriteWord(at, offset, (ushort)(machine.ReadWord(at, offset) + loadSegment));
        }

        machine.ProgramLoaded(program, loadSegment, imageStart, imageSize);

        SetOwner(environment, psp, "");
        SetOwner(psp, psp, name);
        BuildPsp(psp, (ushort)(psp + size), environment, tail, parent == 0 ? psp : parent);
        _psp = psp;
        _dtaSegment = psp;
        _dtaOffset = 0x80;

        var cpu = machine.Cpu;
        cpu.Ds = cpu.Es = psp;
        cpu.Ss = (ushort)(loadSegment + Word(0x0E));
        cpu.Esp = Word(0x10);
        cpu.Cs = (ushort)(loadSegment + Word(0x16));
        cpu.Eip = Word(0x14);
        cpu.Eax = 0;
        cpu.Ebx = 0;
        cpu.Ecx = 0x00FF;
        cpu.Edx = psp;
        cpu.Esi = cpu.Eip;
        cpu.Edi = cpu.Esp;
        cpu.Ebp = 0x091C;
        cpu.Eflags = 0x0202;
        return true;
    }

    /// <summary>
    /// The program segment prefix: the 256 bytes DOS puts before every program, holding how to
    /// exit, how much memory it has, its environment, its file handles and its command line.
    /// </summary>
    private void BuildPsp(ushort psp, ushort end, ushort environment, string tail, ushort parent)
    {
        machine.WriteBytes(psp, 0, new byte[0x100]);
        machine.WriteBytes(psp, 0x00, [0xCD, 0x20]);                 // INT 20h, the old way to exit
        machine.WriteWord(psp, 0x02, end);                            // the paragraph past its memory
        for (uint i = 0; i < 3; i++)
        {
            // the terminate, Ctrl-Break and critical-error vectors (22h to 24h) as they were
            machine.WriteWord(psp, 0x0A + (4 * i), machine.ReadWord(0, (0x22 + i) * 4));
            machine.WriteWord(psp, 0x0C + (4 * i), machine.ReadWord(0, ((0x22 + i) * 4) + 2));
        }

        machine.WriteWord(psp, 0x16, parent);                         // the parent: itself, as for a shell, or the program that ran it
        machine.WriteBytes(psp, 0x18, [1, 1, 1, 0, 2, .. Enumerable.Repeat((byte)0xFF, 15)]);
        machine.WriteWord(psp, 0x2C, environment);
        machine.WriteWord(psp, 0x32, 20);                             // the handle table's size and place
        machine.WriteWord(psp, 0x34, 0x18);
        machine.WriteWord(psp, 0x36, psp);
        machine.WriteBytes(psp, 0x50, [0xCD, 0x21, 0xCB]);           // INT 21h, RETF
        machine.WriteBytes(psp, 0x5C, [0, .. Enumerable.Repeat((byte)' ', 11)]);
        machine.WriteBytes(psp, 0x6C, [0, .. Enumerable.Repeat((byte)' ', 11)]);

        tail = tail[..Math.Min(tail.Length, 126)];
        machine.WriteByte(psp, 0x80, (byte)tail.Length);
        machine.WriteBytes(psp, 0x81, [.. Encoding.Latin1.GetBytes(tail), 0x0D]);
    }

    /// <summary>
    /// Ends the program, as DOS does. A program another ran goes back to it (see
    /// <see cref="ReturnToParent"/>). Otherwise its files are closed and its memory freed (the
    /// next program starts the arena afresh), and with a batch file running, the machine runs on
    /// to its next line; without one, it has ended.
    /// </summary>
    public void Terminate(int code)
    {
        machine.ExitCode = code;
        if (_parents.Count > 0)
        {
            machine.Trace($"exit {code}");
            ReturnToParent();
            return;
        }

        FlushConsole();
        CloseProgramFiles();
        machine.ProgramEnded();
        machine.Trace($"exit {code}");
        if (_shell?.Running == true)
        {
            _betweenPrograms = true;
        }
        else
        {
            machine.Exited = true;
        }
    }

    // ---- the memory arena --------------------------------------------------------------------

    // Memory is a chain of blocks, each led by a 16-byte memory control block: 'M' (or 'Z' for the
    // last), the owning program's PSP (0 when free), the size in paragraphs, and a name.

    private void InitializeArena() => WriteBlock(ArenaStart, 'Z', 0, ArenaEnd - ArenaStart - 1);

    private void WriteBlock(int mcb, char type, int owner, int size)
    {
        machine.WriteByte((ushort)mcb, 0, (byte)type);
        machine.WriteWord((ushort)mcb, 1, (ushort)owner);
        machine.WriteWord((ushort)mcb, 3, (ushort)size);
    }

    private IEnumerable<(ushort Mcb, char Type, ushort Owner, ushort Size)> Blocks()
    {
        ushort mcb = ArenaStart;
        while (true)
        {
            char type = (char)machine.ReadByte(mcb, 0);
            ushort size = machine.ReadWord(mcb, 3);
            yield return (mcb, type, machine.ReadWord(mcb, 1), size);
            if (type != 'M')
            {
                yield break;
            }

            mcb = (ushort)(mcb + size + 1);
        }
    }

    private int LargestFree() => Blocks().Where(b => b.Owner == 0).Select(b => (int)b.Size).DefaultIfEmpty(0).Max();

    /// <summary>Allocates a block of paragraphs by first fit, returning its segment, or null.</summary>
    private ushort? Allocate(int paragraphs, ushort owner)
    {
        foreach (var block in Blocks())
        {
            if (block.Owner != 0 || block.Size < paragraphs)
            {
                continue;
            }

            if (block.Size > paragraphs)
            {
                // split off the rest as a free block
                WriteBlock(block.Mcb + paragraphs + 1, block.Type, 0, block.Size - paragraphs - 1);
                WriteBlock(block.Mcb, 'M', owner == 0 ? 8 : owner, paragraphs);
            }
            else
            {
                WriteBlock(block.Mcb, block.Type, owner == 0 ? 8 : owner, paragraphs);
            }

            return (ushort)(block.Mcb + 1);
        }

        return null;
    }

    private void SetOwner(ushort segment, ushort owner, string name)
    {
        ushort mcb = (ushort)(segment - 1);
        machine.WriteWord(mcb, 1, owner);
        var bytes = new byte[8];
        Encoding.ASCII.GetBytes(name.ToUpperInvariant()[..Math.Min(name.Length, 8)]).CopyTo(bytes, 0);
        machine.WriteBytes(mcb, 8, bytes);
    }

    private bool IsBlock(ushort segment) =>
        Blocks().Any(b => b.Mcb == segment - 1 && b.Owner != 0);

    private void Free(ushort segment)
    {
        machine.WriteWord((ushort)(segment - 1), 1, 0);
        Coalesce();
    }

    /// <summary>Joins each run of free blocks into one.</summary>
    private void Coalesce()
    {
        var blocks = Blocks().ToList();
        for (int i = 0; i < blocks.Count - 1; i++)
        {
            if (blocks[i].Owner == 0 && blocks[i + 1].Owner == 0)
            {
                var next = blocks[i + 1];
                WriteBlock(blocks[i].Mcb, next.Type, 0, blocks[i].Size + next.Size + 1);
                blocks[i] = (blocks[i].Mcb, next.Type, 0, (ushort)(blocks[i].Size + next.Size + 1));
                blocks.RemoveAt(i + 1);
                i--;
            }
        }
    }

    /// <summary>Grows or shrinks a block in place; when it can't grow enough, returns the most it could have.</summary>
    private int? Resize(ushort segment, int paragraphs)
    {
        ushort mcb = (ushort)(segment - 1);
        var blocks = Blocks().ToList();
        int index = blocks.FindIndex(b => b.Mcb == mcb);
        var block = blocks[index];

        // what it could reach: itself and a free block after it
        int available = block.Size;
        char lastType = block.Type;
        if (index + 1 < blocks.Count && blocks[index + 1].Owner == 0)
        {
            available += blocks[index + 1].Size + 1;
            lastType = blocks[index + 1].Type;
        }

        if (paragraphs > available)
        {
            return available;
        }

        if (paragraphs < available)
        {
            WriteBlock(mcb + paragraphs + 1, lastType, 0, available - paragraphs - 1);
            WriteBlock(mcb, 'M', block.Owner, paragraphs);
            Coalesce();
        }
        else
        {
            WriteBlock(mcb, lastType, block.Owner, paragraphs);
        }

        return null;
    }

    // ---- the clock ---------------------------------------------------------------------------

    /// <summary>
    /// DOS's date. Its time of day is the BIOS's tick count; the date is DOS's own, moved on a
    /// day when the tick count passes midnight. Programs can set both (functions 2Bh and 2Dh),
    /// and the machine's clock goes on from there, the host's left alone.
    /// </summary>
    internal DateTime Date { get; set; }

    // the date, moved on if the tick count has passed midnight since DOS last looked
    private DateTime Today()
    {
        if (machine.PassedMidnight())
        {
            Date = Date.AddDays(1);
        }

        return Date;
    }

    /// <summary>Sets the date, from 1980 to 2099 as DOS allows; whether it was a date.</summary>
    private bool SetDate(int year, int month, int day)
    {
        if (year is < 1980 or > 2099 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            machine.Trace($"21/2B date {year}-{month:D2}-{day:D2} isn't one");
            return false;
        }

        machine.PassedMidnight();
        Date = new DateTime(year, month, day);
        machine.Trace($"21/2B date set to {Date:yyyy-MM-dd}");
        return true;
    }

    /// <summary>Sets the time of day, as the BIOS's tick count; whether it was a time.</summary>
    private bool SetTime(int hour, int minute, int second, int hundredths)
    {
        if (hour > 23 || minute > 59 || second > 59 || hundredths > 99)
        {
            machine.Trace($"21/2D time {hour}:{minute:D2}:{second:D2}.{hundredths:D2} isn't one");
            return false;
        }

        // past midnight, the date moves on before the time goes back
        Today();
        // rounded up to the next tick, so the time reads back as it was set
        double seconds = (hour * 3600) + (minute * 60) + second + (hundredths / 100.0);
        machine.TickCount = Math.Min((uint)Math.Ceiling(seconds * Machine.TicksPerSecond), Machine.TicksPerDay - 1);
        machine.Trace($"21/2D time set to {hour:D2}:{minute:D2}:{second:D2}.{hundredths:D2}");
        return true;
    }

    // ---- for the DPMI host ------------------------------------------------------------------

    /// <summary>The running program's PSP segment.</summary>
    internal ushort Psp => _psp;

    /// <summary>Allocates conventional memory for the program, as function 48h would; null, with the largest block free, when it can't.</summary>
    internal ushort? AllocateBlock(int paragraphs, out int largest)
    {
        ushort? segment = Allocate(paragraphs, _psp);
        largest = segment is null ? LargestFree() : 0;
        return segment;
    }

    /// <summary>Whether a segment begins an allocated block.</summary>
    internal bool IsAllocated(ushort segment) => IsBlock(segment);

    /// <summary>Frees a block, as function 49h would.</summary>
    internal void FreeBlock(ushort segment) => Free(segment);

    /// <summary>Resizes a block, as function 4Ah would: null if it could, or the most it could have been.</summary>
    internal int? ResizeBlock(ushort segment, int paragraphs) => Resize(segment, paragraphs);

    // ---- INT 21h -----------------------------------------------------------------------------

    private void Ok() => machine.SetReturnCarry(false);

    private void Fail(int error)
    {
        _lastError = error;
        _registers.Ax = (ushort)error;
        machine.SetReturnCarry(true);
    }

    public void Call()
    {
        var cpu = _registers;
        int function = cpu.Ah;
        switch (function)
        {
            case 0x00: Terminate(0); break;
            case 0x02: ConsoleOut((char)cpu.Dl); break;
            case 0x06 when cpu.Dl != 0xFF: ConsoleOut((char)cpu.Dl); break;
            case 0x09:
                for (uint i = 0; i < 4096; i++)
                {
                    char c = (char)machine.ReadByte(cpu.Ds, cpu.Dx + i);
                    if (c == '$')
                    {
                        break;
                    }

                    ConsoleOut(c);
                }

                break;
            case 0x0D: break;
            case 0x0E:
                // select a drive (DL, 0 for A:): one that isn't there is left as it was
                if (DriveExists(cpu.Dl))
                {
                    _currentDrive = cpu.Dl;
                }

                cpu.Al = LastDrive;
                break;
            case 0x19: cpu.Al = (byte)_currentDrive; break;
            case 0x1A:
                _dtaSegment = cpu.Ds;
                _dtaOffset = cpu.Dx;
                break;
            case 0x25:
                machine.SetVector(cpu.Al, cpu.Ds, cpu.Dx);
                machine.Trace($"21/25 set vector {cpu.Al:X2}h to {cpu.Ds:X4}:{cpu.Dx:X4}");
                break;
            case 0x2A:
            {
                var date = Today();
                cpu.Cx = (ushort)date.Year;
                cpu.Dh = (byte)date.Month;
                cpu.Dl = (byte)date.Day;
                cpu.Al = (byte)date.DayOfWeek;
                break;
            }

            case 0x2B:
                cpu.Al = SetDate(cpu.Cx, cpu.Dh, cpu.Dl) ? (byte)0 : (byte)0xFF;
                break;
            case 0x2C:
            {
                Today();
                long hundredths = (long)(machine.TickCount * 100 / Machine.TicksPerSecond);
                cpu.Ch = (byte)(hundredths / 360_000);
                cpu.Cl = (byte)(hundredths / 6000 % 60);
                cpu.Dh = (byte)(hundredths / 100 % 60);
                cpu.Dl = (byte)(hundredths % 100);
                break;
            }

            case 0x2D:
                cpu.Al = SetTime(cpu.Ch, cpu.Cl, cpu.Dh, cpu.Dl) ? (byte)0 : (byte)0xFF;
                break;

            case 0x2F:
                cpu.Es = _dtaSegment;
                cpu.Bx = _dtaOffset;
                break;
            case 0x30:
                // MS-DOS 5.0
                cpu.Ax = 0x0005;
                cpu.Bx = 0xFF00;
                cpu.Cx = 0;
                machine.Trace("21/30 DOS version 5.0");
                break;
            case 0x33:
                switch (cpu.Al)
                {
                    case 0x00: cpu.Dl = 0; break;
                    case 0x05: cpu.Dl = 3; break;
                    case 0x06: cpu.Bx = 0x0005; cpu.Dx = 0; break;
                }

                break;
            case 0x35:
                cpu.Bx = machine.ReadWord(0, (uint)cpu.Al * 4);
                cpu.Es = machine.ReadWord(0, ((uint)cpu.Al * 4) + 2);
                machine.Trace($"21/35 get vector {cpu.Al:X2}h: {cpu.Es:X4}:{cpu.Bx:X4}");
                break;
            case 0x36:
                if (!DriveExists(DriveIn(cpu.Dl)))
                {
                    cpu.Ax = 0xFFFF;
                }
                else if (DriveIn(cpu.Dl) == DriveD)
                {
                    // the CD: full, its clusters a sector each
                    cpu.Ax = 1;
                    cpu.Bx = 0;
                    cpu.Cx = IsoImage.SectorSize;
                    cpu.Dx = (ushort)Math.Min(_cd!.Sectors, 0xFFFF);
                }
                else
                {
                    // a drive of 8-sector clusters of 512 bytes, about 128 MB of it free
                    cpu.Ax = 8;
                    cpu.Bx = 0x7FFF;
                    cpu.Cx = 512;
                    cpu.Dx = 0xFFFF;
                }

                break;
            case 0x37:
                if (cpu.Al == 0)
                {
                    cpu.Dl = (byte)'/';
                }

                cpu.Al = 0;
                break;
            case 0x38:
                machine.WriteBytes(cpu.Ds, cpu.Dx, new byte[34]);
                cpu.Bx = 1;
                Ok();
                break;
            case 0x48: AllocateCall(cpu); break;
            case 0x49: FreeCall(cpu); break;
            case 0x4A: ResizeCall(cpu); break;
            case 0x4B: RunProgram(cpu); break;
            case 0x4C: Terminate(cpu.Al); break;
            case 0x4D:
                // the exit code of the program it last ran, which ended normally
                cpu.Ax = (byte)machine.ExitCode;
                break;
            case 0x50: _psp = cpu.Bx; break;
            case 0x51:
            case 0x62:
                cpu.Bx = _psp;
                break;
            case 0x52:
                // the "list of lists", DOS's internal tables, undocumented: Wing Commander III's
                // loader follows it to count the files DOS can have open
                cpu.Es = Machine.BiosSegment;
                cpu.Bx = Machine.ListOfLists;
                machine.Trace("21/52 list of lists");
                break;
            case 0x54: cpu.Al = 0; break;
            case 0x58:
                if (cpu.Al is 0x00 or 0x02)
                {
                    cpu.Ax = 0;
                }

                Ok();
                break;
            case 0x59:
                cpu.Ax = (ushort)_lastError;
                cpu.Bx = 0x0104;
                cpu.Ch = 1;
                break;
            case 0x63 when cpu.Al == 0x00:
                // the double-byte character set's lead bytes, which Watcom's runtime asks for:
                // none, an empty table, as DOS gives for a Western country
                cpu.Ds = Machine.BiosSegment;
                cpu.Si = Machine.EmptyTable;
                cpu.Al = 0;
                Ok();
                break;
            case 0x66 when cpu.Al == 0x01:
                // the code page, active and the system's: 437, the PC's own
                cpu.Bx = 437;
                cpu.Dx = 437;
                Ok();
                break;
            case 0x67:
            case 0x68:
                Ok();
                break;
            case 0xEA:
                // NetWare's shell, asked for its version by programs built on C-Worthy, the
                // library Novell later bought: with no network, DOS answers a function it doesn't
                // have as it answers any, AL 0 and nothing else, and the caller's buffer is left
                // as it was
                machine.Trace($"21/EA NetWare's shell (AL={cpu.Al:X2}h): none");
                cpu.Al = 0;
                break;
            default:
                if (!KeyboardCall(function, cpu) && !FileCall(function, cpu))
                {
                    throw new NotSupportedException($"INT 21h function {function:X2}h (AL={cpu.Al:X2}h) isn't provided.");
                }

                break;
        }
    }

    private void AllocateCall(Registers cpu)
    {
        if (Allocate(cpu.Bx, _psp) is { } segment)
        {
            cpu.Ax = segment;
            Ok();
            machine.Trace($"21/48 allocate {cpu.Bx * 16} bytes: {segment:X4}h");
        }
        else
        {
            int largest = LargestFree();
            machine.Trace($"21/48 allocate {cpu.Bx * 16} bytes: only {largest * 16} free");
            Fail(8);
            cpu.Bx = (ushort)largest;
        }
    }

    private void FreeCall(Registers cpu)
    {
        if (IsBlock(cpu.Es))
        {
            Free(cpu.Es);
            Ok();
            machine.Trace($"21/49 free {cpu.Es:X4}h");
        }
        else
        {
            Fail(9);
        }
    }

    private void ResizeCall(Registers cpu)
    {
        if (!IsBlock(cpu.Es))
        {
            Fail(9);
            return;
        }

        if (Resize(cpu.Es, cpu.Bx) is { } most)
        {
            machine.Trace($"21/4A resize {cpu.Es:X4}h to {cpu.Bx * 16} bytes: at most {most * 16}");
            Fail(8);
            cpu.Bx = (ushort)most;
        }
        else
        {
            machine.Trace($"21/4A resize {cpu.Es:X4}h to {cpu.Bx * 16} bytes");
            Ok();
        }
    }

    /// <summary>A line on the console, from the shell.</summary>
    internal void ConsoleLine(string text)
    {
        foreach (char c in text)
        {
            ConsoleOut(c);
        }

        ConsoleOut('\n');
    }

    private void ConsoleOut(char c)
    {
        // to the screen, and a line at a time to the log
        machine.ConsoleCharacter(c);
        if (c == '\n')
        {
            FlushConsole();
        }
        else if (c != '\r')
        {
            _console.Append(c);
        }
    }

    private void FlushConsole()
    {
        if (_console.Length > 0)
        {
            machine.Trace($"console: {_console}");
            _console.Clear();
        }
    }
}
