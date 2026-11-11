namespace Excavate.Player;

/// <summary>
/// The mouse as a joystick, for a game that reads its stick only as a direction and has no
/// mouse of its own. A short move of the mouse pushes the stick all the way that way, and its
/// left and right buttons are the stick's two.
/// </summary>
/// <remarks>
/// <para>
/// A game reads the stick only when it's ready for a move, and may be busy drawing for a second
/// or more before then. So each push, and each click, is held until the game has read the port
/// and a frame more, then let go: one move of the mouse is one step, made however long the game
/// takes to look. Moving again while a push is waiting does nothing more, as with a key pressed
/// while the game is still drawing.
/// </para>
/// <para>
/// Motion is measured in pictures: 1 is the picture's whole width across, or its whole height
/// down. A move of <see cref="Step"/> that way is a push.
/// </para>
/// </remarks>
internal sealed class MouseStick
{
    /// <summary>How far the mouse moves to push the stick: a twentieth of the picture.</summary>
    public const float Step = 0.05f;

    private readonly Latch[] _buttons = [new(), new()];
    private readonly Latch _push = new();
    private float _restX, _restY;

    /// <summary>The stick's x and y now: -1, 0 or 1 each.</summary>
    public int X { get; private set; }

    /// <inheritdoc cref="X"/>
    public int Y { get; private set; }

    /// <summary>Whether the stick's button (0 or 1) is pressed now.</summary>
    public bool Button(int button) => _buttons[button].On;

    /// <summary>The mouse moved, by fractions of the picture, with the game's readings of the port so far.</summary>
    public void Move(float x, float y, long readings)
    {
        if (_push.On)
        {
            return;
        }

        _restX += x;
        _restY += y;
        if (Math.Max(Math.Abs(_restX), Math.Abs(_restY)) < Step)
        {
            return;
        }

        // along the way it moved further
        (X, Y) = Math.Abs(_restX) >= Math.Abs(_restY) ? (Math.Sign(_restX), 0) : (0, Math.Sign(_restY));
        _restX = _restY = 0;
        _push.Start(readings, held: false);
    }

    /// <summary>A button (0 for the left, 1 for the right) went down or up.</summary>
    public void Press(int button, bool down, long readings)
    {
        if (down)
        {
            _buttons[button].Start(readings, held: true);
        }
        else
        {
            _buttons[button].Held = false;
        }
    }

    /// <summary>A frame has run: what the game has read is let go.</summary>
    public void Frame(long readings)
    {
        foreach (var button in _buttons)
        {
            button.Frame(readings);
        }

        _push.Frame(readings);
        if (!_push.On)
        {
            X = Y = 0;
        }
    }

    /// <summary>
    /// A push or a press, on until the game has read the port and a frame has run since, and,
    /// for a button, until it's let go.
    /// </summary>
    private sealed class Latch
    {
        private long _readings;

        // frames run since the game first read the port while on; -1 before then
        private int _framesSeen = -1;

        public bool On { get; private set; }

        public bool Held { get; set; }

        public void Start(long readings, bool held)
        {
            On = true;
            Held = held;
            _readings = readings;
            _framesSeen = -1;
        }

        public void Frame(long readings)
        {
            if (!On)
            {
                return;
            }

            if (_framesSeen < 0)
            {
                if (readings > _readings)
                {
                    _framesSeen = 0;
                }

                return;
            }

            _framesSeen++;
            if (_framesSeen >= 1 && !Held)
            {
                On = false;
            }
        }
    }
}
