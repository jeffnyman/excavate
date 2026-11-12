namespace Excavate.Core.Devices;

/// <summary>
/// The VGA: 256 KB of video memory in four planes, the registers that decide how the processor
/// reaches that memory and how it's shown, and the DAC, the palette of 256 colors each pixel
/// indexes. Drawn are the text modes; the CGA's graphics modes (4 to 6), kept as the CGA kept
/// them; the EGA's, with 16 colors in four planes (0Dh, 0Eh and 10h, and the VGA's own 12h);
/// and mode 13h with 256 colors, and the unchained variants of it known as Mode X.
/// </summary>
/// <remarks>
/// <para>
/// Video memory is four 64 KB planes side by side, stored here interleaved, plane by plane
/// within each address, as DOSBox stores it. How a processor address reaches it depends on the
/// sequencer's memory mode. In mode 13h, "chain 4" is on: the low two bits of the address pick
/// the plane, so the processor sees one linear 64,000-byte screen, a byte per pixel. With chain 4
/// off (Mode X), an address names the same byte in all four planes, the map mask register picks
/// which planes a write reaches, and the screen shows four neighboring pixels from the four
/// planes at each address. That gives a program four times the memory to draw in, room for pages
/// to flip between.
/// </para>
/// <para>
/// The graphics controller's write modes, logical operations and bit mask apply to unchained
/// writes, and its latches (a byte from each plane, loaded by every read) let a program copy
/// four pixels with one read and one write.
/// </para>
/// </remarks>
internal sealed class Vga
{
    /// <summary>The refresh rate of the 400-line modes, mode 13h's included.</summary>
    public const double FrameRate = 70.086;

    private const int TotalLines = 449;
    private const int VisibleLines = 400;

    private readonly byte[] _memory = new byte[0x4_0000];
    private readonly byte[] _text = new byte[0x8000];
    private readonly byte[] _dac = new byte[768];

    private readonly byte[] _sequencer = new byte[5];
    private readonly byte[] _graphics = new byte[9];
    private readonly byte[] _crtc = new byte[25];
    private readonly byte[] _attribute = new byte[21];
    private int _sequencerIndex;
    private int _graphicsIndex;
    private int _crtcIndex;
    private int _attributeIndex;
    private bool _attributeData;
    private byte _miscellaneous;

    // the DAC's ports: an index to write at and to read from, each with the color component next
    private int _dacWrite;
    private int _dacRead;
    private int _dacComponent;
    private byte _pelMask = 0xFF;

    private uint _latches;

    // the fonts the BIOS loads for each height of character
    private static readonly byte[] Font8 = RomFont.Glyphs.ToArray();
    private static readonly byte[] Font14 = RomFont.Glyphs14.ToArray();
    private static readonly byte[] Font16 = RomFont.Glyphs16.ToArray();

    public Vga() => SetMode(3, clear: true);

    /// <summary>The time in seconds, set by the machine before each access, from which the retrace is timed.</summary>
    public double Time { get; set; }

    public int Mode { get; private set; }

    private bool Chained => (_sequencer[4] & 0x08) != 0;

    /// <summary>How a mode keeps its picture in memory, and so how it's drawn.</summary>
    private enum Kind
    {
        Text,
        Cga4,     // 320 by 200 in 4 colors, at B8000h as the CGA kept it, two bits a pixel
        Cga2,     // 640 by 200 in 2 colors, the same way, a bit a pixel
        Planar,   // 16 colors, a bit of each pixel in each of the four planes
        Linear,   // 256 colors, mode 13h and Mode X
    }

    private static Kind KindOf(int mode) => mode switch
    {
        4 or 5 => Kind.Cga4,
        6 => Kind.Cga2,
        0x0D or 0x0E or 0x10 or 0x12 => Kind.Planar,
        0x13 => Kind.Linear,
        _ => Kind.Text,
    };

    // ---- memory ------------------------------------------------------------------------------

