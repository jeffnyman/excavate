using Excavate.Core.Devices;
using Excavate.Core.Sound;

namespace Excavate.Core.Tests;

/// <summary>The sound devices on their own, without a program.</summary>
public class SoundTests
{
    /// <summary>A tenth of a second of the speaker, with counter 2 set to a count, as programs set it for a tone.</summary>
    private static int[] Speaker(int count, byte portB)
    {
        var timer = new IntervalTimer();
        timer.Write(0x43, 0xB6); // counter 2, both bytes, a square wave
        timer.Write(0x42, (byte)count);
        timer.Write(0x42, (byte)(count >> 8));
        timer.Gate2 = (portB & 1) != 0;
        var speaker = new PcSpeaker(timer) { PortB = portB };
        var mix = new int[2 * Machine.SampleRate / 10];
        speaker.Render(mix);
        return mix;
    }

    [Fact]
    public void The_speaker_plays_counter_2s_square_wave_when_both_bits_are_on()
    {
        // 1,193,182 Hz over 1,193 is 1 kHz: a hundred cycles in a tenth of a second, each
        // crossing zero twice
        var mix = Speaker(1193, 0x03);
        int crossings = 0;
        for (int i = 2; i < mix.Length; i += 2)
        {
            if ((mix[i - 2] < 0) != (mix[i] < 0))
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 195, 205);
        Assert.True(mix.Max() > 3000, $"the tone is too quiet: {mix.Max()}");
    }

    [Fact]
    public void The_speaker_is_silent_without_its_data_bit()
    {
        Assert.All(Speaker(1193, 0x01), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void A_cone_held_out_settles_back_to_silence()
    {
        // the gate off holds counter 2's output high, so the data bit alone moves the cone, once
        var mix = Speaker(1193, 0x02);
        Assert.True(mix.Take(20).Max() > 3000);
        Assert.True(Math.Abs(mix[^1]) < 100, $"still at {mix[^1]}");
    }

    [Fact]
    public void Pulses_of_varying_width_play_as_the_sound_they_carry()
    {
        // as Access's RealSound plays sampled sound: counter 2 in mode 0, reloaded 16,572 times a
        // second, every 72 ticks, the speaker on for what's left of the 72 after the count; here
        // the counts carry a 500 Hz sine, for a tenth of a second
        var timer = new IntervalTimer();
        var speaker = new PcSpeaker(timer) { PortB = 0x03 };
        timer.Gate2 = true;
        timer.Write(0x43, 0x90); // counter 2, low byte only, mode 0
        const double TicksPerSample = IntervalTimer.Frequency / Machine.SampleRate;
        var mix = new List<int>();
        long made = 0;
        for (long tick = 0; tick < IntervalTimer.Frequency / 10; tick += 72)
        {
            // the sound is made up to the last whole sample, the speaker settled, then the count
            // changed, as the machine does on each write
            long due = (long)(tick / TicksPerSample);
            var frames = new int[2 * (due - made)];
            speaker.Render(frames);
            mix.AddRange(frames);
            made = due;
            timer.Now = tick;
            speaker.Settle(tick);
            timer.Write(0x42, (byte)(36 + (30 * Math.Sin(2 * Math.PI * 500 * tick / IntervalTimer.Frequency))));
        }

        // the left channel, past the filters' first moments
        var left = mix.Where((_, i) => i % 2 == 0).Skip(400).Select(v => (double)v).ToArray();
        double Power(double hz) => Math.Pow(left.Select((v, i) => v * Math.Cos(2 * Math.PI * hz * i / Machine.SampleRate)).Sum(), 2)
            + Math.Pow(left.Select((v, i) => v * Math.Sin(2 * Math.PI * hz * i / Machine.SampleRate)).Sum(), 2);

        // the 500 Hz comes through far stronger than the 16,572 Hz the pulses come at
        Assert.True(Power(500) > 20 * Power(16_572), $"500 Hz: {Power(500):E2}, 16,572 Hz: {Power(16_572):E2}");
    }

    [Fact]
    public void The_adlib_detection_sequence_finds_the_opl()
    {
        // the sequence AdLib's own documentation gave programs, polling port 388h
        var opl = new Opl(Machine.SampleRate);
        void Set(int register, byte value, double now)
        {
            opl.Write(0x388, (byte)register, now);
            opl.Write(0x389, value, now);
        }

        Set(4, 0x60, 0); // mask and stop both timers
        Set(4, 0x80, 0); // clear their flags
        Assert.Equal(0, opl.Read(0x388, 0) & 0xE0);

        Set(2, 0xFF, 0); // timer 1 one step from the top: 80 microseconds
        Set(4, 0x21, 0); // start timer 1, timer 2 masked
        Assert.Equal(0, opl.Read(0x388, 0.00005) & 0xE0);
        Assert.Equal(0xC0, opl.Read(0x388, 0.0001) & 0xE0);

        Set(4, 0x60, 0.0001);
        Set(4, 0x80, 0.0001);
        Assert.Equal(0, opl.Read(0x388, 0.0002) & 0xE0);
    }

    [Fact]
    public void Timer_2_counts_in_steps_of_320_microseconds()
    {
        var opl = new Opl(Machine.SampleRate);
        opl.Write(0x388, 3, 0);
        opl.Write(0x389, 256 - 10, 0);
        opl.Write(0x388, 4, 0);
        opl.Write(0x389, 0x02, 0);

        Assert.Equal(0, opl.Read(0x388, 0.0031) & 0xE0);
        Assert.Equal(0xA0, opl.Read(0x388, 0.0033) & 0xE0);
    }

    [Fact]
    public void A_keyed_voice_sounds_and_silence_is_silent()
    {
        var opl = new Opl(Machine.SampleRate);
        var mix = new int[2 * 4410];
        opl.Render(mix);
        Assert.All(mix, sample => Assert.Equal(0, sample));

        // voice 0: both operators a plain sine at full level, quick attack, held, then key on at
        // about 440 Hz (f-number 241h in block 4)
        void Set(int register, byte value)
        {
            opl.Write(0x388, (byte)register, 0);
            opl.Write(0x389, value, 0);
        }

        Set(0x20, 0x01);
        Set(0x23, 0x01);
        Set(0x40, 0x10);
        Set(0x43, 0x00);
        Set(0x60, 0xF0);
        Set(0x63, 0xF0);
        Set(0x80, 0x77);
        Set(0x83, 0x77);
        Set(0xA0, 0x41);
        Set(0xB0, 0x32);

        opl.Render(mix);
        double rms = Math.Sqrt(mix.Average(sample => (double)sample * sample));
        Assert.True(rms > 500, $"the voice is too quiet: {rms:F0}");

        // in OPL2 mode the voice is mono: the same on both sides
        for (int i = 0; i < mix.Length; i += 2)
        {
            Assert.Equal(mix[i], mix[i + 1]);
        }
    }
}
