# Excavate

**Excavate** plays a chosen set of DOS games, as they were. It's a C# DOS PC emulator built for
these games alone, not to be a whole PC: you pick a game and it plays, at the speed it was meant
to run, with modern controls.

Excavate is also a dig. Each game is taken apart to show how it works: how it starts, what it
asks of the PC, and what the people who made it had to do to get it running on the machines of
the time. Those notes are in [docs](docs/), a game at a time.

## The games

| Game | Year | Short name | Where it stands |
|---|---|---|---|
| *Portal* | 1986 | `portal` | Plays through to its end, from its disk images; there's a [walkthrough](docs/portal-walkthrough.md) |
| *Metropolis* | 1987 | `metropolis` | Recognized; boots from its own disks, which Excavate doesn't do yet |
| *Guardians of Infinity: To Save Kennedy* | 1988 | `kennedy` | Starts, and the timeship arrives; its slide show runs, and its ending can be won; [notes](docs/kennedy.md). The slide show is `excavate kennedy KSHOW.EXE` |
| *The President Is Missing* | 1988 | `president` | Logs on and shows its files, on the EGA or CGA; [notes](docs/president.md). Its setup is `excavate president SU.EXE` |
| *Presumed Guilty* | 1989 | `guilty` | Starts, shows its menus, messages and DNA analysis on the EGA or CGA, and logs off; [notes](docs/guilty.md). Its setup is `excavate guilty SU.EXE` |
| *The Omnicron Conspiracy* | 1989 | `omnicron` | Not yet tried |
| *Countdown* | 1990 | `countdown` | Not yet tried |
| *The Orion Conspiracy* | 1995 | `orion` | Not yet tried |

None of the games are included: Excavate plays copies you have. Each game runs its own program,
never a launcher, menu or loader that a collection added beside it later.

## Playing

With the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```
dotnet run --project src/Excavate.Player -c Release -- "<the game's folder>"
```

A folder given once is remembered, and the game is then played by its short name:

```
dotnet run --project src/Excavate.Player -c Release -- countdown
```

With no game named, a menu lists them. `--help` lists the options.

### In the game

- **The keyboard** is the PC's, key for key.
- **The mouse:** a click in the window gives the game the mouse, and Ctrl+F10 takes it back.
- **A gamepad** is the joystick. Plug it in before starting: games look for one as they start.
- **In *Portal***, the mouse is a joystick too, for a game with none of its own: once it's
  captured, a short move steps the highlight that way and a click chooses, if you said yes to
  a joystick at the start.
- **F11** is full screen, **Shift+F11** the pixel shape, **F12** full speed on and off, and
  **Pause** pauses.
- **Ctrl+F5** saves the whole machine's state, and **Ctrl+F9** loads it back.
- **Ctrl+F1** to **Ctrl+F4** put disk 1 to 4 in drive A:, for a game played from its disks.
  Where Excavate knows a game's prompts for another disk, as in *Portal*, it answers them for
  you: the disk goes in and the game carries on.
- **Ctrl+F11** switches a CGA game between an RGB monitor and a composite one, where patterns
  of dots blend into colors of their own; `--composite` starts with it.

## How it's built

`Excavate.Core` is the machine: an x86 processor, the timer, interrupt and DMA hardware, the
keyboard controller and game port, a floppy drive, VGA or CGA (with its composite colors), the
PC speaker, FM, MT-32 and Sound Blaster sound, and the BIOS and DOS answered directly in C#
instead of being booted. `Excavate.Player` puts it in a window with SDL3.

A game played from floppies is given its disks' images, which stay untouched: what it writes
goes to a copy in its save folder. Where a game's copy protection lived on its original disks in
a form a plain image can't hold, the player puts it back on the disk, so the game's own program
runs unchanged. How each game was worked out is in [docs](docs/).

Excavate began as a copy of the core of [Confederation](https://github.com/jeffnyman/confederation),
the same kind of emulator built for the *Wing Commander* games, and goes its own way from there.
Neither is a DOSBox fork: their logic is ported from DOSBox where that helps, but DOSBox is never
wrapped or linked.

## Building and testing

```
dotnet build -c Release
dotnet test -c Release
```

The tests that need a game's files look for them in `EXCAVATE_GAMES`, or in an `_excavating`
folder above the repository, and pass without checking when they aren't there. The processor
tests use the [SingleStepTests](https://github.com/SingleStepTests) sets, found the same way
through `EXCAVATE_TESTDATA` or a `_testdata` folder. With `EXCAVATE_PLAYTHROUGH` set, a test
also plays *Portal*'s story from the login to its last message, which takes a couple of minutes.

## License

Excavate is © 2026 Jeff Nyman, under the GNU General Public License, version 3
([LICENSE](LICENSE)). The games, their files and their logic are their publishers' and are not
under that license. See [NOTICE.md](NOTICE.md).