    /// <summary>A read from the processor's window at A0000h (offsets below 10000h) or the text buffer at B8000h.</summary>
    public byte Read(uint address)
    {
        if (address >= 0xB_8000)
        {
            return _text[address & 0x7FFF];
        }

        uint offset = address & 0xFFFF;
        if (Chained)
        {
            return _memory[offset];
        }

        // every read loads the latches with a byte from each plane
        int at = (int)offset * 4;
        _latches = (uint)(_memory[at] | (_memory[at + 1] << 8) | (_memory[at + 2] << 16) | (_memory[at + 3] << 24));
        if ((_graphics[5] & 0x08) != 0)
        {
            // read mode 1: a bit set for each pixel whose color matches the compare register
            int result = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int color = 0;
                for (int plane = 0; plane < 4; plane++)
                {
                    color |= ((_memory[at + plane] >> bit) & 1) << plane;
                }

                int care = _graphics[7] & 0x0F;
                if ((color & care) == (_graphics[2] & care))
                {
                    result |= 1 << bit;
                }
            }

            return (byte)result;
        }

        return _memory[at + (_graphics[4] & 3)];
    }

    public void Write(uint address, byte value)
    {
        if (address >= 0xB_8000)
        {
            _text[address & 0x7FFF] = value;
            return;
        }

        uint offset = address & 0xFFFF;
        if (Chained)
        {
            _memory[offset] = value;
            return;
        }

        int at = (int)offset * 4;
        int mapMask = _sequencer[2] & 0x0F;
        int writeMode = _graphics[5] & 3;
        if (writeMode == 1)
        {
            // write mode 1: the latches, as the last read loaded them
            for (int plane = 0; plane < 4; plane++)
            {
                if ((mapMask & (1 << plane)) != 0)
                {
                    _memory[at + plane] = (byte)(_latches >> (8 * plane));
                }
            }

            return;
        }

        int rotate = _graphics[3] & 7;
        int rotated = ((value >> rotate) | (value << (8 - rotate))) & 0xFF;
        int bitMask = _graphics[8];
        if (writeMode == 3)
        {
            bitMask &= rotated;
        }

        for (int plane = 0; plane < 4; plane++)
        {
            if ((mapMask & (1 << plane)) == 0)
            {
                continue;
            }

            int data = writeMode switch
            {
                2 => (value & (1 << plane)) != 0 ? 0xFF : 0x00,
                3 => (_graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00,
                _ => (_graphics[1] & (1 << plane)) != 0
                    ? ((_graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00)
                    : rotated,
            };

            int latch = (byte)(_latches >> (8 * plane));
            data = ((_graphics[3] >> 3) & 3) switch
            {
                1 => data & latch,
                2 => data | latch,
                3 => data ^ latch,
                _ => data,
            };

            _memory[at + plane] = (byte)((data & bitMask) | (latch & ~bitMask));
        }
    }

    // ---- ports -------------------------------------------------------------------------------

    public byte In(int port)
    {
        switch (port)
        {
            case 0x3C1: return _attribute[_attributeIndex & 0x1F];
            case 0x3C4: return (byte)_sequencerIndex;
            case 0x3C5: return _sequencerIndex < _sequencer.Length ? _sequencer[_sequencerIndex] : (byte)0;
            case 0x3C6: return _pelMask;
            case 0x3C7: return 0x03;
            case 0x3C8: return (byte)_dacWrite;
            case 0x3C9:
            {
                byte value = _dac[(_dacRead * 3) + _dacComponent];
                if (++_dacComponent == 3)
                {
                    _dacComponent = 0;
                    _dacRead = (_dacRead + 1) & 0xFF;
                }

                return value;
            }

            case 0x3CC: return _miscellaneous;
            case 0x3CE: return (byte)_graphicsIndex;
            case 0x3CF: return _graphicsIndex < _graphics.Length ? _graphics[_graphicsIndex] : (byte)0;
            case 0x3D4: return (byte)_crtcIndex;
            case 0x3D5: return _crtcIndex < _crtc.Length ? _crtc[_crtcIndex] : (byte)0;
            case 0x3DA:
                // reading the status also resets the attribute controller to expect an index
                _attributeData = false;
                return Status();
            default: return 0xFF;
        }
    }

    public void Out(int port, byte value)
    {
        switch (port)
        {
            case 0x3C0:
                if (_attributeData)
                {
                    if ((_attributeIndex & 0x1F) < _attribute.Length)
                    {
                        _attribute[_attributeIndex & 0x1F] = value;
                    }
                }
                else
                {
                    _attributeIndex = value;
                }

                _attributeData = !_attributeData;
                break;
            case 0x3C2: _miscellaneous = value; break;
            case 0x3C4: _sequencerIndex = value & 7; break;
            case 0x3C5:
                if (_sequencerIndex < _sequencer.Length)
                {
                    _sequencer[_sequencerIndex] = value;
                }

                break;
            case 0x3C6: _pelMask = value; break;
            case 0x3C7:
                _dacRead = value;
                _dacComponent = 0;
                break;
            case 0x3C8:
                _dacWrite = value;
                _dacComponent = 0;
                break;
            case 0x3C9:
                // a color is three writes, red, green and blue, each of 6 bits
                _dac[(_dacWrite * 3) + _dacComponent] = (byte)(value & 0x3F);
                if (++_dacComponent == 3)
                {
                    _dacComponent = 0;
                    _dacWrite = (_dacWrite + 1) & 0xFF;
                }

                break;
            case 0x3CE: _graphicsIndex = value & 0x0F; break;
            case 0x3CF:
                if (_graphicsIndex < _graphics.Length)
                {
                    _graphics[_graphicsIndex] = value;
                }

                break;
            case 0x3D4: _crtcIndex = value & 0x1F; break;
            case 0x3D5:
                if (_crtcIndex < _crtc.Length)
                {
                    _crtc[_crtcIndex] = value;
                }

                break;
        }
    }

    /// <summary>
    /// Input status 1, port 3DAh. Bit 3 is set during the vertical retrace, when the beam returns to
    /// the top of the screen, about once every 14 ms; programs wait for it to change the picture
    /// without tearing. Bit 0 is set whenever the beam isn't drawing, in either retrace.
    /// </summary>
    private byte Status()
    {
        double frame = Time * FrameRate;
        double line = (frame - Math.Floor(frame)) * TotalLines;
        bool vertical = line >= 412 && line < 414;
        bool blank = line >= VisibleLines || (line - Math.Floor(line)) > 0.8;
        return (byte)((vertical ? 0x08 : 0) | (blank ? 0x01 : 0));
    }

    // ---- the palette, for the BIOS ------------------------------------------------------------

    public void SetColor(int index, int red, int green, int blue)
    {
        _dac[index * 3] = (byte)(red & 0x3F);
        _dac[(index * 3) + 1] = (byte)(green & 0x3F);
        _dac[(index * 3) + 2] = (byte)(blue & 0x3F);
    }

    public (byte Red, byte Green, byte Blue) GetColor(int index) =>
        (_dac[index * 3], _dac[(index * 3) + 1], _dac[(index * 3) + 2]);

    // ---- modes -------------------------------------------------------------------------------

    // the 16-color modes, as IBM's VGA BIOS sets them: the sequencer (chain 4 off), the
    // miscellaneous output register, and the CRT controller's timing, which gives the size
    private static readonly Dictionary<int, (byte[] Sequencer, byte Miscellaneous, byte[] Crtc)> PlanarModes = new()
    {
        [0x0D] = ([0x03, 0x09, 0x0F, 0x00, 0x06], 0x63,
        [
            0x2D, 0x27, 0x28, 0x90, 0x2B, 0x80, 0xBF, 0x1F, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x9C, 0x8E, 0x8F, 0x14, 0x00, 0x96, 0xB9, 0xE3, 0xFF,
        ]),
        [0x0E] = ([0x03, 0x01, 0x0F, 0x00, 0x06], 0x63,
        [
            0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0xBF, 0x1F, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x9C, 0x8E, 0x8F, 0x28, 0x00, 0x96, 0xB9, 0xE3, 0xFF,
        ]),
        [0x10] = ([0x03, 0x01, 0x0F, 0x00, 0x06], 0xA3,
        [
            0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0xBF, 0x1F, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x83, 0x85, 0x5D, 0x28, 0x0F, 0x63, 0xBA, 0xE3, 0xFF,
        ]),
        [0x12] = ([0x03, 0x01, 0x0F, 0x00, 0x06], 0xE3,
        [
            0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0x0B, 0x3E, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0xEA, 0x8C, 0xDF, 0x28, 0x00, 0xE7, 0x04, 0xE3, 0xFF,
        ]),
    };

    /// <summary>
    /// Sets a mode as the video BIOS does: its register values, the default palette, and,
    /// unless asked not to, cleared memory. The text modes are 25 rows, 40 across for modes 0
    /// and 1 and 80 for the rest, in characters of 16 lines.
    /// </summary>
    public void SetMode(int mode, bool clear)
    {
        Mode = mode;
        Array.Clear(_sequencer);
        Array.Clear(_graphics);
        Array.Clear(_crtc);
        Array.Clear(_attribute);

        if (PlanarModes.TryGetValue(mode, out var planar))
        {
            planar.Sequencer.CopyTo(_sequencer, 0);
            planar.Crtc.CopyTo(_crtc, 0);
            byte[] graphics = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x0F, 0xFF];
            graphics.CopyTo(_graphics, 0);
            _miscellaneous = planar.Miscellaneous;
        }
        else if (mode == 0x13)
        {
            // the values of IBM's VGA BIOS for mode 13h
            byte[] sequencer = [0x03, 0x01, 0x0F, 0x00, 0x0E];
            byte[] crtc =
            [
                0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0xBF, 0x1F, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x9C, 0x8E, 0x8F, 0x28, 0x40, 0x96, 0xB9, 0xA3, 0xFF,
            ];
            byte[] graphics = [0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x05, 0x0F, 0xFF];
            sequencer.CopyTo(_sequencer, 0);
            crtc.CopyTo(_crtc, 0);
            graphics.CopyTo(_graphics, 0);
            _miscellaneous = 0x63;
        }
        else
        {
            // text, and the CGA's graphics modes, which the processor reaches at B8000h
            byte[] sequencer = [0x03, 0x00, 0x03, 0x00, 0x02];
            sequencer.CopyTo(_sequencer, 0);
            bool narrow = mode is 0 or 1 or 4 or 5;
            _crtc[0x01] = (byte)(narrow ? 0x27 : 0x4F);
            _crtc[0x09] = (byte)(KindOf(mode) == Kind.Text ? 0x0F : 0xC1);
            _crtc[0x0A] = 0x0D;  // the cursor: the character's last two lines
            _crtc[0x0B] = 0x0E;
            _crtc[0x12] = 0x8F;
            _crtc[0x13] = (byte)(narrow ? 0x14 : 0x28);
            _graphics[8] = 0xFF;
            _miscellaneous = 0x67;
        }

        _attribute[0x10] = (byte)(mode == 0x13 ? 0x41 : KindOf(mode) == Kind.Text ? 0x0C : 0x01);
        _attribute[0x12] = 0x0F;
        _pelMask = 0xFF;
        DefaultPalette(mode);

        if (clear)
        {
            // text is cleared to spaces, light gray on black; the graphics modes to color 0
            Array.Clear(_memory);
            bool text = KindOf(mode) == Kind.Text;
            for (int i = 0; i < _text.Length; i += 2)
            {
                _text[i] = (byte)(text ? ' ' : 0);
                _text[i + 1] = (byte)(text ? 0x07 : 0);
            }
        }
    }

    /// <summary>
    /// The default palette, as the VGA BIOS loads it for a mode. Mode 13h has the 16 colors of
    /// the CGA and EGA, then a ramp of 16 grays; the rest of its default (hue ramps) is left
    /// black, as games set their own before drawing. In the other modes the attribute
    /// controller's 16 registers pick from 64 colors in the DAC: for text and the 350- and
    /// 480-line modes, the EGA's 64 (two bits each of red, green and blue); for the 200-line
    /// modes, the CGA's 16, repeated, which those modes were made for.
    /// </summary>
    private void DefaultPalette(int mode)
    {
        Array.Clear(_dac);
        if (mode == 0x13)
        {
            for (int i = 0; i < 16; i++)
            {
                _attribute[i] = (byte)i;
                var (red, green, blue) = CgaColor(i);
                SetColor(i, red, green, blue);
            }

            int[] grays = [0, 5, 8, 11, 14, 17, 20, 24, 28, 32, 36, 40, 45, 50, 56, 63];
            for (int i = 0; i < 16; i++)
            {
                SetColor(16 + i, grays[i], grays[i], grays[i]);
            }

            return;
        }

        bool cga = mode is 4 or 5 or 6 or 0x0D or 0x0E;
        for (int i = 0; i < 64; i++)
        {
            var (red, green, blue) = cga ? CgaColor(i) : EgaColor(i);
            SetColor(i, red, green, blue);
        }

        // the attribute registers, picking the CGA's 16 colors among the 64: in the CGA's
        // numbering, bright ones have bit 4 set (bit 3 among 16)
        byte[] registers = mode switch
        {
            4 or 5 => [0x00, 0x13, 0x15, 0x17, 0x02, 0x04, 0x06, 0x07, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17],
            6 => [0x00, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17],
            0x0D or 0x0E => [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17],
            _ => [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F],
        };
        registers.CopyTo(_attribute, 0);
    }

    // a color of the EGA's 64, as its bits give it: red, green and blue at two thirds of full
    // (bits 2, 1 and 0), and each again at a third (bits 5, 4 and 3)
    private static (int Red, int Green, int Blue) EgaColor(int i) =>
        ((((i >> 2) & 1) * 0x2A) + (((i >> 5) & 1) * 0x15),
         (((i >> 1) & 1) * 0x2A) + (((i >> 4) & 1) * 0x15),
         ((i & 1) * 0x2A) + (((i >> 3) & 1) * 0x15));

    // a color of the CGA's 16, as the VGA numbers them: red, green and blue at two thirds of
    // full, and a third more of all three with bit 4 or 3. Dark yellow is brown, as IBM's
    // monitor made it
    private static (int Red, int Green, int Blue) CgaColor(int i)
    {
        int low = (i & 0x18) != 0 ? 0x15 : 0;
        int red = ((i & 4) != 0 ? 0x2A : 0) + low;
        int green = ((i & 2) != 0 ? 0x2A : 0) + low;
        int blue = ((i & 1) != 0 ? 0x2A : 0) + low;
        if ((i & 7) == 6 && low == 0)
        {
            green = 0x15;
        }

        return (red, green, blue);
    }

    // ---- the attribute controller and pixels, for the BIOS -----------------------------------

    /// <summary>
    /// A register of the attribute controller: the 16 colors' palette (0 to 0Fh), the mode
    /// control (10h), the border (11h), the color plane enable (12h) and the rest.
    /// </summary>
    public byte GetAttribute(int index) => _attribute[index % _attribute.Length];

    public void SetAttribute(int index, byte value) => _attribute[index % _attribute.Length] = value;

    /// <summary>
    /// The CGA's color select register, as the BIOS keeps it on a VGA (INT 10h function 0Bh): in
    /// the 320-wide modes, the background (bits 0 to 3) and which three colors the others are:
    /// green, red and brown, or (bit 5) cyan, magenta and white, bright with bit 4. In the
    /// 640-wide mode, the dots' color. In every mode, the border's.
    /// </summary>
    public void SelectCgaColors(byte select)
    {
        int color = (select & 7) | ((select & 8) << 1);
        if (Mode is 4 or 5)
        {
            _attribute[0] = (byte)color;
            for (int i = 1; i < 4; i++)
            {
                _attribute[i] = (byte)(((select & 0x20) != 0 ? (2 * i) + 1 : 2 * i) | (select & 0x10));
            }
        }
        else if (Mode == 6)
        {
            _attribute[1] = (byte)color;
        }

        _attribute[0x11] = (byte)color;
    }

    /// <summary>
    /// A pixel of the 16-color modes, by column and row, as the BIOS reaches it (INT 10h
    /// functions 0Ch and 0Dh): when a color is given, written straight to the planes, or with
    /// bit 7 set XORed on. Its color is returned.
    /// </summary>
    public byte PlanarPixel(int x, int y, byte? color)
    {
        var (width, height) = Size;
        if (KindOf(Mode) != Kind.Planar || x >= width || y >= height)
        {
            return 0;
        }

        int at = (((y * _crtc[0x13] * 2) + (x >> 3)) & 0xFFFF) * 4;
        int bit = 0x80 >> (x & 7);
        if (color is { } c)
        {
            for (int plane = 0; plane < 4; plane++)
            {
                bool on = (c & (1 << plane)) != 0;
                if ((c & 0x80) != 0)
                {
                    _memory[at + plane] ^= (byte)(on ? bit : 0);
                }
                else
                {
                    _memory[at + plane] = (byte)(on ? _memory[at + plane] | bit : _memory[at + plane] & ~bit);
                }
            }
        }

        int value = 0;
        for (int plane = 0; plane < 4; plane++)
        {
            value |= (_memory[at + plane] & bit) != 0 ? 1 << plane : 0;
        }

        return (byte)value;
    }

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>
    /// The size of the picture, in pixels. In the 16- and 256-color modes, as the CRT
    /// controller's registers give it: the horizontal display end, in characters of 8 pixels
    /// (4 with 256 colors), and the vertical display end divided by how many times each line is
    /// scanned. In text, 640 by 400; in the CGA's modes, 320 or 640 by 200.
    /// </summary>
    public (int Width, int Height) Size
    {
        get
        {
            switch (KindOf(Mode))
            {
                case Kind.Text: return (640, 400);
                case Kind.Cga4: return (320, 200);
                case Kind.Cga2: return (640, 200);
            }

            int width = (_crtc[0x01] + 1) * (KindOf(Mode) == Kind.Linear ? 4 : 8);
            int end = _crtc[0x12] | ((_crtc[0x07] & 0x02) << 7) | ((_crtc[0x07] & 0x40) << 3);
            int scans = (_crtc[0x09] & 0x1F) + 1;
            if ((_crtc[0x09] & 0x80) != 0)
            {
                scans *= 2;
            }

            return (width, Math.Max(1, (end + 1) / scans));
        }
    }

    /// <summary>Draws the picture as 32-bit ARGB pixels, as it is at <see cref="Time"/>.</summary>
    public void Render(Span<uint> pixels)
    {
        var (width, height) = Size;
        Span<uint> palette = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
        {
            static uint Eight(byte six) => (uint)((six << 2) | (six >> 4));
            int c = (i & _pelMask) * 3;
            palette[i] = 0xFF00_0000 | (Eight(_dac[c]) << 16) | (Eight(_dac[c + 1]) << 8) | Eight(_dac[c + 2]);
        }

        switch (KindOf(Mode))
        {
            case Kind.Text:
                RenderText(pixels[..(width * height)], palette);
                return;
            case Kind.Cga4 or Kind.Cga2:
                RenderCga(pixels, width, height, palette);
                return;
            case Kind.Planar:
                RenderPlanar(pixels, width, height, palette);
                return;
        }

        int start = (_crtc[0x0C] << 8) | _crtc[0x0D];
        int offset = _crtc[0x13] * 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = Chained
                    ? ((start * 4) + (y * offset * 4) + x) & 0x3_FFFF
                    : ((((start + (y * offset) + (x >> 2)) & 0xFFFF) * 4) + (x & 3));
                pixels[(y * width) + x] = palette[_memory[index]];
            }
        }
    }

    // the 16 colors the attribute controller makes of the DAC's 256: its palette registers give
    // the low six bits of each, and its color select register (14h) the top two
    private uint[] Colors16(ReadOnlySpan<uint> palette)
    {
        var colors = new uint[16];
        int high = (_attribute[0x14] & 0x0C) << 4;
        for (int i = 0; i < 16; i++)
        {
            colors[i] = palette[(_attribute[i] & 0x3F) | high];
        }

        return colors;
    }

    /// <summary>
    /// Draws a 16-color mode: each pixel's color a bit from each of the four planes, left to
    /// right from a byte's top bit, with the color plane enable register masking planes off.
    /// Rows start the CRT controller's offset apart, in bytes, from its start address.
    /// </summary>
    private void RenderPlanar(Span<uint> pixels, int width, int height, ReadOnlySpan<uint> palette)
    {
        var colors = Colors16(palette);
        int enable = _attribute[0x12] & 0x0F;
        int start = (_crtc[0x0C] << 8) | _crtc[0x0D];
        int stride = _crtc[0x13] * 2;
        for (int y = 0; y < height; y++)
        {
            for (int column = 0; column < width / 8; column++)
            {
                int at = ((start + (y * stride) + column) & 0xFFFF) * 4;
                int p0 = _memory[at], p1 = _memory[at + 1], p2 = _memory[at + 2], p3 = _memory[at + 3];
                var target = pixels.Slice((y * width) + (column * 8), 8);
                for (int dot = 0; dot < 8; dot++)
                {
                    int bit = 7 - dot;
                    int color = ((p0 >> bit) & 1) | (((p1 >> bit) & 1) << 1) | (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3);
                    target[dot] = colors[color & enable];
                }
            }
        }
    }

    /// <summary>
    /// Draws the CGA's graphics modes as the CGA kept them: the even lines in the first 8 KB at
    /// B8000h and the odd in the next, 80 bytes a line, with two bits a pixel at 320 across and
    /// one at 640. The bits pick among the attribute controller's first registers.
    /// </summary>
    private void RenderCga(Span<uint> pixels, int width, int height, ReadOnlySpan<uint> palette)
    {
        var colors = Colors16(palette);
        int bits = width == 320 ? 2 : 1;
        int perByte = 8 / bits;
        for (int y = 0; y < height; y++)
        {
            int line = ((y & 1) << 13) + ((y >> 1) * 80);
            for (int x = 0; x < width; x++)
            {
                int b = _text[line + (x / perByte)];
                int shift = (perByte - 1 - (x % perByte)) * bits;
                pixels[(y * width) + x] = colors[(b >> shift) & ((1 << bits) - 1)];
            }
        }
    }

    /// <summary>
    /// Draws a text mode as the registers have it: the characters across and the lines in each
    /// (with the font of that height), where the screen starts and how far apart its rows are,
    /// the 16 colors through the attribute controller's palette, and the cursor. Characters
    /// blink, and the cursor at twice the rate, when the attribute controller says so.
    /// </summary>
    private void RenderText(Span<uint> picture, ReadOnlySpan<uint> palette)
    {
        picture.Fill(0xFF00_0000);
        int widen = Mode is 0 or 1 ? 2 : 1;
        int height = (_crtc[0x09] & 0x1F) + 1;
        var font = height <= 8 ? Font8 : height <= 14 ? Font14 : Font16;
        int columns = Math.Clamp(_crtc[0x01] + 1, 1, 80 / widen);
        int rows = VisibleLines / height;
        var colors = Colors16(palette);
        int start = ((_crtc[0x0C] << 8) | _crtc[0x0D]) * 2;
        double frame = Time * FrameRate;
        bool cursorShown = ((long)(frame / 8) & 1) == 0 && (_crtc[0x0A] & 0x20) == 0;
        int cursor = cursorShown ? (((_crtc[0x0E] << 8) | _crtc[0x0F]) * 2) & 0x7FFF : -1;
        TextScreen.Draw(
            _text,
            new TextScreen.Layout(start, columns, rows, _crtc[0x13] * 4, font, Math.Min(height, font.Length / 256)),
            new TextScreen.Look(colors, (_attribute[0x10] & 0x08) != 0, ((long)(frame / 16) & 1) == 0, cursor, _crtc[0x0A] & 0x1F, _crtc[0x0B] & 0x1F),
            picture,
            640,
            widen,
            1);
    }
}
