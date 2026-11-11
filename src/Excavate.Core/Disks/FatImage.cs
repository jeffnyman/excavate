using System.Buffers.Binary;
using System.Text;

namespace Excavate.Core.Disks;

/// <summary>
/// The files on a floppy disk with DOS's FAT12 file system: the boot sector, two copies of the
/// file allocation table, the root directory, and the clusters the files are kept in.
/// </summary>
/// <remarks>
/// The boot sector of a disk formatted by DOS 2.0 or later describes the layout, in its BIOS
/// parameter block. DOS 1's disks have none, and neither do disks a game formatted for itself,
/// so a boot sector that doesn't describe a sensible layout is passed over for the standard one
/// of a disk that size.
/// </remarks>
internal sealed class FatImage(FloppyDisk disk)
{
    /// <summary>A file or directory in a directory.</summary>
    public readonly record struct Entry(string Name, byte Attributes, int Cluster, int Size)
    {
        public bool IsDirectory => (Attributes & 0x10) != 0;
    }

    private readonly Layout _layout = Describe(disk);

    private readonly record struct Layout(int SectorsPerCluster, int ReservedSectors, int Fats, int RootEntries, int SectorsPerFat);

    private static Layout Describe(FloppyDisk disk)
    {
        byte[] boot = disk.Data[..FloppyDisk.SectorSize].ToArray();
        ushort Word(int at) => BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(at));
        var described = new Layout(boot[0x0D], Word(0x0E), boot[0x10], Word(0x11), Word(0x16));
        bool sensible = Word(0x0B) == FloppyDisk.SectorSize
            && described.SectorsPerCluster is 1 or 2 or 4
            && described.ReservedSectors >= 1
            && described.Fats is 1 or 2
            && described.RootEntries is > 0 and <= 512
            && described.SectorsPerFat is > 0 and <= 16
            && Word(0x13) == disk.Data.Length / FloppyDisk.SectorSize;
        if (sensible)
        {
            return described;
        }

        // the standard layouts, by the disk's size
        return disk.Data.Length switch
        {
            163_840 => new Layout(1, 1, 2, 64, 1),
            184_320 => new Layout(1, 1, 2, 64, 2),
            327_680 => new Layout(2, 1, 2, 112, 1),
            368_640 => new Layout(2, 1, 2, 112, 2),
            737_280 => new Layout(2, 1, 2, 112, 3),
            1_228_800 => new Layout(1, 1, 2, 224, 7),
            _ => new Layout(1, 1, 2, 224, 9),
        };
    }

    private int RootStart => (_layout.ReservedSectors + (_layout.Fats * _layout.SectorsPerFat)) * FloppyDisk.SectorSize;

    private int DataStart => RootStart + (_layout.RootEntries * 32);

    private int ClusterSize => _layout.SectorsPerCluster * FloppyDisk.SectorSize;

    /// <summary>The entries of the root directory, or of a directory in it, leaving out deleted entries and volume labels.</summary>
    public IEnumerable<Entry> List(Entry? directory = null)
    {
        byte[] entries = directory is { } d
            ? ReadChain(d.Cluster, int.MaxValue)
            : disk.Data.Slice(RootStart, _layout.RootEntries * 32).ToArray();
        for (int at = 0; at + 32 <= entries.Length; at += 32)
        {
            byte first = entries[at];
            if (first == 0)
            {
                // the end of the directory
                yield break;
            }

            byte attributes = entries[at + 11];
            if (first == 0xE5 || (attributes & 0x08) != 0)
            {
                continue;
            }

            string name = Encoding.ASCII.GetString(entries, at, 8).TrimEnd();
            string extension = Encoding.ASCII.GetString(entries, at + 8, 3).TrimEnd();
            yield return new Entry(
                extension.Length == 0 ? name : $"{name}.{extension}",
                attributes,
                BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(at + 26)),
                BinaryPrimitives.ReadInt32LittleEndian(entries.AsSpan(at + 28)));
        }
    }

    /// <summary>A file by its path from the root, as DOS's components, or null.</summary>
    public Entry? Find(IReadOnlyList<string> parts)
    {
        Entry? at = null;
        for (int i = 0; i < parts.Count; i++)
        {
            var found = List(at).Where(entry => string.Equals(entry.Name, parts[i], StringComparison.OrdinalIgnoreCase)).Cast<Entry?>().FirstOrDefault();
            if (found is not { } entry || (i < parts.Count - 1 && !entry.IsDirectory))
            {
                return null;
            }

            at = entry;
        }

        return at;
    }

    /// <summary>A file's contents.</summary>
    public byte[] Read(Entry file)
    {
        byte[] data = ReadChain(file.Cluster, file.Size);
        return data[..Math.Min(data.Length, file.Size)];
    }

    /// <summary>The clusters of a chain from the first, up to a number of bytes.</summary>
    private byte[] ReadChain(int cluster, int limit)
    {
        var bytes = new List<byte>();
        var fat = disk.Data.Slice(_layout.ReservedSectors * FloppyDisk.SectorSize, _layout.SectorsPerFat * FloppyDisk.SectorSize);
        int clusters = (disk.Data.Length - DataStart) / ClusterSize;
        var seen = new HashSet<int>();
        while (cluster >= 2 && cluster < clusters + 2 && bytes.Count < limit && seen.Add(cluster))
        {
            bytes.AddRange(disk.Data.Slice(DataStart + ((cluster - 2) * ClusterSize), ClusterSize));

            // twelve bits an entry: the even entries in the low bits of their pair of bytes
            int at = cluster * 3 / 2;
            int pair = fat[at] | (fat[at + 1] << 8);
            cluster = (cluster & 1) != 0 ? pair >> 4 : pair & 0xFFF;
        }

        return [.. bytes];
    }
}
