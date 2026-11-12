"""Portal's story, read from its disk images: each level's items, the dataspace each is in, its
title, its prerequisites and successors, and its text.

How the game keeps all this is in docs/portal.md, under "The story".
"""

from __future__ import annotations

import os
import struct
from dataclasses import dataclass

SECTOR = 512
IMAGE_SIZE = 368_640  # 40 cylinders, 2 sides, 9 sectors of 512 bytes
ITEMS = 0x8C

# the dataspaces, by the number in the low nibble of an item's flag: the map's places, left to
# right and top to bottom, then Interface, Homer's side of the conversation
DATASPACES = [
    "Med10",
    "PsiLink",
    "SciTech",
    "History",
    "Military",
    "LifeSupport",
    "Geographical",
    "Wasatch",
    "Psychological",
    "Central Processing",
    "Edmod",
    "Homer",
    "Interface",
    "(13)",
    "(14)",
    "(15)",
]
HOMER, INTERFACE = 11, 12

# the disk each level is on, counting from 0
LEVEL_DISK = {1: 0, 2: 1, 3: 1, 4: 2, 5: 2}

# prerequisite numbers above these aren't items in the level, and count as read; in level 5,
# 85h and up are the exception
LEVEL_LIMIT = {1: 0x38, 2: 0x4C, 3: 0x66, 4: 0x76, 5: 0x6E}

# where the program's state segment is in its image, and the item flags in it
STATE_SEGMENT, FLAGS = 0x584, 0x72C


@dataclass
class Item:
    number: int
    flag: int
    title: str
    prerequisites: list[int]
    successors: list[int]
    text: bytes

    @property
    def dataspace(self) -> str:
        return DATASPACES[self.flag & 0x0F]

    @property
    def ends_level(self) -> bool:
        return 0xFE in self.successors


def find_disks(folder: str) -> list[bytes]:
    """The disk images in a folder, disk 1 (with PORTAL.EXE) first and the rest by name."""
    images = []
    for name in sorted(os.listdir(folder), key=str.upper):
        path = os.path.join(folder, name)
        if os.path.isfile(path) and os.path.getsize(path) == IMAGE_SIZE:
            with open(path, "rb") as file:
                images.append(file.read())

    first = [image for image in images if read_file(image, "PORTAL.EXE") is not None]
    if not first:
        raise SystemExit(f"no disk with PORTAL.EXE in {folder}")

    return first[:1] + [image for image in images if image is not first[0]]


def read_file(image: bytes, wanted: str) -> bytes | None:
    """A file from a 360K disk's root directory, by its name; None if it isn't there."""
    sectors_per_cluster, reserved, fats, root_entries, sectors_per_fat = 2, 1, 2, 112, 2
    root = (reserved + fats * sectors_per_fat) * SECTOR
    data = root + root_entries * 32
    fat = image[reserved * SECTOR : (reserved + sectors_per_fat) * SECTOR]

    def next_cluster(cluster: int) -> int:
        offset = cluster * 3 // 2
        value = fat[offset] | fat[offset + 1] << 8
        return value >> 4 if cluster & 1 else value & 0xFFF

    for entry in range(root_entries):
        record = image[root + entry * 32 : root + entry * 32 + 32]
        if record[0] == 0:
            break

        name = (
            record[0:8].decode("latin1").rstrip()
            + "."
            + record[8:11].decode("latin1").rstrip()
        )
        if record[0] == 0xE5 or name != wanted:
            continue

        cluster, size = struct.unpack_from("<HI", record, 26)
        contents = bytearray()
        while 2 <= cluster < 0xFF8 and len(contents) < size:
            offset = data + (cluster - 2) * sectors_per_cluster * SECTOR
            contents += image[offset : offset + sectors_per_cluster * SECTOR]
            cluster = next_cluster(cluster)

        return bytes(contents[:size])

    return None


