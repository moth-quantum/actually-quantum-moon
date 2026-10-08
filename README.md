![Actually Quantum Moon](https://raw.githubusercontent.com/moth-quantum/actually-quantum-moon/main/thumbnail.jpg)

# Actually Quantum Moon

Spoilers within!

Actually Quantum Moon is an [Outer Wilds](https://www.mobiusdigitalgames.com/outer-wilds.html)
mod that makes the Quantum Moon a bit more quantum, using results from real quantum
computers.

## What it does

Once you're on the Quantum Moon you'll notice four things.

### Blurry scout photos

Photos you take with the scout on the moon get blurred by a quantum circuit that runs
in-game. The further north you are, the blurrier they get.

### Glitches

Every few seconds the world might glitch for a moment. Whether it does is decided by a
measurement result from real quantum hardware. Near the south pole it almost never
happens, near the north pole it almost always does. The further north you are, the
stronger the glitch, same as with the photos.

### A strange hum

The glitches come with a sound. We encoded a wavetable into a quantum state, ran it on
hardware and read it back. Your latitude picks which measurement basis you hear, so it
sounds different as you move around.

### A soap film

There's a rainbow-coloured soap film around the moon. Its colours come from an
entanglement shader we ran on the Moth API. From space you see light reflecting off it,
and from the surface you see light coming through it.

### For the physicists

We treat the moon as a Bloch sphere. Your latitude is the polar angle θ, counted from the
south pole. Each glitch is a measurement of R<sub>y</sub>(θ)|0⟩, so it happens with
probability sin²(θ/2). The same θ also sets how strong the blur is.

## Installation

1. Install the [Outer Wilds Mod Manager](https://outerwildsmods.com/)
2. Find Actually Quantum Moon in the mod list and install it

If you'd rather do it by hand, grab the latest release and unzip it into OWML's `Mods`
folder.

## Settings

There are only two. You can change them in the in-game mod settings (or in `config.json`),
even while playing:

- `glitchToneVolume` (default 0.18) - how loud the hum is
- `glitchTickSeconds` (default 3) - how often the mod checks whether to glitch

Changing `glitchTickSeconds` makes glitches more or less frequent everywhere, but the north
pole will always glitch a lot more than the south pole. That part comes from the hardware.

Everything else is fixed on purpose, so everyone gets the same moon.

## Do I need a quantum computer, or internet?

Nope. All the hardware runs were done ahead of time, and the results ship with the mod in
the `Assets` folder: the film colours, the measured bits for the glitches, and the sound.
The photo blur is the only thing worked out live, and it runs on a small simulator inside
the mod. Nothing connects to anything while you play.

The scripts we used to make those files aren't in this repo.

## License and legal stuff

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Two files are based on other open-source projects of ours (also Apache-2.0):

- [`MicroMoth.cs`](ActuallyQuantumMoon/ThirdParty/MicroMoth.cs) is a modified copy of
  [MicroMoth](https://github.com/moth-quantum/MicroMoth), which we use to build and
  simulate the circuits.
- [`QuantumBlur.cs`](ActuallyQuantumMoon/QuantumBlur.cs) is a C# port of the image part of
  [QuantumBlur](https://github.com/moth-quantum/QuantumBlur).

The top of each file says where it came from and what we changed.

This is an unofficial fan mod. It isn't affiliated with or endorsed by Mobius Digital or
Annapurna Interactive, and Outer Wilds and everything related to it belong to their
owners. The mod doesn't include any game files.
