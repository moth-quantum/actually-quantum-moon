using System.Collections.Generic;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Generates the shell geometry for the ramp render path: a camera-facing spherical cap
/// whose UVs carry the ramp lookup coordinates.
/// </summary>
/// <remarks>
/// <para>
/// A cap rather than a sphere, because the borrowed stock shaders are particle shaders
/// and use <c>Cull Off</c>, drawing both sides of every triangle. A closed sphere would
/// then put two surfaces in every pixel at two different incidence angles. A camera-facing
/// cap puts exactly one surface in every pixel however the borrowed shader culls. The cap
/// covers the whole silhouette, since the pattern is rotationally symmetric about the view
/// axis and the far half contributes nothing the near half does not.
/// </para>
/// <para>
/// Rings are uniform in <c>cosTheta</c> rather than in angle. <c>cosTheta</c> indexes the
/// ramp's U axis, so spacing rings evenly in it bounds the interpolation error at
/// <c>1/rings</c> everywhere, and concentrates rings towards the limb where
/// <c>cosTheta</c> changes fastest per screen pixel.
/// </para>
/// <para>
/// The ramp's V axis is film thickness. This class writes that coordinate into UV.y, so
/// the ring pattern multiplies and sweeps with camera distance rather than sitting on the
/// screen as a fixed bullseye. UV.y also varies per vertex, which is what makes the shell
/// read as a soap bubble: a fringe is the locus <c>thickness * cosTheta = constant</c>, so
/// a thickness that varies with azimuth as well as polar angle bends that locus off a
/// circle. The variation must be two-dimensional and turbulent to do so; a smooth
/// monotonic gradient shifts the pattern by a fraction of a fringe and still reads as a
/// bullseye. See <see cref="SoapFilm"/> for the field.
/// </para>
/// <para>
/// The mesh does not need extra segments for the film: at 384 x 64 it reproduces a
/// per-pixel evaluation of the same field almost exactly, because the rings are the fine
/// axis and the swirls are large-scale in azimuth. 192 and 384 segments were measured and
/// are indistinguishable.
/// </para>
/// </remarks>
internal sealed class EntanglementCap
{
	// Ring count for the shell. Reconstructing the ramp for the shipped bake by linear
	// interpolation between N knots, against a 20k-sample evaluation, gives these errors
	// as a fraction of the full 0-1 output range:
	//
	//     rings    max err   rms err
	//       128     0.0245    0.0022
	//       256     0.0065    0.0007
	//       384     ~0.004    ~0.0005
	//       512     0.0028    0.0003
	//
	// 384 puts the worst case near one least-significant bit of an 8-bit display.
	//
	// This value must be measured against the ramp rather than estimated from the phase
	// term. The estimate 2 * thickness / lambda gives about 4 at 1000nm, but that counts
	// how often the phase coordinate wraps as cosTheta sweeps [0,1], not how often the
	// output oscillates. The bake is itself strongly oscillatory: on the shipped table R
	// turns over 44-70 times along the phase axis and 27-73 times along the angle axis,
	// and a diagonal across it hits about 78 turning points, or 39 oscillations.
	//
	// EntanglementSky passes its own, larger value, because it uses only the narrow
	// cosTheta band between the player's zenith and horizon.
	internal const int ShellRings = 384;

	// Rings is per instance; Segments is shared, since 64 is sufficient for both callers.
	private readonly int _rings;

	// Segments decide only how round the silhouette looks.
	private const int Segments = 64;

	// Radius 0.5, so that localScale is a diameter and matches the sizing convention of
	// Unity's primitive sphere.
	private const float Radius = 0.5f;

	// Camera distance from the shell's centre, in shell radii rather than metres.
	//
	// Past ProxyBody.CUTOFF_DISTANCE the moon on screen is a stand-in that ProxyBody pins
	// at a fixed distance from the camera and rescales to preserve angular size, so world
	// distance to it is constant however far away the real moon is. Anything driven by
	// world distance would freeze at that handoff. The ratio distance/radius is preserved
	// across it, which is also why cos(theta) survives it unchanged.
	//
	// InverseTransformPoint divides out the transform's scale, so this is already in units
	// of the cap's own construction radius.
	internal static float DistanceInRadii(Transform capTransform, Vector3 cameraWorldPos)
	{
		return capTransform.InverseTransformPoint(cameraWorldPos).magnitude / Radius;
	}

