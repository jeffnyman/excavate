using Excavate.Core.Processors;

namespace Excavate.Core;

// The mouse: a Microsoft-compatible mouse driver answering INT 33h, and the mouse it reads.
public sealed partial class Machine
{
    private const int MouseButtons = 2;

    // the host call in the mouse's IRQ 12 handler, numbered for its vector, 74h
    private const byte MouseEventService = 0x74;

    // the events a program's handler can ask for, in its call mask and in AX when it's called:
    // the mouse moved, and each button's press and release
    private const int Moved = 0x01;

    private bool _mouse;
    private double _mouseX, _mouseY;
    private int _mouseMinX, _mouseMaxX, _mouseMinY, _mouseMaxY;
    private int _mickeysPer8X, _mickeysPer8Y;
    private int _mickeyCountX, _mickeyCountY;
    private int _mouseButtons;
    private int _mouseHidden;
    private readonly int[] _presses = new int[MouseButtons], _releases = new int[MouseButtons];
    private readonly (int X, int Y)[] _lastPress = new (int, int)[MouseButtons], _lastRelease = new (int, int)[MouseButtons];

    // the program's event handler: the events it wants, whether it has one, and the events
    // since it was last called
    private int _eventMask;
    private bool _eventHandler;
    private int _events;

    /// <summary>
    /// Whether a mouse and its driver are installed. Without one, <c>INT 33h</c> reports no mouse,
    /// as on a PC with none; the games then steer their pointers by keyboard or joystick. Set it
    /// before the program starts: games look for a mouse once.
    /// </summary>
    public bool Mouse
    {
        get => _mouse;
        set
        {
            _mouse = value;
            ResetMouse();
        }
    }

    /// <summary>
    /// The mouse moves, by a number of "mickeys", the mouse's own steps (a two hundredth of an
    /// inch on Microsoft's mouse): right and down are positive.
    /// </summary>
    public void MoveMouse(int dx, int dy)
    {
        if (!_mouse)
        {
            return;
        }

        _mickeyCountX += dx;
        _mickeyCountY += dy;

        // the driver turns mickeys into its pixels by its ratio: by default 8 to 8 across and
        // 16 to 8 down, so the pointer moves half as fast down as across
        _mouseX = Math.Clamp(_mouseX + (dx * 8.0 / _mickeysPer8X), _mouseMinX, _mouseMaxX);
        _mouseY = Math.Clamp(_mouseY + (dy * 8.0 / _mickeysPer8Y), _mouseMinY, _mouseMaxY);
        if (dx != 0 || dy != 0)
        {
            MouseEvent(Moved);
        }
    }

    /// <summary>
    /// The mickeys that take the pointer from one edge of the driver's area to the other, across
    /// and down: by default 640 and 400, the area being 640 by 200 at 8 mickeys to 8 pixels
    /// across and 16 to 8 down. A program can set another area, or another ratio.
    /// </summary>
    public (int X, int Y) MickeysAcross =>
        ((_mouseMaxX - _mouseMinX + 1) * _mickeysPer8X / 8, (_mouseMaxY - _mouseMinY + 1) * _mickeysPer8Y / 8);

    /// <summary>A mouse button goes down or up: 0 the left, 1 the right.</summary>
    public void SetMouseButton(int button, bool down)
    {
        if (!_mouse || button is < 0 or >= MouseButtons)
        {
            return;
        }

        int bit = 1 << button;
        bool wasDown = (_mouseButtons & bit) != 0;
        if (down == wasDown)
        {
            return;
        }

        _mouseButtons ^= bit;
        if (down)
        {
            _presses[button]++;
            _lastPress[button] = MousePosition;
        }
        else
        {
            _releases[button]++;
            _lastRelease[button] = MousePosition;
        }

        // the left button's press is 02h and release 04h, the right's 08h and 10h
        MouseEvent(1 << (1 + (2 * button) + (down ? 0 : 1)));
    }

    /// <summary>
    /// An event the program's handler may want: if it does, the mouse raises IRQ 12, and the
    /// driver's handler for it calls the program's (see <see cref="MouseEvent()"/>).
    /// </summary>
    private void MouseEvent(int happened)
    {
        _events |= happened;
        if (_eventHandler && (_events & _eventMask) != 0)
        {
            // IRQ 12 is the second controller's line 4
            _pic2.Raise(12 - 8);
        }
    }

    /// <summary>
    /// The driver's IRQ 12 handler, between saving the program's registers and calling its event
    /// handler: the registers the handler is called with, or carry set if there's nothing to
    /// call it for. They're Microsoft's: AX the events since it was last called, BX the buttons
    /// held, CX and DX the pointer, SI and DI the motion in mickeys.
    /// </summary>
    private void MouseEvent()
    {
        int events = _events & _eventMask;
        if (!_mouse || !_eventHandler || events == 0)
        {
            Cpu.Eflags |= Intel386.Flag.Carry;
            return;
        }

        var (x, y) = MousePosition;
        _events = 0;
        SetAx(events);
        SetBx(_mouseButtons);
        SetCx(x);
        SetDx(y);
        Cpu.Esi = (Cpu.Esi & 0xFFFF_0000) | (ushort)_mickeyCountX;
        Cpu.Edi = (Cpu.Edi & 0xFFFF_0000) | (ushort)_mickeyCountY;
        Cpu.Eflags &= ~Intel386.Flag.Carry;
    }

    private (int X, int Y) MousePosition => ((int)_mouseX, (int)_mouseY);

