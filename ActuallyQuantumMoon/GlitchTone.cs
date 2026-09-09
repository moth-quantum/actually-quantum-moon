using System;
using System.IO;
using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Plays the decoherence tone that accompanies a glitch.
/// </summary>
/// <remarks>
/// <para>
/// The clips ship in the mod's Assets folder. The game's own ambience is never copied
/// or modified; the tone is layered over it.
/// </para>
/// <para>
/// WAV files are parsed directly rather than through UnityWebRequestMultimedia, which
/// avoids an asynchronous load and a coroutine for files of a few tens of kilobytes,
/// and avoids <c>UnityEngine.AudioType</c>, which is ambiguous with Outer Wilds' own
/// <c>AudioType</c> enum in this project.
/// </para>
/// </remarks>
public static class GlitchTone
{
	/// <summary>
	/// Prefixes of the tone ladders, in the order they are tried: <c>quantum_*</c> is
	/// read back from quantum hardware, <c>fake_*</c> from a local simulator, so a
	/// simulated run cannot silently replace hardware output. If neither is present
	/// there is no tone at all and the flash is silent.
	/// </summary>
	/// <remarks>
	/// A ladder is produced offline. Each clip is the same pair of wavetables read out at
	/// a different measurement angle beta: rung 0 is the
	/// Z basis (layer A alone, the south pole) and the last is the X basis (layer B
	/// modulated by layer A, the north pole). Latitude selects between them.
	/// </remarks>
	private static readonly string[] LadderPrefixes = { "quantum", "fake" };
	private static readonly string[] BetaSuffixes = { "000", "025", "050", "075", "100" };

	/// <summary>
	/// Loudness of the tone relative to the game's own audio, taken from config.json's
	/// <c>glitchToneVolume</c> and editable while the game runs.
	/// </summary>
	/// <remarks>
	/// This value is duplicated in default-config.json and the two must be kept equal.
	/// </remarks>
	public const float DefaultVolume = 0.18f;

	private static float _volume = DefaultVolume;

	private static AudioClip[] _ladder;
	private static AudioSource _source;
	private static bool _loadFailed;

	public static void SetVolume(float volume)
	{
		_volume = Mathf.Clamp01(volume);
	}

	/// <summary>
	/// Cuts the tone off mid-clip. PlayOneShot cannot be cancelled individually, but the
	/// source carries nothing else, so stopping it wholesale is safe. Used when the game
	/// pauses, since AudioSources are not affected by <c>Time.timeScale</c> and a tone
	/// already playing would continue over the pause menu.
	/// </summary>
	public static void Stop()
	{
		if (_source != null) _source.Stop();
	}

	/// <summary>
	/// Drops the cached clips so that the next flash re-reads them from disk. Paired
	/// with OWML's live config reload, this allows regenerated tones to be heard without
	/// restarting the game.
	/// </summary>
	public static void ReloadClip()
	{
		// Stop first: PlayOneShot holds the clip while it sounds, and destroying a clip
		// out from under a playing voice is undefined.
		if (_source != null) _source.Stop();

		if (_ladder != null)
		{
			foreach (AudioClip clip in _ladder)
			{
				if (clip != null) UnityEngine.Object.Destroy(clip);
			}
			_ladder = null;
		}
		_loadFailed = false;
	}

	/// <summary>
	/// Plays the rung of the tone ladder matching the given latitude.
	/// </summary>
	/// <param name="xi">
	/// The same latitude value the visual blur uses: 0 at the south pole, 1 at the
	/// north, so that picture and sound are driven by one number.
	/// </param>
	public static void Play(float xi)
	{
		if (_loadFailed) return;

		// The clips and the source are loaded independently, and must stay that way:
		// ReloadClip drops the clips on every config.json save but leaves the source
		// alive, so if loading the clips were a side effect of building the source, the
		// first save would leave a live source with no ladder and no way to get one.
		if (_ladder == null && !TryLoadClips()) return;

		AudioSource source = EnsureSource();
		if (source == null || _ladder == null || _ladder.Length == 0) return;

		int index = Mathf.Clamp(
			Mathf.RoundToInt(Mathf.Clamp01(xi) * (_ladder.Length - 1)), 0, _ladder.Length - 1);
		AudioClip chosen = _ladder[index];
		if (chosen != null) source.PlayOneShot(chosen, _volume);
	}

	private static AudioSource EnsureSource()
	{
		if (_source != null) return _source;

		// A plain 2D source: spatialBlend 0 means the tone is heard at full volume
		// wherever the player is standing, rather than emanating from a point in space.
		GameObject host = new GameObject("QuantumDecoherenceTone");
		// DontDestroyOnLoad, so the host survives every scene load and is built once per
		// session. Nothing clears _source when a scene loads: that would strand this
		// object, which by definition is never destroyed, and leave a dead AudioSource
		// behind for every reload of the loop.
		UnityEngine.Object.DontDestroyOnLoad(host);
		_source = host.AddComponent<AudioSource>();
		_source.playOnAwake = false;
		_source.loop = false;
		_source.spatialBlend = 0f;
		_source.volume = 1f;

		return _source;
	}

