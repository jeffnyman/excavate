namespace Excavate.Core.Tests;

internal static class TestData
{
    /// <summary>
    /// The folder at that path, its case aside, in EXCAVATE_TESTDATA, or in a _testdata folder
    /// (or _excavating/_testdata) in a folder above the test binary; null when there is none.
    /// Tests that need it pass without checking then.
    /// </summary>
    public static string? Find(string relativePath) =>
        Search("EXCAVATE_TESTDATA", ["_testdata", "_excavating/_testdata"], relativePath);

    /// <summary>
    /// A game's folder, as the player's copy has it: looked for in EXCAVATE_GAMES or in an
    /// _excavating folder above the test binary. Null when there is none, and the tests that
    /// need the game pass without checking then.
    /// </summary>
    public static string? Game(string folder) => Search("EXCAVATE_GAMES", ["_excavating"], folder);

    /// <summary>
    /// The Roland MT-32 and CM-32L ROMs: the folder in EXCAVATE_MT32_ROMS, or a _mt32-roms
    /// folder above the test binary; null when there is none.
    /// </summary>
    public static string? Mt32Roms()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EXCAVATE_MT32_ROMS");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        return Search("EXCAVATE_MT32_ROMS", ["."], "_mt32-roms");
    }

    private static string? Search(string variable, string[] rootNames, string relativePath)
    {
        var fromEnv = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(fromEnv) && In(fromEnv, relativePath) is { } found)
        {
            return found;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var rootName in rootNames)
            {
                if (In(Path.Combine(dir.FullName, rootName), relativePath) is { } candidate)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? In(string root, string relativePath)
    {
        string current = root;
        foreach (var part in relativePath.Split('/'))
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            current = Directory.EnumerateDirectories(current).FirstOrDefault(d => string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase))!;
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
