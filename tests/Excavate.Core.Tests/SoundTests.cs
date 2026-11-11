using Excavate.Core.Sound;

namespace Excavate.Core.Tests;

/// <summary>The sound devices on their own, without a program.</summary>
public class SoundTests
{
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
