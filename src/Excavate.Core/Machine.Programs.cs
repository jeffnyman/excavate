namespace Excavate.Core;

// Bytes a crack changed in a game's program, put back each time DOS loads it.
public sealed partial class Machine
{
    // what to put back, by program; the player gives it before the game starts, so it isn't saved
    [NotSaved]
    private readonly List<(string Program, int Offset, byte[] Cracked, byte[] Original)> _restorations = [];

    /// <summary>
    /// Puts back bytes a crack changed in one of a game's programs, in memory, each time DOS
    /// loads it, so the program runs as it shipped. The file is left as it is. Where its bytes
    /// aren't the crack's (a copy that was never cracked, or another version), nothing changes.
    /// </summary>
    /// <param name="program">The program's file name, as in <c>SU.EXE</c>.</param>
    /// <param name="offset">Where the bytes are in its file.</param>
    /// <param name="cracked">The bytes the crack wrote there.</param>
    /// <param name="original">The bytes the program shipped with.</param>
    public void RestoreProgramBytes(string program, int offset, byte[] cracked, byte[] original)
    {
        ArgumentNullException.ThrowIfNull(cracked);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentOutOfRangeException.ThrowIfNotEqual(original.Length, cracked.Length);
        _restorations.Add((program, offset, [.. cracked], [.. original]));
    }

    /// <summary>
    /// A program's image has been loaded at a segment, from its file past a header of some
    /// bytes: what a crack changed in it is put back.
    /// </summary>
    internal void ProgramLoaded(string program, ushort segment, int header, int size)
    {
        foreach (var (name, offset, cracked, original) in _restorations)
        {
            int at = offset - header;
            if (!string.Equals(name, program, StringComparison.OrdinalIgnoreCase) || at < 0 || at + cracked.Length > size)
            {
                continue;
            }

            bool isCracked = true;
            for (int i = 0; i < cracked.Length; i++)
            {
                isCracked &= Read(((uint)segment << 4) + (uint)(at + i)) == cracked[i];
            }

            if (isCracked)
            {
                WriteBytes(segment, (uint)at, original);
                Trace($"{program}: {cracked.Length} bytes at {offset:X}h put back as they shipped");
            }
        }
    }
}
