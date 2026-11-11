using Excavate.Core.Devices;

namespace Excavate.Core.Sound;

/// <summary>
/// The PC's speaker: a cone driven on or off, never between. Port 61h's bit 1 lets the signal
/// through, and bit 0 gates the timer's counter 2, whose output is the signal: a square wave at
/// the frequency its count gives, or held high when the gate is off, so a program can also
/// move the cone itself by flipping bit 1.
/// </summary>
/// <remarks>
/// The machine makes the sound up to the moment before either changes, so within a stretch of
/// samples the bits and the counter are as they were set. Each sample is the average of the
/// signal at several moments across it, which keeps a high tone from aliasing into a harsh one,
/// and a filter takes out the steady level the cone rests at, as the speaker's own coupling did.
/// </remarks>
internal sealed class PcSpeaker(IntervalTimer timer) : ISoundSource
{
    private const int Amplitude = 7000;

    // the moments sampled within each output sample
    private const int Steps = 8;

    private const double TicksPerSample = IntervalTimer.Frequency / Machine.SampleRate;

    // the next sample's number, from the machine's start, which places it in the timer's time
    private long _sample;

    // the filter's last input and output
    private double _lastIn, _lastOut;

    /// <summary>Port 61h as last written: bit 0 the counter's gate, bit 1 the speaker's data.</summary>
    public byte PortB { get; set; }

    public void Render(Span<int> mix)
    {
        bool data = (PortB & 0x02) != 0;
        for (int i = 0; i + 1 < mix.Length; i += 2, _sample++)
        {
            double level = 0;
            if (data)
            {
                int high = 0;
                double start = _sample * TicksPerSample;
                for (int step = 0; step < Steps; step++)
                {
                    if (timer.Output2At((long)(start + ((step + 0.5) * TicksPerSample / Steps))))
                    {
                        high++;
                    }
                }

                level = (double)high / Steps;
            }

            // the cone's resting level filtered out: a high-pass at about 10 Hz
            double output = level - _lastIn + (0.9986 * _lastOut);
            _lastIn = level;
            _lastOut = output;

            int sample = (int)(output * Amplitude);
            mix[i] += sample;
            mix[i + 1] += sample;
        }
    }
}
