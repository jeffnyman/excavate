"""Reads The Omnicron Conspiracy's files: its rooms, its text, and its pictures.

    python tools/firststar/omnicron_files.py <the game's folder> rooms              every room, with where its doors go
    python tools/firststar/omnicron_files.py <the game's folder> things             what's in each room, and what holds what
    python tools/firststar/omnicron_files.py <the game's folder> messages           all of the game's text
    python tools/firststar/omnicron_files.py <the game's folder> pictures <folder>  every picture, as PNG files

ALLROOM0.DAT to ALLROOM3.DAT hold the rooms' pictures, packed one after another, and
OFFSETS0.DAT to OFFSETS3.DAT where each starts. The pictures in PAKDATA, and AWARDS.PAK and
FCONTROL.PAK, are packed the same way. The rooms themselves, their names, and where their doors
go, are in SCODE.EXE, with the messages. TITLEEGA.CMP, TITLECGA.CMP, CONTROL1.CMP and
CONTROL7.CMP are full screens, packed row by row. How it all fits is in docs/omnicron.md.
"""

from __future__ import annotations

import itertools
import os
import re
import struct
import sys
import zlib
from dataclasses import dataclass

# SCODE.EXE's data segment: 773h paragraphs into the image, which starts 200h into the file
EXE_SIZE = 111_887
DATA = 0x200 + 0x7730

# its text is all in its data segment, each message ending with a zero, its lines with a
# carriage return; a message that follows the one before it after an Enter has FFh before it
TEXT = re.compile(rb"[ -~\r]{12,}\0")

# the pictures in each set of rooms, ALLROOM0.DAT to ALLROOM3.DAT
SETS = 4

