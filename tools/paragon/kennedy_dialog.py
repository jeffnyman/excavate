"""Reads the conversation that ends Guardians of Infinity: To Save Kennedy, aboard Air Force One,
from K.DAT: what Kennedy and Dave Powers say, to what you type, and what each answer does.

    python tools/paragon/kennedy_dialog.py <the game's folder>             every section
    python tools/paragon/kennedy_dialog.py <the game's folder> <section>   one (k0 to k3, or kt)
    python tools/paragon/kennedy_dialog.py <the game's folder> --points    what wins you points

RUND.EXE runs it. What you type is cut into words, with each of the synonyms below turned into
its word first ("kennedy" and "you" become "jfk"); a pattern matches when its words come in its
order, others between them allowed, or, joined by '_', one right after another. An answer is
given for a set of patterns all matched; the first in a section's order whose conditions hold is
said. If nothing you just said matches, it goes through them again for what you've said before,
and then for the answers given when nothing matches. Six points convince Kennedy; the flight's
15 minutes running out lands you in Dallas (section kt).
"""

from __future__ import annotations

import os
import struct
import sys
from dataclasses import dataclass

# RUND.EXE's synonyms: each word is put in place of the others before the patterns are matched
SYNONYMS = {
    "jfk": ["kennedy", "president", "you", "john", "your", "john's"],
    "kill": ["killed", "assasinated", "shot", "assassinate", "die", "shoot"],
    "plot": ["conspiracy"],
    "part": ["member", "involved"],
    "dave": ["powers"],
    "mothers": ["mother's", "mothers'"],
    "troops": ["troop"],
    "withdraw": ["withdrawal", "withdrawing"],
    "vietnam": ["veitnam"],
    "heinekens": ["heineken", "hienekens", "hieneken", "heneikens", "heniekens"],
    "songs": ["song", "tunes", "ditties"],
    "caroline": ["caroline's", "carolines"],
    "oswald": ["assassin", "gunmen", "gunman"],
    "conway's": ["conways", "conway"],
    "prep": ["preperation"],
    "future": ["2087"],
    "1000": ["000"],
    "shaw": ["maude"],
    "krushchev": ["kruschev"],
    "bath": ["bathtub", "bathroom", "tub"],
    "fake": ["faked", "false", "pretend", "pretended"],
    "i": ["i'm"],
    "john-john": ["john-john's"],
    "likes": ["like", "enjoys", "choice"],
    "dislike": ["hate", "hatred", "hated", "disliked", "dislikes"],
    "sorenson": ["ghost"],
    "fuck": ["shit", "dirtbag", "fucker", "shithead", "homo"],
}

SPEAKERS = {"k": "Kennedy", "d": "Powers"}

ENDINGS = {
    "0": "the end: Kennedy goes on to Dallas, and is shot",
    "t": "the end: Kennedy goes on to Dallas, and is shot",
    "1": "the end: Kennedy turns back for Washington, but takes you with him, and you never "
         "return to the time sphere",
}

# what a pattern number means in a section's table
ANY, ALSO, END = 0xFFFD, 0xFFFF, 0xFFFE


@dataclass
class Answer:
    number: int
    when: list[list[int]]  # sets of patterns, any one of them; [] for nothing matched
    script: str


@dataclass
class Section:
    name: str
    opening: str
    patterns: list[str]
    answers: list[Answer]


def find(folder: str, name: str) -> str:
    for file in os.listdir(folder):
        if file.upper() == name:
            return os.path.join(folder, file)
    raise SystemExit(f"no {name} in {folder}")


def text(data: bytes, at: int) -> str:
    """A string of the section's text, which is XORed with 1Ah, its zero too."""
    end = data.index(0x1A, at)
    return bytes(b ^ 0x1A for b in data[at:end]).decode("latin-1")


def read_sections(folder: str) -> list[Section]:
    with open(find(folder, "K.DAT"), "rb") as f:
        data = f.read()

    sections = []
    # after 1Ah and a word: a byte for how many sections, then each one's name, length, and place
    count = data[2]
    for n in range(count):
        entry = data[3 + (22 * n):3 + (22 * (n + 1))]
        name = entry[:13].rstrip(b"\0").decode()
        length, = struct.unpack("<I", entry[13:17])
        place, = struct.unpack("<I", entry[18:22])
        sections.append(read_section(name, data[place:place + length]))
    return sections


