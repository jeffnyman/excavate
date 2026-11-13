# The Omnicron Conspiracy

*Omnicron Conspiracy* (First Star Software, 1989; Epyx published it in North America, Mirrorsoft
in Europe) sends Captain Ace Powers of the Star Police after a drug ring and a missing
operative, with P.A.L., his ship's droid, for company. The credits, in the game itself: design
and programming by Jesse Taylor, initial game design by Jim Nangano, script by Sheryl Knowles,
original story by Subway Software.

![The title, on the MCGA](images/omnicron-title.png)

## What's in the folder

| File | What it is |
|---|---|
| `OMNICRON.EXE` | The loader, 11,261 bytes, packed with EXEPACK: it runs the game, and stays in memory with the game's graphics code |
| `SCODE.EXE` | The game, 111,887 bytes |
| `PLAY.EXE` | The music player, 16,658 bytes, built with Microsoft C: the theme over the title |
| `OMNICRN9.MUS`, `OMNICRN9.SND` | The theme and its timbre bank, for an AdLib |
| `SOUNDS.DNL`, `THEME.DNL` | Sounds; the game loads the first as it starts |
| `TITLEEGA.CMP`, `TITLECGA.CMP` | The title picture: the first for the EGA and the MCGA, the second for the CGA |
| `CONTROL1.CMP`, `CONTROL7.CMP` | The control panel around the room: the first for the EGA and the MCGA, the second for the CGA |
| `ALLROOM0.DAT` to `ALLROOM3.DAT`, `OFFSETS0.DAT` to `OFFSETS3.DAT` | The rooms, in four sets, each with its table of offsets |
| `DIALOG.BUE` | The dialog |
| `AWARDS.PAK`, `FCONTROL.PAK`, `PAKDATA\*.PAK` | Pictures, 160 of them in `PAKDATA`, named for what they show: Ace (`ALLACE3`), P.A.L. (`PAL`, `PALZM`), doors, missiles, the people of Cron |
| `OMNICRON.COM` | Not the game's: 397 bytes, dated 2009, added for DOSBox (see below) |

The game's files are all dated December 25, 1996. The manual says to copy both disks to a
hard disk, `PAKDATA` with them, and run `OMNICRON`.

## The loader and the game

`OMNICRON.EXE` has its say if there's less than 512 KB, or if `SCODE.EXE` isn't there
("Please ensure that 'disk 1' is in drive 'A'"), and otherwise runs it with DOS's function 4Bh, the call that runs one program
from another and comes back when it ends. It doesn't exit itself. Its strings ("Not a Valid
splatter call for NON-RL encoded buffers", "Internal splatter list OVERFLOW. You splat list IS
TO LARGE!") belong to a graphics library, and the library stays with it: it sets interrupt
vectors 62h and 64h to its own code before it runs the game, and its code is what sets up the
EGA and the MCGA.

The game, in turn, runs `PLAY.EXE` for the title: it plays `OMNICRN9.MUS` on the AdLib's port,
388h, and polls DOS for Enter, function 0Bh then 08h. At Enter it puts the timer back, closes
its files, and exits to the game.

This was the first time a game ran a program that way in Excavate, and three things had to be
added for it:

- **Function 4Bh** loads the program in the memory the caller left free, gives it its own PSP
  with the caller as its parent, and on its exit frees what it had, puts back the vectors it was
  given in its PSP, and returns to the caller as if the call had just finished, with its exit
  code for function 4Dh.
- **A program's handles are its own.** `PLAY.EXE`'s C runtime closes every handle on its way
  out, 0 to 4 among them; in DOS that touches only the player's copy of the table, and the
  game's standard input and output stay open. The handles a program opens are closed when it
  ends, and the ones it was given aren't.
- **DOS reads the keyboard through INT 16h.** By the time the music plays, the game has its
  own INT 09h and INT 16h: keys go to the game and never reach the BIOS's buffer. DOS's console
  calls INT 16h for a key, as its CON driver always did, so the game's INT 16h answers the
  music player's question. DOSBox's DOS reads the BIOS's buffer instead, which is presumably
  why `OMNICRON.COM` exists: it hooks INT 21h to send functions 0Bh and 08h to INT 16h, then
  runs `OMNICRON.EXE`. eXoDOS's launcher types `omnicron`, and DOS runs a .COM before an .EXE
  of the same name. Excavate runs `OMNICRON.EXE`, and doesn't need it.

## The display

The game asks for the display: "(0). Quit (1). CGA (2). Tandy 1000 (3). EGA (4). MCGA (5).
Hercules", and suggests one. On Excavate's VGA it suggests the MCGA. It finds the display with
a series of tests of the kind Richard Wilton's book on PC video gives. When the VGA's BIOS
answers function 1Ah, the display combination, the game takes its answer and skips the tests
that are left, the EGA's among them. So the EGA is never marked as found, and choosing it says
"You do not have an EGA card or the monitor does not support EGA". It would on any VGA. Enter
takes the EGA anyway.

- **The MCGA** is mode 13h. The loader loads the game's 16 colors into the DAC 16 times over,
  each color filling a block of 16 entries, so only a byte's top four bits choose its color.
- **The EGA** is mode 0Eh, 640 by 200, each of the game's pixels two dots wide. The loader
  selects page 1 with INT 10h function 05h and draws there, at A000:4000. In page 0 it writes
  4,096 bytes of patterns: in write mode 2, three of each byte's four pairs of dots, in every
  combination of the 16 colors. Excavate's BIOS only selected pages in the text modes, so
  the EGA showed page 0 and its patterns. It now selects them in the EGA's graphics modes
  too.
- **The CGA** is mode 04h, with its own title and control panel.

The Tandy 1000 and the Hercules aren't tried: Excavate's VGA has neither.

![The bedroom, on the EGA](images/omnicron-bedroom-ega.png)

## The start

After the title and the copyright, Ace is in his bedroom on the ship, and P.A.L. floats in with
H.Q.'s orders in three messages: "Yo, Boss. H.Q. says go to the planet Cron, stat. One of our
operatives has gone silent while tracking a drug ring there." The second gives where Cron is.

![P.A.L.'s orders](images/omnicron-orders.png)

The manual's mission briefing gives the same: "Cron's star co-ordinates are 480117." The
manual also says disk 1 must be in drive A for the game to run, but nothing checks the disks:
the game's one INT 13h is a disk reset after it puts the timer back.

Excavate's test plays it this far, on the MCGA and the EGA: the loader, the display, the
music player handing back, P.A.L.'s three messages, and the bedroom.

## Still to look at

The files: the rooms, the dialog and the pictures. Then the game itself, from the ship to Cron.
