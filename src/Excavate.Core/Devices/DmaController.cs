namespace Excavate.Core.Devices;

/// <summary>
/// The Intel 8237 DMA controller: the PC's first one, with four channels that move bytes between
/// memory and a device without the processor. Written from DOSBox's <c>dma.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// A program sets a channel up and the device does the rest: the program writes the buffer's
/// address and its length less one, picks the direction and whether the channel starts over at
/// the end ("auto-initialize"), and unmasks it. The device then takes (or gives) a byte whenever
/// it needs one, and the channel steps its address and counts down. When the count passes zero the
/// channel has reached its "terminal count": it stops, by masking itself, or starts over from the
/// address and count it was given.
/// </para>
/// <para>
/// The 8237 counts addresses in 16 bits. The top bits come from a separate page register (at 87h,
/// 83h, 81h and 82h for channels 0 to 3) which doesn't count, so a transfer can't cross a
/// 64 KB boundary: it wraps around within its page, which is why programs place their sound
/// buffers carefully. Each 16-bit register is written and read a byte at a time, low then high,
/// with a flip-flop that keeps track of which is next.
/// </para>
/// <para>
/// The AT added a second 8237 for 16-bit transfers, on channels 4 to 7 (4 links the first
/// controller to it). It's the same chip, at ports C0h-DEh, its registers every other port, with
/// page registers at 8Fh, 8Bh, 89h and 8Ah. It moves a word at a time, and counts in words: its
/// address is a word's, so the byte address is twice it, and the page register's lowest bit is
/// left out. A transfer can't cross a 128 KB boundary.
/// </para>
/// </remarks>
internal sealed class DmaController(Func<uint, byte> read, Action<uint, byte> write, bool wide = false)
{
    private readonly Channel[] _channels = [new(wide), new(wide), new(wide), new(wide)];
    private bool _flipFlop;

    // the page registers' ports, for channels 0 to 3 of this controller
    private readonly int[] _pagePorts = wide ? [0x8F, 0x8B, 0x89, 0x8A] : [0x87, 0x83, 0x81, 0x82];

    /// <summary>Whether this is the second controller, which moves words.</summary>
    public bool Wide => wide;

    /// <summary>Called when a channel's setup changes, so a device can see what it means for its timing.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether a channel is ready to move bytes: unmasked.</summary>
    public bool Ready(int channel) => !_channels[channel].Masked;

    /// <summary>Whether a channel starts over at its terminal count.</summary>
    public bool AutoInitialize(int channel) => _channels[channel].AutoInitialize;

    /// <summary>
    /// A device takes a byte from memory through a channel. False when the channel is masked,
    /// and nothing moves.
    /// </summary>
    public bool Read(int number, out byte value)
    {
        var channel = _channels[number];
        if (channel.Masked)
        {
            value = 0;
            return false;
        }

        value = read(channel.Address);
        Step(channel);
        return true;
    }

    /// <summary>
    /// A device takes a word from memory through a channel of the second controller, which
    /// counts in words. False when the channel is masked.
    /// </summary>
    public bool ReadWord(int number, out ushort value)
    {
        var channel = _channels[number];
        if (channel.Masked)
        {
            value = 0;
            return false;
        }

        uint address = channel.Address;
        value = (ushort)(read(address) | (read(address + 1) << 8));
        Step(channel);
        return true;
    }

    /// <summary>A device gives a byte to memory through a channel. False when the channel is masked.</summary>
    public bool Write(int number, byte value)
    {
        var channel = _channels[number];
        if (channel.Masked)
        {
            return false;
        }

        write(channel.Address, value);
        Step(channel);
        return true;
    }

    private static void Step(Channel channel)
    {
        channel.CurrentAddress = (ushort)(channel.CurrentAddress + (channel.Decrement ? -1 : 1));
        if (channel.CurrentCount-- == 0)
        {
            // the terminal count: start over, or stop
            channel.TerminalCount = true;
            if (channel.AutoInitialize)
            {
                channel.CurrentAddress = channel.BaseAddress;
                channel.CurrentCount = channel.BaseCount;
            }
            else
            {
                channel.Masked = true;
            }
        }
    }

