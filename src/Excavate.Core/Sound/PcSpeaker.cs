using Excavate.Core.Devices;

namespace Excavate.Core.Sound;

/// <summary>
/// The PC's speaker: a cone driven on or off, never between. Port 61h's bit 1 lets the signal
/// through, and bit 0 gates the timer's counter 2, whose output is the signal: a square wave at
/// the frequency its count gives, or held high when the gate is off, so a program can also
/// move the cone itself by flipping bit 1.
/// </summary>
/// <remarks>
/// <para>
/// Each sample is the share of its time the signal was on, counted a timer tick at a time.
/// The machine settles the speaker at the moment before the bits or the counter change, so a
/// sample a change falls in is counted part with the old setting and part with the new. That
/// matters to programs that play digitized sound on the speaker, as Access's RealSound does:
/// they reload counter 2 thousands of times a second, and the length of each pulse is a sample.
/// </para>
/// <para>
/// The cone can't follow such pulses, only their average: a low-pass filter at about 7 kHz
/// stands for its weight, so a carrier near the edge of hearing doesn't fold back into the
/// sound as noise. A high-pass at about 10 Hz takes out the steady level the cone rests at, as
/// the speaker's own coupling did.
/// </para>
/// </remarks>
internal sealed class PcSpeaker(IntervalTimer timer) : ISoundSource
{
    private const int Amplitude = 7000;

    private const double TicksPerSample = IntervalTimer.Frequency / Machine.SampleRate;

    // the low-pass's share of each new level: a one-pole filter at 7 kHz, twice over
    private static readonly double Smoothing = 1 - Math.Exp(-2 * Math.PI * 7000 / Machine.SampleRate);

    // the next sample's number, from the machine's start, which places it in the timer's time
    private long _sample;

    // the part of the next sample settled so far: up to which tick, and the ticks it was on
    private double _settled;
    private double _on;

    // the filters' state: the low-pass's two stages, and the high-pass's last input and output
    private double _low1, _low2, _lastIn, _lastOut;

    /// <summary>Port 61h as last written: bit 0 the counter's gate, bit 1 the speaker's data.</summary>
    public byte PortB { get; set; }

    /// <summary>
    /// Counts the signal up to a moment, in timer ticks, as it's set now: called before the bits
    /// or the counter change, once the sound has been made up to the last whole sample.
    /// </summary>
    public void Settle(long tick)
    {
        double start = _sample * TicksPerSample;
        double from = Math.Max(start, _settled);
        double to = Math.Min(tick, start + TicksPerSample);
        if (to > from)
        {
            _on += On(from, to);
            _settled = to;
        }
    }

    public void Render(Span<int> mix)
    {
        for (int i = 0; i + 1 < mix.Length; i += 2, _sample++)
        {
            double start = _sample * TicksPerSample;
            double end = start + TicksPerSample;
            double from = Math.Max(start, _settled);
            double level = (_on + On(from, end)) / TicksPerSample;
            _on = 0;
            _settled = end;

            // the cone's weight, then its resting level filtered out
            _low1 += Smoothing * (level - _low1);
            _low2 += Smoothing * (_low1 - _low2);
            double output = _low2 - _lastIn + (0.9986 * _lastOut);
            _lastIn = _low2;
            _lastOut = output;

            int sample = (int)(output * Amplitude);
            mix[i] += sample;
            mix[i + 1] += sample;
        }
    }

    /// <summary>The ticks the signal was on between two moments, as it's set now.</summary>
    private double On(double from, double to)
    {
        if ((PortB & 0x02) == 0 || to <= from)
        {
            return 0;
        }

        double on = 0;
        for (long tick = (long)Math.Floor(from); tick < to; tick++)
        {
            double overlap = Math.Min(tick + 1, to) - Math.Max(tick, from);
            if (overlap > 0 && timer.Output2At(tick))
            {
                on += overlap;
            }
        }

        return on;
    }
}
