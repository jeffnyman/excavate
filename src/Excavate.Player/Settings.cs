using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excavate.Player;

/// <summary>
/// How a game is played, where it differs from the defaults: each value is optional, and one
/// given on the command line wins over it for that run.
/// </summary>
internal sealed class Profile
{
    /// <summary>The speed, in DOSBox's cycles: instructions a millisecond.</summary>
    public int? Cycles { get; set; }

    public bool? Sound { get; set; }

    public bool? Joystick { get; set; }

    public bool? Mouse { get; set; }

    /// <summary>
    /// How far the game's mouse goes for the host's, in percent: at 100, the mouse moved across
    /// the picture moves the game's pointer across its screen, whatever the window's size.
    /// </summary>
    public int? MouseSpeed { get; set; }

    public int? Scale { get; set; }

    public bool? Fullscreen { get; set; }

    public bool? SquarePixels { get; set; }

    /// <summary>Whether nothing is set.</summary>
    [JsonIgnore]
    public bool IsEmpty => Cycles is null && Sound is null && Joystick is null && Mouse is null
        && MouseSpeed is null && Scale is null && Fullscreen is null && SquarePixels is null;

    /// <summary>The values set in another profile, over this one's.</summary>
    public void Take(Profile other)
    {
        Cycles = other.Cycles ?? Cycles;
        Sound = other.Sound ?? Sound;
        Joystick = other.Joystick ?? Joystick;
        Mouse = other.Mouse ?? Mouse;
        MouseSpeed = other.MouseSpeed ?? MouseSpeed;
        Scale = other.Scale ?? Scale;
        Fullscreen = other.Fullscreen ?? Fullscreen;
        SquarePixels = other.SquarePixels ?? SquarePixels;
    }

    /// <summary>What's set, in a line: "cycles 600, scale 4".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Cycles is { } cycles)
        {
            parts.Add($"cycles {cycles}");
        }

        void Flag(bool? value, string name)
        {
            if (value is { } on)
            {
                parts.Add(on ? name : $"no {name}");
            }
        }

        Flag(Sound, "sound");
        Flag(Joystick, "joystick");
        Flag(Mouse, "mouse");
        if (MouseSpeed is { } speed)
        {
            parts.Add($"mouse speed {speed}%");
        }

        if (Scale is { } scale)
        {
            parts.Add($"scale {scale}");
        }

        Flag(Fullscreen, "full screen");
        Flag(SquarePixels, "square pixels");
        return parts.Count == 0 ? "the defaults" : string.Join(", ", parts);
    }
}

/// <summary>
/// The player's settings, in one file in the user's application data (never in a game's folder,
/// which is only read): where each game is, and each game's profile. It's plain JSON, to be read
/// and edited by hand as well as by the player.
/// </summary>
internal sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Where the settings are unless told otherwise.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Games.Home, "excavate.json");

    /// <summary>Each game's folder, by the game's short name.</summary>
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each game's profile, by the game's short name.</summary>
    public Dictionary<string, Profile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file they're kept in.</summary>
    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    /// <summary>The settings in a file; empty ones when there's no file yet.</summary>
    /// <exception cref="InvalidDataException">The file isn't settings the player can read.</exception>
    public static Settings Load(string path)
    {
        Settings settings;
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"the settings in {path} can't be read: {ex.Message}");
            }

            // names are matched without regard to case, as on the command line
            settings.Folders = new(settings.Folders ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Profiles = new(settings.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            settings = new Settings();
        }

        settings.Path = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }

    /// <summary>A game's folder, if it's known and still there.</summary>
    public string? FolderOf(KnownGame game)
        => Folders.TryGetValue(game.Key, out var folder) && Directory.Exists(folder) ? folder : null;

    /// <summary>Remembers where a game is. Returns whether that changed anything.</summary>
    public bool Remember(KnownGame game, string folder)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (Folders.TryGetValue(game.Key, out var known) && string.Equals(known, folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Folders[game.Key] = folder;
        return true;
    }

    /// <summary>Forgets where a game is. Returns the folder it had, or null when none was kept.</summary>
    public string? Forget(KnownGame game) => Folders.Remove(game.Key, out var folder) ? folder : null;

    /// <summary>A game's profile; an empty one when none is kept.</summary>
    public Profile ProfileOf(KnownGame game) => Profiles.TryGetValue(game.Key, out var profile) ? profile : new Profile();
}
