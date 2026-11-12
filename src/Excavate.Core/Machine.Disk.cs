using Excavate.Core.Disks;
using Excavate.Core.Processors;

namespace Excavate.Core;

// The floppy drive: drive A:, the disks that go in it, and INT 13h, the BIOS's disk service.
public sealed partial class Machine
{
    // INT 13h's status codes
    private const byte DiskOk = 0x00, BadCommand = 0x01, SectorNotFound = 0x04, WriteProtected = 0x03,
        DiskChanged = 0x06, NotReady = 0x80;

    /// <summary>
    /// The diskette parameter table, which INT 1Eh points at, where IBM's BIOS has it: the
    /// floppy controller's step rate and head times, the sector size (2, 512 bytes), the last
    /// sector of a track, the gap lengths, the format's filler (F6h), and the head settle and
    /// motor start times. Programs that format their own tracks read it.
    /// </summary>
    private const ushort DisketteTable = 0xEFC7;

    // the disks by the drive, as the player gave them; which is in it (-1 for none); and whether
    // that changed since a program last asked
    private readonly List<FloppyDisk> _floppies = [];
    private int _floppy = -1;
    private bool _floppyChanged;

    // what INT 13h function 01h reports: the last operation's status
    private byte _diskStatus;

    [NotSaved]
    private readonly string _saveFolder;

    /// <summary>
    /// Puts a set of floppy disks by drive A:, from their images, with the first in the drive.
    /// What a program writes to a disk goes to a copy of it in the save folder, named as its
    /// image is, which is read in place of the image from then on; the images are never written.
    /// </summary>
    /// <param name="images">The disks' images, in the order they're numbered: disk 1 first.</param>
    /// <exception cref="InvalidDataException">An image isn't the size of any PC floppy disk.</exception>
    public void InsertFloppies(IReadOnlyList<string> images)
    {
        _floppies.Clear();
        foreach (string image in images)
        {
            _floppies.Add(new FloppyDisk(image, Path.Combine(_saveFolder, Path.GetFileName(image))));
        }

        Floppy = _floppies.Count > 0 ? 0 : -1;
    }

    /// <summary>How many disks are by the drive.</summary>
    public int Floppies => _floppies.Count;

    /// <summary>Which disk is in drive A:, counting from 0 in the order they were given; -1 when it's empty.</summary>
    public int Floppy
    {
        get => _floppy;
        set
        {
            int disk = value >= 0 && value < _floppies.Count ? value : -1;
            if (disk != _floppy)
            {
                _floppy = disk;
                _floppyChanged = true;
                Trace(disk < 0 ? "drive A: emptied" : $"drive A: disk {disk + 1}, {_floppies[disk].Name}");
            }
        }
    }

    /// <summary>The image of the disk in drive A:, for messages; null when it's empty.</summary>
    public string? FloppyName => _floppy < 0 ? null : _floppies[_floppy].Name;

    /// <summary>
    /// Puts back sectors that a track of one of the disks had beyond its standard ones, and that
    /// an image, a dump of only the standard sectors, lost: copy protection on the original disk
    /// that a game looks for. They're found, and read as the formatting's filler.
    /// </summary>
    /// <param name="disk">The disk, counting from 0 in the order they were given.</param>
    /// <param name="cylinder">The track's cylinder.</param>
    /// <param name="head">The track's side.</param>
    /// <param name="first">The first sector number to put back.</param>
    /// <param name="last">The last.</param>
    public void RestoreSectors(int disk, int cylinder, int head, int first, int last)
        => _floppies[disk].Restore(cylinder, head, first, last);

    /// <summary>
    /// Answers a game's prompt for another disk as the player would: when the program waits for
    /// a key there, the disk goes in drive A: and a key is pressed. The program runs unchanged;
    /// the prompt is only answered for it.
    /// </summary>
    /// <param name="keyWait">
    /// Where the program asks the BIOS for a key: the offset, in the program's image, of the
    /// instruction after its <c>INT 16h</c>.
    /// </param>
    /// <param name="caller">
    /// The offset in the image that the routine with that <c>INT 16h</c> returns to when it's the
    /// prompt that called it: the instruction after the prompt's near call, its return address
    /// being the first word on the stack below the interrupt's.
    /// </param>
    /// <param name="disk">The disk the prompt asks for, counting from 0.</param>
    public void AnswerDiskPrompt(int keyWait, int caller, int disk)
        => _diskPrompts.Add(new DiskPrompt(keyWait, caller, disk, -1, ""));

