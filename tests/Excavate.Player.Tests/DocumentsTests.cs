using System.Text;

namespace Excavate.Player.Tests;

/// <summary>
/// The President Is Missing's documents, read as the game keeps them, from a stand-in folder:
/// a <c>START.EXE</c> of two documents in the game's encoding, and a <c>RECD.TBL</c> of where
/// they start, with the game's filler after; and its five message files, each with a table of
/// ten-byte records.
/// </summary>
public sealed class DocumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("excavate-documents").FullName;

    public DocumentsTests()
    {
        Folder = Directory.CreateDirectory(Path.Combine(_root, "presmis")).FullName;
        const string text = "NSC 513324\r\n   PAX ORIENTIS\r\n\r\n^CIA 601542\r\nTSOUBAN\r\n^||||";
        Write("START.EXE", text);
        int second = text.IndexOf('^', StringComparison.Ordinal) + 1;
        File.WriteAllBytes(Path.Combine(Folder, "recd.tbl"), [0, 0, (byte)second, 0, 0x7C, 0x7C, 0x7C, 0x7C]);
        File.WriteAllBytes(Path.Combine(Folder, "PRES.EXE"), [0x4D, 0x5A]);

        // a bulletin that starts on a blank line, and a second, then filler the table doesn't point at
        Messages("BULLMSG", 'B', "\r\nWAS APPOINTED\r\n", "OLYMPICS\r\n");
        Messages("MEMO", 'M', "STAFF MEMO: FLOW-CHART\r\n");
        Messages("OBJMSG", 'X', "AMAL, MOUBALAL - AMAL MET WITH QATZIR TSOUBAN EARLY IN MAY. SHORTLY\r\n");
        Messages("SDMSG", 'S', "ADAM, OMAN\r\n");
        Messages("CADMSG", 'C', "RE:\r\nthe sun rises ascaroth\r\n");
    }

    private string Folder { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string file, string text)
        => File.WriteAllBytes(Path.Combine(Folder, file), PresidentDocuments.Encode(Encoding.ASCII.GetBytes(text)));

    // a message file and its table: a record for each, its letters, its offset, and the rest
    private void Messages(string name, char from, params string[] messages)
    {
        Write($"{name}.FIL", string.Join('^', messages) + "^|\x7C\x7C");
        var table = new List<byte>();
        int offset = 0;
        foreach (string message in messages)
        {
            table.AddRange([(byte)'D', (byte)from, (byte)offset, (byte)(offset >> 8), 0x20, 0x1C, 0, 0, (byte)'F', (byte)'F']);
            offset += message.Length + 1;
        }

        File.WriteAllBytes(Path.Combine(Folder, $"{name}.TBL"), [.. table, 0x7C]);
    }

    [Fact]
    public void The_encoding_is_each_byte_rotated_right_one_bit()
    {
        // a carriage return, a line feed, a space, and an A, as the game's files hold them
        Assert.Equal([0x86, 0x05, 0x10, 0xA0], PresidentDocuments.Encode([0x0D, 0x0A, 0x20, 0x41]));
        Assert.Equal([0x0D, 0x0A, 0x20, 0x41], PresidentDocuments.Decode([0x86, 0x05, 0x10, 0xA0]));
    }

    [Fact]
    public void Each_document_runs_from_where_its_table_says_to_the_next_caret()
    {
        var documents = PresidentDocuments.Read(Folder);

        Assert.Equal(8, documents.Count);
        Assert.Equal(new GameDocument(1, "the data base (START.EXE)", "NSC 513324", "NSC 513324\n   PAX ORIENTIS\n"), documents[0]);
        Assert.Equal(new GameDocument(2, "the data base (START.EXE)", "CIA 601542", "CIA 601542\nTSOUBAN\n"), documents[1]);
        Assert.Equal(new GameDocument(3, "bulletins (BULLMSG.FIL)", "WAS APPOINTED", "WAS APPOINTED\n"), documents[2]);
        Assert.Equal("OLYMPICS", documents[3].Title);
        Assert.Equal(
            ["STAFF MEMO: FLOW-CHART", "AMAL, MOUBALAL - AMAL MET WITH QATZIR TSOUBAN EARLY IN...", "ADAM, OMAN", "RE:"],
            documents.Skip(4).Select(document => document.Title));
        Assert.Equal("RE:\nthe sun rises ascaroth\n", documents[7].Text);
    }

    [Fact]
    public void The_command_line_shows_a_document_by_its_number_and_no_further()
    {
        string[] settings = ["--settings", Path.Combine(_root, "excavate.json")];

        Assert.Equal(0, Program.Run([Folder, "--documents", "8", .. settings]));
        Assert.ThrowsAny<Exception>(() => Program.Run([Folder, "--documents", "9", .. settings]));
    }
}
