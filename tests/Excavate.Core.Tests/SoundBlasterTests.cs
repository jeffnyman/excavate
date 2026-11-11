using Excavate.Core.Devices;
using Excavate.Core.Sound;

namespace Excavate.Core.Tests;

/// <summary>The DMA controller and the Sound Blaster's DSP on their own, on a megabyte of memory.</summary>
public class SoundBlasterTests
{
    private readonly byte[] _memory = new byte[0x10_0000];
    private readonly DmaController _dma;
    private readonly DmaController _wideDma;
    private readonly SoundBlaster _card;
    private int _interrupts;

    public SoundBlasterTests()
    {
        _dma = new DmaController(address => _memory[address & 0xF_FFFF], (address, value) => _memory[address & 0xF_FFFF] = value);
        _wideDma = new DmaController(address => _memory[address & 0xF_FFFF], (address, value) => _memory[address & 0xF_FFFF] = value, wide: true);
        _card = new SoundBlaster(_dma, 1, Machine.SampleRate, () => _interrupts++, wideDma: _wideDma, wideChannel: 1);
    }

    /// <summary>
    /// Sets channel 5 up, the second controller's channel 1, as channel 1 is, through its own
    /// ports: the address and count in words, the page at 8Bh.
    /// </summary>
    private void SetUpChannel5(uint address, int words)
    {
        _wideDma.Out(0xD4, 0x05);
        _wideDma.Out(0xD8, 0);
        _wideDma.Out(0xD6, 0x49);
        _wideDma.Out(0xC4, (byte)(address >> 1));
        _wideDma.Out(0xC4, (byte)(address >> 9));
        _wideDma.Out(0xC6, (byte)(words - 1));
        _wideDma.Out(0xC6, (byte)((words - 1) >> 8));
        _wideDma.Out(0x8B, (byte)(address >> 16));
        _wideDma.Out(0xD4, 0x01);
    }

    /// <summary>Sets channel 1 up on a buffer, as a program does: masked, flip-flop cleared, mode, address, count, page, unmasked.</summary>
    private void SetUpChannel1(uint address, int length, bool autoInitialize = false)
    {
        _dma.Out(0x0A, 0x05);
        _dma.Out(0x0C, 0);
        _dma.Out(0x0B, (byte)(0x49 | (autoInitialize ? 0x10 : 0))); // single, read from memory, channel 1
        _dma.Out(0x02, (byte)address);
        _dma.Out(0x02, (byte)(address >> 8));
        _dma.Out(0x03, (byte)(length - 1));
        _dma.Out(0x03, (byte)((length - 1) >> 8));
        _dma.Out(0x83, (byte)(address >> 16));
        _dma.Out(0x0A, 0x01);
    }

