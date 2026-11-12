using Excavate.Core.Devices;

namespace Excavate.Core;

// The video BIOS's text: the cursor and the display pages, characters written and read, windows
// scrolled, and the teletype that DOS's console output goes through.
public sealed partial class Machine
{
    private const ushort TextSegment = 0xB800;

    // the BIOS data area's video fields
    private const uint ColumnsField = 0x4A, PageSizeField = 0x4C, PageStartField = 0x4E,
        CursorsField = 0x50, CursorShapeField = 0x60, PageField = 0x62, RowsField = 0x84, HeightField = 0x85;

    private bool TextMode => VideoMode is <= 3 or 7;

    private int Columns => Math.Max((int)ReadWord(0x40, ColumnsField), 1);

    private int Rows => _cga is null ? ReadByte(0x40, RowsField) + 1 : 25;

    private int ActivePage => ReadByte(0x40, PageField) & 7;

    // the lines in a character the BIOS draws in a graphics mode: the font of the EGA's 350
    // lines and the VGA's 480 fits 25 and 30 rows in them; the rest have 200 lines and 8
    private int CharacterHeight => _cga is null ? ReadByte(0x40, HeightField) : 8;

    /// <summary>
    /// The BIOS data area's text fields as setting a mode leaves them: the size of a page, the
    /// first page showing, every page's cursor at the top left, and the cursor's shape; on the
    /// VGA, the rows and the character's height too.
    /// </summary>
    private void ResetText(int mode)
    {
        bool text = mode is <= 3 or 7;
        int columns = Columns;
        int pageSize = mode switch
        {
            _ when text => columns == 40 ? 0x800 : 0x1000,
            0x0D => 0x2000,
            0x10 => 0x8000,
            0x12 => 0xA000,
            0x13 => 0xFA00,
            _ => 0x4000,
        };
        WriteWord(0x40, PageSizeField, (ushort)pageSize);
        WriteWord(0x40, PageStartField, 0);
        for (uint page = 0; page < 8; page++)
        {
            WriteWord(0x40, CursorsField + (page * 2), 0);
        }

        WriteWord(0x40, CursorShapeField, 0x0607);
        WriteByte(0x40, PageField, 0);
        if (_cga is null)
        {
            WriteByte(0x40, RowsField, (byte)(mode == 0x12 ? 29 : 24));
            WriteWord(0x40, HeightField, (ushort)(text || mode == 0x12 ? 16 : mode == 0x10 ? 14 : 8));
        }
    }

    // a register of the CRT controller, the 6845 or the VGA's, which both answer at 3D4h and 3D5h
    private void Crtc(int index, int value)
    {
        Out(0x3D4, (byte)index);
        Out(0x3D5, (byte)value);
    }

    private (int Row, int Column) CursorOf(int page) =>
        (ReadByte(0x40, CursorsField + 1 + ((uint)page * 2)), ReadByte(0x40, CursorsField + ((uint)page * 2)));

    /// <summary>Moves a page's cursor, and the one on the screen if it's the page showing.</summary>
    private void SetCursor(int page, int row, int column)
    {
        WriteByte(0x40, CursorsField + ((uint)page * 2), (byte)column);
        WriteByte(0x40, CursorsField + 1 + ((uint)page * 2), (byte)row);
        if (page == ActivePage)
        {
            int cell = (ReadWord(0x40, PageStartField) / 2) + (row * Columns) + column;
            Crtc(0x0E, cell >> 8);
            Crtc(0x0F, cell & 0xFF);
        }
    }

    // where a cell of a page is in memory
    private uint Cell(int page, int row, int column) =>
        ((uint)TextSegment << 4) + (uint)(page * ReadWord(0x40, PageSizeField)) + (uint)(((row * Columns) + column) * 2);

    /// <summary>
    /// The cursor's shape (INT 10h function 01h): its first line in CH, with bit 5 hiding it, and
    /// its last in CL. Given for an 8-line character; the VGA, with 16 lines, has them doubled.
    /// </summary>
    private void SetCursorShape(int top, int bottom)
    {
        WriteWord(0x40, CursorShapeField, (ushort)((top << 8) | bottom));
        if (_cga is null && (top & 0x20) == 0 && top < 8 && bottom < 8)
        {
            top = top * 2;
            bottom = (bottom * 2) + 1;
        }

        Crtc(0x0A, top);
        Crtc(0x0B, bottom);
    }

    /// <summary>Shows a display page (INT 10h function 05h), in the text modes.</summary>
    private void SelectPage(int page)
    {
        page &= 7;
        WriteByte(0x40, PageField, (byte)page);
        int start = page * ReadWord(0x40, PageSizeField);
        WriteWord(0x40, PageStartField, (ushort)start);

        // the CRT controller counts text in cells, two bytes each, and the planes in bytes
        int address = TextMode ? start / 2 : start;
        Crtc(0x0C, address >> 8);
        Crtc(0x0D, address & 0xFF);
        var (row, column) = CursorOf(page);
        SetCursor(page, row, column);
    }

