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
