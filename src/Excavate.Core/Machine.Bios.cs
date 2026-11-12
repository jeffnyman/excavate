using Excavate.Core.Devices;
using Excavate.Core.Processors;

namespace Excavate.Core;

// The BIOS: the interrupt table, the BIOS data area, and the BIOS services answered in C#.
public sealed partial class Machine
{
    // the stubs, one per vector, at F000:0100 + 4 * vector; an IRET for the rest at F000:0000
    private const ushort StubBase = 0x0100;

    // the vectors answered in C#: the BIOS's, DOS's and the mouse driver's (EMS installs its own)
    private static readonly byte[] Services = [0x10, 0x11, 0x12, 0x13, 0x15, 0x16, 0x1A, 0x20, 0x21, 0x2F, 0x33];

    // the BIOS's timer and keyboard interrupt handlers, past the stubs, and its system
    // configuration table
    private const ushort TimerHandler = 0x0500;
    private const ushort KeyboardHandler = 0x0510;
    private const ushort ConfigurationTable = 0x0540;

    // the mouse driver's handler for IRQ 12, and where it keeps the program's event handler
    private const ushort MouseHandler = 0x05A0;
    private const ushort MouseEventHandler = 0x05E8;

    /// <summary>Four zero bytes in the BIOS area, for DOS's answers that point at an empty table.</summary>
    internal const ushort EmptyTable = 0x05F0;

    /// <summary>
    /// DOS's "list of lists" (INT 21h function 52h), as much of it as programs read: at offset 4,
    /// a pointer to the chain of system file tables, here a single table at <see cref="FileTables"/>.
    /// </summary>
    internal const ushort ListOfLists = 0x0560;

    /// <summary>The one system file table's header: no next table, and room for 40 files, as FILES=40 gives.</summary>
    private const ushort FileTables = 0x0580;

    // the ticks in a day: the timer's 18.2 a second for 24 hours
    private const uint TicksPerDay = 0x1800B0;


