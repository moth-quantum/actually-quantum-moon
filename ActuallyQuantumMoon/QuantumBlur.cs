using System;
using System.Collections.Generic;
using System.Numerics;
using Moth.MicroMoth;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// A C# implementation of the image-domain half of
/// <see href="https://github.com/moth-quantum/QuantumBlur">moth-quantum/QuantumBlur</see>,
/// built on the MicroMoth library vendored in <c>ThirdParty/MicroMoth.cs</c> for the
/// quantum circuit and simulator.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm encodes pixel brightnesses as the amplitudes of a quantum state, one
/// amplitude per pixel, arranged on the grid by a Gray code so that neighbouring pixels
/// occupy neighbouring basis states. An RX rotation is applied to every qubit and the
/// brightness is read back from the resulting probabilities. Because neighbouring pixels
/// differ by a single qubit, rotating a qubit mixes a pixel's amplitude with those of its
/// neighbours, and that mixing produces the blur.
/// </para>
/// <para>
/// The Gray-code grid, the per-qubit rate and the pixel encode and decode are
/// QuantumBlur's own logic; MicroMoth supplies only <c>QuantumCircuit.Init</c>,
/// <c>Rx</c> and <c>Simulator.Statevector</c>, which returns the exact statevector rather
/// than sampled shots.
/// </para>
/// <para>
/// This is a local implementation rather than a reference to the Moth.QuantumBlur
/// library because that library works in string-keyed dictionaries, costing roughly
/// 117 ms for a 256x256 RGB image. It must therefore be given a heavily downscaled
/// image, and the resulting upscale applies a fixed blur before any quantum effect,
/// which masks the latitude-driven blur this mod depends on. This implementation returns
/// its input unchanged at <c>xi = 0</c>, where <c>RX(0)</c> is exactly the identity.
/// </para>
/// </remarks>
public static class QuantumBlur
{
	/// <summary>
	/// Precomputed grid layout for one image size. Depends only on the size, so it is
	/// built once and reused. Amplitudes are addressed by basis index throughout, which
	/// keeps the inner loops flat.
	/// </summary>
	private sealed class Grid
	{
		public int Size;
		public int Qubits;
		public int Dim;
		public int[] XyToIndex;      // [y * Size + x] -> basis index
		public int[] XNeighborQubit; // qubit flipped between column x and x+1
		public int[] YNeighborQubit; // qubit flipped between row y and y+1
	}

	// Keyed by size: the scout photo and the decoherence flash run at different
	// resolutions, and a single-entry cache would rebuild the grid whenever they alternate.
	private static readonly Dictionary<int, Grid> CachedGrids = new Dictionary<int, Grid>();

	/// <summary>
	/// Builds a reflected Gray code of length 2^ceil(log2(length)), so that consecutive
	/// entries differ by exactly one bit. Equivalent to <c>quantumblur.make_line</c>.
	/// </summary>
	private static int[] MakeLine(int length, out int nBits)
	{
		int n = Math.Max(1, (int)Math.Ceiling(Math.Log(length, 2)));
		string[] line = { "0", "1" };
		for (int j = 0; j < n - 1; j++)
		{
			string[] grown = new string[line.Length * 2];
			for (int k = 0; k < line.Length; k++)
			{
				grown[k] = line[k] + "0";
				grown[grown.Length - 1 - k] = line[k] + "1";
			}
			line = grown;
		}

		int[] codes = new int[line.Length];
		for (int i = 0; i < line.Length; i++) codes[i] = Convert.ToInt32(line[i], 2);
		nBits = line[0].Length;
		return codes;
	}

	/// <summary>Index of the single set bit in a power-of-two value.</summary>
	private static int BitIndex(int singleBit)
	{
		int i = 0;
		while ((singleBit >>= 1) != 0) i++;
		return i;
	}

	private static Grid GetGrid(int size)
	{
		if (CachedGrids.TryGetValue(size, out Grid cached)) return cached;

		int[] lineX = MakeLine(size, out int nx);
		int[] lineY = MakeLine(size, out int ny);

		Grid grid = new Grid
		{
			Size = size,
			Qubits = nx + ny,
			Dim = 1 << (nx + ny),
			XyToIndex = new int[size * size],
			XNeighborQubit = new int[Math.Max(1, size - 1)],
			YNeighborQubit = new int[Math.Max(1, size - 1)],
		};

		for (int y = 0; y < size; y++)
			for (int x = 0; x < size; x++)
				grid.XyToIndex[y * size + x] = (lineX[x] << ny) | lineY[y];

		// The qubit flipped by a step between neighbouring columns or rows depends only
		// on that axis, so this is O(size) rather than a per-pixel search.
		for (int x = 0; x + 1 < size; x++)
			grid.XNeighborQubit[x] = BitIndex(lineX[x] ^ lineX[x + 1]) + ny;
		for (int y = 0; y + 1 < size; y++)
			grid.YNeighborQubit[y] = BitIndex(lineY[y] ^ lineY[y + 1]);

		CachedGrids[size] = grid;
		return grid;
	}

