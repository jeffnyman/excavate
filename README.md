# Excavate

**Excavate** plays a chosen set of DOS games, as they were. It's a C# DOS PC emulator built for
these games alone, not to be a whole PC: you pick a game and it plays, at the speed it was meant
to run, with modern controls.

Excavate is also a dig. Each game is taken apart to show how it works: how it starts, what it
asks of the PC, and what the people who made it had to do to get it running on the machines of
the time. Those notes will go in a `docs` folder as each game is worked on.

## The games

| Game | Year | Short name | Where it stands |
|---|---|---|---|
| *Portal* | 1986 | `portal` | Recognized; boots from its own disks, which Excavate doesn't do yet |
| *Metropolis* | 1987 | `metropolis` | Recognized; boots from its own disks, which Excavate doesn't do yet |
| *Guardians of Infinity: To Save Kennedy* | 1988 | `kennedy` | Not yet tried |
| *The President Is Missing* | 1988 | `president` | Not yet tried |
| *Presumed Guilty* | 1989 | `guilty` | Not yet tried |
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
- **F11** is full screen, **Shift+F11** the pixel shape, **F12** full speed on and off, and
  **Pause** pauses.
- **Ctrl+F5** saves the whole machine's state, and **Ctrl+F9** loads it back.

## How it's built

`Excavate.Core` is the machine: an x86 processor, the timer, interrupt and DMA hardware, the
keyboard controller and game port, VGA, FM, MT-32 and Sound Blaster sound, and DOS answered
directly in C# instead of being booted. `Excavate.Player` puts it in a window with SDL3.

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
through `EXCAVATE_TESTDATA` or a `_testdata` folder.

## License

Excavate is © 2026 Jeff Nyman, under the GNU General Public License, version 3
([LICENSE](LICENSE)). The games, their files and their logic are their publishers' and are not
under that license. See [NOTICE.md](NOTICE.md).