    private void InstallBios()
    {
        const uint bios = (uint)BiosSegment << 4;

        // every vector starts at an IRET, so an interrupt nothing handles does nothing
        WriteRom(bios, [0xCF]);
        for (int vector = 0; vector < 256; vector++)
        {
            SetVector(vector, BiosSegment, 0x0000);
        }

        foreach (byte vector in Services)
        {
            ushort offset = (ushort)(StubBase + (4 * vector));
            WriteRom(bios + offset, [0x0F, 0xFF, vector, 0xCF]);
            SetVector(vector, BiosSegment, offset);
        }

        // the hardware interrupts: IRQ 0, the timer, counts the time of day and calls INT 1Ch,
        // the hook programs are meant to use; the rest only end the interrupt, on both
        // controllers for the second's IRQs (8 to 15, vectors 70h to 77h)
        WriteRom(bios + TimerHandler, [0x0F, 0xFF, 0x08, 0xCD, 0x1C, 0xCF]);
        SetVector(0x08, BiosSegment, TimerHandler);
        foreach (int vector in Enumerable.Range(0x0A, 6).Concat(Enumerable.Range(0x70, 8)))
        {
            ushort offset = (ushort)(StubBase + (4 * vector));
            WriteRom(bios + offset, [0x0F, 0xFF, (byte)vector, 0xCF]);
            SetVector(vector, BiosSegment, offset);
        }

        // IRQ 12, the PS/2 mouse, as its driver answers it: the event goes to the program's
        // handler, if it set one for it (INT 33h function 0Ch), with the program's registers kept
        WriteRom(bios + MouseHandler,
        [
            0x1E,                   // push ds
            0x06,                   // push es
            0x66, 0x60,             // pushad
            0x0F, 0xFF, MouseEventService,  // the event in the registers, or carry if no call
            0x72, 0x05,             // jc done
            0x2E, 0xFF, 0x1E, MouseEventHandler & 0xFF, MouseEventHandler >> 8,  // call far cs:[handler]
            0xB0, 0x20,             // done: mov al,20h
            0xE6, 0xA0,             // out 0A0h,al
            0xE6, 0x20,             // out 20h,al
            0x66, 0x61,             // popad
            0x07,                   // pop es
            0x1F,                   // pop ds
            0xCF,                   // iret
        ]);
        SetVector(0x74, BiosSegment, MouseHandler);

        // IRQ 1, the keyboard, as an AT's BIOS has it: the scan code is offered to INT 15h
        // function 4Fh first, the "keyboard intercept", and only if that hands it back (carry
        // set) does the BIOS take it, into its buffer (in C#). Then the interrupt ends.
        WriteRom(bios + KeyboardHandler,
        [
            0x50,               // push ax
            0xE4, 0x60,         // in al,60h
            0xB4, 0x4F,         // mov ah,4Fh
            0xF9,               // stc
            0xCD, 0x15,         // int 15h
            0x73, 0x03,         // jnc done: the intercept kept the key
            0x0F, 0xFF, 0x09,   // the scan code in AL, to the buffer
            0xB0, 0x20,         // done: mov al,20h
            0xE6, 0x20,         // out 20h,al
            0x58,               // pop ax
            0xCF,               // iret
        ]);
        SetVector(0x09, BiosSegment, KeyboardHandler);

        // the system configuration table INT 15h function C0h points at: its length, the model
        // (FCh, an AT) and submodel, the BIOS revision, and the first feature byte: a second
        // interrupt controller, a real-time clock, and INT 09h calling INT 15h function 4Fh
        WriteRom(bios + ConfigurationTable, [0x08, 0x00, 0xFC, 0x00, 0x00, 0x70, 0x00, 0x00, 0x00, 0x00]);

        // DOS's list of lists and its file table, for programs that count the files DOS allows
        WriteRom(bios + ListOfLists + 4, [FileTables & 0xFF, FileTables >> 8, 0x00, 0xF0]);
        WriteRom(bios + FileTables, [0xFF, 0xFF, 0xFF, 0xFF, 40, 0]);

        // the BIOS data area at 0040:0000, the parts programs read
        WriteWord(0x40, 0x10, 0x1021);   // equipment: a floppy drive, 80-column color video, a game port
        WriteWord(0x40, 0x13, 640);      // conventional memory in KB
        WriteWord(0x40, 0x1A, 0x1E);     // keyboard buffer head and tail, empty
        WriteWord(0x40, 0x1C, 0x1E);
        WriteByte(0x40, 0x49, 3);        // video mode
        WriteWord(0x40, 0x4A, 80);       // text columns
        WriteWord(0x40, 0x63, 0x3D4);    // the CRT controller's port
        WriteByte(0x40, 0x84, 24);       // text rows, less one

        // the model byte at F000:FFFE: FCh, an AT-class machine
        WriteRom(0xF_FFFE, [0xFC]);

        // the first half of the character set's 8 by 8 font, where IBM's BIOS has it: games of
        // the CGA's time draw their text from it (Portal does)
        WriteRom(RomFont.Address, RomFont.Glyphs[..(128 * 8)]);
    }

    /// <summary>
    /// IRQ 0's work in the BIOS: the tick count in the data area goes up, wrapping at midnight
    /// with a flag set for INT 1Ah to report. The interrupt is ended here, before the handler's
    /// INT 1Ch rather than after it, which lets a slow hook be interrupted by the next tick.
    /// </summary>
    private void TimerTick()
    {
        uint ticks = ReadWord(0x40, 0x6C) | ((uint)ReadWord(0x40, 0x6E) << 16);
        if (++ticks >= TicksPerDay)
        {
            ticks = 0;
            WriteByte(0x40, 0x70, 1);
        }

        WriteWord(0x40, 0x6C, (ushort)ticks);
        WriteWord(0x40, 0x6E, (ushort)(ticks >> 16));
        _pic.EndOfInterrupt();
    }

    internal void SetVector(int vector, ushort segment, ushort offset)
    {
        WriteWord(0, (uint)vector * 4, offset);
        WriteWord(0, ((uint)vector * 4) + 2, segment);
    }

    private byte Ah => (byte)(Cpu.Eax >> 8);

    private byte Al => (byte)Cpu.Eax;

