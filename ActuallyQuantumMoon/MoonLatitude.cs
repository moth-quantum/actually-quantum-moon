using UnityEngine;

namespace ActuallyQuantumMoon;

/// <summary>
/// Reads the player's position on the Quantum Moon as a Bloch-sphere polar angle.
/// Shared by every effect that scales with latitude, so that they always agree on
/// where the player is standing.
/// </summary>
internal static class MoonLatitude
{
	/// <summary>
	/// Strength used when the player's transform cannot be found, chosen so that the
	/// effect degrades to something visible rather than to nothing.
	/// </summary>
	public const float FallbackStrength = 0.10f;

	/// <summary>
	/// Returns theta/pi, where theta is the angle from the moon's south pole: 0 at the
	/// south pole, 1 at the north pole. Longitude is ignored.
	/// </summary>
	/// <param name="angleDegrees">
	/// The same angle in degrees, or -1 if it could not be determined.
	/// </param>
	/// <remarks>
	/// The south pole is taken as <c>-transform.up</c>, i.e. the moon transform's own
	/// local -Y. That is this mod's convention rather than something the game states,
	/// but it is the same axis the game's own moon-centred
	/// <c>QuantumMoonEyeFluidVolume</c> uses as its pole, so north and south here agree
	/// with the direction the Eye's currents push towards.
	/// </remarks>
	public static float GetBlurStrength(QuantumMoon moon, out float angleDegrees)
	{
		angleDegrees = -1f;

		Transform playerTransform = Locator.GetPlayerTransform();
		if (playerTransform == null) return FallbackStrength;

		Transform moonTransform = moon.transform;
		Vector3 toPlayer = playerTransform.position - moonTransform.position;
		if (toPlayer.sqrMagnitude < 0.0001f) return FallbackStrength;

		Vector3 southPole = -moonTransform.up;
		angleDegrees = Vector3.Angle(southPole, toPlayer.normalized);
		return angleDegrees / 180f;
	}
}