	/// <summary>
	/// Applies the QuantumBlur effect to a square, power-of-two pixel buffer, in place.
	/// </summary>
	/// <param name="pixels">
	/// Row-major pixels in the order <c>GetPixels32</c> and <c>SetPixels32</c> use, that
	/// is <c>index = y * size + x</c>.
	/// </param>
	/// <param name="size">Width and height of the image, which must be equal.</param>
	/// <param name="xi">
	/// Blur strength, as the fraction of a pi rotation applied to the most-affected qubit.
	/// </param>
	/// <param name="locality">
	/// 0 blurs uniformly; 1 concentrates the blur at high-detail edges.
	/// </param>
	public static void Apply(Color32[] pixels, int size, float xi, float locality = 1f)
	{
		Grid grid = GetGrid(size);
		int dim = grid.Dim;
		int n = grid.Qubits;

		double[] hR = new double[dim];
		double[] hG = new double[dim];
		double[] hB = new double[dim];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color32 px = pixels[y * size + x];
				int idx = grid.XyToIndex[y * size + x];
				hR[idx] = px.r;
				hG[idx] = px.g;
				hB[idx] = px.b;
			}
		}

		// Per-qubit rates, as in blur_height: how much local brightness sits on an edge
		// that flips each particular bit. This is what makes the blur follow detail
		// rather than smearing uniformly. All three channels share one pass, since they
		// walk the same neighbour structure.
		double[] ratesR = new double[n];
		double[] ratesG = new double[n];
		double[] ratesB = new double[n];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				int idx = grid.XyToIndex[y * size + x];
				double r = hR[idx], g = hG[idx], b = hB[idx];

				if (x + 1 < size) AddRate(ratesR, ratesG, ratesB, grid.XNeighborQubit[x], r, g, b);
				if (x > 0) AddRate(ratesR, ratesG, ratesB, grid.XNeighborQubit[x - 1], r, g, b);
				if (y + 1 < size) AddRate(ratesR, ratesG, ratesB, grid.YNeighborQubit[y], r, g, b);
				if (y > 0) AddRate(ratesR, ratesG, ratesB, grid.YNeighborQubit[y - 1], r, g, b);
			}
		}

		double[] newR = BlurChannel(hR, ratesR, n, dim, xi, locality);
		double[] newG = BlurChannel(hG, ratesG, n, dim, xi, locality);
		double[] newB = BlurChannel(hB, ratesB, n, dim, xi, locality);

		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				int flat = y * size + x;
				int idx = grid.XyToIndex[flat];
				pixels[flat] = new Color32(
					ToByte(newR[idx]),
					ToByte(newG[idx]),
					ToByte(newB[idx]),
					pixels[flat].a);
			}
		}
	}

	private static void AddRate(double[] ratesR, double[] ratesG, double[] ratesB, int qubit, double r, double g, double b)
	{
		ratesR[qubit] += r;
		ratesG[qubit] += g;
		ratesB[qubit] += b;
	}

	private static byte ToByte(double value)
	{
		if (value <= 0.0) return 0;
		if (value >= 255.0) return 255;
		return (byte)Math.Round(value);
	}

	/// <summary>
	/// Runs <c>blur_height</c> followed by <c>circuit2height</c> for a single colour
	/// channel. Both <paramref name="height"/> and the returned array are indexed by
	/// basis index and hold brightnesses on the same 0-255 scale as the source pixels.
	/// </summary>
	private static double[] BlurChannel(double[] height, double[] rates, int n, int dim, float xi, float locality)
	{
		// height2circuit: amplitude = sqrt(brightness), normalised to a unit vector.
		// `total` is what the normalisation divides out, kept so the decode can restore
		// the original brightness scale.
		double total = 0.0;
		for (int i = 0; i < dim; i++) total += height[i];

		Complex[] state = new Complex[dim];
		if (total > 0.0)
		{
			double invSqrtTotal = 1.0 / Math.Sqrt(total);
			for (int i = 0; i < dim; i++)
				state[i] = new Complex(Math.Sqrt(height[i]) * invSqrtTotal, 0.0);
		}

		double maxRate = 0.0;
		for (int q = 0; q < n; q++) if (rates[q] > maxRate) maxRate = rates[q];
		if (maxRate <= 0.0) maxRate = 1.0;

		// Initialise with the encoded state, then apply the blur rotation to every qubit,
		// as blur_height does with axis='x'.
		QuantumCircuit qc = new QuantumCircuit(n);
		qc.Init(state);
		for (int q = 0; q < n; q++)
		{
			double theta = Math.PI * (locality * (rates[q] / maxRate) + (1 - locality)) * xi;
			qc.Rx(theta, q);
		}

		// Decode from the exact statevector rather than sampled shots.
		//
		// This does not follow probs2height/heights2image, which rescale so that the
		// largest value becomes 1. Blurring lowers the peak, so scaling the peak back up
		// drags every other pixel with it and washes the image out; measured on a test
		// photo, mean brightness rose from 127.5 to 221.5 at maximum blur.
		//
		// Multiplying by `total` instead undoes the encode's normalisation. The
		// simulation is unitary, so the probabilities sum to 1 and the decode is exactly
		// brightness-conserving: light is redistributed between pixels, never created or
		// destroyed.
		Complex[] amplitudes = Simulator.Statevector(qc);
		double[] result = new double[dim];
		for (int i = 0; i < dim; i++)
		{
			double re = amplitudes[i].Real, im = amplitudes[i].Imaginary;
			result[i] = (re * re + im * im) * total;
		}

		return result;
	}
}
