using Excavate.Core.Devices;

namespace Excavate.Core.Tests;

/// <summary>The keyboard's scan codes and the game port, without a program.</summary>
public class InputTests
{
    [Theory]
    [InlineData(0x04, 0x1E, false)] // A
    [InlineData(0x1D, 0x2C, false)] // Z
    [InlineData(0x27, 0x0B, false)] // 0
    [InlineData(0x29, 0x01, false)] // Esc
    [InlineData(0x2C, 0x39, false)] // Space
    [InlineData(0x38, 0x35, false)] // /
    [InlineData(0x43, 0x44, false)] // F10
    [InlineData(0x45, 0x58, false)] // F12
    [InlineData(0x52, 0x48, true)] // the up arrow, in the AT's own block
    [InlineData(0x60, 0x48, false)] // the keypad's 8, the same key on the XT's keyboard
    [InlineData(0x58, 0x1C, true)] // the keypad's Enter
    [InlineData(0x57, 0x4E, false)] // the keypad's +
    [InlineData(0xE4, 0x1D, true)] // right Ctrl
    [InlineData(0xE5, 0x36, false)] // right Shift
    public void Usb_usages_map_to_set_1_scan_codes(int usage, int code, bool extended)
    {
        Assert.True(ScanCodes.TryFromUsb(usage, out byte scanCode, out bool isExtended));
        Assert.Equal(code, scanCode);
        Assert.Equal(extended, isExtended);
    }

    [Theory]
    [InlineData(0xE3)] // the Windows key
    [InlineData(0x48)] // Pause
    [InlineData(0x00)]
    [InlineData(0x1000)]
    public void Keys_a_pc_of_the_time_lacked_have_no_code(int usage)
        => Assert.False(ScanCodes.TryFromUsb(usage, out _, out _));

    [Fact]
    public void An_empty_game_port_reads_all_ones()
    {
        var port = new GamePort();
        port.Write(0);
        Assert.Equal(0xFF, port.Read(1));
    }

    [Fact]
    public void A_centered_stick_times_out_in_about_seven_tenths_of_a_millisecond()
    {
        var port = new GamePort { Sticks = 1 };
        port.Write(1.0);

        // both of A's axes up, and B's, which isn't there, up for good; no buttons pressed
        Assert.Equal(0xFF, port.Read(1.0 + 0.0006));
        Assert.Equal(0xFC, port.Read(1.0 + 0.0007));
        Assert.Equal(0xFC, port.Read(1.5));
    }

    [Fact]
    public void Each_axis_times_out_by_its_own_position()
    {
        var port = new GamePort { Sticks = 1 };
        port.SetAxis(0, -1); // fully left: the shortest time, about 24 us
        port.SetAxis(1, 1); // fully down: the longest, about 1.3 ms
        port.Write(0);

        Assert.Equal(0x02, port.Read(0.0001) & 0x03);
        Assert.Equal(0x02, port.Read(0.0013) & 0x03);
        Assert.Equal(0x00, port.Read(0.0014) & 0x03);
    }

    [Fact]
    public void A_pressed_button_reads_zero()
    {
        var port = new GamePort { Sticks = 1 };
        port.SetButton(1, true);
        Assert.Equal(0xD0, port.Read(1) & 0xF0);
    }
}
