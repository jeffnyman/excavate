namespace Excavate.Core.Dos;

// The CD: drive D:, an image read through IsoImage, and MSCDEX, the driver DOS reached CDs by.
internal sealed partial class DosServices
{
    /// <summary>Drive numbers as DOS counts them in most calls: A: is 0, so C: is 2 and the CD, D:, 3.</summary>
    private const int DriveA = 0, DriveC = 2, DriveD = 3;

    /// <summary>
    /// The highest drive DOS answers for, as function 0Eh reports it: E:, DOS's own default for
    /// LASTDRIVE.
    /// </summary>
    private const int LastDrive = 5;

    // the CD's image, which the machine is built with, like the folders: a saved state goes into
    // a machine with its own
    [NotSaved]
    private IsoImage? _cd;

    // the current drive, and the CD's current directory (C:'s is the drive's own)
    private int _currentDrive = DriveC;
    private string _cdDirectory = "";

    /// <summary>A path, resolved: on the CD or on C:, as components from its root.</summary>
    private readonly record struct Location(bool Cd, string[] Parts)
    {
        public string Name => (Cd ? "D:\\" : "C:\\") + DosDrive.Join(Parts);
    }

    /// <summary>Puts a CD image in drive D:.</summary>
    public void InsertCd(IsoImage image) => _cd = image;

    /// <summary>The CD's image, if there is one.</summary>
    internal IsoImage? Cd => _cd;

    /// <summary>
    /// Where a DOS path is: on the drive it names, or the current drive, from that drive's
    /// current directory. Null for a drive that isn't there, or a path that climbs above a root.
    /// </summary>
    private Location? Locate(string name)
    {
        name = name.Trim();
        bool lettered = name.Length >= 2 && name[1] == ':';
        int number = lettered ? char.ToUpperInvariant(name[0]) - 'A' : _currentDrive;
        if (number == DriveD && _cd is not null)
        {
            return DosDrive.Resolve(lettered ? name[2..] : name, _cdDirectory) is { } parts ? new Location(true, parts) : null;
        }

        if (number == DriveC)
        {
            return drive.Parse(lettered ? name : "C:" + name) is { } parts ? new Location(false, parts) : null;
        }

        return null;
    }

    /// <summary>A file on the CD, or null; directories aren't files.</summary>
    private IsoImage.Entry? CdFile(string[] parts) => _cd?.Find(parts) is { IsDirectory: false } entry ? entry : null;

    private bool CdDirectoryExists(string[] parts) => _cd?.Find(parts) is { IsDirectory: true };

    /// <summary>Whether a drive, numbered from 0 for A:, is one of the machine's: A: when there are floppy disks for it.</summary>
    private bool DriveExists(int number)
        => number == DriveC || (number == DriveD && _cd is not null) || (number == DriveA && machine.Floppies > 0);

    /// <summary>A drive as the calls that number from 1 for A: give it, with 0 for the current drive, numbered from 0.</summary>
    private int DriveIn(byte number) => number == 0 ? _currentDrive : number - 1;

    // ---- MSCDEX ------------------------------------------------------------------------------

    /// <summary>
    /// INT 2Fh AH=15h: MSCDEX, the Microsoft CD-ROM extensions, the driver that made a CD a DOS
    /// drive. Programs ask it whether there's a CD drive, which letter, and its version, and
    /// some read sectors through it directly. False when there's no CD, as if it weren't
    /// installed.
    /// </summary>
    public bool CdCall()
    {
        var cpu = _registers;
        if (_cd is null)
        {
            // not installed: function 1500h leaves BX at zero drives
            if (cpu.Al == 0x00)
            {
                cpu.Bx = 0;
            }

            machine.Trace($"2F/15{cpu.Al:X2} no CD");
            return false;
        }

        switch (cpu.Al)
        {
            case 0x00:
                // installed: how many CD drives, and the first one's letter
                cpu.Bx = 1;
                cpu.Cx = DriveD;
                break;
            case 0x0B:
                // whether a drive is a CD drive, with MSCDEX's mark in BX
                cpu.Ax = (ushort)(cpu.Cx == DriveD ? 0x5AD8 : 0);
                cpu.Bx = 0xADAD;
                break;
            case 0x0C:
                // the version, 2.21 in BH and BL
                cpu.Bx = 0x0215;
                break;
            case 0x0D:
                // the CD drives' letters, a byte each
                machine.WriteByte(cpu.Es, cpu.Bx, DriveD);
                break;
            case 0x08:
                ReadCdSectors(cpu);
                break;
            default:
                throw new NotSupportedException($"MSCDEX function 15{cpu.Al:X2}h isn't provided.");
        }

        machine.Trace($"2F/15{cpu.Al:X2} MSCDEX: ax {cpu.Ax:X4} bx {cpu.Bx:X4} cx {cpu.Cx:X4}");
        return true;
    }

    /// <summary>
    /// MSCDEX 1508h, absolute read: DX sectors from sector SI:DI, of drive CX, into ES:BX. Carry
    /// with an error in AX for a drive that isn't a CD.
    /// </summary>
    private void ReadCdSectors(Registers cpu)
    {
        if (cpu.Cx != DriveD)
        {
            cpu.Ax = 0x000F;
            machine.SetReturnCarry(true);
            return;
        }

        long sector = ((long)cpu.Si << 16) | cpu.Di;
        using (var stream = _cd!.Open())
        {
            machine.WriteBytes(cpu.Es, cpu.Bx, IsoImage.ReadSectors(stream, sector, cpu.Dx));
        }

        machine.SetReturnCarry(false);
    }
}
