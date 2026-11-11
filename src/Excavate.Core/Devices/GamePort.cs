namespace Excavate.Core.Devices;

/// <summary>
/// The PC's game port at 201h: two analog joysticks, A and B, each with two axes and two buttons.
/// </summary>
/// <remarks>
/// <para>
/// The port has no converter to read a stick's position as a number. Each axis is a potentiometer
/// (0 to 100 kilohms or so) charging a capacitor through a 558 timer. A write to the port starts
/// all four timers; each axis's bit (0 to 3) reads 1 until its timer runs out, and the time that
/// takes grows with the resistance, so with how far the stick is pushed. A program writes, then
/// counts in a loop how long each bit stays up. The count depends on the processor's speed, which
/// is why games of the time asked the player to calibrate the stick: center, then the corners.
/// </para>
/// <para>
/// The buttons are bits 4 to 7 (A's two, then B's two), 0 while pressed. Four-button sticks of
/// the time used B's buttons for their extra two.
/// </para>
/// <para>
/// The timing is DOSBox's (<c>joystick.cpp</c>): 24.2 microseconds, plus 11 nanoseconds a ohm,
/// with the stick's travel spanning 0 to 120 kilohms, so 24 us fully left, about 0.68 ms centered and
/// 1.3 ms fully right. A stick that isn't there never ends its timers, and with nothing connected
/// the port reads FFh, as an empty port does.
/// </para>
/// </remarks>
internal sealed class GamePort
{
    private const double Constant = 24.2e-6;
    private const double SecondsPerOhm = 11e-9;
    private const double Ohms = 120_000 / 2.0;

    private readonly float[] _axes = new float[4];
    private readonly bool[] _buttons = new bool[4];

    // when each axis's timer runs out, in emulated seconds
    private readonly double[] _ends = new double[4];

    /// <summary>How many times a program has started a reading, by writing to the port.</summary>
    public long Readings { get; private set; }

    /// <summary>How many sticks are connected: 0, 1 (A) or 2 (A and B).</summary>
    public int Sticks { get; set; }

    /// <summary>Sets an axis, 0 and 1 for A's x and y, 2 and 3 for B's, from -1 (left, up) to 1 (right, down).</summary>
    public void SetAxis(int axis, float value) => _axes[axis] = Math.Clamp(value, -1f, 1f);

    /// <summary>Presses or lets go of a button, 0 and 1 on A, 2 and 3 on B.</summary>
    public void SetButton(int button, bool down) => _buttons[button] = down;

    /// <summary>A write, of any value: the four timers start now.</summary>
    public void Write(double now)
    {
        Readings++;
        for (int axis = 0; axis < 4; axis++)
        {
            _ends[axis] = now + Constant + (SecondsPerOhm * Ohms * (_axes[axis] + 1));
        }
    }

    public byte Read(double now)
    {
        if (Sticks == 0)
        {
            return 0xFF;
        }

        int value = 0;
        for (int axis = 0; axis < 4; axis++)
        {
            // a stick not connected has an open circuit: its timers never end
            bool connected = axis / 2 < Sticks;
            if (!connected || now < _ends[axis])
            {
                value |= 1 << axis;
            }
        }

        for (int button = 0; button < 4; button++)
        {
            if (!_buttons[button])
            {
                value |= 0x10 << button;
            }
        }

        return (byte)value;
    }
}