	private static bool TryLoadClips()
	{
		try
		{
			string root = ActuallyQuantumMoon.Instance.ModHelper.Manifest.ModFolderPath;

			foreach (string prefix in LadderPrefixes)
			{
				if (!File.Exists(LadderPath(root, prefix, BetaSuffixes[0]))) continue;

				AudioClip[] ladder = new AudioClip[BetaSuffixes.Length];
				for (int i = 0; i < BetaSuffixes.Length; i++)
				{
					string path = LadderPath(root, prefix, BetaSuffixes[i]);
					if (!File.Exists(path))
					{
						throw new FileNotFoundException($"ladder rung missing: {path}");
					}
					ladder[i] = LoadWav(File.ReadAllBytes(path), $"{prefix}Tone{i}");
				}
				_ladder = ladder;

				string origin = prefix == "quantum"
					? "quantum hardware"
					: "a local simulator";
				ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
					$"[ActuallyQuantumMoon] Tone ladder loaded from '{prefix}_*': " +
					$"{_ladder.Length} rungs, south pole -> north pole, read back from {origin}.",
					MessageType.Success);
				return true;
			}

			// The ladders are the only tones the .csproj packages, so there is nothing to
			// fall back to and the flash goes out silently.
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] No quantum_* or fake_* tone ladder under " +
				$"{root}/Assets - the flash will be silent. The mod's Assets folder is " +
				"incomplete; reinstall it.",
				MessageType.Error);
			_loadFailed = true;
			return false;
		}
		catch (Exception e)
		{
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] Failed to load the glitch tone: {e}", MessageType.Error);
			_loadFailed = true;
			return false;
		}
	}

	private static string LadderPath(string root, string prefix, string beta)
	{
		return Path.Combine(root, $"Assets/{prefix}_tone_beta{beta}.wav");
	}

	/// <summary>
	/// Minimal RIFF/WAVE reader for uncompressed 16-bit PCM, the format of the shipped
	/// clips. Walks the chunk list rather than assuming fixed offsets, since some writers
	/// insert extra chunks before <c>data</c>.
	/// </summary>
	private static AudioClip LoadWav(byte[] bytes, string name)
	{
		if (bytes.Length < 12 ||
			bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
			bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
		{
			throw new InvalidDataException("Not a RIFF/WAVE file.");
		}

		int channels = 0;
		int sampleRate = 0;
		int bitsPerSample = 0;
		int dataOffset = -1;
		int dataLength = 0;

		int pos = 12;
		while (pos + 8 <= bytes.Length)
		{
			string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
			int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
			int body = pos + 8;

			// The size comes straight out of the file and is signed. A negative one would
			// leave pos where it was, or move it backwards, and this loop would never
			// end. Every other fault here throws and is caught by TryLoadClips, costing
			// the player the tone and nothing else; this one would hang the game.
			if (chunkSize < 0)
			{
				throw new InvalidDataException(
					$"Chunk '{chunkId}' declares a negative size ({chunkSize}).");
			}

			if (chunkId == "fmt ")
			{
				channels = BitConverter.ToInt16(bytes, body + 2);
				sampleRate = BitConverter.ToInt32(bytes, body + 4);
				bitsPerSample = BitConverter.ToInt16(bytes, body + 14);
			}
			else if (chunkId == "data")
			{
				dataOffset = body;
				// A header can promise more data than the file holds. Taking it on trust
				// walks off the end of the array down in the sample loop, where the error
				// says nothing about what is actually wrong.
				dataLength = Math.Min(chunkSize, bytes.Length - body);
			}

			// Chunks are word-aligned, so an odd size is followed by a pad byte. In long
			// arithmetic, since body + chunkSize can overflow an int on a corrupt header.
			long next = (long)body + chunkSize + (chunkSize % 2);
			if (next > bytes.Length) break;
			pos = (int)next;
		}

		if (dataOffset < 0) throw new InvalidDataException("No 'data' chunk.");
		if (bitsPerSample != 16) throw new InvalidDataException($"Expected 16-bit PCM, got {bitsPerSample}-bit.");
		if (channels < 1) throw new InvalidDataException("No channels.");

		// Whole frames only. AudioClip.Create takes a per-channel length, so a data chunk
		// ending mid-frame would leave SetData with more samples than the clip can hold.
		int frames = dataLength / 2 / channels;
		if (frames < 1) throw new InvalidDataException("The 'data' chunk holds no samples.");

		int sampleCount = frames * channels;
		float[] samples = new float[sampleCount];
		for (int i = 0; i < sampleCount; i++)
		{
			samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / 32768f;
		}

		AudioClip clip = AudioClip.Create(name, frames, channels, sampleRate, false);
		clip.SetData(samples, 0);
		return clip;
	}
}
