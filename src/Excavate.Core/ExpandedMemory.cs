namespace Excavate.Core;

/// <summary>
/// Expanded memory (EMS 4.0, the LIM specification): memory beyond the 8086's 1 MB, reached
/// through a 64 KB window in the upper memory area. A program allocates 16 KB logical pages
/// under a handle, then maps up to four of them into the window's four physical pages, and
/// reads and writes them there.
/// </summary>
/// <remarks>
/// On a 386 an expanded memory manager like EMM386 made this out of extended memory with the
/// processor's paging. Here the window is simply decoded on the bus: an access to E0000h to
/// EFFFFh goes to whichever logical page is mapped into that part of it.
/// </remarks>
internal sealed class ExpandedMemory(Machine machine)
{
    private const int PageSize = 0x4000;
    private const int TotalPages = 256; // 4 MB
    private const ushort FrameSegment = 0xE000;

    // the driver's segment: programs find the manager by the name "EMMXXXX0" at offset 0Ah of the
    // segment the INT 67h vector points into
    private const ushort DriverSegment = 0xF800;

    private readonly byte[]?[] _pages = new byte[TotalPages][];
    private readonly Dictionary<int, List<int>> _handles = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<int, int[]> _saved = [];

    // the logical page, as an index into _pages, mapped into each physical page; -1 for none
    private readonly int[] _mapped = [-1, -1, -1, -1];

    private byte Ah => (byte)(machine.Cpu.Eax >> 8);

    private byte Al => (byte)machine.Cpu.Eax;

    private ushort Bx => (ushort)machine.Cpu.Ebx;

    private ushort Dx => (ushort)machine.Cpu.Edx;

    public void Install()
    {
        uint driver = (uint)DriverSegment << 4;
        machine.WriteRom(driver + 0x0A, "EMMXXXX0"u8);
        machine.WriteRom(driver + 0x12, [0x0F, 0xFF, 0x67, 0xCF]);
        machine.SetVector(0x67, DriverSegment, 0x0012);
    }

    public byte Read(uint offset)
    {
        int page = _mapped[offset / PageSize];
        return page < 0 ? (byte)0xFF : Page(page)[offset % PageSize];
    }

    public void Write(uint offset, byte value)
    {
        int page = _mapped[offset / PageSize];
        if (page >= 0)
        {
            Page(page)[offset % PageSize] = value;
        }
    }

    private byte[] Page(int index) => _pages[index] ??= new byte[PageSize];

    private int FreePages => TotalPages - _handles.Values.Sum(pages => pages.Count);

    private void Status(int code) => machine.Cpu.Eax = (machine.Cpu.Eax & 0xFFFF_00FF) | ((uint)code << 8);

    private void SetBx(int value) => machine.Cpu.Ebx = (machine.Cpu.Ebx & 0xFFFF_0000) | (ushort)value;

    private void SetDx(int value) => machine.Cpu.Edx = (machine.Cpu.Edx & 0xFFFF_0000) | (ushort)value;

    /// <summary>INT 67h. Every function returns its status in AH, 0 for success.</summary>
    public void Call()
    {
        int function = Ah;
        machine.Trace($"67/{function:X2} AL={Al:X2} BX={Bx:X4} DX={Dx:X4}");
        switch (function)
        {
            case 0x40: Status(0); break;
            case 0x41: SetBx(FrameSegment); Status(0); break;
            case 0x42: SetBx(FreePages); SetDx(TotalPages); Status(0); break;
            case 0x43: Allocate(Bx); break;
            case 0x44: Map(Al, Bx, Dx); break;
            case 0x45: Release(Dx); break;
            case 0x46: machine.Cpu.Eax = (machine.Cpu.Eax & 0xFFFF_0000) | 0x0040; break;
            case 0x47: _saved[Dx] = (int[])_mapped.Clone(); Status(0); break;
            case 0x48:
                if (_saved.Remove(Dx, out var map))
                {
                    map.CopyTo(_mapped, 0);
                    Status(0);
                }
                else
                {
                    Status(0x8E);
                }

                break;
            case 0x4B: SetBx(_handles.Count); Status(0); break;
            case 0x4C:
                if (_handles.TryGetValue(Dx, out var pages))
                {
                    SetBx(pages.Count);
                    Status(0);
                }
                else
                {
                    Status(0x83);
                }

                break;
            case 0x51: Reallocate(Dx, Bx); break;
            case 0x53 when Al == 0x01:
                _names[Dx] = machine.ReadString(machine.Cpu.Ds, machine.Cpu.Esi, 8);
                Status(0);
                break;
            case 0x58 when Al == 0x01:
                machine.Cpu.Ecx = (machine.Cpu.Ecx & 0xFFFF_0000) | 4;
                Status(0);
                break;
            case 0x58 when Al == 0x00:
                // the mappable physical pages: segment and page number for each
                for (uint i = 0; i < 4; i++)
                {
                    machine.WriteWord(machine.Cpu.Es, machine.Cpu.Edi + (4 * i), (ushort)(FrameSegment + (i * 0x400)));
                    machine.WriteWord(machine.Cpu.Es, machine.Cpu.Edi + (4 * i) + 2, (ushort)i);
                }

                machine.Cpu.Ecx = (machine.Cpu.Ecx & 0xFFFF_0000) | 4;
                Status(0);
                break;
            case 0xDE:
                // VCPI, the other way into protected mode, which the machine doesn't offer: DOS
                // extenders that ask for it first (PMODE/W does) go on to its DPMI host instead
                Status(0x84);
                break;
            default:
                throw new NotSupportedException($"INT 67h function {function:X2}h (AL={Al:X2}h) isn't provided.");
        }
    }

    private void Allocate(int count)
    {
        if (count > FreePages)
        {
            Status(0x88);
            return;
        }

        // handle 0 is the operating system's; programs get 1 and up
        int handle = 1;
        while (_handles.ContainsKey(handle))
        {
            handle++;
        }

        _handles[handle] = [.. TakeFree(count)];
        SetDx(handle);
        Status(0);
    }

    private IEnumerable<int> TakeFree(int count)
    {
        var used = _handles.Values.SelectMany(pages => pages).ToHashSet();
        return Enumerable.Range(0, TotalPages).Where(page => !used.Contains(page)).Take(count).ToList();
    }

    private void Map(int physical, int logical, int handle)
    {
        if (!_handles.TryGetValue(handle, out var pages))
        {
            Status(0x83);
        }
        else if (physical > 3)
        {
            Status(0x8B);
        }
        else if (logical == 0xFFFF)
        {
            _mapped[physical] = -1;
            Status(0);
        }
        else if (logical >= pages.Count)
        {
            Status(0x8A);
        }
        else
        {
            _mapped[physical] = pages[logical];
            Status(0);
        }
    }

    private void Release(int handle)
    {
        if (!_handles.Remove(handle, out var pages))
        {
            Status(0x83);
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            if (pages.Contains(_mapped[i]))
            {
                _mapped[i] = -1;
            }
        }

        foreach (int page in pages)
        {
            _pages[page] = null;
        }

        _names.Remove(handle);
        Status(0);
    }

    private void Reallocate(int handle, int count)
    {
        if (!_handles.TryGetValue(handle, out var pages))
        {
            Status(0x83);
            return;
        }

        if (count > pages.Count + FreePages)
        {
            Status(0x88);
            return;
        }

        if (count < pages.Count)
        {
            pages.RemoveRange(count, pages.Count - count);
        }
        else
        {
            pages.AddRange(TakeFree(count - pages.Count));
        }

        SetBx(pages.Count);
        Status(0);
    }
}
