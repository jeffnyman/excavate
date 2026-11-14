# Countdown

*Countdown* (Access Software, 1990) is what Access called an interactive movie: Mason Powers,
an American intelligence officer in Istanbul, wakes up in the Sanctuary, an asylum "for people
who know too much", with no memory of how he got there. The manual gives him 96 hours, from
midnight on Wednesday, October 7, to midnight on Saturday, October 10, and promises "something
very nasty" if he runs out. Created and designed by Brent Erickson and Chris Jones; RealSound by
Jon Clark and Steve Witzel.

![The title, over Istanbul](images/countdown-title.png)

## What's in the folder

| File | What it is |
|---|---|
| `COUNT.EXE` | The game, 105,205 bytes |
| `INSTALL.EXE` | The hard disk installer |
| `*.CNT` | 67 files of the game's pictures, sounds and scenes, named for what they hold: rooms and people (`MASONCEL`, the cell; `DOCTOR`, `NURSE`, `YURI`), the two flashbacks (`FLASH0`, `FLASH1`), five sounds (`SND1` to `SND4`, `SND7`), the title, two endings (`ENDING`, `ALTEND`), the font |

What's in the `.CNT` files is below, in [the files](#the-files).

## The start

`COUNT.EXE` asks two questions in text mode: how you'll play (mouse, joystick or keyboard),
and what you'll hear it on: RealSound, the AdLib, IBM's PS/2 speech adapter, the Sound
Blaster, MSound, the Covox Sound Master, or nothing. Then it switches to the VGA's mode 13h,
320 by 200 in 256 colors, for the rest of the game.

It keeps its own time. As it starts it sets DOS's time to midnight, and its date to the day it
already is, and it doesn't ask DOS for the time again: the clock in the corner of the screen, which
the manual says is "game" time, starts at 00:00. Its timer runs at 60 Hz rather than the PC's
18.2, counter 0 loaded with 19,886.

