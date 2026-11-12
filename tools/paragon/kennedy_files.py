"""Reads the text of Guardians of Infinity: To Save Kennedy from its files: the news, and what
your agents say on each mission: each person you send them to see, and each task.

    python tools/paragon/kennedy_files.py <the game's folder> news             the news, page by page
    python tools/paragon/kennedy_files.py <the game's folder> people           who MIRKWOOD.DAT has
    python tools/paragon/kennedy_files.py <the game's folder> person <who>     one of them, by number or name
    python tools/paragon/kennedy_files.py <the game's folder> tasks            what SHERWOOD.DAT has
    python tools/paragon/kennedy_files.py <the game's folder> task <number>    one of them

MIRKWOOD.DAT and SHERWOOD.DAT hold records, each the script and the text of a mission, and the
orders that lead to each. The text is rings of passages, one of a ring said at a time, each
marked for the agents who can say it, and lists of words the script matches what you type
against. The names of the people, and the verbs, come from GOI.EXE. How it all fits is in
docs/kennedy.md.
"""

from __future__ import annotations

import os
import struct
import sys
from dataclasses import dataclass

AGENTS = ["Steel", "Bridgette", "Blue", "Stein", "Czernak"]

# the 15 characters a packed passage's nibbles 0 to E stand for; bit 7 is a space after one
NIBBLES = b"\xe5taoinhreslu\xf4mc"

# where GOI.EXE (153,152 bytes) keeps its vocabulary, in its data segment, which starts 1A6F0h
# into its image: its nouns, a far pointer to each one's name and a word of flags, from noun 0,
# Iris Steel; and its verbs, a far pointer each
EXE_SIZE = 153_152
DATA_SEGMENT = 0x1A6F
NOUNS_AT = 0x3346
NOUNS = 147
VERBS_AT = 0x36B8
VERBS = 20

# the verbs a key can have that take a thing: what's stolen, and whose car is disabled
STEAL = 0x11
SEE = 0x0E

# the things an order can name, as its dictionary numbers them ("gun" is a rifle, "cash" money)
THINGS = ["rifle", "tires", "car", "credentials", "money", "news"]

# a key's thing when any will do
ANY = 0xF7


def find(folder: str, name: str) -> str:
    for file in os.listdir(folder):
        if file.upper() == name:
            return os.path.join(folder, file)
    raise SystemExit(f"no {name} in {folder}")


def read(folder: str, name: str) -> bytes:
    with open(find(folder, name), "rb") as f:
        return f.read()


def spaced(data: bytes) -> str:
    """Text where bit 7 of a character means a space after it."""
    return "".join(chr(c & 0x7F) + " " if c & 0x80 else chr(c) for c in data)


# the news

def news(folder: str) -> list[list[str]]:
    """NWS's pages, in its groups: each byte XORed with 1, then spaced; a page ends at '|'."""
    data = read(folder, "NWS")
    first, = struct.unpack_from("<H", data, 2)
    starts = struct.unpack_from(f"<{(first - 2) // 2}H", data, 2)
    groups: list[list[str]] = [[]]
    for start in starts:
        if start == 0xFFFF:
            groups.append([])
            continue
        end = data.index(ord("|") ^ 1, start)
        page = spaced(bytes(b ^ 1 for b in data[start:end])).replace("\x0b", "\n")
        groups[-1].append(page.strip("\n"))
    return groups


def show_news(folder: str) -> None:
    for n, group in enumerate(news(folder)):
        for page in group:
            print(f"== group {n}")
            print(page)
            print()


# the vocabulary

@dataclass
class Vocabulary:
    nouns: list[str]
    verbs: list[str]


def vocabulary(folder: str) -> Vocabulary:
    exe = read(folder, "GOI.EXE")
    if len(exe) != EXE_SIZE:
        raise SystemExit(f"GOI.EXE isn't the one this reads ({EXE_SIZE:,} bytes)")
    data = (struct.unpack_from("<H", exe, 8)[0] + DATA_SEGMENT) * 16

    def name(offset: int, segment: int) -> str:
        if segment != DATA_SEGMENT:
            raise SystemExit("GOI.EXE's vocabulary isn't where this expects it")
        end = exe.index(0, data + offset)
        # a name, then a tab and the other names it answers to
        return exe[data + offset:end].decode("latin-1").split("\t")[0]

    nouns = [name(*struct.unpack_from("<2H", exe, data + NOUNS_AT + (6 * n))) for n in range(NOUNS)]
    verbs = [name(*struct.unpack_from("<2H", exe, data + VERBS_AT + (4 * n))) for n in range(VERBS)]
    return Vocabulary(nouns, verbs)


# the records

@dataclass
class Passage:
    at: int
    agents: int
    next: int
    text: str


@dataclass
class Words:
    at: int
    words: list[str]


@dataclass
class Record:
    number: int
    script: bytes
    table: bytes
    items: list[Passage | Words]
    keys: list[tuple[int, int, int]]  # the orders that lead to it: (verb, noun, thing)


