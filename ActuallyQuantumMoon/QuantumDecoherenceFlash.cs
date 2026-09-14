using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Briefly decoheres the world while the player is aboard the Quantum Moon: a frame is
/// captured, run through the same quantum blur as the scout photos, and laid back over
/// the live view for a fraction of a second.
/// </summary>
/// <remarks>
/// <para>
/// Both the strength of the blur and how often it happens are driven by latitude from
/// <see cref="MoonLatitude"/>. Near the south pole the flash is rare and effectively
/// invisible, growing more frequent and more violent further north.
/// </para>
/// <para>
/// The timing comes from measurement, not from a chosen curve. Every
/// <see cref="TickSeconds"/> this asks <see cref="GlitchBits"/> for the next outcome
/// recorded for the player's latitude band on quantum hardware and flashes on a 1, which
/// produces a rate of <c>sin^2(theta/2)</c>. The interpolated timer below is the fallback
/// for an install that ships no measured bits.
/// </para>
/// <para>
/// The blur runs once per flash rather than every frame, so its cost is the same one-off
/// hitch as taking a photo. The captured frame is drawn over the live view at an opacity
/// that scales with latitude rather than replacing it, so at <c>xi = 0</c> the overlay is
/// fully transparent and the player sees the untouched game.
/// </para>
/// </remarks>
public static class QuantumDecoherenceFlash
{
	// Resolution the frame is captured at before being stretched back over the screen.
	// The circuit itself runs on QuantumBlurTransfer's much smaller grid whatever this
	// is, so this sets the detail of the overlay rather than the cost of the quantum
	// step; capturing a whole widescreen frame instead would multiply the per-pixel work
	// by eight for detail that is on screen for a third of a second.
	private const int CaptureResolution = 512;

	// Duration of a single flash: long enough to register, short enough to read as a
	// glitch rather than as a stutter.
	private const float FlashDuration = 0.3f;

	// Seconds between flashes, interpolated by latitude. Used only when no measurement
	// bits are available.
	private const float IntervalAtSouthPole = 8f;
	private const float IntervalAtNorthPole = 3f;

	/// <summary>
	/// Seconds between draws from the measured bit stream, from config.json's
	/// <c>glitchTickSeconds</c>. Latitude does not enter here: the interval is fixed and
	/// the outcome varies, which is the difference between this and the fallback timer.
	/// </summary>
	/// <remarks>
	/// At three seconds, against a northernmost band that measures 1 about 97% of the
	/// time and a southernmost that measures 1 about 2% of the time, the far north is
	/// nearly continuous decoherence and the far south is one flash in two minutes.
	/// Changing this stretches both ends by the same factor and leaves their ratio, which
	/// is the hardware's, unchanged. Duplicated in default-config.json; the two must be
	/// kept equal.
	/// </remarks>
	public const float DefaultTickSeconds = 3f;

	private static float _tickSeconds = DefaultTickSeconds;

	// How fast the overlay's opacity ramps with latitude. Opacity and blur strength are
	// both driven by xi, so tying them 1:1 multiplies two small numbers and leaves the
	// result invisible - at the equator, a 29% blur shown at 50% opacity. Ramping opacity
	// faster puts the visible effect under the blur's control. Full opacity is reached at
	// xi = 1/gain, around 72 degrees, and xi near the south pole still gives a transparent
	// overlay.
	private const float OpacityRampGain = 2.5f;

	private static QuantumDecoherenceFlashEffect _effect;

	// When the next decision is due: a draw from the bit stream, or a flash outright on
	// the fallback timer. Not the time of the next flash, since most draws return 0.
	private static float _nextDrawTime;
	private static bool _wasInside;

	// When the current pause began, or -1 while the game is running. Everything here is
	// timed off Time.unscaledTime, which ignores Time.timeScale and so keeps running
	// through a pause; see Tick.
	private static float _pauseStartTime = -1f;

	public static void OnSceneLoaded()
	{
		// The old camera, and the component attached to it, are gone with the scene.
		_effect = null;
		_nextDrawTime = 0f;
		_wasInside = false;
		_pauseStartTime = -1f;
	}

