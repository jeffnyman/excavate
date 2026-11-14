"""Reads Countdown's files: the parts packed into its .CNT files, their text, its pictures and its sounds.

    python tools/access/countdown_files.py <the game's folder> parts                every part of every file
    python tools/access/countdown_files.py <the game's folder> text [<file>]        the text in them
    python tools/access/countdown_files.py <the game's folder> pictures <folder>    the rooms and pictures, as files
    python tools/access/countdown_files.py <the game's folder> sounds <folder>      the RealSound recordings, as WAV files
    python tools/access/countdown_files.py <the game's folder> room <file>          a room's things, what each action says, and its messages
    python tools/access/countdown_files.py <the game's folder> talk <file>          a conversation, by approach, and the answers to ASK ABOUT

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
# through (15 or 16 tiles by 8), then two words: the number of lines that edge the floor, and
# the number of rectangles that can be clicked on. Then its palette, colors 0 to 127, three 6-bit
# levels each; then its tiles, row by row, from byte 400; then the map, a byte for each place,
# the number of its tile. After the map come the lines and then the rectangles, each as four
# arrays of words: the lines' first x, first y, second x and second y, and the rectangles' left,
# top, right and bottom. The colors above 127 are for the people and the screen's frame, which
# have palettes of their own.
TILES_AT, ROOM_COLORS, TILE = 400, 128, 16


def color(levels: bytes, at: int) -> tuple[int, int, int]:
    """A palette entry: three 6-bit levels, as 8-bit ones."""
    return levels[at] * 255 // 63, levels[at + 1] * 255 // 63, levels[at + 2] * 255 // 63


def room(data: bytes) -> tuple[int, int, list[int], list[tuple[int, int, int]]]:
    """A room's picture: its width and height in pixels, its pixels and its palette."""
    columns, rows, count = data[0], data[1], data[2]
    palette = [color(data, 16 + 3 * i) for i in range(ROOM_COLORS)]
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


Shape = tuple[int, int, int, int]


def shapes(data: bytes) -> tuple[list[Shape], list[Shape]]:
    """A room's lines and rectangles, from after its map. The lines edge the floor: Mason can't
    walk across them. The rectangles are where things can be clicked on, one for each entry in
    the table of the room's .DAT, so a thing can have more than one. Both are in the room's own
    pixels; the screen shows the room's window centered across, and from the line under the score."""
    columns, rows, count = data[0], data[1], data[2]
    lines, rectangles = struct.unpack_from("<HH", data, 6)
    at = TILES_AT + count * TILE * TILE + columns * rows

    def arrays(n: int) -> list[Shape]:
        nonlocal at
        values = struct.unpack_from(f"<{4 * n}H", data, at)
        at += 8 * n
        return [(values[i], values[n + i], values[2 * n + i], values[3 * n + i]) for i in range(n)]

    return arrays(lines), arrays(rectangles)


def likeliest(width: int, pixels: list[int], own: list[tuple[int, int, int]], others: list[Part]) -> list[tuple[int, int, int]]:
    """Which palette a room is shown in isn't in its picture. Most rooms use their own colors 0 to
    127, with Mason's palette above, which the game picks for the scene (MAN.COL in his cell,
    WALL.COL on the ledge outside, both in TITLE.CNT); a few use a .COL for everything, which we
    haven't seen the game pick yet. This picks the one the picture looks smoothest in: a wrong
    palette shows as speckle, neighboring pixels far apart in color for how far the picture's
    colors spread."""
    candidates: list[list[tuple[int, int, int]]] = [own]
    for other in others:
        full = [color(other.data, 3 * i) for i in range(256)]
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


# --- the rooms' text --------------------------------------------------------------------

ACTIONS = ["LOOK", "OPEN", "MOVE", "GET", "USE", "GO TO", "TALK", "TASTE", "TRAVEL"]


def zero_ended(data: bytes, at: int) -> tuple[str, int]:
    end = data.index(0, at)
    return data[at:end].decode("latin-1"), end + 1


Thing = tuple[str, list[tuple[str, str]]]


