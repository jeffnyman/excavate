using System.Diagnostics;
using Excavate.Core;
using SDL;
using static SDL.SDL3;

namespace Excavate.Player.Sdl;

internal sealed class PlayerOptions
{
    public string Title { get; init; } = "Excavate";

    /// <summary>Integer scale for the window's first size; it can be resized afterward.</summary>
    public int Scale { get; init; } = 3;

    public bool Fullscreen { get; init; }

    public bool SquarePixels { get; init; }

    /// <summary>Whether gamepads are the joystick on the game port; when false the port is empty.</summary>
    public bool Joystick { get; init; } = true;

    /// <summary>Plays the machine's sound; when false, or when there's no sound device, the player is silent.</summary>
    public bool Sound { get; init; } = true;

    /// <summary>Whether the host's mouse is the machine's mouse, once a click in the window captures it.</summary>
    public bool Mouse { get; init; } = true;

    /// <summary>
    /// Whether the captured mouse is also a joystick, for a game with no mouse of its own: the game
    /// port then has a stick even with no gamepad. See <see cref="MouseStick"/>.
    /// </summary>
    public bool MouseJoystick { get; init; }

    /// <summary>The mouse's speed: 1, and moving across the picture moves across the game's screen.</summary>
    public double MouseSpeed { get; init; } = 1;

    /// <summary>The file Ctrl+F5 saves the machine's state to and Ctrl+F9 loads it from; none, and they do nothing.</summary>
    public string? StatePath { get; init; }

    /// <summary>The window's icon, the game's, at its sizes; none, and it's the system's own.</summary>
    public IReadOnlyList<IconImage> Icon { get; init; } = [];
}

/// <summary>
/// Plays the machine in a window, a VGA frame at a time: a frame's worth of the machine's time
/// (1/70th of a second in the 256-color modes), then the picture and the sound, then a wait for
/// the next frame's moment. With sound, the moment is the sound device's: the next frame runs
/// when the sound queued has played down to a few frames, so picture and sound never drift apart.
/// Without, it's the host's clock. Keyboard and gamepads are read between frames.
/// </summary>
/// <remarks>
/// The machine's own sense of time comes from the instructions it runs, so the game always sees
/// the same pace; the player's job is only to hand it its instructions at the rate of real time.
/// If the host falls behind (the window was dragged, the machine was busy), the player carries on
/// from now rather than racing to catch up.
/// </remarks>
internal sealed unsafe class SdlPlayer : IDisposable
{
    // the display's frame rate, which the picture is shown at: 70 for the VGA, 60 for the CGA
    private double FrameRate => _machine.FrameRate;

    // the stick's center is a little loose on most gamepads: inside this, it's centered
    private const float DeadZone = 0.08f;

    // at full speed, frames run between pictures for this long, in seconds of real time
    private const double WarpSlice = 1 / 60.0;

    // how long a message, such as "saved", shows in the title bar, in seconds
    private const double MessageSeconds = 2;

    // sound queued ahead of the device, in samples: 60 ms, about four frames, enough to ride out a late frame
    private const int SoundAhead = Machine.SampleRate * 2 * 60 / 1000;

    private readonly short[] _samples = new short[Machine.SampleRate / 5];
    private readonly SdlAudio? _audio;

    private readonly Machine _machine;
    private readonly PlayerOptions _options;
    private readonly SdlWindow _window;

    // the PC keys held down now, by scan code and whether extended, to let go when focus is lost
    private readonly HashSet<(byte Code, bool Extended)> _held = [];
    private readonly Dictionary<SDL_JoystickID, nint> _gamepads = [];
    private readonly float[] _stick = new float[2];
    private readonly int[] _dpad = new int[2];
    private readonly bool[] _padButtons = new bool[4];
    private readonly MouseStick _mouseStick = new();

