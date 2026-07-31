namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using HarmonyLib;
using UnityEngine;

/// <summary>
/// C5 connected-world boundary. The server publishes its loaded-world descriptor on the canonical
/// session. For selected clients the native PeerInfo container deliberately carries blank world
/// fields; this runner either substitutes a validated Lumberjacks descriptor or holds PeerInfo
/// before scene entry. No default or native fallback world is admitted.
/// </summary>
public sealed class WorldZoneCutoverRunner : IDisposable {
  const string ReceiptFileName = "world-zone-cutover.jsonl";
  const int DescriptorProtocolVersion = 1;
  // assembly_valheim 0.221.12 Version.m_worldGenVersion. Version is internal in the
  // non-publicized compile reference, so the cutover contract names the verified wire value.
  const int ValheimWorldGenerationVersion = 2;
  const int MembershipObjectCount = 3;
  public const string MembershipTagName = "ComfyNetworkSense_C5Tag";
  public const string MembershipActionName = "ComfyNetworkSense_C5Action";
  public const string MembershipOrdinalName = "ComfyNetworkSense_C5Ordinal";
  static readonly int MembershipTagHash = MembershipTagName.GetStableHashCode();
  static readonly int MembershipActionHash = MembershipActionName.GetStableHashCode();
  static readonly int MembershipOrdinalHash = MembershipOrdinalName.GetStableHashCode();
  static readonly int MembershipPrefabHash =
      "ComfyNetworkSense_C5ZoneProbe".GetStableHashCode();
  static readonly MethodInfo RpcPeerInfoMethod = AccessTools.Method(
      typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) });
  static readonly MethodInfo CreateNewZdoMethod =
      AccessTools.Method(
          typeof(ZDOMan), "CreateNewZDO",
          new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
  static readonly MethodInfo HandleDestroyedZdoMethod =
      AccessTools.Method(
          typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
  static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>>
      ObjectsById = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>(
          "m_objectsByID");
  static readonly FieldInfo LocationIconsField =
      AccessTools.Field(typeof(ZoneSystem), "m_locationIcons");
  static readonly MethodInfo ClearGlobalKeysMethod =
      AccessTools.Method(typeof(ZoneSystem), "ClearGlobalKeys");
  static readonly MethodInfo GlobalKeyAddMethod =
      AccessTools.Method(
          typeof(ZoneSystem), "GlobalKeyAdd",
          new[] { typeof(string), typeof(bool) });
  static readonly MethodInfo GlobalKeyRemoveMethod =
      AccessTools.Method(
          typeof(ZoneSystem), "GlobalKeyRemove",
          new[] { typeof(string), typeof(bool) });
  static WorldZoneCutoverRunner _active;

  readonly LumberjacksGameSessionRunner _gameSession;
  readonly ConcurrentQueue<CanonicalFrame> _frames = new();
  readonly TelemetryLogWriter _writer = new();
  readonly HashSet<ZRpc> _replayBypass = new();
  readonly Dictionary<long, List<ZDOID>> _serverSnapshots = new();
  readonly HashSet<ZDOID> _serverSelected = new();
  readonly Dictionary<int, ZDOID> _clientObjects = new();

  WorldDescriptor _descriptor;
  ZRpc _pendingRpc;
  byte[] _pendingPeerInfo;
  string _publishedRunId = string.Empty;
  string _publishedConnectionId = string.Empty;
  float _nextPublishAt;
  float _nextRequestAt;
  bool _descriptorRejected;
  bool _worldBootstrapApplied;
  bool _worldBootstrapApplyFailed;
  bool _bootstrapTerminalWritten;
  float _respawnObservedAt;
  float _nextBootstrapProgressAt;
  MembershipProbe _membership;
  long _nextZoneEpoch;
  long _nativeSyncCandidates;
  long _nativeSyncSuppressed;
  float _nextNativeSuppressionReceiptAt;
  bool _disposed;

  public WorldZoneCutoverRunner(LumberjacksGameSessionRunner gameSession) {
    _gameSession = gameSession;
    WorldZoneCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public static void EnqueueCanonicalFrame(string type, string json) =>
      Volatile.Read(ref _active)?._frames.Enqueue(new(type, json));

  public static bool Selected =>
      PluginConfig.WorldZoneCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveWorldZoneCutover;

  public bool DescriptorAccepted => _descriptor != null && !_descriptorRejected;
  public bool DescriptorRejected => _descriptorRejected;

  public static bool TrySetPortalTraversal(
      bool enabled,
      out bool previous,
      out bool effective,
      out string detail) {
    previous = false;
    effective = false;
    detail = string.Empty;
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || ZNet.instance == null ||
        !ZNet.instance.IsServer() || ZoneSystem.instance == null ||
        GlobalKeyAddMethod == null || GlobalKeyRemoveMethod == null) {
      detail = "portal_traversal_runtime_not_ready";
      return false;
    }
    try {
      previous =
          !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals);
      if (enabled != previous) {
        if (enabled) {
          GlobalKeyRemoveMethod.Invoke(
              ZoneSystem.instance,
              new object[] { GlobalKeys.NoPortals.ToString(), false });
        } else {
          GlobalKeyAddMethod.Invoke(
              ZoneSystem.instance,
              new object[] { GlobalKeys.NoPortals.ToString(), false });
        }
      }
      effective =
          !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals);
      if (effective != enabled) {
        detail = "portal_traversal_runtime_state_mismatch";
        return false;
      }
      active._publishedRunId = string.Empty;
      active._publishedConnectionId = string.Empty;
      active._nextPublishAt = 0f;
      detail = previous == effective ? "unchanged" : "runtime_only";
      active.Write(
          "portal_traversal_runtime_changed", "server",
          "previous=" + previous + " effective=" + effective
          + " persistence=false");
      return true;
    } catch (Exception exception) {
      detail = "portal_traversal_runtime_failed:"
          + (exception.InnerException ?? exception).GetType().Name;
      return false;
    }
  }

  public static bool TryGetInitialZone(out Vector2i zone) {
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active?._descriptor == null || active._descriptorRejected) {
      zone = default;
      return false;
    }
    try {
      PlayerProfile profile = Game.instance?.GetPlayerProfile();
      if (profile != null && profile.HaveLogoutPoint()) {
        zone = ZoneSystem.GetZone(profile.GetLogoutPoint());
        return true;
      }
      if (profile != null && profile.HaveCustomSpawnPoint()) {
        zone = ZoneSystem.GetZone(profile.GetCustomSpawnPoint());
        return true;
      }
    } catch { }
    zone = new Vector2i(
        active._descriptor.initial_zone_x,
        active._descriptor.initial_zone_y);
    return true;
  }

  public bool TryCreateLogicalWorld(
      out World world,
      out double networkTime,
      out string worldEpoch,
      out string detail) {
    world = null;
    networkTime = 0;
    worldEpoch = string.Empty;
    detail = string.Empty;
    if (_descriptorRejected) {
      detail = "world_descriptor_rejected";
      return false;
    }
    if (_descriptor == null) {
      detail = "world_descriptor_not_ready";
      return false;
    }
    world = new World {
        m_name = _descriptor.world_name,
        m_seed = _descriptor.seed,
        m_seedName = _descriptor.seed_name,
        m_uid = _descriptor.world_uid,
        m_worldGenVersion = _descriptor.world_generation_version
    };
    networkTime = _descriptor.network_time;
    worldEpoch = _descriptor.world_epoch;
    return true;
  }

  public void Update(float now) {
    if (_disposed) return;
    DrainFrames();
    if (!Selected || ZNet.instance == null) {
      if (ZNet.instance != null && ZNet.instance.IsServer())
        CleanupServerSnapshots("cutover_disarmed");
      return;
    }

    if (ZNet.instance.IsServer()) {
      PublishDescriptor(now);
      return;
    }

    if (_descriptor == null && !_descriptorRejected &&
        _gameSession.WebSocketConnected && now >= _nextRequestAt) {
      _nextRequestAt = now + 1.0f;
      string runId = NativeAutotestRequest.ActiveRunId;
      if (SafeToken(runId, 80) && _gameSession.TryQueueWorldDescriptorRequest(
              "\"run_id\":\"" + Escape(runId)
              + "\",\"fault_mode\":\""
              + Escape(NativeAutotestRequest.ActiveWorldDescriptorFault) + "\"")) {
        Write("descriptor_requested", "client",
            "fault_mode=" + EmptyAsNone(NativeAutotestRequest.ActiveWorldDescriptorFault));
      }
    }

    if (_descriptor != null && _pendingRpc != null && _pendingPeerInfo != null)
      ResumePendingPeerInfo();
    ApplyWorldBootstrap();
    EvaluateBootstrap(now);
    EvaluateMembershipDeadline(now);
  }

  /// A reincarnated Gateway holds no world descriptor until the dedicated
  /// server reconnects and republishes, but the client cached its descriptor
  /// once and the re-request gate never re-arms - so the first zone action
  /// after reincarnation ran against pre-restart state and its snapshot
  /// ladder wedged (full30 i5, applied=1 replayed=1 complete_count=0).
  /// Dropping the cache re-arms both the descriptor re-request and
  /// BeginMembershipProbe's world_zone_client_not_ready guard, so zone
  /// actions wait for a descriptor confirmed against the new incarnation.
  public static void NotifySessionReincarnated() {
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || ZNet.instance == null || ZNet.instance.IsServer())
      return;
    active._descriptor = null;
    active._descriptorRejected = false;
    active._nextRequestAt = 0f;
    active.Write("reincarnation_descriptor_rearm_requested", "client", string.Empty);
  }

  void PublishDescriptor(float now) {
    if (!_gameSession.WebSocketConnected || now < _nextPublishAt) return;
    string runId = PluginConfig.NativeNetworkEvidenceRunId?.Value ?? string.Empty;
    string connectionId = _gameSession.ConnectionId;
    if (!SafeToken(runId, 80) || string.IsNullOrEmpty(connectionId) ||
        (string.Equals(runId, _publishedRunId, StringComparison.Ordinal) &&
         string.Equals(connectionId, _publishedConnectionId, StringComparison.Ordinal)))
      return;
    World world = ZNet.GetWorldIfIsHost();
    if (world == null || world.m_uid == 0 || string.IsNullOrWhiteSpace(world.m_name) ||
        string.IsNullOrWhiteSpace(world.m_seedName)) return;

    if (ZoneSystem.instance == null || Game.instance == null) return;
    string startLocationName = Game.instance.m_StartLocation;
    if (string.IsNullOrWhiteSpace(startLocationName) ||
        !ZoneSystem.instance.GetLocationIcon(
            startLocationName, out Vector3 startLocation) ||
        !Finite(startLocation))
      return;
    Dictionary<Vector3, string> locationIcons = new();
    ZoneSystem.instance.GetLocationIcons(locationIcons);
    if (locationIcons.Count is < 1 or > 4096 ||
        !locationIcons.Any(item =>
            string.Equals(
                item.Value, startLocationName, StringComparison.Ordinal)))
      return;
    List<string> globalKeys = ZoneSystem.instance.GetGlobalKeys();
    if (globalKeys.Count > 512) return;
    globalKeys.Sort(StringComparer.Ordinal);
    List<KeyValuePair<Vector3, string>> orderedIcons = locationIcons
        .Where(item => Finite(item.Key) &&
            !string.IsNullOrWhiteSpace(item.Value) &&
            item.Value.Length <= 128)
        .OrderBy(item => item.Value, StringComparer.Ordinal)
        .ThenBy(item => item.Key.x)
        .ThenBy(item => item.Key.y)
        .ThenBy(item => item.Key.z)
        .ToList();
    if (orderedIcons.Count != locationIcons.Count) return;

    Vector2i zone = ZoneSystem.GetZone(startLocation);
    string worldEpoch = "world-" + unchecked((ulong)world.m_uid)
        .ToString("x16", CultureInfo.InvariantCulture);
    string fields =
        "\"schema_version\":1"
        + ",\"protocol_version\":" + DescriptorProtocolVersion
        + ",\"release_id\":\"" + Escape(ComfyNetworkSense.ReleaseId) + "\""
        + ",\"run_id\":\"" + Escape(runId) + "\""
        + ",\"world_epoch\":\"" + worldEpoch + "\""
        + ",\"world_name\":\"" + Escape(world.m_name) + "\""
        + ",\"seed\":" + world.m_seed.ToString(CultureInfo.InvariantCulture)
        + ",\"seed_name\":\"" + Escape(world.m_seedName) + "\""
        + ",\"world_uid\":" + world.m_uid.ToString(CultureInfo.InvariantCulture)
        + ",\"world_generation_version\":"
        + world.m_worldGenVersion.ToString(CultureInfo.InvariantCulture)
        + ",\"network_time\":"
        + ZNet.instance.GetTimeSeconds().ToString("R", CultureInfo.InvariantCulture)
        + ",\"save_epoch\":1"
        + ",\"initial_zone_x\":" + zone.x.ToString(CultureInfo.InvariantCulture)
        + ",\"initial_zone_y\":" + zone.y.ToString(CultureInfo.InvariantCulture)
        + ",\"start_location_name\":\"" + Escape(startLocationName) + "\""
        + ",\"global_keys\":" + JsonStringArray(globalKeys)
        + ",\"location_icons\":" + JsonLocationIcons(orderedIcons);
    if (!_gameSession.TryQueueWorldDescriptorPublish(fields)) return;
    _publishedRunId = runId;
    _publishedConnectionId = connectionId;
    _nextPublishAt = now + 2.0f;
    Write("descriptor_published", "server",
        "world_epoch=" + worldEpoch
        + " world_generation_version=" + world.m_worldGenVersion
        + " initial_zone=" + zone.x + "," + zone.y
        + " global_keys=" + globalKeys.Count
        + " location_icons=" + orderedIcons.Count);
  }

  void DrainFrames() {
    while (_frames.TryDequeue(out CanonicalFrame frame)) {
      string envelopeType = ExtractJsonString(frame.Json, "type");
      long reliableSequence = ExtractJsonLong(frame.Json, "seq");
      if (!string.Equals(envelopeType, frame.Type, StringComparison.OrdinalIgnoreCase)) {
        RejectDescriptor("descriptor_envelope_invalid");
        Acknowledge(reliableSequence);
        continue;
      }

      if (frame.Type == "valheim_world_descriptor") {
        WorldDescriptor descriptor = ParseDescriptor(frame.Json);
        string validation = Validate(descriptor);
        if (!string.IsNullOrEmpty(validation)) {
          RejectDescriptor(validation);
          Acknowledge(reliableSequence);
          continue;
        }
        _descriptor = descriptor;
        Write("descriptor_accepted", "client",
            "world_epoch=" + _descriptor.world_epoch
            + " world_generation_version=" + _descriptor.world_generation_version
            + " initial_zone=" + _descriptor.initial_zone_x + ","
            + _descriptor.initial_zone_y
            + " global_keys=" + _descriptor.global_keys.Length
            + " location_icons=" + _descriptor.location_icons.Length);
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_world_descriptor_receipt") {
        Write("descriptor_publish_receipt", "server",
            "reliable_sequence=" + reliableSequence);
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_world_descriptor_status") {
        Write("descriptor_waiting", "client", "server_not_published");
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_zone_snapshot_build") {
        HandleSnapshotBuild(frame.Json);
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_zone_snapshot_chunk") {
        HandleSnapshotChunk(frame.Json, reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_zone_snapshot_complete") {
        HandleSnapshotComplete(frame.Json);
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_zone_snapshot_release") {
        HandleSnapshotRelease(frame.Json);
        Acknowledge(reliableSequence);
        continue;
      }
      if (frame.Type == "valheim_zone_membership_left") {
        HandleMembershipLeft(frame.Json);
        Acknowledge(reliableSequence);
        continue;
      }
      Acknowledge(reliableSequence);
    }
  }

  public bool BeginMembershipProbe(
      string actionId, string mode, float deadlineSeconds, out string detail) {
    detail = string.Empty;
    if (!Selected || ZNet.instance == null || ZNet.instance.IsServer() ||
        Player.m_localPlayer == null || _descriptor == null) {
      detail = "world_zone_client_not_ready";
      return false;
    }
    if (!SafeToken(actionId, 80) || mode is not ("resume_once" or "withhold")) {
      detail = "world_zone_probe_parameters_invalid";
      return false;
    }
    if (_membership != null && !_membership.Terminal) {
      detail = "world_zone_probe_already_active";
      return false;
    }
    Vector3 position = ((Component)Player.m_localPlayer).transform.position;
    Vector2i zone = ZoneSystem.GetZone(position);
    long zoneEpoch = Interlocked.Increment(ref _nextZoneEpoch);
    _clientObjects.Clear();
    _membership = new() {
        ActionId = actionId,
        Mode = mode,
        WorldEpoch = _descriptor.world_epoch,
        ZoneEpoch = zoneEpoch,
        ZoneX = zone.x,
        ZoneY = zone.y,
        DeadlineAt = Time.unscaledTime + Mathf.Clamp(deadlineSeconds, 5.0f, 300.0f)
    };
    string fields =
        "\"run_id\":\"" + Escape(NativeAutotestRequest.ActiveRunId)
        + "\",\"action_id\":\"" + Escape(actionId)
        + "\",\"world_epoch\":\"" + Escape(_descriptor.world_epoch)
        + "\",\"zone_epoch\":" + zoneEpoch.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_x\":" + zone.x.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_y\":" + zone.y.ToString(CultureInfo.InvariantCulture)
        + ",\"mode\":\"" + mode + "\"";
    if (!_gameSession.TryQueueZoneMembershipEnter(fields)) {
      _membership = null;
      detail = "world_zone_enter_queue_full";
      return false;
    }
    Write("membership_enter_queued", NativeAutotestRequest.ActiveClient,
        "action_id=" + actionId + " mode=" + mode
        + " zone_epoch=" + zoneEpoch + " zone=" + zone.x + "," + zone.y);
    detail = "world_zone_enter_queued";
    return true;
  }

  public bool TryGetMembershipProbeResult(
      string actionId, out bool terminal, out bool success, out string detail) {
    if (_membership == null ||
        !string.Equals(_membership.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "world_zone_probe_not_found";
      return false;
    }
    terminal = _membership.Terminal;
    success = _membership.Success;
    detail = _membership.Detail ?? string.Empty;
    return true;
  }

  void HandleSnapshotBuild(string json) {
    if (ZNet.instance == null || !ZNet.instance.IsServer())
      throw new InvalidDataException("zone_snapshot_build_role_invalid");
    string recipient = ExtractJsonString(json, "recipient_id");
    string runId = ExtractJsonString(json, "run_id");
    string actionId = ExtractJsonString(json, "action_id");
    string worldEpoch = ExtractJsonString(json, "world_epoch");
    long zoneEpoch = ExtractJsonLong(json, "zone_epoch");
    long snapshotEpoch = ExtractJsonLong(json, "snapshot_epoch");
    int zoneX = checked((int)ExtractJsonLong(json, "zone_x"));
    int zoneY = checked((int)ExtractJsonLong(json, "zone_y"));
    string currentRun = PluginConfig.NativeNetworkEvidenceRunId?.Value ?? string.Empty;
    if (!SafeToken(recipient, 80) || !SafeToken(runId, 80) ||
        !SafeToken(actionId, 80) || !SafeToken(worldEpoch, 96) ||
        !string.Equals(runId, currentRun, StringComparison.Ordinal) ||
        !string.Equals(worldEpoch, CurrentWorldEpoch(), StringComparison.Ordinal) ||
        zoneEpoch < 1 || snapshotEpoch < 1)
      throw new InvalidDataException("zone_snapshot_build_scope_invalid");

    if (_serverSnapshots.ContainsKey(snapshotEpoch)) {
      Write("snapshot_build_duplicate", "server",
          "snapshot_epoch=" + snapshotEpoch + " recipient_id=" + recipient);
      return;
    }

    Vector3 center = ZoneSystem.GetZonePos(new Vector2i(zoneX, zoneY));
    List<ZDOID> ids = new();
    StringBuilder objects = new();
    for (int ordinal = 1; ordinal <= MembershipObjectCount; ordinal++) {
      Vector3 position = center + new Vector3(ordinal * 2.0f, 0.0f, ordinal);
      ZDO zdo = ZDOMan.instance.CreateNewZDO(position, MembershipPrefabHash);
      _serverSelected.Add(zdo.m_uid);
      // ZDOMan's prefabHashIn parameter only updates its portal index; it does not
      // populate the ZDO's serialized prefab field.
      zdo.SetPrefab(MembershipPrefabHash);
      zdo.Set(MembershipTagHash, runId);
      zdo.Set(MembershipActionHash, actionId);
      zdo.Set(MembershipOrdinalHash, ordinal);
      ZPackage body = new();
      zdo.Serialize(body);
      if (objects.Length > 0) objects.Append(',');
      objects.Append("{\"uid_user\":")
          .Append(zdo.m_uid.UserID.ToString(CultureInfo.InvariantCulture))
          .Append(",\"uid_id\":")
          .Append(zdo.m_uid.ID.ToString(CultureInfo.InvariantCulture))
          .Append(",\"prefab\":")
          .Append(zdo.GetPrefab().ToString(CultureInfo.InvariantCulture))
          .Append(",\"owner\":")
          .Append(zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
          .Append(",\"owner_rev\":")
          .Append(zdo.OwnerRevision.ToString(CultureInfo.InvariantCulture))
          .Append(",\"data_rev\":")
          .Append(zdo.DataRevision.ToString(CultureInfo.InvariantCulture))
          .Append(",\"position_x\":")
          .Append(position.x.ToString("R", CultureInfo.InvariantCulture))
          .Append(",\"position_y\":")
          .Append(position.y.ToString("R", CultureInfo.InvariantCulture))
          .Append(",\"position_z\":")
          .Append(position.z.ToString("R", CultureInfo.InvariantCulture))
          .Append(",\"body_b64\":\"").Append(body.GetBase64()).Append("\"}");
      ids.Add(zdo.m_uid);
    }
    _serverSnapshots[snapshotEpoch] = ids;
    string fields =
        "\"recipient_id\":\"" + Escape(recipient)
        + "\",\"run_id\":\"" + Escape(runId)
        + "\",\"action_id\":\"" + Escape(actionId)
        + "\",\"world_epoch\":\"" + Escape(worldEpoch)
        + "\",\"zone_epoch\":" + zoneEpoch.ToString(CultureInfo.InvariantCulture)
        + ",\"snapshot_epoch\":" + snapshotEpoch.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_x\":" + zoneX.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_y\":" + zoneY.ToString(CultureInfo.InvariantCulture)
        + ",\"objects\":[" + objects + "]";
    if (!_gameSession.TryQueueZoneSnapshotPublish(fields))
      throw new InvalidOperationException("zone_snapshot_publish_queue_full");
    Write("snapshot_published", "server",
        "action_id=" + actionId + " recipient_id=" + recipient
        + " snapshot_epoch=" + snapshotEpoch
        + " object_count=" + ids.Count + " zone=" + zoneX + "," + zoneY);
  }

  void HandleSnapshotChunk(string json, long reliableSequence) {
    if (_membership == null || _membership.Terminal)
      throw new InvalidDataException("zone_snapshot_chunk_without_probe");
    string runId = ExtractJsonString(json, "run_id");
    string actionId = ExtractJsonString(json, "action_id");
    string worldEpoch = ExtractJsonString(json, "world_epoch");
    long zoneEpoch = ExtractJsonLong(json, "zone_epoch");
    long snapshotEpoch = ExtractJsonLong(json, "snapshot_epoch");
    int chunkIndex = checked((int)ExtractJsonLong(json, "chunk_index"));
    int chunkCount = checked((int)ExtractJsonLong(json, "chunk_count"));
    if (!MatchesMembership(
            runId, actionId, worldEpoch, zoneEpoch, snapshotEpoch) ||
        chunkIndex is < 1 or > MembershipObjectCount ||
        chunkCount != MembershipObjectCount || reliableSequence < 1)
      throw new InvalidDataException("zone_snapshot_chunk_scope_invalid");
    _membership.SnapshotEpoch = snapshotEpoch;

    if (_membership.AppliedChunks.Contains(chunkIndex)) {
      _membership.ReplayedChunks++;
      Write("snapshot_chunk_replayed_idempotent", NativeAutotestRequest.ActiveClient,
          "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
          + " chunk_index=" + chunkIndex
          + " reliable_sequence=" + reliableSequence);
      QueueChunkAcks(snapshotEpoch, chunkIndex, reliableSequence);
      return;
    }

    long uidUser = ExtractJsonLong(json, "uid_user");
    long uidIdLong = ExtractJsonLong(json, "uid_id");
    long prefabLong = ExtractJsonLong(json, "prefab");
    long owner = ExtractJsonLong(json, "owner");
    long ownerRevision = ExtractJsonLong(json, "owner_rev");
    long dataRevision = ExtractJsonLong(json, "data_rev");
    float x = checked((float)ExtractJsonDouble(json, "position_x"));
    float y = checked((float)ExtractJsonDouble(json, "position_y"));
    float z = checked((float)ExtractJsonDouble(json, "position_z"));
    string bodyBase64 = ExtractJsonString(json, "body_b64");
    if (uidUser == 0 || uidIdLong is <= 0 or > uint.MaxValue ||
        prefabLong is < int.MinValue or > int.MaxValue ||
        ownerRevision is < 0 or > ushort.MaxValue ||
        dataRevision is < 0 or > uint.MaxValue || !Finite(new Vector3(x, y, z)))
      throw new InvalidDataException("zone_snapshot_object_invalid");
    byte[] body;
    try { body = Convert.FromBase64String(bodyBase64); }
    catch { throw new InvalidDataException("zone_snapshot_body_invalid"); }
    if (body.Length is < 1 or > 1_000_000)
      throw new InvalidDataException("zone_snapshot_body_size_invalid");

    ZDOID uid = new(uidUser, checked((uint)uidIdLong));
    ZDO target = ZDOMan.instance.GetZDO(uid);
    if (target != null)
      throw new InvalidDataException("zone_snapshot_unexpected_existing_object");
    Vector3 position = new(x, y, z);
    target = (ZDO)CreateNewZdoMethod.Invoke(
        ZDOMan.instance,
        new object[] { uid, position, checked((int)prefabLong) });
    target.OwnerRevision = checked((ushort)ownerRevision);
    target.DataRevision = checked((uint)dataRevision);
    target.SetOwnerInternal(owner);
    target.InternalSetPosition(position);
    target.Deserialize(new ZPackage(body));
    if (target.GetPrefab() != checked((int)prefabLong) ||
        target.GetString(MembershipTagHash, string.Empty) != runId ||
        target.GetString(MembershipActionHash, string.Empty) != actionId ||
        target.GetInt(MembershipOrdinalHash, 0) != chunkIndex)
      throw new InvalidDataException("zone_snapshot_typed_readback_mismatch");
    _clientObjects[chunkIndex] = uid;
    _membership.AppliedChunks.Add(chunkIndex);
    Write("snapshot_chunk_applied_typed", NativeAutotestRequest.ActiveClient,
        "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
        + " chunk_index=" + chunkIndex + " uid=" + uid
        + " source=lumberjacks");

    if (_membership.Mode == "resume_once" && !_membership.DropPerformed) {
      _membership.DropPerformed = true;
      Write("snapshot_socket_dropped_before_ack", NativeAutotestRequest.ActiveClient,
          "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
          + " chunk_index=" + chunkIndex
          + " reliable_sequence=" + reliableSequence);
      if (!_gameSession.AbortForOwnershipProbe())
        FailMembership("zone_snapshot_socket_abort_failed");
      return;
    }
    QueueChunkAcks(snapshotEpoch, chunkIndex, reliableSequence);
  }

  void QueueChunkAcks(
      long snapshotEpoch, int chunkIndex, long reliableSequence) {
    string fields =
        "\"snapshot_epoch\":" + snapshotEpoch.ToString(CultureInfo.InvariantCulture)
        + ",\"chunk_index\":" + chunkIndex.ToString(CultureInfo.InvariantCulture);
    if (!_gameSession.QueueReliableAck(reliableSequence) ||
        !_gameSession.TryQueueZoneSnapshotAck(fields)) {
      FailMembership("zone_snapshot_ack_queue_full");
      return;
    }
    Write("snapshot_chunk_ack_queued", NativeAutotestRequest.ActiveClient,
        "action_id=" + _membership.ActionId
        + " snapshot_epoch=" + snapshotEpoch
        + " chunk_index=" + chunkIndex
        + " reliable_sequence=" + reliableSequence);
  }

  void HandleSnapshotComplete(string json) {
    if (_membership == null || _membership.Terminal)
      throw new InvalidDataException("zone_snapshot_complete_without_probe");
    string runId = ExtractJsonString(json, "run_id");
    string actionId = ExtractJsonString(json, "action_id");
    string worldEpoch = ExtractJsonString(json, "world_epoch");
    long zoneEpoch = ExtractJsonLong(json, "zone_epoch");
    long snapshotEpoch = ExtractJsonLong(json, "snapshot_epoch");
    int chunkCount = checked((int)ExtractJsonLong(json, "chunk_count"));
    int objectCount = checked((int)ExtractJsonLong(json, "object_count"));
    bool withheld = ExtractJsonBool(json, "withheld");
    if (!MatchesMembership(
            runId, actionId, worldEpoch, zoneEpoch, snapshotEpoch))
      throw new InvalidDataException("zone_snapshot_complete_scope_invalid");
    _membership.SnapshotEpoch = snapshotEpoch;
    _membership.CompleteCount++;
    if (_membership.CompleteCount != 1)
      throw new InvalidDataException("zone_snapshot_complete_duplicate");

    if (_membership.Mode == "withhold") {
      int present = CountMembershipObjects(runId, actionId);
      if (!withheld || chunkCount != 0 || objectCount != 0 ||
          _membership.AppliedChunks.Count != 0 || present != 0) {
        FailMembership("withheld_membership_spawned_object");
        return;
      }
      Write("withheld_membership_no_spawn", NativeAutotestRequest.ActiveClient,
          "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
          + " object_count=0 local_membership_objects=0");
      CompleteMembership(
          "withheld_membership_no_spawn complete_count=1 local_membership_objects=0");
      return;
    }

    if (withheld || chunkCount != MembershipObjectCount ||
        objectCount != MembershipObjectCount ||
        _membership.AppliedChunks.Count != MembershipObjectCount ||
        CountMembershipObjects(runId, actionId) != MembershipObjectCount) {
      FailMembership("zone_snapshot_complete_readback_mismatch");
      return;
    }
    Write("snapshot_complete_once", NativeAutotestRequest.ActiveClient,
        "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
        + " chunk_count=" + chunkCount + " object_count=" + objectCount);
    string fields =
        "\"run_id\":\"" + Escape(runId)
        + "\",\"action_id\":\"" + Escape(actionId)
        + "\",\"snapshot_epoch\":" + snapshotEpoch.ToString(CultureInfo.InvariantCulture);
    if (!_gameSession.TryQueueZoneMembershipLeave(fields)) {
      FailMembership("zone_membership_leave_queue_full");
      return;
    }
    Write("membership_leave_queued", NativeAutotestRequest.ActiveClient,
        "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch);
  }

  void HandleSnapshotRelease(string json) {
    if (ZNet.instance == null || !ZNet.instance.IsServer())
      throw new InvalidDataException("zone_snapshot_release_role_invalid");
    long snapshotEpoch = ExtractJsonLong(json, "snapshot_epoch");
    string actionId = ExtractJsonString(json, "action_id");
    if (!_serverSnapshots.TryGetValue(snapshotEpoch, out List<ZDOID> ids))
      throw new InvalidDataException("zone_snapshot_release_unknown");
    int destroyed = 0;
    foreach (ZDOID uid in ids) {
      if (ZDOMan.instance.GetZDO(uid) != null) {
        HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
        destroyed++;
      }
      _serverSelected.Remove(uid);
    }
    _serverSnapshots.Remove(snapshotEpoch);
    if (ids.Any(uid => ZDOMan.instance.GetZDO(uid) != null))
      throw new InvalidOperationException("zone_snapshot_server_release_stale");
    Write("snapshot_released_typed", "server",
        "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
        + " destroyed=" + destroyed
        + " native_sync_candidates=" + Interlocked.Read(ref _nativeSyncCandidates)
        + " native_sync_suppressed=" + Interlocked.Read(ref _nativeSyncSuppressed));
  }

  void HandleMembershipLeft(string json) {
    if (_membership == null || _membership.Terminal)
      throw new InvalidDataException("zone_membership_left_without_probe");
    long snapshotEpoch = ExtractJsonLong(json, "snapshot_epoch");
    string actionId = ExtractJsonString(json, "action_id");
    if (snapshotEpoch != _membership.SnapshotEpoch ||
        !string.Equals(actionId, _membership.ActionId, StringComparison.Ordinal))
      throw new InvalidDataException("zone_membership_left_scope_invalid");
    int destroyed = 0;
    foreach (ZDOID uid in _clientObjects.Values.ToArray()) {
      if (ZDOMan.instance.GetZDO(uid) != null) {
        HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
        destroyed++;
      }
    }
    _clientObjects.Clear();
    int present = CountMembershipObjects(
        NativeAutotestRequest.ActiveRunId, _membership.ActionId);
    if (present != 0) {
      FailMembership("zone_membership_leave_stale_objects=" + present);
      return;
    }
    Write("membership_left_no_stale_objects", NativeAutotestRequest.ActiveClient,
        "action_id=" + actionId + " snapshot_epoch=" + snapshotEpoch
        + " destroyed=" + destroyed + " local_membership_objects=0");
    CompleteMembership(
        "snapshot_applied=3 replayed=" + _membership.ReplayedChunks
        + " complete_count=" + _membership.CompleteCount
        + " destroyed=" + destroyed + " stale=0");
  }

  bool MatchesMembership(
      string runId, string actionId, string worldEpoch,
      long zoneEpoch, long snapshotEpoch) =>
      _membership != null &&
      string.Equals(runId, NativeAutotestRequest.ActiveRunId, StringComparison.Ordinal) &&
      string.Equals(actionId, _membership.ActionId, StringComparison.Ordinal) &&
      string.Equals(worldEpoch, _membership.WorldEpoch, StringComparison.Ordinal) &&
      zoneEpoch == _membership.ZoneEpoch &&
      (snapshotEpoch > 0 &&
       (_membership.SnapshotEpoch == 0 || snapshotEpoch == _membership.SnapshotEpoch));

  void Acknowledge(long reliableSequence) {
    if (reliableSequence > 0 && !_gameSession.QueueReliableAck(reliableSequence))
      Write("reliable_ack_queue_failed", "session",
          "reliable_sequence=" + reliableSequence);
  }

  void EvaluateMembershipDeadline(float now) {
    if (_membership == null || _membership.Terminal ||
        now <= _membership.DeadlineAt) return;
    FailMembership(
        "world_zone_probe_deadline applied=" + _membership.AppliedChunks.Count
        + " replayed=" + _membership.ReplayedChunks
        + " complete_count=" + _membership.CompleteCount);
  }

  void CompleteMembership(string detail) {
    _membership.Terminal = true;
    _membership.Success = true;
    _membership.Detail = detail;
    Write("membership_probe_passed", NativeAutotestRequest.ActiveClient,
        "action_id=" + _membership.ActionId + " " + detail);
  }

  void FailMembership(string detail) {
    if (_membership == null || _membership.Terminal) return;
    foreach (ZDOID uid in _clientObjects.Values.ToArray())
      if (ZDOMan.instance?.GetZDO(uid) != null)
        HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
    _clientObjects.Clear();
    _membership.Terminal = true;
    _membership.Success = false;
    _membership.Detail = detail;
    Write("membership_probe_failed", NativeAutotestRequest.ActiveClient,
        "action_id=" + _membership.ActionId + " " + detail);
  }

  static int CountMembershipObjects(string runId, string actionId) {
    if (ZDOMan.instance == null) return 0;
    try {
      return ObjectsById(ZDOMan.instance).Values.Count(zdo =>
          zdo != null &&
          string.Equals(
              zdo.GetString(MembershipTagHash, string.Empty),
              runId, StringComparison.Ordinal) &&
          string.Equals(
              zdo.GetString(MembershipActionHash, string.Empty),
              actionId, StringComparison.Ordinal));
    } catch {
      return -1;
    }
  }

  static string CurrentWorldEpoch() {
    try {
      return ZNet.instance == null ? string.Empty :
          "world-" + unchecked((ulong)ZNet.instance.GetWorldUID())
              .ToString("x16", CultureInfo.InvariantCulture);
    } catch {
      return string.Empty;
    }
  }

  public static void FilterNativeSync(List<ZDO> toSync) {
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected || ZNet.instance == null ||
        !ZNet.instance.IsServer() || toSync == null) return;
    int removed = 0;
    for (int index = toSync.Count - 1; index >= 0; index--) {
      ZDO candidate = toSync[index];
      if (candidate == null || !active._serverSelected.Contains(candidate.m_uid))
        continue;
      Interlocked.Increment(ref active._nativeSyncCandidates);
      Interlocked.Increment(ref active._nativeSyncSuppressed);
      toSync.RemoveAt(index);
      removed++;
    }
    float now = Time.unscaledTime;
    if (removed > 0 && now >= active._nextNativeSuppressionReceiptAt) {
      active._nextNativeSuppressionReceiptAt = now + 1.0f;
      active.Write("native_zone_membership_suppressed", "server",
          "removed=" + removed
          + " candidates_total=" + Interlocked.Read(ref active._nativeSyncCandidates)
          + " suppressed_total=" + Interlocked.Read(ref active._nativeSyncSuppressed));
    }
  }

  void CleanupServerSnapshots(string reason) {
    if (_serverSnapshots.Count == 0 && _serverSelected.Count == 0) return;
    int destroyed = 0;
    foreach (ZDOID uid in _serverSelected.ToArray()) {
      if (ZDOMan.instance?.GetZDO(uid) == null) continue;
      HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
      destroyed++;
    }
    _serverSnapshots.Clear();
    _serverSelected.Clear();
    Write("snapshot_cleanup", "server",
        "reason=" + reason + " destroyed=" + destroyed);
  }

  static WorldDescriptor ParseDescriptor(string json) {
    try {
      DataContractJsonSerializer serializer =
          new(typeof(WorldDescriptorEnvelope));
      using MemoryStream stream =
          new(Encoding.UTF8.GetBytes(json ?? string.Empty));
      return (serializer.ReadObject(stream) as WorldDescriptorEnvelope)?.payload;
    } catch {
      return null;
    }
  }

  string Validate(WorldDescriptor value) {
    if (value == null || value.schema_version != 1 ||
        value.protocol_version != DescriptorProtocolVersion)
      return "descriptor_protocol_mismatch";
    if (!string.Equals(value.release_id, ComfyNetworkSense.ReleaseId,
            StringComparison.Ordinal))
      return "descriptor_release_mismatch";
    if (!string.Equals(value.run_id, NativeAutotestRequest.ActiveRunId,
            StringComparison.Ordinal))
      return "descriptor_run_mismatch";
    if (!SafeToken(value.world_epoch, 96) ||
        string.IsNullOrWhiteSpace(value.world_name) || value.world_name.Length > 80 ||
        string.IsNullOrWhiteSpace(value.seed_name) || value.seed_name.Length > 128 ||
        value.world_uid == 0 || value.save_epoch <= 0)
      return "descriptor_fields_invalid";
    if (value.world_generation_version != ValheimWorldGenerationVersion)
      return "descriptor_world_generation_mismatch";
    if (double.IsNaN(value.network_time) || double.IsInfinity(value.network_time) ||
        value.network_time < 0)
      return "descriptor_network_time_invalid";
    if (string.IsNullOrWhiteSpace(value.start_location_name) ||
        value.start_location_name.Length > 128 ||
        value.global_keys == null || value.global_keys.Length > 512 ||
        value.global_keys.Any(key =>
            string.IsNullOrWhiteSpace(key) || key.Length > 256 ||
            key.Any(char.IsControl)) ||
        value.location_icons == null ||
        value.location_icons.Length is < 1 or > 4096 ||
        value.location_icons.Any(icon =>
            icon == null || string.IsNullOrWhiteSpace(icon.name) ||
            icon.name.Length > 128 || icon.name.Any(char.IsControl) ||
            !Finite(new Vector3(icon.x, icon.y, icon.z))) ||
        !value.location_icons.Any(icon =>
            string.Equals(
                icon.name, value.start_location_name,
                StringComparison.Ordinal)))
      return "descriptor_bootstrap_invalid";
    return string.Empty;
  }

  void ApplyWorldBootstrap() {
    if (_descriptor == null || _descriptorRejected ||
        _worldBootstrapApplied || _worldBootstrapApplyFailed ||
        ZoneSystem.instance == null || ZNet.World == null)
      return;
    try {
      if (LocationIconsField?.GetValue(ZoneSystem.instance)
              is not Dictionary<Vector3, string> locationIcons ||
          ClearGlobalKeysMethod == null || GlobalKeyAddMethod == null)
        throw new MissingMemberException("world_bootstrap_consumer_unavailable");

      locationIcons.Clear();
      foreach (WorldLocationIcon icon in _descriptor.location_icons)
        locationIcons[new Vector3(icon.x, icon.y, icon.z)] = icon.name;

      ClearGlobalKeysMethod.Invoke(ZoneSystem.instance, Array.Empty<object>());
      foreach (string key in _descriptor.global_keys)
        GlobalKeyAddMethod.Invoke(
            ZoneSystem.instance, new object[] { key, false });

      _worldBootstrapApplied = true;
      Write("world_bootstrap_applied_typed", "client",
          "start_location=" + _descriptor.start_location_name
          + " global_keys=" + _descriptor.global_keys.Length
          + " location_icons=" + _descriptor.location_icons.Length
          + " native_routed_rpc=false");
    } catch (Exception exception) {
      _worldBootstrapApplyFailed = true;
      Exception cause = exception.InnerException ?? exception;
      BootstrapFailed(
          "world_bootstrap_apply_failed:" + cause.GetType().Name);
    }
  }

  void EvaluateBootstrap(float now) {
    if (_bootstrapTerminalWritten || _descriptor == null ||
        !NativeAutotestRequest.ActiveSteamFreeColdJoin ||
        Game.instance == null || ZNet.instance == null ||
        ZNet.instance.IsServer())
      return;
    if (Player.m_localPlayer != null) {
      _bootstrapTerminalWritten = true;
      Write("bootstrap_ready", NativeAutotestRequest.ActiveClient,
          BootstrapDetail());
      return;
    }
    if (!Game.instance.WaitingForRespawn()) return;
    if (_respawnObservedAt <= 0f) {
      _respawnObservedAt = now;
      _nextBootstrapProgressAt = now;
    }
    if (now >= _nextBootstrapProgressAt) {
      _nextBootstrapProgressAt = now + 2.0f;
      Write("bootstrap_progress", NativeAutotestRequest.ActiveClient,
          BootstrapDetail());
    }
    if (!_worldBootstrapApplied && now - _respawnObservedAt >= 2.0f) {
      BootstrapFailed("world_bootstrap_not_applied");
      return;
    }
    if (now - _respawnObservedAt >= 25.0f)
      BootstrapFailed("respawn_contract_deadline");
  }

  string BootstrapDetail() {
    string startName = _descriptor?.start_location_name ?? string.Empty;
    bool startIcon = false;
    Vector3 startPosition = Vector3.zero;
    try {
      startIcon = ZoneSystem.instance != null &&
          ZoneSystem.instance.GetLocationIcon(startName, out startPosition);
    } catch { }
    Vector3 reference = Vector3.zero;
    try { reference = ZNet.instance?.GetReferencePosition() ?? Vector3.zero; }
    catch { }
    Vector2i referenceZone = ZoneSystem.GetZone(reference);
    bool zoneLoaded = false;
    bool areaReady = false;
    try {
      zoneLoaded = ZoneSystem.instance != null &&
          ZoneSystem.instance.IsZoneLoaded(referenceZone);
      areaReady = ZNetScene.instance != null &&
          ZNetScene.instance.IsAreaReady(reference);
    } catch { }
    return "bootstrap_applied=" + _worldBootstrapApplied
        + " start_icon=" + startIcon
        + " start_position=" + Format(startPosition)
        + " reference_position=" + Format(reference)
        + " reference_zone=" + referenceZone.x + "," + referenceZone.y
        + " zone_loaded=" + zoneLoaded
        + " area_ready=" + areaReady
        + " local_player=" + (Player.m_localPlayer != null)
        + " " + ZdoJournalCutoverRunner.DescribeActiveRuntimeProgress();
  }

  void BootstrapFailed(string reason) {
    if (_bootstrapTerminalWritten) return;
    _bootstrapTerminalWritten = true;
    string detail = "reason=" + reason + " " + BootstrapDetail();
    Write("bootstrap_failed", NativeAutotestRequest.ActiveClient, detail);
    LabAutoJoinPatches.FailBootstrap(detail);
  }

  void RejectDescriptor(string reason) {
    if (_descriptorRejected) return;
    _descriptorRejected = true;
    Write("descriptor_rejected_before_scene", "client", "reason=" + reason);
    ComfyNetworkSense.LogWarning(
        "C5 world descriptor rejected before scene entry: " + reason);
  }

  public static void BlankServerWorldPayload(string method, object[] parameters) {
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected || ZNet.instance == null ||
        !ZNet.instance.IsServer() ||
        !string.Equals(method, "PeerInfo", StringComparison.Ordinal) ||
        parameters == null || parameters.Length != 1 ||
        parameters[0] is not ZPackage source) return;

    ZPackage header = new(source.GetArray());
    header.SetPos(0);
    ZPackage blank = new();
    blank.Write(header.ReadLong());
    blank.Write(header.ReadString());
    blank.Write(header.ReadUInt());
    blank.Write(header.ReadVector3());
    blank.Write(header.ReadString());
    blank.Write(string.Empty);
    blank.Write(0);
    blank.Write(string.Empty);
    blank.Write(0L);
    blank.Write(0);
    blank.Write(0.0);
    parameters[0] = blank;
    active.Write("native_world_payload_blanked", "server",
        "world_name_length=0 seed=0 seed_name_length=0 world_uid=0"
        + " world_generation_version=0 network_time=0");
  }

  public static bool TryPrepareClientPeerInfo(
      ZRpc rpc, ref ZPackage pkg, out bool owned) {
    owned = false;
    WorldZoneCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected || ZNet.instance == null ||
        ZNet.instance.IsServer() || rpc == null || pkg == null) return true;
    owned = true;
    if (active._replayBypass.Remove(rpc)) return true;

    byte[] bytes = (byte[])pkg.GetArray().Clone();
    string sentinel = ValidateBlankWorldFields(bytes);
    if (!string.IsNullOrEmpty(sentinel)) {
      active.RejectDescriptor(sentinel);
      return false;
    }
    active.Write("native_blank_world_payload_observed", "client",
        "world_name_length=0 seed=0 seed_name_length=0 world_uid=0"
        + " world_generation_version=0 network_time=0");

    if (active._descriptor == null) {
      active._pendingRpc = rpc;
      active._pendingPeerInfo = bytes;
      active.Write("peer_info_held_for_descriptor", "client",
          "scene_entry_withheld=true");
      return false;
    }

    pkg = BuildPeerInfo(bytes, active._descriptor);
    active.Write("peer_info_world_substituted", "client",
        "source=lumberjacks world_epoch=" + active._descriptor.world_epoch);
    return true;
  }

  void ResumePendingPeerInfo() {
    ZRpc rpc = _pendingRpc;
    byte[] bytes = _pendingPeerInfo;
    _pendingRpc = null;
    _pendingPeerInfo = null;
    if (rpc == null || bytes == null || RpcPeerInfoMethod == null) {
      RejectDescriptor("peer_info_resume_unavailable");
      return;
    }
    try {
      ZPackage rebuilt = BuildPeerInfo(bytes, _descriptor);
      _replayBypass.Add(rpc);
      RpcPeerInfoMethod.Invoke(ZNet.instance, new object[] { rpc, rebuilt });
      Write("peer_info_resumed_from_descriptor", "client",
          "source=lumberjacks world_epoch=" + _descriptor.world_epoch);
    } catch (Exception exception) {
      _replayBypass.Remove(rpc);
      RejectDescriptor("peer_info_resume_failed:" + exception.GetType().Name);
    }
  }

  static string ValidateBlankWorldFields(byte[] bytes) {
    try {
      ZPackage value = new(bytes);
      value.SetPos(0);
      value.ReadLong();
      value.ReadString();
      value.ReadUInt();
      value.ReadVector3();
      value.ReadString();
      string name = value.ReadString();
      int seed = value.ReadInt();
      string seedName = value.ReadString();
      long uid = value.ReadLong();
      int worldGeneration = value.ReadInt();
      double networkTime = value.ReadDouble();
      return name.Length == 0 && seed == 0 && seedName.Length == 0 && uid == 0 &&
          worldGeneration == 0 && networkTime == 0.0
          ? string.Empty
          : "native_world_payload_not_blank";
    } catch (Exception exception) {
      return "native_world_payload_decode_failed:" + exception.GetType().Name;
    }
  }

  static ZPackage BuildPeerInfo(byte[] bytes, WorldDescriptor descriptor) {
    ZPackage source = new(bytes);
    source.SetPos(0);
    long uid = source.ReadLong();
    string version = source.ReadString();
    uint networkVersion = source.ReadUInt();
    Vector3 reference = source.ReadVector3();
    string playerName = source.ReadString();

    ZPackage rebuilt = new();
    rebuilt.Write(uid);
    rebuilt.Write(version);
    rebuilt.Write(networkVersion);
    rebuilt.Write(reference);
    rebuilt.Write(playerName);
    rebuilt.Write(descriptor.world_name);
    rebuilt.Write(descriptor.seed);
    rebuilt.Write(descriptor.seed_name);
    rebuilt.Write(descriptor.world_uid);
    rebuilt.Write(descriptor.world_generation_version);
    rebuilt.Write(descriptor.network_time);
    rebuilt.SetPos(0);
    return rebuilt;
  }

  void Write(string state, string client, string detail) {
    string runId = ZNet.instance != null && ZNet.instance.IsServer()
        ? PluginConfig.NativeNetworkEvidenceRunId?.Value ?? string.Empty
        : NativeAutotestRequest.ActiveRunId;
    _writer.Write(ReceiptFileName, new Dictionary<string, object> {
        ["schema_version"] = 1,
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["state"] = state,
        ["run_id"] = runId,
        ["client"] = client,
        ["detail"] = detail ?? string.Empty
    });
    ComfyNetworkSense.LogInfo(
        "WORLD_ZONE_CUTOVER state=" + state
        + " run_id=" + runId + " client=" + client
        + " detail=" + (detail ?? string.Empty).Replace(' ', '_'));
  }

  static bool SafeToken(string value, int max) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > max) return false;
    foreach (char c in value)
      if (!char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  static string Escape(string value) =>
      (value ?? string.Empty)
          .Replace("\\", "\\\\")
          .Replace("\"", "\\\"")
          .Replace("\r", "\\r")
          .Replace("\n", "\\n")
          .Replace("\t", "\\t");

  static string JsonStringArray(IEnumerable<string> values) =>
      "[" + string.Join(
          ",", values.Select(value => "\"" + Escape(value) + "\"")) + "]";

  static string JsonLocationIcons(
      IEnumerable<KeyValuePair<Vector3, string>> icons) {
    StringBuilder json = new("[");
    bool first = true;
    foreach (KeyValuePair<Vector3, string> icon in icons) {
      if (!first) json.Append(',');
      first = false;
      json.Append("{\"name\":\"").Append(Escape(icon.Value))
          .Append("\",\"x\":")
          .Append(icon.Key.x.ToString("R", CultureInfo.InvariantCulture))
          .Append(",\"y\":")
          .Append(icon.Key.y.ToString("R", CultureInfo.InvariantCulture))
          .Append(",\"z\":")
          .Append(icon.Key.z.ToString("R", CultureInfo.InvariantCulture))
          .Append('}');
    }
    return json.Append(']').ToString();
  }

  static string Format(Vector3 value) =>
      value.x.ToString("0.##", CultureInfo.InvariantCulture) + ","
      + value.y.ToString("0.##", CultureInfo.InvariantCulture) + ","
      + value.z.ToString("0.##", CultureInfo.InvariantCulture);

  static string EmptyAsNone(string value) =>
      string.IsNullOrEmpty(value) ? "none" : value;

  static string ExtractJsonString(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    if (!match.Success) return string.Empty;
    string value = match.Groups["value"].Value;
    return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
  }

  static long ExtractJsonLong(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        long.TryParse(match.Groups["value"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long value)
        ? value : 0;
  }

  static double ExtractJsonDouble(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name)
        + "\"\\s*:\\s*(?<value>-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value)
        ? value : double.NaN;
  }

  static bool ExtractJsonBool(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>true|false)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        string.Equals(
            match.Groups["value"].Value, "true",
            StringComparison.OrdinalIgnoreCase);
  }

  static bool Finite(Vector3 value) =>
      !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
      !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
      !float.IsNaN(value.z) && !float.IsInfinity(value.z);

  public void Dispose() {
    _disposed = true;
    if (ReferenceEquals(Volatile.Read(ref _active), this))
      Interlocked.CompareExchange(ref _active, null, this);
  }

  readonly struct CanonicalFrame {
    public readonly string Type;
    public readonly string Json;
    public CanonicalFrame(string type, string json) {
      Type = type ?? string.Empty;
      Json = json ?? string.Empty;
    }
  }

  [DataContract]
  sealed class WorldDescriptorEnvelope {
    [DataMember(Name = "payload")]
    public WorldDescriptor payload { get; set; }
  }

  [DataContract]
  sealed class WorldDescriptor {
    [DataMember(Name = "schema_version")]
    public int schema_version = 0;
    [DataMember(Name = "protocol_version")]
    public int protocol_version = 0;
    [DataMember(Name = "release_id")]
    public string release_id = string.Empty;
    [DataMember(Name = "run_id")]
    public string run_id = string.Empty;
    [DataMember(Name = "world_epoch")]
    public string world_epoch = string.Empty;
    [DataMember(Name = "world_name")]
    public string world_name = string.Empty;
    [DataMember(Name = "seed")]
    public int seed = 0;
    [DataMember(Name = "seed_name")]
    public string seed_name = string.Empty;
    [DataMember(Name = "world_uid")]
    public long world_uid = 0;
    [DataMember(Name = "world_generation_version")]
    public int world_generation_version = 0;
    [DataMember(Name = "network_time")]
    public double network_time = 0;
    [DataMember(Name = "save_epoch")]
    public long save_epoch = 0;
    [DataMember(Name = "initial_zone_x")]
    public int initial_zone_x = 0;
    [DataMember(Name = "initial_zone_y")]
    public int initial_zone_y = 0;
    [DataMember(Name = "start_location_name")]
    public string start_location_name = string.Empty;
    [DataMember(Name = "global_keys")]
    public string[] global_keys = Array.Empty<string>();
    [DataMember(Name = "location_icons")]
    public WorldLocationIcon[] location_icons = Array.Empty<WorldLocationIcon>();
  }

  [DataContract]
  sealed class WorldLocationIcon {
    [DataMember(Name = "name")] public string name = string.Empty;
    [DataMember(Name = "x")] public float x { get; set; }
    [DataMember(Name = "y")] public float y { get; set; }
    [DataMember(Name = "z")] public float z { get; set; }
  }

  sealed class MembershipProbe {
    public string ActionId = string.Empty;
    public string Mode = string.Empty;
    public string WorldEpoch = string.Empty;
    public long ZoneEpoch;
    public long SnapshotEpoch;
    public int ZoneX;
    public int ZoneY;
    public float DeadlineAt;
    public readonly HashSet<int> AppliedChunks = new();
    public int ReplayedChunks;
    public int CompleteCount;
    public bool DropPerformed;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }
}