def room_text(data: bytes) -> tuple[list[Thing], list[int], list[str]]:
    """A room's things, its table and its messages (.DAT). It starts with a word, where the
    messages start, and the table: where each thing starts, in the order of the rectangles in
    the room's picture, so a thing with more than one rectangle is in it more than once. A thing is
    its name, then for each action that has something to say, the action's number in the bar (0
    for LOOK to 8 for TRAVEL) and the text, and FFh to end. The table here is each rectangle's
    thing, by its number. The messages are what the room's puzzles say, which the game's code
    picks by number; whether a thing is there at all is in the code too."""
    messages_at = struct.unpack_from("<H", data, 0)[0]
    first = struct.unpack_from("<H", data, 2)[0]
    starts = struct.unpack_from(f"<{(first - 2) // 2}H", data, 2)
    found, numbers = [], {}
    at = first
    while at < messages_at:
        numbers[at] = len(found)
        name, at = zero_ended(data, at)
        entries = []
        while at < messages_at and data[at] != 0xFF:
            action = data[at]
            text, at = zero_ended(data, at + 1)
            entries.append((ACTIONS[action] if action < len(ACTIONS) else f"({action})", text))
        at += 1
        found.append((name, entries))
    messages = [message.decode("latin-1") for message in data[messages_at:].split(b"\0")]
    if messages and messages[-1] == "":
        messages.pop()
    return found, [numbers.get(start, -1) for start in starts], messages


def list_room(folder: str, name: str) -> None:
    room_parts = parts(folder, name if name.upper().endswith(".CNT") else name + ".CNT")
    lines, rectangles = [], []
    for part in room_parts:
        if kind(part) == "PLY":
            lines, rectangles = shapes(part.data)
    for part in room_parts:
        if kind(part) != "DAT":
            continue
        things_, table, messages = room_text(part.data)
        print(f"{len(lines)} lines edge the floor; {len(rectangles)} rectangles can be clicked on")
        for number, (thing, entries) in enumerate(things_):
            where = [rectangles[i] for i, owner in enumerate(table) if owner == number and i < len(rectangles)]
            print(thing + ("  " + " ".join(f"({x1},{y1})-({x2},{y2})" for x1, y1, x2, y2 in where) if where else "  (no rectangle)"))
            for action, text in entries:
                print(f"    {action:6} {text}")
        print("Messages")
        for number, message in enumerate(messages):
            print(f"    {number:2} {message}")


# --- the conversations --------------------------------------------------------------------

# The names Mason can ask about, as COUNT.EXE lists them, in order
TOPICS = ["ACCIDENT", "MASON POWERS", "FONTAINE", "GENERAL LEELAND", "NIRVA TOOMISH", "FRANK MCBAIN",
          "FAROUK HASHISH", "STORMBRINGER", "THUNDERBOLT", "DOLPHIN", "BLACK DECEMBER", "GOLDEN DESIRE",
          "SCORPIO", "LISA LOOMIS", "BUZZ BREZHNEV", "BARNEY SACNUSSEN", "EARHARDT FLEISCHER",
          "MICHAEL KIRKLAND", "JACK QUINN", "CARLOS RAMIREZ", "JACKAL", "GINA", "RACHEL AKURE", "SANCTUARY",
          "COMPANY", "HAKEEM ABABASH", "LEONARD SPALDING", "DEC. MANIFESTO"]
APPROACHES = ["HELP", "HASSLE", "PLEASANT", "BLUFF"]
SECTION, RECORD = 0x2C0, 8
LINES_AT = 4 * SECTION


Record = tuple[int, int, int, list[int]]