    /// <summary>Answers a prompt for a disk whose number the program keeps in memory, as the digit it shows.</summary>
    /// <param name="keyWait"><inheritdoc cref="AnswerDiskPrompt(int, int, int)" path="/param[@name='keyWait']"/></param>
    /// <param name="caller"><inheritdoc cref="AnswerDiskPrompt(int, int, int)" path="/param[@name='caller']"/></param>
    /// <param name="digitAt">The offset in the image of the byte holding the disk's digit.</param>
    /// <param name="digits">What that byte holds for each disk, disk 1's first: "123" for the plain digits.</param>
    public void AnswerDiskPrompt(int keyWait, int caller, int digitAt, string digits)
        => _diskPrompts.Add(new DiskPrompt(keyWait, caller, -1, digitAt, digits));

    /// <summary>The segment the running program's image was loaded at, just past its PSP.</summary>
    internal ushort ProgramSegment => (ushort)(_dos.Psp + 0x10);

    // a prompt for another disk: where it waits for a key, and which disk it wants, as a number
    // or a digit in memory; offsets are in the program's image
    private readonly record struct DiskPrompt(int KeyWait, int Caller, int Disk, int DigitAt, string Digits);

    [NotSaved]
    private readonly List<DiskPrompt> _diskPrompts = [];

    /// <summary>
    /// When the program waits for a key at a prompt for another disk, puts the disk it asks for
    /// in the drive and a key in the BIOS's buffer: Space. Called from INT 16h with its frame on
    /// the stack, when no key is waiting.
    /// </summary>
    private void AnswerDiskPrompt()
    {
        if (_diskPrompts.Count == 0 || (Cpu.Cr0 & 1) != 0)
        {
            return;
        }

        uint image = (uint)ProgramSegment << 4;
        ushort sp = (ushort)Cpu.Esp;
        uint returns = ((uint)ReadWord(Cpu.Ss, (ushort)(sp + 2)) << 4) + ReadWord(Cpu.Ss, sp);
        uint called = ((uint)ReadWord(Cpu.Ss, (ushort)(sp + 2)) << 4) + ReadWord(Cpu.Ss, (ushort)(sp + 6));
        foreach (var prompt in _diskPrompts)
        {
            if (returns != image + (uint)prompt.KeyWait || called != image + (uint)prompt.Caller)
            {
                continue;
            }

            int disk = prompt.DigitAt < 0 ? prompt.Disk : prompt.Digits.IndexOf((char)Read(image + (uint)prompt.DigitAt));
            if (disk < 0 || disk >= _floppies.Count)
            {
                // a disk the player doesn't have: the prompt is theirs to answer
                Trace($"disk prompt for a disk not given: {disk + 1}");
                return;
            }

            Trace($"disk prompt: disk {disk + 1} put in drive A:, and Space pressed");
            Floppy = disk;
            AddToBuffer(0x3920);
            return;
        }
    }

    /// <summary>The disk in drive A:, or null.</summary>
    internal FloppyDisk? DiskInDrive => _floppy < 0 ? null : _floppies[_floppy];

    private void InstallDisks()
    {
        const uint bios = (uint)BiosSegment << 4;
        WriteRom(bios + DisketteTable, [0xDF, 0x02, 0x25, 0x02, 0x09, 0x2A, 0xFF, 0x50, 0xF6, 0x0F, 0x02]);
        SetVector(0x1E, BiosSegment, DisketteTable);
    }

