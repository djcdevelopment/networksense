namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;

using UnityEngine;

public static class PortalConnectionCache {
  static readonly AccessTools.FieldRef<ZDOMan, long> _sessionIdRef =
      AccessTools.FieldRefAccess<ZDOMan, long>("m_sessionID");
  static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>> _portalObjectsRef =
      AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_portalObjects");
  static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> _objectsByIdRef =
      AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

  static readonly FieldInfo _peersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
  static readonly FieldInfo _forceSendField =
      AccessTools.Field(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), "m_forceSend");

  static readonly HashSet<ZDOID> _zdosToForceSend = [];
  static readonly Dictionary<string, ZDO> _portalsByTagCache = [];

  static float _lastSummaryLogAt = -999999.0f;

  public static bool ShouldReplaceConnectPortals() {
    return PluginConfig.PortalConnectionCacheEnabled.Value
        && ZNet.instance != null
        && ZNet.instance.IsServer()
        && ZDOMan.instance != null;
  }

  public static void RestartGameCoroutine(Game game) {
    if (!game
        || !PluginConfig.PortalConnectionCacheEnabled.Value
        || ZNet.instance == null
        || !ZNet.instance.IsServer()) {
      return;
    }

    try {
      game.StopCoroutine("ConnectPortalsCoroutine");
      game.StartCoroutine(ConnectPortalsCoroutine());
      ComfyNetworkSense.LogInfo(
          $"Portal connection cache enabled; interval={PluginConfig.PortalConnectionCacheIntervalSeconds.Value:0.##}s.");
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Portal connection cache failed to replace vanilla coroutine: {exception.Message}");
    }
  }

  public static IEnumerator ConnectPortalsCoroutine() {
    while (true) {
      if (ShouldReplaceConnectPortals()) {
        ConnectPortals(ZDOMan.instance);
      }

      yield return new WaitForSeconds(Mathf.Max(1.0f, PluginConfig.PortalConnectionCacheIntervalSeconds.Value));
    }
  }

  public static void ConnectPortals(ZDOMan zdoManager) {
    if (zdoManager == null) {
      return;
    }

    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("PortalConnectionCache.ConnectPortals");

    _zdosToForceSend.Clear();
    _portalsByTagCache.Clear();

    List<ZDO> portalObjects = _portalObjectsRef(zdoManager);
    Dictionary<ZDOID, ZDO> objectsById = _objectsByIdRef(zdoManager);
    long sessionId = _sessionIdRef(zdoManager);

    if (portalObjects == null || objectsById == null) {
      return;
    }

    int disconnected = UpdateUnconnectedPortals(portalObjects, objectsById, sessionId);
    int connected = UpdateConnectedPortals(portalObjects, sessionId);
    ForceSendUpdatedPortals(zdoManager);
    LogSummary(portalObjects.Count, connected, disconnected, _zdosToForceSend.Count);

    _zdosToForceSend.Clear();
    _portalsByTagCache.Clear();
  }

  static int UpdateUnconnectedPortals(
      List<ZDO> portalObjects,
      Dictionary<ZDOID, ZDO> objectsById,
      long sessionId) {
    int disconnected = 0;

    foreach (ZDO zdo in portalObjects) {
      string portalTag = zdo.GetString(ZDOVars.s_tag, string.Empty);
      ZDOID targetZdoId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

      if (targetZdoId == ZDOID.None) {
        if (portalTag.Length > 0) {
          _portalsByTagCache[portalTag] = zdo;
        }

        continue;
      }

      if (portalTag.Length > 0
          && objectsById.TryGetValue(targetZdoId, out ZDO targetZdo)
          && targetZdo.GetString(ZDOVars.s_tag, string.Empty) == portalTag) {
        continue;
      }

      DisconnectPortal(zdo, sessionId);
      disconnected++;

      if (portalTag.Length > 0) {
        _portalsByTagCache[portalTag] = zdo;
      }
    }

    return disconnected;
  }

  static int UpdateConnectedPortals(List<ZDO> portalObjects, long sessionId) {
    int connected = 0;

    foreach (ZDO zdo in portalObjects) {
      string portalTag = zdo.GetString(ZDOVars.s_tag, string.Empty);

      if (portalTag.Length <= 0
          || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None
          || !_portalsByTagCache.TryGetValue(portalTag, out ZDO matchingZdo)
          || matchingZdo == zdo
          || matchingZdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None) {
        continue;
      }

      ConnectPortals(zdo, matchingZdo, sessionId);
      connected++;
    }

    return connected;
  }

  static void DisconnectPortal(ZDO zdo, long sessionId) {
    zdo.SetOwner(sessionId);
    zdo.UpdateConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
    _zdosToForceSend.Add(zdo.m_uid);
  }

  static void ConnectPortals(ZDO sourceZdo, ZDO targetZdo, long sessionId) {
    sourceZdo.SetOwner(sessionId);
    sourceZdo.SetConnection(ZDOExtraData.ConnectionType.Portal, targetZdo.m_uid);

    targetZdo.SetOwner(sessionId);
    targetZdo.SetConnection(ZDOExtraData.ConnectionType.Portal, sourceZdo.m_uid);

    _zdosToForceSend.Add(sourceZdo.m_uid);
    _zdosToForceSend.Add(targetZdo.m_uid);
  }

  static void ForceSendUpdatedPortals(ZDOMan zdoManager) {
    if (_zdosToForceSend.Count <= 0 || _peersField == null || _forceSendField == null) {
      return;
    }

    if (_peersField.GetValue(zdoManager) is not IEnumerable peers) {
      return;
    }

    foreach (object peer in peers) {
      if (_forceSendField.GetValue(peer) is HashSet<ZDOID> forceSend) {
        forceSend.UnionWith(_zdosToForceSend);
      }
    }
  }

  static void LogSummary(int portals, int connected, int disconnected, int forceSent) {
    float logInterval = PluginConfig.PortalConnectionCacheLogIntervalSeconds.Value;
    if (logInterval <= 0.0f || Time.realtimeSinceStartup - _lastSummaryLogAt < logInterval) {
      return;
    }

    _lastSummaryLogAt = Time.realtimeSinceStartup;
    ComfyNetworkSense.LogInfo(
        $"Portal connection cache processed portals={portals} connected={connected} disconnected={disconnected} forceSend={forceSent}.");
  }
}

[HarmonyPatch(typeof(Game))]
static class PortalConnectionCachePatches {
  [HarmonyPostfix]
  [HarmonyPatch("Start")]
  static void StartPostfix(Game __instance) {
    PortalConnectionCache.RestartGameCoroutine(__instance);
  }

  [HarmonyPrefix]
  [HarmonyPatch(nameof(Game.ConnectPortals))]
  static bool ConnectPortalsPrefix() {
    if (!PortalConnectionCache.ShouldReplaceConnectPortals()) {
      return true;
    }

    PortalConnectionCache.ConnectPortals(ZDOMan.instance);
    return false;
  }
}
