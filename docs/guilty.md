# Presumed Guilty

*Presumed Guilty* (Cosmi, 1989) is built on *The President Is Missing*: the same kind of
network of menus, messages, dossiers and photographs, the same setup and profile, now COPNET,
the Computer Operated Policing Network, with DNA analysis added. The copy here is a later release, Celery Software's,
whose manual update is dated October 8, 1993.

![The title, on the EGA](images/guilty-title.png)

## What's in the folder

| File | What it is |
|---|---|
| `PGMAIN.EXE` | The game, 37,904 bytes |
| `SU.EXE` | Setup, 4,119 bytes, which makes your profile |
| `SA`, `SA.B` | The profile, and the profile as it shipped |
| `MSG.TXT`, `RECORDS.FIL` | The messages and the records, in the President's encoding: each byte rotated right one bit |
| `STATUS.TBL`, `CF.FIL` | The case as it stands, with `STATUS.TBB` and `CF.FIB` as they shipped |
| `SYNO.FIL` | Written by setup, beside the profile |
| `*.PCC` | The pictures: the title, mugshots, photographs, DNA |
| `GO.EXE`, `GO.CFG` | Celery's menu: setup, play, reset |
| `PG.BAT`, `RESET.BAT`, `SAVE.BAT`, `RESUME.BAT` | Play, start over, and keep or bring back a game under a name |
| `floppy\` | The release's floppy: `INSTALL.EXE` and its data |

`GO.CFG` sets out Celery's menu: "Setup Presumed Guilty", "Play", "RESET", and the line "It's
not shareware!". `SAVE.BAT` copies `CF.FIL`, `STATUS.TBL` and `SA` into a folder of the name
given, and `RESUME.BAT` copies them back: a saved game is those three files.

## Setup

Setup is the President's, changed. It no longer asks which drives you have, and it doesn't ask
for the codephrase either, though the prompt is still in it ("ENTER 20 CHAR CODEPHRASE (NO
SPACES)"). It shows COPNET's employment application, signed by its managing director, asks
your last and first names, and gives the passphrase away: "Memorize your passphrase. It is
\*IT\*RAINS\*IN\*SPAIN!\*". Then it writes `SYNO.FIL` and `SA`, and exits with the drive's
letter, 'C', as the President's setup did.

```
excavate guilty SU.EXE
```

The profile eXoDOS gives you has 20 B's where the President's profile has the code name, and the name Raymond Hogue:
Ray Hogue wrote the President's MS-DOS version.

## The game

It asks for the display, "ENTER 1 for CGA; 2 for EGA; 3 FOR TANDY", shows its title and
credits, and goes straight to COPNET's main menu, on June 24, 1996. There's no logon in this
release. The first message waiting is that letter of acceptance, addressed to the profile's
name.

![The first message](images/guilty-message.png)

A new case starts with an empty database: the dossiers say "> NO RECORDS <", and DNA
analysis, drawn in the EGA's 640-by-200 mode (0Eh), says "NO DNA SAMPLES AVAILABLE". Judging by
its messages ("PHOTOGRAPHIC DATABASE HAS BEEN UPDATED"), the case fills them in as it goes on. Logging off saves `STATUS.TBL`, `SA` and `CF.FIL`.

## What it asks of the PC

- **The EGA, or the CGA, or a Tandy.** On the EGA, the only one tried so far, the title and
  credits are in mode 10h, the menus are text, and DNA analysis is mode 0Eh.
- **Names padded with spaces,** as the President's are: it opens `PGTITLE .PCC`.
- **The BIOS's IRET at F000:FF53h,** for its timer hook, as the President does.
- **Nothing on drive A:.** eXoDOS mounts the floppy as A:, but the game runs from its folder,
  and in what's been played so far it never looks there. Its "PLACE DISK 1 IN DRIVE" is for logging off when it can't find
  `SA` to save the profile, as when it's played from floppies.

## A byte that goes astray

To set the EGA's data rotate register, the game writes the register's number, 3, to the
graphics controller's index port, 3CEh. Then it moves to the data port, 3CFh, and writes the
value with `out dx, ax`, two bytes. The first, the value, goes to 3CFh, as meant. The second,
whatever was in AH, goes to the port after, 3D0h, which nothing on an EGA answers. AH holds
half of the two-letter code the value was chosen by, a space (20h) when drawing the title. It
works, by accident; Excavate's log notes the write to 3D0h and goes on.

## What Excavate does

Nothing it didn't already: the game runs as it is. It ends with exit code 2, the number of the
text mode it sets as it leaves, still in AL; the player reports it as it would any other.
