using System.IO;
using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// The same film seen from underneath: the sky visible when standing on the Quantum Moon
/// and looking up.
/// </summary>
/// <remarks>
/// <para>
/// The bake exports two tables, because its reference use of them is
/// <c>BSDF = Reflectance * microfacet("ggx", ...) + Transmittance * transparent()</c>.
/// R is the light the film bounces back, T the light that comes through it.
/// <see cref="EntanglementMoonShell"/> draws R, correct for a film seen from outside.
/// From inside there is nothing to reflect towards the viewer, who is under the film
/// looking out through it, so the sky is T. Both tables come from the same bake, so the
/// two are the same film by construction.
/// </para>
/// <para>
/// The dome is a sphere centred on the moon rather than on the player, with the player
/// inside it. Looking straight up therefore hits it at normal incidence
/// (<c>cosTheta = 1</c>) and looking towards the horizon hits it obliquely, as a real
/// atmosphere presents a longer path near the horizon. The cap's axis points along the
/// player's local up rather than their gaze, which puts the player on the axis of
/// symmetry and keeps <see cref="EntanglementCap"/>'s per-ring cosTheta shortcut exact.
/// </para>
/// <para>
/// The sky uses only the cosTheta band between 1 at the zenith and
/// <c>sqrt(1 - (d/R)^2)</c> at the horizon, where d is the player's radius and R the
/// dome's: d/R = 0.4 gives 0.92..1.00, d/R = 0.9 gives 0.44..1.00. Altitude alone
/// therefore changes the look, from a fine busy sky low down to broad sweeps higher up,
/// with no parameter driving it. That is why the thickness here is a constant rather than
/// a function of height.
/// </para>
/// <para>
/// It needs many more rings than the shell for the same reason: the shell spreads 384
/// rings over the whole of cosTheta [0,1], while the sky sees only a slice, so at
/// d/R = 0.9 those 384 rings would put barely 38 across the entire sky. 1023 matches a
/// per-pixel evaluation; 384 visibly loses the fine structure near the zenith.
/// </para>
/// <para>
/// Additive, unlike the shell. The shell is alpha-blended because it replaces the grey
/// moon; a sky must leave the terrain, fog and stars intact, so this only adds light,
/// which also makes a wrong strength read as too bright rather than as a hole in the
/// world.
/// </para>
/// </remarks>
internal static class EntanglementSky
{
	private const string RampFileName = "Assets/entanglement_transmittance_ramp.bin";

	// Present in Outer Wilds 1.1.16 alongside the alpha-blended shader the shell uses:
	// single pass, _MainTex, _TintColor.
	private const string AdditiveShader = "Legacy Shaders/Particles/Additive";

	// Ring count. See the class remarks for why the sky needs far more than the shell.
	private const int Rings = 1023;

	// Radius of the dome, as a fraction of the moon's measured outer radius, following the
	// same convention as EntanglementMoonShell.ShellScale.
	//
	// The dome is a fixed place rather than something that follows the player. Deriving
	// the radius from the player's own, holding d/R constant, would hold the look constant
	// at every altitude, since for a dome of radius R, a player at radius d and a zenith
	// angle alpha, cos(theta) = sqrt(1 - (d/R)^2 * sin(alpha)^2). But it would also lift
	// the whole aurora with the player when they climb, which reads as the sky retreating,
	// and eventually push it beyond the interior fog. A fixed radius instead lets d/R vary
	// with altitude, so the band of cos(theta) the sky spans changes as the player climbs
	// - the altitude-driven variation described on the class - and it is also why 1023
	// rings are needed, since low down the whole sky is squeezed into a narrow slice of
	// the angle axis.
	//
	// The radius must be low enough to stay inside the interior fog: too high and an
	// additive layer averaging 0.18 washes out before it reaches the camera. 0.8 was found
	// by testing in game, since the terrain radius is not available to this code -
	// MeasureMoonRadius measures the fog and outer-cloud envelope rather than the ground.
	private const float Scale = 0.8f;