def conversation(data: bytes) -> tuple[list[list[Record]], list[str], list[str], list[tuple[int, int, str]]]:
    """A conversation (.TRE): four sections of 88 records, one for each approach Mason starts
    with, then three blocks, each a word for its size and then its text: what Mason says, what
    he's told, and the answers to ASK ABOUT. A section is a tree four approaches deep, 88 being
    4 + 4 + 16 + 64 (see path). A record is 8 bytes: Mason's line, the answer, the face the person
    makes (0 to 3), then the commands, up to a dot; the rest is left over from an editor, and so
    are the records whose face isn't 0 to 3. An answer to ASK ABOUT is the topic's number, from 0
    for ACCIDENT, the face, and the text."""
    sections = []
    for section in range(4):
        records = []
        for number in range(SECTION // RECORD):
            record = data[section * SECTION + number * RECORD:section * SECTION + (number + 1) * RECORD]
            commands = []
            for byte in record[3:]:
                if byte == 0x2E:
                    break
                commands.append(byte)
            records.append((record[0], record[1], record[2], commands))
        sections.append(records)
    blocks, at = [], LINES_AT
    for _ in range(3):
        size = struct.unpack_from("<H", data, at)[0]
        blocks.append(data[at + 2:at + 2 + size])
        at += 2 + size
    said = [line.decode("latin-1") for line in blocks[0].split(b"\0")[:-1]]
    told = [line.decode("latin-1") for line in blocks[1].split(b"\0")[:-1]]
    answers, at = [], 0
    while at + 2 < len(blocks[2]):
        topic, face = blocks[2][at], blocks[2][at + 1]
        text, at = zero_ended(blocks[2], at + 2)
        answers.append((topic, face, re.sub(r"^[^A-Z0-9'\"(]+", "", text)))
    return sections, said, told, answers


def path(section: int, number: int) -> list[int] | None:
    """The approaches that lead to a record, as numbers (0 HELP, 1 HASSLE, 2 PLEASANT, 3 BLUFF):
    the first is the section's; the second picks one of records 4 to 7, the third one of 8 to
    23 (four for each second), the fourth one of 24 to 87. Records 0 to 3 other than the
    section's own are copies, which aren't reached."""
    if number < 4:
        return [section] if number == section else None
    if number < 8:
        return [section, number - 4]
    if number < 24:
        return [section, (number - 8) // 4, (number - 8) % 4]
    return [section, (number - 24) // 16, (number - 24) // 4 % 4, (number - 24) % 4]


def commands_text(commands: list[int]) -> str:
    """What a record's commands do: 02 and a topic's number adds the topic to ASK ABOUT, 05 and
    two bytes we haven't read kills Mason, 08 ends the talk, and 0B ends that line of
    questioning ("I DON'T KNOW MUCH MORE ALONG THESE LINES. TRY A NEW APPROACH.")."""
    said, at = [], 0
    while at < len(commands):
        command = commands[at]
        if command == 0x02 and at + 1 < len(commands):
            topic = commands[at + 1]
            said.append(f"adds {TOPICS[topic] if topic < len(TOPICS) else topic}")
            at += 2
        elif command == 0x05:
            said.append("kills Mason" + (f" ({' '.join(f'{b:02X}' for b in commands[at + 1:at + 3])})" if at + 1 < len(commands) else ""))
            at += 3
        elif command == 0x08:
            said.append("ends the talk")
            at += 1
        elif command == 0x0B:
            said.append("ends the line")
            at += 1
        else:
            said.append(f"{command:02X}h")
            at += 1
    return ", ".join(said)


def list_talk(folder: str, name: str) -> None:
    for part in parts(folder, name if name.upper().endswith(".CNT") else name + ".CNT"):
        if kind(part) != "TRE":
            continue
        sections, said, told, answers = conversation(part.data)
        for section, records in enumerate(sections):
            print(f"Starting with {APPROACHES[section]}")
            for number, (line, answer, face, commands) in enumerate(records):
                approaches = path(section, number)
                if approaches is None or face > 3 or line >= len(said) or answer >= len(told):
                    continue
                done = commands_text(commands)
                print(f"    {number:2}  {' '.join(APPROACHES[a] for a in approaches)}  (face {face}{', ' + done if done else ''})")
                print(f"        MASON: {said[line]}")
                print(f"        {told[answer]}")
        print("Asked about")
        for topic, face, text in answers:
            if text.strip():
                print(f"    {TOPICS[topic] if topic < len(TOPICS) else topic}, face {face}: {text}")


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
    elif len(arguments) >= 3 and arguments[1] == "room":
        list_room(arguments[0], arguments[2])
    elif len(arguments) >= 3 and arguments[1] == "talk":
        list_talk(arguments[0], arguments[2])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
