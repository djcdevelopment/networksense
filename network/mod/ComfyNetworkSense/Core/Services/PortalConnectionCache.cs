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
  static readonly Dictionary<string, HashSet<ZDOID>> _portalIdsByTag =
      new(StringComparer.Ordinal);
  static readonly Dictionary<ZDOID, string> _tagByPortalId = [];
  static readonly HashSet<string> _dirtyTags = new(StringComparer.Ordinal);

  static float _lastSummaryLogAt = -999999.0f;
  static ZDOMan _indexedManager;

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

    List<ZDO> portalObjects = _portalObjectsRef(zdoManager);
    Dictionary<ZDOID, ZDO> objectsById = _objectsByIdRef(zdoManager);
    long sessionId = _sessionIdRef(zdoManager);

    if (portalObjects == null || objectsById == null) {
      return;
    }

    EnsureIndex(zdoManager, portalObjects);
    int disconnected = 0;
    int connected = 0;
    int processed = 0;
    foreach (string tag in new List<string>(_dirtyTags)) {
      processed++;
      ProcessDirtyTag(
          tag,
          objectsById,
          sessionId,
          ref connected,
          ref disconnected);
    }
    _dirtyTags.Clear();
    ForceSendUpdatedPortals(zdoManager);
    LogSummary(
        portalObjects.Count,
        processed,
        connected,
        disconnected,
        _zdosToForceSend.Count);

    _zdosToForceSend.Clear();
  }

  public static void MarkDirty(ZDO portal) {
    if (portal == null || ZDOMan.instance == null ||
        !ShouldReplaceConnectPortals())
      return;

    List<ZDO> portals = _portalObjectsRef(ZDOMan.instance);
    if (portals == null || !portals.Contains(portal)) return;
    EnsureIndex(ZDOMan.instance, portals);

    string currentTag = portal.GetString(ZDOVars.s_tag, string.Empty);
    if (_tagByPortalId.TryGetValue(portal.m_uid, out string previousTag) &&
        !string.Equals(previousTag, currentTag, StringComparison.Ordinal)) {
      if (_portalIdsByTag.TryGetValue(previousTag, out HashSet<ZDOID> previous))
        previous.Remove(portal.m_uid);
      _dirtyTags.Add(previousTag);
    }
    _tagByPortalId[portal.m_uid] = currentTag;
    if (!_portalIdsByTag.TryGetValue(currentTag, out HashSet<ZDOID> current))
      _portalIdsByTag[currentTag] = current = [];
    current.Add(portal.m_uid);
    _dirtyTags.Add(currentTag);
  }

  public static void MarkRemoved(ZDOID uid) {
    if (!_tagByPortalId.TryGetValue(uid, out string tag)) return;
    _tagByPortalId.Remove(uid);
    if (_portalIdsByTag.TryGetValue(tag, out HashSet<ZDOID> ids))
      ids.Remove(uid);
    _dirtyTags.Add(tag);
  }

  public static void ConnectSavedPortals(ZDOMan zdoManager) {
    if (zdoManager == null || !ShouldReplaceConnectPortals()) return;

    using NetworkSensePerfProbe.Section section =
        NetworkSensePerfProbe.Measure("PortalConnectionCache.ConnectSavedPortals");
    List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(
        ZDOExtraData.ConnectionType.Portal);
    List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(
        ZDOExtraData.ConnectionType.Portal |
        ZDOExtraData.ConnectionType.Target);
    Dictionary<int, Queue<ZDOID>> targetsByHash = [];

    foreach (ZDOID targetId in targets) {
      if (ZDOExtraData.GetConnectionType(targetId) !=
          ZDOExtraData.ConnectionType.None)
        continue;
      ZDO target = zdoManager.GetZDO(targetId);
      ZDOConnectionHashData hash = target == null
          ? null
          : ZDOExtraData.GetConnectionHashData(
              targetId,
              ZDOExtraData.ConnectionType.Portal |
              ZDOExtraData.ConnectionType.Target);
      if (hash == null) continue;
      if (!targetsByHash.TryGetValue(hash.m_hash, out Queue<ZDOID> bucket))
        targetsByHash[hash.m_hash] = bucket = new();
      bucket.Enqueue(targetId);
    }

    int connected = 0;
    long sessionId = _sessionIdRef(zdoManager);
    foreach (ZDOID sourceId in sources) {
      ZDO source = zdoManager.GetZDO(sourceId);
      ZDOConnectionHashData hash = source?.GetConnectionHashData(
          ZDOExtraData.ConnectionType.Portal);
      if (source == null || hash == null ||
          !targetsByHash.TryGetValue(hash.m_hash, out Queue<ZDOID> bucket))
        continue;

      ZDO target = null;
      while (bucket.Count > 0 && target == null) {
        ZDOID targetId = bucket.Dequeue();
        if (targetId != sourceId &&
            ZDOExtraData.GetConnectionType(targetId) ==
                ZDOExtraData.ConnectionType.None)
          target = zdoManager.GetZDO(targetId);
      }
      if (target == null) continue;
      source.SetOwner(sessionId);
      target.SetOwner(sessionId);
      source.SetConnection(
          ZDOExtraData.ConnectionType.Portal, target.m_uid);
      target.SetConnection(
          ZDOExtraData.ConnectionType.Portal, source.m_uid);
      connected++;
    }

    if (connected > 0)
      ComfyNetworkSense.LogInfo(
          $"Portal saved-connection hash join connected={connected} sources={sources.Count} targets={targets.Count}.");
  }

  static void EnsureIndex(ZDOMan manager, List<ZDO> portals) {
    if (_indexedManager == manager) return;
    _indexedManager = manager;
    _portalIdsByTag.Clear();
    _tagByPortalId.Clear();
    _dirtyTags.Clear();
    foreach (ZDO portal in portals) {
      if (portal == null) continue;
      string tag = portal.GetString(ZDOVars.s_tag, string.Empty);
      _tagByPortalId[portal.m_uid] = tag;
      if (!_portalIdsByTag.TryGetValue(tag, out HashSet<ZDOID> ids))
        _portalIdsByTag[tag] = ids = [];
      ids.Add(portal.m_uid);
      _dirtyTags.Add(tag);
    }
  }

  static void ProcessDirtyTag(
      string tag,
      Dictionary<ZDOID, ZDO> objectsById,
      long sessionId,
      ref int connected,
      ref int disconnected) {
    if (!_portalIdsByTag.TryGetValue(tag, out HashSet<ZDOID> indexed))
      return;

    List<ZDO> unconnected = [];
    foreach (ZDOID id in new List<ZDOID>(indexed)) {
      if (!objectsById.TryGetValue(id, out ZDO portal) ||
          portal.GetString(ZDOVars.s_tag, string.Empty) != tag) {
        indexed.Remove(id);
        _tagByPortalId.Remove(id);
        continue;
      }

      ZDOID targetId = portal.GetConnectionZDOID(
          ZDOExtraData.ConnectionType.Portal);
      if (targetId != ZDOID.None &&
          (!objectsById.TryGetValue(targetId, out ZDO target) ||
           target.GetString(ZDOVars.s_tag, string.Empty) != tag)) {
        DisconnectPortal(portal, sessionId);
        disconnected++;
        targetId = ZDOID.None;
      }
      if (targetId == ZDOID.None)
        unconnected.Add(portal);
    }

    for (int i = 0; i + 1 < unconnected.Count; i += 2) {
      ConnectPortals(unconnected[i], unconnected[i + 1], sessionId);
      connected++;
    }
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

  static void LogSummary(
      int portals,
      int dirtyTags,
      int connected,
      int disconnected,
      int forceSent) {
    float logInterval = PluginConfig.PortalConnectionCacheLogIntervalSeconds.Value;
    if (logInterval <= 0.0f || Time.realtimeSinceStartup - _lastSummaryLogAt < logInterval) {
      return;
    }

    _lastSummaryLogAt = Time.realtimeSinceStartup;
    ComfyNetworkSense.LogInfo(
        $"Portal connection cache processed portals={portals} dirtyTags={dirtyTags} connected={connected} disconnected={disconnected} forceSend={forceSent}.");
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

[HarmonyPatch(typeof(ZDOMan), "ConnectPortals")]
static class PortalSavedConnectionPatches {
  [HarmonyPrefix]
  static bool ConnectPortalsPrefix(ZDOMan __instance) {
    if (!PortalConnectionCache.ShouldReplaceConnectPortals())
      return true;
    PortalConnectionCache.ConnectSavedPortals(__instance);
    return false;
  }
}

[HarmonyPatch(typeof(ZDOMan), "AddPortal")]
static class PortalAddPatches {
  [HarmonyPostfix]
  static void AddPortalPostfix(ZDO zdo) =>
      PortalConnectionCache.MarkDirty(zdo);
}

[HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
static class PortalRemovePatches {
  [HarmonyPrefix]
  static void HandleDestroyedZdoPrefix(ZDOID uid) =>
      PortalConnectionCache.MarkRemoved(uid);
}

[HarmonyPatch(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(string) })]
static class PortalTagPatches {
  [HarmonyPostfix]
  static void SetStringPostfix(ZDO __instance, int hash) {
    if (hash == ZDOVars.s_tag)
      PortalConnectionCache.MarkDirty(__instance);
  }
}
