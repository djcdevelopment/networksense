namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using HarmonyLib;

using UnityEngine;

public sealed class ServerPulseBroadcaster {
  public const string ServerPulseRpc = "ComfyNetworkSense_ServerPulse";

  const int MaxOverlapColliders = 4096;

  static readonly AccessTools.FieldRef<ZRpc, int> _sentDataRef = AccessTools.FieldRefAccess<ZRpc, int>("m_sentData");
  static readonly AccessTools.FieldRef<ZRpc, int> _sentPackagesRef =
      AccessTools.FieldRefAccess<ZRpc, int>("m_sentPackages");
  static readonly Collider[] _overlapColliders = new Collider[MaxOverlapColliders];
  static readonly HashSet<int> _seenComponentIds = [];

  readonly Dictionary<long, PeerCounterState> _peerCounters = [];
  readonly Dictionary<string, OwnerState> _ownerStates = [];

  float _lastPulseTime;
  float _lastWorldZdoCountTime = -9999.0f;
  int _cachedWorldZdoCount;

  public void Update(string sessionId, NetworkSenseMode mode, TelemetryLogWriter writer) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ServerPulseBroadcaster.Update");

    if (!ZNet.instance || !ZNet.instance.IsServer()) {
      return;
    }

    float now = Time.unscaledTime;

    float intervalSeconds = Mathf.Max(0.25f, PluginConfig.ServerPulseIntervalSeconds.Value);

    if (now - _lastPulseTime < intervalSeconds) {
      return;
    }

    _lastPulseTime = now;

    int peerCount = 0;
    float aggregateBytesSentPerSec = 0.0f;
    float aggregateMessagesSentPerSec = 0.0f;
    int aggregateObservers = 0;

    foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
      if (peer?.m_rpc == null) {
        continue;
      }

      ServerPulseSnapshot pulse = CapturePulse(peer, sessionId, mode, now);

      writer?.Write("telemetry-server.jsonl", pulse.ToDictionary());

