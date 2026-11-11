using System.Buffers.Binary;
using System.Text;

namespace Excavate.Core.Dos;

/// <summary>
/// A CD's image in ISO 9660, the CD-ROM's file system: what's on the disc, by name, and where.
/// Read-only, as a CD is. Only the plain names are read: the Joliet and Rock Ridge extensions,
/// for long names, came after these games and aren't needed.
/// </summary>
/// <remarks>
/// <para>
/// The disc is 2048-byte sectors. The first sixteen are left for the system; the volume
/// descriptors follow, from sector 16, and the primary one (type 1) holds the volume's name, its
/// size, and the root directory's record. A directory is a run of sectors of records, one per
/// entry: its length, where its data starts (the sector) and how long it is, its date, its flags
/// (02h for a directory), and its name. A record never crosses a sector: the rest of a sector
/// too short for the next one is zeros. Numbers are written twice, little-endian then
/// big-endian, and the little-endian one is read.
/// </para>
/// <para>
/// A file's name ends in its version, <c>;1</c>, which DOS never shows, and one without an
/// extension ends in a dot. The first two records of every directory are itself and its parent,
/// named by the bytes 0 and 1.
/// </para>
/// </remarks>
internal sealed class IsoImage
{
    public const int SectorSize = 2048;

    /// <summary>An entry of a directory: its DOS name, uppercase, and where its data is.</summary>
    internal sealed class Entry
    {
        public string Name { get; init; } = "";

        public long Sector { get; init; }

        public long Size { get; init; }

        public bool IsDirectory { get; init; }

        public DateTime Time { get; init; }
    }

    // directories read so far, by their first sector
    private readonly Dictionary<long, List<Entry>> _directories = [];

    private readonly Entry _root;

    /// <summary>Opens an image and reads its primary volume descriptor.</summary>
    /// <exception cref="InvalidDataException">It isn't an ISO 9660 image.</exception>
    public IsoImage(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        using var stream = Open();
        for (long sector = 16; ; sector++)
        {
            byte[] descriptor = ReadSectors(stream, sector, 1);
            if (descriptor[1..6].AsSpan().SequenceEqual("CD001"u8) is false)
            {
                throw new InvalidDataException($"{path} isn't a CD image in ISO 9660.");
            }

            if (descriptor[0] == 1)
            {
                Label = Encoding.ASCII.GetString(descriptor, 40, 32).TrimEnd();
                Sectors = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(80));
                _root = Record(descriptor.AsSpan(156, 34), root: true) ?? throw new InvalidDataException($"{path} has no root directory.");
                break;
            }

            if (descriptor[0] == 255)
            {
                throw new InvalidDataException($"{path} has no primary volume descriptor.");
            }
        }
    }

    /// <summary>The image's file.</summary>
    public string Path { get; }

    /// <summary>The volume's name, as in <c>WC3_CD1</c>.</summary>
    public string Label { get; } = "";

    /// <summary>The volume's size, in sectors.</summary>
    public long Sectors { get; }

    /// <summary>Opens the image for reading, shared, as a stream of its own.</summary>
    public FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Reads whole sectors from the image.</summary>
    public static byte[] ReadSectors(Stream stream, long sector, int count)
    {
        var buffer = new byte[count * SectorSize];
        stream.Position = sector * SectorSize;
        stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer;
    }

    /// <summary>The entry a path names, from the root; the root itself for no components.</summary>
    public Entry? Find(string[] parts)
    {
        var entry = _root;
        foreach (string part in parts)
        {
            if (!entry.IsDirectory)
            {
                return null;
            }

            entry = List(entry).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }
        }

        return entry;
    }

    /// <summary>The entries of a directory, itself and its parent left out.</summary>
    public IReadOnlyList<Entry> List(Entry directory)
    {
        if (_directories.TryGetValue(directory.Sector, out var known))
        {
            return known;
        }

        var entries = new List<Entry>();
        using (var stream = Open())
        {
            byte[] data = ReadSectors(stream, directory.Sector, (int)((directory.Size + SectorSize - 1) / SectorSize));
            int at = 0;
            while (at < directory.Size)
            {
                int length = data[at];
                if (length == 0)
                {
                    // the rest of this sector is padding
                    at = ((at / SectorSize) + 1) * SectorSize;
                    continue;
                }

                if (Record(data.AsSpan(at, length)) is { } entry)
                {
                    entries.Add(entry);
                }

                at += length;
            }
        }

        _directories[directory.Sector] = entries;
        return entries;
    }

    /// <summary>
    /// A directory record as an entry; null for a directory's records of itself and its parent,
    /// except for the root's own, in the volume descriptor.
    /// </summary>
    private static Entry? Record(ReadOnlySpan<byte> record, bool root = false)
    {
        int nameLength = record[32];
        var name = record.Slice(33, nameLength);
        if (nameLength == 1 && name[0] is 0 or 1 && !root)
        {
            return null;
        }

        string text = Encoding.ASCII.GetString(name).ToUpperInvariant();
        int version = text.IndexOf(';');
        if (version >= 0)
        {
            text = text[..version];
        }

        // the date: years since 1900, month, day, hour, minute, second, and the time zone
        DateTime time;
        try
        {
            time = new DateTime(1900 + record[18], Math.Max((int)record[19], 1), Math.Max((int)record[20], 1), record[21], record[22], record[23]);
        }
        catch (ArgumentOutOfRangeException)
        {
            time = new DateTime(1980, 1, 1);
        }

        return new Entry
        {
            Name = text.TrimEnd('.'),
            Sector = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]),
            IsDirectory = (record[25] & 0x02) != 0,
            Time = time,
        };
    }
}
