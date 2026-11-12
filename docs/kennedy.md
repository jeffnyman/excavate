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
a meeting on Air Force One, en route from Fort Worth to Dallas. Then it's a conversation, with
Kennedy in the top window, Powers in the middle, and you typing in the bottom. Its credits:
"Kennedy Dialog Programming by Thomas J. Holmes" and "Kennedy Dialog Written by John Antinori
and Bill Snyder".

## The conversation

The flight is 15 minutes, counted down on the screen as you talk. In them, you have to
convince Kennedy that you're from the future, by telling him things only he and the people
closest to him could know. Each one
he can't explain is worth one to three points, and six convince him. `RUND.EXE` runs the
conversation, and `K.DAT` holds it. `tools/paragon/kennedy_dialog.py` prints all of it, and
`--points` lists every answer that gives points (25 in all), with what to say for it.

### What you type

`RUND.EXE` cuts what you type into words, everything but letters, digits, `'` and `-` being a
space between them. It replaces some words with others, from a table of its own: "kennedy",
"president", "john", "you" and "your" become "jfk"; "assassin", "gunman" and "gunmen" become
"oswald"; misspellings it expected are corrected ("veitnam", "hienekens", "kruschev"); and
"shit", "dirtbag" and their like become one word, which Kennedy has an answer for. The
patterns are written in those words: "jfk_are_my_kind_of_shah" is "you are my kind of shah".

A pattern is words in an order. Words with a space between them have to come in that order,
with anything between them; words joined by `_` have to come one right after another.
"jfk life danger" matches "Your life is in danger in Dallas"; "westward_look_the_land_is_bright"
needs the line as it was said.

Not every pattern can match. The table turns "gunman" and "gunmen" into "oswald" before the
patterns see them, so "gunman grassy_knoll" and "gunmen grassy_knoll" never match: in the
running game, "a gunman on the grassy knoll" gets the answer for nothing matched. "attempt to kill jkf" has a typo, and
matches only if you make the same one.

### How an answer is chosen

`K.DAT` is five sections, each with its opening line, its patterns, and its answers in order.
Each answer is for sets of patterns, any one set of them; a set is one pattern or several, all
of which have to have matched. It goes through them twice:

1. **For what you just said.** The first answer, in order, whose set matched this time and
   whose conditions hold is the one given.
2. **For what you've said before.** If none was, it goes through again with everything you've
   said so far. Some answers are only for this pass: they bring back a subject ("You mentioned
   Krushchev before. Do you have anything else to say involving him?"). The answers for when
   nothing matches come in here too.

So a set of several patterns can be said over several lines: "1960", "convention" and the
six things they had for breakfast at it are one answer, worth two points.

### What an answer does

An answer is a string of commands, a letter each, some taking a string between delimiters:

| Command | What it does |
|---|---|
| `k/text/`, `d/text/` | Kennedy or Powers says it |
| `+n`, `-n` | Gives or takes away points; at six, Kennedy is convinced |
| `?/n/` | Goes on only if you've never said pattern n |
| `u/n/` | Forgets you said pattern n; `c` forgets everything |
| `f/n/` | Sets flag n; `=/n/` goes on only if it's set, `#/n/` only if it isn't |
| `g`, `n` | Goes on only in the second pass, or only in the first |
| `^` | Is said once: the answer is taken off the list |
| `>` | Goes to the back of the list, so the answers for nothing matched take turns |
| `r/name/` | Goes to another section |
| `e` and a letter | Ends it: `0` or `t` Kennedy goes on to Dallas; `1` he turns back, with you |
| `!` | Never goes on |

A condition that doesn't hold stops the answer before anything is said, and the next one is
tried.

### The file

`K.DAT` starts with 1Ah, a word, and the number of sections, then each section's name (13
bytes), its length (a double word), a byte, and its place in the file (a double word). A
section starts with a word, then the number of its patterns and each one's place; the number
of its answers and, for each, the place of its sets of patterns in the section's table and
the place of its script; and the table's place. All the places count from the end of that
header, where the opening line starts. The text, the opening line, the patterns and the
scripts, is strings ended by a zero, every byte XORed with 1Ah. The table isn't: it's the
number of its words, then for each answer the patterns of its sets, FFFFh after each set and
FFFEh at the end, with FFFDh for nothing matched.

### The sections

- **`k0`, the opening.** "What do you know about this danger?" Talk of Oswald or the Book
  Depository takes you to `k1`; saying you're from the future takes you to `k3`.
- **`k1`, the plot.** "Were you part of the conspiracy?" Say yes, and Kennedy turns back for
  Washington, but takes you with him. Say no, and it goes on to `k2`; contradict yourself twice,
  and he gives up on you. Saying you're from the future goes on to `k3`.
- **`k2`, the source.** Then how do you know about it? Admit you were part of it after all, and
  it ends as a yes in `k1`; say you're from the future, and it goes on to `k3`; say nothing he
  can use three times, and he goes on to Dallas.
- **`k3`, the future.** "Convince me you are from the future." The heart of it: 101 answers to
  125 patterns, about his family, his staff, his habits, and the crises of his presidency.
  Most of them he can explain away: it was in the papers, or anyone in his office could have
  seen it. Fifteen give points. Three points each: a line, "westward look, the land is
  bright", that only Powers, O'Donnell and he could know he said; Khrushchev's letter about the
  "knot of war"; and the Gold Star Mothers, with where Powers and Kennedy first met. Two each:
  a frappe in Boston on a secret ice cream run, the bomb shelter passes of the Cuban missile
  crisis, his breakfast at the 1960 convention, and "I'll get you, you so help me God". The
  rest give one.
- **`kt`, the landing.** When the time runs out, Air Force One lands in Dallas. "Why should we
  trust you?" Powers suggests changing the route and searching around the Book Depository, but
  whatever you say next, Kennedy goes on: "Future, Dave? Really!!"

So "I am from the future", "Westward look, the land is bright" and "Krushchev's letter: the
knot of war" win it in three lines.

![Kennedy, astounded](images/kennedy-westward.png)

### How it ends

With six points, Powers or Kennedy agrees that you've been convincing, and the President calls
off Dallas:

- **The win.** On landing at Love Field, Kennedy's appointments secretary announces the
  President has the flu and is canceling the rest of his Texas trip. Kennedy has you flown
  back to Fort Worth, your team gathers in the Time Sphere, and then a message comes in on
  every trans-continuum frequency: the rogue time traveler, who is the first Adam Cooper,
  from the 2087 where Kennedy lived. He promises that "our paths will cross again", and the
  game ends there.

The other endings:

- **Kennedy saved, Cooper lost** (`e1`). Kennedy believes there's a plot, but not that you're
  from the future: he takes you back to Washington as a conspirator, or as a spy. The motorcade
  through Dallas is canceled, and Lee Harvey Oswald is arrested for conspiring to kill the
  President. But Adam Cooper never returns to the time sphere, and his staying in 1963 brings
  the continuum down: "The Highfather has triumphed."
- **The assassination** (`e0` and `et`). Kennedy isn't convinced, or the time runs out. At
  12:30 on Friday, November 22, it goes ahead, and "the past, present, and future are
  destroyed."

## Still to look at

Playing the game itself; its data files; and the "slow adapter" setup.
