using System.Text;

namespace Excavate.Player.Tests;

/// <summary>
/// The President Is Missing's documents, read as the game keeps them, from a stand-in folder:
/// a <c>START.EXE</c> of two documents in the game's encoding, and a <c>RECD.TBL</c> of where
/// they start, with the game's filler after.
/// </summary>
public sealed class DocumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("excavate-documents").FullName;

    public DocumentsTests()
    {
        Folder = Directory.CreateDirectory(Path.Combine(_root, "presmis")).FullName;
        const string text = "NSC 513324\r\n   PAX ORIENTIS\r\n\r\n^CIA 601542\r\nTSOUBAN\r\n^||||";
        File.WriteAllBytes(Path.Combine(Folder, "START.EXE"), PresidentDocuments.Encode(Encoding.ASCII.GetBytes(text)));
        int second = text.IndexOf('^', StringComparison.Ordinal) + 1;
        File.WriteAllBytes(Path.Combine(Folder, "recd.tbl"), [0, 0, (byte)second, 0, 0x7C, 0x7C, 0x7C, 0x7C]);
        File.WriteAllBytes(Path.Combine(Folder, "PRES.EXE"), [0x4D, 0x5A]);
    }

    private string Folder { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_encoding_is_each_byte_rotated_right_one_bit()
    {
        // a carriage return, a line feed, a space, and an A, as the game's files hold them
        Assert.Equal([0x86, 0x05, 0x10, 0xA0], PresidentDocuments.Encode([0x0D, 0x0A, 0x20, 0x41]));
        Assert.Equal([0x0D, 0x0A, 0x20, 0x41], PresidentDocuments.Decode([0x86, 0x05, 0x10, 0xA0]));
    }

    [Fact]
    public void Each_document_runs_from_where_the_table_says_to_the_next_caret()
    {
        var documents = PresidentDocuments.Read(Folder);

        Assert.Equal(2, documents.Count);
        Assert.Equal(new GameDocument(1, "NSC 513324", "NSC 513324\n   PAX ORIENTIS\n"), documents[0]);
        Assert.Equal(new GameDocument(2, "CIA 601542", "CIA 601542\nTSOUBAN\n"), documents[1]);
    }

    [Fact]
    public void The_command_line_shows_a_document_by_its_number_and_no_further()
    {
        string[] settings = ["--settings", Path.Combine(_root, "excavate.json")];

        Assert.Equal(0, Program.Run([Folder, "--documents", "2", .. settings]));
        Assert.ThrowsAny<Exception>(() => Program.Run([Folder, "--documents", "3", .. settings]));
    }
}
