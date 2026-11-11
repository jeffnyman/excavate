namespace Excavate.Core.Devices;

/// <summary>
/// IBM's Color Graphics Adapter: 16 KB of video memory at B8000h, a Motorola 6845 CRT
/// controller, the mode control and color select registers, and the picture it makes, either
/// as the 16 RGBI colors of an IBM color monitor or as the NTSC composite signal a television
/// or composite monitor decoded into many more.
/// </summary>
/// <remarks>
/// <para>
/// The graphics modes keep their scan lines in two banks: the even lines from B8000h, the odd
/// from BA000h, 80 bytes a line. In the 320 by 200 mode each byte holds four pixels of two bits,
/// the leftmost in the top bits, which pick from a palette of four: the background, set by
/// the color select register, and three fixed colors chosen by its palette bit, green, red and
/// brown or cyan, magenta and white, brightened by its intensity bit. The 640 by 200 mode has
/// eight pixels of a bit each, black or the color select register's color.
/// </para>
/// <para>
/// On a composite monitor, the color comes from where the pixels fall against the 3.58 MHz
/// color carrier: four of the 640 dots across a line make one cycle of it, so a pattern of dots
/// shows as a color of its own rather than as the dots. Games of the time drew for it, dithering
/// patterns that blend into colors the 16 RGBI ones don't have. That's emulated with reenigne's
/// model of the CGA's output, as DOSBox Staging has it (the "old era" CGA of 1981 to 1983),
/// ported from its <c>vga_other.cpp</c> and <c>vga_draw.cpp</c>.
/// </para>
/// <para>
/// The picture is always 640 by 400: each line is drawn twice, and in the 320-wide modes each
/// pixel twice across, so every mode has the same shape on screen as it had on the monitor.
/// Text modes are drawn black for now.
/// </para>
/// </remarks>
internal sealed class Cga
{
    /// <summary>The CGA's frame rate: 262 lines of 912 dots at 14.318 MHz.</summary>
    public const double FrameRate = 14_318_180.0 / (912 * 262);

    public const int Width = 640;
    public const int Height = 400;

    private const int TotalLines = 262;
    private const int VisibleLines = 200;

    // the vertical retrace starts after row 28 of eight lines and lasts the 6845's fixed 16 lines
    private const int RetraceStart = 224;
    private const int RetraceLines = 16;

