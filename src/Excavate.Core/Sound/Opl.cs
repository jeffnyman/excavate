namespace Excavate.Core.Sound;

/// <summary>
/// Yamaha's OPL3 (YMF262) FM synthesizer as a card presents it: an address port and a data
/// port, two timers, and a status byte. The sound itself is DOSBox's DBOPL (<see cref="DbOplChip"/>);
/// the ports and timers are DOSBox's <c>adlib.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// The AdLib card (1987) carried an OPL2 (YM3812), nine voices of two operators each, at ports
/// 388h and 389h. The OPL3 that Sound Blaster Pro 2 and 16 cards carried plays everything an OPL2
/// does the same way, until a program sets the "new" bit in register 105h, so one chip serves
/// both: GOG's DOSBox runs both games with an OPL3. The OPL3's second register bank is reached
/// through ports 38Ah and 38Bh.
/// </para>
/// <para>
/// The timers matter more than they look. A program finds an AdLib by starting timer 1, waiting,
/// and checking that the status shows it ran out: no timers, no AdLib. Timer 1 counts in 80
/// microsecond steps, timer 2 in 320; each runs from its count up to 256 and sets its bit in the
/// status, and the IRQ bit (7) with it. The chip's interrupt line was never wired on the AdLib,
/// so programs poll.
/// </para>
/// </remarks>
internal sealed class Opl : ISoundSource
{
    private readonly DbOplChip _chip = new();
    private readonly OplTimer[] _timers = [new(80e-6), new(320e-6)];
    private readonly int[] _block = new int[BlockLength * 2];
    private uint _register;

    // DBOPL renders at most this many samples at a time into its own buffer
    private const int BlockLength = 512;

    public Opl(int sampleRate) => _chip.Setup((uint)sampleRate);

    /// <summary>
    /// A write to the card. <paramref name="port"/>'s low two bits pick what: 0 the address of
    /// the first bank, 1 data, 2 the address of the second bank, 3 data.
    /// </summary>
    public void Write(int port, byte value, double now)
    {
        if ((port & 1) == 0)
        {
            _register = _chip.WriteAddr((uint)port, value) & 0x1FF;
            return;
        }

        switch (_register)
        {
            // the timers' counts and control live on the card's side, not in the synthesis
            case 0x02:
                _timers[0].Count = value;
                break;
            case 0x03:
                _timers[1].Count = value;
                break;
            case 0x04:
                if ((value & 0x80) != 0)
                {
                    // clears both timers' flags, leaving them running
                    _timers[0].Reset(now);
                    _timers[1].Reset(now);
                }
                else
                {
                    _timers[0].Control(now, start: (value & 0x01) != 0, masked: (value & 0x40) != 0);
                    _timers[1].Control(now, start: (value & 0x02) != 0, masked: (value & 0x20) != 0);
                }

                break;
            default:
                _chip.WriteReg(_register, value);
                break;
        }
    }

    /// <summary>
    /// A read: the status from the first address port (bit 7 a timer ran out, 6 timer 1, 5 timer 2),
    /// and nothing from the others. An OPL2's status has bits 1 and 2 set and an OPL3's doesn't,
    /// which is how programs told the two apart.
    /// </summary>
    public byte Read(int port, double now)
    {
        if ((port & 3) != 0)
        {
            return 0xFF;
        }

        int status = 0;
        if (_timers[0].Overflowed(now))
        {
            status |= 0xC0;
        }

        if (_timers[1].Overflowed(now))
        {
            status |= 0xA0;
        }

        return (byte)status;
    }

    /// <inheritdoc/>
    public void Render(Span<int> mix)
    {
        int frames = mix.Length / 2;
        for (int done = 0; done < frames;)
        {
            int count = Math.Min(frames - done, BlockLength);
            var into = mix.Slice(done * 2, count * 2);
            if (_chip.Opl3Active)
            {
                // OPL3 mode: stereo, each voice panned left, right or both
                _chip.GenerateBlock3((uint)count, _block);
                for (int i = 0; i < count * 2; i++)
                {
                    into[i] += _block[i];
                }
            }
            else
            {
                // OPL2 mode: mono, on both sides
                _chip.GenerateBlock2((uint)count, _block);
                for (int i = 0; i < count; i++)
                {
                    into[2 * i] += _block[i];
                    into[(2 * i) + 1] += _block[i];
                }
            }

            done += count;
        }
    }

    /// <summary>One of the OPL's timers, as DOSBox's <c>adlib.cpp</c> keeps it: worked out from the time when asked.</summary>
    private sealed class OplTimer(double step)
    {
        private double _start;
        private double _delay;
        private bool _enabled;
        private bool _masked;
        private bool _overflow;

        /// <summary>The count it starts from, up to 256.</summary>
        public byte Count { get; set; }

        public bool Overflowed(double now)
        {
            Update(now);
            return _overflow;
        }

        /// <summary>Register 4 without bit 7: starts or stops it, and masks its flag or not.</summary>
        public void Control(double now, bool start, bool masked)
        {
            Update(now);
            if (start)
            {
                // a running timer isn't restarted
                if (!_enabled)
                {
                    _enabled = true;
                    _delay = (256 - Count) * step;
                    _start = now + _delay;
                }
            }
            else
            {
                _enabled = false;
            }

            _masked = masked;
            if (masked)
            {
                _overflow = false;
            }
        }

        /// <summary>Register 4 with bit 7: the flag clears, and the timer runs on in step with its period.</summary>
        public void Reset(double now)
        {
            _overflow = false;
            if (_delay == 0 || !_enabled)
            {
                return;
            }

            double remainder = (now - _start) % _delay;
            _start = now + (_delay - remainder);
        }

        private void Update(double now)
        {
            if (_enabled && _delay != 0 && now - _start >= 0 && !_masked)
            {
                _overflow = true;
            }
        }
    }
}
