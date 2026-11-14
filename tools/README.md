# Tools

Scripts used to take the games apart. They read a game's files and print what they find; none
of them is needed to build or run Excavate. They need Python 3.9 or later, and nothing else.

## Portal

`portal/` reads *Portal*'s story from its three disk images: each level's items, where they are,
what they need read first, and what they lead to. How the game keeps all of this is in
[docs/portal.md](../docs/portal.md#the-story).

Both scripts take the game's folder, the one with its disk images. Disk 1 is the one with
`PORTAL.EXE` on it, and the others follow in the order of their names, as the player has them.

```
python tools/portal/story.py <the game's folder> <level>
python tools/portal/story.py <the game's folder> <level> <item, in hex>
python tools/portal/walkthrough.py <the game's folder>
```

- **`story.py`** lists a level item by item: its flag byte, dataspace, title, prerequisites and
  successors. An asterisk marks the items that must be read to end the level. Given an item's
  number, it prints that item's text.
- **`walkthrough.py`** writes the levels of the walkthrough as Markdown. They are the part of
  [docs/portal-walkthrough.md](../docs/portal-walkthrough.md) after its introduction.
- **`portal_story.py`** is what both share: reading a file from a disk's root directory,
  turning the game's byte addresses into places in an image, and decoding a level's table,
  records, titles and text.

## Presumed Guilty

`cosmi/guilty_case.py` maps *Presumed Guilty*'s case from its files: `MSG.TXT` for every entry,
what it sets off and how long it waits, and `STATUS.TBB` for where each stands as the case
ships. How the game keeps them is in [docs/guilty.md](../docs/guilty.md#how-the-case-runs).

```
python tools/cosmi/guilty_case.py <the game's folder>
python tools/cosmi/guilty_case.py <the game's folder> <name>
python tools/cosmi/guilty_case.py <the game's folder> --falcon
```

- With only the folder, it prints the case from what has arrived as it ships, the welcome
  letter, entry by entry, each under what sets it off, and then the entries nothing sets off:
  what you investigate yourself, and what the game sets off in its code.
- With a name, it prints that entry, what sets it off, what it sets off, and its text. A name
  can be given as the game writes it (`OBETAFAST-`) or without its padding or kind's letter
  (`betafast`).
- With `--falcon`, it prints Falcon's chain, the passphrase a character at a time, with the
  time each step waits and the time to the end.

## Guardians of Infinity: To Save Kennedy

`paragon/kennedy_dialog.py` reads the conversation aboard Air Force One that ends the game from
`K.DAT`: each section's opening line, its patterns, and its answers in order, with what each
is for and what it says and does. How `RUND.EXE` runs it is in
[docs/kennedy.md](../docs/kennedy.md#the-conversation).

```
python tools/paragon/kennedy_dialog.py <the game's folder>
python tools/paragon/kennedy_dialog.py <the game's folder> <section>
python tools/paragon/kennedy_dialog.py <the game's folder> --points
```

- With only the folder, it prints every section; with a section's name (`k0` to `k3`, or
  `kt`), that one.
- With `--points`, it lists every answer that gives points, with what to say for it. Six
  convince Kennedy.

`paragon/kennedy_files.py` reads the rest of the game's text: the news from `NWS`, and from
`MIRKWOOD.DAT` and `SHERWOOD.DAT`, each mission's orders and what the agents say on it, ring by
ring. The names of the people and the verbs come from `GOI.EXE`. How the game keeps them is in
[docs/kennedy.md](../docs/kennedy.md#the-games-files).

```
python tools/paragon/kennedy_files.py <the game's folder> news
python tools/paragon/kennedy_files.py <the game's folder> people
python tools/paragon/kennedy_files.py <the game's folder> person <number or name>
python tools/paragon/kennedy_files.py <the game's folder> tasks
python tools/paragon/kennedy_files.py <the game's folder> task <number or order>
```

- **`news`** prints every page, in its groups.
- **`people`** and **`tasks`** list each mission, with the orders that lead to it and the
  first thing said on it.
- **`person`** and **`task`** print one mission's text: its rings of passages, each with the
  agents who say it, and the words the script listens for.

`paragon/kennedy_missions.py` reads the missions' scripts: each stage's questions and the
answers they want, where and when the person can be found, and the reports that can come back,
with the conditions for each. How they run is in
[docs/kennedy.md](../docs/kennedy.md#the-missions).

```
python tools/paragon/kennedy_missions.py <the game's folder> person <number or name>
python tools/paragon/kennedy_missions.py <the game's folder> task <number or order>
python tools/paragon/kennedy_missions.py <the game's folder> --convinced
python tools/paragon/kennedy_missions.py <the game's folder> --check
```

- **`person`** and **`task`** print a mission, stage by stage.
- **`--convinced`** prints the three stages that each add a warning to Dave Powers; three win
  the game.
- **`--check`** reads every stage of every script to its end, and says how many did.

## The Omnicron Conspiracy

`firststar/omnicron_files.py` reads *The Omnicron Conspiracy*'s files: the rooms and the text
from `SCODE.EXE`, and the pictures from `ALLROOM0.DAT` to `ALLROOM3.DAT`, `PAKDATA` and the rest.
How the game packs them is in [docs/omnicron.md](../docs/omnicron.md#the-games-files).

```
python tools/firststar/omnicron_files.py <the game's folder> rooms
python tools/firststar/omnicron_files.py <the game's folder> things
python tools/firststar/omnicron_files.py <the game's folder> scripts [<name>]
python tools/firststar/omnicron_files.py <the game's folder> messages
python tools/firststar/omnicron_files.py <the game's folder> pictures <folder>
```

- **`rooms`** lists each set's pictures with the rooms that use them, and where each room's
  doors lead.
- **`things`** lists what's in each room as the game starts, each thing with what it holds, and
  what Ace carries.
- **`scripts`** disassembles what the things do: for each thing, or each whose name matches,
  the script that runs every frame, the one that runs when it's used, and a character's
  behavior, with the messages, menus and names they refer to written out. How the language
  works is in [docs/omnicron.md](../docs/omnicron.md#the-scripts).
- **`messages`** prints the game's text in the order it's kept, with "(Enter)" where a message
  goes on after an Enter.
- **`pictures`** writes every picture to the folder as a PNG file, twice its size, in the
  game's colors: each room's picture, the full screens, and each sprite file as a sheet of its
  frames. A sprite's frames are as the file has them, before the game changes any of a
  character's colors.

## Countdown

`access/countdown_files.py` reads *Countdown*'s `.CNT` files: the parts packed into them, with
the names, folders and dates they had on the developers' disks; the text in them; the rooms'
pictures; the GIF pictures; the cells and animations; and the RealSound recordings. How they're packed is in
[docs/countdown.md](../docs/countdown.md#the-files).

```
python tools/access/countdown_files.py <the game's folder> parts
python tools/access/countdown_files.py <the game's folder> text [<file>]
python tools/access/countdown_files.py <the game's folder> pictures <folder>
python tools/access/countdown_files.py <the game's folder> cells <folder>
python tools/access/countdown_files.py <the game's folder> sounds <folder>
python tools/access/countdown_files.py <the game's folder> room <file>
python tools/access/countdown_files.py <the game's folder> talk <file>
```

- **`parts`** lists every part of every file: its number, size unpacked, and its name, date
  and folder, or what kind of part it is when it's stored as it is.
- **`text`** prints the text in each part, or in one file's.
- **`pictures`** writes each room's picture, its whole map, as a PNG file twice its size, and
  the GIF pictures as they are.
- **`cells`** writes each file of cells, the people and the things that change, and each file of
  animations, a frame each, as a PNG sheet, in the likeliest palette.
- **`sounds`** writes each RealSound recording as a WAV file, the share of each sample the
  speaker is on.
- **`room`** prints a room's things, with the rectangles each can be clicked in, what each action
  says about each, and the room's numbered messages, from a file's `.DAT` and `.PLY`
  (`MASONCEL` for the cell).
- **`talk`** prints a conversation from a file's `.TRE` (`GUARD`, `LISA`): each record under the
  approaches that lead to it, with what it does (the topic it adds to ASK ABOUT, or that it ends
  the line of questioning or the talk), Mason's line and the answer; then the answers to ASK
  ABOUT, by topic.
