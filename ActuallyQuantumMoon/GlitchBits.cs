using System;
using System.Collections.Generic;
using System.IO;
using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Supplies the pre-recorded quantum measurement outcomes that decide whether the world
/// decoheres on any given tick.
/// </summary>
/// <remarks>
/// <para>
/// The moon is divided offline into equal bands of theta. The single-qubit state at each
/// band's midpoint - <c>Ry(theta)|0></c>, the moon read as a Bloch sphere - is measured a
/// few thousand times and the raw outcomes stored. They are
/// consumed here one bit per tick: a 1 fires a flash, a 0 does not. The probability of a
/// glitch is therefore <c>sin^2(theta/2)</c> by construction, near zero at the south pole
/// and near one at the north, with no curve fitted to it.
/// </para>
/// <para>
/// Two sets can coexist, tried in the same order as <see cref="GlitchTone"/>:
/// <c>quantum_glitch_bits_s*.txt</c> from quantum hardware, then
/// <c>fake_glitch_bits_s*.txt</c> from a local simulator.
/// </para>
/// <para>
/// Every band is reshuffled whenever the player enters the moon, so that no two visits
/// glitch on the same beats. A shuffle is a permutation, so each band still holds exactly
/// the ones and zeros the device returned in the same proportion, and the glitch rate at
/// every latitude remains the measured one including readout bias. What is given up is
/// the shot ordering, and with it any correlation between consecutive shots.
/// </para>
/// </remarks>
internal static class GlitchBits
{
	private static readonly string[] LadderPrefixes = { "quantum", "fake" };

	// One entry per band, south pole first, holding only '0' and '1'. Shuffled in place:
	// a permutation of a permutation is still a permutation of what the device returned,
	// so no pristine copy is needed.
	private static char[][] _bands;
	private static int[] _cursors;
	private static bool _loadFailed;

	// Device that produced the loaded set, for logging. "aer" for a simulated set.
	private static string _backend = "unknown";

	/// <summary>True once a set of bits has been loaded.</summary>
	public static bool IsLoaded => _bands != null && _bands.Length > 0;

	/// <summary>
	/// Whether measurement outcomes can be drawn at all, loading them on first use.
	/// Callers need this before drawing anything, to know whether they are on the fixed
	/// tick that accompanies drawn bits or on the latitude-interpolated fallback.
	/// </summary>
	public static bool IsAvailable => IsLoaded || TryLoad();

	/// <summary>
	/// Number of bands in the loaded set. Band k covers xi in <c>[k, k+1] / BandCount</c>.
	/// </summary>
	public static int BandCount => _bands == null ? 0 : _bands.Length;

	/// <summary>Which quantum computer the loaded bits came from.</summary>
	public static string Backend => _backend;

	public static void OnSceneLoaded()
	{
		// The bits are scene-independent, but a new world gets a new order. The same
		// happens again on every arrival at the moon; see OnEnteredMoon.
		if (IsLoaded) Reshuffle();
	}

	/// <summary>
	/// Called when the player arrives inside the moon. Reshuffles every band so that a
	/// visit never replays the beats of the last one.
	/// </summary>
	public static void OnEnteredMoon()
	{
		if (IsLoaded) Reshuffle();
	}

	/// <summary>
	/// Drops the cached streams so that the next draw re-reads them from disk, letting a
	/// fresh hardware run be picked up by touching config.json rather than restarting the
	/// game.
	/// </summary>
	public static void Reload()
	{
		_bands = null;
		_cursors = null;
		_backend = "unknown";
		_loadFailed = false;
	}