    // the mouse's motion not yet passed on: the host's motion comes in fractions
    private float _mouseRestX, _mouseRestY;
    private bool _quit;
    private bool _paused;
    private bool _warp;
    private string? _message;
    private long _messageUntil;
    private long _frames;
    private long _instructions;

    public SdlPlayer(Machine machine, PlayerOptions options)
    {
        _machine = machine;
        _options = options;
        // the captured mouse's motion as the desktop's pointer would move, not in the mouse's raw
        // counts, which depend on its resolution: the motion is then in the window's units
        SDL_SetHint(SDL_HINT_MOUSE_RELATIVE_SYSTEM_SCALE, "1");
        SdlException.ThrowIfFalse(
            SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_EVENTS | SDL_InitFlags.SDL_INIT_GAMEPAD),
            "SDL_Init");
        _window = new SdlWindow(options.Title, options.Scale, options.Fullscreen, options.SquarePixels);
        _window.SetIcon(options.Icon);
        if (options.Sound)
        {
            try
            {
                _audio = new SdlAudio();
            }
            catch (SdlException ex)
            {
                Console.Error.WriteLine($"excavate: no sound: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Plays until the window is closed or the program ends, or for the emulated seconds given.
    /// Returns whether the program is still running.
    /// </summary>
    public bool Run(double? seconds = null)
    {
        long frameTicks = (long)(Stopwatch.Frequency / FrameRate);
        long next = Stopwatch.GetTimestamp();
        bool running = true;

        // gamepads already plugged in arrive as events: take them before the game looks for a joystick
        Pump();
        UpdateJoystick();

        while (running && !_quit && (seconds is null || _machine.Time.TotalSeconds < seconds))
        {
            Pump();
            if (_paused)
            {
                // what's queued plays out, and the sound picks up again from the moment of unpausing
                _window.Redraw();
                SDL_Delay(16);
                next = Stopwatch.GetTimestamp();
                continue;
            }

            if (_warp)
            {
                // as many frames as fit in a slice of real time, then one picture
                long until = Stopwatch.GetTimestamp() + (long)(WarpSlice * Stopwatch.Frequency);
                do
                {
                    running = RunFrame();

                    // sound at full speed is no use: it's dropped
                    while (_machine.ReadSamples(_samples) > 0)
                    {
                    }
                }
                while (running && Stopwatch.GetTimestamp() < until);

                Present();
                next = Stopwatch.GetTimestamp();
                continue;
            }

            running = RunFrame();
            Present();

            if (_audio is not null)
            {
                for (int count; (count = _machine.ReadSamples(_samples)) > 0;)
                {
                    _audio.Queue(_samples.AsSpan(0, count));
                }

                while (_audio.Queued > SoundAhead && !_quit)
                {
                    SDL_DelayPrecise(1_000_000);
                }

                continue;
            }

            // without sound, nobody reads it
            while (_machine.ReadSamples(_samples) > 0)
            {
            }

            next += frameTicks;
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                SDL_DelayPrecise((ulong)(wait * 1_000_000_000L / Stopwatch.Frequency));
            }
            else if (-wait > frameTicks * 5)
            {
                next = Stopwatch.GetTimestamp();
            }
        }

        return running;
    }

    public void Dispose()
    {
        foreach (var gamepad in _gamepads.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        }

        _audio?.Dispose();
        _window.Dispose();
        SDL_Quit();
    }

    /// <summary>
    /// One frame of the machine's time. The instructions are counted from the start, so the
    /// fractions of an instruction a frame leaves over add up rather than being lost.
    /// </summary>
    private bool RunFrame()
    {
        _frames++;
        long due = (long)(_frames * (double)_machine.InstructionsPerSecond / FrameRate);
        bool running = _machine.Run(due - _instructions);
        _instructions = due;
        if (_options.MouseJoystick)
        {
            _mouseStick.Frame(_machine.JoystickReadings);
            UpdateAxes();
            UpdateButtons();
        }

        return running;
    }

    /// <summary>
    /// Mickeys for a point of the host mouse's motion. The DOS mouse driver, by default, counts
    /// 640 of its pixels across a 320-wide mode and moves one for a mickey across, and 200 down
    /// at two mickeys each: so 640 mickeys across the picture and 400 down take the pointer from
    /// edge to edge. A game can set a bigger area: <i>Wing Commander III</i>'s is 1280 by 480, so
    /// it takes twice as many across and more than twice down. Whatever the driver's area, it's
    /// spread over the picture as shown, however big, and times the speed asked.
    /// </summary>
    private (float X, float Y) MouseScale()
    {
        var (width, height) = _window.PictureInWindow;
        var (across, down) = _machine.MickeysAcross;
        return ((float)(across / width * _options.MouseSpeed), (float)(down / height * _options.MouseSpeed));
    }

    private void Present() => _window.Present(_machine.DisplayWidth, _machine.DisplayHeight, _machine.Render);

    private void Pump()
    {
        SDL_Event sdlEvent;
        while (SDL_PollEvent(&sdlEvent))
        {
            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    _quit = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_EXPOSED:
                    _window.Redraw();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    // the keys up go to another window: let go of every key held, or one (Alt,
                    // after Alt+Tab) stays down in the game; and the mouse goes back
                    foreach (var (code, extended) in _held)
                    {
                        _machine.ReleaseKey(code, extended);
                    }

                    _held.Clear();
                    ReleaseMouse();
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    MouseButton(sdlEvent.button.button, sdlEvent.button.down);
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    if (_window.MouseCaptured)
                    {
                        // scaled to the picture's size in the window, not passed on pixel for pixel
                        var (scaleX, scaleY) = MouseScale();
                        _mouseRestX += sdlEvent.motion.xrel * scaleX;
                        _mouseRestY += sdlEvent.motion.yrel * scaleY;
                        int dx = (int)_mouseRestX, dy = (int)_mouseRestY;
                        _mouseRestX -= dx;
                        _mouseRestY -= dy;
                        _machine.MoveMouse(dx, dy);
                        if (_options.MouseJoystick)
                        {
                            var (width, height) = _window.PictureInWindow;
                            float speed = (float)_options.MouseSpeed;
                            _mouseStick.Move(sdlEvent.motion.xrel / width * speed, sdlEvent.motion.yrel / height * speed, _machine.JoystickReadings);
                            UpdateAxes();
                        }
                    }

                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    Key(sdlEvent.key);
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    var gamepad = SDL_OpenGamepad(sdlEvent.gdevice.which);
                    if (gamepad is not null)
                    {
                        _gamepads[sdlEvent.gdevice.which] = (nint)gamepad;
                    }

                    UpdateJoystick();
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    if (_gamepads.Remove(sdlEvent.gdevice.which, out var removed))
                    {
                        SDL_CloseGamepad((SDL_Gamepad*)removed);
                    }

                    UpdateJoystick();
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                    GamepadButton((SDL_GamepadButton)sdlEvent.gbutton.button, sdlEvent.gbutton.down);
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                    GamepadAxis((SDL_GamepadAxis)sdlEvent.gaxis.axis, sdlEvent.gaxis.value);
                    break;
            }
        }

        if (_message is not null && Stopwatch.GetTimestamp() > _messageUntil)
        {
            _message = null;
            ShowStatus();
        }
    }

    private void Key(SDL_KeyboardEvent key)
    {
        // the player's own keys, which the game never sees: F11 and F12 came after these games
        // were written, and Pause is the one key a game can't read
        switch (key.scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_F1 or SDL_Scancode.SDL_SCANCODE_F2 or SDL_Scancode.SDL_SCANCODE_F3 or SDL_Scancode.SDL_SCANCODE_F4
                when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0 && _machine.Floppies > 0:
                if (key.down && !key.repeat)
                {
                    // a disk into drive A:, by its number
                    int disk = key.scancode - SDL_Scancode.SDL_SCANCODE_F1;
                    if (disk < _machine.Floppies)
                    {
                        _machine.Floppy = disk;
                        Message($"disk {disk + 1} in drive A:");
                    }
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F11 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0 && _machine.Video == VideoAdapter.Cga:
                if (key.down && !key.repeat)
                {
                    _machine.Composite = !_machine.Composite;
                    Message(_machine.Composite ? "composite monitor" : "RGB monitor");
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F11:
                if (key.down && !key.repeat)
                {
                    if ((key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0)
                    {
                        _window.ToggleSquarePixels();
                    }
                    else
                    {
                        _window.ToggleFullscreen();
                    }
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F12:
                if (key.down && !key.repeat)
                {
                    _warp = !_warp;
                    ShowStatus();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F10 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0:
                if (key.down && !key.repeat)
                {
                    ReleaseMouse();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F5 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0:
                if (key.down && !key.repeat)
                {
                    SaveState();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F9 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0:
                if (key.down && !key.repeat)
                {
                    LoadState();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_PAUSE:
                if (key.down && !key.repeat)
                {
                    _paused = !_paused;
                    ShowStatus();
                }

                return;
        }

        if (!ScanCodes.TryFromUsb((int)key.scancode, out byte code, out bool extended))
        {
            return;
        }

        if (key.down)
        {
            // a key held down repeats its make code, as the PC's keyboard does by itself
            _held.Add((code, extended));
            _machine.PressKey(code, extended);
        }
        else
        {
            if (_held.Remove((code, extended)))
            {
                _machine.ReleaseKey(code, extended);
            }
        }
    }

    private void ShowStatus()
        => _window.ShowStatus(_message ?? (_paused ? "paused" : _warp ? "full speed" : _window.MouseCaptured ? "Ctrl+F10 frees the mouse" : null));

    /// <summary>Says what the player just did, in the title bar, for a couple of seconds.</summary>
    private void Message(string message)
    {
        _message = message;
        _messageUntil = Stopwatch.GetTimestamp() + (long)(MessageSeconds * Stopwatch.Frequency);
        ShowStatus();
    }

    /// <summary>Saves the whole machine to the state file: Ctrl+F5.</summary>
    private void SaveState()
    {
        if (_options.StatePath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // written aside and then moved, so a failed save leaves the last one whole
            string temporary = path + ".new";
            using (var file = File.Create(temporary))
            {
                _machine.SaveState(file);
            }

            File.Move(temporary, path, overwrite: true);
            Message("state saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message("state not saved");
            Console.Error.WriteLine($"excavate: could not save the state: {ex.Message}");
        }
    }

    /// <summary>Puts the machine back as the state file has it: Ctrl+F9.</summary>
    private void LoadState()
    {
        if (_options.StatePath is not { } path)
        {
            return;
        }

        if (!File.Exists(path))
        {
            Message("no state saved");
            return;
        }

        try
        {
            using (var file = File.OpenRead(path))
            {
                _machine.LoadState(file);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message("state not loaded");
            Console.Error.WriteLine($"excavate: could not load the state: {ex.Message}");
            return;
        }

        // the keys held when it was saved are let go, and the ones held now (Ctrl, at least)
        // pressed, so the game's idea of the keyboard is the keyboard's
        _machine.ReleaseKeys();
        foreach (var (code, extended) in _held)
        {
            _machine.PressKey(code, extended);
        }

        // the mouse's buttons as they are now, and the sound from the state's moment on
        _machine.SetMouseButton(0, false);
        _machine.SetMouseButton(1, false);
        _audio?.Clear();
        while (_machine.ReadSamples(_samples) > 0)
        {
        }

        Message("state loaded");
    }

    /// <summary>
    /// A mouse button. While the mouse is free, a click in the window captures it for the game
    /// (and isn't passed on, as it was only meant to take the mouse); after that, the left and
    /// right buttons are the PC mouse's.
    /// </summary>
    private void MouseButton(byte button, bool down)
    {
        if (!_options.Mouse)
        {
            return;
        }

        if (!_window.MouseCaptured)
        {
            if (down)
            {
                _window.CaptureMouse(true);
                ShowStatus();
            }

            return;
        }

        int pcButton = button switch
        {
            SDL_BUTTON_LEFT => 0,
            SDL_BUTTON_RIGHT => 1,
            _ => -1,
        };

        if (pcButton >= 0)
        {
            _machine.SetMouseButton(pcButton, down);
            if (_options.MouseJoystick)
            {
                _mouseStick.Press(pcButton, down, _machine.JoystickReadings);
                UpdateButtons();
            }
        }
    }

    /// <summary>The mouse back to the desktop, with the PC mouse's buttons let go.</summary>
    private void ReleaseMouse()
    {
        if (!_window.MouseCaptured)
        {
            return;
        }

        _window.CaptureMouse(false);
        _machine.SetMouseButton(0, false);
        _machine.SetMouseButton(1, false);
        ShowStatus();
    }

    /// <summary>
    /// The game port has a joystick while a gamepad is plugged in, or always when the mouse is one,
    /// unless the joystick is turned off.
    /// </summary>
    private void UpdateJoystick()
    {
        _machine.Joysticks = _options.Joystick && (_gamepads.Count > 0 || _options.MouseJoystick) ? 1 : 0;
        _stick[0] = _stick[1] = 0;
        _dpad[0] = _dpad[1] = 0;
        Array.Clear(_padButtons);
        UpdateButtons();
        UpdateAxes();
    }

    /// <summary>
    /// The face buttons are the joystick's four (A's two, then B's two, as four-button sticks had
    /// them); the d-pad pushes the stick all the way, over the left stick.
    /// </summary>
    private void GamepadButton(SDL_GamepadButton button, bool down)
    {
        int index = button switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => 0,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => 1,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => 2,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => 3,
            _ => -1,
        };

        if (index >= 0)
        {
            _padButtons[index] = down;
            UpdateButtons();
            return;
        }

        (int axis, int direction) = button switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => (0, -1),
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => (0, 1),
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => (1, -1),
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => (1, 1),
            _ => (-1, 0),
        };

        if (axis >= 0)
        {
            if (down)
            {
                _dpad[axis] = direction;
            }
            else if (_dpad[axis] == direction)
            {
                _dpad[axis] = 0;
            }

            UpdateAxes();
        }
    }

    /// <summary>The left stick is the joystick, in proportion: these games were flown with an analog stick.</summary>
    private void GamepadAxis(SDL_GamepadAxis axis, short value)
    {
        int index = axis switch
        {
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX => 0,
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY => 1,
            _ => -1,
        };

        if (index >= 0)
        {
            float position = Math.Max(-1f, value / 32767f);
            _stick[index] = Math.Abs(position) < DeadZone ? 0 : position;
            UpdateAxes();
        }
    }

    /// <summary>The stick where the mouse pushes it, or else the d-pad, or else the left stick.</summary>
    private void UpdateAxes()
    {
        for (int axis = 0; axis < 2; axis++)
        {
            int mouse = axis == 0 ? _mouseStick.X : _mouseStick.Y;
            _machine.SetJoystickAxis(axis, mouse != 0 ? mouse : _dpad[axis] != 0 ? _dpad[axis] : _stick[axis]);
        }
    }

    /// <summary>A button is pressed while the gamepad's is, or the mouse's for the first two.</summary>
    private void UpdateButtons()
    {
        for (int button = 0; button < 4; button++)
        {
            _machine.SetJoystickButton(button, _padButtons[button] || (button < 2 && _mouseStick.Button(button)));
        }
    }
}
