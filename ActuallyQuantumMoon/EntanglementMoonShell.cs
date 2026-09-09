using System.IO;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Draws the entanglement film on the Quantum Moon as seen from outside.
/// </summary>
/// <remarks>
/// <para>
/// The bake's output is a BSDF rather than a picture; its reference use is
/// <c>BSDF = Reflectance * microfacet("ggx", ...) + Transmittance * transparent()</c>,
/// where R and T are multipliers on light. Pasting an image of R over the moon gives grey
/// mud, because R alone is a near-neutral field of mean 0.53 and the structure lives in
/// the multiplications. This instead evaluates R against the real view geometry.
/// </para>
/// <para>
/// Two renderers draw the moon, and both need the shell or the effect disappears at
/// whichever distance the game switches between them: the moon itself inside
/// <c>ProxyBody.CUTOFF_DISTANCE</c>, and <c>ProxyQuantumMoon._mainRenderer</c> in the
/// proxy scene, at a compressed scale and drawn by a different camera. One material serves
/// both: the angle axis of the lookup is scale-invariant, and the thickness axis is driven
/// by distance in shell radii rather than metres, which the proxy's angular-size-preserving
/// rescale leaves intact. See <see cref="EntanglementCap.DistanceInRadii"/>.
/// </para>
/// <para>
/// There is no custom shader. The reflectance is baked into a 2D ramp texture
/// (<see cref="EntanglementRamp"/>), with viewing angle across and film thickness down,
/// carried to the GPU through mesh UVs on a generated cap
/// (<see cref="EntanglementCap"/>) and drawn with a shader Outer Wilds already ships. A
/// custom fragment shader can only be compiled by the Unity Editor, which this project
/// does not depend on; see UNITY.md.
/// </para>
/// </remarks>
public static class EntanglementMoonShell
{
	private const string RampFileName = "Assets/entanglement_ramp.bin";

	// How long to keep looking for the proxy moon before giving up, in seconds. ProxyBody
	// is an ILateInitializer, so it is absent at scene load.
	private const float ProxySearchTimeout = 30f;

	private static readonly FieldInfo FogRadiusField =
		AccessTools.Field(typeof(QuantumMoon), "_fogRadius");
	private static readonly FieldInfo FogThicknessField =
		AccessTools.Field(typeof(QuantumMoon), "_fogThickness");
	private static readonly FieldInfo FogRolloffField =
		AccessTools.Field(typeof(QuantumMoon), "_fogRolloffDistance");
	private static readonly FieldInfo OuterCloudField =
		AccessTools.Field(typeof(QuantumMoon), "_outerCloudTransform");
	private static readonly FieldInfo ProxyMainRendererField =
		AccessTools.Field(typeof(ProxyQuantumMoon), "_mainRenderer");

	// OnCompleteSceneLoad fires again on every reload, so the ramp and material are
	// process-wide and built once.
	private static Material _material;
	private static bool _loadAttempted;

	private static EntanglementRamp _ramp;
	private static EntanglementCap _capNear;
	private static EntanglementCap _capProxy;

	// Stock shader, confirmed present in Outer Wilds 1.1.16 by enumerating
	// Resources.FindObjectsOfTypeAll<Shader>() in the running game: 278 shaders, this
	// among them, with _MainTex, _TintColor and a single pass. Single-pass matters, since
	// Outer Wilds/Particles/Alpha reports two and would draw the shell twice.
	//
	// Alpha-blended rather than additive. Blend mode is baked into each borrowed shader,
	// neither of which exposes _SrcBlend, so the choice of shader is the choice of look:
	// additive layers the iridescence over a still-visible grey moon.
	// "Legacy Shaders/Particles/Additive" is the counterpart, and swapping this string
	// and the alpha term in ApplyMaterialSettings is the whole change.
	private const string RampShaderAlpha = "Legacy Shaders/Particles/Alpha Blended";

