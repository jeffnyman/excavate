"""Writes the levels of Portal's walkthrough, as Markdown: for each level, the items that must be
read to end it, in steps. Each step is what has just unlocked, everything its items need having
been read in the steps before; within a step, the order doesn't matter. docs/portal-walkthrough.md
is this, after its introduction.

    python tools/portal/walkthrough.py <the game's folder>
"""

import sys

from portal_story import (
    HOMER,
    INTERFACE,
    counts,
    find_disks,
    level_end,
    level_items,
    required,
    text,
)

# the order dataspaces are listed in, within a step
ORDER = [
    "Central Processing",
    "Med10",
    "PsiLink",
    "SciTech",
    "History",
    "Military",
    "Geographical",
    "Homer",
]


def name(number, items):
    """What an item's list calls it, or, where two share a title, the first words of its text."""
    title = items[number].title
    sharing = [n for n, item in items.items() if item.title == title]
    if not title or (len(sharing) > 1 and number < max(sharing)):
        return '"' + " ".join(text(items[number].text).split()[:5]) + '..."'

    return title


def steps(level, items):
    """The level's required items, in waves: each what's unlocked once the waves before are read."""
    needed = required(level, items)
    done, waves = set(), []
    while len(done) < len(needed):
        wave = sorted(
            n
            for n in needed - done
            if all(p in done for p in items[n].prerequisites if counts(level, p, items))
        )
        if not wave:
            raise SystemExit(f"level {level}: nothing more unlocks")

        waves.append(wave)
        done |= set(wave)

    return waves


def describe(level, wave, items):
    by_space = {}
    for number in wave:
        flag = items[number].flag & 0x0F
        space = "Homer" if flag in (HOMER, INTERFACE) else items[number].dataspace
        by_space.setdefault(space, []).append(number)

    end = level_end(level, items)
    parts = []
    for space in sorted(
        by_space, key=lambda s: ORDER.index(s) if s in ORDER else len(ORDER)
    ):
        names = [name(n, items) for n in by_space[space]]
        if space == "Homer":
            names = [n.removeprefix("HOMER ") for n in names]

        if space != "Homer":
            parts.append(f"**{space}**: " + "; ".join(names))
        elif level == 5 and by_space[space] == end:
            parts.append(
                f"**Homer**: his goodbye ({names[0]}), which comes up by itself"
            )
        else:
            plural = "s" if len(names) > 1 else ""
            parts.append(
                f"**Homer**: {len(names)} message{plural} ({', '.join(names)})"
            )

    line = ". ".join(parts) + "."
    if any(n in end for n in wave):
        line += " The story ends here." if level == 5 else " The level ends here."

    return line


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)

    disks = find_disks(sys.argv[1])
    out = []
    for level in range(1, 6):
        items = level_items(disks, level)
        waves = steps(level, items)
        disk = {1: 1, 2: 2, 3: 2, 4: 3, 5: 3}[level]
        out += [
            f"## Level {level}",
            "",
            f"On disk {disk}. {len(required(level, items))} items, in {len(waves)} steps.",
            "",
        ]
        out += [
            f"{n}. {describe(level, wave, items)}" for n, wave in enumerate(waves, 1)
        ]
        out.append("")

    # the same file on any system: lines end with a line feed alone
    sys.stdout.buffer.write("\n".join(out).encode("ascii"))


if __name__ == "__main__":
    main()
