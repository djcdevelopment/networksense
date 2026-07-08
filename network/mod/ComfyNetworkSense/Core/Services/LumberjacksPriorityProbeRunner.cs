namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

using UnityEngine;

public sealed class LumberjacksPriorityProbeRunner : IDisposable {
  const int MaxOverlapColliders = 8192;

  static readonly Collider[] _overlapColliders = new Collider[MaxOverlapColliders];
  static readonly HashSet<int> _seenPieceIds = [];
  static readonly Dictionary<Type, FieldInfo> _creatorFieldCache = [];

  readonly object _statsLock = new();

  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  string _runLabel = "manual";
  string _routeStopId = string.Empty;
  string _routePhase = string.Empty;
  string _lastSampleId = string.Empty;
  DateTime _startedUtc;
  DateTime _lastSampleUtc;
  float _scanAccumulator;
  float? _radiusOverride;
  float? _intervalOverride;
  int? _maxObjectsOverride;
  int _samples;
  int _objectRows;
  int _errors;
  int _lastColliderCount;
  int _lastCandidateCount;
  int _lastEmittedCount;
  int _lastScanDurationMs;
  bool _lastColliderBufferFull;
  readonly Dictionary<string, int> _lastTierCounts = [];

  public bool IsRunning => _running;

  public string Start(
      TelemetryCoordinator coordinator,
      float? radiusOverride = null,
      float? intervalOverride = null,
      int? maxObjectsOverride = null) {
    if (_running) {
      return $"Lumberjacks priority probe already running: {GetStatus()}";
    }

    _running = true;
    _status = "starting";
    _lastError = string.Empty;
    _runLabel = "manual";
    _routeStopId = string.Empty;
    _routePhase = "start";
    _lastSampleId = string.Empty;
    _startedUtc = DateTime.UtcNow;
    _lastSampleUtc = DateTime.MinValue;
    _radiusOverride = radiusOverride.HasValue ? Mathf.Clamp(radiusOverride.Value, 8.0f, 256.0f) : (float?) null;
    _intervalOverride = intervalOverride.HasValue ? Mathf.Clamp(intervalOverride.Value, 0.5f, 30.0f) : (float?) null;
    _maxObjectsOverride = maxObjectsOverride.HasValue ? Mathf.Clamp(maxObjectsOverride.Value, 1, 512) : (int?) null;
    _scanAccumulator = ScanIntervalSeconds();
    _samples = 0;
    _objectRows = 0;
    _errors = 0;
    _lastColliderCount = 0;
    _lastCandidateCount = 0;
    _lastEmittedCount = 0;
    _lastScanDurationMs = 0;
    _lastColliderBufferFull = false;
    _lastTierCounts.Clear();

    coordinator?.RecordLumberjacksPriority(BuildStatusRow("start"));
    return $"Lumberjacks priority probe starting: radius={RadiusMeters():0.#}m, interval={ScanIntervalSeconds():0.#}s, maxObjects={MaxObjectsPerSample()}.";
  }

  public string Stop(TelemetryCoordinator coordinator = null) {
    if (!_running) {
      return "Lumberjacks priority probe is not running.";
    }

    _status = "stopped";
    _running = false;
    coordinator?.RecordLumberjacksPriority(BuildStatusRow("stop"));
    return "Lumberjacks priority probe stopped; no Valheim ZDOs or transforms were modified.";
  }

  public void SetRouteContext(string runLabel, string routeStopId, string routePhase) {
    lock (_statsLock) {
      _runLabel = string.IsNullOrWhiteSpace(runLabel) ? "manual" : runLabel.Trim();
      _routeStopId = routeStopId ?? string.Empty;
      _routePhase = routePhase ?? string.Empty;
    }
  }

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("LumberjacksPriorityProbeRunner.Update");

    if (!_running) {
      return;
    }

    _scanAccumulator += deltaTime;
    float intervalSeconds = ScanIntervalSeconds();
    if (_scanAccumulator < intervalSeconds) {
      return;
    }

