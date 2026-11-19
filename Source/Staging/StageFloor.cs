using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>
    /// Floor heights near the player. The terrain height is wrong inside rooms built far above it (Endgame rooms sit
    /// near y 5,000 over terrain at sea level), so points are dropped onto whatever solid surface is under them,
    /// measured from a height close to where the player is.
    /// </summary>
    internal static class StageFloor
    {
        private static readonly int SurfaceMask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default", "Default_small", "vehicle");

        /// <summary>The highest solid surface under (x, z) from <paramref name="fromY"/> + 3 m down 40 m, or null.</summary>
        public static float? Below(float x, float z, float fromY)
        {
            return Physics.Raycast(new Vector3(x, fromY + 3f, z), Vector3.down, out RaycastHit hit, 40f, SurfaceMask, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : null;
        }

        /// <summary>True when the player stands far above or below the terrain under them, as in a sky room.</summary>
        public static bool PlayerAwayFromTerrain(out float playerY)
        {
            playerY = 0f;
            Player? player = Player.m_localPlayer;
            if (player == null || ZoneSystem.instance == null) return false;
            Vector3 position = player.transform.position;
            playerY = position.y;
            return Mathf.Abs(position.y - ZoneSystem.instance.GetGroundHeight(position)) > 50f;
        }
    }
}
