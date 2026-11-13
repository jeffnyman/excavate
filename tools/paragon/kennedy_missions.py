"""Reads the scripts of Guardians of Infinity: To Save Kennedy's missions: what each agent asks
you, what answers it wants, where and when the person can be found, and what each report says
and leads to.

    python tools/paragon/kennedy_missions.py <the game's folder> person <who>    a person's mission
    python tools/paragon/kennedy_missions.py <the game's folder> task <number>   one of the tasks
    python tools/paragon/kennedy_missions.py <the game's folder> --convinced     the three that win
    python tools/paragon/kennedy_missions.py <the game's folder> --check         every script, checked

GOI.EXE runs a mission's script a stage at a time. A stage is commands, then where and when the
person can be found, then the reports that can come back; the first whose condition holds is
the one given, and if none does, the agent goes on to the next stage. Three stages in all the
missions add one to the count of people who warn Dave Powers; three such warnings win the game.
How it all fits is in docs/kennedy.md.
"""

from __future__ import annotations

import struct
import sys
from dataclasses import dataclass

from kennedy_files import (
    AGENTS,
    Passage,
    Record,
    Vocabulary,
    Words,
    read_records,
    vocabulary,
    who,
)

# a condition's items: the tag, how many bytes follow it
CONDITION_SIZES = {
    1: 1,
    2: 0,
    3: 0,
    4: 4,
    5: 1,
    6: 1,
    7: 1,
    8: 0,
    9: 1,
    10: 1,
    11: 1,
    12: 1,
    13: 1,
}

# the order's parts that opcodes 06h and 07h set
SLOTS = {1: "hour", 2: "day", 3: "a.m. or p.m.", 4: "city", 5: "place"}

# opcode 0Fh's 17 special actions
SPECIALS = {
    1: "makes the person William Douglas",
    2: "makes the time an hour later",
    3: "leaves the agent in 1963, waiting",
    4: "goes on to the mission to kill the person",
    5: 'says "O.K."',
    6: "notes whether the person is an agent left in 1963",
    7: "unless flag 6 is set, leaves the agent in 1963, and no report comes",
    8: "marks that Steel has gone back to the ship",
    9: "takes the order without reading it back",
    0xA: "goes on to the mission to disable an FBI agent's car, at stage 6",
    0xB: "makes the person no longer unfamiliar",
    0xC: "makes the time now",
    0xD: "makes the time none",
    0xE: "sets the agent's flag 8",
    0xF: "marks that the motorcade has been canceled",
    0x10: (
        "at the person's workplace, unless the agent's flag 2 is set, goes on to the FBI agents'"
        " arrest, at stage 6"
    ),
    0x11: 'says "(static)"',
}

# opcode 12h's endings
ENDINGS = {
    1: "the agent left in a coma",
    2: "the agent shot escaping the police",
    3: "the agent dies",
}

NEVER = 0xC7


class Bad(Exception):
    pass


@dataclass
class Stage:
    number: int
    lines: list[str]
    end: int  # where its trailer ends
    convinced: bool


