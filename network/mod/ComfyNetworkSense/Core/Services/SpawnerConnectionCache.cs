namespace ComfyNetworkSense;

using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;

public static class SpawnerConnectionCache {
  static readonly AccessTools.FieldRef<ZDOMan, long> _sessionIdRef =
      AccessTools.FieldRefAccess<ZDOMan, long>("m_sessionID");
  static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> _objectsByIdRef =
      AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

  static readonly FieldInfo _connectionsHashDataField =
      AccessTools.Field(typeof(ZDOExtraData), "s_connectionsHashData");

  public static bool ShouldReplaceConnectSpawners() {
    return PluginConfig.SpawnerConnectionCacheEnabled.Value;
  }

  public static void ConnectSpawners(ZDOMan zdoManager) {
    if (zdoManager == null || _connectionsHashDataField == null) {
      return;
    }

    if (_connectionsHashDataField.GetValue(null)
        is not Dictionary<ZDOID, ZDOConnectionHashData> connectionsHashData) {
      return;
    }

    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("SpawnerConnectionCache.ConnectSpawners");

    Dictionary<ZDOID, ZDOConnectionHashData> spawned = [];
    Dictionary<int, ZDOID> targetsByHash = [];
    ZDOExtraData.ConnectionType targetConnectionType =
        ZDOExtraData.ConnectionType.Portal
        | ZDOExtraData.ConnectionType.SyncTransform
        | ZDOExtraData.ConnectionType.Target;

    foreach (KeyValuePair<ZDOID, ZDOConnectionHashData> pair in connectionsHashData) {
      if (pair.Value.m_type == ZDOExtraData.ConnectionType.Spawned) {
        spawned[pair.Key] = pair.Value;
      } else if (pair.Value.m_type == targetConnectionType) {
        targetsByHash[pair.Value.m_hash] = pair.Key;
      }
    }

    Dictionary<ZDOID, ZDO> objectsById = _objectsByIdRef(zdoManager);
    if (objectsById == null) {
      return;
    }

    long sessionId = _sessionIdRef(zdoManager);
    int connectedCount = 0;
    int doneCount = 0;

    foreach (KeyValuePair<ZDOID, ZDOConnectionHashData> pair in spawned) {
      if (pair.Key == ZDOID.None || !objectsById.TryGetValue(pair.Key, out ZDO zdo) || zdo == null) {
        continue;
      }

      zdo.SetOwner(sessionId);

      if (targetsByHash.TryGetValue(pair.Value.m_hash, out ZDOID targetZdoId) && pair.Key != targetZdoId) {
        connectedCount++;
        zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, targetZdoId);
      } else {
        doneCount++;
        zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, ZDOID.None);
      }
    }

    ComfyNetworkSense.LogInfo(
        $"Spawner connection cache processed spawned={spawned.Count} targets={targetsByHash.Count} connected={connectedCount} done={doneCount}.");
  }
}

[HarmonyPatch(typeof(ZDOMan))]
static class SpawnerConnectionCachePatches {
  [HarmonyPrefix]
  [HarmonyPatch("ConnectSpawners")]
  static bool ConnectSpawnersPrefix(ZDOMan __instance) {
    if (!SpawnerConnectionCache.ShouldReplaceConnectSpawners()) {
      return true;
    }

    SpawnerConnectionCache.ConnectSpawners(__instance);
    return false;
  }
}