	// The borrowed shaders compute 2 * _TintColor * vertexColour * tex2D(_MainTex, uv),
	// the factor of 2 being an old Unity convention that let a particle tint brighten as
	// well as darken. Dividing it back out puts the shell at exactly the brightness the
	// reference shader produced.
	//
	// Verified in game rather than assumed: at gain 1 the shell is visibly about twice as
	// bright, which is only possible if the shader's factor of 2 is present. This is a
	// correction constant rather than a look parameter - changing it invalidates the
	// brightness claims made in the code and in ENTANGLEMENT_RENDERING.tex.
	private const float LegacyGain = 2f;

	private static GameObject _nearShell;
	private static GameObject _proxyShell;
	private static Transform _moonTransform;

	// The moon's own outer radius, before ShellScale. Reported by LogState alongside the
	// shell radius derived from it, which is how the shell's placement is checked in game.
	private static float _moonRadius;

	// Proxy attachment is retried from Tick: ProxyBody initialises late, and
	// FindObjectOfType would also miss it while its GameObject is inactive.
	private static bool _proxyAttached;
	private static bool _proxyGaveUp;
	private static float _proxySearchStarted;

	// Which side of the near shell the camera is on. The caps point away from the camera
	// when it is inside, which keeps a surface on screen rather than letting the moon snap
	// back to stock grey.
	private static bool _insideShell;

	// Brightness of the iridescence: the reference shader's _Fade dial, at its reference
	// value. Dimming is handled by Opacity.
	private const float Fade = 1f;

	// Multiplier on the measured moon radius, which is the outer radius including fog and
	// outer cloud (see MeasureMoonRadius) rather than the ground.
	//
	// Below 1, so the shell sits inside that envelope and the iridescence is seen through
	// the moon's own grey fog rather than laid on top of it. With a low Opacity, the fog
	// softens the film instead of competing with it.
	//
	// If retuned: the fog reaches 115 (_fogRadius 105 + _fogThickness 5 +
	// _fogRolloffDistance 5), and values that put the shell partway through the fog shell
	// look muddiest. Above about 1.06 the shell sits outside the fog entirely.
	private const float ShellScale = 0.88f;

	// The shell's appearance parameters are constants rather than config keys, so that a
	// player cannot reach the failure modes documented below - a grey shell at high soap
	// amount, a bullseye at zero. Changing the look means editing this file and rebuilding,
	// which requires a game restart because the assembly loads once.
	//
	// Opacity: 1 makes the shell opaque, replacing the grey moon; lower values let the moon
	// and its fog show through. Because ShellScale puts the shell inside the fog, this
	// reads as how strongly the film tints the moon rather than how much of it is hidden.
	private const float Opacity = 0.4f;

	// The soap film is what makes the shell a bubble rather than concentric rings; see
	// SoapFilm for the field and for why a thickness varying with theta alone cannot work.
	//
	// SoapAmount is how far across the ramp's baked thickness range the film swings, and
	// decides whether this is a bubble at all: 0 gives a uniform-thickness shell.
	//
	// The effect reverses past about 0.5. Measured against the shipped bake: at 1.0 with
	// fine detail the field sweeps every fringe order into each screen pixel and the shell
	// turns grey. The same applies to Scale - the swirls must stay large compared with the
	// fringe spacing.
	private const float SoapAmount = 0.3f;

	// The sky's own amount, separate only so that the two can be tuned apart. They are
	// equal because it is one film seen from two sides. The transmittance table is smoother
	// than the reflectance one - 24 turning points along its phase axis against 66 - so the
	// same amount reads as broader, calmer sweeps overhead.
	internal const float SkySoapAmount = 0.3f;
	private const float SoapScale = 3f;
	private const float SoapSwirl = 0.8f;
	private const int SoapDetail = 4;

	// Cube-face resolution for the baked field. 64 is well above what the mesh can carry:
	// 64 faces resolve about 1.4 degrees against the cap's 5.6 degrees of azimuth per
	// segment, so this is not the limiting term. Six faces of 64x64 floats is 96 KB.
	private const int SoapResolution = 64;

	// Selects the field. One arrangement of the swirls is as good as another, and a fixed
	// seed means every screenshot and bug report shows the same moon.
	private const int SoapSeed = 7;

