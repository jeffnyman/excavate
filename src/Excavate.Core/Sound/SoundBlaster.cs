using Excavate.Core.Devices;

namespace Excavate.Core.Sound;

/// <summary>
/// The Sound Blaster's digital side: its DSP (digital sound processor) and mixer, with the
/// samples it plays coming from memory through a DMA channel. The card's FM chip is <see cref="Opl"/>.
/// Written from DOSBox's <c>sblaster.cpp</c>, as a Sound Blaster 16 (DSP 4.05) at 220h, IRQ 5,
/// DMA 1 and the 16-bit DMA 5, GOG's settings.
/// </summary>
/// <remarks>
/// <para>
/// A program talks to the DSP through four ports. It resets it by writing 1 and then 0 to
/// <c>base+6</c>, and the DSP answers AAh. Commands and their arguments are written to
/// <c>base+C</c>, once bit 7 of a read of that port says it's ready; answers are read from
/// <c>base+A</c>, once bit 7 of <c>base+E</c> says one is there. Reading <c>base+E</c> is also how
/// the program acknowledges the card's interrupt.
/// </para>
/// <para>
/// To play a sound, a program sets up a DMA channel on its buffer, tells the DSP the rate
/// (command 40h, a "time constant", or on the SB16 41h, the rate itself) and the length, and the
/// DSP fetches the samples through DMA as it plays them, 8-bit and unsigned, at that rate. At the
/// end of the length it raises its interrupt. In "single-cycle" mode (14h) it then stops, and the
/// program starts the next block from its interrupt handler; in "auto-initialize" mode (1Ch) it
/// starts over at once, and the program refills the half of its buffer that just played.
/// </para>
/// <para>
/// The samples are made just in time, like the FM chip's: <see cref="Render"/> fetches bytes
/// through DMA as the playing reaches them, and raises the interrupt when the length runs out.
/// For that to happen at the right moment, and not at the next port write, the machine asks
/// <see cref="FramesUntilInterrupt"/> and makes the sound up to then.
/// </para>
/// <para>
/// The SB16 also plays 16-bit samples (commands B0h-BFh), which come through the second DMA
/// controller, a word at a time, on channel 5. Its lengths count samples rather than bytes, and
/// the end of a block raises the same IRQ, but the program acknowledges it by reading
/// <c>base+F</c> instead; the mixer's register 82h says which of the two is pending.
/// </para>
/// <para>
/// ADPCM and recording aren't here, and the commands for them are logged.
/// </para>
/// </remarks>
internal sealed class SoundBlaster : ISoundSource
{
    private const int FracBits = 16;
    private const long FracOne = 1L << FracBits;
    private const byte Version = 4, Revision = 5;

    private readonly DmaController _dma;
    private readonly int _dmaChannel;
    private readonly DmaController? _wideDma;
    private readonly int _wideChannel;
    private readonly int _sampleRate;
    private readonly Action _interrupt;
    private readonly Action<string>? _log;

    private readonly Queue<byte> _output = new();
    private readonly byte[] _mixer = new byte[256];
    private int _mixerIndex;

    // a command being received: the command, its arguments so far, and how many it needs
    private int _command = -1;
    private readonly byte[] _arguments = new byte[3];
    private int _argumentCount, _argumentsNeeded;
    private bool _resetting;
    private byte _testRegister;

    // the playing: the rate, the bytes left in the block, and how it goes on
    private int _rate = 22050;
    private int _blockLength = 0x800;
    private int _bytesLeft;
    private int _length;
    private bool _playing, _paused, _autoInitialize, _stereo, _silence, _exitAutoInitialize;
    private bool _interruptPending, _wideInterruptPending;
    private bool _signed, _halveRate, _sixteenBit;

    // the output: where between the last two samples the output is, and those two
    private long _position;
    private long _step;
    private int _lastLeft, _lastRight, _left16, _right16;

