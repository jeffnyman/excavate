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

Each `.CNT` file starts with a count and a table of offsets to its parts, and the parts start
with `MDmd`. Their format is still to read.

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

## Still to look at

- The `.CNT` files: the pictures, the scenes and the sounds.
- The other sound devices: the AdLib and the Sound Blaster.
- The game itself, from the cell on.