	/// <summary>
	/// Returns the band a latitude falls in, where <paramref name="xi"/> is theta/pi from
	/// <see cref="MoonLatitude"/>. The bands are equal, so this floors rather than
	/// rounding to the nearest, unlike <see cref="GlitchTone"/> whose rungs sit at the
	/// ends and midpoint.
	/// </summary>
	public static int BandFor(float xi)
	{
		if (!IsLoaded) return -1;
		return Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(xi) * _bands.Length), 0, _bands.Length - 1);
	}

	/// <summary>
	/// Takes the next measurement outcome for the band at <paramref name="xi"/>.
	/// </summary>
	/// <returns>
	/// False only if there are no bits to draw from, which the caller must treat as
	/// "decide another way" rather than as "no glitch".
	/// </returns>
	public static bool TryDraw(float xi, out bool glitch, out int band)
	{
		glitch = false;
		band = -1;

		if (!IsAvailable) return false;

		band = BandFor(xi);
		char[] stream = _bands[band];
		if (stream == null || stream.Length == 0) return false;

		// Wrap rather than stop. At the default 4096 shots and one draw every three
		// seconds, exhausting a band takes over three hours spent within it.
		int cursor = _cursors[band] % stream.Length;
		_cursors[band] = cursor + 1;
		glitch = stream[cursor] == '1';
		return true;
	}

	/// <summary>
	/// Fisher-Yates shuffle of each band, resetting every cursor to the start.
	/// </summary>
	private static void Reshuffle()
	{
		for (int b = 0; b < _bands.Length; b++)
		{
			char[] stream = _bands[b];
			for (int i = stream.Length - 1; i > 0; i--)
			{
				// Random.Range is exclusive on the int upper bound, so this is the
				// inclusive [0, i] the shuffle needs.
				int j = UnityEngine.Random.Range(0, i + 1);
				char swap = stream[i];
				stream[i] = stream[j];
				stream[j] = swap;
			}
			_cursors[b] = 0;
		}
	}

	private static bool TryLoad()
	{
		if (_loadFailed) return false;

		try
		{
			string root = ActuallyQuantumMoon.Instance.ModHelper.Manifest.ModFolderPath;

			foreach (string prefix in LadderPrefixes)
			{
				// The band count is discovered rather than assumed, so re-running the
				// tool with --bands N needs no code change: walk s0, s1, ... until one is
				// missing. A set with a gap reads as a shorter set, and every band present
				// still maps to the latitude it was measured at, because BandFor divides
				// by the number found.
				List<char[]> found = new List<char[]>();
				string backend = "unknown";
				for (int k = 0; ; k++)
				{
					string path = BandPath(root, prefix, k);
					if (!File.Exists(path)) break;
					string text = File.ReadAllText(path);
					found.Add(ParseBits(text, path));
					if (k == 0) backend = ParseBackend(text);
				}

				if (found.Count == 0) continue;

				_bands = found.ToArray();
				_cursors = new int[_bands.Length];
				_backend = backend;
				Reshuffle();

				int total = 0;
				string rates = "";
				for (int k = 0; k < _bands.Length; k++)
				{
					total += _bands[k].Length;
					rates += $" {Ones(_bands[k]) / (float)Mathf.Max(1, _bands[k].Length):F3}";
				}

				string origin = prefix == "quantum"
					? $"quantum hardware: {_backend}"
					: $"a local simulator ({_backend})";
				ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
					$"[ActuallyQuantumMoon] Glitch bits loaded from '{prefix}_glitch_bits_s*': " +
					$"{_bands.Length} bands, {total} measurements from {origin}. " +
					$"P(1) south -> north:{rates}",
					MessageType.Success);
				return true;
			}

			// Not an error: the flash still happens, on the latitude-interpolated timer
			// in QuantumDecoherenceFlash.
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] No glitch bits found under {root}/Assets; the flash " +
				"will use its own interval instead of measurement outcomes.",
				MessageType.Warning);
			_loadFailed = true;
			return false;
		}
		catch (Exception e)
		{
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] Failed to load the glitch bits: {e}", MessageType.Error);
			_loadFailed = true;
			return false;
		}
	}

	private static string BandPath(string root, string prefix, int band)
	{
		return Path.Combine(root, $"Assets/{prefix}_glitch_bits_s{band}.txt");
	}

	/// <summary>
	/// Extracts the device name from the file header, which has the form
	/// <c># Ry(theta)|0> on ibm_kingston at 2026-09-04T...</c>. Best-effort: a header that
	/// does not parse costs a log line its detail and nothing more, so this never throws.
	/// </summary>
	private static string ParseBackend(string text)
	{
		const string marker = "|0> on ";
		int start = text.IndexOf(marker, StringComparison.Ordinal);
		if (start < 0) return "unknown";
		start += marker.Length;

		int end = text.IndexOf(" at ", start, StringComparison.Ordinal);
		if (end < 0) end = text.IndexOf('\n', start);
		if (end < 0 || end <= start) return "unknown";

		return text.Substring(start, end - start).Trim();
	}

	/// <summary>
	/// Keeps '0' and '1' and drops everything else: '#' header lines, newlines and any
	/// byte-order mark. The header makes the shipped file self-describing, so it stays in
	/// the file and is ignored here.
	/// </summary>
	private static char[] ParseBits(string text, string path)
	{
		List<char> bits = new List<char>(text.Length);
		foreach (string line in text.Split('\n'))
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed[0] == '#') continue;
			foreach (char c in trimmed)
			{
				if (c == '0' || c == '1') bits.Add(c);
			}
		}

		if (bits.Count == 0) throw new InvalidDataException($"no measurement bits in {path}");
		return bits.ToArray();
	}

	private static int Ones(char[] bits)
	{
		int n = 0;
		foreach (char c in bits)
		{
			if (c == '1') n++;
		}
		return n;
	}
}
