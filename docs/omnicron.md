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
| `ALLROOM0.DAT` to `ALLROOM3.DAT`, `OFFSETS0.DAT` to `OFFSETS3.DAT` | The rooms' pictures, 212 of them in four sets, each set with where each picture starts |
| `DIALOG.BUE` | Not the dialog: the picture of the box it's shown in |
| `AWARDS.PAK` | A full screen: the awards |
| `FCONTROL.PAK`, `PAKDATA\*.PAK` | Sprites, 160 files of them in `PAKDATA`, named for what they show: Ace (`ALLACE3`), P.A.L. (`PAL`, `PALZM`), doors, missiles, the people of Cron |
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

## The game's files

`tools/firststar/omnicron_files.py` reads them (see [the tools](../tools/README.md)): it lists
the rooms and the text, and writes every picture as a PNG file.

### The packing

The rooms' pictures, the sprites and `AWARDS.PAK` are packed the same way, in two layers.
`SCODE.EXE` unpacks them itself, with a routine of about a hundred instructions.

- **Byte pairs.** A packed file starts with its size unpacked, then has blocks. Each block lists
  pairs: a byte that stands for two others, and those two. The block's bytes follow, and each
  that stands for a pair becomes its two, which can stand for pairs in turn. A byte stands for
  a pair only if that pair comes before the one that made it in the list, so the same byte can
  mean different pairs in different places. It's much the scheme Philip Gage published as byte
  pair encoding in *The C Users Journal* in 1994, five years later.
- **Runs.** What that gives has runs: BFh and a count is that many zeros, and CFh, a count and
  a byte is that many of the byte.

The CMP files, the title and the control panel, are packed another way, row by row: a table of
where each row starts, and in each row runs of a 2-byte word repeated or words given one by one.
That unpacking is the loader's, in its graphics library.

### The pictures

All of them are 16 colors, two pixels to a byte. The CGA's title and control panel are the
exception, with four pixels to a byte. A picture starts with 32 bytes that give its width and
height.

A room's picture is 200 by 81 pixels, the window in the middle of the control panel.
`ALLROOM0.DAT` has the 8 rooms of Ace's ship, the Falcon:

![The Falcon, from the bedroom to outer space](images/omnicron-ship.png)

The other three sets are Cron and what's around it: the Blackstar, the freighter Ace finds
attacked, and the police station, the Mentant Temple, the library and the Green Tattoo bar in
`ALLROOM1.DAT`; the city's streets and shops, the hotel and the Mind Zi Tavern in
`ALLROOM2.DAT`; and in `ALLROOM3.DAT`, the G.U.N. factory and its offices, the labs, the secret
drug lab and the asteroid.

![Some of Cron: the police station, the Green Tattoo bar, the Mentant Temple, the Mind Zi Tavern, Bellows' study, G.U.N.'s reception, the secret drug lab and the asteroid](images/omnicron-cron.png)

A sprite file has frames, each with its own size. Ace's `ALLACE3.PAK` has 114: walking,
standing, shooting, breaking up into sparks, and one of him as a giant purple artichoke. As the game loads
a sprite, it swaps the file's see-through color, given in its header, with color 0, which is
see-through when it draws. A character can also have one more color changed: Ace's
olive-drab helmet and boots are black on the screen.

![Ace walking and standing, as he's drawn](images/omnicron-ace.png)

The pictures are drawn here in the game's own colors, the 16 its loader gives the MCGA. They
aren't the EGA's: the dark colors are darker, the bright ones purer, the brown is olive and the
light red is orange.

### The rooms

The rooms themselves are records in `SCODE.EXE`'s data. Each has a name, a picture and its set,
three doors, and the room each door leads to. There are 252 of them, more than the pictures,
because some pictures serve several rooms: the five hallways of the police station are one
picture, and so are the hotel's four bedrooms. The clerk gives Ace number 4, "at the end of the
hallway". A room wider than the screen is
two or more pictures side by side, each its own record, with a door between them.

Two pictures have no room. They're the secret drug lab again, wrecked, so the game presumably
swaps them in after the lab blows up:

![The secret drug lab, before and after](images/omnicron-lab.png)

### The text

`SCODE.EXE` also has all the text: what people say, what things look like when Ace looks at
them, the letters, memos and logs he finds, the menus, and the credits. A message that follows
another after an Enter is marked as one. Some of what's there:

- An ad for Epyx's *The Legend of Blacksilver*, among lines that read like TV, presumably for
  the OmniMon, which "allows you to view programming from many different worlds": "Ready for a
  spine-tingling swords and sorcery adventure? Try The Legend of Blacksilver, from Epyx. Coming
  soon."
- The Blackstar's log, which stops at "Wait! Those markings! Sarbai!! What are they doing
  .....", and its manifest, with Dr. L. Quattro on the passenger list.
- Quattro's victory speech, in two versions. One begins "You're too late, Captain Ace Powers",
  the other "At last, my death ray is fully operational", and both get to "DEAD wrong. HA HA HA
  HA HA HA...."
- The real estate agency's listings, each with its star coordinate. The asteroid in the Cron
  system, 356972, is sold. A memo gives the Sarbai base's: "asteroid D-892 coordinate 563381".
  A string of seven six-digit numbers in the data has Cron's 480117 among them, and these two.
- The rejuvenation booth. "You were lucky this time, Boss. Please be more careful. This
  procedure is very unpredictable." When the luck runs out, Ace's genetic structure has been
  permanently altered: "But don't feel too bad. I hear the girls just can't keep their hands off
  of a giant purple artichoke in a Star Police uniform." That's the artichoke among his frames.

## Still to look at

The game itself, from the ship to Cron, and what makes it end: which of the seven coordinates
the ship goes to, and what's at each.
