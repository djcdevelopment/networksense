namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Linq;

using HarmonyLib;

using UnityEngine;

public sealed class ClientTelemetrySampler {
  const int MaxOverlapColliders = 4096;

  static readonly AccessTools.FieldRef<ZRpc, int> _sentDataRef = AccessTools.FieldRefAccess<ZRpc, int>("m_sentData");
  static readonly AccessTools.FieldRef<ZRpc, int> _recvDataRef = AccessTools.FieldRefAccess<ZRpc, int>("m_recvData");
  static readonly AccessTools.FieldRef<ZRpc, int> _sentPackagesRef =
      AccessTools.FieldRefAccess<ZRpc, int>("m_sentPackages");
  static readonly AccessTools.FieldRef<ZRpc, int> _recvPackagesRef =
      AccessTools.FieldRefAccess<ZRpc, int>("m_recvPackages");
  static readonly Collider[] _overlapColliders = new Collider[MaxOverlapColliders];
  static readonly HashSet<int> _seenComponentIds = [];

  readonly List<float> _frameTimesMs = [];

  bool _hasPreviousCounters;
  int _previousSentData;
  int _previousRecvData;
  int _previousSentPackages;
  int _previousRecvPackages;
  float _previousCounterTime;
  float _lastRttMs;
  float _lastSceneScanTime = -9999.0f;
  string _lastSceneScanRegionId = string.Empty;
  int _cachedNearbyEntities;
  int _cachedNearbyBuildPieces;

  public void RecordFrame(float deltaTime) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ClientTelemetrySampler.RecordFrame");

    _frameTimesMs.Add(deltaTime * 1000.0f);

