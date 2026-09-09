using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using System.Reflection;

namespace ActuallyQuantumMoon;

/// <summary>
/// Entry point for the mod. Owns the OWML lifecycle and forwards scene loads,
/// per-frame ticks and configuration changes to the individual effects.
/// </summary>
public class ActuallyQuantumMoon : ModBehaviour
{
	public static ActuallyQuantumMoon Instance;

	public void Awake()
	{
		// OWML's mod helper is not available until Start.
		Instance = this;
	}

	public void Start()
	{
		ModHelper.Console.WriteLine($"{nameof(ActuallyQuantumMoon)} loaded.", MessageType.Success);

		new Harmony("MothQuantum.ActuallyQuantumMoon").PatchAll(Assembly.GetExecutingAssembly());

		OnCompleteSceneLoad(OWScene.TitleScreen, OWScene.TitleScreen);
		LoadManager.OnCompleteSceneLoad += OnCompleteSceneLoad;
	}

	/// <summary>
	/// Applies the player-facing configuration. OWML calls this at startup and again
	/// whenever config.json changes on disk, so these values are tunable without
	/// restarting the game. Code changes still require a restart, as the assembly is
	/// loaded once.
	/// </summary>
	/// <remarks>
	/// Only the values a player is likely to want to change are exposed here; the rest
	/// of the effect parameters are constants on the individual effect classes. The
	/// mod's on/off switch is OWML's own <c>enabled</c> key.
	/// </remarks>
	public override void Configure(IModConfig config)
	{
		base.Configure(config);

		// Each fallback duplicates a value in default-config.json. OWML copies that
		// file only when no config.json exists and never merges new keys into an
		// existing one, so a player upgrading the mod can be missing a key entirely.
		// Both copies must be changed together.
		GlitchTone.SetVolume(SettingOr(config, "glitchToneVolume", GlitchTone.DefaultVolume));
		QuantumDecoherenceFlash.SetTickSeconds(SettingOr(
			config, "glitchTickSeconds", QuantumDecoherenceFlash.DefaultTickSeconds));
		EntanglementMoonShell.Configure();

		// Drop the cached clips and measured bits so that regenerating an asset and
		// then touching config.json picks up the new one without a restart.
		GlitchTone.ReloadClip();
		GlitchBits.Reload();
	}

	/// <summary>
	/// Reads a float setting, returning <paramref name="fallback"/> if the key is
	/// absent or cannot be converted to a number.
	/// </summary>
	private static float SettingOr(IModConfig config, string name, float fallback)
	{
		if (config.Settings == null || !config.Settings.ContainsKey(name)) return fallback;

		// ContainsKey only proves the key exists. A hand-edited config.json can hold a
		// string or a bool where a number belongs, and the conversion then throws.
		// Configure must not propagate that, or the remaining effects never load.
		try
		{
			return config.GetSettingsValue<float>(name);
		}
		catch (System.Exception error)
		{
			// Instance can still be null: OWML may call Configure before the behaviour
			// is up, which is the other reason this must not throw.
			Instance?.ModHelper?.Console?.WriteLine(
				$"[ActuallyQuantumMoon] config.json key '{name}' could not be read as a " +
				$"number ({error.GetType().Name}), using {fallback}. Delete config.json to " +
				"have OWML write a fresh one from default-config.json.",
				MessageType.Warning);
			return fallback;
		}
	}

	public void OnCompleteSceneLoad(OWScene previousScene, OWScene newScene)
	{
		if (newScene != OWScene.SolarSystem) return;

		QuantumDecoherenceFlash.OnSceneLoaded();
		// GlitchTone has nothing to reset: its clips and its AudioSource both outlive the
		// scene. See EnsureSource.
		GlitchBits.OnSceneLoaded();
		EntanglementMoonShell.OnSceneLoaded();
	}

	public void Update()
	{
		QuantumDecoherenceFlash.Tick();
		EntanglementMoonShell.Tick();
	}
}
