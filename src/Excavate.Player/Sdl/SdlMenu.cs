using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Excavate.Player.Sdl;

/// <summary>
/// The games in one place: a window listing every game the player knows, for when it's started
/// with none named. The ones whose folder is known can be played; a game's folder dropped on the
/// window sets it up. The text is SDL's own 8 by 8 debug font, drawn twice its size.
/// </summary>
internal sealed unsafe class SdlMenu : IDisposable
{
    private const float TextScale = 2;
    private const int LineHeight = 12;
    private const int ListTop = 52;

    private readonly Settings _settings;
    private readonly SDL_Window* _window;
    private int _selected;
    private string? _message;

    private SdlMenu(Settings settings)
    {
        _settings = settings;
        SdlException.ThrowIfFalse(SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_EVENTS), "SDL_Init");
        _window = SdlException.ThrowIfNull(SDL_CreateWindow("Excavate", 800, 500, SDL_WindowFlags.SDL_WINDOW_RESIZABLE), "SDL_CreateWindow");
        SdlWindow.SetIcon(_window, GameIcons.Excavate);
        _selected = Math.Max(0, Array.FindIndex(Games.Known, Playable));
    }

    /// <summary>
    /// Shows the menu until a game is chosen, or the window is closed (null). The window closes
    /// either way; the game opens one of its own.
    /// </summary>
    public static Game? Choose(Settings settings)
    {
        using var menu = new SdlMenu(settings);
        return menu.Run();
    }

    /// <summary>
    /// Draws the menu once into a BMP file, with no window on screen (SDL's "dummy" video
    /// driver keeps the window's picture in memory); for tests.
    /// </summary>
    internal static void Snapshot(Settings settings, string path, int selected = 0, string? message = null)
    {
        SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy");
        try
        {
            using var menu = new SdlMenu(settings) { _message = message };
            menu._selected = selected;
            menu.Draw();
            SdlException.ThrowIfFalse(SDL_SaveBMP(SDL_GetWindowSurface(menu._window), path), "SDL_SaveBMP");
        }
        finally
        {
            SDL_ResetHint(SDL_HINT_VIDEO_DRIVER);
        }
    }

    public void Dispose()
    {
        SDL_DestroyWindow(_window);
        SDL_Quit();
    }

    private bool Playable(KnownGame game) => Folder(game) is not null;

    // the game's folder, when it's set up and its files are still there
    private string? Folder(KnownGame game)
        => _settings.FolderOf(game) is { } folder && Games.IsIn(folder, game) ? folder : null;

    private Game? Run()
    {
        Draw();
        while (true)
        {
            SDL_Event sdlEvent;
            if (!SDL_WaitEventTimeout(&sdlEvent, 250))
            {
                continue;
            }

            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    return null;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                    switch (sdlEvent.key.scancode)
                    {
                        case SDL_Scancode.SDL_SCANCODE_ESCAPE:
                            return null;
                        case SDL_Scancode.SDL_SCANCODE_UP:
                            Move(-1);
                            break;
                        case SDL_Scancode.SDL_SCANCODE_DOWN:
                            Move(1);
                            break;
                        case SDL_Scancode.SDL_SCANCODE_RETURN:
                        case SDL_Scancode.SDL_SCANCODE_KP_ENTER:
                            if (Pick() is { } game)
                            {
                                return game;
                            }

                            break;
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    if (LineAt(sdlEvent.motion.y) is { } over && over != _selected)
                    {
                        _selected = over;
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                    if (sdlEvent.button.button == SDL_BUTTON_LEFT && LineAt(sdlEvent.button.y) is { } clicked)
                    {
                        _selected = clicked;
                        if (Pick() is { } game)
                        {
                            return game;
                        }
                    }

                    break;

                case SDL_EventType.SDL_EVENT_DROP_FILE:
                    Dropped(Marshal.PtrToStringUTF8((nint)sdlEvent.drop.data));
                    break;
            }

            Draw();
        }
    }

    private void Move(int by)
    {
        int count = Games.Known.Length;
        _selected = (_selected + by + count) % count;
    }

    // the line of the list under a point in the window, if any
    private int? LineAt(float y)
    {
        int line = (int)Math.Floor(((y / TextScale) - ListTop) / LineHeight);
        return line >= 0 && line < Games.Known.Length ? line : null;
    }

    private Game? Pick()
    {
        var known = Games.Known[_selected];
        if (Folder(known) is not { } folder)
        {
            _message = $"{known.Title} isn't set up: drop its folder on this window";
            return null;
        }

        if (known.Program is null)
        {
            _message = $"{known.Title} {known.Note}";
            return null;
        }

        return Games.In(folder, known);
    }

    /// <summary>A file or folder dropped on the window: a game's folder sets it up.</summary>
    private void Dropped(string? path)
    {
        if (path is null)
        {
            return;
        }

        string folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (Directory.Exists(folder) && Games.KnownIn(folder) is { } known)
        {
            if (_settings.Remember(known, folder))
            {
                _settings.Save();
            }

            _message = $"{known.Title} is set up, from {folder}";
            _selected = Array.IndexOf(Games.Known, known);
        }
        else
        {
            _message = $"no game the player knows in {folder}";
        }
    }

    private void Draw()
    {
        var surface = SDL_GetWindowSurface(_window);
        if (surface is null)
        {
            return;
        }

        var renderer = SDL_CreateSoftwareRenderer(surface);
        if (renderer is null)
        {
            return;
        }

        SDL_SetRenderScale(renderer, TextScale, TextScale);
        SDL_SetRenderDrawColor(renderer, 0x10, 0x14, 0x28, 0xFF);
        SDL_RenderClear(renderer);

        void Text(float x, float y, string text, byte r, byte g, byte b)
        {
            SDL_SetRenderDrawColor(renderer, r, g, b, 0xFF);
            SDL_RenderDebugText(renderer, x, y, text);
        }

        Text(12, 12, "EXCAVATE", 0xF0, 0xC0, 0x40);
        Text(12, 28, "Up and Down, then Enter, or click. Esc quits.", 0xA0, 0xA8, 0xC0);

        for (int i = 0; i < Games.Known.Length; i++)
        {
            var game = Games.Known[i];
            float y = ListTop + (i * LineHeight);
            bool playable = Playable(game);
            if (i == _selected)
            {
                var bar = new SDL_FRect { x = 8, y = y - 2, w = (surface->w / TextScale) - 16, h = LineHeight };
                SDL_SetRenderDrawColor(renderer, 0x28, 0x34, 0x60, 0xFF);
                SDL_RenderFillRect(renderer, &bar);
            }

            string line = $"{game.Title} ({game.Year})";
            if (playable)
            {
                Text(16, y, line, 0xF0, 0xF0, 0xF0);
            }
            else
            {
                Text(16, y, line, 0x60, 0x68, 0x80);
            }
        }

        float bottom = ListTop + ((Games.Known.Length + 1) * LineHeight);
        var selected = Games.Known[_selected];
        if (Folder(selected) is { } folder)
        {
            // a folder's end says the most: it's cut from the front
            Text(12, bottom, Fit(folder, surface->w, fromFront: true), 0xA0, 0xA8, 0xC0);
            // where the game stands
            string about = $"{char.ToUpperInvariant(selected.Note[0])}{selected.Note[1..]}.";
            Text(12, bottom + LineHeight, Fit(about, surface->w), 0xA0, 0xA8, 0xC0);
        }
        else
        {
            Text(12, bottom, Fit("Not set up yet.", surface->w), 0xA0, 0xA8, 0xC0);
        }

        if (_message is not null)
        {
            Text(12, bottom + (2 * LineHeight), Fit(_message, surface->w), 0xF0, 0xC0, 0x40);
        }

        Text(12, (surface->h / TextScale) - 20, Fit("Drop a game's folder here to set it up.", surface->w), 0x60, 0x68, 0x80);

        SDL_RenderPresent(renderer);
        SDL_DestroyRenderer(renderer);
        SDL_UpdateWindowSurface(_window);

        // as much of a line as fits the window, at 8 pixels a character
        static string Fit(string text, int width, bool fromFront = false)
        {
            int room = Math.Max(4, (int)(width / TextScale / 8) - 3);
            return text.Length <= room ? text
                : fromFront ? string.Concat("...", text.AsSpan(text.Length - room + 3))
                : string.Concat(text.AsSpan(0, room - 3), "...");
        }
    }
}