    /// <summary>
    /// INT 13h, the BIOS's disk service, for drive A: (DL 0). There's no drive B:, and no hard
    /// disk the BIOS knows of: drive C: is DOS's own, answered without a disk under it.
    /// </summary>
    /// <remarks>
    /// Sectors are addressed as the floppy controller finds them: cylinder (CH), head (DH) and
    /// sector number (CL, from 1). A multi-sector transfer that runs off the end of a track
    /// carries on with the other side's, as the BIOS's controller command does, but not onto the
    /// next cylinder. The status of each call is left in AH, and in the BIOS data area, with
    /// carry set for an error.
    /// </remarks>
    private void DiskService()
    {
        int drive = (byte)Cpu.Edx;
        byte function = Ah;

        // the functions that don't touch a disk
        switch (function)
        {
            case 0x00:
                // reset the controller, which needs no disk in the drive
                DiskResult(drive == 0 ? DiskOk : NotReady, drive);
                return;
            case 0x01:
                // the last operation's status, in AH (and AL, as some BIOSes have it)
                SetAx((_diskStatus << 8) | _diskStatus);
                SetReturnCarry(_diskStatus != DiskOk);
                return;
            case 0x08:
                DriveParameters(drive);
                return;
            case 0x15:
                // the drive's type: 1, a floppy drive that can't tell a disk was changed, as
                // on the PC; none for the others
                SetAh(drive == 0 ? 0x01 : 0x00);
                SetReturnCarry(false);
                return;
            case 0x16:
            {
                // whether the disk was changed since the last call
                bool changed = _floppyChanged;
                _floppyChanged = false;
                DiskResult(drive != 0 ? NotReady : _floppy < 0 ? NotReady : changed ? DiskChanged : DiskOk, drive);
                return;
            }

            case 0x17:
            case 0x18:
                // the disk's type, for formatting: taken as given
                DiskResult(drive == 0 ? DiskOk : NotReady, drive);
                return;
        }

        if (function is not (0x02 or 0x03 or 0x04))
        {
            Trace($"13/{function:X2} drive {drive:X2}h: not provided");
            DiskResult(BadCommand, drive);
            return;
        }

        int count = Al;
        int cylinder = (byte)(Cpu.Ecx >> 8) | ((int)(Cpu.Ecx & 0xC0) << 2);
        int sector = (int)(Cpu.Ecx & 0x3F);
        int head = (byte)(Cpu.Edx >> 8);
        string what = function switch { 0x02 => "read", 0x03 => "write", _ => "verify" };
        if (drive != 0 || DiskInDrive is not { } disk)
        {
            Trace($"13/{function:X2} {what} drive {drive:X2}h: no disk");
            SetAl(0);
            DiskResult(NotReady, drive);
            return;
        }

        ushort segment = Cpu.Es;
        uint offset = (ushort)Cpu.Ebx;
        var buffer = new byte[FloppyDisk.SectorSize];
        int done = 0;
        byte status = DiskOk;
        for (; done < count; done++)
        {
            if (!disk.Has(cylinder, head, sector))
            {
                status = SectorNotFound;
                break;
            }

            switch (function)
            {
                case 0x02:
                    disk.Read(cylinder, head, sector, buffer);
                    WriteBytes(segment, offset, buffer);
                    break;
                case 0x03:
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        buffer[i] = Read(((uint)segment << 4) + offset + (uint)i);
                    }

                    disk.Write(cylinder, head, sector, buffer);
                    break;
            }

            offset += FloppyDisk.SectorSize;

            // on to the next sector, and past the track's last, the other side's first
            if (++sector > disk.SectorsPerTrack && !disk.Has(cylinder, head, sector))
            {
                sector = 1;
                head++;
            }
        }

        Trace($"13/{function:X2} {what} {count} at {cylinder}/{(byte)(Cpu.Edx >> 8)}/{Cpu.Ecx & 0x3F}, disk {_floppy + 1}: {done} done, status {status:X2}h");
        SetAl(done);
        DiskResult(status, drive);
    }

    /// <summary>
    /// INT 13h function 08h, the drive's parameters: its type in BL, the last cylinder, sector
    /// and head in CX and DH, the number of floppy drives in DL, and the parameter table in ES:DI.
    /// </summary>
    private void DriveParameters(int drive)
    {
        if (drive != 0)
        {
            DiskResult(drive >= 0x80 ? BadCommand : NotReady, drive);
            return;
        }

        // the drive is the kind the first disk is for; a 360K drive with none
        var disk = _floppies.FirstOrDefault();
        int cylinders = disk?.Cylinders ?? 40, sectors = disk?.SectorsPerTrack ?? 9;
        SetAx(0);
        SetBx(disk?.DriveType ?? 1);
        SetCx(((cylinders - 1) << 8) | sectors);
        SetDx((((disk?.Heads ?? 2) - 1) << 8) | 1);
        Cpu.Es = BiosSegment;
        Cpu.Edi = (Cpu.Edi & 0xFFFF_0000) | DisketteTable;
        DiskResult(DiskOk, drive, keep: true);
    }

    /// <summary>Ends an INT 13h call: the status in AH and the data area, and carry set for an error.</summary>
    private void DiskResult(byte status, int drive, bool keep = false)
    {
        if (drive < 0x80)
        {
            _diskStatus = status;
            WriteByte(0x40, 0x41, status);
        }

        if (!keep)
        {
            SetAh(status);
        }

        SetReturnFlag(Intel386.Flag.Carry, status != DiskOk);
    }
}