class Mission:
    """A record's script, and the text it points into."""

    def __init__(self, record: Record, words: Vocabulary):
        self.record = record
        self.words = words
        self.script = record.script
        self.items = {item.at: item for item in record.items}

    def word(self, at: int) -> int:
        return struct.unpack_from("<H", self.script, at)[0]

    def jump(self, at: int) -> int:
        """A jump's target counts from the jump's own word."""
        return (at + self.word(at)) & 0xFFFF

    def text(self, at: int) -> str:
        item = self.items.get(at)
        if isinstance(item, Words):
            return f"the words {' '.join(item.words)}"
        if isinstance(item, Passage):
            ring = [item]
            following = self.items.get(item.next)
            while isinstance(following, Passage) and following is not item:
                ring.append(following)
                following = self.items.get(following.next)
            said = " ".join(item.text.split())
            more = f" (and {len(ring) - 1} more)" if len(ring) > 1 else ""
            return f'{speakers(item.agents)}: "{said[:70]}{"..." if len(said) > 70 else ""}"{more}'
        return "no words"

    def noun(self, n: int) -> str:
        return named(self.words.nouns, n)

    def condition(self, at: int) -> tuple[str, int]:
        """Items, ended by a zero; items with the same tag are ORed, the rest ANDed."""
        groups: list[list[str]] = []
        last = None
        while True:
            tag = self.script[at]
            at += 1
            if tag == 0:
                break
            if tag not in CONDITION_SIZES:
                raise Bad(f"condition tag {tag:02X}h")
            item = self.condition_item(tag, at)
            at += CONDITION_SIZES[tag]
            if tag == last:
                groups[-1].append(item)
            else:
                groups.append([item])
            last = tag
        parts = [" or ".join(group) for group in groups if group != ["always"]]
        return " and ".join(parts) if parts else "always", at

    def condition_item(self, tag: int, at: int) -> str:
        value = self.script[at] if CONDITION_SIZES[tag] == 1 else 0
        if tag == 1:
            return f"the agent is {speakers(value, ' or ')}"
        if tag == 2:
            return "the person is unfamiliar"
        if tag == 3:
            return "the agent's flag 2 is set"
        if tag == 4:
            start, end = struct.unpack_from("<2h", self.script, at)
            return f"between {clock(start)} and {clock(end)}"
        if tag == 5:
            return f"the person is {self.noun(value)}"
        if tag == 6:
            return f"it's {named(self.words.days, value)}"
        if tag == 7:
            return (
                "you said to tell them you're from the future"
                if value
                else "you said not to"
            )
        if tag == 8:
            return "always"
        if tag == 9:
            return f"in {named(self.words.cities, value)}"
        if tag == 10:
            return f"at the {named(self.words.places, value)}"
        if tag == 11:
            return f"the order is {named(self.words.verbs, value)}"
        if tag == 12:
            return f"flag {value} is set"
        return f"flag {value} isn't set"

    def stage(self, number: int, start: int) -> Stage:
        """One stage: its length (counting itself), its commands, then its trailer."""
        s = self.script
        end = start + self.word(start)
        at = start + 2
        lines = []
        convinced = False
        while at < end:
            here = at
            op = s[at]
            at += 1
            if op == 0:
                continue
            if op == 1:
                line = "the order is given; the agent goes"
            elif op == 2:
                when, at = self.condition(at)
                line = (
                    f"if {when}, asks {self.text(self.word(at + 2))}; on no, to {self.jump(at + 4):04X}"
                    f" (else to {self.jump(at):04X})"
                )
                at += 6
            elif op == 3:
                when, at = self.condition(at)
                least = s[at + 6]
                wants = (
                    "anything"
                    if least == 0
                    else "nothing; it passes"
                    if least > 100
                    else f"{least}% of {self.text(self.word(at + 4))}"
                )
                line = (
                    f"if {when}, asks {self.text(self.word(at + 2))}; wants {wants};"
                    f" if not, to {self.jump(at + 7):04X} (else to {self.jump(at):04X})"
                )
                at += 9
            elif op == 4:
                when, at = self.condition(at)
                line = (
                    f"if {when}, asks whether to say they're from the future: {self.text(self.word(at))};"
                    f" if so, what to say: {self.text(self.word(at + 5))}, wanting {s[at + 4]}% of"
                    f" {self.text(self.word(at + 2))}"
                )
                at += 7
            elif op == 5:
                line = "asks where and when, reads the order back, and waits for it to be confirmed"
            elif op in (6, 7):
                slot, value = s[at], self.word(at + 1)
                at += 3
                if slot not in SLOTS:
                    raise Bad(f"order slot {slot}")
                names = {
                    3: ["a.m.", "p.m."],
                    4: self.words.cities,
                    5: self.words.places,
                }.get(slot)
                shown = named(names, value) if names else str(value)
                line = f"{'sets' if op == 6 else 'unless given, sets'} the {SLOTS[slot]} to {shown}"
            elif op == 8:
                line = f"goes on to the mission to see {self.noun(self.word(at))}, at stage {s[at + 2]}"
                at += 3
            elif op == 9:
                line = f"goes on to the mission to see {self.noun(self.word(at))}"
                at += 2
            elif op == 0xA:
                when, at = self.condition(at)
                line = f"if {when}, says {self.text(self.word(at))}"
                at += 3
            elif op == 0xB:
                hour, minute, half, next_day = s[at : at + 4]
                at += 4
                line = (
                    "sets no meeting time"
                    if half == 0xF7
                    else f"sets the meeting for {hour}:{minute:02d} {'p.m.' if half else 'a.m.'}"
                    f"{' the next day' if next_day else ''}"
                )
            elif op == 0xC:
                line = 'gives up: "What should I do now?"'
            elif op == 0xD:
                line = f"to {self.jump(at):04X}"
                at += 2
            elif op == 0xE:
                line = "** convinced: one more person warns Dave Powers **"
                convinced = True
            elif op == 0xF:
                special = s[at]
                at += 1
                if special not in SPECIALS:
                    raise Bad(f"special {special:02X}h")
                line = SPECIALS[special]
            elif op == 0x10:
                line = f"sets flag {s[at]}"
                at += 1
            elif op == 0x11:
                line = f"clears flag {s[at]}"
                at += 1
            elif op == 0x12:
                line = f"ends the game: {ENDINGS.get(s[at], s[at])}, after {hours(s[at + 1])}"
                at += 2
            else:
                raise Bad(f"opcode {op:02X}h at {here:04X}")
            if at > end:
                raise Bad(f"the command at {here:04X} runs past the stage")
            lines.append(f"  {here:04X}  {line}")

        # the trailer: the delays, the appointments, and the reports
        at = end
        next_delay, report_delay, appointments = s[at], s[at + 1], s[at + 2]
        at += 3
        lines.append(
            f"  then: the report comes after {hours(report_delay)},"
            f" the next stage after {hours(next_delay)}"
        )
        for _ in range(appointments):
            day, start_time, end_time, city, place = struct.unpack_from("<BhhBB", s, at)
            at += 7
            lines.append(
                f"  found: {named(self.words.days, day)}, {clock(start_time)} to {clock(end_time)},"
                f" in {named(self.words.cities, city)}, at the {named(self.words.places, place)}"
            )
        reports = s[at]
        at += 1
        for _ in range(reports):
            said, count = self.word(at), s[at + 2]
            at += 3
            conditions = []
            for _ in range(count):
                when, at = self.condition(at)
                conditions.append(when)
            when = (
                " or ".join(f"[{c}]" for c in conditions)
                if len(conditions) > 1
                else "".join(conditions)
            )
            lines.append(f"  report, if {when or 'never'}: {self.text(said)}")
        return Stage(number, lines, at, convinced)

    def stages(self) -> list[Stage]:
        """The index's words count from themselves to each stage."""
        count = len(self.record.table)
        starts = [(2 * n) + self.word(2 * n) for n in range(count)]
        stages = []
        for n, start in enumerate(starts):
            stage = self.stage(n + 1, start)
            following = min([s for s in starts if s > start] + [len(self.script)])
            if stage.end != following:
                raise Bad(f"stage {n + 1} ends at {stage.end:04X}, not {following:04X}")
            stages.append(stage)
        return stages