def unpack(data: bytes, at: int) -> tuple[str, int]:
    """A passage: plain, ended by a zero, or, after FFh, nibbles, high first: 0 to E from
    NIBBLES, F and two more for a byte, and F 0 0 for the end."""
    if data[at] != 0xFF:
        end = data.index(0, at)
        return spaced(data[at:end]), end + 1

    position = [at + 1, True]

    def nibble() -> int:
        byte = data[position[0]]
        value = byte >> 4 if position[1] else byte & 0xF
        if not position[1]:
            position[0] += 1
        position[1] = not position[1]
        return value

    out = bytearray()
    while True:
        n = nibble()
        if n == 0xF:
            c = (nibble() << 4) | nibble()
            if c == 0:
                break
            out.append(c)
        else:
            out.append(NIBBLES[n])
    return spaced(bytes(out)), position[0] + (0 if position[1] else 1)


def read_records(folder: str, name: str) -> list[Record]:
    data = read(folder, name)
    if data[:3] != b"\x99\x99\x02":
        raise SystemExit(f"{name} isn't one of the game's data files")
    count = data[3]
    starts = list(struct.unpack_from(f"<{count}I", data, 4)) + [len(data)]
    at = 4 + (4 * count)
    keys_count, = struct.unpack_from("<H", data, at)
    keys: dict[int, list[tuple[int, int, int]]] = {}
    for n in range(keys_count):
        noun, verb, thing, record = data[at + 2 + (4 * n):at + 6 + (4 * n)]
        keys.setdefault(record, []).append((verb, noun, thing))

    records = []
    for n in range(count):
        record = data[starts[n]:starts[n + 1]]
        table_size = record[0]
        text_at, script_at, table_at, script_size, text_size = struct.unpack_from("<5H", record, 1)
        text = record[text_at:text_at + text_size]
        records.append(Record(
            number=n,
            script=record[script_at:script_at + script_size],
            table=record[table_at:table_at + table_size],
            items=read_items(text),
            keys=keys.get(n, [])))
    return records


def read_items(text: bytes) -> list[Passage | Words]:
    """A record's text, in order: a passage starts with its agents' bits (below 20h) and the
    place of the next in its ring; a list of words is words ended by 01h, then a zero."""
    items: list[Passage | Words] = []
    at = 0
    while at < len(text):
        b = text[at]
        if b == 0:
            at += 1
        elif b < 0x20:
            following, = struct.unpack_from("<H", text, at + 1)
            said, at_end = unpack(text, at + 3)
            items.append(Passage(at, b, following, said))
            at = at_end
        else:
            end = text.index(0, at)
            items.append(Words(at, [w.decode("latin-1") for w in text[at:end].split(b"\x01") if w]))
            at = end + 1
    return items


def who(record: Record, words: Vocabulary) -> str:
    """The orders that lead to a record; for people, just their names."""
    if not record.keys:
        return "(the game's own)"
    if all(verb == SEE for verb, _, _ in record.keys):
        return ", ".join(words.nouns[noun] for _, noun, _ in record.keys)
    orders = []
    for verb, noun, thing in record.keys:
        if thing == ANY:
            orders.append(f"{words.verbs[verb]} {words.nouns[noun]}")
        elif verb == STEAL:
            orders.append(f"steal {THINGS[thing]} from {words.nouns[noun]}")
        else:
            orders.append(f"{words.verbs[verb]} {words.nouns[noun]}'s {THINGS[thing]}")
    return "; ".join(orders)


def speakers(agents: int) -> str:
    if agents == 0x1F:
        return "anyone"
    return ", ".join(name for bit, name in enumerate(AGENTS) if agents & (1 << bit))


def show_record(record: Record, words: Vocabulary) -> None:
    print(f"== {record.number}: {who(record, words)}")
    shown: set[int] = set()
    passages = {item.at: item for item in record.items if isinstance(item, Passage)}
    for item in record.items:
        if isinstance(item, Words):
            print(f"  words: {' '.join(item.words)}")
            continue
        if item.at in shown:
            continue
        # a ring: its passages, each said by the agents it's marked for
        ring = []
        at = item.at
        while at not in shown and at in passages:
            shown.add(at)
            ring.append(passages[at])
            at = passages[at].next
        print(f"  ring at {item.at:04X}h:" if len(ring) > 1 else f"  at {item.at:04X}h:")
        for passage in ring:
            print(f"    {speakers(passage.agents)}: {passage.text.strip()}")
    print()


def show_list(records: list[Record], words: Vocabulary) -> None:
    for record in records:
        first = next((item.text.strip() for item in record.items if isinstance(item, Passage)
                      and not item.text.startswith("Err")), "")
        print(f"{record.number:3}  {who(record, words)}")
        print(f"       {first[:100]}")


def main() -> None:
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)

    folder, command = sys.argv[1], sys.argv[2]
    if command == "news":
        show_news(folder)
        return

    words = vocabulary(folder)
    if command in ("people", "person"):
        records = read_records(folder, "MIRKWOOD.DAT")
    elif command in ("tasks", "task"):
        records = read_records(folder, "SHERWOOD.DAT")
    else:
        raise SystemExit(__doc__)

    if command in ("people", "tasks"):
        show_list(records, words)
        return

    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    wanted = sys.argv[3]
    if wanted.isdigit() and int(wanted) < len(records):
        show_record(records[int(wanted)], words)
        return
    matches = [r for r in records if wanted.lower() in who(r, words).lower()]
    if not matches:
        raise SystemExit(f"no one called {wanted}")
    for record in matches:
        show_record(record, words)


if __name__ == "__main__":
    main()