	public static void Tick()
	{
		// Update keeps being called while the game is paused and this uses the unscaled
		// clock, so without an explicit pause check the world would decohere behind the
		// pause menu, and a long pause would bank up a flash that fired on resume.
		if (OWTime.IsPaused())
		{
			if (_pauseStartTime < 0f)
			{
				_pauseStartTime = Time.unscaledTime;

				// Cut a flash already on screen: its fade runs on the same unscaled
				// clock, so it would otherwise fade out beneath the menu.
				if (_effect != null) _effect.Cancel();
				GlitchTone.Stop();
			}
			return;
		}

		if (_pauseStartTime >= 0f)
		{
			// Push the schedule forward by the length of the pause, so the player gets
			// the remainder of their interval rather than an immediate flash.
			_nextDrawTime += Time.unscaledTime - _pauseStartTime;
			_pauseStartTime = -1f;
		}

		QuantumMoon moon = Locator.GetQuantumMoon();
		if (moon == null || !moon.IsPlayerInside() || IsAtTheEye(moon))
		{
			_wasInside = false;
			return;
		}

		float xi = MoonLatitude.GetBlurStrength(moon, out float angleDegrees);

		// Wait out a full interval on arrival rather than firing immediately.
		if (!_wasInside)
		{
			_wasInside = true;

			// A fresh order for every visit. Here rather than in OnSceneLoaded alone,
			// because leaving the moon and returning is also an arrival.
			GlitchBits.OnEnteredMoon();

			_nextDrawTime = Time.unscaledTime + GapFor(xi);
			return;
		}

		if (Time.unscaledTime < _nextDrawTime) return;

		// Schedule the next decision before acting on this one: every return below this
		// point must leave the clock advanced, or a band that keeps measuring 0 would be
		// re-examined on every frame instead of every tick.
		bool measured = GlitchBits.TryDraw(xi, out bool glitch, out int band);
		_nextDrawTime = Time.unscaledTime + GapFor(xi);

		// A measured 0 is the moon staying coherent.
		if (measured && !glitch) return;

		QuantumDecoherenceFlashEffect effect = EnsureEffect();
		if (effect == null) return;

		effect.Trigger(xi, CaptureResolution, FlashDuration, Mathf.Clamp01(xi * OpacityRampGain));

		// The tone accompanies the picture so the two read as one event. It layers over
		// the game's ambience rather than ducking it.
		GlitchTone.Play(xi);

		string source = measured
			? $"measured 1 in band {band + 1}/{GlitchBits.BandCount} on {GlitchBits.Backend}"
			: "no bits shipped, fallback timer";
		ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
			$"[ActuallyQuantumMoon] Decoherence flash at {angleDegrees:F0} deg " +
			$"(xi = {xi:F3}, {source}, opacity {Mathf.Clamp01(xi * OpacityRampGain):F2}, " +
			$"next draw in {GapFor(xi):F1}s)",
			MessageType.Info);
	}

	/// <summary>
	/// Whether the player is at the Eye, in either sense: the moon's sixth state, where
	/// the player stands on the Eye's own surface, or the Eye of the Universe scene.
	/// </summary>
	private static bool IsAtTheEye(QuantumMoon moon)
	{
		if (moon.GetStateIndex() == QuantumMoon.EYE_INDEX) return true;

		EyeStateManager eyeStateManager = Locator.GetEyeStateManager();
		return eyeStateManager != null && eyeStateManager.IsInsideTheEye();
	}

	public static void SetTickSeconds(float seconds)
	{
		// A tick of zero would draw a bit per frame, exhausting in seconds a stream meant
		// to last hours. The floor still allows deliberately frantic settings.
		_tickSeconds = Mathf.Clamp(seconds, 0.25f, 60f);
	}

	/// <summary>
	/// Seconds until the next decision. With measured bits the gap is fixed and the
	/// outcome varies; without them the gap itself carries the latitude.
	/// </summary>
	private static float GapFor(float xi)
	{
		return GlitchBits.IsAvailable ? _tickSeconds : IntervalFor(xi);
	}

	private static float IntervalFor(float xi)
	{
		return Mathf.Lerp(IntervalAtSouthPole, IntervalAtNorthPole, Mathf.Clamp01(xi));
	}

	private static QuantumDecoherenceFlashEffect EnsureEffect()
	{
		if (_effect != null) return _effect;

		OWCamera playerCamera = Locator.GetPlayerCamera();
		if (playerCamera == null || playerCamera.mainCamera == null) return null;

		// Explicit null check rather than ??, since Unity objects override equality
		// against null.
		QuantumDecoherenceFlashEffect existing =
			playerCamera.mainCamera.GetComponent<QuantumDecoherenceFlashEffect>();
		_effect = existing != null
			? existing
			: playerCamera.mainCamera.gameObject.AddComponent<QuantumDecoherenceFlashEffect>();

		return _effect;
	}
}

