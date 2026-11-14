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

A room's picture is made of tiles, 16 by 16, laid out by a map. Its first 16 bytes give its
width and height in tiles, the number of tiles, the window it's seen through, and two counts.
Then come its palette (colors 0 to 127), the tiles from byte 400, and the map, a byte for each
place, the number of its tile. The colors above 127 belong to the people and the screen's
frame. A room can be bigger than its window: the cargo hold is 60 tiles wide and scrolls, the
cliff 33 tiles high, and some rooms are maps seen from above, the alley's streets, the hallways
and a maze.

![Six rooms: the bar, the Golden Desire club, the gear room, the castle's cellar, a room of crated art, and Mason's apartment](images/countdown-rooms.png)

![The alley, from above](images/countdown-alley.png)

Which palette a room is shown in isn't in its picture. In the game, the cell is shown in its
own colors, with `MAN.COL`, Mason's palette, above them. The ledge outside uses its own colors
too, with `WALL.COL` for Mason, a second palette for him in the same file, `TITLE.CNT`. So the
game picks Mason's colors for each scene. The game itself draws the score, the date and the
action bar, in colors 1 to 4 and 7. But a few rooms, the bar among them, look right only in a
`.COL` for all 256 colors, and we haven't been there in the game yet. The tool picks the
palette the picture looks smoothest in. One room, McBain's apartment, comes out dark either way.

After the map come the two things the counts in the header count: lines and rectangles, each
four words. The lines edge the floor, and Mason can't walk across them: the cell has 12. The
rectangles are where things can be clicked, one for each entry in the table of the room's
`.DAT`, in the same order. So a thing can be clicked in more than one place: the doctor's
office has four rectangles for its file drawers and five for its books. They're in the room's
own pixels, and the screen shows the room's window centered across it, from the line under
the score.

