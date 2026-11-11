namespace Excavate.Core.Sound;

/// <summary>
/// Roland's MPU-401 MIDI interface, in the "UART" mode that games use: a data port (330h) whose
/// bytes go straight out on the MIDI cable, and a command and status port (331h). Written from
/// DOSBox's <c>mpu401.cpp</c>, without the "intelligent" mode's sequencer.
/// </summary>
/// <remarks>
/// <para>
/// The MPU-401 (1984) was a small computer of its own: in its intelligent mode it kept time and
/// played up to eight tracks for the program. Most games skipped all that. They reset the card
/// (command FFh, answered by FEh on the data port), switch it to UART mode (3Fh, answered the same
/// way), and from then on every byte written to the data port is sent down the cable as it is, and
/// the game times the music itself. Reading the status port tells a program whether it may write
/// (bit 6 clear) and whether a byte is waiting to be read (bit 7 clear).
/// </para>
/// <para>
/// The bytes are put back together into MIDI messages here, so the synth gets whole ones: a status
/// byte and its data, with "running status" (a message that leaves out its status byte reuses the
/// last one), system exclusive messages from F0h to F7h, and real-time bytes (F8h and up) passed
/// over wherever they come.
/// </para>
/// </remarks>
internal sealed class Mpu401(Action<string>? log = null)
{
    private const byte Acknowledge = 0xFE;

    private readonly Queue<byte> _input = new();
    private readonly List<byte> _sysEx = [];
    private bool _uart;

    // the message being put together: its status byte, its data so far, and how much it needs
    private byte _status;
    private int _data1;
    private int _received;
    private int _needed;
    private bool _inSysEx;

    // set when a message is finished, which ends a "want to send" in intelligent mode
    private bool _finished;

    // intelligent mode: after a "want to send" command, the bytes of one message go straight out
    private bool _wantToSend;

    /// <summary>What the MIDI cable is plugged into, if anything.</summary>
    public IMidiOut? Output { get; set; }

    /// <summary>Whether it's in UART mode, passing every data byte on.</summary>
    public bool Uart => _uart;

    /// <summary>A read of the data port (330h) or the status port (331h).</summary>
    public byte Read(int port)
    {
        if ((port & 1) == 0)
        {
            return _input.Count > 0 ? _input.Dequeue() : Acknowledge;
        }

        // bit 6 clear: ready for a byte; bit 7 clear: a byte to read. The rest read as ones.
        return (byte)(0x3F | (_input.Count == 0 ? 0x80 : 0));
    }

    /// <summary>A write to the data port (330h) or the command port (331h).</summary>
    public void Write(int port, byte value)
    {
        if ((port & 1) == 0)
        {
            if (_uart)
            {
                Send(value);
            }
            else if (_wantToSend)
            {
                Send(value);
                _wantToSend = !_finished;
            }
            else
            {
                log?.Invoke($"mpu-401: data {value:X2} in intelligent mode, ignored");
            }

            return;
        }

        Command(value);
    }

    private void Command(byte command)
    {
        if (_uart)
        {
            // in UART mode only a reset is heard, and it isn't acknowledged: the card goes back to
            // its intelligent mode quietly, as DOSBox has it
            if (command == 0xFF)
            {
                Reset();
            }

            return;
        }

        switch (command)
        {
            case 0xFF:
                Reset();
                break;
            case 0x3F:
                _uart = true;
                break;
            case >= 0xD0 and <= 0xD7:
            case 0xDF:
                // "want to send data" (a channel message, for track 0-7) and "want to send a
                // system message": the next message written to the data port goes straight out
                // on the cable. That's how a program uses the intelligent mode without its
                // sequencer, as Wing Commander's Roland driver does.
                _wantToSend = true;
                break;
            default:
                // the intelligent mode's commands: acknowledged, and otherwise not done
                log?.Invoke($"mpu-401: command {command:X2} is not done");
                break;
        }

        _input.Enqueue(Acknowledge);
    }

    private void Reset()
    {
        _uart = false;
        _wantToSend = false;
        _input.Clear();
        _status = 0;
        _received = 0;
        _needed = 0;
        _inSysEx = false;
        _sysEx.Clear();
    }

    /// <summary>A byte on the MIDI cable, gathered into messages.</summary>
    private void Send(byte value)
    {
        _finished = false;
        if (value >= 0xF8)
        {
            // real-time: clock, start, stop, active sensing, reset. Nothing here needs them.
            return;
        }

        if (_inSysEx)
        {
            if (value < 0x80 || value == 0xF7)
            {
                _sysEx.Add(value);
                if (value == 0xF7)
                {
                    _inSysEx = false;
                    Output?.SysEx(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sysEx));
                    _sysEx.Clear();
                    _finished = true;
                }

                return;
            }

            // a status byte ends a system exclusive message that never got its F7
            _inSysEx = false;
            _sysEx.Add(0xF7);
            Output?.SysEx(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sysEx));
            _sysEx.Clear();
        }

        if (value >= 0x80)
        {
            switch (value)
            {
                case 0xF0:
                    _inSysEx = true;
                    _sysEx.Add(value);
                    _status = 0;
                    return;
                case >= 0xF0:
                    // the other system messages; they cancel running status
                    _status = value;
                    _needed = value switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, _ => 0 };
                    break;
                default:
                    _status = value;
                    _needed = (value & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                    break;
            }

            _received = 0;
            if (_needed == 0)
            {
                Deliver(0);
            }

            return;
        }

        if (_status == 0)
        {
            // data with no status to go with it
            return;
        }

        if (_received == 0)
        {
            _data1 = value;
        }

        if (++_received == _needed)
        {
            Deliver(_needed == 1 ? 0 : value);
        }
    }

    private void Deliver(int data2)
    {
        uint message = _status | ((uint)(_needed > 0 ? _data1 : 0) << 8) | ((uint)data2 << 16);
        if (_status < 0xF0)
        {
            Output?.Message(message);
        }

        // channel messages keep their status for the next (running status); system ones don't
        _received = 0;
        _finished = true;
        if (_status >= 0xF0)
        {
            _status = 0;
        }
    }
}
