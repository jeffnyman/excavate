using Excavate.Core.Devices;

namespace Excavate.Core.Tests;

/// <summary>The VGA on its own: memory, palette and timing, without a program.</summary>
public class VgaTests
{
    [Fact]
    public void Mode_13h_is_a_byte_per_pixel_through_the_palette()
    {
        var vga = new Vga();
        vga.SetMode(0x13, clear: true);

        // color 1 to bright red, through the DAC's ports: index, then red, green and blue
        vga.Out(0x3C8, 1);
        vga.Out(0x3C9, 63);
        vga.Out(0x3C9, 0);
        vga.Out(0x3C9, 0);
        vga.Write(0xA_0000 + (10 * 320) + 5, 1);

        Assert.Equal((320, 200), vga.Size);
        var pixels = new uint[320 * 200];
        vga.Render(pixels);
        Assert.Equal(0xFFFF_0000u, pixels[(10 * 320) + 5]);
        Assert.Equal(0xFF00_0000u, pixels[(10 * 320) + 6]);
    }

    [Fact]
    public void Mode_X_writes_reach_the_planes_the_map_mask_picks()
    {
        var vga = new Vga();
        vga.SetMode(0x13, clear: true);

        // unchain: chain 4 off in the sequencer's memory mode, then write to plane 1 only
        vga.Out(0x3C4, 4);
        vga.Out(0x3C5, 0x06);
        vga.Out(0x3C4, 2);
        vga.Out(0x3C5, 0x02);
        vga.Write(0xA_0000, 15);

        var pixels = new uint[320 * 200];
        vga.Render(pixels);

        // each address holds four neighboring pixels, one per plane: plane 1 is the second
        Assert.Equal(0xFF00_0000u, pixels[0]);
        Assert.Equal(0xFFFF_FFFFu, pixels[1]);
        Assert.Equal(0xFF00_0000u, pixels[2]);
    }

    [Fact]
    public void Latches_copy_four_pixels_with_one_read_and_one_write()
    {
        var vga = new Vga();
        vga.SetMode(0x13, clear: true);
        vga.Out(0x3C4, 4);
        vga.Out(0x3C5, 0x06);

        // four planes of different colors at address 0
        for (int plane = 0; plane < 4; plane++)
        {
            vga.Out(0x3C4, 2);
            vga.Out(0x3C5, (byte)(1 << plane));
            vga.Write(0xA_0000, (byte)(plane + 1));
        }

        // write mode 1: the read loads the latches, the write stores them
        vga.Out(0x3C4, 2);
        vga.Out(0x3C5, 0x0F);
        vga.Out(0x3CE, 5);
        vga.Out(0x3CF, 0x01);
        vga.Read(0xA_0000);
        vga.Write(0xA_0001, 0);

        vga.Out(0x3CE, 5);
        vga.Out(0x3CF, 0x00);
        for (int plane = 0; plane < 4; plane++)
        {
            vga.Out(0x3CE, 4);
            vga.Out(0x3CF, (byte)plane);
            Assert.Equal(plane + 1, vga.Read(0xA_0001));
        }
    }

    [Fact]
    public void The_vertical_retrace_comes_once_a_frame()
    {
        var vga = new Vga();
        double line = 1 / Vga.FrameRate / 449;

        vga.Time = 10 * line;
        Assert.Equal(0, vga.In(0x3DA) & 0x08);
        vga.Time = 412.5 * line;
        Assert.Equal(0x08, vga.In(0x3DA) & 0x08);
        vga.Time = (1 / Vga.FrameRate) + (412.5 * line);
        Assert.Equal(0x08, vga.In(0x3DA) & 0x08);
    }
}