![Mason's cell, with the lines the floor ends at in red and the rectangles of its things in green](images/countdown-shapes.png)

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

### The rooms' text

A room's `.DAT` is the room's things, each with its name and what each action says about it:
the action's place in the bar (0 for LOOK, 1 OPEN, 2 MOVE, 3 GET, 5 GO TO, 6 TALK, 7 TASTE)
and the text. USE, 4, is in none of them. Its table has one entry for each rectangle in the
room's picture. The cell, for one:

| Thing | Action | Text |
|---|---|---|
| Medical chart | LOOK | THE CHARTS READS: PATIENT NAME: MASON POWERS ACCIDENT VICTIM, SEVERE CONCUSSION, MULTIPLE LACERATIONS AND CONTUSIONS. SCHEDULED FOR EXPLORATORY SURGERY- DR. FAROUK HASHISH. |
| Cross | TASTE | IS THIS SOME NEW RELIGIOUS RITUAL? |
| Radiator | TALK | PERHAPS THIS SHOULD BE YOUR PERMANENT RESIDENCE. |
| Food | TALK | YOU SAY SOMETHING TO THE FOOD AND FOR A MOMENT YOU THINK YOU HEAR A RESPONSE. |
| Writing | LOOK | A MESSAGE SCRAWLED IN BLOOD ON THE WALL. IT READS 'I MAY NOT KNOW REALITY, BUT I KNOW WHAT I LIKE!' |

Every thing is in a table but one, a door in the second hallway. Things that turn up later,
such as the knife under Mason's food, are in the table from the start, and the code decides
whether they're there. After the things come the room's messages,
numbered, which are what its puzzles say: in the cell, "USING THE KNIFE YOU CHIP AWAY THE THE
WIRE SCREEN UNTIL FINALLY IT LOOSENS ENOUGH FOR YOU TO CRAWL THROUGH AND OPEN THE WINDOW", and
"YOU TRAP THE BUG IN THE CUP AND SNAP ON THE LID." When a message is shown, and what using a
thing does, isn't in the files: the puzzles are in `COUNT.EXE`'s code ([out of the
cell](#out-of-the-cell) has one). So are the inventory's names, from DART and POUCH to DOSSIER
and EXPLOSIVE (with a SCALPAL among them).

### The conversations

Talking is by approach: Mason can ask for help, hassle, be pleasant or bluff, in any order,
and a different line comes out of each sequence. A conversation's `.TRE` has four sections,
one for each approach he starts with. Each section is a tree four approaches deep, which is why
it has 88 records: 4 + 4 + 16 + 64. The first approach is the section's record of the same
number, 0 to 3. The second leads to one of records 4 to 7, the third to one of 8 to 23 (4 + 4
× the second + the third), and the fourth to one of 24 to 87 (24 + 16 × the second + 4 × the
third + the fourth). Records 0 to 3 that aren't the section's own are copies, never reached.

A record is 8 bytes: Mason's line, the answer, the face the person makes as they give it (0 to
3), and then commands, up to a dot:

| Command | What it does |
|---|---|
| `02` and a topic | Adds the topic to ASK ABOUT ("HIS NAME IS DR. FAROUK HASHISH.") |
| `05` and two bytes | Kills Mason, by the answers it comes with ("I'M AFRAID NOT SCUM, EAT LEAD!"); not seen in the game yet |
| `08` | Ends the talk ("DROP DEAD YOU MURDERING SCUM.", and Mason is back in the room) |
| `0B` | Ends that line of questioning: the game says, in red, "I DON'T KNOW MUCH MORE ALONG THESE LINES. TRY A NEW APPROACH." |

A record with none goes on. The rest of each record, and the records past each tree's end, are
leftovers from the editor they were written in: dots, commas and digits. In the game, HELP,
HELP, HELP, HASSLE with the guard gives records 0, 4, 8 and 25, one of the sequences the
manual's beginners' tips suggest. The last is "CONFIDENTIAL! WHO THE HELL DO YOU THINK I WOULD
TELL. GIVE ME A BREAK HERE!", answered with "WELL... I GUESS IT WOULDN'T HURT TO TELL YOU. HIS
NAME IS JACK QUINN." Its commands are `02 12 0B`: JACK QUINN goes on the list, the score goes
up a point, and the line ends.

Then come the lines, in three blocks: what Mason says, what he's told, and the answers to ASK
ABOUT, each with the topic it answers and a face. The topics are 28 names in `COUNT.EXE`, from
ACCIDENT and MASON POWERS to THUNDERBOLT, BLACK DECEMBER and the DEC. MANIFESTO. Asked about
STORMBRINGER, Lisa Loomis says "THAT'S THE NAME OF MCBAIN'S 'MYTHICAL' MOLE WORKING INSIDE THE
CIA"; asked about JACK QUINN, "JACK QUINN HAD YOU PUT AWAY."

### The newspaper

The paper, *The American Eagle*, has a President Thomas Carroll losing his lead to L.J.
Leeland, a former NATO commander, over terrorism, ahead of a summit on terrorism in Paris.

## Out of the cell

With the mouse, clicking an action and then a thing puts them on the command line, and
clicking the thing again carries it out. A long message holds the game, the clock
included, until the next click. The way out of the cell follows the manual's beginners' tips:

1. GET the CUP by the sink.
2. USE the CUP ON the BUG, a rhinoceros beetle on the wall by the door: "YOU TRAP THE BUG IN
   THE CUP AND SNAP ON THE LID."
3. TALK to the guard. His face is at the door's window for a second or two, about once a
   minute, and he can only be talked to then.
4. OFFER him the bug: "I HAVE BEEN LOOKING EVERYWHERE FOR A RHINOCEROS BEETLE! I'LL HAVE SOME
   FOOD SENT TO YOU RIGHT AWAY." When Mason leaves the talk, a tray comes in under the door,
   and under it is a knife.
5. GET the KNIFE, USE it ON the WINDOW on the right, which chips its wire screen away, GET the
   locker KEY at the foot of the bed, and GO TO the WINDOW.

Mason climbs out onto a ledge, high over the water, with 20 of the 515 points.

![The guard, and Mason out on the ledge](images/countdown-escape.png)

The knife was the hard part, and the code shows why. USE with the knife (item 5) on thing 12,
the window, says "YOU'RE NOT CLOSE ENOUGH TO DO THAT" unless Mason is at 180 or more across,
and between 44 and 64 down, a little out from the back wall. The sandbags under the window keep
him from going farther right along the wall itself. And the mouse won't take him there: it
walks him the longer way first, then the other, and stops for good at the first line in the
way. Sent toward the window from the back of the room, he walks right along the back wall
until the floor's corner stops him, and never comes forward. The arrow keys walk him one step
at a time, so a little down and then right gets him there. Around it, the code checks the
other USEs the same way, the item and the thing by number, and keeps a flag for each change in
the cell: the bug caught, the key taken, the screen chipped.

## Still to look at

- The cells (`.GPH`) and the animations (`.ANM`).
- The rest of the puzzles in `COUNT.EXE`, room by room, and how a conversation opens ASK ABOUT
  and OFFER.
- Which rooms take a `.COL` for all their colors, and where the game picks it.
- The game itself, from the ledge on.
