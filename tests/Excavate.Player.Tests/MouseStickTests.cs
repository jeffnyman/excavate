namespace Excavate.Player.Tests;

/// <summary>The mouse as a joystick: a short move is a push, held until the game has read the port.</summary>
public sealed class MouseStickTests
{
    [Fact]
    public void A_short_move_pushes_the_stick_the_way_it_went_furthest()
    {
        var stick = new MouseStick();
        stick.Move(0.02f, 0.01f, readings: 0);
        Assert.Equal((0, 0), (stick.X, stick.Y));

        stick.Move(0.01f, 0.05f, readings: 0);
        Assert.Equal((0, 1), (stick.X, stick.Y));
    }

    [Fact]
    public void A_push_is_held_until_the_game_has_read_the_port_and_a_frame_more()
    {
        var stick = new MouseStick();
        stick.Move(-0.06f, 0, readings: 10);
        Assert.Equal(-1, stick.X);

        // the game busy drawing: frames go by without a reading
        stick.Frame(readings: 10);
        stick.Frame(readings: 10);
        Assert.Equal(-1, stick.X);

        // moving again while it waits adds nothing
        stick.Move(0, 0.2f, readings: 10);
        Assert.Equal((-1, 0), (stick.X, stick.Y));

        // read, then a frame more, and it's let go
        stick.Frame(readings: 12);
        Assert.Equal(-1, stick.X);
        stick.Frame(readings: 15);
        Assert.Equal((0, 0), (stick.X, stick.Y));
    }

    [Fact]
    public void A_click_is_held_until_the_game_has_read_it_even_if_let_go_at_once()
    {
        var stick = new MouseStick();
        stick.Press(0, down: true, readings: 3);
        stick.Press(0, down: false, readings: 3);
        Assert.True(stick.Button(0));
        Assert.False(stick.Button(1));

        stick.Frame(readings: 3);
        Assert.True(stick.Button(0));
        stick.Frame(readings: 4);
        stick.Frame(readings: 4);
        Assert.False(stick.Button(0));
    }

    [Fact]
    public void A_button_held_down_stays_down_after_the_game_has_read_it()
    {
        var stick = new MouseStick();
        stick.Press(1, down: true, readings: 0);
        stick.Frame(readings: 1);
        stick.Frame(readings: 2);
        stick.Frame(readings: 3);
        Assert.True(stick.Button(1));
        stick.Press(1, down: false, readings: 3);
        stick.Frame(readings: 4);
        Assert.False(stick.Button(1));
    }
}