def read_section(name: str, data: bytes) -> Section:
    # a word, the patterns' places, the answers' places in the table and the text, and the
    # table's place: all from the end of this header, where the opening line starts
    patterns_count, = struct.unpack_from("<H", data, 2)
    patterns_at = struct.unpack_from(f"<{patterns_count}H", data, 4)
    at = 4 + (2 * patterns_count)
    answers_count, = struct.unpack_from("<H", data, at)
    pairs = struct.unpack_from(f"<{2 * answers_count}H", data, at + 2)
    at += 2 + (4 * answers_count)
    table_at, = struct.unpack_from("<H", data, at)
    base = at + 2

    table_count, = struct.unpack_from("<H", data, base + table_at)
    table = struct.unpack_from(f"<{table_count}H", data, base + table_at + 2)

    answers = []
    for n in range(answers_count):
        entry, script_at = pairs[2 * n], pairs[(2 * n) + 1]
        answers.append(Answer(n, read_when(table, entry), text(data, base + script_at)))

    return Section(
        name=name,
        opening=text(data, base),
        patterns=[text(data, base + p) for p in patterns_at],
        answers=answers)


def read_when(table: tuple[int, ...], at: int) -> list[list[int]]:
    """The sets of patterns an answer is for, up to the table's end mark; ANY alone for nothing
    matched."""
    sets, current = [], []
    while table[at] != END:
        word = table[at]
        if word == ALSO:
            if current:
                sets.append(current)
            current = []
        elif word != ANY:
            current.append(word)
        at += 1
    if current:
        sets.append(current)
    return sets


def describe(section: Section, answer: Answer) -> list[str]:
    """What an answer says and does, a line each, in the order RUND.EXE takes them."""
    def pattern(n: str) -> str:
        number = int(n)
        return f"\"{section.patterns[number]}\"" if number < len(section.patterns) else f"pattern {n}"

    # the commands that take a string, between a delimiter (the character after the command)
    # and the next of it
    with_string = {
        "k": lambda a: f"Kennedy: {a}",
        "d": lambda a: f"Powers: {a}",
        "?": lambda a: f"(only if you've never said {pattern(a)})",
        "u": lambda a: f"(forgets you said {pattern(a)})",
        "f": lambda a: f"(sets flag {a})",
        "=": lambda a: f"(only if flag {a} is set)",
        "#": lambda a: f"(only if flag {a} isn't set)",
        "r": lambda a: f"(goes to section {a})",
    }
    # the commands that take the one character after them
    with_character = {
        "+": lambda a: f"(+{a} points)",
        "-": lambda a: f"(-{a} points)",
        "e": lambda a: f"({ENDINGS.get(a, 'an ending')})",
    }
    alone = {
        "^": "(said once)",
        ">": "(goes to the back of the line)",
        "c": "(forgets everything you've said)",
        "g": "(only for what you said before)",
        "n": "(only for what you just said)",
        "!": "(never said)",
    }

    lines = []
    script = answer.script
    i = 0
    while i < len(script):
        command = script[i]
        if command in with_string:
            delimiter = script[i + 1]
            end = script.index(delimiter, i + 2)
            lines.append(with_string[command](script[i + 2:end]))
            i = end + 1
        elif command in with_character:
            lines.append(with_character[command](script[i + 1]))
            i += 2
        else:
            lines.append(alone.get(command, f"(unknown command {command!r})"))
            i += 1
    return lines


def points(section: Section, answer: Answer) -> int:
    return sum(int(line[2]) for line in describe(section, answer) if line.startswith("(+"))


def when(section: Section, answer: Answer) -> str:
    if not answer.when:
        return "when nothing matches"
    return " or ".join(" + ".join(f"\"{section.patterns[p]}\"" for p in group) for group in answer.when)


def show_section(section: Section) -> None:
    print(f"== {section.name}")
    print("Opens:")
    for line in describe(section, Answer(-1, [], section.opening)):
        print(f"    {line}")
    print()
    print("Patterns:")
    for n, pattern in enumerate(section.patterns):
        print(f"  {n:3}  {pattern}")
    print()
    for answer in section.answers:
        print(f"Answer {answer.number}, {when(section, answer)}:")
        for line in describe(section, answer):
            print(f"    {line}")
    print()


def show_points(sections: list[Section]) -> None:
    """Every answer that gives points, with what to say for it: six convince Kennedy."""
    total = 0
    for section in sections:
        for answer in section.answers:
            gained = points(section, answer)
            if gained:
                total += gained
                print(f"{section.name} answer {answer.number}: +{gained}, {when(section, answer)}")
    print(f"{total} points in all; six convince Kennedy")


def main() -> None:
    if len(sys.argv) not in (2, 3):
        raise SystemExit(__doc__)

    sections = read_sections(sys.argv[1])
    if len(sys.argv) == 2:
        for section in sections:
            show_section(section)
    elif sys.argv[2] == "--points":
        show_points(sections)
    else:
        wanted = [s for s in sections if s.name == sys.argv[2].lower()]
        if not wanted:
            raise SystemExit(f"no section {sys.argv[2]}: there are {', '.join(s.name for s in sections)}")
        show_section(wanted[0])


if __name__ == "__main__":
    main()
