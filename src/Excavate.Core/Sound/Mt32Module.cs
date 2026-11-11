using Excavate.Core.Sound.Mt32;

namespace Excavate.Core.Sound;

/// <summary>
/// A Roland MT-32 (or CM-32L) on the end of a MIDI cable: Munt's emulation of its synthesizer
/// (<see cref="Synth"/>), fed whole MIDI messages, and its output converted from its own 32 kHz to
/// the machine's rate.
/// </summary>
/// <remarks>
/// <para>
/// The synth is the C# port of Munt (libmt32emu 2.7.0) made for the author's earlier projects,
/// Daventry and GUE, and carried over unchanged. It needs the module's two ROMs: the control ROM
/// (the firmware, with its patches and timbres) and the PCM ROM (the sampled attacks and drums).
/// They're Roland's, and not part of this project; <see cref="Mt32RomSet"/> recognizes them by
/// their SHA-1, whatever the files are called.
/// </para>
/// <para>
/// Messages are played the moment they arrive. That's exact here, because the machine makes the
/// sound up to the moment of each write to the MIDI port before the write takes effect
/// (<c>Machine.UpdateSound</c>), so "now" for the synth is the sample the write happened at.
/// </para>
/// </remarks>
internal sealed class Mt32Module : ISoundSource, IMidiOut
{
    private const int FracBits = 16;
    private const long FracOne = 1L << FracBits;
    private const int BlockFrames = 32;

    private readonly Synth _synth;
    private readonly long _step;
    private readonly short[] _block = new short[BlockFrames * 2];
    private int _blockPosition = BlockFrames;
    private long _position = FracOne;
    private int _lastLeft, _lastRight, _left, _right;

    /// <summary>Starts the synth with the ROMs; throws <see cref="InvalidDataException"/> if it won't start with them.</summary>
    public Mt32Module(Mt32RomSet roms, int sampleRate, Action<string>? log = null)
    {
        _synth = new Synth(new LogReports(log));
        if (!_synth.Open(roms.Control, roms.Pcm))
        {
            throw new InvalidDataException($"the MT-32 emulation won't start with {roms.Control.Info.Description} and {roms.Pcm.Info.Description}");
        }

        // messages are played as they come (see the remarks); full gain on the dry and reverb
        // outputs, as Munt and ScummVM default to
        _synth.MIDIDelayMode = MIDIDelayMode.Immediate;
        _synth.SetOutputGain(1.0f);
        _synth.SetReverbOutputGain(1.0f);

        Description = roms.Description;
        _step = (long)Synth.SampleRate * FracOne / sampleRate;
    }

    /// <summary>The module emulated, as in "CM-32L/LAPC-I Control v1.02".</summary>
    public string Description { get; }

    /// <summary>The 20 characters on the module's display.</summary>
    public string Display
    {
        get
        {
            _synth.GetDisplayState(out string text);
            return text;
        }
    }

    /// <summary>The synth's ROMs and what's made from them, which a saved state refers to rather than holds.</summary>
    internal object[] Fixed => _synth.Fixed;

    /// <inheritdoc/>
    public void Message(uint message) => _synth.PlayMsgNow(message);

    /// <inheritdoc/>
    public void SysEx(ReadOnlySpan<byte> message) => _synth.PlaySysexNow(message);

    /// <inheritdoc/>
    /// <remarks>Linear interpolation between the synth's samples: plain, and enough for a source with nothing much above 16 kHz.</remarks>
    public void Render(Span<int> mix)
    {
        int frames = mix.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            while (_position >= FracOne)
            {
                if (_blockPosition == BlockFrames)
                {
                    _synth.Render(_block, BlockFrames);
                    _blockPosition = 0;
                }

                _lastLeft = _left;
                _lastRight = _right;
                _left = _block[_blockPosition * 2];
                _right = _block[(_blockPosition * 2) + 1];
                _blockPosition++;
                _position -= FracOne;
            }

            mix[i * 2] += _lastLeft + (int)(((_left - _lastLeft) * _position) >> FracBits);
            mix[(i * 2) + 1] += _lastRight + (int)(((_right - _lastRight) * _position) >> FracBits);
            _position += _step;
        }
    }

    /// <summary>The display's messages, such as the one a game sends to greet the player, go to the log.</summary>
    private sealed class LogReports(Action<string>? log) : ReportHandler
    {
        public override void ShowLCDMessage(string message) => log?.Invoke($"mt32: \"{message}\"");
    }
}
