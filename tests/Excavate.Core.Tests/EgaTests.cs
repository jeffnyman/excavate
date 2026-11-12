namespace Excavate.Core.Tests;

/// <summary>
/// The EGA's graphics and the CGA's, as the VGA and its BIOS give them: what programs check to
/// find an EGA, pixels, characters and the palette in the 16-color modes, and the CGA's modes.
/// </summary>
public sealed class EgaTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("excavate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    private Machine Run(byte[] code, VideoAdapter video = VideoAdapter.Vga)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), DosTests.Exe(code));
        var machine = new Machine(_game, _saves, video);
        machine.Load("TEST.EXE");
        machine.Run(2_000_000);
        Assert.True(machine.Exited);
        return machine;
    }

    private static uint[] Picture(Machine machine)
    {
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        return pixels;
    }

    private static uint Pixel(Machine machine, int x, int y) => Picture(machine)[(y * machine.DisplayWidth) + x] & 0xFF_FFFF;

    [Theory]
    [InlineData(VideoAdapter.Vga, 0x60)]
    [InlineData(VideoAdapter.Cga, 0x00)]
    public void An_EGA_is_found_by_its_byte_in_the_BIOS_data(VideoAdapter video, int found)
    {
        // mov ax, 4C00h; int 21h
        var machine = Run([0xB8, 0x00, 0x4C, 0xCD, 0x21], video);
        Assert.Equal(found, machine.Read(0x487));
    }

    [Fact]
    public void A_pixel_is_written_and_read_back_in_mode_10h()
    {
        var machine = Run(
        [
            // mode 10h: mov ax, 0010h; int 10h
            0xB8, 0x10, 0x00, 0xCD, 0x10,

            // yellow at column 5, row 3: mov ax, 0C0Eh; mov bh, 0; mov cx, 5; mov dx, 3; int 10h
            0xB8, 0x0E, 0x0C, 0xB7, 0x00, 0xB9, 0x05, 0x00, 0xBA, 0x03, 0x00, 0xCD, 0x10,

            // read it back and exit with it: mov ah, 0Dh; int 10h; mov ah, 4Ch; int 21h
            0xB4, 0x0D, 0xCD, 0x10, 0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0x0E, machine.ExitCode);
        Assert.Equal((640, 350), (machine.DisplayWidth, machine.DisplayHeight));
        Assert.Equal(0xFFFF55u, Pixel(machine, 5, 3));
        Assert.Equal(0x000000u, Pixel(machine, 6, 3));
    }

    [Fact]
    public void A_character_in_mode_10h_is_14_lines_high_and_a_palette_register_recolors_it()
    {
        var machine = Run(
        [
            0xB8, 0x10, 0x00, 0xCD, 0x10,

            // a full block in color 1: mov ax, 0EDBh; mov bx, 0001h; int 10h
            0xB8, 0xDB, 0x0E, 0xBB, 0x01, 0x00, 0xCD, 0x10,

            // color 1 to bright red (3Ch): mov ax, 1000h; mov bx, 3C01h; int 10h
            0xB8, 0x00, 0x10, 0xBB, 0x01, 0x3C, 0xCD, 0x10,

            // read the register back and exit with it: mov ax, 1007h; mov bl, 1; int 10h;
            // mov al, bh; mov ah, 4Ch; int 21h
            0xB8, 0x07, 0x10, 0xB3, 0x01, 0xCD, 0x10, 0x88, 0xF8, 0xB4, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(0x3C, machine.ExitCode);
        Assert.Equal(0xFF5555u, Pixel(machine, 0, 0));
        Assert.Equal(0xFF5555u, Pixel(machine, 7, 13));
        Assert.Equal(0x000000u, Pixel(machine, 0, 14));
        Assert.Equal(1, machine.Read(0x450));
    }

    [Theory]
    [InlineData(0x00, 0xFFFF55)]  // the first palette: green, red and brown, bright, so yellow
    [InlineData(0x01, 0xFFFFFF)]  // the second, the default: cyan, magenta and white
    public void The_CGAs_mode_4_on_the_VGA_takes_its_palette_from_the_BIOS(int palette, uint color3)
    {
        var machine = Run(
        [
            // mode 4: mov ax, 0004h; int 10h
            0xB8, 0x04, 0x00, 0xCD, 0x10,

            // the palette: mov ah, 0Bh; mov bh, 1; mov bl, palette; int 10h
            0xB4, 0x0B, 0xB7, 0x01, 0xB3, (byte)palette, 0xCD, 0x10,

            // color 3 at the top left: mov ax, 0C03h; mov cx, 0; mov dx, 0; int 10h
            0xB8, 0x03, 0x0C, 0xB9, 0x00, 0x00, 0xBA, 0x00, 0x00, 0xCD, 0x10,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal((320, 200), (machine.DisplayWidth, machine.DisplayHeight));
        Assert.Equal(0xC0, machine.Read(0xB_8000));
        Assert.Equal(color3, Pixel(machine, 0, 0));
    }

    [Fact]
    public void A_vector_set_to_the_BIOSs_own_IRET_returns()
    {
        var machine = Run(
        [
            // INT 1Ch to F000:FF53: mov ax, 0F000h; mov ds, ax; mov dx, 0FF53h; mov ax, 251Ch;
            // int 21h; then int 1Ch, and exit with 7: mov ax, 4C07h; int 21h
            0xB8, 0x00, 0xF0, 0x8E, 0xD8, 0xBA, 0x53, 0xFF, 0xB8, 0x1C, 0x25, 0xCD, 0x21,
            0xCD, 0x1C, 0xB8, 0x07, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(7, machine.ExitCode);
    }
}
