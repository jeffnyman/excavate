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