	private readonly Mesh _mesh;
	private readonly Vector3[] _positions;
	private readonly Vector3[] _normals;

	// Half a texel on the ramp's V axis, from EntanglementRamp.VInset. Thickness fractions
	// are mapped into [_vInset, 1 - _vInset] so that 0 and 1 land on the centres of the
	// first and last rows rather than half a row outside the texture, which clamping would
	// flatten into constant-coloured poles.
	private readonly float _vInset;

	// Reused across frames. Mesh.SetUVs and SetColors take a List and copy out of it, so
	// holding these keeps the update allocation-free.
	private readonly List<Vector2> _uvs;
	private readonly List<Color32> _colors;

	// The thickness field, and how far across the ramp's thickness axis it swings. A null
	// field or zero amount gives a uniform-thickness shell.
	private readonly SoapFilm _film;

	// Mutable because EntanglementSky adjusts it at runtime; the shell's value is fixed.
	private float _soapAmount;

	// What the current UVs were built for. NaN forces the first update to run.
	private float _builtForDistance = float.NaN;
	private bool _builtForInside;
	private float _builtForDistanceFraction;

	// The cap-to-body rotation the soap field was last sampled at. The field is fixed to
	// the moon, so re-pointing the cap at the camera moves every vertex through it and the
	// UVs must be rebuilt; the distance-only early-out below is not sufficient once a film
	// is present.
	private Quaternion _builtForRotation = Quaternion.identity;
	private bool _hasBuiltRotation;

	// The soap field at every vertex, in the moon's frame. Allocated on first use so that
	// a film-less cap costs nothing, and refreshed only when the cap turns.
	private float[] _soapField;

	// Whether _colors currently holds a horizon mask rather than plain opaque white. The
	// constructor fills it opaque, so this starts false. See Refresh for what it saves.
	private bool _colorsCarryMask;

	// How far the cap may turn before the soap field is resampled. Orbiting a planet turns
	// this rotation slowly, so a fraction of a degree keeps tens of thousands of samples
	// off most frames, and the worst error it leaves is that same fraction of a degree of
	// lag in a pattern whose features are tens of degrees across.
	//
	// The sky needs a looser threshold than the shell. The shell's rotation follows the
	// camera's position relative to the moon, which changes slowly at distance. The sky's
	// follows the player's local up, which on a moon of radius ~65 turns a quarter of a
	// degree for every ~0.3 units walked - at walking pace, a full 65k-vertex rebuild
	// roughly fifteen times a second. A degree costs four times less and stays below what
	// is visible on a smooth field.
	private readonly float _rotationEpsilonDeg;

	public Mesh Mesh => _mesh;

	public EntanglementCap(float vInset, SoapFilm film, float soapAmount, int rings,
		float rotationEpsilonDeg = 0.25f)
	{
		_rotationEpsilonDeg = Mathf.Max(0f, rotationEpsilonDeg);
		_vInset = vInset;
		_film = film;
		_soapAmount = Mathf.Clamp01(soapAmount);
		_rings = Mathf.Max(1, rings);

		// One pole vertex, then `rings` rings of Segments each. No seam duplication is
		// needed: UV.x carries cosTheta, which does not depend on azimuth, and UV.y is a
		// continuous function of position, so the first and last segment can share
		// vertices without a UV discontinuity.
		int vertexCount = 1 + _rings * Segments;

		_positions = new Vector3[vertexCount];
		_normals = new Vector3[vertexCount];
		_uvs = new List<Vector2>(vertexCount);
		_colors = new List<Color32>(vertexCount);

		_positions[0] = new Vector3(0f, 0f, Radius);
		_normals[0] = Vector3.forward;

		for (int ring = 1; ring <= _rings; ring++)
		{
			// cosTheta from 1 at the pole down to 0 at the rim, the silhouette in the
			// far-field limit. Perspective moves the true horizon inside the rim, and
			// Refresh masks the difference.
			float cosTheta = 1f - (float)ring / _rings;
			float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));

