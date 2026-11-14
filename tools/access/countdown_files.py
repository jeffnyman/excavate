"""Reads Countdown's files: the parts packed into its .CNT files, their text, its pictures and its sounds.

    python tools/access/countdown_files.py <the game's folder> parts                every part of every file
    python tools/access/countdown_files.py <the game's folder> text [<file>]        the text in them
    python tools/access/countdown_files.py <the game's folder> pictures <folder>    the rooms and pictures, as files
    python tools/access/countdown_files.py <the game's folder> sounds <folder>      the RealSound recordings, as WAV files

Each .CNT file is a count of parts, a table of where each starts, and the parts. Most parts are
the developers' own files, packed with LZW, each with a header of 122 bytes that starts with
MDmd and keeps the file's name, the folder it was in, and its date. The rest are stored as they
are: palettes, small tables, and whole screens as GIF pictures. How it all fits is in
docs/countdown.md.
"""

from __future__ import annotations

import os
import re
import struct
import sys
import zlib
from dataclasses import dataclass

# a packed part's header: MDmd, then at 18h the method (1, LZW), at 19h the size unpacked, at
# 1Dh the size packed, at 21h the file's DOS attributes, time and date, at 27h a check, and at
# 29h the file's name and folder, each a length and the text
HEADER = 122


@dataclass
class Part:
    file: str
    number: int
    start: int
    size: int
    name: str
    folder: str
    date: str
    data: bytes


def unlzw(data: bytes, size: int) -> bytes:
    """Unpacks LZW as Countdown packs it: codes from 9 bits to 13, read low bit first, 256 to
    start the table again and 257 to end."""
    out = bytearray()
    table = [bytes([i]) for i in range(256)] + [b"", b""]
    width, at, previous = 9, 0, None
    bits = len(data) * 8
    while len(out) < size and at + width <= bits:
        code = int.from_bytes(data[at >> 3:(at >> 3) + 3].ljust(3, b"\0"), "little") >> (at & 7) & ((1 << width) - 1)
        at += width
        if code == 256:
            table, width, previous = table[:258], 9, None
            continue
        if code == 257:
            break
        if code < len(table):
            entry = table[code]
        elif code == len(table) and previous is not None:
            entry = previous + previous[:1]
        else:
            raise ValueError(f"code {code} isn't in the table yet")
        out += entry
        if previous is not None and len(table) < 8192:
            table.append(previous + entry[:1])
            if len(table) >= 1 << width and width < 13:
                width += 1
        previous = entry
    return bytes(out)


def dos_date(date: int, time: int) -> str:
    return (f"{1980 + (date >> 9)}-{(date >> 5) & 15:02}-{date & 31:02} "
            f"{time >> 11:02}:{(time >> 5) & 63:02}")


def parts(folder: str, name: str) -> list[Part]:
    with open(os.path.join(folder, name), "rb") as file:
        data = file.read()
    count = struct.unpack_from("<H", data, 0)[0]
    starts = list(struct.unpack_from(f"<{count}I", data, 2)) + [len(data)]
    found = []
    for number in range(count):
        start, end = starts[number], starts[number + 1]
        if data[start:start + 4] != b"MDmd":
            found.append(Part(name, number, start, end - start, "", "", "", data[start:end]))
            continue
        size, packed = struct.unpack_from("<II", data, start + 0x19)
        time, date = struct.unpack_from("<HH", data, start + 0x23)
        length = data[start + 0x29]
        part_name = data[start + 0x2A:start + 0x2A + length].decode("ascii").strip()
        length2 = data[start + 0x36]
        part_folder = data[start + 0x37:start + 0x37 + length2].decode("ascii").strip()
        body = unlzw(data[start + HEADER:start + HEADER + packed], size)
        found.append(Part(name, number, start, end - start, part_name, part_folder, dos_date(date, time), body))
    return found


