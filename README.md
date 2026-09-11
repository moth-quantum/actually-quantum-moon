# Actually Quantum Moon

An [Outer Wilds](https://www.mobiusdigitalgames.com/outer-wilds.html) mod that paints the
Quantum Moon with the output of real quantum computation.

Four effects driven by quantum computation:

- **Quantum-blurred scout photos.** Any snapshot taken inside the Quantum Moon is blurred
  by a quantum circuit running in-game, with strength set by how far north you are
  standing.
- **Decoherence flashes.** Every few seconds inside the moon, the world briefly glitches.
  Whether it glitches at all is decided by a measurement of $R_y(\theta)|0\rangle$ recorded on quantum
  hardware, one outcome per tick, so the glitch rate follows $\sin²(\theta/2)$: almost never at
  the south pole, almost always at the north. The visual glitch/blur strength works as for 
  the scout photos.
- **A decoherence tone.** The accompanying sound is a wavetable that was encoded into a
  quantum state, run on hardware and read back. Latitude selects which measurement basis
  you hear, so the timbre shifts as you travel.
- **An entanglement film.** An iridescent soap film over the moon, whose colours come from
  an entanglement-shader job on the Moth API. Seen from outside it is the film in
  reflection; standing on the surface and looking up, it is the same film in transmission.

The moon is read as a Bloch sphere throughout: your latitude is the polar angle $\theta$, and
that single number drives the blur and the glitch rate together.

## Configuration

Two settings, in `config.json`:

| key | default | effect |
|---|---|---|
| `glitchToneVolume` | 0.18 | loudness of the decoherence tone |
| `glitchTickSeconds` | 3.0 | how often a measurement outcome is drawn |

Both reload while the game is running. Everything else is fixed, so the effect looks the
same for everyone.

`glitchTickSeconds` stretches both ends of the glitch rate by the same factor; the ratio
between the poles comes from the hardware and does not change.

## How the quantum results get here

Everything under `ActuallyQuantumMoon/Assets/` was produced offline and is shipped as
data: the baked colour ramps for the film, the measured bits that decide each glitch, and
the tone ladder. Nothing contacts a quantum device or an API while you play.

The generation pipeline itself is not part of this repository.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE); both ship with every release.

Two parts of this mod are derived from Apache-2.0 code of ours and carry their own
copyright notices:

- [`ThirdParty/MicroMoth.cs`](ActuallyQuantumMoon/ThirdParty/MicroMoth.cs) is a modified
  copy of [MicroMoth](https://github.com/moth-quantum/MicroMoth), used for the circuits
  and the simulator.
- [`QuantumBlur.cs`](ActuallyQuantumMoon/QuantumBlur.cs) is a C# port of the image-domain
  half of [QuantumBlur](https://github.com/moth-quantum/QuantumBlur).

Each file states its provenance and the changes made to it, in full, in its header, as
Apache-2.0 4(b) and 4(c) require.

## Disclaimer

Actually Quantum Moon is an unofficial, fan-made modification for Outer Wilds. It is not
affiliated with, endorsed by, or sponsored by Mobius Digital or Annapurna Interactive.
"Outer Wilds" and all related marks and assets are the property of their respective
owners. No game assets or assemblies are redistributed with this mod.
