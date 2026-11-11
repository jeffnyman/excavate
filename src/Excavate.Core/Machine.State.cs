namespace Excavate.Core;

// Saved states: the whole machine, written out and read back.
public sealed partial class Machine
{
    /// <summary>
    /// Saves the whole machine as it is between instructions: the processor, memory (expanded
    /// memory included), every device, DOS's open files and searches, and the MT-32's synthesizer
    /// down to its notes in flight. Not saved: the files themselves, in the game's folder and the
    /// save folder, which stay where they are; and the MT-32's ROMs, which the machine a state is
    /// loaded into has its own of.
    /// </summary>
    public void SaveState(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MachineState.Save(this, Fixed, _mt32?.Description ?? "", stream);
    }

    /// <summary>
    /// Puts the machine back as <see cref="SaveState"/> saved it. The machine must be made the same
    /// way: for the same game, and with an MT-32 made from the same ROMs if the saved one had one.
    /// The files the program had open are opened again, where they were; one that's gone since
    /// fails the load part way, and leaves the machine unfit to carry on.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The stream is not a saved state, is one saved by a different version, is from a machine
    /// with a different MT-32 (or one with where this has none, or the other way round), or names
    /// a file that can't be opened again.
    /// </exception>
    public void LoadState(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MachineState.Load(this, Fixed, CheckState, stream);

        // the map of plain memory follows the A20 gate the state had
        MapMemory();
    }

    // what a state refers to rather than holds: the MT-32's ROMs
    private object[] Fixed => _mt32?.Fixed ?? [];

    // a state's header is the MT-32 it was saved with; a machine with another can't take it
    private void CheckState(string mt32)
    {
        string mine = _mt32?.Description ?? "";
        if (mt32 != mine)
        {
            throw new InvalidDataException(
                mt32.Length == 0 ? $"This state was saved with no MT-32, and this machine has a {mine}."
                : mine.Length == 0 ? $"This state was saved with a {mt32} for music, and this machine has no MT-32."
                : $"This state was saved with a {mt32}, and this machine's MT-32 is a {mine}.");
        }

        // the state's open files take the place of the machine's
        _dos.CloseFiles();
    }
}
