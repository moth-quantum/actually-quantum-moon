using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Applies <see cref="QuantumBlur"/> to an image larger than the grid the circuit runs
/// on, by treating the quantum step's output as a correction to the full-resolution
/// image rather than as the image itself.
/// </summary>
/// <remarks>
/// <para>
/// The statevector holds one amplitude per pixel and a rotation is applied per qubit, so
/// the circuit's cost grows as size^2 * log(size) and cannot be run at the native
/// resolution of a photo: 512 costs about 65 ms against 2 ms for 128. Running it at 128
/// and stretching the result back over the photo is affordable, but it resamples the
/// image twice, and that softens every photo - including at the south pole, where the
/// quantum step is exactly the identity and the picture should come back untouched.
/// </para>
/// <para>
/// The fix is to resample the blur's <em>effect</em> instead of the image. Each grid cell
/// yields a difference, blurred minus unblurred, which is added to every native pixel the
/// cell covers. Since bilinear interpolation is linear, this is equivalent to the blurred
/// grid plus the fine detail the downsample discarded, so nothing is invented: the detail
/// is measured from the photo that is already in hand. It works because the two halves
/// have opposite frequency content - the image carries detail as fine as a single pixel,
/// which no downsample survives, while a blur is by nature a broad redistribution that
/// resamples cleanly.
/// </para>
/// <para>
/// The correction is held constant across each cell rather than interpolated. That is
/// several times cheaper, and at the strengths where the block edges would show, the
/// output is already resolving into hard-edged blocks of its own (see the note on
/// <see cref="DetailFadeFor"/>).
/// </para>
/// <para>
/// At <c>xi = 0</c> the rotation is exactly the identity, so the difference is exactly
/// zero and the image passes through pixel for pixel. That falls out of the structure
/// rather than from a special case, so there is no discontinuity at the south pole.
/// </para>
/// </remarks>
public static class QuantumBlurTransfer
{
	/// <summary>
	/// Edge of the square grid the circuit runs on: 2 * log2(GridSize) qubits, so 14 at
	/// 128. Independent of the size of the image being blurred.
	/// </summary>
	public const int GridSize = 128;

	/// <summary>
	/// Blurs <paramref name="pixels"/> in place at its own resolution, running the
	/// circuit on a <see cref="GridSize"/> grid sampled from it.
	/// </summary>
	/// <param name="pixels">Row-major pixels, <c>index = y * width + x</c>.</param>
	/// <param name="xi">
	/// Blur strength, 0 at the south pole and 1 at the north. See
	/// <see cref="QuantumBlur.Apply"/>.
	/// </param>
	public static void Apply(Color32[] pixels, int width, int height, float xi)
	{
		// Below the grid resolution there is nothing to preserve, so the circuit can run
		// on the image directly.
		if (width <= GridSize || height <= GridSize)
		{
			if (width == height) QuantumBlur.Apply(pixels, width, xi);
			return;
		}

		Color32[] grid = SampleGrid(pixels, width, height);
		Color32[] blurred = (Color32[])grid.Clone();
		QuantumBlur.Apply(blurred, GridSize, xi);

		float detailFade = DetailFadeFor(xi);

		for (int gy = 0; gy < GridSize; gy++)
		{
			// Cell bounds are computed from the image size rather than a fixed factor, so
			// a native resolution that is not a multiple of the grid still tiles exactly.
			int y0 = gy * height / GridSize;
			int y1 = (gy + 1) * height / GridSize;

			for (int gx = 0; gx < GridSize; gx++)
			{
				int x0 = gx * width / GridSize;
				int x1 = (gx + 1) * width / GridSize;

				int cell = gy * GridSize + gx;
				Color32 before = grid[cell];
				Color32 after = blurred[cell];

				// Two readings of the same cell, mixed by detailFade: the difference
				// added to the native pixel, which keeps the photo's detail, and the
				// blurred value itself, which does not.
				int deltaR = after.r - before.r;
				int deltaG = after.g - before.g;
				int deltaB = after.b - before.b;

				for (int y = y0; y < y1; y++)
				{
					int row = y * width;
					for (int x = x0; x < x1; x++)
					{
						int i = row + x;
						Color32 source = pixels[i];
						pixels[i] = new Color32(
							Mix(source.r, deltaR, after.r, detailFade),
							Mix(source.g, deltaG, after.g, detailFade),
							Mix(source.b, deltaB, after.b, detailFade),
							source.a);
					}
				}
			}
		}
	}

	/// <summary>
	/// How much of the native detail survives, as a function of blur strength.
	/// </summary>
	/// <remarks>
	/// A difference added to the image can redistribute light but can never destroy
	/// detail, so on its own it leaves the photo's sharp edges intact however hard the
	/// blur is driven - at the north pole the moon keeps a crisp outline sitting on top
	/// of a heavy blur, which reads as a fault rather than as an effect. Fading the
	/// detail out as xi rises ends at exactly the behaviour of a plain blurred upscale,
	/// so the north pole is unchanged by all of this, and the south and middle - where
	/// the photo is meant to stay legible - keep their detail.
	/// </remarks>
	private static float DetailFadeFor(float xi) => Mathf.Clamp01(xi);

	/// <summary>
	/// Blends the detail-preserving reading of a pixel with the plain blurred one.
	/// </summary>
	private static byte Mix(byte source, int delta, byte blurred, float fade)
	{
		float detail = source + delta;
		float value = detail + (blurred - detail) * fade;
		return value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(value + 0.5f);
	}

	/// <summary>
	/// Builds the grid the circuit runs on, averaging each cell rather than sampling one
	/// pixel from it. Averaging costs a handful of additions per cell and is what stops
	/// fine detail from aliasing: with a single sample, a star that misses the sample
	/// points never enters the circuit at all, while one that hits them is spread across
	/// the whole cell.
	/// </summary>
	private static Color32[] SampleGrid(Color32[] pixels, int width, int height)
	{
		Color32[] grid = new Color32[GridSize * GridSize];

		for (int gy = 0; gy < GridSize; gy++)
		{
			int y0 = gy * height / GridSize;
			int y1 = (gy + 1) * height / GridSize;

			for (int gx = 0; gx < GridSize; gx++)
			{
				int x0 = gx * width / GridSize;
				int x1 = (gx + 1) * width / GridSize;

				int sumR = 0, sumG = 0, sumB = 0, count = 0;
				for (int y = y0; y < y1; y++)
				{
					int row = y * width;
					for (int x = x0; x < x1; x++)
					{
						Color32 px = pixels[row + x];
						sumR += px.r;
						sumG += px.g;
						sumB += px.b;
						count++;
					}
				}

				if (count == 0) count = 1;
				grid[gy * GridSize + gx] = new Color32(
					(byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count), 255);
			}
		}

		return grid;
	}
}
