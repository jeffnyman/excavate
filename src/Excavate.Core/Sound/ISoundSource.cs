namespace Excavate.Core.Sound;

/// <summary>A device that makes sound: it adds its next samples into the machine's mix.</summary>
internal interface ISoundSource
{
    /// <summary>
    /// Adds the device's next samples, interleaved left and right at the machine's
    /// <see cref="Machine.SampleRate"/>, into <paramref name="mix"/>, which holds what the other
    /// devices made for the same moments.
    /// </summary>
    void Render(Span<int> mix);
}
