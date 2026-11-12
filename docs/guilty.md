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

Setup is the President's, changed. It no longer asks which drives you have, or for your
address, and it doesn't ask for the codephrase either, though the prompt is still in it
("ENTER 20 CHAR CODEPHRASE (NO SPACES)"). It shows COPNET's employment application, signed by
its managing director, asks your last and first names, and gives the passphrase away:
"Memorize your passphrase. It is \*IT\*RAINS\*IN\*SPAIN!\*". Then it writes `SYNO.FIL` and
`SA`, and exits with the drive's letter, 'C', as the President's setup did.

```
excavate guilty SU.EXE
```

The profile is the President's too, but where the President keeps your code name, this one
keeps your passphrase: setup writes `*IT*RAINS*IN*SPAIN!*` there, 20 characters. In place of
the address it no longer asks, it writes a row of `s`'s for the street and `c`'s for the city.
`SYNO.FIL` looks to be a list of the names the game knows for each thing you can
investigate, the first of them yours.

The profile eXoDOS gives you was made otherwise: its passphrase is 20 B's, its address
`FA`, `FASDF`, and its name Raymond Hogue. Ray Hogue wrote the President's MS-DOS version.

## The game

It asks for the display, "ENTER 1 for CGA; 2 for EGA; 3 FOR TANDY", shows its title and
credits, and goes straight to COPNET's main menu, on June 24, 1996. There's no logon in this
release. The first message waiting is that letter of acceptance, addressed to the profile's
name.

![The first message](images/guilty-message.png)

A new case starts with an empty database: the dossiers say "> NO RECORDS <", and DNA
analysis, drawn in the EGA's 640-by-200 mode (0Eh), says "NO DNA SAMPLES AVAILABLE". Judging by
its messages ("PHOTOGRAPHIC DATABASE HAS BEEN UPDATED"), the case fills them in as it goes
on. Logging off saves `STATUS.TBL`, `SA` and `CF.FIL`.

`MSG.TXT` holds every message, in the President's encoding, each ending in a `|`: a line with
its name, a letter and a number, the names of the messages it leads to, and after a `~` its
text. In the text, `^F` and `^L` stand for your first and last names. `STATUS.TBL` lists the
messages and the things you can investigate, one record each: a letter for the kind, a
ten-character name, a letter for where it stands, and where its text starts in `MSG.TXT`.

## The Umbra files

*This section and the next give away codes and part of the plot.*

The database's fourth choice is "UMBRA CLEARANCE REQUIRED", the sequel's Top Secret files. It
asks "ENTER UMBRA CODE (ALL UPPERCASE) OR ENTER TO EXIT", and the code is `CHAMBERLAIN`,
stored as plain text in `PGMAIN.EXE`. The check is the President's routine again: it compares
your answer as far as the code goes and no further, so `CHAMBERLAINS` gets in too.

The code doesn't show a file. "UMBRA CLEARANCE ESTABLISHED" flashes, and the game goes
through `STATUS.TBL` turning each record marked `U` to `D`. Six things you can investigate are
marked `U` as the case ships, HACKER, BUCKS, CHAMP, GR-13, MAINFRAME and SLAM: they're what
the clearance opens.

A wrong code gets "!! WARNING - 3RD UNAUTHORIZED ATTEMPT CAUSES TERMINAL DEACTIVATION !!",
and the game counts. The first sets up a message named `FUMBRASUGG`, which is from Falcon:

```
I NOTICED YOU TRYING TO GET INTO THE
UMBRA FILES AND I THOUGHT YOU MIGHT LIKE
MY HELP.  THE UMBRA ACCESS CODE IS

          "DO IT TO ME"
```

It isn't. Typed, it's another wrong code. The third sets up `CUMBRATERM`, "TERMINAL
DEACTIVATION NOTICE", which leads to the end of the game: COPNET deactivates your terminal and
issues a warrant for your arrest. Those two messages are what the code sets up; I haven't
played on to see them arrive. Once cleared, the game stops counting.

## The passphrase

The passphrase is the game's clock. Falcon, the voice behind the plot, is cracking it a
character at a time, in a chain of messages that come at intervals:

```
HERE'S THE 7TH CHARACTER OF YOUR
PASSPHRASE ^F.  ONLY 13 MORE TO GO AND YOUR
HISTORY!
```

Some come with a riddle about the case. When one arrives, the game marks that character in
`CF.FIL`, 20 bytes, one for each character, and shows your passphrase with a `-` for each
character he hasn't found. When he has them all, he gives your address to COPNET,
and the game ends with your arrest. Changing your address puts him off: "THAT WAS MOST CLEVER
OF YOU TO CHANGE YOUR ADDRESS".

So the passphrase is never typed. The prompt "- PLEASE ENTER YOUR PASSPHRASE BELOW -" is in
`PGMAIN.EXE`, but nothing in the program that I could find refers to it, like setup's codephrase prompt: what
the release shows of a logon it took out.

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