      ZPackage package = new();
      pulse.WriteTo(package);

      ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, ServerPulseRpc, [package]);

      peerCount++;
      aggregateBytesSentPerSec += pulse.BytesSentPerSec;
      aggregateMessagesSentPerSec += pulse.MessagesSentPerSec;
      aggregateObservers += pulse.RegionObserverCount;
    }

    // Always-on server heartbeat: one row per interval regardless of peer count, so the
    // headless server is a self-sufficient recorder. With 0 peers it is the at-rest
    // baseline; with N peers it is the aggregate "how much does the server push at N
    // players" number (server-side bytes/sec + messages/sec), plus world ZDO size.
    ServerPulseSnapshot heartbeat =
        CaptureServerHeartbeat(sessionId, mode, peerCount, aggregateBytesSentPerSec, aggregateMessagesSentPerSec, aggregateObservers);
    writer?.Write("telemetry-server.jsonl", heartbeat.ToDictionary());
  }

  ServerPulseSnapshot CaptureServerHeartbeat(
      string sessionId,
      NetworkSenseMode mode,
      int peerCount,
      float aggregateBytesSentPerSec,
      float aggregateMessagesSentPerSec,
      int aggregateObservers) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ServerPulseBroadcaster.CaptureServerHeartbeat");

    int worldZdoCount = GetWorldZdoCount();
    int connectedPlayers = ZNet.instance != null ? ZNet.instance.GetNrOfPlayers() : peerCount;

    return new() {
        TimestampUtc = DateTime.UtcNow.ToString("o"),
        SessionId = sessionId,
        SampleId = Guid.NewGuid().ToString("N"),
        PlayerId = "server",
        PlayerName = string.Empty,
        RegionId = "server",
        ClusterId = "server",
        Mode = mode.ToString(),
        OwnerId = string.Empty,
        OwnerReason = "server_aggregate",
        SampleSource = "server_heartbeat",
        BuildVersion = ComfyNetworkSense.PluginVersion,
        RegionObserverCount = aggregateObservers,
        RegionPlayerCount = connectedPlayers,
        RegionEntityCount = worldZdoCount,
        RegionBuildCount = 0,
        RegionPressureScore = 0.0f,
        HeartbeatGapMs = 0.0f,
        MessagesSentPerSec = aggregateMessagesSentPerSec,
        BytesSentPerSec = aggregateBytesSentPerSec,
        OwnerConfidence = 0.0f,
        AuthorityStabilitySec = 0.0f,
        OwnerCandidateCount = peerCount
    };
  }

  int GetWorldZdoCount() {
    if (!PluginConfig.ServerHeartbeatWorldZdoCountEnabled.Value || ZDOMan.instance == null) {
      return 0;
    }

    float intervalSeconds = Mathf.Max(1.0f, PluginConfig.ServerHeartbeatWorldZdoCountIntervalSeconds.Value);
    if (Time.unscaledTime - _lastWorldZdoCountTime < intervalSeconds) {
      return _cachedWorldZdoCount;
    }

    _lastWorldZdoCountTime = Time.unscaledTime;
    using (NetworkSensePerfProbe.Measure("ServerPulseBroadcaster.WorldZdoCount")) {
      _cachedWorldZdoCount = ZDOMan.instance.NrOfObjects();
    }

    return _cachedWorldZdoCount;
  }

  ServerPulseSnapshot CapturePulse(ZNetPeer peer, string sessionId, NetworkSenseMode mode, float now) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ServerPulseBroadcaster.CapturePulse");

    Vector3 refPosition = peer.m_refPos;
    Vector2i zone = ZoneSystem.instance ? ZoneSystem.GetZone(refPosition) : default;
    string regionId = $"{zone.x}:{zone.y}";

    int regionPlayerCount = CountPlayersNear(refPosition, PluginConfig.NearbyRadiusMeters.Value);
    int regionEntityCount = CountCharactersNear(refPosition, PluginConfig.NearbyRadiusMeters.Value);
    int regionBuildCount = CountPiecesNear(refPosition, PluginConfig.BuildScanRadiusMeters.Value);
    int observerCount = CountPeersNear(refPosition, PluginConfig.NearbyRadiusMeters.Value);

    float regionPressure = ScoreCalculator.CalculateRegionPressure(regionPlayerCount, regionEntityCount, regionBuildCount);
    float heartbeatGapMs = peer.m_rpc.GetTimeSinceLastPing() * 1000.0f;

    PeerCounterState counterState = GetOrCreatePeerCounter(peer.m_uid);
    int sentData = _sentDataRef(peer.m_rpc);
    int sentPackages = _sentPackagesRef(peer.m_rpc);
    float elapsed = counterState.HasPrevious ? Mathf.Max(0.001f, now - counterState.Time) : 0.0f;
    float bytesSentPerSec = counterState.HasPrevious ? Mathf.Max(0.0f, sentData - counterState.SentData) / elapsed : 0.0f;
    float messagesSentPerSec =
        counterState.HasPrevious ? Mathf.Max(0.0f, sentPackages - counterState.SentPackages) / elapsed : 0.0f;

    counterState.HasPrevious = true;
    counterState.Time = now;
    counterState.SentData = sentData;
    counterState.SentPackages = sentPackages;
    _peerCounters[peer.m_uid] = counterState;

    string predictedOwnerName = FindClosestPlayerName(refPosition, PluginConfig.NearbyRadiusMeters.Value, out float ownerConfidence);
    OwnerState ownerState = GetOwnerState(regionId, predictedOwnerName, now);

    return new() {
        TimestampUtc = DateTime.UtcNow.ToString("o"),
        SessionId = sessionId,
        SampleId = Guid.NewGuid().ToString("N"),
        PlayerId = peer.m_uid.ToString(),
        PlayerName = peer.m_playerName ?? string.Empty,
        RegionId = regionId,
        ClusterId = regionId,
        Mode = mode.ToString(),
        OwnerId = predictedOwnerName,
        OwnerReason = "closest_to_reference",
        SampleSource = "server_pulse",
        BuildVersion = ComfyNetworkSense.PluginVersion,
        RegionObserverCount = observerCount,
        RegionPlayerCount = regionPlayerCount,
        RegionEntityCount = regionEntityCount,
        RegionBuildCount = regionBuildCount,
        RegionPressureScore = regionPressure,
        HeartbeatGapMs = heartbeatGapMs,
        MessagesSentPerSec = messagesSentPerSec,
        BytesSentPerSec = bytesSentPerSec,
        OwnerConfidence = ownerConfidence,
        AuthorityStabilitySec = ownerState.StabilitySeconds,
        OwnerCandidateCount = regionPlayerCount
    };
  }

  PeerCounterState GetOrCreatePeerCounter(long uid) {
    if (_peerCounters.TryGetValue(uid, out PeerCounterState counterState)) {
      return counterState;
    }

    return new() {
        Uid = uid
    };
  }

  OwnerState GetOwnerState(string regionId, string ownerName, float now) {
    if (_ownerStates.TryGetValue(regionId, out OwnerState ownerState)) {
      if (ownerState.OwnerName == ownerName) {
        ownerState.StabilitySeconds = now - ownerState.LastChangedAt;
        _ownerStates[regionId] = ownerState;
        return ownerState;
      }
    }

    ownerState = new() {
        OwnerName = ownerName,
        LastChangedAt = now,
        StabilitySeconds = 0.0f
    };

    _ownerStates[regionId] = ownerState;
    return ownerState;
  }

  static int CountPlayersNear(Vector3 position, float radius) {
    if (!ZNet.instance) {
      return 0;
    }

    int count = 0;

    foreach (ZNet.PlayerInfo playerInfo in ZNet.instance.GetPlayerList()) {
      if (!playerInfo.m_publicPosition) {
        continue;
      }

      if (Vector3.Distance(position, playerInfo.m_position) <= radius) {
        count++;
      }
    }

    return count;
  }

  static int CountPeersNear(Vector3 position, float radius) {
    if (!ZNet.instance) {
      return 0;
    }

    int count = 0;

    foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
      if (peer == null) {
        continue;
      }

      if (Vector3.Distance(position, peer.m_refPos) <= radius) {
        count++;
      }
    }

    return count;
  }

  static int CountCharactersNear(Vector3 position, float radius) {
    return CountNearbyComponents<Character>(position, radius);
  }

  static int CountPiecesNear(Vector3 position, float radius) {
    return CountNearbyComponents<Piece>(position, radius);
  }

  static int CountNearbyComponents<T>(Vector3 position, float radius) where T : Component {
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
      if (!component) {
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

  static string FindClosestPlayerName(Vector3 position, float radius, out float confidence) {
    confidence = 0.0f;

    if (!ZNet.instance) {
      return string.Empty;
    }

    string closestName = string.Empty;
    float closestDistance = float.MaxValue;
    float secondDistance = float.MaxValue;
    int candidates = 0;

    foreach (ZNet.PlayerInfo playerInfo in ZNet.instance.GetPlayerList()) {
      if (!playerInfo.m_publicPosition) {
        continue;
      }

      float distance = Vector3.Distance(position, playerInfo.m_position);

      if (distance > radius) {
        continue;
      }

      candidates++;

      if (distance < closestDistance) {
        secondDistance = closestDistance;
        closestDistance = distance;
        closestName = playerInfo.m_name;
      } else if (distance < secondDistance) {
        secondDistance = distance;
      }
    }

    if (candidates <= 0) {
      return string.Empty;
    }

    if (candidates == 1 || secondDistance == float.MaxValue) {
      confidence = 1.0f;
      return closestName;
    }

    confidence = Mathf.Clamp01((secondDistance - closestDistance) / Mathf.Max(1.0f, radius));
    return closestName;
  }

  struct PeerCounterState {
    public long Uid;
    public bool HasPrevious;
    public int SentData;
    public int SentPackages;
    public float Time;
  }

  struct OwnerState {
    public string OwnerName;
    public float LastChangedAt;
    public float StabilitySeconds;
  }
}
