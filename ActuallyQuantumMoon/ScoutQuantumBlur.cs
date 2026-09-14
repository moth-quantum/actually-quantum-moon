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
/// The circuit cannot be run at the snapshot's own resolution - 512 costs around 65 ms
/// against 2 ms for 128 - so it runs on a small grid and
/// <see cref="QuantumBlurTransfer"/> carries the result back onto the full-resolution
/// photo. The photo itself is never resampled, so at the south pole, where the quantum
/// step is the identity, the snapshot comes back exactly as it was rendered.
/// </para>
/// </remarks>
public static class ScoutQuantumBlur
{
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
		int width = snapshot.width, height = snapshot.height;

		try
		{
			// Read the snapshot at its own resolution: the grid the circuit runs on is
			// sampled from these pixels on the CPU, so there is no downsampled render
			// target any more.
			RenderTexture previousActive = RenderTexture.active;
			RenderTexture.active = snapshot;
			working = new Texture2D(width, height, TextureFormat.RGBA32, false);
			working.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			working.Apply();
			RenderTexture.active = previousActive;

			Color32[] pixels = working.GetPixels32();
			QuantumBlurTransfer.Apply(pixels, width, height, xi);
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
			if (working != null) UnityEngine.Object.Destroy(working);
		}
	}
}
