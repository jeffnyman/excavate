using System.Security.Cryptography;

namespace Excavate.Core.Tests;

/// <summary>
/// Saved states. A state loaded into a fresh machine, or back into the machine it came from after
/// that has run on, must carry on exactly as the machine did from the moment it was saved: the
/// same sound, sample for sample, and the same processor, memory and picture at the end. Anything
/// a device keeps that the state missed shows up as the runs drifting apart.
/// </summary>
public sealed class StateTests : IDisposable
{
    private readonly string _saves = Directory.CreateTempSubdirectory("excavate-saves").FullName;

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    [Fact]
    public void Something_else_is_not_a_state()
    {
        var machine = new Machine(_saves, _saves);
        Assert.Throws<InvalidDataException>(() => machine.LoadState(new MemoryStream([0, 1, 2, 3, 4, 5])));
        Assert.Throws<InvalidDataException>(() => machine.LoadState(new MemoryStream()));

        // a state cut short
        var state = new MemoryStream();
        machine.SaveState(state);
        var cut = new MemoryStream(state.ToArray()[..(int)(state.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => machine.LoadState(cut));

        // and a whole one, into a machine as it was made
        state.Position = 0;
        new Machine(_saves, _saves).LoadState(state);
    }

    [Fact]
    public void A_state_with_an_mt32_needs_one_from_the_same_roms()
    {
        if (TestData.Mt32Roms() is not { } roms)
        {
            return;
        }

        var with = new Machine(_saves, _saves);
        with.UseMt32(roms);
        var without = new Machine(_saves, _saves);

        var state = new MemoryStream();
        with.SaveState(state);
        state.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => without.LoadState(state));
        Assert.Contains("no MT-32", ex.Message);

        state = new MemoryStream();
        without.SaveState(state);
        state.Position = 0;
        ex = Assert.Throws<InvalidDataException>(() => with.LoadState(state));
        Assert.Contains("saved with no MT-32", ex.Message);
    }

    private static void Run(Machine machine, double until)
    {
        var buffer = new short[Machine.SampleRate * 2];
        while (machine.Time.TotalSeconds < until && machine.Run(machine.InstructionsPerSecond / 10))
        {
            machine.ReadSamples(buffer);
        }

        Assert.True(machine.Time.TotalSeconds >= until, $"the program stopped at {machine.Time.TotalSeconds:F1} s");
    }

    /// <summary>
    /// Saves the machine, and runs on for the seconds given three ways: the machine itself; a
    /// fresh one with the state loaded; and the machine itself again, with the state loaded back
    /// over where it had got to. All three must make the same sound and end the same.
    /// </summary>
    private static void AssertCarriesOn(Machine original, Func<Machine> make, double seconds, double loud)
    {
        var state = new MemoryStream();
        original.SaveState(state);
        var buffer = new short[Machine.SampleRate * 2];
        original.ReadSamples(buffer);

        var first = RunOn(original, seconds);
        double rms = Math.Sqrt(first.Sound.Average(sample => (double)sample * sample));
        Assert.True(rms > loud, $"nearly silent after the save: {rms:F0}");

        var fresh = make();
        state.Position = 0;
        fresh.LoadState(state);
        AssertSame(first, RunOn(fresh, seconds), "a fresh machine");

        state.Position = 0;
        original.LoadState(state);
        AssertSame(first, RunOn(original, seconds), "the same machine, loaded back");
    }

    private sealed record Run_(List<short> Sound, string Processor, byte[] Memory, byte[] Picture);

    private static Run_ RunOn(Machine machine, double seconds)
    {
        var sound = new List<short>();
        var buffer = new short[Machine.SampleRate * 2];
        double end = machine.Time.TotalSeconds + seconds;
        while (machine.Time.TotalSeconds < end && machine.Run(machine.InstructionsPerSecond / 10))
        {
            sound.AddRange(buffer.AsSpan(0, machine.ReadSamples(buffer)));
        }

        var cpu = machine.Cpu;
        string processor = $"{cpu.Cs:X4}:{cpu.Eip:X8} ax {cpu.Eax:X8} bx {cpu.Ebx:X8} cx {cpu.Ecx:X8} dx {cpu.Edx:X8} " +
            $"si {cpu.Esi:X8} di {cpu.Edi:X8} bp {cpu.Ebp:X8} sp {cpu.Ss:X4}:{cpu.Esp:X8} ds {cpu.Ds:X4} es {cpu.Es:X4} " +
            $"flags {cpu.Eflags:X8} after {cpu.Instructions} instructions, {machine.Time.TotalSeconds:F3} s";

        var memory = new byte[0x11_0000];
        for (uint address = 0; address < memory.Length; address++)
        {
            memory[address] = machine.Read(address);
        }

        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        byte[] picture = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, picture, 0, picture.Length);
        return new Run_(sound, processor, memory, picture);
    }

    private static void AssertSame(Run_ expected, Run_ actual, string what)
    {
        Assert.Equal(expected.Sound.Count, actual.Sound.Count);
        int differ = Enumerable.Range(0, expected.Sound.Count).FirstOrDefault(i => expected.Sound[i] != actual.Sound[i], -1);
        Assert.True(differ < 0, $"{what}: the sound differs from sample {differ / 2}, {differ / 2.0 / Machine.SampleRate:F3} s in");
        Assert.Equal(expected.Processor, actual.Processor);
        int at = Enumerable.Range(0, expected.Memory.Length).FirstOrDefault(i => expected.Memory[i] != actual.Memory[i], -1);
        Assert.True(at < 0, $"{what}: memory differs at {at:X5}");
        Assert.Equal(SHA256.HashData(expected.Picture), SHA256.HashData(actual.Picture));
    }
}