	// Where on the baked thickness range the sky's film sits. Constant, because altitude
	// already varies the look; 0.5 is the middle of the range, which has fringe structure
	// at every cosTheta the sky can reach.
	private const float Thickness = 0.5f;

	// Brightness. 1 is the transmittance at face value; see EnsureLoaded for why T is not
	// scaled up despite being the darker of the two tables.
	private const float Strength = 1f;

	// How often the sky reports its state while the player is under it, in seconds; 0
	// silences it. "The sky is not iridescent" and "I am not under the sky" are
	// indistinguishable from inside the game, so this is kept at a rate slow enough not to
	// clutter the console.
	private const float LogSeconds = 30f;

	private static float _lastLogged = -999f;

	// EntanglementCap is built at radius 0.5, so localScale is a diameter.
	private static void ApplyRadius()
	{
		_dome.transform.localScale = Vector3.one * (_moonRadius * 2f * Scale);
	}

	private static IModConsole Console => ActuallyQuantumMoon.Instance.ModHelper.Console;

	private static EntanglementRamp _ramp;
	private static Material _material;
	private static bool _loadAttempted;

	private static GameObject _dome;
	private static EntanglementCap _cap;

	// The moon's outer radius, in the moon's own units.
	private static float _moonRadius;

	// Whether the player is under the dome at all. Logged, because that state is not
	// otherwise distinguishable from inside the game.
	private static bool _underDome;

	internal static bool Attached => _dome != null;
	internal static bool UnderDome => _underDome;

	internal static void OnSceneLoaded(Transform moon, float moonRadius, int layer)
	{
		_dome = null;
		_cap = null;
		_underDome = false;
		_moonRadius = moonRadius;

		if (!EnsureLoaded()) return;

		// 1 degree rather than the shell's 0.25: the sky's axis is the player's local up,
		// which turns quickly when walking. See EntanglementCap's rotation threshold.
		_cap = new EntanglementCap(_ramp.VInset, EntanglementMoonShell.Film,
			EntanglementMoonShell.SkySoapAmount, Rings, 1f);

		_dome = new GameObject("EntanglementSky");
		_dome.AddComponent<MeshFilter>().sharedMesh = _cap.Mesh;
		_dome.AddComponent<MeshRenderer>().sharedMaterial = _material;
		_dome.layer = layer;
		_dome.transform.SetParent(moon, false);
		_dome.transform.localPosition = Vector3.zero;
		_dome.transform.localRotation = Quaternion.identity;
		ApplyRadius();
		_dome.SetActive(false);

		var renderer = _dome.GetComponent<MeshRenderer>();
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
		renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

		Console.WriteLine(
			$"[ActuallyQuantumMoon] Entanglement sky attached: dome radius " +
			$"{moonRadius * Scale:F1} (scale {Scale:F2} of {moonRadius:F1}), " +
			$"transmittance ramp {_ramp.Colours} colours x {_ramp.Rows} rows " +
			$"({_ramp.ThicknessMinNm:F0}-{_ramp.ThicknessMaxNm:F0} nm), {Rings} rings, " +
			$"soap {EntanglementMoonShell.SkySoapAmount:F2}, strength {Strength:F2}.",
			MessageType.Success);
	}