    private void SetAx(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | (ushort)value;

    private void SetAl(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_FF00) | (byte)value;

    private void SetAh(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_00FF) | ((uint)(byte)value << 8);

    private void SetBx(int value) => Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | (ushort)value;

    private void SetCx(int value) => Cpu.Ecx = (Cpu.Ecx & 0xFFFF_0000) | (ushort)value;

    private void SetDx(int value) => Cpu.Edx = (Cpu.Edx & 0xFFFF_0000) | (ushort)value;

    /// <summary>INT 10h, the video BIOS. Only what's needed until the VGA is emulated.</summary>
    private void VideoService()
    {
        if (_cga is not null && CgaVideoService())
        {
            return;
        }

        switch (Ah)
        {
            case 0x00:
            {
                // bit 7 asks for the memory to be kept
                int mode = Al & 0x7F;
                _vga.SetMode(mode, clear: (Al & 0x80) == 0);
                WriteByte(0x40, 0x49, (byte)mode);
                WriteWord(0x40, 0x4A, (ushort)(mode is 0 or 1 or 0x13 ? 40 : 80));
                Trace($"10/00 set video mode {mode:X2}h");
                break;
            }

            case 0x01: break; // cursor shape
            case 0x02: break; // cursor position
            case 0x03: SetCx(0x0607); SetDx(0); break;
            case 0x05: break; // display page
            case 0x06:
            case 0x07:
                break; // scroll a window
            case 0x08: SetAx(0x0720); break;
            case 0x09:
            case 0x0A:
            case 0x0E:
                Trace($"10/{Ah:X2} text '{(char)Al}'");
                break;
            case 0x0F:
                SetAx((ReadWord(0x40, 0x4A) << 8) | VideoMode);
                Cpu.Ebx &= 0xFFFF_00FF;
                break;
            case 0x10: PaletteService(); break;
            case 0x11:
                if (Al == 0x30)
                {
                    // font information: 16-line characters, 25 rows
                    SetCx(16);
                    SetDx(24);
                }

                break;
            case 0x12:
                if ((byte)Cpu.Ebx == 0x10)
                {
                    // EGA information: color, 256 KB
                    SetBx(0x0003);
                    SetCx(0x0009);
                }

                break;
            case 0x1A:
                if (Al == 0x00)
                {
                    // display combination: a VGA with a color display
                    SetAl(0x1A);
                    SetBx(0x0008);
                }

                break;
            case 0x4F:
                // VESA: there's none, which a caller tells from AL not being 4Fh
                break;
            default:
                throw new NotSupportedException($"INT 10h function {Ah:X2}h isn't provided.");
        }
    }

    /// <summary>
    /// The video BIOS's calls that differ on a CGA: setting a mode (0 to 6), the palette, and
    /// pixels in the graphics modes. The rest are answered as on the VGA. Returns whether the
    /// call was answered here.
    /// </summary>
    private bool CgaVideoService()
    {
        var cga = _cga!;
        switch (Ah)
        {
            case 0x00:
            {
                int mode = Al & 0x7F;
                cga.SetMode(mode, clear: (Al & 0x80) == 0);
                WriteByte(0x40, 0x49, (byte)cga.Mode);
                WriteWord(0x40, 0x4A, (ushort)(cga.Mode is 0 or 1 or 4 or 5 ? 40 : 80));
                WriteWord(0x40, 0x4C, (ushort)(cga.Mode is >= 4 and <= 6 ? 0x4000 : cga.Mode is 0 or 1 ? 0x800 : 0x1000));
                WriteByte(0x40, 0x65, cga.ModeControl);
                WriteByte(0x40, 0x66, cga.ColorSelect);
                Trace($"10/00 set video mode {mode:X2}h (CGA)");
                return true;
            }

            case 0x0B:
            {
                // the palette: BH 0 sets the background (in graphics) or border (in text) color,
                // with its intensity, from BL; BH 1 picks the 320-wide modes' palette from BL's bit 0
                byte bl = (byte)Cpu.Ebx;
                byte select = (byte)(Cpu.Ebx >> 8) == 0
                    ? (byte)((cga.ColorSelect & 0xE0) | (bl & 0x1F))
                    : (byte)((cga.ColorSelect & ~0x20) | ((bl & 1) != 0 ? 0x20 : 0));
                cga.ColorSelect = select;
                WriteByte(0x40, 0x66, select);
                Trace($"10/0B palette {(ushort)Cpu.Ebx:X4}h: color select {select:X2}h");
                return true;
            }

            case 0x0C:
                // write a pixel, color AL, at column CX and row DX; bit 7 of AL XORs it on
                CgaPixel((ushort)Cpu.Ecx, (ushort)Cpu.Edx, Al);
                return true;
            case 0x0D:
                SetAl(CgaPixel((ushort)Cpu.Ecx, (ushort)Cpu.Edx, null));
                return true;
            case 0x10:
                // the EGA's and VGA's palette calls, which a CGA doesn't have
                Trace($"10/10 palette function {Al:X2}h: not on a CGA");
                return true;
            case 0x11:
            case 0x12:
            case 0x1A:
                // the EGA's and VGA's information: a CGA leaves the registers as they were,
                // which is how programs tell there isn't one
                return true;
            case > 0x0F:
                // past the PC BIOS's table of functions, which it returns from untouched
                Trace($"10/{Ah:X2} not a function of the CGA's BIOS");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A pixel of the CGA's graphics modes, by column and row: written when a color is given (and
    /// XORed on with bit 7 set), and its color returned. Outside the graphics modes, nothing.
    /// </summary>
    private byte CgaPixel(int x, int y, byte? color)
    {
        var cga = _cga!;
        if (cga.Mode is < 4 or > 6 || x >= (cga.Mode == 6 ? 640 : 320) || y >= 200)
        {
            return 0;
        }

        // two bits a pixel in the 320-wide modes, one in the 640-wide
        int bits = cga.Mode == 6 ? 1 : 2;
        int perByte = 8 / bits;
        uint address = CgaMemory + (uint)(((y & 1) << 13) + ((y >> 1) * 80) + (x / perByte));
        int shift = (perByte - 1 - (x % perByte)) * bits;
        int mask = ((1 << bits) - 1) << shift;
        byte b = cga.Read(address);
        if (color is { } c)
        {
            int value = ((c & ((1 << bits) - 1)) << shift) & mask;
            b = (c & 0x80) != 0 ? (byte)(b ^ value) : (byte)((b & ~mask) | value);
            cga.Write(address, b);
        }

        return (byte)((b & mask) >> shift);
    }

    /// <summary>INT 10h AH=10h: the palette. Only the DAC's 256 colors matter in the 256-color modes.</summary>
    private void PaletteService()
    {
        int bx = (ushort)Cpu.Ebx, cx = (ushort)Cpu.Ecx, dx = (ushort)Cpu.Edx;
        switch (Al)
        {
            case 0x10:
                _vga.SetColor(bx & 0xFF, dx >> 8, cx >> 8, cx & 0xFF);
                break;
            case 0x12:
                // a block of colors from ES:DX, three bytes each
                for (int i = 0; i < cx; i++)
                {
                    uint at = (uint)(dx + (3 * i));
                    _vga.SetColor((bx + i) & 0xFF, ReadByte(Cpu.Es, at), ReadByte(Cpu.Es, at + 1), ReadByte(Cpu.Es, at + 2));
                }

                break;
            case 0x15:
            {
                var (red, green, blue) = _vga.GetColor(bx & 0xFF);
                SetDx((red << 8) | (dx & 0xFF));
                SetCx((green << 8) | blue);
                break;
            }

            case 0x17:
                for (int i = 0; i < cx; i++)
                {
                    var (red, green, blue) = _vga.GetColor((bx + i) & 0xFF);
                    uint at = (uint)(dx + (3 * i));
                    WriteByte(Cpu.Es, at, red);
                    WriteByte(Cpu.Es, at + 1, green);
                    WriteByte(Cpu.Es, at + 2, blue);
                }

                break;
            default:
                // the EGA's 16-entry palette and the rest, which 256-color modes don't use
                Trace($"10/10 palette function {Al:X2}h ignored");
                break;
        }
    }

    /// <summary>INT 15h, the AT's system services. Most report that they aren't supported.</summary>
    private void SystemService()
    {
        switch (Ah)
        {
            case 0x4F:
                // the keyboard intercept, unhooked: the key goes on to the BIOS as it is
                SetReturnCarry(true);
                break;
            case 0xC0:
                // the system configuration: where the table is. Wing Commander II reads its
                // feature byte to see whether it can watch the keyboard through the intercept
                // above, leaving the BIOS to buffer keys for DOS, or must take INT 09h over.
                Cpu.Es = BiosSegment;
                Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | ConfigurationTable;
                SetAh(0);
                SetReturnCarry(false);
                break;
            case 0x88:
                // extended memory in KB: none offered this way
                SetAx(0);
                SetReturnCarry(false);
                break;
            case 0x86:
                // wait a number of microseconds: returns at once
                SetReturnCarry(false);
                break;
            default:
                Trace($"15/{Ah:X2} not supported");
                SetAh(0x86);
                SetReturnCarry(true);
                break;
        }
    }

    /// <summary>INT 16h, the keyboard: keystrokes from the machine's queue.</summary>
    private void KeyboardService()
    {
        switch (Ah)
        {
            case 0x00:
            case 0x10:
                if (FromBuffer(take: false) is null)
                {
                    AnswerDiskPrompt();
                }

                if (FromBuffer(take: true) is { } key)
                {
                    SetAx(key);
                }
                else
                {
                    RepeatInterrupt();
                }

                break;
            case 0x01:
            case 0x11:
                // IBM's BIOS turns interrupts on as its keyboard service starts, so a program
                // polling for a key with them off still gets the keyboard's interrupt, before
                // the IRET puts its flags back
                Cpu.Eflags |= Intel386.Flag.Interrupt;
                if (FromBuffer(take: false) is null)
                {
                    AnswerDiskPrompt();
                }

                if (FromBuffer(take: false) is { } waiting)
                {
                    SetAx(waiting);
                    SetReturnZero(false);
                }
                else
                {
                    SetReturnZero(true);
                }

                break;
            case 0x02:
            case 0x12:
                SetAl(ReadByte(0x40, 0x17));
                break;
            case 0x03:
                // the keys' repeat rate and delay: the host's keyboard repeats keys its own way
                Trace($"16/03 typematic rate {Cpu.Ebx & 0xFFFF:X4}h");
                break;
            default:
                throw new NotSupportedException($"INT 16h function {Ah:X2}h isn't provided.");
        }
    }

    /// <summary>INT 1Ah, the clock: the BIOS tick count, and the real-time clock from the host's.</summary>
    private void ClockService()
    {
        static int Bcd(int value) => ((value / 10) << 4) | (value % 10);
        var now = DateTime.Now;
        switch (Ah)
        {
            case 0x00:
                SetCx(ReadWord(0x40, 0x6E));
                SetDx(ReadWord(0x40, 0x6C));
                SetAl(ReadByte(0x40, 0x70));
                WriteByte(0x40, 0x70, 0);
                break;
            case 0x02:
                SetCx((Bcd(now.Hour) << 8) | Bcd(now.Minute));
                SetDx(Bcd(now.Second) << 8);
                SetReturnCarry(false);
                break;
            case 0x04:
                SetCx((Bcd(now.Year / 100) << 8) | Bcd(now.Year % 100));
                SetDx((Bcd(now.Month) << 8) | Bcd(now.Day));
                SetReturnCarry(false);
                break;
            default:
                throw new NotSupportedException($"INT 1Ah function {Ah:X2}h isn't provided.");
        }
    }

    /// <summary>
    /// INT 2Fh, the multiplex interrupt, through which programs ask whether drivers and
    /// environments are present. Nothing is, yet: the answers leave AL at 0.
    /// </summary>
    private void MultiplexService()
    {
        if ((ushort)Cpu.Eax == 0x1687)
        {
            // a DPMI host: the machine's own
            _dpmi.Detect();
            return;
        }

        if (Ah == 0x15 && _dos.CdCall())
        {
            // MSCDEX, for the CD in D:
            return;
        }

        Trace($"2F/{(ushort)Cpu.Eax:X4} nothing installed");
        if (Al == 0x00)
        {
            SetAl(0);
        }
    }

}