def cnt_files(folder: str) -> list[str]:
    return sorted(name for name in os.listdir(folder) if name.upper().endswith(".CNT"))


def kind(part: Part) -> str:
    if part.name:
        return part.name.rsplit(".", 1)[-1]
    if part.data[:6] in (b"GIF87a", b"GIF89a"):
        return "GIF"
    if len(part.data) == 768 and max(part.data) < 64:
        return "palette"
    return "data"


def list_parts(folder: str) -> None:
    for name in cnt_files(folder):
        for part in parts(folder, name):
            what = f"{part.name:12} {part.date}  {part.folder}" if part.name else kind(part)
            print(f"{name:13} {part.number:2}  {len(part.data):7}  {what}")


def list_text(folder: str, only: str | None) -> None:
    """The text in each part: runs of at least four letters, spaces and punctuation."""
    for name in cnt_files(folder):
        if only and name.upper() != only.upper() and name.upper() != only.upper() + ".CNT":
            continue
        for part in parts(folder, name):
            lines = [match.group().decode("ascii") for match in re.finditer(rb"[A-Za-z0-9 .,'!?\"():;-]{4,}", part.data)
                     if re.search(rb"[A-Z]{2}", match.group()) and b" " in match.group()]
            if lines:
                print(f"{name} {part.number} {part.name or kind(part)}")
                for line in lines:
                    print("    " + line.strip())


# --- the pictures ------------------------------------------------------------------------

# A room's picture (.PLY) is made of tiles, 16 by 16, laid out by a map. It starts with its
# map's width and height in tiles and the number of tiles, then the size of the window it's seen
# through (15 or 16 tiles by 8), and more we don't read yet, in 16 bytes; then its palette, colors
# 0 to 127, three 6-bit levels each; then its tiles, row by row, from byte 400; then the map, a
# byte for each place, the number of its tile. What follows the map we don't read yet. The colors
# above 127 are for the people and the screen's frame, which have palettes of their own.
TILES_AT, ROOM_COLORS, TILE = 400, 128, 16