def image_offset(address: int) -> int:
    """Where one of the game's byte addresses is in an image: eight sectors to a track as the
    game uses them, side 0 then side 1 of each cylinder."""
    sector, byte = divmod(address, SECTOR)
    track, sector = divmod(sector, 8)
    return (track * 9 + sector) * SECTOR + byte


def read(image: bytes, address: int, length: int) -> bytes:
    """Bytes at one of the game's addresses on a disk."""
    return bytes(image[image_offset(a)] for a in range(address, address + length))


def text(data: bytes) -> str:
    """An item's text as plain text: the high bit marks the end of a word or line, and FFh the end."""
    out = []
    for byte in data:
        if byte == 0xFF:
            break

        char = byte & 0x7F
        out.append(chr(char) if 32 <= char < 127 else " ")
        if byte & 0x80:
            out.append(" ")

    return " ".join("".join(out).split())


def level_flags(disks: list[bytes], level: int) -> bytes:
    """The level's item flags as it starts: level 1's are the program's own starting values, and
    each later level's are on its disk."""
    if level == 1:
        program = read_file(disks[0], "PORTAL.EXE")
        if program is None:
            raise SystemExit("no PORTAL.EXE on disk 1")

        header = struct.unpack_from("<H", program, 8)[0] * 16
        start = header + STATE_SEGMENT * 16 + FLAGS
        return program[start : start + ITEMS]

    address = 0x44CF3 + (0x5000 if level & 4 else 0) + (0x186 if level & 1 else 0)
    return read(disks[LEVEL_DISK[level]], address, ITEMS)


def level_items(disks: list[bytes], level: int) -> dict[int, Item]:
    """A level's items, by number: from its table of 5-byte entries (an address and a length),
    the records they point to, and the titles from 5DCh on in the same table."""
    disk = disks[LEVEL_DISK[level]]
    table_address = (
        0x45000 + (0x5000 if level & 4 else 0) + (0 if level & 1 else 0x1C20)
    )
    table = read(disk, table_address, 0x1C20)
    flags = level_flags(disks, level)

    def title(number: int) -> str:
        offset = struct.unpack_from("<H", table, 0x5DC + 2 * number)[0] - 2
        if not 0 <= offset < len(table):
            return ""

        chars = []
        for byte in table[offset : offset + 60]:
            chars.append(chr(byte & 0x7F))
            if byte & 0x80:
                break

        return "".join(chars)

    items = {}
    for number in range(ITEMS):
        entry = table[number * 5 : number * 5 + 5]
        address = entry[0] << 16 | entry[1] << 8 | entry[2]
        length = entry[3] << 8 | entry[4]
        if (
            address in (0, 0xFFFFFF)
            or length == 0
            or image_offset(address + length) > len(disk)
        ):
            continue

        record = read(disk, address, length)
        items[number] = Item(
            number=number,
            flag=flags[number],
            title=title(number),
            prerequisites=[n for n in record[0:8] if n != 0xFF],
            successors=[n for n in record[8:16] if n != 0xFF],
            text=record[20:],
        )

    return items


def counts(level: int, number: int, items: dict[int, Item]) -> bool:
    """Whether a prerequisite is an item the game waits for, rather than one that counts as read."""
    return number in items and (
        number <= LEVEL_LIMIT[level] or (level == 5 and number >= 0x85)
    )


def level_end(level: int, items: dict[int, Item]) -> list[int]:
    """The item that ends a level: the one with FEh among its successors, or in level 5, which
    has none, Homer's goodbye."""
    return [n for n, item in items.items() if item.ends_level] or [0x22]


def required(level: int, items: dict[int, Item]) -> set[int]:
    """The items that must be read to end the level: its end, and back through prerequisites."""
    needed: set[int] = set()
    todo = level_end(level, items)
    while todo:
        number = todo.pop()
        if number in needed or number not in items:
            continue

        needed.add(number)
        todo += [n for n in items[number].prerequisites if counts(level, n, items)]

    return needed