    _scanAccumulator = 0.0f;
    Scan(coordinator);
  }

  public string GetStatus() {
    lock (_statsLock) {
      return
          $"Lumberjacks priority probe: status={_status}, samples={_samples}, "
          + $"lastCandidates={_lastCandidateCount}, lastEmitted={_lastEmittedCount}, "
          + $"radius={RadiusMeters():F1}m, interval={ScanIntervalSeconds():F1}s, "
          + $"lastScan={_lastScanDurationMs}ms, error={_lastError}";
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_statsLock) {
      Dictionary<string, object> row = BaseRow(eventType, string.Empty, Vector3.zero, string.Empty);
      row["status"] = _status;
      row["samples"] = _samples;
      row["object_rows"] = _objectRows;
      row["last_sample_id"] = _lastSampleId;
      row["last_collider_count"] = _lastColliderCount;
      row["last_collider_buffer_full"] = _lastColliderBufferFull;
      row["last_candidate_count"] = _lastCandidateCount;
      row["last_emitted_object_count"] = _lastEmittedCount;
      row["last_scan_duration_ms"] = _lastScanDurationMs;
      row["last_sample_utc"] = _lastSampleUtc == DateTime.MinValue ? string.Empty : _lastSampleUtc.ToString("o");
      row["errors"] = _errors;
      row["last_error"] = _lastError;
      AddTierCounts(row, "last_", _lastTierCounts);
      row["claim"] = ScopeClaim();
      return row;
    }
  }

  void Scan(TelemetryCoordinator coordinator) {
    Stopwatch stopwatch = Stopwatch.StartNew();
    string sampleId = Guid.NewGuid().ToString("N");
    Player player = Player.m_localPlayer;
    if (!player) {
      NoteError("Player.m_localPlayer is not available.");
      coordinator?.RecordLumberjacksPriority(BuildStatusRow("sample_error"));
      return;
    }

    Vector3 playerPosition = ((Component) player).transform.position;
    string regionId = ResolveRegionId(playerPosition);
    float radius = RadiusMeters();
    int maxObjects = MaxObjectsPerSample();

    List<PriorityCandidate> candidates = [];
    Dictionary<string, int> tierCounts = NewTierCounts();
    AddLocalPlayerCandidate(candidates, tierCounts, player, playerPosition);

    int colliderCount = 0;
    bool colliderBufferFull = false;
    try {
      colliderCount = Physics.OverlapSphereNonAlloc(playerPosition, radius, _overlapColliders);
      colliderBufferFull = colliderCount >= MaxOverlapColliders;
      _seenPieceIds.Clear();

      for (int index = 0; index < colliderCount; index++) {
        Collider collider = _overlapColliders[index];
        _overlapColliders[index] = null;

        if (!collider) {
          continue;
        }

        Piece piece = collider.GetComponentInParent<Piece>();
        if (!piece) {
          continue;
        }

        int pieceId = piece.GetInstanceID();
        if (!_seenPieceIds.Add(pieceId)) {
          continue;
        }

        PriorityCandidate candidate = BuildPieceCandidate(piece, playerPosition, candidates.Count, radius);
        candidates.Add(candidate);
        tierCounts[candidate.PriorityTier]++;
      }
    } catch (Exception exception) {
      NoteError(exception.GetType().Name + ": " + exception.Message);
    } finally {
      for (int index = 0; index < colliderCount && index < _overlapColliders.Length; index++) {
        _overlapColliders[index] = null;
      }
      _seenPieceIds.Clear();
    }

    candidates.Sort(ComparePriorityCandidates);
    int emittedCount = Math.Min(maxObjects, candidates.Count);
    stopwatch.Stop();

    lock (_statsLock) {
      _status = "scanning";
      _samples++;
      _lastSampleId = sampleId;
      _lastSampleUtc = DateTime.UtcNow;
      _lastColliderCount = colliderCount;
      _lastColliderBufferFull = colliderBufferFull;
      _lastCandidateCount = candidates.Count;
      _lastEmittedCount = emittedCount;
      _lastScanDurationMs = Mathf.RoundToInt((float) stopwatch.Elapsed.TotalMilliseconds);
      _lastTierCounts.Clear();
      foreach (KeyValuePair<string, int> pair in tierCounts) {
        _lastTierCounts[pair.Key] = pair.Value;
      }
    }

    Dictionary<string, object> summary = BaseRow("sample", sampleId, playerPosition, regionId);
    summary["status"] = _status;
    summary["collider_count"] = colliderCount;
    summary["collider_buffer_full"] = colliderBufferFull;
    summary["candidate_count"] = candidates.Count;
    summary["emitted_object_count"] = emittedCount;
    summary["emission_capped"] = emittedCount < candidates.Count;
    summary["scan_duration_ms"] = Mathf.RoundToInt((float) stopwatch.Elapsed.TotalMilliseconds);
    summary["top_priority_tier"] = emittedCount > 0 ? candidates[0].PriorityTier : string.Empty;
    summary["top_object_name"] = emittedCount > 0 ? candidates[0].ObjectName : string.Empty;
    summary["claim"] = ScopeClaim();
    AddTierCounts(summary, string.Empty, tierCounts);
    coordinator?.RecordLumberjacksPriority(summary);

    for (int priorityIndex = 0; priorityIndex < emittedCount; priorityIndex++) {
      PriorityCandidate candidate = candidates[priorityIndex];
      Dictionary<string, object> row = BaseRow("object", sampleId, playerPosition, regionId);
      row["scan_order"] = candidate.ScanOrder;
      row["priority_order"] = priorityIndex + 1;
      row["object_kind"] = candidate.ObjectKind;
      row["object_name"] = candidate.ObjectName;
      row["object_instance_id"] = candidate.InstanceId;
      row["object_stable_key"] = candidate.StableKey;
      row["creator_id"] = candidate.CreatorId;
      row["priority_tier"] = candidate.PriorityTier;
      row["priority_rank"] = candidate.PriorityRank;
      row["priority_reason"] = candidate.PriorityReason;
      row["distance_meters"] = Math.Round(candidate.DistanceMeters, 3);
      row["distance_horizontal_meters"] = Math.Round(candidate.HorizontalDistanceMeters, 3);
      row["object_x"] = Math.Round(candidate.Position.x, 3);
      row["object_y"] = Math.Round(candidate.Position.y, 3);
      row["object_z"] = Math.Round(candidate.Position.z, 3);
      row["component_names"] = candidate.ComponentNames;
      row["claim"] = ScopeClaim();
      coordinator?.RecordLumberjacksPriority(row);
    }

    lock (_statsLock) {
      _objectRows += emittedCount;
    }
  }

  Dictionary<string, object> BaseRow(string eventType, string sampleId, Vector3 playerPosition, string regionId) {
    return new Dictionary<string, object> {
        ["event"] = eventType,
        ["run_label"] = _runLabel,
        ["route_stop_id"] = _routeStopId,
        ["route_phase"] = _routePhase,
        ["sample_id"] = sampleId ?? string.Empty,
        ["radius_meters"] = Math.Round(RadiusMeters(), 3),
        ["scan_interval_seconds"] = Math.Round(ScanIntervalSeconds(), 3),
        ["max_objects_per_sample"] = MaxObjectsPerSample(),
        ["player_x"] = Math.Round(playerPosition.x, 3),
        ["player_y"] = Math.Round(playerPosition.y, 3),
        ["player_z"] = Math.Round(playerPosition.z, 3),
        ["region_id"] = regionId ?? string.Empty,
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  PriorityCandidate BuildPieceCandidate(Piece piece, Vector3 playerPosition, int scanOrder, float radiusMeters) {
    Vector3 position = ((Component) piece).transform.position;
    string objectName = CleanObjectName(((Component) piece).gameObject.name);
    string[] componentNames = CollectComponentNames(piece);
    string priorityTier =
        ClassifyPriority(objectName, componentNames, position, playerPosition, radiusMeters, out int priorityRank, out string reason);
    Vector3 delta = position - playerPosition;

    return new PriorityCandidate {
        ObjectKind = "piece",
        ObjectName = objectName,
        InstanceId = piece.GetInstanceID(),
        StableKey = StableKey(objectName, position),
        CreatorId = TryGetCreatorId(piece),
        Position = position,
        ScanOrder = scanOrder + 1,
        PriorityTier = priorityTier,
        PriorityRank = priorityRank,
        PriorityReason = reason,
        DistanceMeters = Vector3.Distance(position, playerPosition),
        HorizontalDistanceMeters = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z),
        ComponentNames = componentNames
    };
  }

  static void AddLocalPlayerCandidate(
      List<PriorityCandidate> candidates,
      Dictionary<string, int> tierCounts,
      Player player,
      Vector3 playerPosition) {
    string playerName = player.GetPlayerName();
    candidates.Add(new PriorityCandidate {
        ObjectKind = "local_player",
        ObjectName = string.IsNullOrWhiteSpace(playerName) ? "local_player" : playerName,
        InstanceId = player.GetInstanceID(),
        StableKey = "local_player:" + player.GetPlayerID().ToString(CultureInfo.InvariantCulture),
        CreatorId = player.GetPlayerID().ToString(CultureInfo.InvariantCulture),
        Position = playerPosition,
        ScanOrder = 1,
        PriorityTier = "player_critical",
        PriorityRank = 0,
        PriorityReason = "local_player_state",
        DistanceMeters = 0.0f,
        HorizontalDistanceMeters = 0.0f,
        ComponentNames = ["Player", "Character"]
    });
    tierCounts["player_critical"]++;
  }

  static string ClassifyPriority(
      string objectName,
      string[] componentNames,
      Vector3 objectPosition,
      Vector3 playerPosition,
      float radiusMeters,
      out int priorityRank,
      out string reason) {
    string name = (objectName ?? string.Empty).ToLowerInvariant();
    string joinedComponents = string.Join("|", componentNames ?? []).ToLowerInvariant();
    float distance = Vector3.Distance(objectPosition, playerPosition);

    if (ContainsAny(name, "portal", "teleport") || ContainsAny(joinedComponents, "teleportworld", "teleport")) {
      priorityRank = 1;
      reason = "portal_name_or_component";
      return "portal";
    }

    if (ContainsAny(name, "bed", "guardstone", "ward", "piece_guardstone", "fire", "hearth", "bonfire", "shieldgenerator")
        || ContainsAny(joinedComponents, "bed", "fireplace", "privatearea", "shieldgenerator")) {
      priorityRank = 0;
      reason = "spawn_protection_or_survival_component";
      return "player_critical";
    }

    if (ContainsAny(name, "chest", "crate", "cart", "karve", "longship", "forge", "workbench", "artisan", "cauldron", "oven", "smelter", "kiln", "fermenter", "windmill", "spinningwheel", "sapcollector", "beehive")
        || ContainsAny(joinedComponents, "container", "craftingstation", "smelter", "fermenter", "cookingstation", "beehive", "sapcollector")) {
      priorityRank = 4;
      reason = "storage_or_crafting_name_or_component";
      return "storage_crafting";
    }

    if (ContainsAny(name, "door", "gate", "sign", "itemstand", "chair", "table", "maptable", "raven", "ship")
        || ContainsAny(joinedComponents, "door", "textreceiver", "itemstand", "chair", "ship", "maptable")) {
      priorityRank = 3;
      reason = "near_interactive_name_or_component";
      return "near_interactive";
    }

    if (ContainsAny(name, "pole", "beam", "pillar", "column", "foundation", "floor", "wall", "roof", "stone", "marble", "blackmarble", "iron_")) {
      priorityRank = 2;
      reason = "structural_piece_name";
      return "structural_anchor";
    }

    if (distance > radiusMeters * 0.66f
        && ContainsAny(name, "rug", "banner", "curtain", "deco", "piece_banner", "piece_tapestry", "piece_xmas", "piece_maypole")) {
      priorityRank = 6;
      reason = "far_decorative_name";
      return "decorative_far";
    }

    priorityRank = 5;
    reason = "default_loaded_piece";
    return "support_piece";
  }

  static string[] CollectComponentNames(Component root) {
    SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    try {
      foreach (Component component in root.GetComponents<Component>()) {
        if (component) {
          names.Add(component.GetType().Name);
        }
      }

      foreach (Component component in root.GetComponentsInParent<Component>()) {
        if (component) {
          names.Add(component.GetType().Name);
        }
      }
    } catch {
      // Component inventory is best-effort only; classification can fall back to prefab names.
    }

    string[] result = new string[names.Count];
    names.CopyTo(result);
    return result;
  }

  static int ComparePriorityCandidates(PriorityCandidate left, PriorityCandidate right) {
    int rank = left.PriorityRank.CompareTo(right.PriorityRank);
    if (rank != 0) {
      return rank;
    }

    int distance = left.HorizontalDistanceMeters.CompareTo(right.HorizontalDistanceMeters);
    if (distance != 0) {
      return distance;
    }

    return string.Compare(left.ObjectName, right.ObjectName, StringComparison.OrdinalIgnoreCase);
  }

  static Dictionary<string, int> NewTierCounts() =>
      new(StringComparer.OrdinalIgnoreCase) {
          ["player_critical"] = 0,
          ["portal"] = 0,
          ["structural_anchor"] = 0,
          ["near_interactive"] = 0,
          ["storage_crafting"] = 0,
          ["support_piece"] = 0,
          ["decorative_far"] = 0
      };

  static void AddTierCounts(Dictionary<string, object> row, string prefix, Dictionary<string, int> counts) {
    foreach (KeyValuePair<string, int> pair in NewTierCounts()) {
      row[prefix + pair.Key + "_count"] = counts.TryGetValue(pair.Key, out int value) ? value : 0;
    }
  }

  static bool ContainsAny(string value, params string[] needles) {
    if (string.IsNullOrEmpty(value)) {
      return false;
    }

    foreach (string needle in needles) {
      if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) {
        return true;
      }
    }

    return false;
  }

  static string CleanObjectName(string value) {
    if (string.IsNullOrWhiteSpace(value)) {
      return string.Empty;
    }

    return value.Replace("(Clone)", string.Empty).Trim();
  }

  static string StableKey(string objectName, Vector3 position) =>
      string.Format(
          CultureInfo.InvariantCulture,
          "{0}@{1:0.0},{2:0.0},{3:0.0}",
          objectName ?? string.Empty,
          position.x,
          position.y,
          position.z);

  static string TryGetCreatorId(Piece piece) {
    if (!piece) {
      return string.Empty;
    }

    try {
      Type type = piece.GetType();
      if (!_creatorFieldCache.TryGetValue(type, out FieldInfo field)) {
        field =
            type.GetField("m_creator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type.GetField("m_creatorID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _creatorFieldCache[type] = field;
      }

      object value = field?.GetValue(piece);
      return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    } catch {
      return string.Empty;
    }
  }

  void NoteError(string error) {
    lock (_statsLock) {
      _status = "error";
      _lastError = error;
      _errors++;
    }
  }

  float RadiusMeters() => Mathf.Clamp(_radiusOverride ?? PluginConfig.LumberjacksPriorityProbeRadiusMeters.Value, 8.0f, 256.0f);

  float ScanIntervalSeconds() =>
      Mathf.Clamp(_intervalOverride ?? PluginConfig.LumberjacksPriorityProbeIntervalSeconds.Value, 0.5f, 30.0f);

  int MaxObjectsPerSample() =>
      Mathf.Clamp(_maxObjectsOverride ?? PluginConfig.LumberjacksPriorityProbeMaxObjectsPerSample.Value, 1, 512);

  static string ResolveRegionId(Vector3 position) {
    Vector2i zone = ZoneSystem.instance ? ZoneSystem.GetZone(position) : default;
    return $"{zone.x}:{zone.y}";
  }

  static string ScopeClaim() =>
      "Priority probe observes loaded local Valheim objects and writes a Lumberjacks-ready priority manifest only; it does not write ZDOs, change ZNetView ownership, correct transforms, or replace vanilla replication.";

  public void Dispose() {
    _running = false;
  }

  sealed class PriorityCandidate {
    public string ObjectKind;
    public string ObjectName;
    public int InstanceId;
    public string StableKey;
    public string CreatorId;
    public Vector3 Position;
    public int ScanOrder;
    public string PriorityTier;
    public int PriorityRank;
    public string PriorityReason;
    public float DistanceMeters;
    public float HorizontalDistanceMeters;
    public string[] ComponentNames;
  }
}
