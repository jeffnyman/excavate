using SDL;
using static SDL.SDL3;

namespace Excavate.Player.Sdl;

/// <summary>
/// The window: the machine's picture in a texture of 32-bit pixels, which SDL's renderer scales
/// onto the window, on the graphics card where there is one. The window can be resized and
/// switched to full screen; the picture keeps its shape, centered with black around it.
/// </summary>
/// <remarks>
/// The VGA's 320 by 200 filled a 4:3 monitor, so its pixels were 1.2 times as tall as they were
/// wide; the picture is shown at 4:3 whatever its size in pixels, unless square pixels are asked
/// for. The size can change as the game changes mode, and the texture follows it.
/// <para>
/// The picture was once scaled in software, onto the window's own surface. That was simple and
/// exact, but a full screen is millions of pixels to fill seventy times a second: on a large
/// screen a frame took longer to draw than it lasts, the sound ran dry between frames, and
/// crackled. The renderer scales with the nearest pixel, as the software did, so the picture is
/// the same.
/// </para>
/// </remarks>
internal sealed unsafe class SdlWindow : IDisposable
{
    private const float MonitorAspect = 4f / 3f;

    private readonly string _title;
    private SDL_Window* _window;
    private SDL_Renderer* _renderer;
    private SDL_Texture* _frame;
    private int _width, _height;

    public SdlWindow(string title, int scale, bool fullscreen, bool squarePixels)
    {
        _title = title;
        SquarePixels = squarePixels;
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        if (fullscreen)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
        }

        // sized for mode 13h, the games' own
        scale = Math.Max(1, scale);
        int height = squarePixels ? 200 * scale : (int)(320 * scale / MonitorAspect);
        _window = SdlException.ThrowIfNull(SDL_CreateWindow(title, 320 * scale, height, flags), "SDL_CreateWindow");
        SdlException.ThrowIfFalse(SDL_SetWindowMinimumSize(_window, 320, 200), "SDL_SetWindowMinimumSize");

