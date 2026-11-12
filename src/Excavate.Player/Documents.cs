using System.Text;

namespace Excavate.Player;

/// <summary>A document a game keeps in its files, as the game would show it.</summary>
/// <param name="Number">Its number, counting from 1, in the order the game keeps them.</param>
/// <param name="Group">What kind of document it is, and the file it's from.</param>
/// <param name="Title">Its first line, cut short if it's long.</param>
/// <param name="Text">All of it, lines ending in a newline.</param>
internal sealed record GameDocument(int Number, string Group, string Title, string Text);

/// <summary>
/// The documents of <i>The President Is Missing</i>, read from the player's copy without
/// playing: its data base's, the Top Secret files included, and every message it can send you.
/// </summary>
/// <remarks>
/// <para>
/// The data base's are in <c>START.EXE</c>, which isn't a program: it's text, every byte rotated
/// right by one bit, each document starting after a <c>^</c>. <c>RECD.TBL</c> holds 20 two-byte
/// offsets into it, one for each document, then <c>||</c> filler for the slots not used.
/// </para>
/// <para>
/// The messages are in <c>*.FIL</c> files in the same encoding, one after another, each but the
/// first starting after a <c>^</c>, and filler after the last. Each has a <c>.TBL</c> beside it,
/// of ten-byte records, one for each message: two letters, the second saying who sends it, then
/// where it starts in the file.
/// </para>
/// </remarks>
internal static class PresidentDocuments
{
    private const string Documents = "START.EXE", Table = "RECD.TBL";

    // the table's filler, two '|'s, where there's no document
    private const int Unused = 0x7C7C;

    // the message tables' records, each ten bytes long
    private const int RecordLength = 10;

    // a title longer than this is cut short
    private const int TitleLength = 60;

    // the message files, in the order they're listed, and who sends them
    private static readonly (string File, string Group)[] Messages =
    [
        ("BULLMSG.FIL", "bulletins"),
        ("MEMO.FIL", "staff memos"),
        ("OBJMSG.FIL", "agents' reports on their objectives"),
        ("SDMSG.FIL", "the State Department's answers"),
        ("CADMSG.FIL", "the cryptographics lab's (C.A.D.'s) analyses"),
    ];

    /// <summary>The documents in the game's folder: the data base's in the game's order, then the messages.</summary>
    public static IReadOnlyList<GameDocument> Read(string folder)
    {
        var documents = new List<GameDocument>();
        byte[] table = File.ReadAllBytes(Find(folder, Table));
        AddEach(documents, folder, Documents, $"the data base ({Documents})",
            Enumerable.Range(0, table.Length / 2).Select(entry => table[2 * entry] | (table[(2 * entry) + 1] << 8)));

        foreach (var (file, group) in Messages)
        {
            // a record of ten bytes for each message, its offset after two letters; the byte left over is filler
            byte[] records = File.ReadAllBytes(Find(folder, Path.ChangeExtension(file, ".TBL")));
            AddEach(documents, folder, file, $"{group} ({file})",
                Enumerable.Range(0, records.Length / RecordLength).Select(record => records[(record * RecordLength) + 2] | (records[(record * RecordLength) + 3] << 8)));
        }

        return documents;
    }

    // the documents in a file, from each offset given to the next '^'
    private static void AddEach(List<GameDocument> documents, string folder, string file, string group, IEnumerable<int> starts)
    {
        byte[] text = Decode(File.ReadAllBytes(Find(folder, file)));
        foreach (int start in starts)
        {
            if (start == Unused || start >= text.Length)
            {
                continue;
            }

            int end = Array.IndexOf(text, (byte)'^', start);
            Add(documents, group, Encoding.ASCII.GetString(text, start, (end < 0 ? text.Length : end) - start));
        }
    }

    /// <summary>The game's encoding undone: each byte rotated left by one bit.</summary>
    public static byte[] Decode(byte[] bytes) => [.. bytes.Select(b => (byte)((b << 1) | (b >> 7)))];

    /// <summary>The game's encoding: each byte rotated right by one bit.</summary>
    public static byte[] Encode(byte[] bytes) => [.. bytes.Select(b => (byte)((b >> 1) | (b << 7)))];

    // a document, its lines ending in a newline, with no blank lines before or after; none when it's empty
    private static void Add(List<GameDocument> documents, string group, string text)
    {
        string body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        if (body.Trim().Length == 0)
        {
            return;
        }

        string title = body.Split('\n')[0].Trim();
        if (title.Length > TitleLength)
        {
            // at the last word that fits
            int space = title.LastIndexOf(' ', TitleLength - 3);
            title = title[..(space > 0 ? space : TitleLength - 3)].TrimEnd(' ', ',', '.', '-') + "...";
        }

        documents.Add(new GameDocument(documents.Count + 1, group, title, body + "\n"));
    }

    // a file in the folder, whatever its case
    private static string Find(string folder, string name)
        => Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"no {name} in {folder}, where the game keeps its documents");
}