    /// <summary>
    /// Scrolls a window of a page up or down by some lines (INT 10h functions 06h and 07h),
    /// filling the lines it opens with spaces in an attribute, or in graphics with color 0, the
    /// attribute's color for the 256-color mode. No lines, or more than the window has, clears it.
    /// </summary>
    private void Scroll(int page, int top, int left, int bottom, int right, int lines, bool up, byte attribute)
    {
        bottom = Math.Min(bottom, Rows - 1);
        right = Math.Min(right, Columns - 1);
        if (top > bottom || left > right)
        {
            return;
        }

        int height = bottom - top + 1;
        if (lines <= 0 || lines > height)
        {
            lines = height;
        }

        for (int i = 0; i < height; i++)
        {
            int row = up ? top + i : bottom - i;
            int from = up ? row + lines : row - lines;
            bool fill = up ? from > bottom : from < top;
            for (int column = left; column <= right; column++)
            {
                if (TextMode)
                {
                    uint to = Cell(page, row, column);
                    if (fill)
                    {
                        Write(to, (byte)' ');
                        Write(to + 1, attribute);
                    }
                    else
                    {
                        uint source = Cell(page, from, column);
                        Write(to, Read(source));
                        Write(to + 1, Read(source + 1));
                    }
                }
                else
                {
                    int cellHeight = CharacterHeight;
                    for (int line = 0; line < cellHeight; line++)
                    {
                        for (int dot = 0; dot < 8; dot++)
                        {
                            int x = (column * 8) + dot;
                            byte color = !fill ? GraphicsPixel(x, (from * cellHeight) + line, null)
                                : VideoMode switch
                                {
                                    4 or 5 or 6 => (byte)0,
                                    0x13 => attribute,
                                    _ => (byte)(attribute & 0x0F),
                                };
                            GraphicsPixel(x, (row * cellHeight) + line, color);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// A pixel of the graphics modes: the CGA's, the EGA's 16-color ones and mode 13h. Written
    /// when a color is given, with bit 7 XORing it on except in mode 13h; its color returned.
    /// </summary>
    private byte GraphicsPixel(int x, int y, byte? color)
    {
        if (_cga is not null || VideoMode is 4 or 5 or 6)
        {
            return CgaPixel(x, y, color);
        }

        if (VideoMode != 0x13)
        {
            return _vga.PlanarPixel(x, y, color);
        }

        if (x >= 320 || y >= 200)
        {
            return 0;
        }

        uint address = 0xA0000 + (uint)((y * 320) + x);
        if (color is { } c)
        {
            Write(address, c);
        }

        return Read(address);
    }

    /// <summary>
    /// A character drawn in a graphics mode at a cell, from the font of the mode's height: its
    /// dots in the color, the rest in color 0, or with bit 7 of the color (but in mode 13h),
    /// XORed on.
    /// </summary>
    private void DrawCharacter(int row, int column, byte character, byte color)
    {
        bool xor = (color & 0x80) != 0 && VideoMode != 0x13;
        int height = CharacterHeight;
        var font = height switch
        {
            14 => RomFont.Glyphs14,
            16 => RomFont.Glyphs16,
            _ => RomFont.Glyphs,
        };
        height = font.Length / 256;
        var glyph = font.Slice(character * height, height);
        for (int line = 0; line < height; line++)
        {
            for (int dot = 0; dot < 8; dot++)
            {
                bool on = (glyph[line] & (0x80 >> dot)) != 0;
                if (on || !xor)
                {
                    GraphicsPixel((column * 8) + dot, (row * height) + line, on ? color : (byte)0);
                }
            }
        }
    }

    /// <summary>
    /// Writes a character some times from a page's cursor on (INT 10h functions 09h and 0Ah),
    /// without moving the cursor: in text, with an attribute or keeping the cells' own; in
    /// graphics, drawn in a color.
    /// </summary>
    private void WriteCharacters(int page, byte character, byte attribute, bool keepAttribute, int count)
    {
        var (row, column) = CursorOf(page);
        for (int i = 0; i < count; i++)
        {
            int at = column + i;
            int r = row + (at / Columns), c = at % Columns;
            if (r >= Rows)
            {
                break;
            }

            if (TextMode)
            {
                uint cell = Cell(page, r, c);
                Write(cell, character);
                if (!keepAttribute)
                {
                    Write(cell + 1, attribute);
                }
            }
            else
            {
                DrawCharacter(r, c, character, attribute);
            }
        }
    }

    /// <summary>
    /// Writes a character as a terminal would (INT 10h function 0Eh), on the page showing: bell,
    /// backspace, line feed and carriage return do their work; anything else is written at the
    /// cursor (in text keeping the cell's attribute, in graphics in a color) and the cursor moves
    /// on, to the next line at the end of one, the screen scrolling up past the last. The new line
    /// takes the attribute of the cell the cursor was on.
    /// </summary>
    internal void Teletype(byte character, byte color)
    {
        int page = ActivePage;
        var (row, column) = CursorOf(page);
        switch (character)
        {
            case 0x07:
                break;
            case 0x08:
                column = Math.Max(column - 1, 0);
                break;
            case 0x0A:
                row++;
                break;
            case 0x0D:
                column = 0;
                break;
            default:
                if (TextMode)
                {
                    Write(Cell(page, row, column), character);
                }
                else
                {
                    DrawCharacter(row, column, character, color);
                }

                if (++column >= Columns)
                {
                    column = 0;
                    row++;
                }

                break;
        }

        if (row >= Rows)
        {
            byte attribute = TextMode ? Read(Cell(page, Rows - 1, Math.Min(column, Columns - 1)) + 1) : (byte)0;
            Scroll(page, 0, 0, Rows - 1, Columns - 1, 1, up: true, attribute);
            row = Rows - 1;
        }

        SetCursor(page, row, column);
    }

    /// <summary>
    /// A character of DOS's console output, to the screen: through the teletype, with a tab
    /// spaced out to the next column of eight, as DOS's console device does.
    /// </summary>
    internal void ConsoleCharacter(char c)
    {
        if (c == '\t')
        {
            do
            {
                Teletype((byte)' ', 7);
            }
            while (CursorOf(ActivePage).Column % 8 != 0);

            return;
        }

        Teletype((byte)c, 7);
    }

    /// <summary>
    /// Writes a string (INT 10h function 13h) from ES:BP, CX characters long, at a page's row DH
    /// and column DL: with the attribute in BL, or, if AL's bit 1 is set, each character followed
    /// by its own. AL's bit 0 leaves the cursor after the string.
    /// </summary>
    private void WriteString()
    {
        int page = (byte)(Cpu.Ebx >> 8) & 7;
        var saved = CursorOf(page);
        bool attributes = (Al & 0x02) != 0;
        byte attribute = (byte)Cpu.Ebx;
        uint at = ((uint)Cpu.Es << 4) + (ushort)Cpu.Ebp;
        SetCursor(page, (byte)(Cpu.Edx >> 8), (byte)Cpu.Edx);
        for (int i = 0; i < (ushort)Cpu.Ecx; i++)
        {
            byte character = Read(at++);
            if (attributes)
            {
                attribute = Read(at++);
            }

            var (row, column) = CursorOf(page);
            if (character is 0x07 or 0x08 or 0x0A or 0x0D)
            {
                if (page == ActivePage)
                {
                    Teletype(character, attribute);
                }

                continue;
            }

            if (TextMode)
            {
                Write(Cell(page, row, column), character);
                Write(Cell(page, row, column) + 1, attribute);
            }
            else
            {
                DrawCharacter(row, column, character, attribute);
            }

            if (++column >= Columns)
            {
                column = 0;
                row = Math.Min(row + 1, Rows - 1);
            }

            SetCursor(page, row, column);
        }

        if ((Al & 0x01) == 0)
        {
            SetCursor(page, saved.Row, saved.Column);
        }
    }

    /// <summary>
    /// The video BIOS's text functions, the same on the CGA and the VGA. Returns whether the call
    /// was one of them.
    /// </summary>
    private bool TextService()
    {
        int page = (byte)(Cpu.Ebx >> 8) & 7;
        switch (Ah)
        {
            case 0x01:
                SetCursorShape((byte)(Cpu.Ecx >> 8), (byte)Cpu.Ecx);
                return true;
            case 0x02:
                SetCursor(page, (byte)(Cpu.Edx >> 8), (byte)Cpu.Edx);
                return true;
            case 0x03:
            {
                var (row, column) = CursorOf(page);
                SetCx(ReadWord(0x40, CursorShapeField));
                SetDx((row << 8) | column);
                return true;
            }

            case 0x05:
                if (TextMode)
                {
                    SelectPage(Al);
                }

                return true;
            case 0x06:
            case 0x07:
                Scroll(ActivePage, (byte)(Cpu.Ecx >> 8), (byte)Cpu.Ecx, (byte)(Cpu.Edx >> 8), (byte)Cpu.Edx, Al, up: Ah == 0x06, (byte)(Cpu.Ebx >> 8));
                return true;
            case 0x08:
            {
                var (row, column) = CursorOf(page);
                uint cell = Cell(page, row, column);
                SetAx(TextMode ? (Read(cell + 1) << 8) | Read(cell) : 0);
                return true;
            }

            case 0x09:
            case 0x0A:
                WriteCharacters(page, Al, (byte)Cpu.Ebx, keepAttribute: Ah == 0x0A && TextMode, (ushort)Cpu.Ecx);
                return true;
            case 0x0E:
                Teletype(Al, (byte)Cpu.Ebx);
                return true;
            case 0x0F:
                SetAx((Columns << 8) | VideoMode);
                Cpu.Ebx = (Cpu.Ebx & 0xFFFF_00FF) | ((uint)ActivePage << 8);
                return true;
            case 0x13:
                WriteString();
                return true;
            default:
                return false;
        }
    }
}