        // the best the system has; SDL falls back on its own software renderer
        _renderer = SdlException.ThrowIfNull(SDL_CreateRenderer(_window, (string?)null), "SDL_CreateRenderer");
        Resize(320, 200);
    }

    public bool IsFullscreen => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    /// <summary>Shows the picture's pixels square, rather than in the 4:3 shape a monitor gave them.</summary>
    public bool SquarePixels { get; private set; }

    /// <summary>
    /// The picture's size in the window, in the window's own units (which the mouse's motion is
    /// in, and which are fewer than its pixels on a high-resolution screen).
    /// </summary>
    public (float Width, float Height) PictureInWindow
    {
        get
        {
            int windowWidth, windowHeight;
            if (!SDL_GetWindowSize(_window, &windowWidth, &windowHeight) || windowWidth <= 0 || windowHeight <= 0)
            {
                return (320, 200);
            }

            var picture = Fit(windowWidth, windowHeight);
            return (picture.w, picture.h);
        }
    }

    /// <summary>Whether the mouse is the game's: hidden, held in the window, and read as motion rather than a place.</summary>
    public bool MouseCaptured { get; private set; }

    /// <summary>
    /// Copies a picture into the window: <paramref name="width"/> by <paramref name="height"/>
    /// pixels of ARGB, row by row, drawn by <paramref name="render"/> straight into the texture.
    /// </summary>
    public void Present(int width, int height, RenderAction render)
    {
        if (width != _width || height != _height)
        {
            Resize(width, height);
        }

        nint pixelsAt;
        int pitchBytes;
        SdlException.ThrowIfFalse(SDL_LockTexture(_frame, null, &pixelsAt, &pitchBytes), "SDL_LockTexture");
        try
        {
            // the texture's rows can be padded; the machine draws unpadded rows, so pad them here
            int pitch = pitchBytes / sizeof(uint);
            var pixels = new Span<uint>((void*)pixelsAt, pitch * height);
            if (pitch == width)
            {
                render(pixels[..(width * height)]);
            }
            else
            {
                uint[] rows = new uint[width * height];
                render(rows);
                for (int y = 0; y < height; y++)
                {
                    rows.AsSpan(y * width, width).CopyTo(pixels.Slice(y * pitch, width));
                }
            }
        }
        finally
        {
            SDL_UnlockTexture(_frame);
        }

        Redraw();
    }

    /// <summary>Paints the last picture onto the window again, scaled to fit.</summary>
    public void Redraw()
    {
        int windowWidth, windowHeight;
        if (!SDL_GetCurrentRenderOutputSize(_renderer, &windowWidth, &windowHeight) || windowWidth <= 0 || windowHeight <= 0)
        {
            // minimized, on some systems
            return;
        }

        var target = Fit(windowWidth, windowHeight);
        SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL_RenderClear(_renderer);
        SdlException.ThrowIfFalse(SDL_RenderTexture(_renderer, _frame, null, &target), "SDL_RenderTexture");
        SDL_RenderPresent(_renderer);
    }

    /// <summary>
    /// Gives the window an icon, in the title bar and the taskbar: the largest picture, with the
    /// others as its smaller sizes, for the system to choose among.
    /// </summary>
    public void SetIcon(IReadOnlyList<IconImage> images) => SetIcon(_window, images);

    /// <summary>Gives a window an icon: the player's own windows, the game's and the menu's.</summary>
    public static void SetIcon(SDL_Window* window, IReadOnlyList<IconImage> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        var sizes = images.OrderByDescending(image => image.Width).ToList();
        var icon = Surface(sizes[0]);
        try
        {
            // the alternates belong to the icon once added, and go with it
            foreach (var smaller in sizes.Skip(1))
            {
                var alternate = Surface(smaller);
                SDL_AddSurfaceAlternateImage(icon, alternate);
                SDL_DestroySurface(alternate);
            }

            // an icon is a nicety: a system that won't take one is no reason to stop
            SDL_SetWindowIcon(window, icon);
        }
        finally
        {
            SDL_DestroySurface(icon);
        }

        static SDL_Surface* Surface(IconImage image)
        {
            var surface = SdlException.ThrowIfNull(SDL_CreateSurface(image.Width, image.Height, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888), "SDL_CreateSurface");
            int pitch = surface->pitch / sizeof(uint);
            var pixels = new Span<uint>((void*)surface->pixels, pitch * image.Height);
            for (int y = 0; y < image.Height; y++)
            {
                image.Pixels.AsSpan(y * image.Width, image.Width).CopyTo(pixels.Slice(y * pitch, image.Width));
            }

            return surface;
        }
    }

    /// <summary>Switches between a window and the full screen.</summary>
    public void ToggleFullscreen() => SdlException.ThrowIfFalse(SDL_SetWindowFullscreen(_window, !IsFullscreen), "SDL_SetWindowFullscreen");

    /// <summary>Switches between square pixels and the monitor's shape, and repaints.</summary>
    public void ToggleSquarePixels()
    {
        SquarePixels = !SquarePixels;
        Redraw();
    }

    /// <summary>Gives the mouse to the game, or back to the desktop.</summary>
    public void CaptureMouse(bool capture)
    {
        if (capture != MouseCaptured && SDL_SetWindowRelativeMouseMode(_window, capture))
        {
            MouseCaptured = capture;
        }
    }

    /// <summary>Shows a short status, such as "paused", after the title in the title bar; null clears it.</summary>
    public void ShowStatus(string? status) => SDL_SetWindowTitle(_window, status is null ? _title : $"{_title}  ({status})");

    public void Dispose()
    {
        if (_frame is not null)
        {
            SDL_DestroyTexture(_frame);
            _frame = null;
        }

        if (_renderer is not null)
        {
            SDL_DestroyRenderer(_renderer);
            _renderer = null;
        }

        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
    }

    private void Resize(int width, int height)
    {
        if (_frame is not null)
        {
            SDL_DestroyTexture(_frame);
        }

        _frame = SdlException.ThrowIfNull(
            SDL_CreateTexture(_renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888, SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, width, height),
            "SDL_CreateTexture");
        SdlException.ThrowIfFalse(SDL_SetTextureScaleMode(_frame, SDL_ScaleMode.SDL_SCALEMODE_NEAREST), "SDL_SetTextureScaleMode");
        _width = width;
        _height = height;
    }

    /// <summary>The largest rectangle of the picture's shape that fits the window, centered.</summary>
    private SDL_FRect Fit(int windowWidth, int windowHeight)
    {
        float aspect = SquarePixels ? (float)_width / _height : MonitorAspect;
        float scale = Math.Min(windowWidth / aspect, windowHeight);
        int w = Math.Max(1, (int)(aspect * scale));
        int h = Math.Max(1, (int)scale);
        return new SDL_FRect { x = (windowWidth - w) / 2, y = (windowHeight - h) / 2, w = w, h = h };
    }
}

/// <summary>Draws a picture into the pixels given, row by row.</summary>
internal delegate void RenderAction(Span<uint> pixels);
