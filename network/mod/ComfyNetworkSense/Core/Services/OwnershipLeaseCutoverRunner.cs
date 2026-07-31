namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// C4 ownership boundary. The selected pickup is created by Valheim, but every ownership
/// authorization and action result crosses the canonical Lumberjacks session. Native ownership
/// request, release, CreateSyncList and destroyed-ZDO delivery are suppressed for the selected
/// object. Leases are bound to a stable logical peer and fail closed after disconnect or expiry.
/// </summary>
public sealed class OwnershipLeaseCutoverRunner : IDisposable {
  const string ReceiptFileName = "ownership-lease-cutover.jsonl";
  const string ItemPrefab = "Raspberry";
  static readonly int C3TagHash =
      "ComfyNetworkSense_C3Tag".GetStableHashCode();
  static readonly int C4TagHash =
      "ComfyNetworkSense_C4Ownership".GetStableHashCode();
  static readonly MethodInfo HandleDestroyedZdoMethod =
      AccessTools.Method(
          typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });

  static OwnershipLeaseCutoverRunner _active;
  internal static int ReleaseScopeDepth;
  internal static int CanonicalDestroyScopeDepth;

  readonly ConcurrentQueue<CanonicalFrame> _frames = new();
  readonly Dictionary<string, ServerTarget> _serverTargets =
      new(StringComparer.Ordinal);
  readonly HashSet<ZDOID> _selectedTargets = new();
  readonly TelemetryLogWriter _writer = new();
  readonly LumberjacksGameSessionRunner _gameSession;

  ClientProbe _probe;
  string _runId = string.Empty;
  string _worldEpoch = string.Empty;
  bool _disposed;
  long _nativeSelectionSuppressed;
  long _nativeInvalidSuppressed;
  long _nativeReleaseSuppressed;
  long _nativeRequestOwnSuppressed;
  long _nativePickupSuppressed;
  long _nativeDestroySuppressed;

  public OwnershipLeaseCutoverRunner(LumberjacksGameSessionRunner gameSession) {
    _gameSession = gameSession ?? throw new ArgumentNullException(nameof(gameSession));
    OwnershipLeaseCutoverRunner previous =
        Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed || !Enabled()) return;
    RefreshContext();
    if (!SafeToken(_runId, 80) || !SafeToken(_worldEpoch, 96)) return;
    DrainFrames(now);
    if (IsServer()) {
      DriveServer(now);
    } else {
      DriveClient(now);
    }
  }

  public bool BeginProbe(string actionId, float deadlineSeconds, out string detail) {
    detail = string.Empty;
    if (!Enabled()) {
      detail = "ownership_lease_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        !SafeToken(actionId, 80)) {
      detail = "ownership_lease_client_not_ready";
      return false;
    }
    if (!_gameSession.WebSocketConnected ||
        !SafeToken(_gameSession.LogicalPeerId, 80)) {
      detail = "lumberjacks_session_not_connected";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_ownership_probe_active";
      return false;
    }
    Vector3 origin = ((Component) Player.m_localPlayer).transform.position;
    _probe = new ClientProbe {
        ActionId = actionId,
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 20.0f, 300.0f),
        State = "await_create_grant",
        Origin = origin,
        InitialInventoryUnits = InventoryUnits()
    };
    if (!QueueLeaseRequest(_probe, "create")) {
      detail = "ownership_create_queue_failed";
      _probe.Terminal = true;
      return false;
    }
    Write("lease_request_sent", actionId, "phase=create");
    return true;
  }

  public bool BeginContentionProbe(
      string actionId, float deadlineSeconds, out string detail) {
    detail = string.Empty;
    if (!Enabled()) {
      detail = "ownership_lease_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        !SafeToken(actionId, 80)) {
      detail = "ownership_lease_client_not_ready";
      return false;
    }
    if (!_gameSession.WebSocketConnected ||
        !SafeToken(_gameSession.LogicalPeerId, 80)) {
      detail = "lumberjacks_session_not_connected";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_ownership_probe_active";
      return false;
    }
    _probe = new ClientProbe {
        ActionId = actionId,
        Mode = "contender",
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 10.0f, 120.0f),
        State = "contention_retry",
        NextAt = Time.unscaledTime
    };
    Write("contention_probe_started", actionId,
        "contender_logical_peer_id=" + _gameSession.LogicalPeerId);
    return true;
  }

  public bool TryGetProbeResult(
      string actionId, out bool terminal, out bool success, out string detail) {
    if (_probe == null ||
        !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "ownership_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  public static void EnqueueCanonicalFrame(string type, string json) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled()) return;
    active._frames.Enqueue(new CanonicalFrame(type, json));
  }

  public static void FilterNativeSync(object peer, List<ZDO> toSync) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    if (toSync != null) {
      for (int index = toSync.Count - 1; index >= 0; index--) {
        ZDO zdo = toSync[index];
        if (zdo == null || !active._selectedTargets.Contains(zdo.m_uid)) continue;
        toSync.RemoveAt(index);
        Interlocked.Increment(ref active._nativeSelectionSuppressed);
        active.Write("native_create_sync_list_suppressed", "server",
            "uid=" + zdo.m_uid);
      }
    }
    if (peer == null) return;
    try {
      object value = AccessTools.Field(peer.GetType(), "m_invalidSector")?.GetValue(peer);
      if (value is not IList && value is IEnumerable enumerable) {
        List<ZDOID> remove = new();
        foreach (object item in enumerable)
          if (item is ZDOID uid && active._selectedTargets.Contains(uid))
            remove.Add(uid);
        if (value is ICollection<ZDOID> set) {
          foreach (ZDOID uid in remove) {
            if (!set.Remove(uid)) continue;
            Interlocked.Increment(ref active._nativeInvalidSuppressed);
            active.Write("native_destroyed_zdo_suppressed", "server",
                "uid=" + uid);
          }
        }
      }
    } catch (Exception exception) {
      active.Write("native_invalid_filter_failed", "server",
          exception.GetType().Name + ":" + exception.Message);
    }
  }

  public static bool ShouldBlockRelease(ZDO zdo, long attemptedOwner) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || ReleaseScopeDepth <= 0 ||
        zdo == null || !active._selectedTargets.Contains(zdo.m_uid) ||
        attemptedOwner == zdo.GetOwner()) return false;
    Interlocked.Increment(ref active._nativeReleaseSuppressed);
    active.Write("native_release_suppressed", "server",
        "uid=" + zdo.m_uid +
        " held_owner=" + zdo.GetOwner().ToString(CultureInfo.InvariantCulture) +
        " attempted_owner=" + attemptedOwner.ToString(CultureInfo.InvariantCulture));
    return true;
  }

  public static bool ShouldBlockRequestOwn(ItemDrop item) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || IsServer() || item == null) return false;
    ZDO zdo = item.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null || active._probe == null ||
        zdo.m_uid != active._probe.Target) return false;
    Interlocked.Increment(ref active._nativeRequestOwnSuppressed);
    active.Write("native_request_own_suppressed", active._probe.ActionId,
        "uid=" + zdo.m_uid + " lease_epoch=" + active._probe.LeaseEpoch);
    active._probe.NativeRequestOwnSuppressed = true;
    return true;
  }

  public static bool ShouldBlockPickup(Humanoid humanoid, GameObject itemObject) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || IsServer() ||
        humanoid == null || humanoid != Player.m_localPlayer ||
        itemObject == null || active._probe == null)
      return false;
    ZDO zdo = itemObject.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null ||
        (zdo.m_uid != active._probe.Target &&
         !string.Equals(
             zdo.GetString(C4TagHash, string.Empty),
             active._probe.ActionId,
             StringComparison.Ordinal)))
      return false;
    Interlocked.Increment(ref active._nativePickupSuppressed);
    active._probe.NativePickupSuppressed = true;
    active.Write("native_inventory_pickup_suppressed", active._probe.ActionId,
        "uid=" + zdo.m_uid +
        " lease_epoch=" + active._probe.LeaseEpoch);
    return true;
  }

  public static bool ShouldBlockDestroyRequest(ZDO zdo) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || zdo == null ||
        CanonicalDestroyScopeDepth > 0 || !active.Selected(zdo.m_uid))
      return false;
    Interlocked.Increment(ref active._nativeDestroySuppressed);
    active.Write("native_destroy_request_suppressed",
        active.ActionFor(zdo.m_uid),
        "uid=" + zdo.m_uid);
    return true;
  }

  public static bool ShouldBlockHandleDestroyed(ZDOID uid) {
    OwnershipLeaseCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() ||
        CanonicalDestroyScopeDepth > 0 || !active.Selected(uid))
      return false;
    Interlocked.Increment(ref active._nativeDestroySuppressed);
    active.Write("native_destroy_delivery_suppressed",
        active.ActionFor(uid),
        "uid=" + uid);
    return true;
  }

  public static void EnterCanonicalDestroy() =>
      CanonicalDestroyScopeDepth++;

  public static void ExitCanonicalDestroy() {
    if (CanonicalDestroyScopeDepth > 0)
      CanonicalDestroyScopeDepth--;
  }

  void DrainFrames(float now) {
    while (_frames.TryDequeue(out CanonicalFrame frame)) {
      OwnershipEnvelope envelope = Deserialize(frame.Json);
      OwnershipPayload payload = envelope?.payload;
      if (envelope == null || payload == null || envelope.seq <= 0 ||
          !string.Equals(frame.Type, envelope.type, StringComparison.Ordinal) ||
          !string.Equals(payload.run_id, _runId, StringComparison.Ordinal) ||
          !string.Equals(payload.world_epoch, _worldEpoch, StringComparison.Ordinal)) {
        Write("canonical_frame_invalid", payload?.action_id ?? string.Empty,
            "frame_type=" + (frame.Type ?? string.Empty) +
            " parsed_type=" + (envelope?.type ?? string.Empty) +
            " seq=" + (envelope?.seq ?? 0) +
            " parsed_run=" + (payload?.run_id ?? string.Empty) +
            " expected_run=" + _runId +
            " parsed_world=" + (payload?.world_epoch ?? string.Empty) +
            " expected_world=" + _worldEpoch);
        throw new InvalidDataException("ownership canonical frame invalid");
      }
      if (IsServer()) HandleServerFrame(frame.Type, envelope.seq, payload, now);
      else HandleClientFrame(frame.Type, envelope.seq, payload, now);
      if (!_gameSession.QueueReliableAck(envelope.seq))
        throw new InvalidDataException("ownership reliable ACK queue failed");
    }
  }

  void HandleServerFrame(
      string type, long reliableSequence, OwnershipPayload payload, float now) {
    switch (type) {
      case "valheim_ownership_lease_command":
        HandleLeaseCommand(payload);
        break;
      case "valheim_ownership_lease_receipt":
        Write("lease_receipt", payload.action_id,
            "phase=" + payload.phase + " uid=" + Uid(payload) +
            " epoch=" + payload.lease_epoch +
            " logical_peer_id=" + payload.holder_logical_peer_id);
        break;
      case "valheim_ownership_action_authorized":
        HandleAuthorized(payload, now);
        break;
      case "valheim_ownership_result_receipt":
        Write("result_receipt", payload.action_id,
            "attempt=" + payload.attempt_id + " result=" + payload.result +
            " epoch=" + payload.lease_epoch);
        break;
      default:
        throw new InvalidDataException("ownership server frame type invalid");
    }
    Write("canonical_frame_acked", payload.action_id,
        "type=" + type + " reliable_seq=" + reliableSequence);
  }

  void HandleLeaseCommand(OwnershipPayload payload) {
    if (!SafeToken(payload.action_id, 80) ||
        !SafeToken(payload.holder_logical_peer_id, 80) ||
        payload.holder_peer_uid == 0 ||
        payload.phase is not ("create" or "reissue") ||
        (payload.phase == "create" && payload.request_ordinal != 0) ||
        (payload.phase == "reissue" && payload.request_ordinal < 1))
      throw new InvalidDataException("ownership lease command invalid");

    ServerTarget target;
    if (payload.phase == "create") {
      if (_serverTargets.TryGetValue(payload.action_id, out target)) {
        Issue(target, "create");
        return;
      }
      GameObject prefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
      if (prefab == null)
        throw new InvalidDataException("ownership item prefab missing");
      Vector3 origin =
          new((float) payload.origin_x, (float) payload.origin_y, (float) payload.origin_z);
      GameObject instance =
          UnityEngine.Object.Instantiate(prefab, origin + Vector3.forward * 1.5f,
              Quaternion.identity);
      ZNetView view = instance.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      if (zdo == null)
        throw new InvalidDataException("ownership target ZDO missing");
      target = new ServerTarget {
          ActionId = payload.action_id,
          HolderLogicalPeerId = payload.holder_logical_peer_id,
          HolderPeerUid = payload.holder_peer_uid,
          Instance = instance,
          Zdo = zdo,
          Target = zdo.m_uid
      };
      _serverTargets.Add(payload.action_id, target);
      _selectedTargets.Add(zdo.m_uid);
      zdo.Set(C3TagHash, _runId);
      zdo.Set(C4TagHash, payload.action_id);
      Write("target_created", payload.action_id,
          "uid=" + zdo.m_uid + " owner=" + zdo.GetOwner() +
          " item_prefab=" + ItemPrefab);
      Issue(target, "create");
      return;
    }

    if (!_serverTargets.TryGetValue(payload.action_id, out target) ||
        target.Target.UserID != payload.uid_user ||
        target.Target.ID != payload.uid_id ||
        payload.request_ordinal != target.Reissues + 1 ||
        !string.Equals(target.HolderLogicalPeerId,
            payload.holder_logical_peer_id, StringComparison.Ordinal) ||
        target.HolderPeerUid != payload.holder_peer_uid)
      throw new InvalidDataException("ownership reissue target mismatch");
    Issue(target, "reissue");
  }

  void Issue(ServerTarget target, string phase) {
    int duration;
    if (phase == "create") {
      duration = 20;
    } else {
      target.Reissues++;
      duration = target.Reissues == 1 ? 2 : 15;
    }
    string fields =
        CommonFields(target.ActionId, target.Target) +
        ",\"phase\":\"" + phase +
        "\",\"request_ordinal\":" +
            (phase == "create" ? 0 : target.Reissues)
                .ToString(CultureInfo.InvariantCulture) +
        ",\"holder_logical_peer_id\":\"" +
            Escape(target.HolderLogicalPeerId) +
        "\",\"holder_peer_uid\":" +
            target.HolderPeerUid.ToString(CultureInfo.InvariantCulture) +
        ",\"duration_seconds\":" +
            duration.ToString(CultureInfo.InvariantCulture) +
        ",\"item_prefab\":\"" + ItemPrefab + "\"";
    if (!_gameSession.TryQueueOwnershipLeaseIssue(fields))
      throw new InvalidDataException("ownership lease issue queue failed");
    Write("lease_issue_sent", target.ActionId,
        "phase=" + phase + " duration_seconds=" + duration +
        " reissues=" + target.Reissues);
  }

  void HandleAuthorized(OwnershipPayload payload, float now) {
    if (!_serverTargets.TryGetValue(payload.action_id, out ServerTarget target) ||
        target.Target.UserID != payload.uid_user ||
        target.Target.ID != payload.uid_id ||
        target.Completed || target.Authorized ||
        !string.Equals(target.HolderLogicalPeerId,
            payload.holder_logical_peer_id, StringComparison.Ordinal) ||
        target.HolderPeerUid != payload.holder_peer_uid)
      throw new InvalidDataException("ownership authorization target invalid");
    target.Authorized = true;
    target.LeaseEpoch = payload.lease_epoch;
    target.AttemptId = payload.attempt_id;
    target.Zdo.SetOwner(target.HolderPeerUid);
    target.Phase = "holder_owner";
    target.NextAt = now + 0.75f;
    Write("action_authorized", target.ActionId,
        "attempt=" + target.AttemptId + " uid=" + target.Target +
        " epoch=" + target.LeaseEpoch +
        " owner=" + target.Zdo.GetOwner());
  }

  void DriveServer(float now) {
    foreach (ServerTarget target in _serverTargets.Values) {
      if (!target.Authorized || target.Completed || now < target.NextAt) continue;
      if (target.Phase == "holder_owner") {
        target.Zdo.SetOwner(ZNet.GetUID());
        if (ZDOMan.instance == null ||
            ZDOMan.instance.GetZDO(target.Target) == null ||
            HandleDestroyedZdoMethod == null)
          throw new InvalidDataException("ownership target ZDO missing before destroy");
        EnterCanonicalDestroy();
        try {
          HandleDestroyedZdoMethod.Invoke(
              ZDOMan.instance, new object[] { target.Target });
        } finally {
          ExitCanonicalDestroy();
        }
        if (ZDOMan.instance.GetZDO(target.Target) != null)
          throw new InvalidDataException("ownership target ZDO survived destroy");
        target.Phase = "destroyed";
        target.NextAt = now + 0.75f;
        Write("authoritative_target_destroyed", target.ActionId,
            "uid=" + target.Target +
            " server_owner_restored=true native_destroy_rpc=false");
        continue;
      }
      if (target.Phase == "destroyed") {
        string fields =
            CommonFields(target.ActionId, target.Target) +
            ",\"attempt_id\":\"" + Escape(target.AttemptId) +
            "\",\"holder_logical_peer_id\":\"" +
                Escape(target.HolderLogicalPeerId) +
            "\",\"lease_epoch\":" +
                target.LeaseEpoch.ToString(CultureInfo.InvariantCulture) +
            ",\"success\":true,\"item_prefab\":\"" + ItemPrefab +
            "\",\"item_count\":1";
        if (!_gameSession.TryQueueOwnershipActionResult(fields))
          throw new InvalidDataException("ownership action result queue failed");
        target.Completed = true;
        Write("action_result_sent", target.ActionId,
            "attempt=" + target.AttemptId + " epoch=" + target.LeaseEpoch);
      }
    }
  }

  void HandleClientFrame(
      string type, long reliableSequence, OwnershipPayload payload, float now) {
    if (_probe == null || _probe.Terminal ||
        !string.Equals(payload.action_id, _probe.ActionId,
            StringComparison.Ordinal))
      throw new InvalidDataException("ownership client frame has no active probe");
    switch (type) {
      case "valheim_ownership_lease_granted":
        HandleGrant(payload, now);
        break;
      case "valheim_ownership_action_rejected":
        HandleRejected(payload, now);
        break;
      case "valheim_ownership_action_completed":
        HandleCompleted(payload);
        break;
      default:
        throw new InvalidDataException("ownership client frame type invalid");
    }
    Write("canonical_frame_acked", payload.action_id,
        "type=" + type + " reliable_seq=" + reliableSequence);
  }

  void HandleGrant(OwnershipPayload payload, float now) {
    if (!SafeToken(payload.holder_logical_peer_id, 80) ||
        !string.Equals(payload.holder_logical_peer_id,
            _gameSession.LogicalPeerId, StringComparison.Ordinal) ||
        payload.uid_user == 0 || payload.uid_id == 0 ||
        payload.lease_epoch < 1 ||
        !DateTimeOffset.TryParse(payload.expires_utc,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out DateTimeOffset expires))
      throw new InvalidDataException("ownership grant invalid");
    _probe.Target = new ZDOID(payload.uid_user, payload.uid_id);
    _probe.LeaseEpoch = payload.lease_epoch;
    _probe.ExpiresUtc = expires;
    _probe.ItemPrefab = payload.item_prefab;
    Write("lease_granted", _probe.ActionId,
        "phase=" + payload.phase + " uid=" + _probe.Target +
        " epoch=" + _probe.LeaseEpoch +
        " expires_utc=" + expires.ToString("o"));

    if (payload.phase == "create") {
      if (payload.request_ordinal != 0)
        throw new InvalidDataException("ownership create grant ordinal invalid");
      _probe.State = "disconnect_delay";
      _probe.NextAt = now + 0.75f;
      return;
    }
    if (payload.request_ordinal != _probe.Reissues + 1)
      throw new InvalidDataException("ownership reissue grant ordinal invalid");
    _probe.Reissues++;
    if (_probe.Reissues == 1) {
      SendAction(_probe.LeaseEpoch + 1, "wrong_epoch");
      _probe.State = "await_wrong_epoch_reject";
      return;
    }
    if (_probe.Reissues == 2) {
      _probe.State = "await_target";
      return;
    }
    throw new InvalidDataException("ownership unexpected lease reissue");
  }

  void HandleRejected(OwnershipPayload payload, float now) {
    if (!string.Equals(payload.attempt_id, _probe.AttemptId,
            StringComparison.Ordinal))
      throw new InvalidDataException("ownership rejection attempt mismatch");
    Write("action_rejected", _probe.ActionId,
        "attempt=" + payload.attempt_id + " reason=" + payload.reason +
        " epoch=" + payload.lease_epoch);
    if (_probe.Mode == "contender") {
      if (payload.reason == "lease_missing") {
        _probe.State = "contention_retry";
        _probe.NextAt = now + 0.5f;
        return;
      }
      if (payload.reason == "holder_mismatch" &&
          payload.lease_state == "active" &&
          payload.uid_user != 0 && payload.uid_id != 0 &&
          payload.lease_epoch > 0) {
        _probe.Target = new ZDOID(payload.uid_user, payload.uid_id);
        _probe.LeaseEpoch = payload.lease_epoch;
        _probe.Terminal = true;
        _probe.Success = true;
        _probe.Detail =
            "second_logical_peer_rejected_for_active_target" +
            " uid=" + _probe.Target +
            " epoch=" + _probe.LeaseEpoch +
            " reason=holder_mismatch";
        Write("contention_probe_passed", _probe.ActionId, _probe.Detail);
        return;
      }
      if (payload.reason == "holder_mismatch" &&
          payload.lease_state is "reclaimed" or "expired") {
        _probe.State = "contention_retry";
        _probe.NextAt = now + 0.25f;
        return;
      }
      throw new InvalidDataException(
          "ownership contention rejection invariant failed");
    }
    if (_probe.State == "await_reclaimed_reject" &&
        payload.reason == "lease_reclaimed") {
      if (!QueueLeaseRequest(_probe, "reissue"))
        throw new InvalidDataException("ownership reissue queue failed");
      _probe.State = "await_reissue_one";
      return;
    }
    if (_probe.State == "await_wrong_epoch_reject" &&
        payload.reason == "epoch_mismatch") {
      _probe.State = "await_expiry";
      return;
    }
    if (_probe.State == "await_expired_reject" &&
        payload.reason == "lease_expired") {
      if (!QueueLeaseRequest(_probe, "reissue"))
        throw new InvalidDataException("ownership final reissue queue failed");
      _probe.State = "await_reissue_two";
      return;
    }
    throw new InvalidDataException("ownership rejection invariant failed");
  }

  void HandleCompleted(OwnershipPayload payload) {
    if (_probe.State != "await_completion" ||
        !string.Equals(payload.attempt_id, _probe.AttemptId,
            StringComparison.Ordinal) ||
        payload.lease_epoch != _probe.LeaseEpoch ||
        payload.item_count != 1 ||
        !string.Equals(payload.item_prefab, ItemPrefab,
            StringComparison.Ordinal) ||
        !_probe.NativeRequestOwnSuppressed ||
        !_probe.NativePickupSuppressed)
      throw new InvalidDataException("ownership completion invariant failed");
    GameObject prefab = ObjectDB.instance?.GetItemPrefab(payload.item_prefab);
    if (prefab == null ||
        Player.m_localPlayer.GetInventory().AddItem(prefab, payload.item_count) == false)
      throw new InvalidDataException("ownership authoritative inventory apply failed");
    int after = InventoryUnits();
    Write("authoritative_inventory_applied", _probe.ActionId,
        "item_prefab=" + payload.item_prefab +
        " item_count=" + payload.item_count +
        " inventory_units_before=" + _probe.InitialInventoryUnits +
        " inventory_units_after=" + after);
    if (_probe.InitialInventoryUnits < 0 ||
        after != _probe.InitialInventoryUnits + payload.item_count)
      throw new InvalidDataException(
          "ownership inventory did not increment from " +
          _probe.InitialInventoryUnits + " to " + after);
    _probe.Terminal = true;
    _probe.Success = true;
    _probe.Detail =
        "lease_reclaim_wrong_epoch_expiry_and_authoritative_pickup_passed" +
        " uid=" + _probe.Target +
        " epoch=" + _probe.LeaseEpoch +
        " inventory_units_before=" + _probe.InitialInventoryUnits +
        " inventory_units_after=" + after +
        " native_request_own_suppressed=" +
            Interlocked.Read(ref _nativeRequestOwnSuppressed) +
        " native_inventory_pickup_suppressed=" +
            Interlocked.Read(ref _nativePickupSuppressed);
    Write("probe_passed", _probe.ActionId, _probe.Detail);
  }

  void DriveClient(float now) {
    if (_probe == null || _probe.Terminal) return;
    if (now > _probe.DeadlineAt) {
      Fail("ownership_probe_deadline state=" + _probe.State);
      return;
    }
    switch (_probe.State) {
      case "contention_retry":
        if (now < _probe.NextAt) return;
        if (!QueueContentionRequest(_probe)) {
          Fail("ownership_contention_queue_failed");
          return;
        }
        _probe.State = "await_contention_reject";
        break;
      case "disconnect_delay":
        if (now < _probe.NextAt) return;
        _probe.PreDisconnectResumeEpoch = _gameSession.ResumeEpoch;
        if (!_gameSession.AbortForOwnershipProbe()) return;
        _probe.SawDisconnected = false;
        _probe.State = "await_reconnect";
        Write("canonical_socket_aborted", _probe.ActionId,
            "logical_peer_id=" + _gameSession.LogicalPeerId +
            " resume_epoch=" + _probe.PreDisconnectResumeEpoch);
        break;
      case "await_reconnect":
        if (!_gameSession.WebSocketConnected) {
          _probe.SawDisconnected = true;
          return;
        }
        if (!_probe.SawDisconnected ||
            _gameSession.ResumeEpoch <= _probe.PreDisconnectResumeEpoch) return;
        SendAction(_probe.LeaseEpoch, "reclaimed");
        _probe.State = "await_reclaimed_reject";
        break;
      case "await_expiry":
        if (DateTimeOffset.UtcNow < _probe.ExpiresUtc.AddMilliseconds(250))
          return;
        SendAction(_probe.LeaseEpoch, "expired");
        _probe.State = "await_expired_reject";
        break;
      case "await_target":
        ItemDrop item = FindTargetItem(_probe.Target);
        if (item == null) return;
        item.RequestOwn();
        if (!_probe.NativeRequestOwnSuppressed) {
          Fail("native_request_own_not_suppressed");
          return;
        }
        SendAction(_probe.LeaseEpoch, "valid");
        _probe.State = "await_completion";
        _probe.NextAt = now + 0.25f;
        break;
      case "await_completion":
        if (_probe.NativePickupSuppressed || now < _probe.NextAt) return;
        ItemDrop selected = FindTargetItem(_probe.Target);
        if (selected != null)
          Player.m_localPlayer.Pickup(selected.gameObject);
        break;
    }
  }

  bool QueueLeaseRequest(ClientProbe probe, string phase) {
    ZDOID target = phase == "create" ? ZDOID.None : probe.Target;
    string fields =
        CommonFields(probe.ActionId, target) +
        ",\"phase\":\"" + phase +
        "\",\"request_ordinal\":" +
            (phase == "create" ? 0 : probe.Reissues + 1)
                .ToString(CultureInfo.InvariantCulture) +
        ",\"origin_x\":" + F(probe.Origin.x) +
        ",\"origin_y\":" + F(probe.Origin.y) +
        ",\"origin_z\":" + F(probe.Origin.z);
    return _gameSession.TryQueueOwnershipLeaseRequest(fields);
  }

  bool QueueContentionRequest(ClientProbe probe) {
    probe.Attempts++;
    probe.AttemptId =
        probe.ActionId + ".contend." +
        probe.Attempts.ToString(CultureInfo.InvariantCulture);
    string fields =
        CommonFields(probe.ActionId, ZDOID.None) +
        ",\"attempt_id\":\"" + Escape(probe.AttemptId) +
        "\",\"phase\":\"contend\"" +
        ",\"request_ordinal\":" +
            probe.Attempts.ToString(CultureInfo.InvariantCulture) +
        ",\"origin_x\":0,\"origin_y\":0,\"origin_z\":0";
    bool queued = _gameSession.TryQueueOwnershipLeaseRequest(fields);
    if (queued)
      Write("contention_request_sent", probe.ActionId,
          "attempt=" + probe.AttemptId);
    return queued;
  }

  void SendAction(long epoch, string suffix) {
    _probe.Attempts++;
    _probe.AttemptId =
        _probe.ActionId + "." + suffix + "." +
        _probe.Attempts.ToString(CultureInfo.InvariantCulture);
    string fields =
        CommonFields(_probe.ActionId, _probe.Target) +
        ",\"attempt_id\":\"" + Escape(_probe.AttemptId) +
        "\",\"action_kind\":\"pickup\"" +
        ",\"lease_epoch\":" + epoch.ToString(CultureInfo.InvariantCulture);
    if (!_gameSession.TryQueueOwnershipAction(fields))
      throw new InvalidDataException("ownership action queue failed");
    Write("action_sent", _probe.ActionId,
        "attempt=" + _probe.AttemptId + " epoch=" + epoch);
  }

  string CommonFields(string actionId, ZDOID uid) =>
      "\"run_id\":\"" + Escape(_runId) +
      "\",\"action_id\":\"" + Escape(actionId) +
      "\",\"world_epoch\":\"" + Escape(_worldEpoch) +
      "\",\"uid_user\":" + uid.UserID.ToString(CultureInfo.InvariantCulture) +
      ",\"uid_id\":" + uid.ID.ToString(CultureInfo.InvariantCulture);

  void RefreshContext() {
    string runId = IsServer()
        ? PluginConfig.NativeNetworkEvidenceRunId?.Value
        : NativeAutotestRequest.ActiveRunId;
    if (SafeToken(runId, 80)) {
      string nextRunId = runId.Trim();
      if (!string.Equals(nextRunId, _runId, StringComparison.Ordinal)) {
        string previousRunId = _runId;
        int staleTargetsDestroyed = ResetRunState();
        _runId = nextRunId;
        Write("run_context_reset", IsServer() ? "server" : "client",
            "previous_run_id=" + previousRunId +
            " stale_targets_destroyed=" + staleTargetsDestroyed);
      }
    }
    try {
      if (ZNet.instance != null)
        _worldEpoch =
            "world-" + unchecked((ulong) ZNet.instance.GetWorldUID())
                .ToString("x16", CultureInfo.InvariantCulture);
    } catch { }
  }

  int ResetRunState() {
    int staleTargetsDestroyed = 0;
    if (IsServer() && ZDOMan.instance != null &&
        HandleDestroyedZdoMethod != null) {
      foreach (ServerTarget target in _serverTargets.Values) {
        if (ZDOMan.instance.GetZDO(target.Target) == null) continue;
        EnterCanonicalDestroy();
        try {
          HandleDestroyedZdoMethod.Invoke(
              ZDOMan.instance, new object[] { target.Target });
        } finally {
          ExitCanonicalDestroy();
        }
        if (ZDOMan.instance.GetZDO(target.Target) != null)
          throw new InvalidDataException(
              "stale ownership target survived run reset");
        staleTargetsDestroyed++;
      }
    }
    while (_frames.TryDequeue(out _)) { }
    _serverTargets.Clear();
    _selectedTargets.Clear();
    _probe = null;
    Interlocked.Exchange(ref _nativeSelectionSuppressed, 0);
    Interlocked.Exchange(ref _nativeInvalidSuppressed, 0);
    Interlocked.Exchange(ref _nativeReleaseSuppressed, 0);
    Interlocked.Exchange(ref _nativeRequestOwnSuppressed, 0);
    Interlocked.Exchange(ref _nativePickupSuppressed, 0);
    Interlocked.Exchange(ref _nativeDestroySuppressed, 0);
    return staleTargetsDestroyed;
  }

  void Fail(string detail) {
    _probe.Terminal = true;
    _probe.Success = false;
    _probe.Detail = detail;
    Write("probe_failed", _probe.ActionId, detail);
  }

  void Write(string state, string actionId, string detail) {
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] =
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = _runId,
            ["role"] = IsServer() ? "server" : "client",
            ["client"] = IsServer()
                ? "server" : NativeAutotestRequest.ActiveClient,
            ["action_id"] = actionId ?? string.Empty,
            ["connection_id"] = _gameSession.ConnectionId,
            ["logical_peer_id"] = _gameSession.LogicalPeerId,
            ["native_selection_suppressed"] =
                Interlocked.Read(ref _nativeSelectionSuppressed),
            ["native_invalid_suppressed"] =
                Interlocked.Read(ref _nativeInvalidSuppressed),
            ["native_release_suppressed"] =
                Interlocked.Read(ref _nativeReleaseSuppressed),
            ["native_request_own_suppressed"] =
                Interlocked.Read(ref _nativeRequestOwnSuppressed),
            ["native_inventory_pickup_suppressed"] =
                Interlocked.Read(ref _nativePickupSuppressed),
            ["native_destroy_suppressed"] =
                Interlocked.Read(ref _nativeDestroySuppressed),
            ["detail"] = detail ?? string.Empty
        });
  }

  bool Selected(ZDOID uid) =>
      _selectedTargets.Contains(uid) ||
      (!IsServer() && _probe != null && _probe.Target == uid);

  string ActionFor(ZDOID uid) {
    if (!IsServer() && _probe != null && _probe.Target == uid)
      return _probe.ActionId;
    foreach (ServerTarget target in _serverTargets.Values)
      if (target.Target == uid) return target.ActionId;
    return "server";
  }

  static OwnershipEnvelope Deserialize(string json) {
    string raw = json ?? string.Empty;
    long uidId = ExtractJsonLong(raw, "uid_id");
    return new OwnershipEnvelope {
        type = ExtractJsonString(raw, "type"),
        seq = ExtractJsonLong(raw, "seq"),
        payload = new OwnershipPayload {
            run_id = ExtractJsonString(raw, "run_id"),
            action_id = ExtractJsonString(raw, "action_id"),
            attempt_id = ExtractJsonString(raw, "attempt_id"),
            phase = ExtractJsonString(raw, "phase"),
            request_ordinal = (int) ExtractJsonLong(raw, "request_ordinal"),
            world_epoch = ExtractJsonString(raw, "world_epoch"),
            holder_logical_peer_id =
                ExtractJsonString(raw, "holder_logical_peer_id"),
            holder_peer_uid = ExtractJsonLong(raw, "holder_peer_uid"),
            uid_user = ExtractJsonLong(raw, "uid_user"),
            uid_id = uidId is >= 0 and <= uint.MaxValue ? (uint) uidId : 0,
            lease_epoch = ExtractJsonLong(raw, "lease_epoch"),
            origin_x = ExtractJsonDouble(raw, "origin_x"),
            origin_y = ExtractJsonDouble(raw, "origin_y"),
            origin_z = ExtractJsonDouble(raw, "origin_z"),
            expires_utc = ExtractJsonString(raw, "expires_utc"),
            item_prefab = ExtractJsonString(raw, "item_prefab"),
            item_count = (int) ExtractJsonLong(raw, "item_count"),
            reason = ExtractJsonString(raw, "reason"),
            lease_state = ExtractJsonString(raw, "lease_state"),
            result = ExtractJsonString(raw, "result")
        }
    };
  }

  static string ExtractJsonString(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    return match.Success ? DecodeJsonString(match.Groups["value"].Value) : string.Empty;
  }

  static string DecodeJsonString(string value) {
    if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;
    StringBuilder decoded = new(value.Length);
    for (int index = 0; index < value.Length; index++) {
      char character = value[index];
      if (character != '\\') {
        decoded.Append(character);
        continue;
      }
      if (++index >= value.Length)
        throw new InvalidDataException("ownership JSON ended inside an escape sequence");
      switch (value[index]) {
        case '"': decoded.Append('"'); break;
        case '\\': decoded.Append('\\'); break;
        case '/': decoded.Append('/'); break;
        case 'b': decoded.Append('\b'); break;
        case 'f': decoded.Append('\f'); break;
        case 'n': decoded.Append('\n'); break;
        case 'r': decoded.Append('\r'); break;
        case 't': decoded.Append('\t'); break;
        case 'u':
          if (index + 4 >= value.Length ||
              !ushort.TryParse(
                  value.Substring(index + 1, 4),
                  NumberStyles.AllowHexSpecifier,
                  CultureInfo.InvariantCulture,
                  out ushort codeUnit))
            throw new InvalidDataException("ownership JSON has invalid Unicode escape");
          decoded.Append((char) codeUnit);
          index += 4;
          break;
        default:
          throw new InvalidDataException("ownership JSON has unsupported escape");
      }
    }
    return decoded.ToString();
  }

  static long ExtractJsonLong(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        long.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
        ? value : 0;
  }

  static double ExtractJsonDouble(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) +
            "\"\\s*:\\s*(?<value>-?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        double.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
        ? value : 0;
  }

  static ItemDrop FindTargetItem(ZDOID uid) {
    foreach (ItemDrop item in Resources.FindObjectsOfTypeAll<ItemDrop>()) {
      if (item == null || item.gameObject == null) continue;
      ZDO zdo = item.GetComponent<ZNetView>()?.GetZDO();
      if (zdo != null && zdo.m_uid == uid) return item;
    }
    return null;
  }

  static int InventoryUnits() {
    try {
      Inventory inventory = Player.m_localPlayer?.GetInventory();
      if (inventory == null) return -1;
      int count = 0;
      foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        if (item != null) count += item.m_stack;
      return count;
    } catch {
      return -1;
    }
  }

  static string Uid(OwnershipPayload payload) =>
      payload.uid_user.ToString(CultureInfo.InvariantCulture) + ":" +
      payload.uid_id.ToString(CultureInfo.InvariantCulture);

  static string F(float value) =>
      value.ToString("R", CultureInfo.InvariantCulture);

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
          .Replace("\r", "_").Replace("\n", "_");

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
      return false;
    foreach (char c in value)
      if (!char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  static bool Enabled() =>
      PluginConfig.OwnershipLeaseCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveOwnershipLeaseCutover;

  static bool IsServer() {
    try { return ZNet.instance != null && ZNet.instance.IsServer(); }
    catch { return false; }
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  readonly struct CanonicalFrame {
    public readonly string Type;
    public readonly string Json;

    public CanonicalFrame(string type, string json) {
      Type = type;
      Json = json;
    }
  }

  sealed class ClientProbe {
    public string ActionId;
    public string Mode = "owner";
    public string State;
    public Vector3 Origin;
    public ZDOID Target;
    public long LeaseEpoch;
    public DateTimeOffset ExpiresUtc;
    public string ItemPrefab;
    public string AttemptId;
    public float DeadlineAt;
    public float NextAt;
    public long PreDisconnectResumeEpoch;
    public int InitialInventoryUnits;
    public int Attempts;
    public int Reissues;
    public bool SawDisconnected;
    public bool NativeRequestOwnSuppressed;
    public bool NativePickupSuppressed;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class ServerTarget {
    public string ActionId;
    public string HolderLogicalPeerId;
    public long HolderPeerUid;
    public GameObject Instance;
    public ZDO Zdo;
    public ZDOID Target;
    public string AttemptId;
    public string Phase;
    public long LeaseEpoch;
    public float NextAt;
    public int Reissues;
    public bool Authorized;
    public bool Completed;
  }

  [Serializable]
  sealed class OwnershipEnvelope {
    public string type;
    public long seq;
    public OwnershipPayload payload;
  }

  [Serializable]
  sealed class OwnershipPayload {
    public string run_id;
    public string action_id;
    public string attempt_id;
    public string phase;
    public int request_ordinal;
    public string world_epoch;
    public string holder_logical_peer_id;
    public long holder_peer_uid;
    public long uid_user;
    public uint uid_id;
    public long lease_epoch;
    public double origin_x;
    public double origin_y;
    public double origin_z;
    public string expires_utc;
    public string item_prefab;
    public int item_count;
    public string reason;
    public string lease_state;
    public string result;
  }
}

[HarmonyPatch(typeof(ZDOMan))]
static class OwnershipLeaseScopePatches {
  [HarmonyPrefix]
  [HarmonyPatch("ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) })]
  static void ReleaseNearbyPrefix() =>
      OwnershipLeaseCutoverRunner.ReleaseScopeDepth++;

  [HarmonyFinalizer]
  [HarmonyPatch("ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) })]
  static void ReleaseNearbyFinalizer() {
    if (OwnershipLeaseCutoverRunner.ReleaseScopeDepth > 0)
      OwnershipLeaseCutoverRunner.ReleaseScopeDepth--;
  }
}

[HarmonyPatch(typeof(ZDO))]
static class OwnershipLeaseOwnerPatches {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.Last)]
  [HarmonyPatch("SetOwner", new[] { typeof(long) })]
  static bool SetOwnerPrefix(ZDO __instance, long uid) =>
      !OwnershipLeaseCutoverRunner.ShouldBlockRelease(__instance, uid);
}

[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
static class OwnershipLeaseDestroyPatches {
  [HarmonyPrefix]
  static bool DestroyZdoPrefix(ZDO zdo) =>
      !OwnershipLeaseCutoverRunner.ShouldBlockDestroyRequest(zdo);
}

[HarmonyPatch(typeof(ItemDrop))]
static class OwnershipLeaseItemPatches {
  [HarmonyPrefix]
  [HarmonyPatch(nameof(ItemDrop.RequestOwn))]
  static bool RequestOwnPrefix(ItemDrop __instance) =>
      !OwnershipLeaseCutoverRunner.ShouldBlockRequestOwn(__instance);
}

[HarmonyPatch(typeof(Humanoid))]
static class OwnershipLeaseInventoryPatches {
  [HarmonyPrefix]
  [HarmonyPatch(
      nameof(Humanoid.Pickup),
      new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
  static bool PickupPrefix(
      Humanoid __instance, GameObject go, ref bool __result) {
    if (!OwnershipLeaseCutoverRunner.ShouldBlockPickup(__instance, go))
      return true;
    __result = false;
    return false;
  }
}
