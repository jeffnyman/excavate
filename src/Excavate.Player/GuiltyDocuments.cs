namespace Excavate.Player;

/// <summary>
/// The documents of <i>Presumed Guilty</i>, read from the player's copy without playing: every
/// message the case can bring, its dossiers and reports, and the audio cassette's transcript.
/// </summary>
/// <remarks>
/// <para>
/// They're in <c>MSG.TXT</c>, every byte rotated right by one bit as the President's files are,
/// one entry after another, each ending in a <c>|</c>. An entry starts with a line naming it: ten
/// characters of name, the first a letter for its kind, then a letter for where it stands, and
/// a number.
/// Then come the names of the entries it leads to, a line each, and after a <c>~</c> its text,
/// in which <c>^F</c> and <c>^L</c> stand for the player's first and last names.
/// </para>
/// <para>
/// The transcript is in <c>RECORDS.FIL</c>, the same encoding: its text after a <c>~</c>, up to
/// the first <c>|</c>.
/// </para>
/// </remarks>
internal static class GuiltyDocuments
{
    private const string Messages = "MSG.TXT", Records = "RECORDS.FIL";

    // the kinds of entry, by their letter, in the order they're listed; the notes left in come last
    private static readonly (char Kind, string Group)[] Kinds =
    [
        ('C', "COPNET's messages"),
        ('F', "Falcon's messages"),
        ('N', "the news"),
        ('D', "dossiers"),
        ('M', "the mugshots' entries"),
        ('P', "the photographs' captions"),
        ('O', "reports on what you can investigate"),
        ('S', "notes on the messages, left in"),
        ('R', "remarks, left in"),
    ];

    /// <summary>The documents in the game's folder, by kind, each kind in the game's order.</summary>
    public static IReadOnlyList<GameDocument> Read(string folder)
    {
        var entries = GameText.ReadDecoded(folder, Messages).Split('|')
            .Select(entry => entry.TrimStart('\r', '\n'))
            .Where(entry => entry.Length > 0)
            .ToList();

        var documents = new List<GameDocument>();
        foreach (var (kind, group) in Kinds.Take(Kinds.Length - 2))
        {
            AddKind(documents, entries, kind, group);
        }

        string records = GameText.ReadDecoded(folder, Records);
        int start = records.IndexOf('~', StringComparison.Ordinal) + 1;
        int end = records.IndexOf('|', start);
        GameText.Add(documents, $"the audio cassette's transcript ({Records})", records[start..(end < 0 ? records.Length : end)]);

        foreach (var (kind, group) in Kinds.Skip(Kinds.Length - 2))
        {
            AddKind(documents, entries, kind, group);
        }

        return documents;
    }

    // the entries of a kind, each titled with its name
    private static void AddKind(List<GameDocument> documents, List<string> entries, char kind, string group)
    {
        foreach (string entry in entries.Where(entry => entry[0] == kind))
        {
            string name = entry[..Math.Min(10, entry.Length)].Split('\r')[0].TrimEnd('-', ' ');
            // its text after the '~'; one entry lacks it, and its text follows the line naming it
            int tilde = entry.IndexOf('~', StringComparison.Ordinal);
            int text = tilde >= 0 ? tilde + 1 : entry.IndexOf('\n', StringComparison.Ordinal) + 1;
            GameText.Add(documents, $"{group} ({Messages})", entry[text..], name);
        }
    }
}
