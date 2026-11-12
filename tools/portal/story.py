"""Lists one of Portal's levels, item by item: its flag, dataspace, title, prerequisites and
successors, with an asterisk on the items that must be read to end the level. With an item's
number, prints its text instead.

    python tools/portal/story.py <the game's folder> <level> [item, in hex]
"""

import sys

from portal_story import find_disks, level_items, required, text


def main() -> None:
    if len(sys.argv) not in (3, 4):
        raise SystemExit(__doc__)

    disks = find_disks(sys.argv[1])
    level = int(sys.argv[2])
    items = level_items(disks, level)
    if len(sys.argv) == 4:
        item = items[int(sys.argv[3], 16)]
        print(f"{item.number:02X}h {item.dataspace}: {item.title}")
        print(text(item.text))
        return

    needed = required(level, items)
    for number, item in items.items():
        pre = " ".join(f"{n:02X}" for n in item.prerequisites)
        nxt = " ".join(f"{n:02X}" for n in item.successors)
        mark = "*" if number in needed else " "
        print(
            f"{mark} {number:02X} {item.flag:02X} {item.dataspace:18} {item.title[:36]:36} pre {pre:24} next {nxt}"
        )


if __name__ == "__main__":
    main()
