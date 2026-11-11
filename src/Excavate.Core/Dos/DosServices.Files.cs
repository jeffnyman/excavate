using System.Text;

namespace Excavate.Core.Dos;

// Files: handles, reading, writing, seeking, directories and searches.
internal sealed partial class DosServices
{
    // handles 0 to 4 are the standard devices: input, output, error, auxiliary, printer
    private const int FirstFileHandle = 5;

    private readonly Dictionary<int, OpenFile> _files = new()
    {
        [0] = OpenFile.Device("CON"),
        [1] = OpenFile.Device("CON"),
        [2] = OpenFile.Device("CON"),
        [3] = OpenFile.Device("AUX"),
        [4] = OpenFile.Device("PRN"),
    };

    // searches in progress, by the address of the disk transfer area each one fills
    private readonly Dictionary<uint, Queue<Found>> _searches = [];

    /// <summary>A file or directory a search found, on either drive.</summary>
    private sealed class Found
    {
        public string Name { get; init; } = "";

        public bool IsDirectory { get; init; }

        public long Size { get; init; }

        public DateTime Time { get; init; }

        public static Found From(FileSystemInfo entry) => new()
        {
            Name = entry.Name.ToUpperInvariant(),
            IsDirectory = entry is DirectoryInfo,
            Size = entry is FileInfo file ? file.Length : 0,
            Time = entry.LastWriteTime,
        };

