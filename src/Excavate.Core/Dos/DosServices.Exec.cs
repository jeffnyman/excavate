using System.Text;

namespace Excavate.Core.Dos;

// Programs that run programs: function 4Bh, and the return to the program that called it.
internal sealed partial class DosServices
{
    /// <summary>
    /// A program that has run another with function 4Bh and waits for it to end: what DOS
    /// gives back to it then. Its registers are kept whole, as they were at its <c>INT 21h</c>,
    /// so it carries on from the stub's <c>IRET</c> as if the call had just returned.
    /// </summary>
    private sealed class Parent
    {
        public ushort Psp { get; init; }

        public ushort DtaSegment { get; init; }

        public ushort DtaOffset { get; init; }

        /// <summary>
        /// Its handles, which its child inherits. They're its own: what the child closes stays
        /// open for it, and what the child opens is closed when the child ends.
        /// </summary>
        public Dictionary<int, OpenFile> Files { get; init; } = [];

        public uint[] Registers { get; init; } = [];

        public ushort[] Segments { get; init; } = [];

        public uint Eip { get; init; }

        public uint Eflags { get; init; }
    }

    // the programs waiting for the one running to end, the innermost last
    private readonly List<Parent> _parents = [];

    /// <summary>
    /// Function 4Bh with AL 0: loads a program and runs it, from DS:DX's path and ES:BX's
    /// parameter block (its environment's segment, or 0 for one like the caller's; a far pointer
    /// to its command tail; and two to file control blocks for its PSP). The caller stays in
    /// memory and waits, and carries on when its child ends, as if the call had just returned.
    /// </summary>
    /// <remarks>
    /// Loading a program for the caller to start itself (AL 1) and loading an overlay (AL 3)
    /// aren't done.
    /// </remarks>
    private void RunProgram(Registers cpu)
    {
        if (cpu.Al != 0x00)
        {
            throw new NotSupportedException($"INT 21h function 4Bh (AL={cpu.Al:X2}h) isn't provided.");
        }

        string path = NameAt(cpu.Ds, cpu.Dx);
        ushort block = cpu.Bx;
        ushort environment = machine.ReadWord(cpu.Es, block);
        string tail = CommandTail(machine.ReadWord(cpu.Es, (ushort)(block + 4)), machine.ReadWord(cpu.Es, (ushort)(block + 2)));

        // the file control blocks, as the caller parsed them for the PSP: their drives and names
        byte[][] fcbs = [.. Enumerable.Range(0, 2).Select(i => Bytes(
            machine.ReadWord(cpu.Es, (ushort)(block + 8 + (4 * i))), machine.ReadWord(cpu.Es, (ushort)(block + 6 + (4 * i))), 12))];

        if (Locate(path) is not { Cd: false } location || drive.FindFile(location.Parts) is not { } file)
        {
            machine.Trace($"21/4B run {path}: not found");
            Fail(2);
            return;
        }

        byte[] image = File.ReadAllBytes(file);
        if (image.Length < 0x1C || image[0] != 'M' || image[1] != 'Z')
        {
            throw new NotSupportedException($"{path} isn't an MZ executable; .COM programs aren't run yet.");
        }

        var core = machine.Cpu;
        var parent = new Parent
        {
            Psp = _psp,
            DtaSegment = _dtaSegment,
            DtaOffset = _dtaOffset,
            Files = new Dictionary<int, OpenFile>(_files),
            Registers = [core.Eax, core.Ecx, core.Edx, core.Ebx, core.Esp, core.Ebp, core.Esi, core.Edi],
            Segments = [core.Es, core.Cs, core.Ss, core.Ds, core.Fs, core.Gs],
            Eip = core.Eip,
            Eflags = core.Eflags,
        };

        ushort environmentSegment = BuildEnvironment(EnvironmentOf(environment), location.Name);
        if (!TryLoadExe(image, location.Parts[^1], environmentSegment, tail, parent.Psp))
        {
            Free(environmentSegment);
            Fail(8);
            return;
        }

        machine.WriteBytes(_psp, 0x5C, fcbs[0]);
        machine.WriteBytes(_psp, 0x6C, fcbs[1]);

        _parents.Add(parent);
        machine.Trace($"21/4B run {location.Name} at {_psp:X4}h,{tail}");
    }

