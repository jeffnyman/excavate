namespace Excavate.Core.Tests;

/// <summary>
/// <i>Portal</i>'s story, played through from the login to Homer's goodbye by a reader that
/// knows the game's controls and reads its state from memory: which items are unlocked and
/// unread, and the name across the top of the screen. It takes some minutes, so it runs only
/// when EXCAVATE_PLAYTHROUGH is set, and leaves its log of what it read beside the pictures.
/// </summary>
public sealed partial class PortalTests
{
    [Fact]
    public void Its_story_plays_through_to_homers_goodbye()
    {
        if (Environment.GetEnvironmentVariable("EXCAVATE_PLAYTHROUGH") is null || Start() is not { } machine)
        {
            return;
        }

        machine.AnswerDiskPrompt(0x10970, 0x11EFA, 0x2F67, "124");
        machine.AnswerDiskPrompt(0x10970, 0x1236B, 0);
        LogIn(machine);
        var reader = new StoryReader(machine);
        bool ended = reader.Play(rounds: 400);
        File.WriteAllLines(Path.Combine(Shots, "portal-playthrough.log"), reader.Log);

        // the last level, and its last item, Homer's goodbye, read
        Assert.True(ended, "the story didn't end");
        Assert.Equal(5, reader.Level);
        Assert.False(machine.Exited);
    }

    /// <summary>
    /// Reads Portal's story as a player would, from what the game holds in memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game keeps a flag byte for each of a level's items, at 72Ch in its state: the low
    /// nibble is the dataspace it's in (12, Interface, coming through Homer), 20h is set while
    /// it's locked, and 10h while it's unread. The map's place is at 138h in its data, 0 to 11
    /// in the order of the dataspaces' numbers, and the number of the name across the top of
    /// the screen, the choice highlighted, at 13Ch.
    /// </para>
    /// <para>
    /// Each round goes to a dataspace with something unlocked and unread, reads all of it, and
    /// leaves. An item is read when it's paged to NO MORE (as the game's flag at 218h has it)
    /// and left; the first unread one is highlighted as the list comes up. Homer's first
    /// message in a level comes from his menu of kinds of story, as CURRENT. PsiLink's and
    /// Military's demand for a DNA code takes any key.
    /// </para>
    /// </remarks>
    private sealed class StoryReader(Machine machine)
    {
        // the names the game puts across the top, by their numbers
        private static readonly string[] Names =
        [
            "MED10", "PSILINK", "SCITECH", "HISTORY", "MILITARY", "LIFESUPPORT", "GEOGRAPHICAL", "WASATCH", "PSYCHOLOGICAL",
            "CENTRAL PROCESSING", "EDMOD", "HOMER", "INTERFACE", "MED10 GRAPHIC", "PSILINK GRAPHIC", "SCITECH GRAPHIC",
            "HISTORY GRAPHIC", "MILITARY GRAPHIC", "ENTER DATABASE", "EXIT TO INTERFACE", "EXIT THIS LEVEL", "TEXT WINDOW",
            "SELECT ITEM", "CANCEL", "MORE", "NO MORE", "DATA STORAGE", "HOMER MESSAGES",
        ];

        private const int Homer = 11, Items = 0x8C, Goodbye = 0x22;

        private readonly uint _state = (machine.ProgramSegment + 0x584u) << 4;
        private readonly uint _data = (machine.ProgramSegment + 0x28u) << 4;
        private HashSet<int> _seen = [];

        /// <summary>What it did: each round, and each item read, as "READ level item where".</summary>
        public List<string> Log { get; } = [];

        /// <summary>The story's level, 1 to 5.</summary>
        public int Level => machine.Read(_state + 0x213);

        private byte Flag(int item) => machine.Read(_state + 0x72C + (uint)item);

        private bool IsRead(int item) => Flag(item) != 0xFF && (Flag(item) & 0x10) == 0;

        private bool IsOpen(int item) => Flag(item) != 0xFF && (Flag(item) & 0x30) == 0x10;

        private int SpaceOf(int item) => (Flag(item) & 0x0F) == 12 ? Homer : Flag(item) & 0x0F;

        private List<int> OpenIn(int space) => [.. Enumerable.Range(0, Items).Where(i => IsOpen(i) && SpaceOf(i) == space)];

        private string Title()
        {
            int name = machine.Read(_data + 0x13C);
            return name < Names.Length ? Names[name] : $"#{name:X2}";
        }

        private void Key(char key, double seconds = 0)
        {
            Type(machine, key.ToString());
            machine.Run((long)(machine.InstructionsPerSecond * seconds));
        }

        private bool WaitFor(Func<string, bool> wanted, double seconds)
        {
            for (double waited = 0; waited < seconds; waited += 0.5)
            {
                if (wanted(Title()))
                {
                    return true;
                }

                machine.Run(machine.InstructionsPerSecond / 2);
            }

            return false;
        }

