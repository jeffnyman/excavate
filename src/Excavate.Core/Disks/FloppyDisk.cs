namespace Excavate.Core.Disks;

/// <summary>
/// A floppy disk, from an image of its sectors: every sector of every track, in order, track by
/// track and side by side, as a dump of a disk reads it. The image's size gives the disk's
/// format, one of the PC's standard ones.
/// </summary>
/// <remarks>
/// <para>
/// A plain image keeps only the data of the sectors a standard format has. Copy protection of
/// the time often lived outside that: a track formatted with more sectors than the standard nine,
/// or sectors numbered out of the usual range, which a program then looks for. Those are lost in
/// a dump, and <see cref="Restore"/> puts back the ones a game is known to look for, so the game
/// finds the disk as it was made.
/// </para>
/// <para>
/// The image is read into memory and never written. What a program writes to the disk (a saved
/// game, say) goes into the copy in memory and, if the disk was given a place for it, into a
/// copy of the whole disk there, which is read in place of the image from then on.
/// </para>
/// </remarks>
internal sealed class FloppyDisk
{
    /// <summary>The size of a sector on a PC's floppy disks.</summary>
    public const int SectorSize = 512;

    // the PC's standard formats, by image size: cylinders, sides, and sectors to a track
    private static readonly (int Size, int Cylinders, int Heads, int Sectors, byte DriveType)[] Formats =
    [
        (163_840, 40, 1, 8, 1),     // 160K, single-sided, DOS 1.0
        (184_320, 40, 1, 9, 1),     // 180K
        (327_680, 40, 2, 8, 1),     // 320K, DOS 1.1
        (368_640, 40, 2, 9, 1),     // 360K, DOS 2.0 on
        (737_280, 80, 2, 9, 3),     // 720K, 3.5-inch
        (1_228_800, 80, 2, 15, 2),  // 1.2M, the AT's high density
        (1_474_560, 80, 2, 18, 4),  // 1.44M, 3.5-inch high density
    ];

    private readonly byte[] _data;

    // sectors restored to tracks, by cylinder and head: their numbers, beyond the track's own
    private readonly Dictionary<(int Cylinder, int Head), HashSet<int>> _restored = [];

    // where writes are kept, if anywhere
    [NotSaved]
    private readonly string? _writes;

    /// <summary>Reads a disk's image, or the copy of it holding what was written to it.</summary>
    /// <param name="image">The image's file.</param>
    /// <param name="writes">
    /// The file to keep the disk's writes in: read in place of the image when it's there, and
    /// written whenever a program writes a sector. Null to keep writes only in memory.
    /// </param>
    /// <exception cref="InvalidDataException">The image isn't the size of any PC floppy disk.</exception>
    public FloppyDisk(string image, string? writes = null)
    {
        Name = Path.GetFileName(image);
        _writes = writes;
        string from = writes is not null && File.Exists(writes) && new FileInfo(writes).Length == new FileInfo(image).Length ? writes : image;
        _data = File.ReadAllBytes(from);
        var format = Formats.FirstOrDefault(f => f.Size == _data.Length);
        if (format.Size == 0)
        {
            throw new InvalidDataException($"{Name} is {_data.Length:N0} bytes, which isn't the size of a PC floppy disk's image.");
        }

        (Cylinders, Heads, SectorsPerTrack, DriveType) = (format.Cylinders, format.Heads, format.Sectors, format.DriveType);
    }

    /// <summary>The image's file name, for messages.</summary>
    public string Name { get; }

    public int Cylinders { get; }

    public int Heads { get; }

    public int SectorsPerTrack { get; }

    /// <summary>The kind of drive the disk is for, as INT 13h function 08h reports it: 1 for 360K, 2 for 1.2M, 3 for 720K, 4 for 1.44M.</summary>
    public byte DriveType { get; }

    /// <summary>The whole disk, as its image has it with what's been written since.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>
    /// Puts back sectors a track had on the original disk beyond its standard ones, numbered
    /// <paramref name="first"/> to <paramref name="last"/>. Their contents weren't kept by the
    /// dump: they read as the formatting's filler, F6h.
    /// </summary>
    public void Restore(int cylinder, int head, int first, int last)
    {
        if (!_restored.TryGetValue((cylinder, head), out var sectors))
        {
            _restored[(cylinder, head)] = sectors = [];
        }

        for (int sector = first; sector <= last; sector++)
        {
            sectors.Add(sector);
        }
    }

    /// <summary>Whether a sector is on the disk: one of the track's standard sectors, or one restored to it.</summary>
    public bool Has(int cylinder, int head, int sector)
        => Standard(cylinder, head, sector) || (_restored.TryGetValue((cylinder, head), out var sectors) && sectors.Contains(sector));

    private bool Standard(int cylinder, int head, int sector)
        => cylinder >= 0 && cylinder < Cylinders && head >= 0 && head < Heads && sector >= 1 && sector <= SectorsPerTrack;

    /// <summary>Reads a sector the disk <see cref="Has"/>.</summary>
    public void Read(int cylinder, int head, int sector, Span<byte> into)
    {
        if (Standard(cylinder, head, sector))
        {
            _data.AsSpan(Offset(cylinder, head, sector), SectorSize).CopyTo(into);
        }
        else
        {
            into[..SectorSize].Fill(0xF6);
        }
    }

    /// <summary>Writes a sector the disk <see cref="Has"/>, keeping the disk's copy up to date.</summary>
    public void Write(int cylinder, int head, int sector, ReadOnlySpan<byte> from)
    {
        if (!Standard(cylinder, head, sector))
        {
            // a restored sector isn't in the image, so there's nowhere to keep what's written
            return;
        }

        from[..SectorSize].CopyTo(_data.AsSpan(Offset(cylinder, head, sector), SectorSize));
        if (_writes is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_writes)!);
            File.WriteAllBytes(_writes, _data);
        }
    }

    private int Offset(int cylinder, int head, int sector)
        => (((cylinder * Heads) + head) * SectorsPerTrack + (sector - 1)) * SectorSize;
}
