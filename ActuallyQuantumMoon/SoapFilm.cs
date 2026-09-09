using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// The film-thickness field that makes the entanglement shell read as a soap bubble
/// rather than a uniformly thick sphere.
/// </summary>
/// <remarks>
/// <para>
/// The ramp's thickness dependence enters only through the reference shader's phase term,
/// <c>D = -2 * 2pi * thickness * cosTheta</c>, so a fringe is the locus
/// <c>thickness * cosTheta = constant</c>. A thickness that is constant, or any function
/// of <c>cosTheta</c>, makes that locus a circle centred on the view axis and the shell a
/// bullseye. Breaking the symmetry requires a thickness that varies over the sphere
/// independently of the viewing angle, and that variation must be genuinely
/// two-dimensional: a gradient along a single axis produces only latitude stripes, which
/// still read as concentric. Hence a turbulent two-dimensional field, fixed to the moon's
/// body frame.
/// </para>
/// <para>
/// Domain warping is what makes the field swirl rather than blob. A plain fractal sum
/// gives rounded patches; evaluating that sum at a position itself displaced by another
/// fractal sum stretches those patches into filaments and folds, as surface flow does to a
/// real film. This is the standard turbulence construction, and <c>swirl</c> is its
/// displacement.
/// </para>
/// <para>
/// Amplitude is the parameter that matters, and the effect reverses past a point. Measured
/// against the shipped bake, sweeping the field across the whole thickness axis at a high
/// spatial frequency averages every fringe colour together within one screen pixel and the
/// shell goes grey. A moderate amplitude at a low spatial frequency with a strong warp is
/// what produces the intended look.
/// </para>
/// <para>
/// The field is baked into six cube faces rather than evaluated per vertex per frame. It
/// must be sampled once per vertex every time the cap re-points at the camera, around 25k
/// samples, and a warped four-octave fractal sum costs about 10 lattice fetches each;
/// baking reduces that to one bilinear fetch. Cube faces avoid the per-vertex atan2 and
/// asin a latitude/longitude table would need. The cost is a half-texel discontinuity
/// along the face seams, which at this resolution and smoothness sits below one
/// least-significant bit of an 8-bit display.
/// </para>
/// </remarks>
internal sealed class SoapFilm
{
	// The value-noise lattice. It wraps, and so tiles, which is invisible here because it
	// is only ever sampled on a sphere of radius 1 at a few cycles per sphere.
	private const int LatticeSize = 32;

	private readonly float[][] _faces;
	private readonly int _res;

	public int Resolution => _res;

	/// <summary>
	/// Bakes the warped fractal field into six cube faces.
	/// </summary>
	/// <param name="resolution">Texels per cube-face edge.</param>
	/// <param name="scale">Cycles across the sphere.</param>
	/// <param name="octaves">How much fine detail rides on top.</param>
	/// <param name="swirl">Domain-warp displacement.</param>
	/// <param name="seed">Selects which field is generated.</param>
	public SoapFilm(int resolution, float scale, int octaves, float swirl, int seed)
	{
		_res = Mathf.Max(8, resolution);
		octaves = Mathf.Max(1, Mathf.Min(6, octaves));

		var lattices = new float[4][];
		for (int i = 0; i < lattices.Length; i++)
		{
			lattices[i] = BuildLattice(seed + i * 7919);
		}

		_faces = new float[6][];
		float lo = float.MaxValue;
		float hi = float.MinValue;

		for (int face = 0; face < 6; face++)
		{
			var data = new float[_res * _res];
			for (int y = 0; y < _res; y++)
			{
				// Texel centres, so that the two faces either side of a seam sample the
				// same 3D field at the same place and the seam stays small.
				float fy = (y + 0.5f) / _res * 2f - 1f;
				for (int x = 0; x < _res; x++)
				{
					float fx = (x + 0.5f) / _res * 2f - 1f;
					Vector3 dir = FaceDirection(face, fx, fy).normalized;
					float value = Warped(dir, lattices, scale, octaves, swirl);
					data[y * _res + x] = value;
					if (value < lo) lo = value;
					if (value > hi) hi = value;
				}
			}
			_faces[face] = data;
		}

		// Normalise to fill [0, 1] once here rather than per frame, so that `amount` means
		// the same thing for every seed and scale. The fractal sum's own range varies with
		// the octave count, which would otherwise change the strength of the effect
		// whenever the detail setting changed.
		float span = Mathf.Max(hi - lo, 1e-6f);
		for (int face = 0; face < 6; face++)
		{
			var data = _faces[face];
			for (int i = 0; i < data.Length; i++)
			{
				data[i] = (data[i] - lo) / span;
			}
		}
	}

