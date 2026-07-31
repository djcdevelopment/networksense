namespace ComfyNetworkSense;

using HarmonyLib;

// Auto-applied by Harmony.CreateAndPatchAll in ComfyNetworkSense.Awake.
//
// full36 failed its spawn contract behind 612 vanilla "Found another terrain compiler in
// this area, removing it" removals in 25 seconds: a non-owned duplicate compiler is
// removed as an instance but its ZDO survives, so ZNetScene re-instantiates it every
// frame and IsAreaReady never turns true. The server world holds exactly one compiler
// per zone (dedup receipt sweep-20260731-terrain-dedup matched none), so the colliding
// pair is client-local. This log runs BEFORE vanilla's duplicate check and names every
// compiler's ZDO identity, so one failing run identifies which compiler loops (repeated
// uid) and which one it collided with (logged once) — evidence the vanilla warning
// cannot produce.
[HarmonyPatch(typeof(TerrainComp), "Awake")]
public static class TerrainCompDiagnosticsPatches {
  static void Prefix(TerrainComp __instance) {
    try {
      ZNetView nview = __instance.GetComponent<ZNetView>();
      ZDO zdo = nview?.GetZDO();
      if (zdo == null) {
        ComfyNetworkSense.LogInfo("TERRAIN_COMP awake without a valid ZDO");
        return;
      }
      Vector2i sector = zdo.GetSector();
      ComfyNetworkSense.LogInfo(
          "TERRAIN_COMP awake uid=" + zdo.m_uid
          + " owner=" + zdo.GetOwner()
          + " data_rev=" + zdo.DataRevision
          + " prefab=" + zdo.GetPrefab()
          + " zone=" + sector.x + "," + sector.y);
    } catch { }
  }
}
