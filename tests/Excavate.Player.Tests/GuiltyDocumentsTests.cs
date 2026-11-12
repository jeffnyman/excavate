using System.Text;

namespace Excavate.Player.Tests;

/// <summary>
/// Presumed Guilty's documents, read as the game keeps them, from a stand-in folder: a
/// <c>MSG.TXT</c> of entries in the game's encoding, each named, with the entries it leads to
/// and its text after a <c>~</c>, and a <c>RECORDS.FIL</c> holding the transcript.
/// </summary>
public sealed class GuiltyDocumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("excavate-guilty").FullName;

    public GuiltyDocumentsTests()
    {
        Folder = Directory.CreateDirectory(Path.Combine(_root, "presguil")).FullName;
        Write("MSG.TXT",
            "REMARK---- N 00000\r\n~JUST A SPACER\r\n|"
            + "FINTRODUCE I 00000\r\nOFALCON---\r\n~WELL, WELL, WELL ^F\r\n|"
            + "CASSIGN1-- R 00000\r\nFINTRODUCE\r\n~  ---------\r\n  : COPNET :\r\n|"
            + "OUMBRA---- N 00005\r\n~ - UMBRA -\r\n|"
            + "OHOARE SAM D 00900\r\n     RESPONSIBLE\r\n|"
            + "\\\\\\\\\\\\\\\\\\\\  N 00600\r\n\\\\\\\\\r\n\x1A");
        Write("RECORDS.FIL", "  ~\r\n   AUDIO INTELLIGENCE\r\nFile tape #01\r\n||||\\\\\r\n\x1A");
        File.WriteAllBytes(Path.Combine(Folder, "PGMAIN.EXE"), [0x4D, 0x5A]);
    }

    private string Folder { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string file, string text)
        => File.WriteAllBytes(Path.Combine(Folder, file), GameText.Encode(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Each_entry_is_listed_by_its_kind_and_titled_with_its_name()
    {
        var documents = GuiltyDocuments.Read(Folder);

        // COPNET's first, then Falcon's, the reports, the transcript, and what was left in last;
        // a report whose '~' was left out is its text after its name, and the filler at the end,
        // of no kind, isn't one
        Assert.Equal(
            ["CASSIGN1: : COPNET :", "FINTRODUCE: WELL, WELL, WELL ^F", "OUMBRA: - UMBRA -", "OHOARE SAM: RESPONSIBLE", "AUDIO INTELLIGENCE", "REMARK: JUST A SPACER"],
            documents.Select(document => document.Title));
        Assert.Equal("COPNET's messages (MSG.TXT)", documents[0].Group);
        Assert.Equal("  ---------\n  : COPNET :\n", documents[0].Text);
        Assert.Equal("the audio cassette's transcript (RECORDS.FIL)", documents[4].Group);
        Assert.Equal("   AUDIO INTELLIGENCE\nFile tape #01\n", documents[4].Text);
    }

    [Fact]
    public void The_command_line_reads_them_from_the_games_folder()
        => Assert.Equal(0, Program.Run([Folder, "--documents", "6", "--settings", Path.Combine(_root, "excavate.json")]));
}