	/// <summary>
	/// Eight fixed directions whose field values identify a field closely enough to detect
	/// a divergent reimplementation. <c>LogState</c> prints them, so an offline port of
	/// this class can be checked against what the game actually draws.
	/// </summary>
	/// <remarks>
	/// Expect agreement to about 1e-4 rather than exactly, if the other side works in
	/// double precision.
	/// </remarks>
	private static readonly Vector3[] FingerprintDirections =
	{
		new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
		new Vector3(-1f, 0f, 0f), new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, -1f),
		new Vector3(0.5773503f, 0.5773503f, 0.5773503f),
		new Vector3(-0.4082483f, 0.8164966f, -0.4082483f),
	};

	/// <summary>
	/// The field at the fingerprint directions, as a compact string for logging.
	/// </summary>
	public string Fingerprint()
	{
		var parts = new string[FingerprintDirections.Length];
		for (int i = 0; i < FingerprintDirections.Length; i++)
		{
			parts[i] = Sample(FingerprintDirections[i]).ToString("F5");
		}
		return string.Join(" ", parts);
	}

	/// <summary>
	/// The field at a direction, in [0, 1]. <paramref name="dir"/> need not be normalised.
	/// </summary>
	public float Sample(Vector3 dir)
	{
		float ax = Mathf.Abs(dir.x);
		float ay = Mathf.Abs(dir.y);
		float az = Mathf.Abs(dir.z);

		int face;
		float u, v, major;
		if (ax >= ay && ax >= az)
		{
			major = ax;
			face = dir.x >= 0f ? 0 : 1;
			u = dir.x >= 0f ? -dir.z : dir.z;
			v = -dir.y;
		}
		else if (ay >= az)
		{
			major = ay;
			face = dir.y >= 0f ? 2 : 3;
			u = dir.x;
			v = dir.y >= 0f ? dir.z : -dir.z;
		}
		else
		{
			major = az;
			face = dir.z >= 0f ? 4 : 5;
			u = dir.z >= 0f ? dir.x : -dir.x;
			v = -dir.y;
		}
		if (major < 1e-9f) return 0.5f;

		float inv = 1f / major;
		return Bilinear(_faces[face], u * inv, v * inv);
	}

	private float Bilinear(float[] data, float u, float v)
	{
		// [-1,1] to texel space, then clamp. Clamping at the face border is the seam
		// approximation described on the class.
		float x = (u * 0.5f + 0.5f) * _res - 0.5f;
		float y = (v * 0.5f + 0.5f) * _res - 0.5f;
		int x0 = Mathf.FloorToInt(x);
		int y0 = Mathf.FloorToInt(y);
		float fx = x - x0;
		float fy = y - y0;
		int xa = Clamp(x0, 0, _res - 1);
		int xb = Clamp(x0 + 1, 0, _res - 1);
		int ya = Clamp(y0, 0, _res - 1) * _res;
		int yb = Clamp(y0 + 1, 0, _res - 1) * _res;

		float top = data[ya + xa] + (data[ya + xb] - data[ya + xa]) * fx;
		float bot = data[yb + xa] + (data[yb + xb] - data[yb + xa]) * fx;
		return top + (bot - top) * fy;
	}

	// net48 has no Math.Clamp.
	private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

	private static Vector3 FaceDirection(int face, float u, float v)
	{
		switch (face)
		{
			case 0: return new Vector3(1f, -v, -u);
			case 1: return new Vector3(-1f, -v, u);
			case 2: return new Vector3(u, 1f, v);
			case 3: return new Vector3(u, -1f, -v);
			case 4: return new Vector3(u, -v, 1f);
			default: return new Vector3(-u, -v, -1f);
		}
	}

	// --- the field itself -----------------------------------------------------

	private static float Warped(Vector3 p, float[][] lattices, float scale, int octaves,
		float swirl)
	{
		if (swirl > 0f)
		{
			// Three independent two-octave sums as a displacement vector. The offsets are
			// arbitrary and need only differ from each other.
			float wx = Fbm(p + new Vector3(11.3f, 5.1f, 2.7f), lattices, scale, 2);
			float wy = Fbm(p + new Vector3(27.7f, 13.9f, 7.3f), lattices, scale, 2);
			float wz = Fbm(p + new Vector3(43.1f, 23.3f, 17.1f), lattices, scale, 2);
			p += new Vector3(wx - 0.5f, wy - 0.5f, wz - 0.5f) * (swirl * 2f);
		}
		return Fbm(p, lattices, scale, octaves);
	}

	private static float Fbm(Vector3 p, float[][] lattices, float scale, int octaves)
	{
		float total = 0f;
		float amp = 1f;
		float norm = 0f;
		float freq = scale;
		for (int o = 0; o < octaves; o++)
		{
			total += amp * ValueNoise(p * freq, lattices[o % lattices.Length]);
			norm += amp;
			amp *= 0.5f;
			freq *= 2f;
		}
		return total / Mathf.Max(norm, 1e-6f);
	}

	private static float[] BuildLattice(int seed)
	{
		var data = new float[LatticeSize * LatticeSize * LatticeSize];
		// A local xorshift rather than UnityEngine.Random: reproducible across runs, and it
		// leaves the game's own random sequence untouched.
		uint state = (uint)seed | 1u;
		for (int i = 0; i < data.Length; i++)
		{
			state ^= state << 13;
			state ^= state >> 17;
			state ^= state << 5;
			data[i] = (state & 0xFFFFFF) / (float)0x1000000;
		}
		return data;
	}

	private static float ValueNoise(Vector3 p, float[] lattice)
	{
		int ix = Mathf.FloorToInt(p.x);
		int iy = Mathf.FloorToInt(p.y);
		int iz = Mathf.FloorToInt(p.z);
		float fx = Smooth(p.x - ix);
		float fy = Smooth(p.y - iy);
		float fz = Smooth(p.z - iz);

		int x0 = Wrap(ix), x1 = Wrap(ix + 1);
		int y0 = Wrap(iy), y1 = Wrap(iy + 1);
		int z0 = Wrap(iz), z1 = Wrap(iz + 1);

		float c000 = At(lattice, x0, y0, z0), c100 = At(lattice, x1, y0, z0);
		float c010 = At(lattice, x0, y1, z0), c110 = At(lattice, x1, y1, z0);
		float c001 = At(lattice, x0, y0, z1), c101 = At(lattice, x1, y0, z1);
		float c011 = At(lattice, x0, y1, z1), c111 = At(lattice, x1, y1, z1);

		float c00 = c000 + (c100 - c000) * fx;
		float c10 = c010 + (c110 - c010) * fx;
		float c01 = c001 + (c101 - c001) * fx;
		float c11 = c011 + (c111 - c011) * fx;
		float c0 = c00 + (c10 - c00) * fy;
		float c1 = c01 + (c11 - c01) * fy;
		return c0 + (c1 - c0) * fz;
	}

	// Smoothstep, so that the field is C1 and the fringes it drives do not crease along
	// lattice cell boundaries.
	private static float Smooth(float t) => t * t * (3f - 2f * t);

	private static int Wrap(int i)
	{
		i %= LatticeSize;
		return i < 0 ? i + LatticeSize : i;
	}

	private static float At(float[] lattice, int x, int y, int z)
		=> lattice[(z * LatticeSize + y) * LatticeSize + x];
}