def room(data: bytes) -> tuple[int, int, list[int], list[tuple[int, int, int]]]:
    """A room's picture: its width and height in pixels, its pixels and its palette."""
    columns, rows, count = data[0], data[1], data[2]
    palette = [tuple(level * 255 // 63 for level in data[16 + 3 * i:19 + 3 * i]) for i in range(ROOM_COLORS)]
    palette += [(128, 128, 128)] * (256 - ROOM_COLORS)
    width, height = columns * TILE, rows * TILE
    pixels = [0] * (width * height)
    tiles = data[TILES_AT:TILES_AT + count * TILE * TILE]
    room_map = data[TILES_AT + count * TILE * TILE:TILES_AT + count * TILE * TILE + columns * rows]
    for place, tile in enumerate(room_map):
        x, y = TILE * (place % columns), TILE * (place // columns)
        for row in range(TILE):
            at = (tile * TILE * TILE) + (row * TILE)
            pixels[(y + row) * width + x:(y + row) * width + x + TILE] = tiles[at:at + TILE]
    return width, height, pixels, palette


def likeliest(width: int, pixels: list[int], own: list[tuple[int, int, int]], others: list[Part]) -> list[tuple[int, int, int]]:
    """Which palette a room is shown in isn't in its picture, and we haven't found where the game
    keeps it. Most rooms use their own colors 0 to 127, with the people's palette (.COL) above;
    a few use a .COL for everything. This picks the one the picture looks smoothest in: a wrong
    palette shows as speckle, neighboring pixels far apart in color for how far the picture's
    colors spread."""
    candidates = [own]
    for other in others:
        full = [tuple(level * 255 // 63 for level in other.data[3 * i:3 * i + 3]) for i in range(256)]
        candidates += [own[:ROOM_COLORS] + full[ROOM_COLORS:], full]

    def roughness(palette: list[tuple[int, int, int]]) -> float:
        # how far neighbors are apart, against how far the picture's colors spread: a palette
        # that's all dark is smooth too, but has no spread
        near, levels = 0, []
        for at in range(0, len(pixels) - 1, 7):
            a = palette[pixels[at]]
            levels.append(sum(a))
            if (at + 1) % width:
                b = palette[pixels[at + 1]]
                near += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2])
        mean = sum(levels) / len(levels)
        spread = (sum((level - mean) ** 2 for level in levels) / len(levels)) ** 0.5
        return (near / len(levels) + 10) / (spread + 1)

    return min(candidates, key=roughness)


def png(path: str, width: int, height: int, pixels: list[int], palette: list[tuple[int, int, int]], scale: int = 2) -> None:
    raw = bytearray()
    for y in range(height):
        line = bytearray()
        for x in range(width):
            line += bytes(palette[pixels[y * width + x]]) * scale
        raw += (b"\0" + line) * scale

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    header = struct.pack(">IIBBBBB", width * scale, height * scale, 8, 2, 0, 0, 0)
    with open(path, "wb") as file:
        file.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def write_pictures(folder: str, out: str) -> None:
    os.makedirs(out, exist_ok=True)
    written = 0
    for name in cnt_files(folder):
        for part in parts(folder, name):
            stem = f"{name[:-4].lower()}-{part.number}"
            if kind(part) == "PLY":
                width, height, pixels, palette = room(part.data)
                palette = likeliest(width, pixels, palette, [other for other in parts(folder, name) if kind(other) == "COL"])
                png(os.path.join(out, stem + ".png"), width, height, pixels, palette)
                written += 1
            elif kind(part) == "GIF":
                with open(os.path.join(out, stem + ".gif"), "wb") as file:
                    file.write(part.data)
                written += 1
    print(f"Wrote {written} pictures to {out}.")


# --- the sounds --------------------------------------------------------------------------

# A RealSound recording (.RS): 32 bytes, STEVE (for Steve Witzel, who wrote RealSound), how many
# of the timer's interrupts each sample lasts, H, and the count of samples; then the samples,
# each the count RealSound loads into the timer's counter 2 at 16,572 interrupts a second: the
# speaker is off for that many of the 72 ticks between interrupts, and on for the rest.
RATE, TICKS = 1_193_182 / 72, 72


def wav(path: str, samples: bytes, rate: int) -> None:
    with open(path, "wb") as file:
        file.write(b"RIFF" + struct.pack("<I", 36 + len(samples)) + b"WAVEfmt "
                   + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate, 1, 8)
                   + b"data" + struct.pack("<I", len(samples)) + samples)


def write_sounds(folder: str, out: str) -> None:
    os.makedirs(out, exist_ok=True)
    written = set()
    for name in cnt_files(folder):
        for part in parts(folder, name):
            if kind(part) != "RS" or part.name in written:
                continue
            data = part.data
            if data[:5] != b"STEVE":
                continue
            repeat, count = data[5], struct.unpack_from("<I", data, 7)[0]
            # the share of each interrupt the speaker is on, as an 8-bit sample
            samples = bytes(max(0, min(255, (TICKS - count_) * 255 // TICKS)) for count_ in data[32:32 + count])
            wav(os.path.join(out, part.name.lower().replace(".rs", ".wav")), samples, round(RATE / repeat))
            written.add(part.name)
    print(f"Wrote {len(written)} sounds to {out}.")


def main(arguments: list[str]) -> None:
    if len(arguments) >= 2 and arguments[1] == "parts":
        list_parts(arguments[0])
    elif len(arguments) >= 2 and arguments[1] == "text":
        list_text(arguments[0], arguments[2] if len(arguments) >= 3 else None)
    elif len(arguments) >= 3 and arguments[1] == "pictures":
        write_pictures(arguments[0], arguments[2])
    elif len(arguments) >= 3 and arguments[1] == "sounds":
        write_sounds(arguments[0], arguments[2])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
