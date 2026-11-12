"""Maps Presumed Guilty's case from its files: what each message, report and record sets off,
how long each waits, and Falcon's chain to the end of the game.

    python tools/cosmi/guilty_case.py <the game's folder>             the case, from its start
    python tools/cosmi/guilty_case.py <the game's folder> <name>      one entry, and its text
    python tools/cosmi/guilty_case.py <the game's folder> --falcon    Falcon's chain, and its time

An entry's name is ten characters, its kind's letter first, as the game writes it (FX02, or
"OGENERAL W"); shorter ones are padded with '-' as the game pads them.
"""

from __future__ import annotations

import os
import sys
from dataclasses import dataclass, field

KINDS = {
    "C": "COPNET's message",
    "F": "Falcon's message",
    "N": "news",
    "D": "dossier",
    "M": "mugshot entry",
    "P": "photograph",
    "O": "report on something you can investigate",
    "S": "switch",
    "R": "remark",
    "U": "unused",
}

# what the status letter means, as the game's code uses it
STATES = {
    "N": "not yet",
    "D": "open (a report: can be investigated)",
    "U": "locked until Umbra clearance",
    "I": "sent at once",
    "C": "timed",
    "R": "arrived",
    "T": "timing",
    "Q": "announced",
    "A": "read",
    "Z": "done",
}

# the game keeps each delay in timer ticks, 18 a second
TICKS_PER_SECOND = 18


@dataclass
class Entry:
    name: str
    state: str
    seconds: int
    leads_to: list[str]
    text: str
    led_from: list[str] = field(default_factory=list)

    @property
    def kind(self) -> str:
        return KINDS.get(self.name[0], "?")


def decode(data: bytes) -> str:
    """The game's encoding undone: each byte rotated left by one bit."""
    return bytes(((b << 1) | (b >> 7)) & 0xFF for b in data).decode("latin-1")


def find(folder: str, name: str) -> str:
    for file in os.listdir(folder):
        if file.upper() == name:
            return os.path.join(folder, file)
    raise SystemExit(f"no {name} in {folder}")


def named(entries: dict[str, list[Entry]], name: str) -> list[Entry] | None:
    """The entries of a name, as the game writes it or without its padding or kind's letter."""
    wanted = name.upper().rstrip("- ")
    for key, versions in entries.items():
        if key.rstrip("- ") == wanted or key[1:].rstrip("- ") == wanted:
            return versions
    return None


def read_case(folder: str) -> dict[str, list[Entry]]:
    """Every entry in MSG.TXT by name (some names have more than one), with its links both ways
    and its state as the case ships, from STATUS.TBB."""
    with open(find(folder, "MSG.TXT"), "rb") as f:
        messages = decode(f.read())

    entries: dict[str, list[Entry]] = {}
    for part in messages.split("|"):
        part = part.lstrip("\r\n")
        if not part.strip() or part[0] not in KINDS:
            continue

        first, _, rest = part.partition("\r\n")
        head, tilde, text = rest.partition("~")
        if not tilde:
            # one entry has no '~': its text follows the line naming it
            head, text = "", rest

        number = first[12:].strip()
        entry = Entry(
            name=first[:10],
            state=first[11:12],
            seconds=int(number) if number.isdigit() else 0,
            leads_to=[line.strip().ljust(10)[:10] for line in head.split("\r\n") if line.strip()],
            text=text.replace("\r\n", "\n").strip("\n"),
        )
        entries.setdefault(entry.name, []).append(entry)

    # the states as the case ships: STATUS.TBB's records, 27 bytes each, in MSG.TXT's order
    try:
        with open(find(folder, "STATUS.TBB"), "rb") as f:
            status = f.read()
        seen: dict[str, int] = {}
        for at in range(0, len(status) - 26, 27):
            name = status[at:at + 10].decode("latin-1")
            if name in entries:
                n = seen.get(name, 0)
                if n < len(entries[name]):
                    entries[name][n].state = chr(status[at + 10])
                seen[name] = n + 1
    except SystemExit:
        pass

    for name, versions in entries.items():
        for entry in versions:
            for target in entry.leads_to:
                for other in entries.get(target, []):
                    if name not in other.led_from:
                        other.led_from.append(name)

    return entries


def when(seconds: int) -> str:
    if seconds >= 3600:
        return f"{seconds / 3600:.1f} h"
    if seconds >= 60:
        return f"{seconds / 60:.0f} min"
    return f"{seconds} s"


def describe(entry: Entry) -> str:
    wait = f", after {when(entry.seconds)}" if entry.seconds else ""
    return f"{entry.name}  {entry.kind}; {STATES.get(entry.state, entry.state)}{wait}"


def show_case(entries: dict[str, list[Entry]]) -> None:
    """The case from what's arrived as it ships, each entry once, with what reading it sets off."""
    shown: set[str] = set()

    def walk(name: str, depth: int) -> None:
        versions = entries.get(name)
        if not versions:
            print("    " * depth + f"{name}  (no such entry)")
            return
        if name in shown:
            print("    " * depth + f"{name}  (above)")
            return
        shown.add(name)
        more = f"  (and {len(versions) - 1} more of the name)" if len(versions) > 1 else ""
        print("    " * depth + describe(versions[0]) + more)
        for target in dict.fromkeys(target for entry in versions for target in entry.leads_to):
            walk(target, depth + 1)

    for name, versions in entries.items():
        if any(entry.state == "R" for entry in versions):
            walk(name, 0)

    unreached = [name for name in entries if name not in shown and not name.startswith(("R", "S"))]
    print()
    print(f"Not set off by anything from the start ({len(unreached)}): what you investigate yourself,")
    print("and what the game sets off in its code (the Umbra messages):")
    print("  " + " ".join(name.rstrip("-") for name in unreached))


def show_entry(entries: dict[str, list[Entry]], name: str) -> None:
    versions = named(entries, name)
    if not versions:
        raise SystemExit(f"no entry named {name}")
    for entry in versions:
        print(describe(entry))
        print(f"  set off by: {' '.join(entry.led_from) or '(nothing: the game, or you)'}")
        print(f"  sets off:   {' '.join(entry.leads_to) or '(nothing)'}")
        print()
        print(entry.text)
        print()


def show_falcon(entries: dict[str, list[Entry]]) -> None:
    """Falcon's passphrase chain: from what starts it to the end, with the time it takes."""
    chain = [name for name in entries if name.startswith("FX")]
    first = next(name for name in chain if not any(source.startswith("FX") for source in entries[name][0].led_from))
    starts = entries[first][0].led_from
    print(f"Started by {' '.join(starts)}, which is set off by {' '.join(entries[starts[0]][0].led_from)}")
    total = 0
    name = first
    while name in entries:
        entry = entries[name][0]
        total += entry.seconds
        others = [target for target in entry.leads_to if not target.startswith("FX")]
        also = f"; also sets off {' '.join(others)}" if others else ""
        print(f"  {name}  after {when(entry.seconds):>7}, {when(total):>7} in{also}")
        name = next((target for target in entry.leads_to if target.startswith("FX")), "")
    print(f"Then {' '.join(entries[chain[-1]][0].leads_to)}: about {when(total)} of waiting, each wait starting")
    print("when the message before it is read (with BETAFAST, every wait is 5 ticks)")


def main() -> None:
    if len(sys.argv) not in (2, 3):
        raise SystemExit(__doc__)

    entries = read_case(sys.argv[1])
    if len(sys.argv) == 2:
        show_case(entries)
    elif sys.argv[2] == "--falcon":
        show_falcon(entries)
    else:
        show_entry(entries, sys.argv[2])


if __name__ == "__main__":
    main()
