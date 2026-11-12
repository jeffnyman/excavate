namespace Excavate.Core.Devices;

/// <summary>
/// Draws a text mode's screen, as the CGA, EGA and VGA all keep it: a character and an attribute
/// byte for each cell, row after row. The attribute's low four bits are the character's color and
/// the high four the background's, or, with blinking on, three bits of background and a bit that
/// makes the character blink.
/// </summary>
internal static class TextScreen
{
    /// <summary>The screen's layout in video memory and the font it's drawn with.</summary>
    /// <param name="Start">Where the first cell is, in bytes.</param>
    /// <param name="Columns">Cells across.</param>
    /// <param name="Rows">Rows of cells.</param>
    /// <param name="Stride">Bytes from one row's start to the next's.</param>
    /// <param name="Font">The glyphs, <paramref name="Height"/> bytes each, a row of eight dots a byte.</param>
    /// <param name="Height">The lines in a character.</param>
    public readonly record struct Layout(int Start, int Columns, int Rows, int Stride, byte[] Font, int Height);

    /// <summary>What the screen shows now: the colors, and the blinking.</summary>
    /// <param name="Colors">The 16 colors, as ARGB.</param>
    /// <param name="Blinking">Whether the attribute's top bit blinks the character, rather than brightening the background.</param>
    /// <param name="BlinkOn">Whether blinking characters are showing at this moment.</param>
    /// <param name="Cursor">The cell the cursor is on, in bytes from the start of memory; -1 for none.</param>
    /// <param name="CursorTop">The cursor's first line in the cell.</param>
    /// <param name="CursorBottom">Its last.</param>
    public readonly record struct Look(uint[] Colors, bool Blinking, bool BlinkOn, int Cursor, int CursorTop, int CursorBottom);

    /// <summary>
    /// Draws the screen into a picture <paramref name="width"/> pixels across, each dot of a
    /// character <paramref name="widen"/> pixels wide and each of its lines <paramref name="lines"/>
    /// pixels high. Memory wraps around at its length.
    /// </summary>
    public static void Draw(ReadOnlySpan<byte> memory, Layout layout, Look look, Span<uint> pixels, int width, int widen, int lines)
    {
        int mask = memory.Length - 1;
        for (int row = 0; row < layout.Rows; row++)
        {
            for (int column = 0; column < layout.Columns; column++)
            {
                int cell = (layout.Start + (row * layout.Stride) + (column * 2)) & mask;
                byte character = memory[cell];
                byte attribute = memory[(cell + 1) & mask];
                uint foreground = look.Colors[attribute & 0x0F];
                uint background = look.Colors[look.Blinking ? (attribute >> 4) & 0x07 : attribute >> 4];
                bool hidden = look.Blinking && (attribute & 0x80) != 0 && !look.BlinkOn;
                bool cursor = cell == look.Cursor;
                for (int line = 0; line < layout.Height; line++)
                {
                    int dots = hidden ? 0 : layout.Font[(character * layout.Height) + line];
                    if (cursor && line >= look.CursorTop && line <= look.CursorBottom)
                    {
                        dots = 0xFF;
                    }

                    for (int repeat = 0; repeat < lines; repeat++)
                    {
                        int y = (((row * layout.Height) + line) * lines) + repeat;
                        var target = pixels.Slice((y * width) + (column * 8 * widen), 8 * widen);
                        for (int dot = 0; dot < 8; dot++)
                        {
                            uint color = (dots & (0x80 >> dot)) != 0 ? foreground : background;
                            for (int w = 0; w < widen; w++)
                            {
                                target[(dot * widen) + w] = color;
                            }
                        }
                    }
                }
            }
        }
    }
}