	// Built once on first use, and shared with the sky: there is one film, seen in
	// reflection from outside and in transmission from underneath.
	private static SoapFilm _film;

	internal static SoapFilm Film
	{
		get
		{
			if (_film == null)
			{
				_film = new SoapFilm(SoapResolution, SoapScale, SoapDetail, SoapSwirl, SoapSeed);
			}
			return _film;
		}
	}

	// Where the baked thickness range maps onto camera distance, in shell radii (see
	// EntanglementCap.DistanceInRadii). Near maps to the thickest, busiest row of the ramp
	// and far to the thinnest, calmest one.
	//
	// 1.2 is just outside the shell surface, so the film is busiest on arrival. 48 radii is
	// about 6000 units from a shell of ~124, well before the proxy handoff, so the pattern
	// has finished settling long before the handoff matters.
	private const float ThicknessNearRadii = 1.2f;
	private const float ThicknessFarRadii = 48f;

	private static IModConsole Console => ActuallyQuantumMoon.Instance.ModHelper.Console;

	/// <summary>
	/// Reprints the state line, including the soap fingerprint. There is nothing to
	/// configure, but OWML calls this whenever config.json is saved, which is the only way
	/// to get that line on demand without restarting the game.
	/// </summary>
	public static void Configure()
	{
		ApplyMaterialSettings();
		LogState("config");
	}