	/// <summary>
	/// Called every frame from <c>EntanglementMoonShell.Tick</c>, with the same camera it
	/// uses, which must be <c>Locator.GetActiveCamera()</c> rather than
	/// <c>Camera.current</c>.
	/// </summary>
	internal static void Tick(Vector3 cameraWorldPos)
	{
		if (_dome == null) return;

		// The player's radius in the moon's own units, which is what _moonRadius is
		// measured in, so the two are directly comparable.
		Transform moon = _dome.transform.parent;
		float playerRadius = moon != null
			? moon.InverseTransformPoint(cameraWorldPos).magnitude
			: (cameraWorldPos - _dome.transform.position).magnitude;

		// Fixed for the life of the scene: Scale is a constant and _moonRadius is measured
		// once at scene load. Beyond it the player has left the film and the shell is what
		// should be visible.
		float domeRadius = _moonRadius * Scale;
		bool under = playerRadius < domeRadius * 0.98f && playerRadius > 1e-3f;

		if (under)
		{
			// Pointed along the player's local up rather than their gaze: the dome is
			// centred on the moon, so the player's zenith is its axis of symmetry. This
			// keeps EntanglementCap's per-ring cos(theta) shortcut exact, and means
			// turning the camera costs nothing - only moving re-points the dome.
			Vector3 up = cameraWorldPos - _dome.transform.position;
			if (up.sqrMagnitude > 1e-6f)
			{
				_dome.transform.rotation = Quaternion.LookRotation(up);
			}

			// cameraInside: true disables the horizon mask. From under the dome every part
			// of it is in front of the viewer and dot(N, toCamera) is negative everywhere,
			// so the mask would zero the whole sky.
			_cap.Refresh(_dome.transform, cameraWorldPos, true, Thickness);
		}

		if (under != _underDome)
		{
			_underDome = under;
			_dome.SetActive(under);
			// Logged in both states, since they are indistinguishable from inside the
			// game.
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Entanglement sky {(under ? "ON" : "OFF")} - " +
				$"player radius {playerRadius:F1}, dome {domeRadius:F1} " +
				$"(scale {Scale:F2} of {_moonRadius:F1}).", MessageType.Info);
			_lastLogged = Time.time;
		}
		else if (under && LogSeconds > 0f && Time.time - _lastLogged >= LogSeconds)
		{
			_lastLogged = Time.time;
			float ratio = playerRadius / Mathf.Max(domeRadius, 1e-3f);
			// cos(theta) at the horizon is sqrt(1 - (d/R)^2); at the zenith it is 1.
			float horizon = Mathf.Sqrt(Mathf.Max(0f, 1f - ratio * ratio));
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Sky: player radius {playerRadius:F1}, dome " +
				$"{domeRadius:F1} ({domeRadius - playerRadius:F1} overhead), d/R " +
				$"{ratio:F2}, cos(theta) {horizon:F2}..1.00, thickness {Thickness:F2}, " +
				$"strength {Strength:F2}, soap " +
				$"{EntanglementMoonShell.SkySoapAmount:F2}.", MessageType.Info);
		}
	}

	private static bool EnsureLoaded()
	{
		if (_loadAttempted) return _material != null;
		_loadAttempted = true;

		string path = Path.Combine(
			ActuallyQuantumMoon.Instance.ModHelper.Manifest.ModFolderPath, RampFileName);
		try
		{
			_ramp = EntanglementRamp.Load(path);
		}
		catch (System.Exception error)
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Entanglement sky DISABLED - could not load " +
				$"{RampFileName}: {error.Message} Reinstall the mod, which " +
				"writes it beside the reflectance ramp.", MessageType.Error);
			return false;
		}

		Shader stock = Shader.Find(AdditiveShader);
		if (stock == null)
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Entanglement sky DISABLED - the game no longer " +
				$"ships '{AdditiveShader}'. See UNITY.md for how to find a replacement from " +
				"inside the game.", MessageType.Error);
			return false;
		}

		_material = new Material(stock) { name = "EntanglementSky" };
		_material.SetTexture("_MainTex", _ramp.Texture);

		// The borrowed shader computes 2 * _TintColor * vertexColour * texture, so halving
		// the tint returns the transmittance unchanged. The factor of 2 is measured; see
		// the LegacyGain note in EntanglementMoonShell.
		//
		// T is not scaled up despite being darker than R (mean 0.18 against 0.51 on this
		// bake). Rendering it at 3x and 5x washes the colour towards white, because T is
		// the more saturated of the two tables and gain destroys that. Unity gain is both
		// the better-looking and the faithful value.
		ApplyTint();

		// Just after the shell's 3100. Additive output is order-independent against other
		// additive draws, but must land after the moon's own alpha-blended fog, or the fog
		// blends grey back over it.
		_material.renderQueue = 3101;
		return true;
	}

	private static void ApplyTint()
	{
		// Halving the tint divides out the borrowed shader's factor of 2; see the call site.
		float tint = Strength / 2f;
		_material.SetColor("_TintColor", new Color(tint, tint, tint, tint));
	}
}
