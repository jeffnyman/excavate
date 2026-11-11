namespace Excavate.Core.Sound;

/// <summary>Whatever is on the other end of the MIDI cable: it takes whole messages.</summary>
internal interface IMidiOut
{
    /// <summary>
    /// A short message: the status byte in bits 0-7, the first data byte in 8-15 and the
    /// second in 16-23, as Windows and Munt pack them.
    /// </summary>
    void Message(uint message);

    /// <summary>A system exclusive message, from its F0 to its F7.</summary>
    void SysEx(ReadOnlySpan<byte> message);
}