    /// <param name="dma">The first DMA controller, for 8-bit sound.</param>
    /// <param name="dmaChannel">Its channel, 1.</param>
    /// <param name="sampleRate">The output's rate.</param>
    /// <param name="interrupt">Raises the card's IRQ.</param>
    /// <param name="log">Where to say what isn't done.</param>
    /// <param name="wideDma">The second DMA controller, for 16-bit sound; without it, 16-bit commands are logged.</param>
    /// <param name="wideChannel">Its channel, numbered within it: 1 for channel 5.</param>
    public SoundBlaster(DmaController dma, int dmaChannel, int sampleRate, Action interrupt, Action<string>? log = null, DmaController? wideDma = null, int wideChannel = 1)
    {
        _dma = dma;
        _dmaChannel = dmaChannel;
        _wideDma = wideDma;
        _wideChannel = wideChannel;
        _sampleRate = sampleRate;
        _interrupt = interrupt;
        _log = log;
        ResetMixer();
        SetRate(_rate);
    }

    /// <summary>Whether a block is playing; for tests.</summary>
    public bool Playing => _playing && !_paused;

    /// <summary>The port base, 220h.</summary>
    public const int Base = 0x220;

    /// <summary>A read of one of the card's digital ports, 224h-22Fh.</summary>
    public byte Read(int port)
    {
        switch (port - Base)
        {
            case 0x5:
                return ReadMixer();
            case 0xA:
                return _output.Count > 0 ? _output.Dequeue() : (byte)0xFF;
            case 0xC:
                // bit 7 clear: ready for a command
                return 0x7F;
            case 0xE:
                // bit 7 set: an answer to read. Reading acknowledges the 8-bit interrupt.
                _interruptPending = false;
                return (byte)(_output.Count > 0 ? 0xFF : 0x7F);
            case 0xF:
                // acknowledges the 16-bit interrupt
                _wideInterruptPending = false;
                return 0xFF;
            default:
                return 0xFF;
        }
    }

    /// <summary>A write to one of the card's digital ports.</summary>
    public void Write(int port, byte value)
    {
        switch (port - Base)
        {
            case 0x4:
                _mixerIndex = value;
                break;
            case 0x5:
                WriteMixer(value);
                break;
            case 0x6:
                if ((value & 1) != 0)
                {
                    _resetting = true;
                }
                else if (_resetting)
                {
                    _resetting = false;
                    Reset();
                }

                break;
            case 0xC:
                Command(value);
                break;
        }
    }

    private void Reset()
    {
        _output.Clear();
        _command = -1;
        _playing = false;
        _paused = false;
        _silence = false;
        _interruptPending = false;
        _wideInterruptPending = false;
        _lastLeft = _lastRight = _left16 = _right16 = 0;
        _output.Enqueue(0xAA);
    }

    private void Command(byte value)
    {
        if (_command < 0)
        {
            _command = value;
            _argumentCount = 0;
            _argumentsNeeded = value switch
            {
                0x10 or 0x40 or 0xE0 or 0xE2 or 0xE4 => 1,
                0x14 or 0x16 or 0x17 or 0x24 or 0x41 or 0x42 or 0x48 or 0x74 or 0x75 or 0x76 or 0x77 or 0x80 => 2,
                >= 0xB0 and <= 0xCF => 3,
                _ => 0,
            };
        }
        else
        {
            _arguments[_argumentCount++] = value;
        }

        if (_argumentCount < _argumentsNeeded)
        {
            return;
        }

        int command = _command;
        _command = -1;
        Execute(command);
    }

    private int Word(int first) => _arguments[first] | (_arguments[first + 1] << 8);

