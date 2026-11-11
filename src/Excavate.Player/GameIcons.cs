namespace Excavate.Player;

/// <summary>A picture of an icon: <see cref="Width"/> by <see cref="Height"/> pixels of ARGB, row by row from the top.</summary>
internal sealed record IconImage(int Width, int Height, uint[] Pixels);

/// <summary>
/// The games' icons, built into the player (<c>Icons/</c>, named by each game's short name), the
/// player's own, and a reader for the Windows icon files they come in.
/// </summary>
/// <remarks>
/// An <c>.ico</c> file is a directory of pictures of one icon at several sizes. Each is either a
/// PNG or a Windows bitmap without its file header: a <c>BITMAPINFOHEADER</c> whose height counts
/// twice, because the colors are followed by a mask of one bit a pixel, set where the icon is
/// see-through. The rows run from the bottom up, each padded to four bytes. The reader takes the
/// bitmaps of 24 and 32 bits a pixel, which is what the icons have at their small sizes, and
/// leaves the PNGs (only at 256 pixels here, more than a window's icon needs) and anything else.
/// </remarks>
internal static class GameIcons
{
    /// <summary>
    /// The player's own icon, and the menu's; none until there's one in <c>Icons/</c>, and the
    /// window then has the system's own.
    /// </summary>
    public static IReadOnlyList<IconImage> Excavate => Built("excavate");

    /// <summary>The icon for a game, as pictures at its sizes; the player's own for a game without one.</summary>
    public static IReadOnlyList<IconImage> For(Game game)
        => game.Known is { } known && Built(known.Key) is { Count: > 0 } icon ? icon : Excavate;

    private static IReadOnlyList<IconImage> Built(string name)
    {
        using var stream = typeof(GameIcons).Assembly.GetManifestResourceStream($"Icons/{name}.ico");
        if (stream is null)
        {
            return [];
        }

        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Read(bytes.ToArray());
    }

    /// <summary>The bitmaps in an icon file, of the kinds the reader takes.</summary>
    public static IReadOnlyList<IconImage> Read(byte[] ico)
    {
        var images = new List<IconImage>();
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1)
        {
            return images;
        }

        int count = BitConverter.ToUInt16(ico, 4);
        for (int i = 0; i < count && 6 + (16 * (i + 1)) <= ico.Length; i++)
        {
            int entry = 6 + (16 * i);
            int size = BitConverter.ToInt32(ico, entry + 8);
            int offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || size < 40 || offset + size > ico.Length || BitConverter.ToInt32(ico, offset) != 40)
            {
                // a PNG, or not a bitmap the reader knows
                continue;
            }

            if (Bitmap(ico.AsSpan(offset, size)) is { } image)
            {
                images.Add(image);
            }
        }

        return images;
    }

    private static IconImage? Bitmap(ReadOnlySpan<byte> dib)
    {
        int width = BitConverter.ToInt32(dib[4..]);
        int height = BitConverter.ToInt32(dib[8..]) / 2;
        int bits = BitConverter.ToUInt16(dib[14..]);
        if (width is <= 0 or > 256 || height is <= 0 or > 256 || bits is not (24 or 32) || BitConverter.ToInt32(dib[16..]) != 0)
        {
            return null;
        }

        int colorStride = ((width * bits) + 31) / 32 * 4;
        int maskStride = (width + 31) / 32 * 4;
        int colors = 40, mask = colors + (colorStride * height);
        if (mask > dib.Length)
        {
            return null;
        }

        // 32-bit pictures carry their own alpha; 24-bit ones, and 32-bit ones whose alpha was left
        // at nothing everywhere, take their shape from the mask. Some writers leave the mask out
        // of a 32-bit picture, whose alpha makes it needless: then it has to have its alpha.
        bool hasMask = mask + (maskStride * height) <= dib.Length;
        bool ownAlpha = false;
        if (bits == 32)
        {
            for (int y = 0; y < height && !ownAlpha; y++)
            {
                for (int x = 0; x < width && !ownAlpha; x++)
                {
                    ownAlpha = dib[colors + (y * colorStride) + (x * 4) + 3] != 0;
                }
            }
        }

        if (!ownAlpha && !hasMask)
        {
            return null;
        }

        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = colors + ((height - 1 - y) * colorStride);
            int maskRow = mask + ((height - 1 - y) * maskStride);
            for (int x = 0; x < width; x++)
            {
                int at = row + (x * bits / 8);
                uint a = ownAlpha
                    ? dib[at + 3]
                    : (dib[maskRow + (x / 8)] & (0x80 >> (x % 8))) != 0 ? 0u : 255u;
                pixels[(y * width) + x] = (a << 24) | ((uint)dib[at + 2] << 16) | ((uint)dib[at + 1] << 8) | dib[at];
            }
        }

        return new IconImage(width, height, pixels);
    }
}
