using System.Text;

namespace Excavate.Player;

/// <summary>A document a game keeps in its files, as the game would show it.</summary>
/// <param name="Number">Its number, counting from 1, in the order the game keeps them.</param>
/// <param name="Title">Its first line.</param>
/// <param name="Text">All of it, lines ending in a newline.</param>
internal sealed record GameDocument(int Number, string Title, string Text);

/// <summary>
/// The documents of <i>The President Is Missing</i>'s data base, read from the player's copy:
/// all of them, the Top Secret files included, without playing.
/// </summary>
/// <remarks>
/// <para>
/// They're in <c>START.EXE</c>, which isn't a program: it's text, every byte rotated right by one
/// bit, each document starting after a <c>^</c>. <c>RECD.TBL</c> holds 20 two-byte offsets into
/// it, one for each document, then <c>||</c> filler for the slots not used.
/// </para>
/// </remarks>
internal static class PresidentDocuments
{
    private const string Documents = "START.EXE", Table = "RECD.TBL";

    // the table's filler, two '|'s, where there's no document
    private const int Unused = 0x7C7C;

    /// <summary>The documents in the game's folder, in the game's order.</summary>
    public static IReadOnlyList<GameDocument> Read(string folder)
    {
        byte[] text = Decode(File.ReadAllBytes(Find(folder, Documents)));
        byte[] table = File.ReadAllBytes(Find(folder, Table));

        var documents = new List<GameDocument>();
        for (int entry = 0; entry + 1 < table.Length; entry += 2)
        {
            int start = table[entry] | (table[entry + 1] << 8);
            if (start == Unused || start >= text.Length)
            {
                continue;
            }

            int end = Array.IndexOf(text, (byte)'^', start);
            string body = Encoding.ASCII.GetString(text, start, (end < 0 ? text.Length : end) - start)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .TrimEnd('\n') + "\n";
            documents.Add(new GameDocument(documents.Count + 1, body[..body.IndexOf('\n', StringComparison.Ordinal)].Trim(), body));
        }

        return documents;
    }

    /// <summary>The game's encoding undone: each byte rotated left by one bit.</summary>
    public static byte[] Decode(byte[] bytes) => [.. bytes.Select(b => (byte)((b << 1) | (b >> 7)))];

    /// <summary>The game's encoding: each byte rotated right by one bit.</summary>
    public static byte[] Encode(byte[] bytes) => [.. bytes.Select(b => (byte)((b >> 1) | (b << 7)))];

    // a file in the folder, whatever its case
    private static string Find(string folder, string name)
        => Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"no {name} in {folder}, where the game keeps its documents");
}