			for (int seg = 0; seg < Segments; seg++)
			{
				float phi = TwoPi * seg / Segments;
				var normal = new Vector3(
					sinTheta * Mathf.Cos(phi), sinTheta * Mathf.Sin(phi), cosTheta);
				int index = 1 + (ring - 1) * Segments + seg;
				_normals[index] = normal;
				_positions[index] = normal * Radius;
			}
		}

		var triangles = new List<int>(_rings * Segments * 6);

		// Pole fan.
		for (int seg = 0; seg < Segments; seg++)
		{
			triangles.Add(0);
			triangles.Add(1 + seg);
			triangles.Add(1 + (seg + 1) % Segments);
		}

		// Quad bands between consecutive rings.
		for (int ring = 1; ring < _rings; ring++)
		{
			int inner = 1 + (ring - 1) * Segments;
			int outer = 1 + ring * Segments;
			for (int seg = 0; seg < Segments; seg++)
			{
				int next = (seg + 1) % Segments;
				triangles.Add(inner + seg);
				triangles.Add(outer + seg);
				triangles.Add(outer + next);

				triangles.Add(inner + seg);
				triangles.Add(outer + next);
				triangles.Add(inner + next);
			}
		}

		// The highest index must fit in 16 bits, Unity's default index format. The shell
		// is 1 + 384*64 = 24577 and the sky is 1 + 1023*64 = 65473, just under the 65535
		// ceiling. More rings than that would corrupt the mesh silently, so the limit is
		// checked.
		if (vertexCount > 65535)
		{
			throw new System.ArgumentOutOfRangeException(nameof(rings),
				$"{rings} rings x {Segments} segments is {vertexCount} vertices, past the " +
				"65535 that Unity's default 16-bit mesh index format can address. Either " +
				"lower the ring count or set Mesh.indexFormat to UInt32 here.");
		}

		_mesh = new Mesh { name = "EntanglementCap" };
		_mesh.vertices = _positions;
		_mesh.normals = _normals;
		_mesh.SetTriangles(triangles, 0);

		// Valid UVs and colours before the first Refresh, in case a frame draws between
		// construction and Tick. cos(polar angle) is the far-field answer for U, and the
		// middle of the thickness range is a safe V.
		for (int i = 0; i < vertexCount; i++)
		{
			_uvs.Add(new Vector2(Mathf.Abs(_normals[i].z), 0.5f));
			_colors.Add(new Color32(255, 255, 255, 255));
		}
		_mesh.SetUVs(0, _uvs);
		_mesh.SetColors(_colors);

		// The cap is re-pointed at the camera every frame, so its bounds must cover the
		// whole sphere it is a half of, or Unity frustum-culls it as soon as the visible
		// half swings away from the baked bounds.
		_mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (Radius * 2f));
	}

	private const float TwoPi = 6.283185307179586f;

	/// <summary>
	/// Writes this frame's <c>cosTheta</c> into UV.x, film thickness into UV.y, and the
	/// horizon mask into vertex alpha.
	/// </summary>
	/// <remarks>
	/// <para>
	/// UV.x is the one place this path differs from a per-pixel shader in kind rather than
	/// in value: cosTheta is computed per ring and interpolated between rings. The error is
	/// second order in ring spacing, and the fringes themselves are still recovered per
	/// pixel from the ramp texture, so what interpolates is smooth and what is sharp is not.
	/// </para>
	/// <para>
	/// One cosTheta per ring rather than per vertex, because
	/// <see cref="EntanglementMoonShell"/> points the cap's local +Z at the camera, putting
	/// the camera on the axis of symmetry and making cosTheta exactly constant around each
	/// ring.
	/// </para>
	/// <para>
	/// Without a film, UV.y is one number for the whole cap and the update does nothing
	/// unless the distance or that number changed, so orbiting the moon at a steady range
	/// costs no mesh updates.
	/// </para>
	/// <para>
	/// Vertex alpha carries the horizon. Under perspective the visible cap stops at
	/// <c>dot(N, V) = 0</c>, inside the geometric rim, and the band beyond it projects back
	/// over the visible part. Zeroing alpha there removes it, and because alpha interpolates
	/// the result is a soft edge at the limb.
	/// </para>
	/// </remarks>
	public void Refresh(Transform transform, Vector3 cameraWorldPos, bool cameraInside,
		float thicknessFraction)
	{
		// Into the cap's own space, which folds away the moon's rotation and the proxy's
		// compressed scale at once. Scale is uniform on both shells, so directions survive
		// the transform.
		Vector3 cameraLocal = transform.InverseTransformPoint(cameraWorldPos);

		// Signed, so that pointing the cap away from the camera - the inside-the-shell
		// case - is not mistaken for the same distance on the near side.
		float distance = cameraLocal.magnitude * Mathf.Sign(cameraLocal.z);

		// Cap-local to body (moon) space. PointCapAt sets the cap's world rotation, so
		// Unity derives this from the parent, which anchors the soap field to the moon
		// rather than to the screen. Anchored to the screen, the swirls would sit still
		// while the moon turned under them.
		Quaternion bodyRotation = transform.localRotation;
		bool sampleFilm = _film != null && _soapAmount > 0f;

		// Two independent reasons to rebuild, only one of them expensive. The soap field
		// at a vertex depends only on that vertex's direction in the moon's frame, and so
		// only on the cap's rotation; cos(theta) depends only on the distance. Treating
		// them as one condition would re-sample the film at every vertex whenever the
		// camera moved, which on the sky's 65473-vertex mesh is ~65k warped cube-map
		// fetches per frame.
		//
		// So the field is cached per vertex and refreshed only when the rotation passes its
		// threshold; a distance-only change re-walks the cache at a read and a lerp per
		// vertex. This is what makes a fixed-radius sky affordable, since with the radius
		// pinned the player's distance to the dome changes with every step.
		bool rotationChanged = sampleFilm &&
			(!_hasBuiltRotation ||
			 Quaternion.Angle(bodyRotation, _builtForRotation) >= _rotationEpsilonDeg);

		bool distanceChanged = float.IsNaN(_builtForDistance) ||
			cameraInside != _builtForInside ||
			Mathf.Abs(distance - _builtForDistance) >= Radius * 1e-4f ||
			Mathf.Abs(thicknessFraction - _builtForDistanceFraction) >= 1e-4f;

		if (!rotationChanged && !distanceChanged) return;

		_builtForDistance = distance;
		_builtForInside = cameraInside;
		_builtForDistanceFraction = thicknessFraction;

		if (rotationChanged)
		{
			RebuildSoapField(bodyRotation);
			_builtForRotation = bodyRotation;
			_hasBuiltRotation = true;
		}

		// The distance-driven thickness, which without a film is a single V for the whole
		// cap.
		float v = Mathf.Lerp(_vInset, 1f - _vInset, Mathf.Clamp01(thicknessFraction));

		// With a film, camera distance instead chooses where a window of fixed width sits
		// on the thickness axis, and the field fills that window.
		//
		// The window slides rather than shrinking. Shrinking it to fit inside [0,1] would
		// collapse its width to zero when the camera is very close or very far away,
		// returning the bullseye at both ends of the approach. Sliding keeps the swirl
		// equally strong everywhere while still letting distance change which fringe orders
		// are visible.
		float width = Mathf.Min(_soapAmount, 0.5f) * 2f;
		float windowLo = Mathf.Min(
			Mathf.Max(Mathf.Clamp01(thicknessFraction) - width * 0.5f, 0f), 1f - width);

		// From inside there is no horizon and every vertex is opaque, so once the colours
		// hold no mask there is nothing left for them to say and they can be left alone.
		// The sky is always inside, and its UVs do have to be rewritten whenever the
		// player moves, so this takes a 65473-vertex write and upload off those frames.
		bool writeColours = !cameraInside || _colorsCarryMask;

		// The pole first: its normal is the axis, so it faces the camera directly.
		float poleCos = PointCos(0, cameraLocal);
		_uvs[0] = new Vector2(Mathf.Abs(poleCos), sampleFilm
			? FilmV(0, windowLo, width)
			: v);
		if (writeColours) _colors[0] = MaskColour(poleCos, cameraInside);

		for (int ring = 1; ring <= _rings; ring++)
		{
			int rowStart = 1 + (ring - 1) * Segments;
			// Segment 0 stands for its whole ring. Using the real camera vector rather
			// than assuming (0, 0, d) keeps this correct even if the cap is a frame behind
			// its own rotation.
			float cos = PointCos(rowStart, cameraLocal);
			float u = Mathf.Abs(cos);

			if (sampleFilm)
			{
				// U remains exact per ring and only V varies around the ring, which is why
				// the mesh needs no extra segments: the coordinate carrying the fringes is
				// unchanged, and the one carrying the swirls is smooth.
				for (int seg = 0; seg < Segments; seg++)
				{
					int index = rowStart + seg;
					_uvs[index] = new Vector2(u, FilmV(index, windowLo, width));
				}
			}
			else
			{
				var uv = new Vector2(u, v);
				for (int seg = 0; seg < Segments; seg++)
				{
					_uvs[rowStart + seg] = uv;
				}
			}

			if (writeColours)
			{
				// One colour for the whole ring: the mask depends on cos(theta), which is
				// constant around it.
				Color32 colour = MaskColour(cos, cameraInside);
				for (int seg = 0; seg < Segments; seg++) _colors[rowStart + seg] = colour;
			}
		}

		_mesh.SetUVs(0, _uvs);
		if (writeColours)
		{
			_mesh.SetColors(_colors);
			_colorsCarryMask = !cameraInside;
		}
	}

	/// <summary>
	/// Samples the soap field at every vertex, in the moon's frame. The expensive half of
	/// the refresh, called only when the cap has turned far enough to matter.
	/// </summary>
	private void RebuildSoapField(Quaternion bodyRotation)
	{
		if (_soapField == null) _soapField = new float[_normals.Length];
		for (int i = 0; i < _normals.Length; i++)
		{
			_soapField[i] = _film.Sample(bodyRotation * _normals[i]);
		}
	}

	/// <summary>
	/// Computes one vertex's V from the cached field, mapped into the sliding window and
	/// inset off the edge rows as <see cref="EntanglementRamp"/> requires.
	/// </summary>
	private float FilmV(int index, float windowLo, float width)
	{
		return Mathf.Lerp(_vInset, 1f - _vInset, windowLo + width * _soapField[index]);
	}

	/// <summary>
	/// Changes how far the film swings and forces the next Refresh to rebuild rather than
	/// early-out.
	/// </summary>
	public void SetSoapAmount(float soapAmount)
	{
		_soapAmount = Mathf.Clamp01(soapAmount);
		_builtForDistance = float.NaN;
		_hasBuiltRotation = false;
	}

	/// <summary>
	/// Signed cosine of the incidence angle at one vertex. The sign says whether the point
	/// is over the horizon; the ramp lookup then takes the absolute value, since a surface
	/// seen from behind has the same optical path as one seen from in front.
	/// </summary>
	private float PointCos(int index, Vector3 cameraLocal)
	{
		Vector3 toCamera = cameraLocal - _positions[index];
		float lengthSq = toCamera.sqrMagnitude;
		if (lengthSq < 1e-12f) return 1f;
		return Vector3.Dot(_normals[index], toCamera / Mathf.Sqrt(lengthSq));
	}

	// From inside the shell the entire inner surface is visible, with no horizon to clip
	// against, so nothing is masked.
	private static Color32 MaskColour(float cosSigned, bool cameraInside)
	{
		byte alpha = (cameraInside || cosSigned > 0f) ? (byte)255 : (byte)0;
		return new Color32(255, 255, 255, alpha);
	}
}
