using Excavate.Core.Sound;
using Excavate.Core.Sound.Mt32;

namespace Excavate.Core.Tests;

/// <summary>The MPU-401 and the MT-32 on their own, without a program.</summary>
public class MidiTests
{
    /// <summary>What came out of the MIDI cable, message by message, as hex.</summary>
    private sealed class Cable : IMidiOut
    {
        public List<string> Messages { get; } = [];

        public void Message(uint message) => Messages.Add($"{message:X6}");

        public void SysEx(ReadOnlySpan<byte> message) => Messages.Add(Convert.ToHexString(message));
    }

    private static void Bytes(Mpu401 mpu, params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            mpu.Write(0x330, value);
        }
    }

    [Fact]
    public void A_reset_and_uart_mode_are_acknowledged()
    {
        var mpu = new Mpu401();
        Assert.Equal(0x80, mpu.Read(0x331) & 0xC0); // ready, nothing to read

        mpu.Write(0x331, 0xFF);
        Assert.Equal(0x00, mpu.Read(0x331) & 0xC0); // a byte to read
        Assert.Equal(0xFE, mpu.Read(0x330));
        Assert.Equal(0x80, mpu.Read(0x331) & 0xC0);

        mpu.Write(0x331, 0x3F);
        Assert.Equal(0xFE, mpu.Read(0x330));
        Assert.True(mpu.Uart);

        // in UART mode a reset is obeyed but not acknowledged
        mpu.Write(0x331, 0xFF);
        Assert.False(mpu.Uart);
        Assert.Equal(0x80, mpu.Read(0x331) & 0xC0);
    }

    [Fact]
    public void Uart_mode_gathers_bytes_into_messages()
    {
        var cable = new Cable();
        var mpu = new Mpu401 { Output = cable };
        mpu.Write(0x331, 0x3F);

        // a note on, two more by running status, a program change (one data byte), a real-time
        // clock in the middle of a message, and a system exclusive message
        Bytes(mpu, 0x90, 0x3C, 0x64, 0x40, 0x64, 0x43, 0x00);
        Bytes(mpu, 0xC1, 0x05);
        Bytes(mpu, 0xB0, 0x07, 0xF8, 0x7F);
        Bytes(mpu, 0xF0, 0x41, 0x10, 0x16, 0x12, 0x01, 0xF7);

        Assert.Equal(["643C90", "644090", "004390", "0005C1", "7F07B0", "F041101612" + "01F7"], cable.Messages);
    }

    [Fact]
    public void Intelligent_mode_sends_one_message_after_want_to_send()
    {
        var cable = new Cable();
        var mpu = new Mpu401 { Output = cable };

        // without a "want to send", data in intelligent mode goes nowhere
        Bytes(mpu, 0x90, 0x3C, 0x64);
        Assert.Empty(cable.Messages);

        // "want to send a system message", as Wing Commander's Roland driver does
        mpu.Write(0x331, 0xDF);
        Assert.Equal(0xFE, mpu.Read(0x330));
        Bytes(mpu, 0xF0, 0x41, 0x10, 0x16, 0x12, 0x01, 0xF7);

        // "want to send data" for track 0: one channel message
        mpu.Write(0x331, 0xD0);
        Bytes(mpu, 0x91, 0x3C, 0x64);

        // and then it's over: the next bytes go nowhere
        Bytes(mpu, 0x91, 0x40, 0x64);

        Assert.Equal(["F04110161201F7", "643C91"], cable.Messages);
    }

    [Fact]
    public void The_mt32_shows_a_message_and_plays_a_note()
    {
        if (TestData.Mt32Roms() is not { } folder)
        {
            return;
        }

        var lines = new List<string>();
        var mt32 = new Mt32Module(Mt32RomSet.FromDirectory(folder)!, Machine.SampleRate, lines.Add);
        var mix = new int[2 * 4410];

        // it shows its banner as it starts, and is silent
        Assert.Contains("Roland", mt32.Display);
        mt32.Render(mix);
        Assert.All(mix, sample => Assert.Equal(0, sample));

        // a message for the display: Roland data set 1 to address 20 00 00, with its checksum
        byte[] text = "Excavate            "u8.ToArray();
        byte[] body = [0x20, 0x00, 0x00, .. text];
        byte[] sysEx = [0xF0, 0x41, 0x10, 0x16, 0x12, .. body, Synth.CalcSysexChecksum(body), 0xF7];
        mt32.SysEx(sysEx);
        mt32.Render(mix); // the display is brought up to date as the synth runs
        Assert.Equal("Excavate            ", mt32.Display);
        Assert.Contains(lines, line => line.Contains("Excavate"));

        // a note on part 1 (channel 2), the piano it starts with
        mt32.Message(0x7F3C91);
        mt32.Render(mix);
        double rms = Math.Sqrt(mix.Average(sample => (double)sample * sample));
        Assert.True(rms > 200, $"the note is too quiet: {rms:F0}");
    }
}