The introduction is the scream, in blue text on black ("SCREAMING... I HEAR SCREAMING... IT
STOPS WHEN I CLOSE MY MOUTH..."), the scream itself, Access's logo, and the title and credits
over the rooftops of Istanbul. Esc skips each part. Then Mason is in his cell, with the score
("000 OF 515"), the date and the clock above, and the actions below: look, open, move, get,
use, go to, talk, taste and travel.

![Mason's cell](images/countdown-cell.png)

## RealSound

RealSound was Access's way of playing recorded sound, speech and screams and music, on the
PC's speaker alone, which was only ever built to beep: "a patent protected technological
breakthrough that allows your MS DOS computer to play high quality digitized sounds ...
without adding any hardware", as the manual has it.

The speaker can only be on or off. RealSound plays a sound by how long it's on. While a sound
plays, the game sets the timer's counter 0 to interrupt every 72 ticks, 16,572 times a second,
and counter 2, the speaker's, to mode 0, where its output goes low when it's loaded and high
when it has counted down. Each interrupt loads counter 2 with the next sample, a count from 1 to
69: the speaker is off for that many ticks and on for the rest of the 72. A pulse 16,572 times
a second is too fast for the speaker's cone to follow, so it follows the pulses' average, and
the average is the sound. The scream at the start, from `SND1.CNT`, is about two seconds of
this.

Excavate's speaker had to change for it. It used to sample the speaker's signal at a few
moments in each of its own samples, 44,100 a second, as the timer was set when the sample was
made. That's fine for a tone, but here the pulses are barely three of its samples long, and
their edges folded back into the sound as noise: in the scream, about a third of the power was
the 16.5 kHz carrier, and less than a third was below 3 kHz, where the scream is. Now the
speaker counts, a timer tick at a time, how long the signal was on in each sample, and the
machine settles it before each change to the counter or port 61h, so a sample a change falls
in is counted part with the old setting and part with the new. A low-pass at about 7 kHz stands
for the cone's weight. In the same scream, the carrier is now 3% of the power and 90% is below
3 kHz. Below 3 kHz, the sound follows the counts the game wrote with a correlation of 0.996,
up from 0.98.

The other sound devices work too. The Sound Blaster asks for its port and interrupt (Excavate's
is at 220h, on IRQ 5) and is sent the scream by DMA, 16,320 bytes at 8,333 Hz. The AdLib plays it
as well.

## The files

`tools/access/countdown_files.py` reads them (see [the tools](../tools/README.md)): it lists
every part of every file, prints their text, and writes the rooms, the pictures and the sounds
as PNG, GIF and WAV files.

### The packing

A `.CNT` file is a count of its parts, a table of where each starts, and the parts, 360 of them
in the 67 files. 314 are files from the developers' own disks, packed, each with a header of
122 bytes: `MDmd`, the method, the sizes unpacked and packed, the file's DOS attributes, time and
date, a check, and its name and folder. The packing is LZW, as GIF pictures are packed: codes from
9 bits to 13, low bit first, 256 to start the table again and 257 to end. The game's own
decoder, in `COUNT.EXE`, takes its codes to 13 bits, where GIF stops at 12. The other 46 parts
are stored as they are: palettes, small tables, and 14 whole screens as GIF pictures.

So every packed part still has its name, where it was, and when it was last saved. The kinds,
by their extensions:

| Kind | How many | What it is |
|---|---|---|
| `.GPH` | 90 | Pictures of the people and things, in cells |
| `.RS` | 49 | RealSound recordings |
| `.ANM` | 43 | Animations |
| `.PLY` | 38 | The rooms' pictures |
| `.DAT` | 36 | The rooms' text and what can be done in them |
| `.COL` | 35 | Palettes |
| `.TRE` | 15 | Conversations, what Mason can say and what's said back |
| `.BAK`, `.BK2`, `.TMP` | 8 | Backups and a temporary file, packed in by mistake, by the look of it: the newspaper's text ships as `NEWS.BAK` |

The folders show the work's layout, mostly under `C:\IBM\COUNT\`, a folder for each part of the
story (`SANCT`, `ALLEY`, `GOLDEN`, `BOMB`), the sounds in `SOUND`, the palettes in `DATA`, and
some on a `D:` drive. The title's four recordings come from `D:\COUNTDEM\DEMO`, a demo's folder. The dates
run from May 14, 1990 (the title's palette) to October 10, 1990 at 12:16 (the Golden Desire
club's picture), which is, by chance or not, the date the game's own clock runs out on.

### The rooms

A room's picture is made of tiles, 16 by 16, laid out by a map: its width and height in tiles,
the number of tiles, and the window it's seen through, in its first 16 bytes; then its palette,
colors 0 to 127; the tiles from byte 400; and the map, a byte for each place, the number of its
tile. The colors above 127 belong to the people and the screen's frame. A room can be bigger
than its window: the cargo hold is 60 tiles wide and scrolls, the cliff 33 tiles high, and some
rooms are maps seen from above, the alley's streets, the hallways and a maze.

![Six rooms: the bar, the Golden Desire club, the gear room, the castle's cellar, a room of crated art, and Mason's apartment](images/countdown-rooms.png)

![The alley, from above](images/countdown-alley.png)

Which palette a room is shown in isn't in its picture, and we haven't found where the game keeps
it: most use their own colors, with a `.COL` for the people above them, but a few, the bar
among them, use a `.COL` for all 256. The tool picks the palette the picture looks smoothest in.
One room, McBain's apartment, comes out dark either way. And after the map there's more, a few
hundred bytes we don't read yet.

### The pictures

The 14 GIF pictures are the whole screens: the title, the newspaper, the map of Europe Mason
travels by, the CAD's keypad, the flashbacks, and the endings, in Paris.

![The map of Europe, and the Eiffel Tower](images/countdown-pictures.png)

### The sounds

A RealSound recording starts with 32 bytes: `STEVE`, for Steve Witzel, who wrote RealSound; how
many of the timer's interrupts each sample lasts, 2 for most, about 8,286 samples a second; `H`;
and the number of samples. Some of these headers end with scraps of x86 code where they should be
zeros, left over in the memory of the program that wrote them. The samples are the counts
themselves: in Excavate, the 32,640 counts the game loaded into the timer for the scream are the
16,320 samples of `SCREAM5.RS`, each twice, exactly. There are 42 different recordings, among
them `CRZYLAF`, `LAUGH2`, `SNORE`, six `PARROT`s, `EXPLODE`, `DIEPIG`, `IKNOW` and `LOVEYOU`.

### The text

All the text is plain once unpacked: what each thing in a room is and what happens when it's
looked at, opened or moved ("A TURKISH BIBLE. ALL THE BOOKS ARE IN TURKISH. THE BOOKS ARE
COVERED IN DUST."), what Mason can say in each conversation and what's said back, and the
newspaper's stories. The paper, *The American Eagle*, has a President Thomas Carroll losing his
lead to L.J. Leeland, a former NATO commander, over terrorism, ahead of a summit on terrorism in
Paris.

## Still to look at

- The cells (`.GPH`), the animations (`.ANM`), and how the rooms' text (`.DAT`) and the
  conversations (`.TRE`) are laid out, which would give the game's logic.
- The game itself, from the cell on.