    private void Dsp(params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            _card.Write(0x22C, value);
        }
    }

    private byte Answer()
    {
        Assert.Equal(0x80, _card.Read(0x22E) & 0x80);
        return _card.Read(0x22A);
    }

    [Fact]
    public void A_channel_moves_its_bytes_and_stops_at_its_terminal_count()
    {
        _memory[0x1_2340] = 0x11;
        _memory[0x1_2341] = 0x22;
        _memory[0x1_2342] = 0x33;
        SetUpChannel1(0x1_2340, 3);
        Assert.True(_dma.Ready(1));

        Assert.True(_dma.Read(1, out byte first));
        Assert.True(_dma.Read(1, out byte second));

        // the current address and count read back a byte at a time, low first
        _dma.Out(0x0C, 0);
        Assert.Equal(0x42, _dma.In(0x02));
        Assert.Equal(0x23, _dma.In(0x02));
        Assert.Equal(0x00, _dma.In(0x03));
        Assert.Equal(0x00, _dma.In(0x03));

        Assert.True(_dma.Read(1, out byte third));
        Assert.Equal([0x11, 0x22, 0x33], new[] { first, second, third });

        // the terminal count: masked, and shown once in the status
        Assert.False(_dma.Ready(1));
        Assert.False(_dma.Read(1, out _));
        Assert.Equal(0x02, _dma.In(0x08) & 0x0F);
        Assert.Equal(0x00, _dma.In(0x08) & 0x0F);
    }

    [Fact]
    public void An_auto_initializing_channel_starts_over()
    {
        _memory[0x8000] = 1;
        _memory[0x8001] = 2;
        SetUpChannel1(0x8000, 2, autoInitialize: true);

        var bytes = new List<byte>();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(_dma.Read(1, out byte value));
            bytes.Add(value);
        }

        Assert.Equal([1, 2, 1, 2, 1], bytes);
        Assert.True(_dma.Ready(1));
    }

    [Fact]
    public void A_transfer_wraps_within_its_64k_page()
    {
        _memory[0x2_FFFF] = 0xAA;
        _memory[0x2_0000] = 0xBB;
        SetUpChannel1(0x2_FFFF, 2);
        Assert.True(_dma.Read(1, out byte last));
        Assert.True(_dma.Read(1, out byte wrapped));
        Assert.Equal((0xAA, 0xBB), (last, wrapped));
    }

    [Fact]
    public void A_word_channel_moves_words_and_wraps_within_its_128k_page()
    {
        // the last word of the page that starts at 2_0000h (page 3's lowest bit is left out), and
        // the first, where the address wraps
        _memory[0x3_FFFE] = 0x34;
        _memory[0x3_FFFF] = 0x12;
        _memory[0x2_0000] = 0x78;
        _memory[0x2_0001] = 0x56;
        SetUpChannel5(0x3_FFFE, 2);

        Assert.True(_wideDma.ReadWord(1, out ushort last));
        Assert.True(_wideDma.ReadWord(1, out ushort wrapped));
        Assert.Equal((0x1234, 0x5678), (last, wrapped));

        // two words was the count: the terminal count, shown in the second controller's status
        Assert.False(_wideDma.Ready(1));
        Assert.Equal(0x02, _wideDma.In(0xD0) & 0x0F);
    }

    [Fact]
    public void A_16_bit_block_plays_signed_samples_and_its_interrupt_is_acknowledged_apart()
    {
        // 1,000 signed samples, a square wave at half swing, at 10,000 Hz (41h, high byte first)
        for (int i = 0; i < 1000; i++)
        {
            short sample = (short)((i / 10) % 2 == 0 ? 0x4000 : -0x4000);
            _memory[0x6000 + (2 * i)] = (byte)sample;
            _memory[0x6000 + (2 * i) + 1] = (byte)(sample >> 8);
        }

        SetUpChannel5(0x6000, 1000);
        Dsp(0x41, 0x27, 0x10, 0xB0, 0x10, (1000 - 1) & 0xFF, (1000 - 1) >> 8);
        Assert.True(_card.Playing);
        Assert.InRange(_card.FramesUntilInterrupt, 4409, 4412);

        var mix = new int[2 * 4420];
        _card.Render(mix);
        Assert.Equal(1, _interrupts);
        Assert.False(_card.Playing);
        Assert.InRange(mix.Take(4000).Max(), 0x3F00, 0x4000);
        Assert.InRange(mix.Take(4000).Min(), -0x4000, -0x3F00);

        // the 16-bit interrupt is pending, bit 1 of the mixer's 82h; reading 22Eh doesn't
        // acknowledge it, 22Fh does
        _card.Write(0x224, 0x82);
        Assert.Equal(0x02, _card.Read(0x225) & 0x03);
        _card.Read(0x22E);
        Assert.Equal(0x02, _card.Read(0x225) & 0x03);
        _card.Read(0x22F);
        Assert.Equal(0x00, _card.Read(0x225) & 0x03);
    }

    [Fact]
    public void The_dsp_resets_and_answers_its_version()
    {
        Assert.Equal(0, _card.Read(0x22E) & 0x80);
        _card.Write(0x226, 1);
        _card.Write(0x226, 0);
        Assert.Equal(0xAA, Answer());
        Assert.Equal(0, _card.Read(0x22E) & 0x80);

        // the version, an SB16's 4.05; identification inverts its argument
        Dsp(0xE1);
        Assert.Equal(4, Answer());
        Assert.Equal(5, Answer());
        Dsp(0xE0, 0x5A);
        Assert.Equal(0xA5, Answer());

        // ready for commands; the mixer reports IRQ 5 and DMA 1
        Assert.Equal(0, _card.Read(0x22C) & 0x80);
        _card.Write(0x224, 0x80);
        Assert.Equal(0x02, _card.Read(0x225));
        _card.Write(0x224, 0x81);
        Assert.Equal(0x22, _card.Read(0x225));
    }

    [Fact]
    public void A_forced_interrupt_comes_at_once()
    {
        Dsp(0xF2);
        Assert.Equal(1, _interrupts);
        _card.Write(0x224, 0x82);
        Assert.Equal(0x01, _card.Read(0x225) & 0x01);

        // reading the status port acknowledges it
        _card.Read(0x22E);
        Assert.Equal(0x00, _card.Read(0x225) & 0x01);
    }

    [Fact]
    public void A_single_cycle_block_plays_and_interrupts_on_its_last_sample()
    {
        // a block of 1,000 samples, a square wave at full swing, at 10,000 Hz (time constant 156)
        for (int i = 0; i < 1000; i++)
        {
            _memory[0x3000 + i] = (byte)((i / 10) % 2 == 0 ? 0xF0 : 0x10);
        }

        SetUpChannel1(0x3000, 1000);
        Dsp(0x40, 156, 0x14, (1000 - 1) & 0xFF, (1000 - 1) >> 8);
        Assert.True(_card.Playing);

        // a tenth of a second at 44,100 Hz: the interrupt is due 4,410 frames on, give or take one
        int frames = _card.FramesUntilInterrupt;
        Assert.InRange(frames, 4409, 4412);

        var mix = new int[2 * (frames - 1)];
        _card.Render(mix);
        Assert.Equal(0, _interrupts);
        Assert.True(_card.Playing);
        double rms = Math.Sqrt(mix.Average(sample => (double)sample * sample));
        Assert.True(rms > 20_000, $"the block is too quiet: {rms:F0}");

        // the next frame takes the last byte: the interrupt, and the block is over
        _card.Render(new int[2]);
        Assert.Equal(1, _interrupts);
        Assert.False(_card.Playing);
        Assert.Equal(-1, _card.FramesUntilInterrupt);
        Assert.False(_dma.Ready(1));
    }

    [Fact]
    public void An_auto_initialize_block_interrupts_at_each_end_until_told_to_stop()
    {
        SetUpChannel1(0x4000, 200, autoInitialize: true);
        Dsp(0x40, 156, 0x48, 99, 0, 0x1C);

        // a little over 1,000 samples: ten blocks of 100 (the first sample is taken a step in)
        var mix = new int[2 * 4420];
        _card.Render(mix);
        Assert.Equal(10, _interrupts);
        Assert.True(_card.Playing);

        // exit auto-initialize: the block playing is the last
        Dsp(0xDA);
        _card.Render(mix);
        Assert.Equal(11, _interrupts);
        Assert.False(_card.Playing);
    }

    [Fact]
    public void A_masked_channel_holds_the_dsp_until_it_is_unmasked()
    {
        SetUpChannel1(0x5000, 100);
        _dma.Out(0x0A, 0x05);
        Dsp(0x40, 156, 0x14, 99, 0);

        Assert.Equal(-1, _card.FramesUntilInterrupt);
        _card.Render(new int[2 * 4410]);
        Assert.Equal(0, _interrupts);

        // a hundred samples from wherever between two samples the output had got to
        _dma.Out(0x0A, 0x01);
        Assert.InRange(_card.FramesUntilInterrupt, 437, 443);
    }

    [Fact]
    public void Direct_output_sets_the_level_at_once()
    {
        Dsp(0x10, 0xC0);
        var mix = new int[2 * 10];
        _card.Render(mix);
        Assert.All(mix, sample => Assert.Equal(0x40 << 8, sample));
    }
}