    /// <summary>
    /// A register's number from its port: the first controller's are ports 00h-0Fh, the
    /// second's every other port from C0h. A page register's port is left as it is.
    /// </summary>
    private int Register(int port) => wide && port >= 0xC0 ? (port - 0xC0) >> 1 : port;

    /// <summary>A read of one of the controller's registers, or a page register.</summary>
    public byte In(int port)
    {
        int page = Array.IndexOf(_pagePorts, port);
        if (page >= 0)
        {
            return _channels[page].Page;
        }

        port = Register(port);
        if (port < 8)
        {
            var channel = _channels[port >> 1];
            ushort value = (port & 1) == 0 ? channel.CurrentAddress : channel.CurrentCount;
            _flipFlop = !_flipFlop;
            return (byte)(_flipFlop ? value : value >> 8);
        }

        switch (port)
        {
            case 0x08:
                // the status: bits 0-3 which channels reached their terminal count since the
                // last read, which clears them; bits 4-7 requests, none pending here
                int status = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (_channels[i].TerminalCount)
                    {
                        status |= 1 << i;
                        _channels[i].TerminalCount = false;
                    }
                }

                return (byte)status;
            default:
                return 0xFF;
        }
    }

    /// <summary>A write to one of the controller's registers, or a page register.</summary>
    public void Out(int port, byte value)
    {
        int page = Array.IndexOf(_pagePorts, port);
        port = Register(port);
        if (page >= 0)
        {
            _channels[page].Page = value;
        }
        else if (port < 8)
        {
            var channel = _channels[port >> 1];
            _flipFlop = !_flipFlop;
            if ((port & 1) == 0)
            {
                // the address, the base and current together
                channel.BaseAddress = _flipFlop ? (ushort)((channel.BaseAddress & 0xFF00) | value) : (ushort)((channel.BaseAddress & 0x00FF) | (value << 8));
                channel.CurrentAddress = channel.BaseAddress;
            }
            else
            {
                // the count, one less than the bytes to move
                channel.BaseCount = _flipFlop ? (ushort)((channel.BaseCount & 0xFF00) | value) : (ushort)((channel.BaseCount & 0x00FF) | (value << 8));
                channel.CurrentCount = channel.BaseCount;
            }
        }
        else
        {
            switch (port)
            {
                case 0x08:
                    // the command register: memory to memory, priorities, and such; nothing a
                    // PC program changes
                    break;
                case 0x09:
                    // a request by software; for memory-to-memory transfers, which the PC never used
                    break;
                case 0x0A:
                    _channels[value & 3].Masked = (value & 4) != 0;
                    break;
                case 0x0B:
                {
                    // the mode: bits 2-3 the direction, 4 auto-initialize, 5 count down, 6-7 how
                    // the device asks (single bytes, blocks, on demand), which are all one here
                    var channel = _channels[value & 3];
                    channel.AutoInitialize = (value & 0x10) != 0;
                    channel.Decrement = (value & 0x20) != 0;
                    break;
                }

                case 0x0C:
                    _flipFlop = false;
                    break;
                case 0x0D:
                    // master clear: every channel masked, the flip-flop back to the low byte
                    _flipFlop = false;
                    foreach (var channel in _channels)
                    {
                        channel.Masked = true;
                        channel.TerminalCount = false;
                    }

                    break;
                case 0x0E:
                    foreach (var channel in _channels)
                    {
                        channel.Masked = false;
                    }

                    break;
                case 0x0F:
                    for (int i = 0; i < 4; i++)
                    {
                        _channels[i].Masked = (value & (1 << i)) != 0;
                    }

                    break;
            }
        }

        Changed?.Invoke();
    }

    private sealed class Channel(bool wide)
    {
        public ushort BaseAddress;
        public ushort BaseCount;
        public ushort CurrentAddress;
        public ushort CurrentCount;
        public byte Page;
        public bool Masked = true;
        public bool AutoInitialize;
        public bool Decrement;
        public bool TerminalCount;

        // a word channel's address is a word's, within a 128 KB page whose lowest bit is left out
        public uint Address => wide
            ? ((uint)(Page & 0xFE) << 16) | ((uint)CurrentAddress << 1)
            : ((uint)Page << 16) | CurrentAddress;
    }
}
