using HarmonyLib;
using OWML.Common;
using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Blurs any scout photo taken while the player is inside the Quantum Moon, using the
/// QuantumBlur algorithm in <see cref="QuantumBlur"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProbeCamera.TakeSnapshot</c> renders into a shared 512x512 RenderTexture and
/// returns it; every UI element that displays the photo reads from that same texture.
/// Patching the point where the pixels are produced therefore covers all of them at
/// once, without hooking the display code separately.
/// </para>
/// <para>
/// The blur runs at <see cref="ProcessResolution"/> rather than the native 512, because
/// the statevector holds one amplitude per pixel and a rotation is applied per qubit, so
/// the cost goes as size^2 * log(size) - halving the edge is a 4.5x saving, measured
/// below. The resulting upscale softens
/// every photo slightly, including at the south pole where the quantum step is exactly
/// the identity.
/// </para>
/// </remarks>
public static class ScoutQuantumBlur
{
	/// <summary>
	/// Resolution the blur runs at. The snapshot is scaled down to this, blurred, and
	/// scaled back up. Measured at roughly 4.5x cheaper than the native 512.
	/// </summary>
	private const int ProcessResolution = 256;

	[HarmonyPatch(typeof(ProbeCamera), "TakeSnapshot")]
	private static class TakeSnapshot_Patch
	{
		private static void Postfix(RenderTexture __result)
		{
			if (__result == null) return;

			QuantumMoon moon = Locator.GetQuantumMoon();
			if (moon == null || !moon.IsPlayerInside()) return;

			float xi = MoonLatitude.GetBlurStrength(moon, out float angleDegrees);

			// The angle is logged alongside xi because an unblurred photo has two
			// distinct causes - the blur not running, and the player being too close to
			// the south pole for xi to matter - and only the angle separates them.
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] Snapshot inside the moon: {angleDegrees:F1} deg from the south pole " +
				$"-> xi = {xi:F3} (0 = south pole, 1 = north pole)",
				MessageType.Info);

			ApplyQuantumBlur(__result, xi);
		}
	}

	private static void ApplyQuantumBlur(RenderTexture snapshot, float xi)
	{
		Texture2D working = null;
		RenderTexture downsampled = RenderTexture.GetTemporary(
			ProcessResolution, ProcessResolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

		try
		{
			// GPU-downsample the native render into the processing resolution.
			Graphics.Blit(snapshot, downsampled);

			RenderTexture previousActive = RenderTexture.active;
			RenderTexture.active = downsampled;
			working = new Texture2D(ProcessResolution, ProcessResolution, TextureFormat.RGBA32, false);
			working.ReadPixels(new Rect(0, 0, ProcessResolution, ProcessResolution), 0, 0);
			working.Apply();
			RenderTexture.active = previousActive;

			Color32[] pixels = working.GetPixels32();
			QuantumBlur.Apply(pixels, ProcessResolution, xi);
			working.SetPixels32(pixels);
			working.Apply();

			// Blit back into the original texture, overwriting the snapshot that every
			// other consumer reads from.
			Graphics.Blit(working, snapshot);
		}
		catch (System.Exception e)
		{
			// An unblurred photo is indistinguishable from the effect being disabled, so
			// report the outcome rather than only the exception.
			ActuallyQuantumMoon.Instance.ModHelper.Console.WriteLine(
				$"[ActuallyQuantumMoon] Quantum blur failed - this photo is unblurred. {e}",
				MessageType.Error);
		}
		finally
		{
			RenderTexture.ReleaseTemporary(downsampled);
			if (working != null) UnityEngine.Object.Destroy(working);
		}
	}
}