    if (_frameTimesMs.Count > 240) {
      _frameTimesMs.RemoveAt(0);
    }
  }

  public ClientTelemetrySample Capture(string sessionId, NetworkSenseMode mode, string ownerId) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ClientTelemetrySampler.Capture");

    Player localPlayer = Player.m_localPlayer;
    Vector3 position = localPlayer ? localPlayer.transform.position : Vector3.zero;
    Vector2i zone = ZoneSystem.instance ? ZoneSystem.GetZone(position) : default;
    string regionId = $"{zone.x}:{zone.y}";

    float rttMs = GetRttMs();
    float jitterMs = _lastRttMs > 0.0f ? Mathf.Abs(rttMs - _lastRttMs) : 0.0f;
    _lastRttMs = rttMs;

    ZRpc serverRpc = ZNet.instance ? ZNet.instance.GetServerRPC() : null;
    float bytesInPerSec = 0.0f;
    float bytesOutPerSec = 0.0f;
    float packetsInPerSec = 0.0f;
    float packetsOutPerSec = 0.0f;

    if (serverRpc != null) {
      float now = Time.unscaledTime;
      int sentData = _sentDataRef(serverRpc);
      int recvData = _recvDataRef(serverRpc);
      int sentPackages = _sentPackagesRef(serverRpc);
      int recvPackages = _recvPackagesRef(serverRpc);

      if (_hasPreviousCounters) {
        float elapsed = Mathf.Max(0.001f, now - _previousCounterTime);
        bytesOutPerSec = Mathf.Max(0.0f, sentData - _previousSentData) / elapsed;
        bytesInPerSec = Mathf.Max(0.0f, recvData - _previousRecvData) / elapsed;
        packetsOutPerSec = Mathf.Max(0.0f, sentPackages - _previousSentPackages) / elapsed;
        packetsInPerSec = Mathf.Max(0.0f, recvPackages - _previousRecvPackages) / elapsed;
      }

      _hasPreviousCounters = true;
      _previousCounterTime = now;
      _previousSentData = sentData;
      _previousRecvData = recvData;
      _previousSentPackages = sentPackages;
      _previousRecvPackages = recvPackages;
    }

    float frameTimeMs = _frameTimesMs.Count > 0 ? _frameTimesMs[_frameTimesMs.Count - 1] : 0.0f;
    float p95FrameMs = GetP95FrameTimeMs();
    float fps = frameTimeMs > 0.0f ? 1000.0f / frameTimeMs : 0.0f;
    float cpuBoundEstimate = GetCpuBoundEstimate(frameTimeMs, p95FrameMs);

    int nearbyPlayers = CountNearbyPlayers(position, PluginConfig.NearbyRadiusMeters.Value, localPlayer);
    int nearbyEntities = _cachedNearbyEntities;
    int nearbyBuildPieces = _cachedNearbyBuildPieces;

    if (_lastSceneScanRegionId != regionId) {
      _lastSceneScanRegionId = regionId;
      _lastSceneScanTime = -9999.0f;
      _cachedNearbyEntities = 0;
      _cachedNearbyBuildPieces = 0;
      nearbyEntities = 0;
      nearbyBuildPieces = 0;
    }

    bool zoneLoaded = ZoneSystem.instance == null || ZoneSystem.instance.IsZoneLoaded(zone);
    float sceneScanIntervalSeconds = Mathf.Max(0.1f, PluginConfig.SceneScanIntervalSeconds.Value);
    if (PluginConfig.SceneScanEnabled.Value
        && zoneLoaded
        && Time.unscaledTime - _lastSceneScanTime >= sceneScanIntervalSeconds) {
      _lastSceneScanTime = Time.unscaledTime;
      using (NetworkSensePerfProbe.Measure("ClientTelemetrySampler.SceneScan")) {
        _cachedNearbyEntities = CountNearbyEntities(position, PluginConfig.NearbyRadiusMeters.Value, localPlayer);
        _cachedNearbyBuildPieces = CountNearbyPieces(position, PluginConfig.BuildScanRadiusMeters.Value);
      }
      nearbyEntities = _cachedNearbyEntities;
      nearbyBuildPieces = _cachedNearbyBuildPieces;
    }

    return new() {
        TimestampUtc = DateTime.UtcNow.ToString("o"),
        SessionId = sessionId,
        SampleId = Guid.NewGuid().ToString("N"),
        PlayerId = localPlayer ? localPlayer.GetPlayerID().ToString() : string.Empty,
        PlayerName = localPlayer ? localPlayer.GetPlayerName() : string.Empty,
        RegionId = regionId,
        ClusterId = regionId,
        Mode = mode.ToString(),
        OwnerId = ownerId ?? string.Empty,
        SampleSource = "client_live",
        BuildVersion = ComfyNetworkSense.PluginVersion,
        RttMs = rttMs,
        JitterMs = jitterMs,
        Fps = fps,
        FrameTimeMs = frameTimeMs,
        FrameTimeP95Ms = p95FrameMs,
        CpuBoundEstimate = cpuBoundEstimate,
        BytesInPerSec = bytesInPerSec,
        BytesOutPerSec = bytesOutPerSec,
        PacketsInPerSec = packetsInPerSec,
        PacketsOutPerSec = packetsOutPerSec,
        TimeSinceLastAuthoritativeUpdateMs = 0.0f,
        TimeSinceLastNearbyEntityUpdateMs = 0.0f,
        CorrectionCountRecent = 0,
        CorrectionMagnitudeAvg = 0.0f,
        NearbyPlayers = nearbyPlayers,
        NearbyEntities = nearbyEntities,
        NearbyBuildPieces = nearbyBuildPieces,
        DangerNearby = nearbyEntities > nearbyPlayers + 2
    };
  }

  float GetRttMs() {
    if (!ZNet.instance) {
      return 0.0f;
    }

    float raw = ZNet.instance.GetServerPing();

    if (raw <= 0.0f) {
      return 0.0f;
    }

    return raw < 10.0f ? raw * 1000.0f : raw;
  }

  float GetP95FrameTimeMs() {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ClientTelemetrySampler.P95");

    if (_frameTimesMs.Count <= 0) {
      return 0.0f;
    }

    List<float> sorted = [.._frameTimesMs];
    sorted.Sort();
    int index = Mathf.Clamp(Mathf.CeilToInt(sorted.Count * 0.95f) - 1, 0, sorted.Count - 1);
    return sorted[index];
  }

  static float GetCpuBoundEstimate(float frameTimeMs, float p95FrameMs) {
    float normalizedFrame = Mathf.Clamp01((frameTimeMs - 16.0f) / 50.0f);
    float normalizedP95 = Mathf.Clamp01((p95FrameMs - 20.0f) / 60.0f);
    return Mathf.Clamp01(normalizedFrame * 0.45f + normalizedP95 * 0.55f);
  }

  static int CountNearbyPlayers(Vector3 position, float radius, Player localPlayer) {
    if (!ZNet.instance) {
      return 0;
    }

    int count = 0;

    foreach (ZNet.PlayerInfo playerInfo in ZNet.instance.GetPlayerList()) {
      if (!playerInfo.m_publicPosition) {
        continue;
      }

      if (localPlayer && playerInfo.m_name == localPlayer.GetPlayerName()) {
        continue;
      }

      if (Vector3.Distance(position, playerInfo.m_position) <= radius) {
        count++;
      }
    }

    return count;
  }

  static int CountNearbyEntities(Vector3 position, float radius, Player localPlayer) {
    return CountNearbyComponents<Character>(position, radius, localPlayer);
  }

  static int CountNearbyPieces(Vector3 position, float radius) {
    return CountNearbyComponents<Piece>(position, radius, null);
  }

  static int CountNearbyComponents<T>(Vector3 position, float radius, Component excluded) where T : Component {
    _seenComponentIds.Clear();

    int colliderCount = Physics.OverlapSphereNonAlloc(position, radius, _overlapColliders);
    int count = 0;

    for (int index = 0; index < colliderCount; index++) {
      Collider collider = _overlapColliders[index];
      _overlapColliders[index] = null;

      if (!collider) {
        continue;
      }

      T component = collider.GetComponentInParent<T>();
      if (!component || component == excluded) {
        continue;
      }

      int instanceId = component.GetInstanceID();
      if (_seenComponentIds.Add(instanceId)) {
        count++;
      }
    }

    _seenComponentIds.Clear();
    return count;
  }
}
