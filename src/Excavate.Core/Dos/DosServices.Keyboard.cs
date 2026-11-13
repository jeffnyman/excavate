namespace Excavate.Core.Dos;

// The keyboard, as DOS's console reads it: through INT 16h.
internal sealed partial class DosServices
{
    // DOS reads the keyboard as its console driver does, by calling INT 16h, so a program that
    // has taken over INT 16h (with INT 09h, to keep keys from the BIOS) answers DOS's calls too:
    // The Omnicron Conspiracy's music player waits for Enter with function 0Bh, and only the
    // game's own INT 16h has the key. DOSBox reads the BIOS's buffer instead, and the game's
    // eXoDOS copy comes with a program of 2009, OMNICRON.COM, that hooks INT 21h to send those
    // calls to INT 16h. Here DOS does what it always did.
    //
    // A call that reads the keyboard leaves C# for the BIOS's code at Machine.DosKeyStub, which
    // calls INT 16h (AH 0 for a key, 1 for whether there is one) through its vector, and comes
    // back with the answer to finish the call, then returns to the program. A key the call
    // waits for is waited for inside INT 16h.

    // the scan code still to come after the zero that DOS gives for a key with no character
    private byte? _extendedKey;

    // the line being typed for function 0Ah, kept in the program's buffer: its length so far
    private int _lineLength;

    // the call waiting on INT 16h: the DOS function, or 0 for none, and whether it asked for a
    // key or only whether one is waiting
    private byte _keyFunction;
    private bool _keyTaken;

    // for function 0Ch, the input function to run once the keyboard is empty
    private byte _flushThen;

    /// <summary>The INT 21h functions that read the keyboard; false for any other.</summary>
    private bool KeyboardCall(int function, Registers cpu)
    {
        switch (function)
        {
            case 0x01:
            case 0x07:
            case 0x08:
                // the next key's character, waiting for one; 01h echoes it
                if (_extendedKey is { } scan)
                {
                    _extendedKey = null;
                    cpu.Al = scan;
                }
                else
                {
                    AskBios((byte)function, take: true);
                }

                return true;
            case 0x06 when cpu.Dl == 0xFF:
                // direct console input: a key if one is waiting, zero clear, without waiting for one
                if (_extendedKey is { } waiting)
                {
                    _extendedKey = null;
                    cpu.Al = waiting;
                    machine.SetReturnZero(false);
                }
                else
                {
                    AskBios(0x06, take: false);
                }

                return true;
            case 0x0A:
                // a line, typed into the buffer at DS:DX, whose first byte is the most it holds
                if (machine.ReadByte(cpu.Ds, cpu.Dx) != 0)
                {
                    _lineLength = 0;
                    machine.Trace($"21/0A read a line, up to {machine.ReadByte(cpu.Ds, cpu.Dx) - 1} characters, into {cpu.Ds:X4}:{cpu.Dx:X4}");
                    AskBios(0x0A, take: true);
                }

                return true;
            case 0x0B:
                // whether a key is waiting
                if (_extendedKey is not null)
                {
                    cpu.Al = 0xFF;
                }
                else
                {
                    AskBios(0x0B, take: false);
                }

                return true;
            case 0x0C:
                // empty the keyboard, then do the input function in AL
                _extendedKey = null;
                _flushThen = cpu.Al;
                AskBios(0x0C, take: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Goes to the BIOS's code that calls INT 16h for a call: for a key, or for whether one is waiting.</summary>
    private void AskBios(byte function, bool take)
    {
        _keyFunction = function;
        _keyTaken = take;
        var cpu = machine.Cpu;
        cpu.Eax = (cpu.Eax & 0xFFFF_00FF) | (take ? 0x0000u : 0x0100u);
        cpu.Cs = Machine.BiosSegment;
        cpu.Eip = Machine.DosKeyStub;
    }

    /// <summary>
    /// INT 16h has answered a call that reads the keyboard: AX the key it gave, or for function
    /// 1, zero set when none is waiting. The call finishes, or asks again.
    /// </summary>
    internal void KeyFromBios()
    {
        var cpu = _registers;
        ushort key = cpu.Ax;
        bool waiting = (machine.Cpu.Eflags & Processors.Intel386.Flag.Zero) == 0;
        byte function = _keyFunction;
        _keyFunction = 0;
        cpu.Ah = function;

        switch (function)
        {
            case 0x01:
            case 0x07:
            case 0x08:
                cpu.Al = Character(key);
                if (function == 0x01 && cpu.Al != 0)
                {
                    ConsoleOut((char)cpu.Al);
                }

                break;
            case 0x06:
                if (_keyTaken)
                {
                    cpu.Al = Character(key);
                    machine.SetReturnZero(false);
                }
                else if (waiting)
                {
                    AskBios(0x06, take: true);
                }
                else
                {
                    cpu.Al = 0;
                    machine.SetReturnZero(true);
                }

                break;
            case 0x0A:
                if (!LineKey(Character(key)))
                {
                    AskBios(0x0A, take: true);
                }

                break;
            case 0x0B:
                cpu.Al = waiting ? (byte)0xFF : (byte)0;
                break;
            case 0x0C:
                if (_keyTaken || waiting)
                {
                    // a key taken; or one to take
                    AskBios(0x0C, take: !_keyTaken);
                }
                else if (_flushThen is 0x01 or 0x06 or 0x07 or 0x08 or 0x0A)
                {
                    cpu.Ax = (ushort)((_flushThen << 8) | _flushThen);
                    Call();
                }
                else
                {
                    cpu.Al = 0;
                }

                break;
            default:
                throw new InvalidOperationException($"INT 16h answered, and no DOS call waits for it (function {function:X2}h).");
        }
    }

    /// <summary>
    /// A key's character, as DOS gives it. A key with no character (an arrow, a function key)
    /// comes as two: a zero, then its scan code at the next call.
    /// </summary>
    private byte Character(ushort key)
    {
        byte character = (byte)key;
        if (character is 0 or 0xE0)
        {
            _extendedKey = (byte)(key >> 8);
            return 0;
        }

        return character;
    }

    /// <summary>
    /// A key typed into function 0Ah's line; true when the line is done. The keys are echoed as
    /// they come, Backspace takes one back, Escape the whole line, and keys with no character
    /// are ignored. At Enter, the buffer's second byte gets the line's length and the carriage
    /// return follows the line.
    /// </summary>
    private bool LineKey(byte key)
    {
        var cpu = _registers;
        int room = machine.ReadByte(cpu.Ds, cpu.Dx);
        switch (key)
        {
            case 0:
                // a key with no character: its scan code is dropped
                _extendedKey = null;
                break;
            case 0x0D:
                machine.WriteByte(cpu.Ds, cpu.Dx + 1u, (byte)_lineLength);
                machine.WriteByte(cpu.Ds, cpu.Dx + 2u + (uint)_lineLength, 0x0D);
                ConsoleOut('\r');
                return true;
            case 0x08:
                if (_lineLength > 0)
                {
                    _lineLength--;
                    ConsoleOut('\b');
                    ConsoleOut(' ');
                    ConsoleOut('\b');
                }

                break;
            case 0x1B:
                for (; _lineLength > 0; _lineLength--)
                {
                    ConsoleOut('\b');
                    ConsoleOut(' ');
                    ConsoleOut('\b');
                }

                break;
            default:
                // one byte is kept for the carriage return
                if (_lineLength < room - 1)
                {
                    machine.WriteByte(cpu.Ds, cpu.Dx + 2u + (uint)_lineLength, key);
                    _lineLength++;
                    ConsoleOut((char)key);
                }

                break;
        }

        return false;
    }
}