	/// <summary>
	/// Prints the shell's current state. Called from Configure as well as scene load,
	/// because the scene-load lines scroll off the top of the OWML console, and the
	/// measured moon radius reported here is the number ShellScale multiplies.
	/// </summary>
	private static void LogState(string reason)
	{
		if (_material == null)
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Entanglement shell ({reason}): NOT LOADED - " +
				"no material. Check for an earlier DISABLED message.", MessageType.Warning);
			return;
		}

		string proxy = _proxyShell != null
			? "attached"
			: (_proxyGaveUp ? "GAVE UP - grey beyond 42 km" : "still searching");

		Console.WriteLine(
			$"[ActuallyQuantumMoon] Entanglement shell ({reason}): " +
			$"moon outer radius {_moonRadius:F1}, shell radius {_moonRadius * ShellScale:F1} " +
			$"(scale {ShellScale:F2}) | ramp {_ramp.Colours} colours x {_ramp.Rows} " +
			$"thickness rows ({_ramp.ThicknessMinNm:F0}-{_ramp.ThicknessMaxNm:F0} nm), " +
			$"distance map {ThicknessNearRadii:F1}-{ThicknessFarRadii:F0} radii, " +
			$"opacity {Opacity:F2} (strength {Fade:F2}, gain {LegacyGain:F2}) | " +
			$"soap film {(SoapAmount > 0f ? $"amount {SoapAmount:F2}, scale {SoapScale:F1}, swirl {SoapSwirl:F1}, detail {SoapDetail}, fingerprint [{Film.Fingerprint()}]" : "OFF - concentric rings")} | " +
			$"near shell {(_nearShell != null ? "yes" : "NO")}, proxy shell {proxy} | " +
			$"sky {(EntanglementSky.Attached ? (EntanglementSky.UnderDome ? "UNDER the dome - transmittance" : "attached, above the dome") : "NOT attached")} | " +
			$"camera {(_insideShell ? "INSIDE" : "outside")} the shell.",
			MessageType.Info);
	}

	public static void OnSceneLoaded()
	{
		_nearShell = null;
		_proxyShell = null;
		_capNear = null;
		_capProxy = null;
		_moonTransform = null;
		_proxyAttached = false;
		_proxyGaveUp = false;
		_proxySearchStarted = Time.time;

		if (!EnsureLoaded()) return;

		QuantumMoon moon = Locator.GetQuantumMoon();
		if (moon == null)
		{
			Console.WriteLine(
				"[ActuallyQuantumMoon] No QuantumMoon in this scene - the entanglement " +
				"shell is not attached.", MessageType.Warning);
			return;
		}

		_moonTransform = moon.transform;
		_moonRadius = MeasureMoonRadius(moon);

		_nearShell = CreateShell("EntanglementShell_Near", moon.transform,
			_moonRadius * 2f * ShellScale, moon.gameObject.layer);

		// The sky is the same film seen in transmission from underneath, and shares this
		// class's SoapFilm; see EntanglementSky.
		EntanglementSky.OnSceneLoaded(moon.transform, _moonRadius, moon.gameObject.layer);

		LogState("scene load");
	}

	/// <summary>
	/// Measures the radius the shell must clear to be visible from outside.
	/// </summary>
	/// <remarks>
	/// <c>_fogRadius</c> alone is not enough: the fog reaches
	/// <c>_fogRadius + _fogThickness + _fogRolloffDistance</c>, and the outer cloud sits
	/// beyond that. The cloud is not a child of the moon and so cannot be found by
	/// GetComponentsInChildren, because <c>QuantumMoon.Awake</c> sets
	/// <c>_outerCloudTransform.parent = null</c> and keeps its position in sync manually;
	/// it is reached through the field instead.
	/// </remarks>
	private static float MeasureMoonRadius(QuantumMoon moon)
	{
		float radius = FogRadiusField != null ? (float)FogRadiusField.GetValue(moon) : 105f;
		if (FogThicknessField != null) radius += (float)FogThicknessField.GetValue(moon);
		if (FogRolloffField != null) radius += (float)FogRolloffField.GetValue(moon);

		var outerCloud = OuterCloudField != null
			? OuterCloudField.GetValue(moon) as Transform : null;
		if (outerCloud != null)
		{
			Vector3 centre = moon.transform.position;
			Renderer[] renderers = outerCloud.GetComponentsInChildren<Renderer>(true);
			for (int i = 0; i < renderers.Length; i++)
			{
				Bounds bounds = renderers[i].bounds;
				// World-space AABB. The largest half-extent rather than its magnitude,
				// which would overstate a sphere's radius by sqrt(3).
				float extent = Mathf.Max(bounds.extents.x,
					Mathf.Max(bounds.extents.y, bounds.extents.z));
				radius = Mathf.Max(radius, (bounds.center - centre).magnitude + extent);
			}

			Console.WriteLine(
				$"[ActuallyQuantumMoon] Moon outer cloud: {renderers.Length} renderer(s), " +
				$"combined radius {radius:F1}.", MessageType.Info);
		}

		return radius;
	}

	/// <summary>
	/// Loads the ramp and builds the material, reporting loudly on failure, since a silent
	/// one is indistinguishable from the mod being switched off. The only asset required is
	/// the baked colour ramp.
	/// </summary>
	private static bool EnsureLoaded()
	{
		if (_material != null) return true;
		if (_loadAttempted) return false;
		_loadAttempted = true;

		string root = ActuallyQuantumMoon.Instance.ModHelper.Manifest.ModFolderPath;
		string rampPath = Path.Combine(root, RampFileName);

		if (!File.Exists(rampPath))
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Colour ramp missing at {rampPath} - the " +
				"entanglement shell is disabled. The mod's Assets folder is incomplete; " +
				"reinstall it.",
				MessageType.Error);
			return false;
		}

		try
		{
			_ramp = EntanglementRamp.Load(rampPath);
		}
		catch (System.Exception e)
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] Could not read {RampFileName} - the entanglement " +
				$"shell is DISABLED. {e.Message}", MessageType.Error);
			return false;
		}

		Console.WriteLine(
			$"[ActuallyQuantumMoon] Entanglement ramp from {RampFileName}: " +
			$"{_ramp.Colours} colours.", MessageType.Info);

		if (!BuildMaterial()) return false;

		// The reference shader carried `Fog { Mode Off }`. A borrowed shader cannot be told
		// that, and the alpha-blended particle shader does apply fog, darkening the shell
		// with distance. Reported once, as it is a likely cause whenever the shell looks
		// dimmer than expected.
		Console.WriteLine(
			$"[ActuallyQuantumMoon] Scene fog is {(RenderSettings.fog ? "ON" : "off")}" +
			(RenderSettings.fog
				? $" (mode {RenderSettings.fogMode}, colour {RenderSettings.fogColor}). The " +
				  "borrowed shader applies it and the reference shader did not, so this " +
				  "can dim the shell with distance."
				: ". So fog is not dimming the shell; brightness is down to " +
				  "LegacyGain and the baked ramp."),
			MessageType.Info);

		ApplyMaterialSettings();
		return true;
	}

	/// <summary>
	/// Builds the material: a shader the game already ships, handed the baked ramp as its
	/// main texture.
	/// </summary>
	private static bool BuildMaterial()
	{
		Shader stock = Shader.Find(RampShaderAlpha);
		if (stock == null)
		{
			Console.WriteLine(
				$"[ActuallyQuantumMoon] '{RampShaderAlpha}' is not in this build of the " +
				"game, so there is nothing to draw the shell with - it is DISABLED. " +
				"It was confirmed present in 1.1.16; if the game has been updated, " +
				"re-run a shader enumeration (see UNITY.md).",
				MessageType.Error);
			return false;
		}

		_material = new Material(stock) { name = "EntanglementRamp" };
		_material.SetTexture("_MainTex", _ramp.Texture);
		return true;
	}

	/// <summary>
	/// Applies the tint and render queue. Everything a custom shader would have exposed as
	/// a property is either baked into the ramp (thickness and the optics), carried by the
	/// material tint (strength, opacity), decided by which shader is borrowed (blend mode),
	/// or handled by the mesh (cull, horizon).
	/// </summary>
	private static void ApplyMaterialSettings()
	{
		if (_material == null) return;

		// The 2x applies to alpha as well as colour, and alpha is not decorative here:
		// alpha-blended is Blend SrcAlpha OneMinusSrcAlpha, so the result is multiplied by
		// it. Dividing the gain back out lands the alpha at exactly _Opacity.
		//
		const float Tint = Fade / LegacyGain;
		_material.SetColor("_TintColor",
			new Color(Tint, Tint, Tint, Opacity / LegacyGain));

		// Transparent + 100. Both the shell and the moon's fog sphere live in the
		// Transparent queue, and Unity sorts transparent renderers by distance to their
		// bounds centre, which for these two is the same point. That tie can hand the fog
		// the later draw and put grey back over an opaque shell; an explicit queue removes
		// the ambiguity.
		_material.renderQueue = 3100;
	}

	/// <summary>
	/// Attaches a shell to the proxy moon, which draws the moon past
	/// <c>ProxyBody.CUTOFF_DISTANCE</c> as a separate object on its own layer, rendered by
	/// its own camera. Its transform is heavily scaled down, so the shell is sized from the
	/// renderer's own mesh bounds in local space rather than from the moon's world radius.
	/// </summary>
	private static bool TryAttachProxyShell()
	{
		if (ProxyMainRendererField == null) return false;

		// Resources.FindObjectsOfTypeAll rather than FindObjectOfType: ProxyBody.Start calls
		// ToggleRendering(false) and the proxy stays inactive until the player is far
		// enough away, and FindObjectOfType skips inactive objects.
		ProxyQuantumMoon[] found = Resources.FindObjectsOfTypeAll<ProxyQuantumMoon>();
		for (int i = 0; i < found.Length; i++)
		{
			ProxyQuantumMoon proxy = found[i];
			// Prefabs and other non-scene objects also come back from
			// FindObjectsOfTypeAll; a scene object has a valid scene handle.
			if (proxy == null || !proxy.gameObject.scene.IsValid()) continue;

			var renderer = ProxyMainRendererField.GetValue(proxy) as MeshRenderer;
			if (renderer == null) continue;

			var filter = renderer.GetComponent<MeshFilter>();
			if (filter == null || filter.sharedMesh == null) continue;

			Vector3 size = filter.sharedMesh.bounds.size;
			float diameter = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * ShellScale;

			// Layer copied from the renderer: the proxy camera has a culling mask, and a
			// shell on the wrong layer is never drawn.
			_proxyShell = CreateShell("EntanglementShell_Proxy", renderer.transform,
				diameter, renderer.gameObject.layer);

			// Mirror the proxy's current visibility immediately; the Harmony patch below
			// keeps it in step from here on.
			_proxyShell.SetActive(renderer.enabled);

			Console.WriteLine(
				$"[ActuallyQuantumMoon] Entanglement proxy shell attached to " +
				$"'{renderer.name}' (mesh diameter {diameter:F3}, layer " +
				$"{renderer.gameObject.layer}). The effect now survives past 42 km.",
				MessageType.Success);
			return true;
		}

		return false;
	}

	private static GameObject CreateShell(string name, Transform parent, float diameter, int layer)
	{
		// Generated rather than taken from Unity's primitive sphere, which has no collider
		// to strip and whose rings are uniform in angle - the wrong distribution for a ramp
		// indexed by cosTheta.
		var shell = new GameObject(name);
		var cap = new EntanglementCap(_ramp.VInset, Film, SoapAmount, EntanglementCap.ShellRings);
		shell.AddComponent<MeshFilter>().sharedMesh = cap.Mesh;
		shell.AddComponent<MeshRenderer>();

		// Which cap this is decides how it gets pointed at the camera in Tick.
		if (name.EndsWith("Proxy")) _capProxy = cap; else _capNear = cap;

		shell.layer = layer;
		shell.transform.SetParent(parent, false);
		shell.transform.localPosition = Vector3.zero;
		shell.transform.localRotation = Quaternion.identity;
		// EntanglementCap is built at radius 0.5, so localScale is a diameter.
		shell.transform.localScale = Vector3.one * diameter;

		var renderer = shell.GetComponent<MeshRenderer>();
		renderer.sharedMaterial = _material;
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
		renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
		return shell;
	}

	public static void Tick()
	{
		if (_material == null || _moonTransform == null) return;

		// Keep trying for the proxy moon until it exists: one array scan per frame for at
		// most ProxySearchTimeout seconds, then it stops.
		if (!_proxyAttached && !_proxyGaveUp)
		{
			if (TryAttachProxyShell())
			{
				_proxyAttached = true;
			}
			else if (Time.time - _proxySearchStarted > ProxySearchTimeout)
			{
				_proxyGaveUp = true;
				Console.WriteLine(
					"[ActuallyQuantumMoon] No ProxyQuantumMoon found after " +
					$"{ProxySearchTimeout:F0}s - the entanglement shell will VANISH beyond " +
					"42 km (ProxyBody.CUTOFF_DISTANCE). The near shell still works.",
					MessageType.Warning);
			}
		}

		// The player's camera, not Camera.current. Camera.current is only meaningful inside
		// a render callback; read from Update it returns whichever camera rendered last,
		// and Outer Wilds has many - the player camera, the proxy camera, the map camera,
		// ship monitors, the scout's probe camera - some of which render only
		// intermittently.
		//
		// This matters once the soap film exists. Pointing the cap at a slightly different
		// position only rotates it, which is invisible on a rotationally symmetric
		// bullseye, but the film has a top and a bottom, so the same wobble moves the crown
		// and the pattern visibly jumps whenever a secondary camera takes a turn.
		//
		// Locator.GetActiveCamera is what ProxyBody.Update itself uses to place the proxy,
		// so this also keeps the shell and the proxy moon agreeing on the viewer's
		// position.
		OWCamera active = Locator.GetActiveCamera();
		Transform viewer = active != null ? active.transform
			: (Camera.main != null ? Camera.main.transform : null);
		if (viewer == null) return;

		UpdateInsideFlag(viewer.position);
		UpdateCaps(viewer.position);
		EntanglementSky.Tick(viewer.position);
	}

	/// <summary>
	/// Points each cap at the camera and rewrites its cosTheta UVs.
	/// </summary>
	/// <remarks>
	/// The rotation is what makes a half-sphere sufficient: spinning a sphere about its
	/// centre is geometrically invisible, so the cap can always face the viewer and still
	/// look like part of the same shell. Inside the shell it faces away instead, keeping a
	/// surface on screen so the moon does not snap back to stock grey when the camera
	/// crosses the shell radius. A custom shader would do this by flipping its cull mode;
	/// here the geometry does it, which is what allows a borrowed Cull Off shader.
	/// </remarks>
	private static void UpdateCaps(Vector3 cameraPos)
	{

		if (_nearShell != null && _capNear != null)
		{
			PointCapAt(_nearShell.transform, cameraPos, _insideShell);
			_capNear.Refresh(_nearShell.transform, cameraPos, _insideShell,
				ThicknessFractionFor(
					EntanglementCap.DistanceInRadii(_nearShell.transform, cameraPos)));
		}

		// The proxy is a small stand-in placed near the camera along the true direction to
		// the moon, on the same layer and drawn by the same camera, so one camera position
		// serves both. Its compressed scale cancels in InverseTransformPoint, and the
		// lookup depends only on a scale-invariant angle. It is never treated as inside,
		// since the proxy exists only beyond the cutoff distance.
		if (_proxyShell != null && _capProxy != null)
		{
			PointCapAt(_proxyShell.transform, cameraPos, false);
			_capProxy.Refresh(_proxyShell.transform, cameraPos, false,
				ThicknessFractionFor(
					EntanglementCap.DistanceInRadii(_proxyShell.transform, cameraPos)));
		}
	}

	/// <summary>
	/// Maps camera distance, in shell radii, to a position in the baked thickness range,
	/// where 0 is the thinnest row of the ramp and 1 the thickest.
	/// </summary>
	/// <remarks>
	/// Logarithmic, because distance to a planet is a ratio quantity: halving the distance
	/// should be a fixed step wherever it starts, and a linear map would spend almost its
	/// whole range in the last seconds of an approach and sit pinned at one end otherwise.
	/// Thickest up close, since more thickness means more and tighter bands - measured at
	/// 2-3 broad rings at 300 nm against 9 tight ones at 1400 nm - and a distant moon covers
	/// few screen pixels, where the finest fringes would alias into shimmer.
	/// </remarks>
	private static float ThicknessFractionFor(float radii)
	{
		const float near = ThicknessNearRadii;
		const float far = ThicknessFarRadii;
		float t = Mathf.InverseLerp(
			Mathf.Log(near), Mathf.Log(far), Mathf.Log(Mathf.Max(radii, 1e-3f)));
		return 1f - t;
	}

	private static void PointCapAt(Transform cap, Vector3 cameraPos, bool inside)
	{
		Vector3 direction = cameraPos - cap.position;
		if (inside) direction = -direction;
		if (direction.sqrMagnitude < 1e-6f) return;
		cap.rotation = Quaternion.LookRotation(direction);
	}

	/// <summary>
	/// Which side of the near shell the camera is on, which decides which way the cap
	/// faces. Only the near shell is affected: the proxy moon is not drawn until
	/// <c>ProxyBody.CUTOFF_DISTANCE</c>, far outside this shell's radius.
	/// </summary>
	private static void UpdateInsideFlag(Vector3 cameraPos)
	{
		if (_nearShell == null) return;

		// lossyScale rather than the configured radius: the shell inherits the moon's
		// transform, so this is the radius actually on screen.
		float shellRadius = _nearShell.transform.lossyScale.x * 0.5f;
		float distance = Vector3.Distance(cameraPos, _moonTransform.position);

		// A small hysteresis band, so that a camera sitting on the boundary cannot flip the
		// mode every frame.
		const float Hysteresis = 1.5f;
		if (distance > shellRadius + Hysteresis) _insideShell = false;
		else if (distance < shellRadius - Hysteresis) _insideShell = true;
	}

	/// <summary>
	/// Keeps the proxy shell exactly as visible as the proxy moon. Without this the shell
	/// outlives the moon's disappearance, leaving a halo where the moon is not.
	/// </summary>
	[HarmonyPatch(typeof(ProxyQuantumMoon), nameof(ProxyQuantumMoon.ToggleRendering))]
	private static class ToggleRendering_Patch
	{
		private static void Postfix(bool on)
		{
			if (_proxyShell != null) _proxyShell.SetActive(on);
		}
	}
}