    private void Execute(int command)
    {
        switch (command)
        {
            case 0x10:
                // direct output: one sample, now
                _left16 = _right16 = (_arguments[0] - 128) << 8;
                _lastLeft = _left16;
                _lastRight = _right16;
                break;
            case 0x14:
            case 0x91:
                // 8-bit single-cycle: a block of the length given (14h), or the block size (91h)
                Start(command == 0x14 ? Word(0) + 1 : _blockLength, autoInitialize: false, stereo: Stereo, halveRate: Stereo);
                break;
            case 0x1C:
            case 0x90:
                // 8-bit auto-initialize, in blocks of the block size
                Start(_blockLength, autoInitialize: true, stereo: Stereo, halveRate: Stereo);
                break;
            case 0x20:
                // direct input: the microphone, silent
                _output.Enqueue(0x80);
                break;
            case 0x40:
                // the time constant: 256 less a million over the rate (for both channels together, in stereo)
                SetRate(1_000_000 / (256 - _arguments[0]));
                break;
            case 0x41:
            case 0x42:
                // the SB16's rate, high byte first
                SetRate((_arguments[0] << 8) | _arguments[1]);
                break;
            case 0x48:
                _blockLength = Word(0) + 1;
                break;
            case 0x80:
                // silence for a length, then the interrupt
                Start(Word(0) + 1, autoInitialize: false, stereo: false);
                _silence = true;
                break;
            case >= 0xB0 and <= 0xCF:
                // the SB16's own commands, Bxh 16-bit and Cxh 8-bit: bit 3 recording, bit 2
                // auto-initialize, bit 1 the FIFO (nothing here); the mode's bit 5 stereo and bit 4
                // signed samples; then the length, in samples, less one
                if ((command & 0x08) != 0 || (command < 0xC0 && _wideDma is null))
                {
                    goto default;
                }

                Start(Word(1) + 1, autoInitialize: (command & 0x04) != 0, stereo: (_arguments[0] & 0x20) != 0, sixteenBit: command < 0xC0);
                _signed = (_arguments[0] & 0x10) != 0;
                break;
            case 0x45:
            case 0x47:
                // carry on auto-initializing, after DAh or D9h
                _exitAutoInitialize = false;
                break;
            case 0xD0:
            case 0xD5:
                // pause the 8-bit or the 16-bit sound, and carry on
                _paused = true;
                break;
            case 0xD4:
            case 0xD6:
                _paused = false;
                break;
            case 0xD1:
            case 0xD3:
                // the speaker on and off: an SB16 ignores it
                break;
            case 0xD8:
                _output.Enqueue(0xFF);
                break;
            case 0xD9:
            case 0xDA:
                // ends auto-initialize after the block playing, 16-bit or 8-bit
                _exitAutoInitialize = true;
                break;
            case 0xE0:
                // identification: the argument inverted
                _output.Enqueue((byte)~_arguments[0]);
                break;
            case 0xE1:
                _output.Enqueue(Version);
                _output.Enqueue(Revision);
                break;
            case 0xE3:
                foreach (char c in "COPYRIGHT (C) CREATIVE TECHNOLOGY LTD, 1992.\0")
                {
                    _output.Enqueue((byte)c);
                }

                break;
            case 0xE4:
                _testRegister = _arguments[0];
                break;
            case 0xE8:
                _output.Enqueue(_testRegister);
                break;
            case 0xF2:
                // an interrupt, at once: how programs find the card's IRQ
                RaiseInterrupt();
                break;
            case 0xF3:
                // the 16-bit interrupt, at once
                _wideInterruptPending = true;
                _interrupt();
                break;
            case 0xF8:
                _output.Enqueue(0);
                break;
            default:
                _log?.Invoke($"sound blaster: DSP command {command:X2} is not done");
                break;
        }
    }

    // the SB Pro's stereo switch, in its mixer
    private bool Stereo => (_mixer[0x0E] & 0x02) != 0;

    private void SetRate(int rate)
    {
        _rate = Math.Clamp(rate, 4000, 48000);
        UpdateStep();
    }

    // the time constant's rate is for both channels together on an SB Pro, so in its stereo each
    // side plays at half of it; the SB16's own commands give the rate of each side
    private void UpdateStep() => _step = (long)_rate * FracOne / _sampleRate / (_halveRate ? 2 : 1);

    private void Start(int length, bool autoInitialize, bool stereo, bool halveRate = false, bool sixteenBit = false)
    {
        _sixteenBit = sixteenBit;
        _halveRate = halveRate;
        UpdateStep();
        _length = length;
        _bytesLeft = length;
        _autoInitialize = autoInitialize;
        _exitAutoInitialize = false;
        _stereo = stereo;
        _silence = false;
        _signed = false;
        _playing = true;
        _paused = false;
        _log?.Invoke($"sound blaster: {length} {(sixteenBit ? "16-bit samples" : "bytes")} at {_rate / (_halveRate ? 2 : 1)} Hz{(stereo ? ", stereo" : "")}{(autoInitialize ? ", over and over" : "")}");
    }

    private void RaiseInterrupt()
    {
        if (_sixteenBit && _playing)
        {
            _wideInterruptPending = true;
        }
        else
        {
            _interruptPending = true;
        }

        _interrupt();
    }