    /// <summary>The driver's state after a reset: the pointer hidden, in the middle of a 640 by 200 screen.</summary>
    private void ResetMouse()
    {
        _mouseMinX = 0;
        _mouseMaxX = 639;
        _mouseMinY = 0;
        _mouseMaxY = 199;
        _mouseX = 320;
        _mouseY = 100;
        _mickeysPer8X = 8;
        _mickeysPer8Y = 16;
        _mickeyCountX = _mickeyCountY = 0;
        _mouseHidden = 1;
        Array.Clear(_presses);
        Array.Clear(_releases);
        _eventMask = 0;
        _eventHandler = false;
        _events = 0;
    }

    /// <summary>
    /// INT 33h, the mouse driver, as Microsoft's defined it and DOSBox's <c>mouse.cpp</c> answers
    /// it: reset, the pointer's position and buttons, presses and releases counted since last
    /// asked, the area the pointer is kept in, the motion in mickeys, and a handler of the
    /// program's own, which the driver calls when the mouse moves or a button goes down or up.
    /// </summary>
    /// <remarks>
    /// The driver keeps the pointer in its own coordinates, which in the 320 by 200 graphics
    /// modes are 640 by 200: a program halves the column. It draws no pointer of its own here;
    /// the games draw theirs.
    /// </remarks>
    private void MouseService()
    {
        int function = (ushort)Cpu.Eax;
        if (!_mouse)
        {
            Trace($"33/{function:X4} no mouse");
            if (function == 0)
            {
                SetAx(0);
            }

            return;
        }

        var (x, y) = MousePosition;
        switch (function)
        {
            case 0x00:
            case 0x21:
                // reset: present, two buttons
                ResetMouse();
                SetAx(0xFFFF);
                SetBx(MouseButtons);
                break;
            case 0x01:
                _mouseHidden = Math.Max(0, _mouseHidden - 1);
                break;
            case 0x02:
                _mouseHidden++;
                break;
            case 0x03:
                SetBx(_mouseButtons);
                SetCx(x);
                SetDx(y);
                break;
            case 0x04:
                _mouseX = Math.Clamp((short)Cpu.Ecx, _mouseMinX, _mouseMaxX);
                _mouseY = Math.Clamp((short)Cpu.Edx, _mouseMinY, _mouseMaxY);
                break;
            case 0x05:
            case 0x06:
            {
                // presses (5) or releases (6) of a button since last asked, and where the last was
                int button = Math.Clamp((int)(ushort)Cpu.Ebx, 0, MouseButtons - 1);
                var counts = function == 0x05 ? _presses : _releases;
                var (atX, atY) = function == 0x05 ? _lastPress[button] : _lastRelease[button];
                SetAx(_mouseButtons);
                SetBx(counts[button]);
                SetCx(atX);
                SetDx(atY);
                counts[button] = 0;
                break;
            }

            case 0x07:
                (_mouseMinX, _mouseMaxX) = Range((short)Cpu.Ecx, (short)Cpu.Edx);
                _mouseX = Math.Clamp(_mouseX, _mouseMinX, _mouseMaxX);
                break;
            case 0x08:
                (_mouseMinY, _mouseMaxY) = Range((short)Cpu.Ecx, (short)Cpu.Edx);
                _mouseY = Math.Clamp(_mouseY, _mouseMinY, _mouseMaxY);
                break;
            case 0x0B:
                // the motion since last asked, in mickeys
                SetCx(_mickeyCountX);
                SetDx(_mickeyCountY);
                _mickeyCountX = _mickeyCountY = 0;
                break;
            case 0x0C:
                // the program's event handler, at ES:DX, for the events in CX's mask
                SetMouseEventHandler((ushort)Cpu.Ecx, Cpu.Es, (ushort)Cpu.Edx);
                break;
            case 0x14:
            {
                // the same, handing back the handler it replaces
                ushort mask = (ushort)_eventMask;
                ushort segment = ReadWord(BiosSegment, MouseEventHandler + 2u);
                ushort offset = ReadWord(BiosSegment, MouseEventHandler);
                SetMouseEventHandler((ushort)Cpu.Ecx, Cpu.Es, (ushort)Cpu.Edx);
                SetCx(mask);
                Cpu.Es = segment;
                SetDx(offset);
                break;
            }

            case 0x0F:
                _mickeysPer8X = Math.Max(1, (int)(ushort)Cpu.Ecx);
                _mickeysPer8Y = Math.Max(1, (int)(ushort)Cpu.Edx);
                break;
            case 0x24:
                // the driver's version (8.05) and kind (a PS/2 mouse, on its own IRQ)
                SetBx(0x0805);
                SetCx(0x0400);
                break;
            default:
                Trace($"33/{function:X4} is not done");
                break;
        }

        static (int Min, int Max) Range(int a, int b) => a <= b ? (a, b) : (b, a);
    }

    private void SetMouseEventHandler(ushort mask, ushort segment, ushort offset)
    {
        const uint at = ((uint)BiosSegment << 4) + MouseEventHandler;
        WriteRom(at, [(byte)offset, (byte)(offset >> 8), (byte)segment, (byte)(segment >> 8)]);
        _eventMask = mask;
        _eventHandler = mask != 0 && (segment != 0 || offset != 0);
        _events = 0;
        Trace($"33/000C the program's handler at {segment:X4}:{offset:X4}, for events {mask:X2}h");
    }
}
