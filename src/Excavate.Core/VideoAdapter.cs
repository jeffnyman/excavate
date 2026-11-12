namespace Excavate.Core;

/// <summary>The display adapter a machine is built with, as the game was written for.</summary>
public enum VideoAdapter
{
    /// <summary>
    /// A VGA, for the games of 1987 on: its 256-color mode and the planar modes known as Mode X,
    /// and, as it kept them, the EGA's 16-color modes and the CGA's.
    /// </summary>
    Vga,

    /// <summary>
    /// IBM's Color Graphics Adapter of 1981: 320 by 200 in four colors and 640 by 200 in two,
    /// on an RGB monitor or as the colors a composite monitor made of them.
    /// </summary>
    Cga,
}