# the game's 16 colors, as the loader gives them to the MCGA's DAC, 0 to 63 each
COLORS = [
    (0x00, 0x00, 0x00), (0x00, 0x00, 0x27), (0x03, 0x25, 0x03), (0x00, 0x1B, 0x1B),
    (0x20, 0x00, 0x00), (0x1B, 0x00, 0x1B), (0x20, 0x1B, 0x05), (0x26, 0x26, 0x26),
    (0x10, 0x10, 0x10), (0x00, 0x00, 0x38), (0x00, 0x3F, 0x00), (0x00, 0x3F, 0x3F),
    (0x3A, 0x20, 0x10), (0x3F, 0x00, 0x3F), (0x3F, 0x3F, 0x00), (0x3F, 0x3F, 0x3F),
]
PALETTE = [(r * 255 // 63, g * 255 // 63, b * 255 // 63) for r, g, b in COLORS]

# the CGA's mode 04h with its second palette, as the game sets it: black, cyan, magenta and
# light gray
CGA = [(0, 0, 0), (0, 170, 170), (170, 0, 170), (170, 170, 170)]

# what's drawn behind a sprite's see-through color on a sheet
BACKGROUND = (96, 96, 96)


def find(folder: str, name: str) -> str:
    for file in os.listdir(folder):
        if file.upper() == name:
            return os.path.join(folder, file)
    raise SystemExit(f"There's no {name} in {folder}.")


def read(folder: str, name: str) -> bytes:
    with open(find(folder, name), "rb") as file:
        return file.read()


# --- packing -----------------------------------------------------------------------------


def unpack(data: bytes, at: int = 0) -> tuple[bytes, int]:
    """Unpacks what starts at `at`: a word, the size unpacked, then blocks, each of byte pairs
    and the bytes they make. Returns what it unpacks and where it ends.

    A block starts with the count of its pairs, a byte that's nonzero if another block follows,
    and a word, the count of its bytes. Each pair is three bytes: a byte that stands for two
    others, and those two. A byte of the block that one stands for becomes its two, and each of
    those can stand for two in turn, but only by a pair listed before the one that made it: a
    byte can stand for different pairs at different places in the list.

    What comes out of that has runs: BFh and a count is that many zeros, CFh, a count and a
    byte, that many of the byte. A count of 0 is 256.
    """
    size = data[at] | data[at + 1] << 8
    at += 2
    out = bytearray()
    escape, count = 0, -1

    def put(byte: int) -> None:
        nonlocal escape, count
        if escape == 0:
            if byte in (0xBF, 0xCF):
                escape = byte
            else:
                out.append(byte)
        elif escape == 0xBF:
            out.extend(bytes(byte or 256))
            escape = 0
        elif count < 0:
            count = byte
        else:
            out.extend(bytes([byte]) * (count or 256))
            escape, count = 0, -1

    while True:
        pairs, more, length = data[at], data[at + 1], data[at + 2] | data[at + 3] << 8
        at += 4
        table = [data[at + 3 * i: at + 3 * i + 3] for i in range(pairs)]
        at += 3 * pairs
        for byte in data[at: at + length]:
            stack = [(byte, pairs)]
            while stack:
                byte, before = stack.pop()
                for i in range(before - 1, -1, -1):
                    if table[i][0] == byte:
                        stack.append((table[i][2], i))
                        stack.append((table[i][1], i))
                        break
                else:
                    put(byte)
        at += length
        if not more:
            break

    if len(out) != size:
        raise ValueError(f"unpacked {len(out)} bytes, not {size}")
    return bytes(out), at


# --- pictures ----------------------------------------------------------------------------


@dataclass
class Picture:
    width: int
    height: int
    pixels: list[int]
    clear: int | None = None
    cga: bool = False


def nibbles(data: bytes) -> list[int]:
    return [n for byte in data for n in (byte >> 4, byte & 15)]


def screen(data: bytes) -> Picture:
    """A picture with a 32-byte header: a room's, DIALOG.BUE, AWARDS.PAK and the CMP files.
    From 10h: a byte that's 1 if the rows are packed, a byte that's 1 for two pixels to a byte
    (0 for the CGA's four), the width of a row in bytes, and the rows."""
    packed, wide = data[0x10], data[0x11]
    row, rows = struct.unpack_from("<HH", data, 0x12)
    if packed:
        data = rows_of(data, row, rows)
    else:
        data = data[0x20: 0x20 + row * rows]
    if wide:
        return Picture(row * 2, rows, nibbles(data))
    return Picture(row * 4, rows, [byte >> shift & 3 for byte in data for shift in (6, 4, 2, 0)], cga=True)


def rows_of(data: bytes, row: int, rows: int) -> bytes:
    """Unpacks a CMP file's rows. A table at 20h has where each row starts, and where the last
    ends, from 10h. A row starts with a byte that's skipped; then a byte under 80h and a word
    are that many words plus one of the word, and a byte from 80h, the negative of a count, is
    that count plus one of words that follow."""
    starts = struct.unpack_from(f"<{rows + 1}H", data, 0x20)
    out = bytearray()
    for y in range(rows):
        at, end = 0x10 + starts[y] + 1, 0x10 + starts[y + 1]
        line = bytearray()
        while at < end:
            count = data[at]
            if count < 0x80:
                line += data[at + 1: at + 3] * (count + 1)
                at += 3
            else:
                words = 0x100 - count + 1
                line += data[at + 1: at + 1 + 2 * words]
                at += 1 + 2 * words
        if len(line) != row:
            raise ValueError(f"row {y} is {len(line)} bytes, not {row}")
        out += line
    return bytes(out)


def sprites(data: bytes) -> list[Picture]:
    """A sprite file's frames. It starts with the count of its frames, a byte at 8 whose low
    four bits are the color that's see-through, and a word at 0Ah, where a table of the frames
    starts. Each frame takes 32 bytes there: at 2, where its pixels start; at 8, its width in
    bytes; at 0Ah, its height.

    As the game loads a sprite, it swaps its see-through color with 0, which is see-through
    when it draws, so what the file has as 0 is drawn in the see-through color. A character can
    also have one more color changed (see docs/omnicron.md); the frames here are as the file
    has them."""
    count, clear, table = data[0] | data[1] << 8, data[8] & 15, data[0x0A] | data[0x0B] << 8
    frames = []
    for i in range(count):
        at, = struct.unpack_from("<H", data, table + 32 * i + 2)
        row, rows = struct.unpack_from("<HH", data, table + 32 * i + 8)
        pixels = [clear if c == 0 else 0 if c == clear else c for c in nibbles(data[at: at + row * rows])]
        frames.append(Picture(row * 2, rows, pixels, clear=0))
    return frames


def sheet(frames: list[Picture], width: int = 320) -> Picture:
    """Lays frames out in rows, two pixels apart."""
    lines, line, x, tallest = [], [], 0, 0
    for frame in frames:
        if line and x + frame.width > width:
            lines.append((line, tallest))
            line, x, tallest = [], 0, 0
        line.append((x, frame))
        x += frame.width + 2
        tallest = max(tallest, frame.height + 2)
    if line:
        lines.append((line, tallest))
    width = max(sum(frame.width + 2 for _, frame in line) for line, _ in lines) - 2
    height = sum(tallest for _, tallest in lines) - 2
    pixels = [16] * (width * height)
    top = 0
    for line, tallest in lines:
        for left, frame in line:
            for y in range(frame.height):
                for x in range(frame.width):
                    color = frame.pixels[y * frame.width + x]
                    if color != frame.clear:
                        pixels[(top + y) * width + left + x] = color
        top += tallest
    return Picture(width, height, pixels)


def png(path: str, picture: Picture, palette: list[tuple[int, int, int]], scale: int = 2) -> None:
    """Writes a picture as a PNG, each pixel `scale` times over."""
    colors = palette + [BACKGROUND]
    raw = bytearray()
    for y in range(picture.height):
        line = bytearray()
        for x in range(picture.width):
            line += bytes(colors[picture.pixels[y * picture.width + x]]) * scale
        raw += (b"\0" + line) * scale

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    header = struct.pack(">IIBBBBB", picture.width * scale, picture.height * scale, 8, 2, 0, 0, 0)
    with open(path, "wb") as file:
        file.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def room_pictures(folder: str, number: int) -> list[bytes]:
    """A set's room pictures: OFFSETS.DAT has where each starts, a word high and a word low,
    and where the last ends."""
    offsets = read(folder, f"OFFSETS{number}.DAT")
    starts = [high << 16 | low for high, low in struct.iter_unpack("<HH", offsets)]
    rooms = read(folder, f"ALLROOM{number}.DAT")
    pictures = []
    for start, end in itertools.pairwise(starts):
        picture, at = unpack(rooms, start)
        if at != end:
            raise ValueError(f"ALLROOM{number}.DAT's picture at {start} ends at {at}, not {end}")
        pictures.append(picture)
    return pictures


# --- the rooms ---------------------------------------------------------------------------


@dataclass
class Room:
    at: int
    name: str
    set: int
    picture: int
    leads: list[int]


def code(folder: str) -> bytes:
    data = read(folder, "SCODE.EXE")
    if len(data) != EXE_SIZE:
        raise SystemExit(f"This SCODE.EXE is {len(data):,} bytes; the one this reads is {EXE_SIZE:,}.")
    return data


def rooms(folder: str) -> dict[int, Room]:
    """Finds the rooms in SCODE.EXE's data. A room's record starts with a pointer to its name,
    the number of its picture in its set, and its set, from 1; a word of how it's drawn; at 0Ah,
    three words for its doors; and at 10h, three for the rooms they lead to. Most rooms have
    their name at 1Ch, after the record."""
    data = code(folder)[DATA:]
    counts = [len(read(folder, f"OFFSETS{n}.DAT")) // 4 - 1 for n in range(SETS)]

    def word(at: int) -> int:
        return struct.unpack_from("<H", data, at)[0]

    def text(at: int) -> str | None:
        match = re.match(rb"([ -~]{1,40})\0", data[at:])
        return match.group(1).decode("ascii") if match else None

    def candidate(at: int) -> bool:
        return (0 < at < len(data) - 0x20 and 1 <= data[at + 3] <= SETS and data[at + 2] < counts[data[at + 3] - 1]
                and data[at + 6] <= 3 and data[at + 7] in (0, 8, 0x10) and text(word(at)) is not None)

    found = {at for at in range(len(data) - 0x20) if candidate(at)}
    rooms = {}
    for at in sorted(found):
        leads = [word(at + 0x10 + 2 * k) for k in range(3)]
        if any(leads) and all(lead == 0 or lead in found for lead in leads) or not any(leads) and word(at) == at + 0x1C:
            name = text(word(at))
            if name is not None:
                rooms[at] = Room(at, name, data[at + 3] - 1, data[at + 2], [lead for lead in leads if lead])
    return rooms


def list_rooms(folder: str) -> None:
    found = rooms(folder)
    counts = [len(read(folder, f"OFFSETS{n}.DAT")) // 4 - 1 for n in range(SETS)]
    for number in range(SETS):
        print(f"ALLROOM{number}.DAT, {counts[number]} pictures")
        used = set()
        for room in sorted(found.values(), key=lambda room: (room.set, room.picture, room.at)):
            if room.set != number:
                continue
            used.add(room.picture)
            leads = ", ".join(found[lead].name for lead in room.leads) or "nowhere"
            print(f"  {room.picture:3}  {room.name:24} to {leads}")
        unused = [str(n) for n in range(counts[number]) if n not in used]
        if unused:
            print(f"  no room has picture {', '.join(unused)}")
        print()


# --- the things --------------------------------------------------------------------------

# Ace's own record, which holds what he carries
ACE = 0xCDE7


def things(folder: str) -> None:
    """Prints what's in each room as the game starts, and what each thing holds. A thing's
    record has a pointer to its name at 2; at 4, the first thing it holds; at 6, what holds it,
    a room, another thing, or Ace; and at 8, the next thing in the same place. A room's first
    thing is at its 4 too. The doors are left out, and so is what the game makes as it goes,
    Ace's shots among them."""
    data = code(folder)[DATA:]
    found = rooms(folder)

    def word(at: int) -> int:
        return struct.unpack_from("<H", data, at)[0]

    def name(at: int) -> str:
        match = re.match(rb"([ -~]{1,40})\0", data[word(at + 2):])
        return match.group(1).decode("ascii") if match else "?"

    def chain(first: int, depth: int, seen: set[int]) -> int:
        """Prints a chain of things and what each holds; returns how many it printed."""
        printed = 0
        at = first
        while at and at not in seen and at + 0x1C <= len(data):
            seen.add(at)
            if name(at) not in ("A door", "?"):
                print("    " * depth + name(at))
                printed += 1 + chain(word(at + 4), depth + 1, seen)
            at = word(at + 8)
        return printed

    seen: set[int] = set()
    for room in sorted(found.values(), key=lambda room: (room.set, room.picture, room.at)):
        print(f"{room.name} (ALLROOM{room.set}.DAT, picture {room.picture})")
        if chain(word(room.at + 4), 1, seen) == 0:
            print("    nothing")

    print("Ace Powers")
    chain(word(ACE + 4), 1, seen)


# --- the messages ------------------------------------------------------------------------


def list_messages(folder: str) -> None:
    """Prints the text in SCODE.EXE's data segment, in the order it's there: what's said, what
    things look like, the letters and memos, the save and load prompts, and the credits. A
    string of at least 12 characters with a space and a small letter counts as text."""
    data = code(folder)[DATA:]
    for match in TEXT.finditer(data):
        text = match.group()[:-1].decode("ascii")
        if " " not in text or not re.search("[a-z]", text) or re.search(r"\.[a-z]{3}\b", text):
            continue
        print("    (Enter)" if data[match.start() - 1] == 0xFF else "")
        for line in text.split("\r"):
            print("    " + line.rstrip())


# --- the pictures ------------------------------------------------------------------------


def write_pictures(folder: str, out: str) -> None:
    os.makedirs(out, exist_ok=True)
    written = 0
    for number in range(SETS):
        for i, picture in enumerate(room_pictures(folder, number)):
            png(os.path.join(out, f"room{number}-{i:02}.png"), screen(picture), PALETTE)
            written += 1
    for name in ("TITLEEGA.CMP", "CONTROL1.CMP", "TITLECGA.CMP", "CONTROL7.CMP", "DIALOG.BUE"):
        picture = screen(read(folder, name))
        png(os.path.join(out, name.lower().replace(".", "-") + ".png"), picture, CGA if picture.cga else PALETTE)
        written += 1
    png(os.path.join(out, "awards-pak.png"), screen(unpack(read(folder, "AWARDS.PAK"))[0]), PALETTE)
    png(os.path.join(out, "fcontrol-pak.png"), sheet(sprites(unpack(read(folder, "FCONTROL.PAK"))[0])), PALETTE)
    written += 2
    pakdata = find(folder, "PAKDATA")
    for name in sorted(os.listdir(pakdata)):
        with open(os.path.join(pakdata, name), "rb") as file:
            frames = sprites(unpack(file.read())[0])
        png(os.path.join(out, name.lower().replace(".", "-") + ".png"), sheet(frames), PALETTE)
        written += 1
    print(f"Wrote {written} pictures to {out}.")


def main(arguments: list[str]) -> None:
    if len(arguments) >= 2 and arguments[1] == "rooms":
        list_rooms(arguments[0])
    elif len(arguments) >= 2 and arguments[1] == "things":
        things(arguments[0])
    elif len(arguments) >= 2 and arguments[1] == "messages":
        list_messages(arguments[0])
    elif len(arguments) >= 3 and arguments[1] == "pictures":
        write_pictures(arguments[0], arguments[2])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
