# Notes

How each game was dug into: what it is on disk, what it asks of the PC, and what Excavate does
so that its own program runs as it was shipped.

- [Portal](portal.md): Activision, 1986. A DOS program on a disk that ships without DOS, a
  protection track hidden behind a hidden file, its own text drawn from the BIOS's font,
  artwork made for a composite monitor, and a story of pieces that unlock one another, with a
  [walkthrough](portal-walkthrough.md) worked out from it.
- [The President Is Missing](president.md): Cosmi, 1988. A setup whose crack is undone in
  memory, a supervisor's name checked to all but its last letter, a logon with a way around
  it, photographs on the EGA or the CGA, and a story clock borrowed from the PC's.
- [Guardians of Infinity: To Save Kennedy](kennedy.md): Paragon Software, 1988. A game built on
  the interface library Novell later bought, asking a 1988 PC for a network and a multitasker
  it didn't have; its slide show of IFF pictures; and an ending that runs only on a token the
  game leaves in the BIOS's data area, a conversation with Kennedy that three lines win; and
  its missions, a vocabulary, text packed in nibbles, and the scripts that decide that three
  people must warn Dave Powers; and the week that wins it, played through.
- [Presumed Guilty](guilty.md): Cosmi, 1989, as Celery Software released it in 1993. The
  President's sequel, its setup giving away the passphrase it once asked for, its logon gone,
  a villain cracking that passphrase as the game's clock, an Umbra code and a false one, a
  fast-forward its makers left in, notes they left in its messages, and a byte that goes to a
  port nothing answers.
- [The Omnicron Conspiracy](omnicron.md): First Star Software, 1989. A loader that keeps the
  graphics library and runs the game, a game that runs its music player, a keyboard DOS has to
  read through the game's own INT 16h, and an EGA mode drawn on its second page. Its files:
  pictures packed with byte pairs, 252 rooms in the game's code, and all its text.
- [Countdown](countdown.md): Access Software, 1990. An interactive movie in the VGA's 256
  colors that keeps its own clock from midnight, and RealSound, recorded sound played on the
  PC's speaker by the length of its pulses, which Excavate's speaker had to learn to count.