    // where the samples come from: the second controller for 16-bit sound
    private DmaController Dma => _sixteenBit ? _wideDma! : _dma;

    private int DmaChannel => _sixteenBit ? _wideChannel : _dmaChannel;

    /// <summary>
    /// How many more frames of output until the block ends and the interrupt comes, or -1 when
    /// none will come by itself.
    /// </summary>
    public int FramesUntilInterrupt
    {
        get
        {
            if (!_playing || _paused || (!_silence && !Dma.Ready(DmaChannel)))
            {
                return -1;
            }

            // a sample is taken each time the position passes a whole step; the block ends with
            // the sample that takes its last byte (or word)
            int bytesPerSample = _stereo && !_silence ? 2 : 1;
            long samples = (_bytesLeft + bytesPerSample - 1) / bytesPerSample;
            long frames = ((samples * FracOne) - _position + _step - 1) / _step;
            return (int)Math.Clamp(frames + 1, 1, int.MaxValue);
        }
    }

    /// <inheritdoc/>
    public void Render(Span<int> mix)
    {
        int frames = mix.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            while (_position >= FracOne)
            {
                NextSample();
                _position -= FracOne;
            }

            int left = _lastLeft + (int)(((_left16 - _lastLeft) * _position) >> FracBits);
            int right = _lastRight + (int)(((_right16 - _lastRight) * _position) >> FracBits);
            mix[i * 2] += left;
            mix[(i * 2) + 1] += right;
            _position += _step;
        }
    }

    /// <summary>The next sample, from memory through DMA while a block is playing.</summary>
    private void NextSample()
    {
        _lastLeft = _left16;
        _lastRight = _right16;
        if (!_playing || _paused)
        {
            return;
        }

        if (_silence)
        {
            _left16 = _right16 = 0;
            _bytesLeft--;
        }
        else if (_sixteenBit)
        {
            if (!_wideDma!.ReadWord(_wideChannel, out ushort left))
            {
                return;
            }

            _bytesLeft--;
            ushort right = left;
            if (_stereo && _bytesLeft > 0 && _wideDma.ReadWord(_wideChannel, out right))
            {
                _bytesLeft--;
            }

            _left16 = Sample(left);
            _right16 = Sample(right);
        }
        else
        {
            if (!_dma.Read(_dmaChannel, out byte left))
            {
                // the channel isn't ready: the DSP waits, holding its output
                return;
            }

            _bytesLeft--;
            byte right = left;
            if (_stereo && _bytesLeft > 0 && _dma.Read(_dmaChannel, out right))
            {
                _bytesLeft--;
            }

            _left16 = Sample(left);
            _right16 = Sample(right);
        }

        if (_bytesLeft > 0)
        {
            return;
        }

        // the end of the block: the interrupt, and then over again or stop
        RaiseInterrupt();
        if (_autoInitialize && !_exitAutoInitialize)
        {
            _bytesLeft = _length;
        }
        else
        {
            _playing = false;
            _silence = false;
        }
    }

    private int Sample(byte value) => _signed ? (sbyte)value << 8 : (value - 128) << 8;

    private int Sample(ushort value) => _signed ? (short)value : value - 32768;

    private void ResetMixer()
    {
        Array.Clear(_mixer);

        // the SB16's IRQ and DMA, as its mixer reports them: IRQ 5, DMA 1 and the 16-bit DMA 5
        _mixer[0x80] = 0x02;
        _mixer[0x81] = 0x22;
    }

    private byte ReadMixer() => _mixerIndex switch
    {
        // which interrupts are pending: bit 0 the 8-bit one, bit 1 the 16-bit one
        0x82 => (byte)((_interruptPending ? 0x01 : 0x00) | (_wideInterruptPending ? 0x02 : 0x00)),
        _ => _mixer[_mixerIndex],
    };

    private void WriteMixer(byte value)
    {
        switch (_mixerIndex)
        {
            case 0x00:
                ResetMixer();
                break;
            case 0x80:
            case 0x81:
            case 0x82:
                // the IRQ and DMA are fixed here
                break;
            default:
                _mixer[_mixerIndex] = value;
                break;
        }
    }
}