        private bool PressUntil(char key, Func<string, bool> wanted, int times)
        {
            for (int n = 0; n < times && !wanted(Title()); n++)
            {
                Key(key, 2);
            }

            return wanted(Title());
        }

        // what's been read since the last look, in the level it was read in
        private void Note(string where, int level)
        {
            if (Level != level)
            {
                return;
            }

            foreach (int item in Enumerable.Range(0, Items).Where(i => IsRead(i) && !_seen.Contains(i)))
            {
                _seen.Add(item);
                Log.Add($"READ {level} {item:X2} {where}");
            }
        }

        /// <summary>Plays until Homer's goodbye has been read, or the rounds run out; whether it was.</summary>
        public bool Play(int rounds)
        {
            int level = Level;
            _seen = [.. Enumerable.Range(0, Items).Where(IsRead)];
            for (int round = 0; round < rounds; round++)
            {
                if (level == 5 && IsRead(Goodbye))
                {
                    return true;
                }

                int at = machine.Read(_data + 0x138);
                var spaces = Enumerable.Range(0, 12).Where(space => OpenIn(space).Count > 0).ToList();
                Log.Add($"round {round}: level {level} at {Names[at % Names.Length]}; open: " +
                    string.Join("; ", spaces.Select(space => $"{Names[space]} {string.Join(' ', OpenIn(space).Select(i => i.ToString("X2")))}")));
                if (spaces.Count == 0)
                {
                    return false;
                }

                int to = spaces.Contains(at) ? at : spaces[0];
                Visit(at, to);
                Note(Names[to], level);
                if (Level != level)
                {
                    level = Level;
                    _seen = [.. Enumerable.Range(0, Items).Where(IsRead)];
                    Log.Add($"LEVEL {level}");
                }
            }

            return level == 5 && IsRead(Goodbye);
        }

        // across the map to a dataspace, everything open there read, and back out to the map
        private void Visit(int at, int to)
        {
            int level = Level;
            for (; at / 4 < to / 4; at += 4)
            {
                Key('v', 4);
            }

            for (; at / 4 > to / 4; at -= 4)
            {
                Key('^', 4);
            }

            for (; at < to; at++)
            {
                Key('>', 4);
            }

            for (; at > to; at--)
            {
                Key('<', 4);
            }

            Key(' ');
            for (int tries = 0; tries < 3 && !WaitFor(t => t is "ENTER DATABASE" or "SELECT ITEM", 12); tries++)
            {
                // the demand for a DNA code, which any key answers
                Key(' ');
            }

            bool current = false;
            for (int guard = 0; guard < 20 && OpenIn(to).Count > 0; guard++)
            {
                var before = OpenIn(to);
                if (current)
                {
                    ChooseCurrent();
                }

                ReadHighlighted();
                Note(Names[to], level);
                if (OpenIn(to).SequenceEqual(before))
                {
                    if (to == Homer && !current)
                    {
                        // his menu of kinds of story, as a level starts
                        current = true;
                        continue;
                    }

                    Log.Add($"  stuck in {Names[to]}");
                    break;
                }

                current = false;
            }

            if (Title() == Names[machine.Read(_data + 0x138) % Names.Length])
            {
                // the game has already gone to the map, as it does after some items
                return;
            }

            // down to the row of buttons, along to the last, and out
            PressUntil('v', t => t is "EXIT THIS LEVEL" or "DATA STORAGE" or "HOMER MESSAGES" or "EXIT TO INTERFACE", 16);
            PressUntil('>', t => t == "EXIT TO INTERFACE", 5);
            Key(' ');
            WaitFor(t => t == Names[to], 30);
            machine.Run(machine.InstructionsPerSecond * 3);
        }

        // the highlighted item opened, paged to its end, and left
        private void ReadHighlighted()
        {
            PressUntil(' ', t => t is "MORE" or "NO MORE", 4);
            machine.Run(machine.InstructionsPerSecond * 2);
            PressUntil('v', t => t == "NO MORE" && machine.Read(_state + 0x218) == 1, 80);
            PressUntil(' ', t => t == "EXIT THIS LEVEL", 3);
            Key(' ');
            WaitFor(t => t is "ENTER DATABASE" or "SELECT ITEM", 20);
        }

        // Homer's menu: into the list, scrolling on, down twelve to CURRENT, chosen, and in
        private void ChooseCurrent()
        {
            PressUntil('^', t => t == "SELECT ITEM", 3);
            Key(' ', 2);
            for (int n = 0; n < 12; n++)
            {
                Key('v', 2);
            }

            Key(' ', 2);
            PressUntil('v', t => t == "ENTER DATABASE", 3);
            Key(' ');
            WaitFor(t => t is "MORE" or "NO MORE", 30);
        }
    }
}
