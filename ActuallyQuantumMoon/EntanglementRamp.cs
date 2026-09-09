using System.IO;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Loads the baked entanglement colour table and uploads it to the GPU as a texture.
/// </summary>
/// <remarks>
/// <para>
/// The colours are computed offline and stored as a flat table of RGB values; no optics
/// or quantum simulation happens here.
/// </para>
/// <para>
/// The table has two axes. U is <c>cos(theta)</c>, the viewing angle. V is the film's
/// interlayer spacing, sampled at a range of thicknesses. Spacing enters the underlying
/// maths only at lookup time, so every row is the same quantum result resampled and the
/// second axis costs no extra computation.
/// </para>
/// <para>
/// The two axes drive different parts of the effect: U alone depends only on the view
/// direction, which on a sphere presents the same set of normals from every angle. V is
/// what makes the pattern move, driven by camera distance in
/// <see cref="EntanglementMoonShell"/> and by position on the shell in
/// <see cref="EntanglementCap"/>.
/// </para>
/// <para>
/// Stored as float32 rather than an 8-bit image because the whole signal is a smooth
/// colour difference of roughly 30% across the table, which quantises into visible
/// banding.
/// </para>
/// </remarks>
internal sealed class EntanglementRamp
{
	private const int BytesPerColour = 12; // 3 channels * float32

	/// <summary>
	/// Header layout: magic, version, columns, rows, and the thickness of the first and
	/// last row.
	/// </summary>
	private const int HeaderBytes = 24;
	private const uint Magic = 0x32525445; // "ETR2" little-endian
	private const uint SupportedVersion = 2;

	public Texture2D Texture { get; private set; }

	/// <summary>Colours per row, along cos(theta).</summary>
	public int Colours { get; private set; }

	/// <summary>Thickness levels, along V.</summary>
	public int Rows { get; private set; }

	/// <summary>
	/// Interlayer spacing of the first and last row, in nanometres. Not used for
	/// rendering, which addresses rows directly, but logged so the console reports what
	/// the shell is sampling.
	/// </summary>
	public float ThicknessMinNm { get; private set; }
	public float ThicknessMaxNm { get; private set; }

	/// <summary>
	/// Half a texel on the V axis. Sampling at <c>VInset</c> lands on the centre of row
	/// 0 and at <c>1 - VInset</c> on the centre of the last row, so a caller holding a
	/// thickness fraction in [0,1] should map it through
	/// <c>Mathf.Lerp(VInset, 1 - VInset, fraction)</c>. Using the fraction raw clamps
	/// half a row of range against each end.
	/// </summary>
	public float VInset => Rows > 0 ? 0.5f / Rows : 0.5f;

	public static EntanglementRamp Load(string path)
	{
		byte[] blob = File.ReadAllBytes(path);
		string name = Path.GetFileName(path);

		if (blob.Length < HeaderBytes)
		{
			throw new InvalidDataException(
				$"{name} is {blob.Length} bytes, too short to hold even the {HeaderBytes}-byte " +
				"header. The mod's Assets folder is incomplete; reinstall it.");
		}

		using var reader = new BinaryReader(new MemoryStream(blob));

		// The version 1 format was headerless, so an older asset would otherwise be read
		// as a table of arbitrary shape and rendered as noise.
		uint magic = reader.ReadUInt32();
		if (magic != Magic)
		{
			throw new InvalidDataException(
				$"{name} does not start with the 'ETR2' marker. This is almost certainly " +
				"the older one-dimensional ramp, which carried no header. Reinstall the " +
				"mod to get a ramp in the current format.");
		}

		uint version = reader.ReadUInt32();
		if (version != SupportedVersion)
		{
			throw new InvalidDataException(
				$"{name} is format version {version}, and this build reads version " +
				$"{SupportedVersion}. Reinstall the mod so that the assembly and the assets " +
				"match.");
		}

		int cols = (int)reader.ReadUInt32();
		int rows = (int)reader.ReadUInt32();
		float thicknessMin = reader.ReadSingle();
		float thicknessMax = reader.ReadSingle();

		if (cols <= 0 || rows <= 0)
		{
			throw new InvalidDataException(
				$"{name} declares a {cols}x{rows} table, which is empty. The asset is " +
				"corrupt; reinstall the mod.");
		}

		long expected = (long)cols * rows * BytesPerColour + HeaderBytes;
		if (blob.Length != expected)
		{
			throw new InvalidDataException(
				$"{name} declares {rows} rows of {cols} colours, which needs {expected} " +
				$"bytes, but the file is {blob.Length}. It is truncated or the header is " +
				"wrong; reinstall the mod.");
		}

		var pixels = new Color[cols * rows];
		for (int i = 0; i < pixels.Length; i++)
		{
			// BinaryReader is little-endian on every platform, matching the "<f4" the
			// packer writes. Alpha is 1 because the borrowed shaders blend on alpha;
			// opacity is set once on the material instead.
			pixels[i] = new Color(
				reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 1f);
		}

		// Linear, not sRGB: these are physical reflectance ratios rather than colours
		// chosen by eye, so no transfer curve should be applied to them.
		var texture = new Texture2D(cols, rows, TextureFormat.RGBAFloat, false, true)
		{
			name = "EntanglementRamp",
			// Both axes are intervals rather than cycles - U is cos(theta) over [0,1],
			// V is a thickness range - so repeating either would blend the two ends
			// together and seam the busiest row against the calmest.
			wrapMode = TextureWrapMode.Clamp,
			// Interpolates between thickness rows per pixel; nothing blends the table
			// by hand.
			filterMode = FilterMode.Bilinear,
			anisoLevel = 0,
		};
		// Row 0 of the file is the thinnest film and SetPixels fills from the bottom
		// left, so the thin end lands at V = 0, matching the order the baker writes.
		texture.SetPixels(pixels);
		// No mip chain: mips average across the colour bands and grey them out.
		texture.Apply(false, true);

		return new EntanglementRamp
		{
			Texture = texture,
			Colours = cols,
			Rows = rows,
			ThicknessMinNm = thicknessMin,
			ThicknessMaxNm = thicknessMax,
		};
	}
}