        public static Found From(IsoImage.Entry entry) => new()
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : entry.Size,
            Time = entry.Time,
        };
    }

    /// <summary>
    /// An open file or device. A file opened for writing is read from the game's folder until the
    /// first write, which moves it to its copy in the save folder: games open their data files for
    /// writing as a matter of course, and only copying what they really change keeps the save
    /// folder to their saves.
    /// </summary>
    private sealed class OpenFile
    {
        public string Name { get; init; } = "";

        public FileStream? Stream { get; set; }

        public string[]? Parts { get; init; }

        public bool Writable { get; init; }

        public bool Copied { get; set; }

        public bool IsDevice => Stream is null;

        /// <summary>
        /// For a file on the CD, where it starts in the image: its stream is the whole image,
        /// and the file is a window on it.
        /// </summary>
        public long Origin { get; init; }

        /// <summary>For a file on the CD, its length; -1 for a file of its own, as long as its stream.</summary>
        public long Size { get; init; } = -1;

        /// <summary>For a file on the CD, its date, from the disc.</summary>
        public DateTime? Time { get; init; }

        /// <summary>The DOS drive it's on: 2 for C:, 3 for the CD.</summary>
        public int Drive { get; init; } = DriveC;

        /// <summary>The position in the file, the stream's less where the file starts.</summary>
        public long Position
        {
            get => Stream!.Position - Origin;
            set => Stream!.Position = Origin + value;
        }

        public long Length => Size >= 0 ? Size : Stream!.Length;

        public static OpenFile Device(string name) => new() { Name = name };
    }

    /// <summary>
    /// Closes every file the program has open, without telling it: before a saved state is
    /// loaded over the machine, which opens the state's own files again.
    /// </summary>
    public void CloseFiles()
    {
        foreach (var file in _files.Values)
        {
            file.Stream?.Dispose();
        }
    }

    /// <summary>Closes the files a program left open when it ends, as DOS does; the devices stay.</summary>
    private void CloseProgramFiles()
    {
        foreach (int handle in _files.Keys.Where(handle => handle >= FirstFileHandle).ToList())
        {
            _files[handle].Stream?.Dispose();
            _files.Remove(handle);
        }

        _searches.Clear();
    }

    private void PrepareToWrite(OpenFile file)
    {
        if (file.Copied || file.Parts is null || file.Stream is not { } stream)
        {
            return;
        }

        long position = stream.Position;
        stream.Dispose();
        string target = drive.ForWriting(file.Parts, keep: true);
        file.Stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) { Position = position };
        file.Copied = true;
    }

    /// <summary>The INT 21h functions that deal with files; false for any other.</summary>
    private bool FileCall(int function, Registers cpu)
    {
        switch (function)
        {
            case 0x39: MakeDirectory(cpu); return true;
            case 0x3A: RemoveDirectory(cpu); return true;
            case 0x3B: ChangeDirectory(cpu); return true;
            case 0x3C: Create(cpu, replace: true); return true;
            case 0x3D: Open(cpu); return true;
            case 0x3E: Close(cpu); return true;
            case 0x3F: ReadFile(cpu); return true;
            case 0x40: WriteFile(cpu); return true;
            case 0x41: Delete(cpu); return true;
            case 0x42: Seek(cpu); return true;
            case 0x43: Attributes(cpu); return true;
            case 0x44: Ioctl(cpu); return true;
            case 0x45: Duplicate(cpu); return true;
            case 0x47: CurrentDirectory(cpu); return true;
            case 0x4E: FindFirst(cpu); return true;
            case 0x4F: FindNext(); return true;
            case 0x56: Rename(cpu); return true;
            case 0x57: FileTime(cpu); return true;
            case 0x5B: Create(cpu, replace: false); return true;
            default: return false;
        }
    }

    private string NameAt(ushort segment, ushort offset) => machine.ReadString(segment, offset);

    private int NewHandle(OpenFile file)
    {
        int handle = FirstFileHandle;
        while (_files.ContainsKey(handle))
        {
            handle++;
        }

        _files[handle] = file;
        return handle;
    }

    private static bool IsDeviceName(string name, out string device)
    {
        device = Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/')[^1]).ToUpperInvariant();
        return device is "CON" or "NUL" or "PRN" or "AUX" or "EMMXXXX0";
    }

    private void Open(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        int mode = cpu.Al & 7;

        if (IsDeviceName(name, out string device))
        {
            // EMMXXXX0 is how some programs find the expanded memory manager: by opening it
            int deviceHandle = NewHandle(OpenFile.Device(device));
            cpu.Ax = (ushort)deviceHandle;
            Ok();
            machine.Trace($"21/3D open device {device}: handle {deviceHandle}");
            return;
        }

        if (Locate(name) is { Cd: true } onCd)
        {
            OpenOnCd(cpu, name, onCd.Parts, mode);
            return;
        }

        string[]? parts = Locate(name)?.Parts;
        string? file = parts is null ? null : drive.FindFile(parts);
        if (parts is null || file is null)
        {
            machine.Trace($"21/3D open {name}: not found");
            Fail(2);
            return;
        }

        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int handle = NewHandle(new OpenFile { Name = DosDrive.Join(parts), Stream = stream, Parts = parts, Writable = mode != 0 });
        cpu.Ax = (ushort)handle;
        Ok();
        machine.Trace($"21/3D open {name} ({(mode == 0 ? "read" : "write")}): handle {handle}, {stream.Length} bytes");
    }

    /// <summary>Opens a file on the CD: for reading only, as a window on the image.</summary>
    private void OpenOnCd(Registers cpu, string name, string[] parts, int mode)
    {
        if (CdFile(parts) is not { } entry)
        {
            machine.Trace($"21/3D open {name}: not found");
            Fail(parts.Length > 0 && CdDirectoryExists(parts[..^1]) ? 2 : 3);
            return;
        }

        if (mode != 0)
        {
            // a CD can't be written, so opening for writing is refused
            machine.Trace($"21/3D open {name} (write): a CD is read-only");
            Fail(5);
            return;
        }

        var file = new OpenFile
        {
            Name = "D:\\" + DosDrive.Join(parts),
            Stream = _cd!.Open(),
            Origin = entry.Sector * IsoImage.SectorSize,
            Size = entry.Size,
            Time = entry.Time,
            Drive = DriveD,
        };
        file.Position = 0;
        int handle = NewHandle(file);
        cpu.Ax = (ushort)handle;
        Ok();
        machine.Trace($"21/3D open {name} (read): handle {handle}, {entry.Size} bytes, on the CD");
    }

    private void Create(Registers cpu, bool replace)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is { Cd: true })
        {
            Fail(5);
            return;
        }

        if (Locate(name)?.Parts is not { Length: > 0 } parts || !drive.DirectoryExists(parts[..^1]))
        {
            Fail(3);
            return;
        }

        if (!replace && drive.FindFile(parts) is not null)
        {
            Fail(0x50);
            return;
        }

        string file = drive.ForWriting(parts, keep: false);
        var stream = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        int handle = NewHandle(new OpenFile { Name = DosDrive.Join(parts), Stream = stream, Parts = parts, Writable = true, Copied = true });
        cpu.Ax = (ushort)handle;
        Ok();
        machine.Trace($"21/{(replace ? "3C" : "5B")} create {name}: handle {handle}");
    }

    private bool TryFile(Registers cpu, out OpenFile file)
    {
        if (_files.TryGetValue(cpu.Bx, out file!))
        {
            return true;
        }

        Fail(6);
        return false;
    }

    private void Close(Registers cpu)
    {
        if (TryFile(cpu, out var file))
        {
            _files.Remove(cpu.Bx);
            if (!_files.ContainsValue(file))
            {
                // the last handle to it, duplicates included
                file.Stream?.Dispose();
            }

            Ok();
            machine.Trace($"21/3E close {cpu.Bx} ({file.Name})");
        }
    }

    private void ReadFile(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        int count = 0;
        if (file.Stream is { } stream)
        {
            // a file on the CD ends where its window does
            var buffer = new byte[(int)Math.Clamp(file.Length - file.Position, 0, cpu.Cx)];
            count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            uint address = ((uint)cpu.Ds << 4) + cpu.Dx;
            for (int i = 0; i < count; i++)
            {
                machine.Write(address + (uint)i, buffer[i]);
            }
        }

        machine.Trace($"21/3F read {cpu.Cx} bytes from {cpu.Bx} ({file.Name}) to {cpu.Ds:X4}:{cpu.Dx:X4}: {count}");
        cpu.Ax = (ushort)count;
        Ok();
    }

    private void WriteFile(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        var buffer = new byte[cpu.Cx];
        uint address = ((uint)cpu.Ds << 4) + cpu.Dx;
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = machine.Read(address + (uint)i);
        }

        if (file.Stream is not null && !file.Writable)
        {
            Fail(5);
            return;
        }

        PrepareToWrite(file);
        if (file.Stream is { } stream)
        {
            if (buffer.Length == 0)
            {
                // a write of nothing truncates the file at the current position
                stream.SetLength(stream.Position);
            }

            stream.Write(buffer);
            machine.Trace($"21/40 write {buffer.Length} bytes to {cpu.Bx} ({file.Name})");
        }
        else if (file.Name == "CON")
        {
            foreach (byte b in buffer)
            {
                ConsoleOut((char)b);
            }
        }

        cpu.Ax = (ushort)buffer.Length;
        Ok();
    }

    private void Seek(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        long position = 0;
        if (file.Stream is { } stream)
        {
            int offset = (cpu.Cx << 16) | cpu.Dx;
            long target = cpu.Al switch
            {
                0 => (uint)offset,
                1 => file.Position + offset,
                _ => file.Length + offset,
            };

            if (target < 0)
            {
                Fail(0x19);
                return;
            }

            position = file.Position = target;
        }

        cpu.Dx = (ushort)(position >> 16);
        cpu.Ax = (ushort)position;
        Ok();
        machine.Trace($"21/42 seek {cpu.Bx} ({file.Name}) to {position}");
    }

    private void Delete(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is { Cd: false } location && drive.Delete(location.Parts))
        {
            Ok();
            machine.Trace($"21/41 delete {name}");
        }
        else
        {
            Fail(Locate(name) is { Cd: true } ? 5 : 2);
        }
    }

    private void MakeDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        var location = Locate(name);
        bool made = location is { Cd: false } && drive.MakeDirectory(location.Value.Parts);
        if (made)
        {
            Ok();
        }
        else
        {
            // access denied: DOS's answer when it's there already, as well as when it can't be
            // made, and on a CD
            Fail(location is { Cd: true } || location is { Parts: { Length: > 0 } p } && drive.DirectoryExists(p[..^1]) ? 5 : 3);
        }

        machine.Trace($"21/39 make directory {name}: {(made ? "made" : "failed")}");
    }

    private void RemoveDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        bool removed = Locate(name) is { Cd: false } location && drive.RemoveDirectory(location.Parts);
        if (removed)
        {
            Ok();
        }
        else
        {
            Fail(5);
        }

        machine.Trace($"21/3A remove directory {name}: {(removed ? "removed" : "failed")}");
    }

    private void Rename(Registers cpu)
    {
        string from = NameAt(cpu.Ds, cpu.Dx), to = NameAt(cpu.Es, cpu.Di);
        if (Locate(from) is { Cd: true } || Locate(to) is { Cd: true })
        {
            Fail(5);
            return;
        }

        if (Locate(from)?.Parts is not { } source || drive.FindFile(source) is not { } file || Locate(to)?.Parts is not { } target)
        {
            Fail(2);
            return;
        }

        byte[] contents = File.ReadAllBytes(file);
        drive.Delete(source);
        File.WriteAllBytes(drive.ForWriting(target, keep: false), contents);
        Ok();
        machine.Trace($"21/56 rename {from} to {to}");
    }

    private void Attributes(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is { Cd: true } onCd)
        {
            // a CD's files are read-only, and their attributes can't be changed
            var entry = _cd!.Find(onCd.Parts);
            if (entry is null || cpu.Al != 0)
            {
                Fail(entry is null ? 2 : 5);
                return;
            }

            cpu.Cx = (ushort)(entry.IsDirectory ? 0x11 : 0x01);
            Ok();
            return;
        }

        var parts = Locate(name)?.Parts;
        if (parts is not null && drive.FindFile(parts) is not null)
        {
            if (cpu.Al == 0)
            {
                cpu.Cx = 0x20;
            }

            Ok();
        }
        else if (parts is not null && drive.DirectoryExists(parts))
        {
            cpu.Cx = 0x10;
            Ok();
        }
        else
        {
            Fail(2);
        }
    }

    /// <summary>INT 21h AH=44h, I/O control: mostly telling devices from files.</summary>
    private void Ioctl(Registers cpu)
    {
        switch (cpu.Al)
        {
            case 0x00:
                if (TryFile(cpu, out var file))
                {
                    // device information: bit 7 for a device (with 80h-83h for the console's
                    // roles), and for a file the drive number, C: being 2
                    cpu.Dx = file.IsDevice ? (ushort)(file.Name == "CON" ? 0x80D3 : 0x80C0) : (ushort)file.Drive;
                    Ok();
                }

                break;
            case 0x01: Ok(); break;
            case 0x06:
            case 0x07:
                if (TryFile(cpu, out _))
                {
                    cpu.Al = 0xFF;
                    Ok();
                }

                break;
            case 0x08:
                // whether a drive (BL: 0 for the current one, 1 for A:) is removable. C: isn't.
                // DOS leaves a CD to MSCDEX, and answers that the function isn't valid for it.
                if (!DriveExists(DriveIn(cpu.Bl)))
                {
                    Fail(0x0F);
                }
                else if (DriveIn(cpu.Bl) == DriveD)
                {
                    Fail(0x01);
                }
                else
                {
                    cpu.Ax = 1;
                    Ok();
                }

                break;
            case 0x09:
                // whether a drive is remote: a CD under MSCDEX is, as a network drive is
                if (!DriveExists(DriveIn(cpu.Bl)))
                {
                    Fail(0x0F);
                    break;
                }

                cpu.Dx = (ushort)(DriveIn(cpu.Bl) == DriveD ? 0x1000 : 0);
                Ok();
                break;
            case 0x0E:
                cpu.Al = 0; // the drive has only the one letter
                Ok();
                break;
            case 0x0F:
                Ok();
                break;
            default:
                throw new NotSupportedException($"INT 21h function 44h subfunction {cpu.Al:X2}h isn't provided.");
        }
    }

    private void Duplicate(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        // the duplicate shares the original's position, as the same open file
        cpu.Ax = (ushort)NewHandle(file);
        Ok();
    }

    private void ChangeDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        var location = Locate(name);
        if (location is { Cd: true } && CdDirectoryExists(location.Value.Parts))
        {
            _cdDirectory = DosDrive.Join(location.Value.Parts);
            Ok();
        }
        else if (location is { Cd: false } && drive.ChangeDirectory(location.Value.Parts))
        {
            Ok();
        }
        else
        {
            Fail(3);
        }

        machine.Trace($"21/3B change directory to {name}");
    }

    private void CurrentDirectory(Registers cpu)
    {
        int number = DriveIn(cpu.Dl);
        if (!DriveExists(number))
        {
            Fail(0x0F);
            return;
        }

        string directory = number == DriveD ? _cdDirectory : drive.CurrentDirectory;
        machine.WriteBytes(cpu.Ds, cpu.Si, [.. Encoding.ASCII.GetBytes(directory), 0]);
        Ok();
    }

    private void FileTime(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        if (cpu.Al == 0)
        {
            var time = file.Time ?? (file.Stream is { } stream ? File.GetLastWriteTime(stream.Name) : DateTime.Now);
            (cpu.Cx, cpu.Dx) = DosTime(time);
        }

        Ok();
    }

    private static (ushort Time, ushort Date) DosTime(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)),
         (ushort)(((Math.Max(t.Year, 1980) - 1980) << 9) | (t.Month << 5) | t.Day));

    // ---- searches ----------------------------------------------------------------------------

    private uint Dta => ((uint)_dtaSegment << 4) + _dtaOffset;

    /// <summary>
    /// INT 21h AH=4Eh: the first file matching a pattern with wildcards, into the disk transfer
    /// area. The rest of the matches wait for AH=4Fh, keyed by that area.
    /// </summary>
    private void FindFirst(Registers cpu)
    {
        string pattern = NameAt(cpu.Ds, cpu.Dx);
        int attributes = cpu.Cx;
        var location = Locate(pattern);
        if (location is not { Parts: { Length: > 0 } parts }
            || (location.Value.Cd ? !CdDirectoryExists(parts[..^1]) : !drive.DirectoryExists(parts[..^1])))
        {
            Fail(3);
            return;
        }

        var entries = location.Value.Cd
            ? _cd!.List(_cd.Find(parts[..^1])!).Select(Found.From)
            : drive.List(parts[..^1]).Select(Found.From);
        var matches = new Queue<Found>(entries
            .Where(entry => Matches(entry.Name, parts[^1]))
            .Where(entry => !entry.IsDirectory || (attributes & 0x10) != 0)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal));
        _searches[Dta] = matches;
        machine.Trace($"21/4E find {pattern}: {matches.Count} matches");

        if (!NextMatch())
        {
            Fail(2);
        }
    }

    private void FindNext()
    {
        if (!NextMatch())
        {
            Fail(0x12);
        }
    }

    private bool NextMatch()
    {
        if (!_searches.TryGetValue(Dta, out var matches) || !matches.TryDequeue(out var entry))
        {
            return false;
        }

        // the search's record: 21 bytes DOS keeps for itself, then the attribute, time, date,
        // size and name of the file found
        var (time, date) = DosTime(entry.Time);
        uint size = (uint)Math.Min(entry.Size, uint.MaxValue);
        machine.WriteBytes(_dtaSegment, _dtaOffset, new byte[43]);
        machine.WriteByte(_dtaSegment, _dtaOffset + 0x15u, (byte)(entry.IsDirectory ? 0x10 : 0x20));
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x16u, time);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x18u, date);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x1Au, (ushort)size);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x1Cu, (ushort)(size >> 16));
        machine.WriteBytes(_dtaSegment, _dtaOffset + 0x1Eu, [.. Encoding.ASCII.GetBytes(entry.Name), 0]);
        Ok();
        return true;
    }

    /// <summary>
    /// Whether a name matches a DOS wildcard pattern, the name part and extension apart: "?"
    /// matches any one character, "*" the rest of its part.
    /// </summary>
    internal static bool Matches(string name, string pattern)
    {
        static (string Name, string Extension) Split(string s)
        {
            int dot = s.LastIndexOf('.');
            return dot < 0 ? (s, "") : (s[..dot], s[(dot + 1)..]);
        }

        static bool Part(string text, string pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '*')
                {
                    return true;
                }

                if (i >= text.Length)
                {
                    return pattern[i..].All(c => c is '?' or '*');
                }

                if (pattern[i] != '?' && pattern[i] != text[i])
                {
                    return false;
                }
            }

            return text.Length == pattern.Length;
        }

        var (n, e) = Split(name);
        var (pn, pe) = Split(pattern);
        if (pattern == "*.*" || pattern == "*")
        {
            return true;
        }

        return Part(n, pn) && Part(e, pe);
    }
}