/// <summary>
/// Camera component that captures and composites a single flash. Lives on the player
/// camera and stays disabled between flashes, so it costs nothing while idle. A flash
/// enables it for <c>FlashDuration</c>: OnRenderImage captures one frame and otherwise
/// passes the image through untouched, and OnGUI draws the blurred capture back over the
/// screen.
/// </summary>
public class QuantumDecoherenceFlashEffect : MonoBehaviour
{
	private static bool _loggedFirstCapture;

	private Texture2D _frame;
	private bool _captureRequested;
	private bool _showing;
	private float _xi;
	private float _peakOpacity;
	private int _resolution;
	private float _duration;
	private float _startTime;

	private void Awake()
	{
		enabled = false;
	}

	private void OnDestroy()
	{
		ReleaseFrame();
	}

	/// <summary>
	/// Ends a flash early, whether it is on screen or still waiting on its capture. Used
	/// when the game pauses mid-flash.
	/// </summary>
	public void Cancel()
	{
		_captureRequested = false;
		EndFlash();
	}

	public void Trigger(float xi, int resolution, float duration, float peakOpacity)
	{
		_xi = Mathf.Clamp01(xi);
		_peakOpacity = Mathf.Clamp01(peakOpacity);
		_resolution = resolution;
		_duration = duration;
		_captureRequested = true;
		enabled = true;
	}

	// The live image is never altered: the capture is taken and passed through unchanged,
	// and the blurred version is composited separately in OnGUI.
	private void OnRenderImage(RenderTexture source, RenderTexture destination)
	{
		if (_captureRequested)
		{
			_captureRequested = false;
			if (CaptureAndBlur(source))
			{
				_showing = true;
				_startTime = Time.unscaledTime;
			}
			else
			{
				enabled = false;
			}
		}

		Graphics.Blit(source, destination);
	}

	private void OnGUI()
	{
		if (!_showing || Event.current.type != EventType.Repaint) return;
		if (_frame == null)
		{
			EndFlash();
			return;
		}

		float t = (Time.unscaledTime - _startTime) / _duration;
		if (t >= 1f)
		{
			EndFlash();
			return;
		}

		// Opacity peaks mid-flash and fades at both ends so the overlay never pops. The
		// peak scales with latitude, keeping a south-pole flash invisible while a
		// northern one fully replaces the view.
		float alpha = _peakOpacity * Mathf.Sin(t * Mathf.PI);

		Color previousColor = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, alpha);
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _frame, ScaleMode.StretchToFill);
		GUI.color = previousColor;
	}

	// OnGUI only runs while something is drawing, so the flash is also ended from Update
	// to stop the overlay sticking.
	private void Update()
	{
		if (_showing && Time.unscaledTime - _startTime >= _duration) EndFlash();
	}

	private bool CaptureAndBlur(RenderTexture source)
	{
		RenderTexture downsampled = RenderTexture.GetTemporary(
			_resolution, _resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

		try
		{
			// Squashing a widescreen frame into a square loses more horizontal detail
			// than vertical, but stretching it back on the way out restores the aspect
			// ratio.
			Graphics.Blit(source, downsampled);

			RenderTexture previousActive = RenderTexture.active;
			RenderTexture.active = downsampled;
			ReleaseFrame();
			_frame = new Texture2D(_resolution, _resolution, TextureFormat.RGBA32, false);
			_frame.ReadPixels(new Rect(0, 0, _resolution, _resolution), 0, 0);
			_frame.Apply();
			RenderTexture.active = previousActive;

			Color32[] pixels = _frame.GetPixels32();
			QuantumBlurTransfer.Apply(pixels, _resolution, _resolution, _xi);
			_frame.SetPixels32(pixels);
			_frame.Apply();

			// Logged once. If the trigger line appears but this does not, the capture is
			// at fault, meaning OnRenderImage is not reaching this camera; if both appear
			// and nothing is visible, the fault is in the overlay.
			if (!_loggedFirstCapture)
			{
				_loggedFirstCapture = true;
				ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
					$"[ActuallyQuantumMoon] Decoherence capture OK ({_resolution}px, " +
					$"peak opacity {_peakOpacity:F2}) - overlay drawing from now on.",
					MessageType.Success);
			}

			return true;
		}
		catch (System.Exception e)
		{
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] Decoherence flash FAILED - no flash this time. {e}",
				MessageType.Error);
			ReleaseFrame();
			return false;
		}
		finally
		{
			RenderTexture.ReleaseTemporary(downsampled);
		}
	}

	private void EndFlash()
	{
		_showing = false;
		ReleaseFrame();
		enabled = false;
	}

	private void ReleaseFrame()
	{
		if (_frame == null) return;
		Destroy(_frame);
		_frame = null;
	}
}