    /// <summary>Whether a handle is open on a file: the running program's, or a waiting parent's.</summary>
    private bool IsOpen(OpenFile file) => _files.ContainsValue(file) || _parents.Any(parent => parent.Files.ContainsValue(file));

    /// <summary>A command tail, as a program gives one: a count, then that many characters.</summary>
    private string CommandTail(ushort segment, ushort offset)
    {
        int length = Math.Min((int)machine.ReadByte(segment, offset), 126);
        return Encoding.Latin1.GetString(Bytes(segment, (uint)(offset + 1), length));
    }

    /// <summary>The variables in an environment block, or DOS's own for segment 0.</summary>
    private IEnumerable<string> EnvironmentOf(ushort segment)
    {
        if (segment == 0)
        {
            segment = machine.ReadWord(_psp, 0x2C);
        }

        var variables = new List<string>();
        uint offset = 0;
        while (offset < 0x8000 && machine.ReadByte(segment, offset) != 0)
        {
            string variable = machine.ReadString(segment, offset, 1024);
            variables.Add(variable);
            offset += (uint)variable.Length + 1;
        }

        return variables;
    }

    /// <summary>
    /// The program running has ended, and another waits for it: its memory is freed, the files
    /// it opened closed, and the vectors it was given put back from its PSP; then its parent
    /// carries on from its call, carry clear, with the exit code for function 4Dh to give.
    /// </summary>
    private void ReturnToParent()
    {
        var parent = _parents[^1];
        _parents.RemoveAt(_parents.Count - 1);
        ushort child = _psp;

        var files = _files.Values.Distinct().ToList();
        _files.Clear();
        foreach (var (handle, file) in parent.Files)
        {
            _files[handle] = file;
        }

        foreach (var file in files.Where(file => !IsOpen(file)))
        {
            file.Stream?.Dispose();
        }

        for (uint i = 0; i < 3; i++)
        {
            machine.WriteWord(0, (0x22 + i) * 4, machine.ReadWord(child, 0x0A + (4 * i)));
            machine.WriteWord(0, ((0x22 + i) * 4) + 2, machine.ReadWord(child, 0x0C + (4 * i)));
        }

        foreach (var block in Blocks().Where(b => b.Owner == child).ToList())
        {
            machine.WriteWord(block.Mcb, 1, 0);
        }

        Coalesce();

        _psp = parent.Psp;
        _dtaSegment = parent.DtaSegment;
        _dtaOffset = parent.DtaOffset;

        var cpu = machine.Cpu;
        (cpu.Eax, cpu.Ecx, cpu.Edx, cpu.Ebx) = (parent.Registers[0], parent.Registers[1], parent.Registers[2], parent.Registers[3]);
        (cpu.Esp, cpu.Ebp, cpu.Esi, cpu.Edi) = (parent.Registers[4], parent.Registers[5], parent.Registers[6], parent.Registers[7]);
        cpu.Es = parent.Segments[0];
        cpu.Cs = parent.Segments[1];
        cpu.Ss = parent.Segments[2];
        cpu.Ds = parent.Segments[3];
        cpu.Fs = parent.Segments[4];
        cpu.Gs = parent.Segments[5];
        cpu.Eip = parent.Eip;
        cpu.Eflags = parent.Eflags;
        Ok();
        machine.Trace($"back to {_psp:X4}h");
    }

    private byte[] Bytes(ushort segment, uint offset, int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = machine.ReadByte(segment, (uint)(offset + i));
        }

        return bytes;
    }
}