def named(names: list[str], value: int) -> str:
    """A name from a list, or none (F7h, FFh, or FFF7h, as the game marks it)."""
    if value in (0xF7, 0xFF, 0xFFF7):
        return "none"
    return names[value] if value < len(names) else f"number {value}"


def speakers(agents: int, joined: str = ", ") -> str:
    if agents == 0x1F:
        return "anyone"
    return joined.join(name for bit, name in enumerate(AGENTS) if agents & (1 << bit))


def clock(minutes: int) -> str:
    return (
        f"{minutes // 60:02d}:{minutes % 60:02d}" if minutes >= 0 else f"{minutes} min"
    )


def hours(halves: int) -> str:
    """A delay, in half hours."""
    if halves >= NEVER:
        return "never"
    whole, half = divmod(halves * 30, 60)
    return f"{whole} h {half} min" if half else f"{whole} h"


def show(record: Record, words: Vocabulary) -> None:
    mission = Mission(record, words)
    print(f"== {record.number}: {who(record, words)}")
    for stage in mission.stages():
        person = record.table[stage.number - 1]
        # a stage names who it's for, though some of them aren't in GOI.EXE's nouns
        name = words.nouns[person] if person < len(words.nouns) else ""
        named = f", seeing {name}" if name else ""
        print(f"stage {stage.number}{named}")
        print("\n".join(stage.lines))
    print()


def main() -> None:
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    folder, command = sys.argv[1], sys.argv[2]
    words = vocabulary(folder)
    files = {"person": "MIRKWOOD.DAT", "task": "SHERWOOD.DAT"}

    if command in ("--check", "--convinced"):
        good = total = 0
        for name in files.values():
            for record in read_records(folder, name):
                mission = Mission(record, words)
                total += len(record.table)
                try:
                    stages = mission.stages()
                except (Bad, IndexError, struct.error) as problem:
                    print(f"{name} {record.number}: {problem}")
                    continue
                good += len(stages)
                if command == "--convinced":
                    for stage in stages:
                        if stage.convinced:
                            print(
                                f"{name} {record.number} ({who(record, words)}), stage {stage.number}:"
                            )
                            print("\n".join(stage.lines))
                            print()
        if command == "--check":
            print(f"{good} of {total} stages read to their ends")
        return

    if command not in files or len(sys.argv) != 4:
        raise SystemExit(__doc__)
    records = read_records(folder, files[command])
    wanted = sys.argv[3]
    if wanted.isdigit() and int(wanted) < len(records):
        show(records[int(wanted)], words)
        return
    matches = [r for r in records if wanted.lower() in who(r, words).lower()]
    if not matches:
        raise SystemExit(f"no mission for {wanted}")
    for record in matches:
        show(record, words)


if __name__ == "__main__":
    main()
