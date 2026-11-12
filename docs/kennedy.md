# Guardians of Infinity: To Save Kennedy

*Guardians of Infinity: To Save Kennedy* (Paragon Software, 1988). You direct a timeship and its
crew, sent back to stop the assassination of John F. Kennedy. The box held a novella, a reference card and a registration card, which a copy of the
game today keeps as PDFs beside it.

![The timeship arrives](images/kennedy-start.png)

## What's in the folder

| File | What it is |
|---|---|
| `KENNEDY.BAT` | How it's started: runs the game, then, by its exit code, `RUND.EXE` or a prompt to play again; `KENNEDY SETUP` chooses the display |
| `GOI.EXE` | The game, 153,152 bytes |
| `RUND.EXE` | Run by the batch file on the game's exit code 2 |
| `KSHOW.EXE`, `SHOW.DAT` | A show of some kind, not yet looked at |
| `$RUN.OVL`, `$KS.OVL` | The display's overlays: setup swaps them, for a "fast" adapter or a "slow" CGA that shows snow |
| `SYS$MSG.DAT` | The interface library's messages |
| `GETKEY.COM` | Waits for a key and exits with it, for the batch file |
| `GOIDAT.SAV`, `K.DAT`, `MIRKWOOD.DAT`, `SHERWOOD.DAT`, `NWS`, `P`, `JFKT1` | The game's data, not yet looked at |

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

## Still to look at

Playing it; `RUND.EXE`, which runs on exit code 2, and `KSHOW.EXE`; the data files; and the
"slow adapter" setup.