    /// <summary>The 16 colors of an IBM color monitor, as RGBI makes them: dark yellow is brown.</summary>
    private static readonly uint[] Rgbi =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    // the BIOS's mode control values for modes 0 to 7, and its 6845 tables for 40-column text,
    // 80-column text and graphics
    private static readonly byte[] ModeControls = [0x2C, 0x28, 0x2D, 0x29, 0x2A, 0x2E, 0x1E, 0x29];
    private static readonly byte[] Text40 = [0x38, 0x28, 0x2D, 0x0A, 0x1F, 0x06, 0x19, 0x1C, 0x02, 0x07, 0x06, 0x07, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Text80 = [0x71, 0x50, 0x5A, 0x0A, 0x1F, 0x06, 0x19, 0x1C, 0x02, 0x07, 0x06, 0x07, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Graphics = [0x38, 0x28, 0x2D, 0x0A, 0x7F, 0x06, 0x64, 0x70, 0x02, 0x01, 0x06, 0x07, 0x00, 0x00, 0x00, 0x00];

    private readonly byte[] _memory = new byte[0x4000];
    private readonly byte[] _crtc = new byte[18];
    private int _crtcIndex;
    private byte _modeControl;
    private byte _colorSelect;

    // the composite decoder's table and coefficients, worked out when the mode changes
    private readonly int[] _compositeTable = new int[1024];
    private int _ri, _rq, _gi, _gq, _bi, _bq;
    private bool _compositeReady;

    public Cga() => SetMode(3, clear: true);

    /// <summary>The time in seconds, set by the machine before each access, from which the retrace is timed.</summary>
    public double Time { get; set; }

    /// <summary>The mode the BIOS last set.</summary>
    public int Mode { get; private set; }

    /// <summary>Whether the picture is decoded as a composite monitor shows it rather than as RGBI.</summary>
    public bool Composite
    {
        get;
        set
        {
            field = value;
            _compositeReady = false;
        }
    }

    private bool GraphicsMode => (_modeControl & 0x02) != 0;

    private bool BlackAndWhite => (_modeControl & 0x04) != 0;

    private bool Enabled => (_modeControl & 0x08) != 0;

    private bool HighResolution => (_modeControl & 0x10) != 0;

    // ---- memory and ports --------------------------------------------------------------------

    /// <summary>A read from B8000h to BFFFFh, where the 16 KB appear twice.</summary>
    public byte Read(uint address) => _memory[address & 0x3FFF];

    public void Write(uint address, byte value) => _memory[address & 0x3FFF] = value;

    public byte In(int port) => port switch
    {
        // the 6845's registers can only be read in part; the index port reads back as nothing
        0x3D1 or 0x3D3 or 0x3D5 or 0x3D7 => _crtcIndex is >= 0x0E and <= 0x11 ? _crtc[_crtcIndex] : (byte)0x00,
        0x3DA => Status(),
        _ => 0xFF,
    };

    public void Out(int port, byte value)
    {
        switch (port)
        {
            // the 6845's index and data, which appear at each even and odd port of 3D0h to 3D7h
            case 0x3D0 or 0x3D2 or 0x3D4 or 0x3D6:
                _crtcIndex = value & 0x1F;
                break;
            case 0x3D1 or 0x3D3 or 0x3D5 or 0x3D7:
                if (_crtcIndex < _crtc.Length)
                {
                    _crtc[_crtcIndex] = value;
                }

                break;
            case 0x3D8:
                _modeControl = (byte)(value & 0x3F);
                _compositeReady = false;
                break;
            case 0x3D9:
                _colorSelect = (byte)(value & 0x3F);
                break;
        }
    }

    /// <summary>
    /// The status register, port 3DAh: bit 0 set while the beam isn't drawing, between lines or
    /// below the picture, when a program can reach the memory without the "snow" the CGA showed
    /// otherwise; bit 3 set during the vertical retrace.
    /// </summary>
    private byte Status()
    {
        double frame = Time * FrameRate;
        double line = (frame - Math.Floor(frame)) * TotalLines;
        bool retrace = line >= RetraceStart && line < RetraceStart + RetraceLines;

        // 640 of a line's 912 dots are drawn
        bool blank = line >= VisibleLines || (line - Math.Floor(line)) >= 640.0 / 912;
        return (byte)(0xF0 | (retrace ? 0x08 : 0) | (blank ? 0x01 : 0));
    }

    // ---- modes, for the BIOS -------------------------------------------------------------------

    /// <summary>The mode control register's value, which the BIOS keeps a copy of.</summary>
    public byte ModeControl => _modeControl;

    /// <summary>The color select register's value, for the BIOS's palette call.</summary>
    public byte ColorSelect
    {
        get => _colorSelect;
        set => Out(0x3D9, value);
    }

    /// <summary>
    /// Sets one of the CGA's modes as IBM's BIOS does: the 6845's table for it, the mode control
    /// and color select registers, and, unless asked not to, the memory cleared. Modes 0 to 3
    /// are text, 4 and 5 are 320 by 200 in four colors (5 with the color burst off, so in gray
    /// on a composite monitor), and 6 is 640 by 200 in two.
    /// </summary>
    public void SetMode(int mode, bool clear)
    {
        Mode = mode;
        int index = mode is >= 0 and <= 7 ? mode : 3;
        byte[] table = index switch { 0 or 1 => Text40, 4 or 5 or 6 => Graphics, _ => Text80 };
        table.CopyTo(_crtc, 0);
        _modeControl = ModeControls[index];
        _colorSelect = (byte)(index == 6 ? 0x3F : 0x30);
        _compositeReady = false;
        if (clear)
        {
            if (index >= 4 && index != 7)
            {
                Array.Clear(_memory);
            }
            else
            {
                for (int i = 0; i < _memory.Length; i += 2)
                {
                    _memory[i] = (byte)' ';
                    _memory[i + 1] = 0x07;
                }
            }
        }
    }

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>
    /// Draws the picture as 640 by 400 pixels of 32-bit ARGB: blank with the video turned off,
    /// black in the text modes for now.
    /// </summary>
    public void Render(Span<uint> pixels)
    {
        var picture = pixels[..(Width * Height)];
        if (!Enabled || !GraphicsMode)
        {
            picture.Fill(0xFF00_0000);
            return;
        }

        // the 640 dots of a line, each a 4-bit RGBI color
        Span<byte> dots = stackalloc byte[Width];
        Span<uint> line = stackalloc uint[Width];
        int start = ((_crtc[0x0C] << 8) | _crtc[0x0D]) * 2;
        var palette = Palette();
        if (Composite && !_compositeReady)
        {
            PrepareComposite();
        }

        for (int y = 0; y < VisibleLines; y++)
        {
            // the even lines' bank, then the odd lines'
            int row = ((y & 1) << 13) | ((start + ((y >> 1) * 80)) & 0x1FFF);
            for (int x = 0; x < 80; x++)
            {
                byte b = _memory[row + x];
                for (int i = 0; i < 8; i++)
                {
                    dots[(x * 8) + i] = HighResolution
                        ? (b & (0x80 >> i)) != 0 ? (byte)(_colorSelect & 0x0F) : (byte)0
                        : palette[(b >> (6 - ((i >> 1) * 2))) & 3];
                }
            }

            if (Composite)
            {
                DecodeComposite(dots, line, HighResolution ? (byte)0 : (byte)(_colorSelect & 0x0F));
            }
            else
            {
                for (int x = 0; x < Width; x++)
                {
                    line[x] = 0xFF00_0000 | Rgbi[dots[x]];
                }
            }

            line.CopyTo(picture.Slice(y * 2 * Width, Width));
            line.CopyTo(picture.Slice(((y * 2) + 1) * Width, Width));
        }
    }

    /// <summary>
    /// The four colors of the 320-wide modes: the background, then the palette the color select
    /// register picks, brightened by its intensity bit. With the color burst off (mode 5) it's
    /// cyan, red and white, whichever palette is selected.
    /// </summary>
    private byte[] Palette()
    {
        byte background = (byte)(_colorSelect & 0x0F);
        byte bright = (byte)((_colorSelect & 0x10) != 0 ? 8 : 0);
        return BlackAndWhite ? [background, (byte)(3 + bright), (byte)(4 + bright), (byte)(7 + bright)]
            : (_colorSelect & 0x20) != 0 ? [background, (byte)(3 + bright), (byte)(5 + bright), (byte)(7 + bright)]
            : [background, (byte)(2 + bright), (byte)(4 + bright), (byte)(6 + bright)];
    }

    // ---- composite ----------------------------------------------------------------------------

    // reenigne's measurements of the CGA's output: for each pair of neighboring RGBI colors and
    // each of the four positions against the color carrier, the chroma signal's level
    private static readonly byte[] ChromaMultiplexer =
    [
        2, 2, 2, 2, 114, 174, 4, 3, 2, 1, 133, 135, 2, 113, 150, 4, 133, 2, 1, 99, 151, 152, 2, 1, 3, 2,
        96, 136, 151, 152, 151, 152, 2, 56, 62, 4, 111, 250, 118, 4, 0, 51, 207, 137, 1, 171, 209, 5,
        140, 50, 54, 100, 133, 202, 57, 4, 2, 50, 153, 149, 128, 198, 198, 135, 32, 1, 36, 81, 147, 158,
        1, 42, 33, 1, 210, 254, 34, 109, 169, 77, 177, 2, 0, 165, 189, 154, 3, 44, 33, 0, 91, 197, 178,
        142, 144, 192, 4, 2, 61, 67, 117, 151, 112, 83, 4, 0, 249, 255, 3, 107, 249, 117, 147, 1, 50,
        162, 143, 141, 52, 54, 3, 0, 145, 206, 124, 123, 192, 193, 72, 78, 2, 0, 159, 208, 4, 0, 53, 58,
        164, 159, 37, 159, 171, 1, 248, 117, 4, 98, 212, 218, 5, 2, 54, 59, 93, 121, 176, 181, 134, 130,
        1, 61, 31, 0, 160, 255, 34, 1, 1, 58, 197, 166, 0, 177, 194, 2, 162, 111, 34, 96, 205, 253, 32,
        1, 1, 57, 123, 125, 119, 188, 150, 112, 78, 4, 0, 75, 166, 180, 20, 38, 78, 1, 143, 246, 42, 113,
        156, 37, 252, 4, 1, 188, 175, 129, 1, 37, 118, 4, 88, 249, 202, 150, 145, 200, 61, 59, 60, 60,
        228, 252, 117, 77, 60, 58, 248, 251, 81, 212, 254, 107, 198, 59, 58, 169, 250, 251, 81, 80, 100,
        58, 154, 250, 251, 252, 252, 252,
    ];

    // and the luminance of the four combinations of the intensity bits of the two colors
    private static readonly float[] Intensity = [77.175381f, 88.654656f, 166.564623f, 174.228438f];

    /// <summary>
    /// Works out the decoder's table: the signal level of each pair of neighboring dots at each
    /// phase of the carrier, scaled to the monitor's contrast and brightness, and the coefficients
    /// that turn the decoded chroma into red, green and blue. Ported from DOSBox Staging's
    /// <c>update_cga16_color</c>, with its default knobs: contrast 100, brightness 0, saturation
    /// 100, hue 0, the old era's CGA, and no sharpening.
    /// </summary>
    private void PrepareComposite()
    {
        const float tau = (float)(2 * Math.PI);
        const float contrast = 100, brightness = 0, saturation = 100, hue = 0;

        float minimum = ChromaMultiplexer[0] + Intensity[0];
        float maximum = ChromaMultiplexer[255] + Intensity[3];
        float modeContrast = 2.56f * contrast / (maximum - minimum);
        float modeBrightness = (brightness * 5) - (256 * minimum / (maximum - minimum));
        const float modeHue = 4;
        float modeSaturation = saturation * 2.9f / 100;

        // with the color burst off (modes 5 and 6, as the BIOS sets them), any color but black
        // is taken as white's chroma, so all of them come out gray
        bool burstOff = BlackAndWhite;
        for (int x = 0; x < 1024; x++)
        {
            int right = (x >> 2) & 15;
            int rc = burstOff ? (right & 8) | ((right & 7) != 0 ? 7 : 0) : right;
            int left = (x >> 6) & 15;
            int lc = burstOff ? (left & 8) | ((left & 7) != 0 ? 7 : 0) : left;
            int phase = x & 3;
            float c = ChromaMultiplexer[((lc & 7) << 5) | ((rc & 7) << 2) | phase];
            float i = Intensity[(left >> 3) | ((right >> 2) & 2)];
            _compositeTable[x] = (int)(((c + i) * modeContrast) + modeBrightness);
        }

        float iq = _compositeTable[6 * 68] - _compositeTable[(6 * 68) + 2];
        float qq = _compositeTable[(6 * 68) + 1] - _compositeTable[(6 * 68) + 3];
        float a = tau * (33 + 90 + hue + modeHue) / 360.0f;
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        float r = BlackAndWhite ? 0.0f : 256 * modeSaturation / MathF.Sqrt((iq * iq) + (qq * qq));
        float adjustI = -((iq * cos) + (qq * sin)) * r;
        float adjustQ = ((qq * cos) - (iq * sin)) * r;

        const float ri = 0.9563f, rq = 0.6210f, gi = -0.2721f, gq = -0.6474f, bi = -1.1069f, bq = 1.7046f;
        _ri = (int)((ri * adjustI) + (rq * adjustQ));
        _rq = (int)((-ri * adjustQ) + (rq * adjustI));
        _gi = (int)((gi * adjustI) + (gq * adjustQ));
        _gq = (int)((-gi * adjustQ) + (gq * adjustI));
        _bi = (int)((bi * adjustI) + (bq * adjustQ));
        _bq = (int)((-bi * adjustQ) + (bq * adjustI));
        _compositeReady = true;
    }

    /// <summary>
    /// Decodes a line of 640 dots as a composite monitor would: the signal, with the border's
    /// color on either side, is made from the table, its chroma separated by a comb filter over
    /// a carrier cycle, and the rest is luminance. Ported from DOSBox Staging's
    /// <c>Composite_Process</c>.
    /// </summary>
    private void DecodeComposite(ReadOnlySpan<byte> dots, Span<uint> line, byte border)
    {
        int w = dots.Length;
        Span<int> signal = stackalloc int[w + 10];
        int n = 0;
        int borderAt = border * 68;
        for (int x = 0; x < 4; x++)
        {
            signal[n++] = _compositeTable[borderAt + ((x + 3) & 3)];
        }

        signal[n++] = _compositeTable[(border << 6) | (dots[0] << 2) | 3];
        for (int x = 0; x < w - 1; x++)
        {
            signal[n++] = _compositeTable[(dots[x] << 6) | (dots[x + 1] << 2) | (x & 3)];
        }

        signal[n++] = _compositeTable[(dots[w - 1] << 6) | (border << 2) | 3];
        for (int x = 0; x < 5; x++)
        {
            signal[n++] = _compositeTable[borderAt + (x & 3)];
        }

        static uint Clamp(int v)
        {
            v >>= 13;
            return v < 0 ? 0u : v > 255 ? 255u : (uint)v;
        }

        if (BlackAndWhite)
        {
            for (int x = 0, i = 5; x < w; x++, i++)
            {
                int c = (signal[i] + signal[i]) << 3;
                int d = (signal[i - 1] + signal[i + 1]) << 3;
                uint y = Clamp((c + d) << 8);
                line[x] = 0xFF00_0000 | (y * 0x10101);
            }

            return;
        }

        // the chroma, as two signals a quarter of a carrier cycle apart
        Span<int> chromaA = stackalloc int[w + 2];
        Span<int> chromaB = stackalloc int[w + 2];
        for (int x = -1, i = 4; x < w + 1; x++, i++)
        {
            chromaA[x + 1] = signal[i - 4] - ((signal[i - 2] - signal[i] + signal[i + 2]) << 1) + signal[i + 4];
            chromaB[x + 1] = (signal[i - 3] - signal[i - 1] + signal[i + 1] - signal[i + 3]) << 1;
        }

        // the luminance, with the chroma taken out, and the color from both
        int at = 5, chroma = 1;
        signal[at - 1] = (signal[at - 1] << 3) - chromaA[chroma - 1];
        signal[at] = (signal[at] << 3) - chromaA[chroma];
        int output = 0;
        void Convert(Span<int> signal, Span<int> chromaA, Span<uint> line, int ii, int q)
        {
            signal[at + 1] = (signal[at + 1] << 3) - chromaA[chroma + 1];
            int c = signal[at] + signal[at];
            int d = signal[at - 1] + signal[at + 1];
            int y = (c + d) << 8;
            uint red = Clamp(y + (_ri * ii) + (_rq * q));
            uint green = Clamp(y + (_gi * ii) + (_gq * q));
            uint blue = Clamp(y + (_bi * ii) + (_bq * q));
            at++;
            chroma++;
            line[output++] = 0xFF00_0000 | (red << 16) | (green << 8) | blue;
        }

        for (int x = 0; x < w / 4; x++)
        {
            Convert(signal, chromaA, line, chromaA[chroma], chromaB[chroma]);
            Convert(signal, chromaA, line, -chromaB[chroma], chromaA[chroma]);
            Convert(signal, chromaA, line, -chromaA[chroma], -chromaB[chroma]);
            Convert(signal, chromaA, line, chromaB[chroma], -chromaA[chroma]);
        }
    }
}
