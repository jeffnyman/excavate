# Guardians of Infinity: To Save Kennedy

*Guardians of Infinity: To Save Kennedy* (Paragon Software, 1988). You direct a timeship and its
crew, sent back to stop the assassination of John F. Kennedy. The box held a novella, a reference card and a registration card, which a copy of the
game today keeps as PDFs beside it.

![The timeship arrives](images/kennedy-start.png)

## What's in the folder

| File | What it is |
|---|---|
| `KENNEDY.BAT` | How it's started: runs the game, then, by its exit code, the ending or a prompt to play again; `KENNEDY SETUP` chooses the display |
| `GOI.EXE` | The game, 153,152 bytes |
| `RUND.EXE` | The ending: your talk with Kennedy on Air Force One, and how it all turns out |
| `KSHOW.EXE`, `SHOW.DAT`, `JFKT1`, `P` | The "Top-Secret Slide Show": the story before the game, in pages of text and pictures |
| `K.DAT` | The ending's dialog: what Kennedy and Dave Powers answer to what you type, not yet looked at |
| `$RUN.OVL`, `$KS.OVL` | The display's overlays: setup swaps them, for a "fast" adapter or a "slow" CGA that shows snow |
| `SYS$MSG.DAT` | The interface library's messages |
| `GETKEY.COM` | Waits for a key and exits with it, for the batch file |
| `GOIDAT.SAV`, `MIRKWOOD.DAT`, `SHERWOOD.DAT`, `NWS` | The game's data, not yet looked at |

`GOI.EXE`, `RUND.EXE` and `KSHOW.EXE` were built with Turbo C (Borland, 1987). The game and
`RUND.EXE` are also built on C-Worthy, an interface library by Custom Design Systems of Provo,
Utah: its windows, its overlays, and `SYS$MSG.DAT`, a "C-Worthy (R) Message Librarian Data
File".

## What it asks of the PC

Two things a PC of 1988 didn't have, both asked by C-Worthy, and both answered as such a PC
answers them:

- **NetWare's shell.** It calls INT 21h function EAh, AL 01h: the version of the NetWare
  shell, Novell's network software, which fills in a buffer with its system's name and
  version. Novell later bought C-Worthy and built its own utilities on it. Without a network
  there's no shell, and DOS answers as for any function it doesn't have: AL 0 and nothing
  else. The game had cleared the buffer first, and goes on with empty names.
- **A multitasker's screen.** It calls INT 10h function FEh, TopView's, which DESQview and
  Windows also answered: given the screen's address in ES:DI, a multitasker hands back a
  buffer of its own to write in. With none, the BIOS doesn't know the call, ES:DI are left as
  they were, and the game writes the screen itself.

It also points interrupts 34h to 3Dh at itself: Turbo C's emulator, for floating point with no
8087.

## What Excavate does

It answers both calls as DOS and the BIOS did, and notes them in its log. With that, the game
starts from its batch file and draws its screen: the date, Friday 15; the time, 0800 CST; the
timeship's status; and its computer's first word, "Adam, the timeship has arrived in a
null-time space outside of Fort Worth."

The slide show and the ending need nothing more.

## The slide show

![A picture from the slide show](images/kennedy-show.png)

The batch file doesn't run `KSHOW.EXE`: you run it yourself (`excavate kennedy KSHOW.EXE`).
It's the story up to the game's start, Adam Cooper's mission log from his time sphere outside
Fort Worth on November 15, 1963: time anomalies in the 2080s, the rogue time traveler who
engineered the assassination, the stolen Time Sphere. It takes a key a page, and returns to
DOS with exit code 3 after "END OF ENTRY." On a monochrome card it refuses to run.

It draws in the EGA's 320 by 200 mode in 16 colors, or, on a CGA, in its 320 by 200 mode in
four. Its pictures are IFF ILBM files, the picture format of Electronic Arts' Interchange File
Format, which the Amiga made common: `JFKT1`, the title, and the ten in `P`, one after another
in the one file. Each is 320 by 200 in four bit planes, compressed, with a chunk of Paragon's
own besides the standard ones: `CCGA`, two bytes, which differ from picture to picture and look
to give the colors it's shown in on a CGA. Strings left in `KSHOW.EXE` ("Adding CGA info to EGA
IFF ILBM file", "Raster width", "Color register") show it was built from Paragon's tool for
making and checking these files.

`SHOW.DAT` is the show's script and its text. It starts with 1Ah, then two words: the script's
length (739 bytes) and the text's (4,508), which follow in that order. The text is lines ended
by a zero, and the script is one-letter commands:

| Command | What it does |
|---|---|
| `t` | Starts a page of text |
| `d` and a word | A line of the text, at that offset in it, as it is |
| `c` and a word | A line, centered |
| `p` | Waits for a key |
| `g` and a double word | Shows the picture at that offset in `P` |
| `z` | The end |

The show is 14 pages of text and 10 pictures. Any other letter stops it: "ABORT: Illegal
instruction".

## How the game hands over to its ending

When the game ends with you on Air Force One, it exits with code 2, and the batch file runs
`rund Z k0`. `RUND.EXE` won't run any other way: given other arguments, or started by itself,
it says "Bad command or file name", as DOS would, and exits with code 1.

It also needs a token from the game, left in memory. The BIOS keeps 16 bytes at 0000:04F0
(0040:00F0) as an "inter-application communication area": nothing of the BIOS's or DOS's own
is kept there, and one program can leave something there for the next. As it exits, the game
clears the area, then, for the three endings that go on to Air Force One, writes in it:

| Bytes | What |
|---|---|
| 0 to 9 | `1012ABACAB` |
| 10 | Whether the sound is on |
| 11 | Which of the three it was, 0 to 2: by what the ending does with it, how many of your agents are still out in the field (none, one, or more) |

`RUND.EXE` checks for `1012ABACAB` and reads the two bytes after it, then clears the area, so
the token is good for one run. The last byte decides a line of the winning ending: "The
President listens to your predicament with your one agent" (or "agents"), and helps find them.

![Air Force One](images/kennedy-air-force-one.png)

The ending opens on Dave Powers, who has heard the same story from three people and arranged
a meeting on Air Force One, en route from Fort Worth to Dallas. Then it's a conversation: 15
minutes of flight, Kennedy in the top window, Powers in the middle, and you typing in the
bottom. `RUND.EXE` matches what you type against keywords, misspellings included ("veitnam",
"hienekens", "kruschev"), and reads its dialog from `K.DAT`. Its credits: "Kennedy Dialog Programming
by Thomas J. Holmes" and "Kennedy Dialog Written by John Antinori and Bill Snyder".

Its strings hold the endings:

- **The win.** On landing at Love Field, Kennedy's appointments secretary announces the
  President has the flu and is cancelling the rest of his Texas trip. Kennedy has you flown
  back to Fort Worth, your team gathers in the Time Sphere, and then a message comes in on
  every trans-continuum frequency: the rogue time traveler, who is the first Adam Cooper,
  from the 2087 where Kennedy lived. He promises that "our paths will cross again", and the
  game ends there.
- **Kennedy saved, Cooper lost.** The motorcade through Dallas is cancelled, and Lee Harvey
  Oswald is arrested for conspiring to kill the President. But Adam Cooper hasn't returned to
  the time sphere, and his staying in 1963 brings the continuum down: "The Highfather has
  triumphed."
- **The assassination.** At 12:30 on Friday, November 22, it goes ahead, and "the past,
  present, and future are destroyed."

The win leaves the time traveler free, with his own time sphere.

## Still to look at

Playing it; the ending's dialog in `K.DAT`; the game's data files; and the "slow adapter"
setup.
