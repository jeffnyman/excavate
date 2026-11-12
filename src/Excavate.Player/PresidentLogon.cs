using System.Text;

namespace Excavate.Player;

/// <summary>
/// What <i>The President Is Missing</i> will log on with: the code name and supervisor's name in
/// its profile, <c>SA</c>, which setup made. The game takes only those, so the player says them.
/// </summary>
/// <remarks>
/// The profile is the answers setup was given, each ending in a 0 byte: the kind of drive, its
/// letter, the code name, the supervisor's name, and the rest; then a <c>~</c> and the story's
/// clock. The game reads the one in the save folder, once setup or the game has written one
/// there, and otherwise its own.
/// </remarks>
internal static class PresidentLogon
{
    private const string Profile = "SA";

    /// <summary>The logon to give, said in a line; null without a profile to read.</summary>
    public static string? Describe(string folder, string saves)
    {
        if ((Find(saves) ?? Find(folder)) is not { } profile)
        {
            return null;
        }

        string[] answers = Encoding.ASCII.GetString(File.ReadAllBytes(profile)).Split('\0');
        return answers.Length > 3 && answers[2].Length > 0
            ? $"its profile logs on as {answers[2]}, supervisor {answers[3]}; excavate president SU.EXE makes another"
            : null;
    }

    // the profile in a folder, whatever its case
    private static string? Find(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), Profile, StringComparison.OrdinalIgnoreCase))
            : null;
}
