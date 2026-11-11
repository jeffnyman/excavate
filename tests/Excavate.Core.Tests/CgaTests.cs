using Excavate.Core.Devices;

namespace Excavate.Core.Tests;

/// <summary>The CGA, on a machine built with one: its memory, registers, BIOS calls and pictures.</summary>
public sealed class CgaTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    /// <summary>A CGA machine with a program run that sets a mode and exits.</summary>
    private Machine InMode(byte mode, params byte[] more)
    {
        // mov ax, 00xxh; int 10h; (more); mov ax, 4C00h; int 21h
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), DosTests.Exe([0xB8, mode, 0x00, 0xCD, 0x10, .. more, 0xB8, 0x00, 0x4C, 0xCD, 0x21]));
        var machine = new Machine(_game, _saves, VideoAdapter.Cga);
        machine.Load("TEST.EXE");
        machine.Run(100_000);
        Assert.True(machine.Exited);
        return machine;
    }

    private static uint[] Picture(Machine machine)
    {
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        return pixels;
    }

    // a pixel of a 320 by 200 mode, as drawn: two across and two down in the 640 by 400 picture
    private static uint At(uint[] picture, int x, int y) => picture[(y * 2 * 640) + (x * 2)] & 0xFFFFFF;

    [Fact]
    public void The_picture_is_640_by_400_at_sixty_frames_a_second()
    {
        var machine = InMode(4);
        Assert.Equal(VideoAdapter.Cga, machine.Video);
        Assert.Equal(4, machine.VideoMode);
        Assert.Equal((640, 400), (machine.DisplayWidth, machine.DisplayHeight));
        Assert.Equal(59.92, machine.FrameRate, 2);
    }

    [Fact]
    public void Mode_4_draws_four_pixels_a_byte_from_the_palette_the_color_select_register_picks()
    {
        var machine = InMode(4);

        // the first line's first byte: background, then colors 1, 2 and 3; the second line is
        // in the odd lines' bank, at BA000h
        machine.Write(0xB_8000, 0b00_01_10_11);
        machine.Write(0xB_A000, 0b11_00_00_00);

        // the BIOS leaves palette 1 with intensity: bright cyan, magenta and white
        var picture = Picture(machine);
        Assert.Equal(new uint[] { 0x000000u, 0x55FFFF, 0xFF55FF, 0xFFFFFF }, new[] { At(picture, 0, 0), At(picture, 1, 0), At(picture, 2, 0), At(picture, 3, 0) });
        Assert.Equal(0xFFFFFFu, At(picture, 0, 1));

        // without intensity, as Portal sets it: cyan, magenta and light gray
        machine.Out(0x3D9, 0x20);
        picture = Picture(machine);
        Assert.Equal(new uint[] { 0x00AAAAu, 0xAA00AA, 0xAAAAAA }, new[] { At(picture, 1, 0), At(picture, 2, 0), At(picture, 3, 0) });

        // palette 0 with a blue background: green, red and brown
        machine.Out(0x3D9, 0x01);
        picture = Picture(machine);
        Assert.Equal(new uint[] { 0x0000AAu, 0x00AA00, 0xAA0000, 0xAA5500 }, new[] { At(picture, 0, 0), At(picture, 1, 0), At(picture, 2, 0), At(picture, 3, 0) });
    }

    [Fact]
    public void The_bios_palette_call_picks_the_palette_and_the_background()
    {
        // mov ax, 0B00h; mov bx, 0100h (palette 0); int 10h; mov bx, 0004h (red background); int 10h
        var machine = InMode(4, 0xB8, 0x00, 0x0B, 0xBB, 0x00, 0x01, 0xCD, 0x10, 0xBB, 0x04, 0x00, 0xCD, 0x10);
        machine.Write(0xB_8000, 0b00_01_00_00);
        // BL's bit 4 is the palette's intensity, cleared by the second call
        var picture = Picture(machine);
        Assert.Equal(0xAA0000u, At(picture, 0, 0));
        Assert.Equal(0x00AA00u, At(picture, 1, 0));
    }

    [Fact]
    public void Mode_6_draws_eight_pixels_a_byte_in_the_color_select_registers_color()
    {
        var machine = InMode(6);
        machine.Write(0xB_8000, 0b1000_0001);
        var picture = Picture(machine);
        Assert.Equal(0xFFFFFFu, picture[0] & 0xFFFFFF);
        Assert.Equal(0x000000u, picture[1] & 0xFFFFFF);
        Assert.Equal(0xFFFFFFu, picture[7] & 0xFFFFFF);
    }

    [Fact]
    public void The_memory_is_16_KB_at_B8000h_seen_twice_with_nothing_below()
    {
        var machine = InMode(4);
        machine.Write(0xB_8123, 0x42);
        Assert.Equal(0x42, machine.Read(0xB_C123));
        machine.Write(0xA_0000, 0x42);
        Assert.Equal(0xFF, machine.Read(0xA_0000));
    }

    [Fact]
    public void A_composite_monitor_turns_a_pattern_of_dots_into_a_color_of_its_own()
    {
        var machine = InMode(4);

        // a line of alternating cyan and magenta pixels, as Portal fills its backgrounds
        for (uint x = 0; x < 80; x++)
        {
            machine.Write(0xB_8000 + x, 0b01_10_01_10);
        }

        var rgb = Picture(machine);
        Assert.Equal(0x55FFFFu, At(rgb, 40, 0));
        Assert.Equal(0xFF55FFu, At(rgb, 41, 0));

        // on a composite monitor the stripes are one even color, which none of the RGBI 16 is
        machine.Composite = true;
        var composite = Picture(machine);
        static (int R, int G, int B) Rgb(uint color) => ((int)(color >> 16) & 0xFF, (int)(color >> 8) & 0xFF, (int)color & 0xFF);
        var color = Rgb(composite[320]);
        for (int x = 300; x < 340; x++)
        {
            var other = Rgb(composite[x]);
            Assert.True(Math.Abs(other.R - color.R) <= 2 && Math.Abs(other.G - color.G) <= 2 && Math.Abs(other.B - color.B) <= 2, $"{other} isn't {color}");
        }

        uint[] rgbi = [0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF];
        Assert.DoesNotContain(composite[320] & 0xFFFFFF, rgbi);
    }

    [Fact]
    public void The_status_register_shows_the_vertical_retrace_once_a_frame()
    {
        var cga = new Cga();
        double frame = 1 / Cga.FrameRate;
        cga.Time = 0;
        Assert.Equal(0, cga.In(0x3DA) & 0x08);
        cga.Time = frame * 230 / 262;
        Assert.Equal(0x09, cga.In(0x3DA) & 0x09);
        cga.Time = frame * (1 + (230.0 / 262));
        Assert.Equal(0x08, cga.In(0x3DA) & 0x08);
    }

    [Fact]
    public void The_bios_keeps_the_fonts_first_half_where_ibms_does()
    {
        var machine = new Machine(_game, _saves, VideoAdapter.Cga);

        // the A: 30h 78h CCh CCh FCh CCh CCh 00h
        Assert.Equal([0x30, 0x78, 0xCC, 0xCC, 0xFC, 0xCC, 0xCC, 0x00], Enumerable.Range(0, 8).Select(i => machine.Read(0xF_FA6E + ('A' * 8) + (uint)i)));
    }
}
