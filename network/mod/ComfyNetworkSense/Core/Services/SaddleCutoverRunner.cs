namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using HarmonyLib;
using Lumberjacks.Contracts.Valheim;

using UnityEngine;

/// <summary>
/// Selected saddle/mount cutover lane. Valheim's Sadle grant transfers the
/// Character ZDO owner to the rider; subsequent Controls and ReleaseControl
/// calls self-dispatch locally. This runner keeps those native gameplay
/// handlers, makes the fused user/owner transition canonical on the server,
/// and publishes numbered owner snapshots plus the rider's native
/// SyncTransform parent edge for non-owner presentation.
/// </summary>
public sealed class SaddleCutoverRunner : IDisposable {
  public const string ReceiptFileName = "saddle-cutover.jsonl";

  const int SnapshotSchema = 1;
  const float SnapshotIntervalSeconds = 0.2f;
  const float MaximumSnapshotStepMeters = 25.0f;
  const float MaximumVelocity = 100.0f;
  const float MinimumProofMovementMeters = 3.0f;
  const float MinimumProofHeadingDegrees = 15.0f;
  const float MaximumAttachmentErrorMeters = 0.25f;
  const int MinimumSnapshotAdvance = 15;

  static readonly int RunTagHash =
      ZdoJournalCutoverRunner.ProbeTagName.GetStableHashCode();
  static readonly int ActionTagHash =
      "ComfyNetworkSense_SaddleAction".GetStableHashCode();
  static int _canonicalParentSyncScopeDepth;
  static SaddleCutoverRunner _active;

  readonly LumberjacksGameSessionRunner _gameSession;
  readonly RoutedRpcCutoverRunner _routedRpc;
  readonly TelemetryLogWriter _writer = new();
  readonly Dictionary<ZDOID, ClientAuthority> _clientAuthorities = new();
  readonly Dictionary<ZDOID, ServerAuthority> _serverAuthorities = new();
  readonly Dictionary<ZDOID, uint> _clientSnapshotSequences = new();
  readonly Dictionary<ZDOID, long> _clientSnapshotUsers = new();
  readonly Dictionary<string, uint> _serverSnapshotSequences =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, uint> _replicaSnapshotSequences =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, ZDOID> _serverCharactersByRunPeer =
      new(StringComparer.Ordinal);
  readonly Dictionary<ZDOID, ZDOID> _serverRidersByMount = new();
  readonly Dictionary<ZDOID, ZDOID> _replicaRidersByMount = new();
  readonly Dictionary<string, MountAnnouncement> _announcements =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, ZDOID> _serverMountsByRun =
      new(StringComparer.Ordinal);
  readonly HashSet<ZDOID> _releasedRiderEdgeRepairLogged = new();
  readonly VehicleSnapshotRelevanceSet _snapshotRelevance = new();

  ZRoutedRpc _registeredRpc;
  MountProbe _probe;
  float _nextSnapshotAt;
  float _nextAdoptionAt;
  bool _disposed;

  public SaddleCutoverRunner(
      LumberjacksGameSessionRunner gameSession,
      RoutedRpcCutoverRunner routedRpc) {
    _gameSession = gameSession;
    _routedRpc = routedRpc;
    SaddleCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    EnsureHandlers();
    if (Enabled() && IsServer() && now >= _nextAdoptionAt) {
      _nextAdoptionAt = now + 1.0f;
      AdoptExistingServerMounts();
    }
    if (Enabled() && now >= _nextSnapshotAt) {
      _nextSnapshotAt = now + SnapshotIntervalSeconds;
      PublishOwnedSnapshots();
    }
    TickProbe(now);
  }

  /// <summary>
  /// C6 deliberately suppresses the ordinary remote-player ZSyncTransform
  /// writer. Re-run only the exact native parent solve after C6's world-space
  /// motion apply, and only when the canonical rider ZDO points at this tagged
  /// mount. World-space motion remains the server/AoI truth.
  /// </summary>
  public void LateUpdate(float deltaTime) {
    if (_disposed || !Enabled() || IsServer()) return;
    foreach (Character mount in FindMounts()) {
      ZDO mountZdo = mount.GetComponent<ZNetView>()?.GetZDO();
      Sadle saddle = FindSaddle(mount);
      if (mountZdo == null || saddle?.m_attachPoint == null) continue;
      long user = mountZdo.GetLong(ZDOVars.s_user, 0L);
      if (user == 0) continue;
      Player rider = FindPlayerBySession(user);
      if (rider == null || ReferenceEquals(rider, Player.m_localPlayer)) continue;
      ZNetView riderView = rider.GetComponent<ZNetView>();
      ZDO riderZdo = riderView?.GetZDO();
      ZSyncTransform sync = rider.GetComponent<ZSyncTransform>();
      if (riderZdo == null || sync == null || !sync.m_characterParentSync ||
          riderZdo.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.SyncTransform) != mountZdo.m_uid ||
          !string.Equals(
              riderZdo.GetString(ZDOVars.s_attachJointHash, string.Empty),
              saddle.m_attachPoint.name,
              StringComparison.Ordinal))
        continue;
      try {
        _canonicalParentSyncScopeDepth++;
        sync.CustomFixedUpdate(Mathf.Max(0.001f, Time.fixedDeltaTime));
      } finally {
        _canonicalParentSyncScopeDepth--;
      }
      float error = Vector3.Distance(
          rider.transform.position, saddle.m_attachPoint.position);
      MountProbe probe = _probe;
      if (probe != null && !probe.Terminal && probe.Mode == "observe" &&
          Selected(saddle)) {
        probe.AttachmentErrors.Add(error);
        probe.AttachmentSamples++;
        probe.MaxAttachmentError = Mathf.Max(
            probe.MaxAttachmentError, error);
      }
    }
  }

  internal static bool AllowCanonicalParentSync(ZSyncTransform transform) {
    if (_canonicalParentSyncScopeDepth <= 0 || transform == null) return false;
    Player player = transform.GetComponent<Player>();
    ZDO zdo = player?.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null || !transform.m_characterParentSync) return false;
    ZDOID parent = zdo.GetConnectionZDOID(
        ZDOExtraData.ConnectionType.SyncTransform);
    if (parent.IsNone()) return false;
    GameObject parentObject = ZNetScene.instance?.FindInstance(parent);
    ZDO parentZdo = parentObject?.GetComponent<ZNetView>()?.GetZDO();
    Character parentCharacter = parentObject?.GetComponent<Character>();
    return parentZdo != null && parentCharacter != null &&
        parentCharacter.IsTamed() &&
        parentZdo.GetBool(ZDOVars.s_haveSaddleHash) &&
        FindSaddle(parentCharacter) != null;
  }

  internal static bool AllowCanonicalParentSyncFor(
      Sadle saddle, out Player rider) {
    rider = null;
    if (saddle == null || !Enabled()) return false;
    ZDO mountZdo = saddle.GetCharacter()?.GetComponent<ZNetView>()?.GetZDO();
    Character mount = saddle.GetCharacter();
    if (mountZdo == null || mount == null || !mount.IsTamed() ||
        !mountZdo.GetBool(ZDOVars.s_haveSaddleHash)) return false;
    long user = mountZdo.GetLong(ZDOVars.s_user, 0L);
    if (user == 0) return false;
    rider = FindPlayerBySession(user);
    return rider != null;
  }

  internal static void NotifyVanillaGrant(
      Sadle saddle, long previousOwner, long newOwner) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || saddle == null ||
        previousOwner == 0 || newOwner == 0 || previousOwner == newOwner ||
        previousOwner != ZDOMan.GetSessionID()) return;
    ZDO zdo = saddle.GetCharacter()?.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null) return;
    string actionId = RoutedRpcCutoverRunner.CurrentInboundActionId;
    if (!SafeToken(actionId, 80))
      actionId = active._probe?.ActionId ?? "saddle-owner-grant";
    if (IsServer()) {
      active.CommitServerVanillaGrant(
          zdo, actionId, previousOwner, newOwner);
      return;
    }
    if (!active.SendTransfer(
            actionId, CurrentRunId(), zdo.m_uid, newOwner))
      active.FailMatchingProbe("saddle_transfer_request_queue_failed");
    else
      active.Write("vanilla_grant_observed", actionId,
          "uid=" + zdo.m_uid + " old_owner=" + previousOwner +
          " new_owner=" + newOwner);
  }

  internal static void NotifyVanillaRelease(
      Sadle saddle,
      long sender,
      long playerId,
      long previousUser,
      long releasedUser) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || saddle == null) return;
    ZDO zdo = saddle.GetCharacter()?.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null) return;
    active.Write("vanilla_release_observed", "unscoped",
        "uid=" + zdo.m_uid +
        " owner=" + zdo.GetOwner() +
        " sender=" + sender +
        " player=" + playerId +
        " user_before=" + previousUser +
        " user_after=" + releasedUser +
        " self_dispatch=" + (sender == zdo.GetOwner()));
  }

  internal static void NotifyPeerDetached(long peerId) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer() || peerId == 0) return;
    active.ReclaimDetachedPeer(peerId);
  }

  internal static void NotifyLocalControls(
      Sadle saddle, long sender, Vector3 direction, int speed) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    MountProbe probe = active?._probe;
    if (probe == null || probe.Terminal || probe.Mode != "drive" ||
        saddle == null || !Selected(saddle)) return;
    probe.LocalControlCalls++;
    if (direction.sqrMagnitude > 0.01f && speed is >= 1 and <= 3)
      probe.NonZeroControlCalls++;
  }

  internal static void NotifyRidingTick(Sadle saddle, bool applied) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    MountProbe probe = active?._probe;
    if (applied && probe != null && !probe.Terminal &&
        probe.Mode == "drive" && Selected(saddle))
      probe.RidingTicks++;
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float durationSeconds,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) || mode is not (
            "spawn" or "spawn_untagged" or "wait_mount" or "rendezvous" or "drive" or
            "observe" or "wait_released" or "disconnect_reclaim" or
            "observe_reclaim" or "server_handoff" or "observe_server" or
            "relevance_leave" or "relevance_enter")) {
      detail = "saddle_probe_parameters_invalid";
      return false;
    }
    if (!Enabled()) {
      detail = "saddle_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        ZRoutedRpc.instance == null || ZNetScene.instance == null) {
      detail = "saddle_probe_client_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_saddle_probe_active";
      return false;
    }
    string runId = CurrentRunId();
    if (!SafeToken(runId, 80)) {
      detail = "saddle_probe_run_missing";
      return false;
    }
    _probe = new MountProbe {
        RunId = runId,
        ActionId = actionId,
        Mode = mode,
        StartedAt = Time.unscaledTime,
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 5.0f, 240.0f),
        Duration = Mathf.Clamp(durationSeconds, 0.0f, 30.0f),
        NextAttemptAt = Time.unscaledTime
    };
    Write("probe_started", actionId,
        "mode=" + mode + " duration=" +
        _probe.Duration.ToString("0.##", CultureInfo.InvariantCulture));
    return true;
  }

  public bool TryGetProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    if (_probe == null || !string.Equals(
            _probe.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "saddle_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  void TickProbe(float now) {
    MountProbe probe = _probe;
    if (probe == null || probe.Terminal || IsServer()) return;
    if (now > probe.DeadlineAt) {
      FailProbe("saddle_probe_deadline_exceeded mode=" + probe.Mode +
          " phase=" + probe.Phase + " " + Progress(probe));
      return;
    }
    switch (probe.Mode) {
      case "spawn": TickSpawnProbe(probe); break;
      case "spawn_untagged": TickSpawnProbe(probe); break;
      case "wait_mount": TickWaitMountProbe(probe, now); break;
      case "rendezvous": TickRendezvousProbe(probe); break;
      case "drive": TickDriveProbe(probe, now); break;
      case "observe": TickObserveProbe(probe, now); break;
      case "wait_released": TickWaitReleasedProbe(probe); break;
      case "disconnect_reclaim": TickDisconnectReclaimProbe(probe, now); break;
      case "observe_reclaim": TickObserveReclaimProbe(probe); break;
      case "server_handoff": TickServerHandoffProbe(probe, now); break;
      case "observe_server": TickObserveServerProbe(probe, now); break;
      case "relevance_leave": TickRelevanceProbe(probe, enter: false); break;
      case "relevance_enter": TickRelevanceProbe(probe, enter: true); break;
    }
  }

  void TickServerHandoffProbe(MountProbe probe, float now) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out _) || view?.GetZDO() == null)
      return;
    ZDO zdo = view.GetZDO();
    long serverPeerId = ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
    long localPeerId = Player.m_localPlayer?.GetZDOID().UserID ?? 0;
    if (serverPeerId == 0 || localPeerId == 0) return;

    if (!probe.RequestSent) {
      if (!_clientAuthorities.TryGetValue(
              zdo.m_uid, out ClientAuthority authority) ||
          authority.OwnerPeerId != localPeerId ||
          zdo.GetOwner() != localPeerId || !view.IsOwner() ||
          zdo.GetLong(ZDOVars.s_user, 0L) != 0)
        return;
      probe.PreReclaimEpoch = authority.Epoch;
      if (!SendTransfer(
              probe.ActionId, probe.RunId, zdo.m_uid, serverPeerId)) {
        FailProbe("saddle_server_handoff_queue_failed");
        return;
      }
      probe.RequestSent = true;
      probe.Phase = "waiting_server_owner_epoch";
      return;
    }

    if (!_clientAuthorities.TryGetValue(
            zdo.m_uid, out ClientAuthority current) ||
        current.OwnerPeerId != serverPeerId ||
        current.Epoch != checked(probe.PreReclaimEpoch + 1) ||
        zdo.GetOwner() != serverPeerId || view.IsOwner())
      return;
    int sequence = ReplicaSequence(zdo.m_uid, current.Epoch);
    if (!probe.ObservationStarted) {
      probe.ObservationStarted = true;
      probe.ControlStartedAt = now;
      probe.StartSnapshotSequence = sequence;
      probe.AuthorityEpoch = current.Epoch;
      probe.Phase = "observing_server_owner_snapshots";
      return;
    }
    probe.SnapshotAdvance = Math.Max(
        probe.SnapshotAdvance,
        Math.Max(0, sequence - probe.StartSnapshotSequence));
    if (now - probe.ControlStartedAt >= 2.0f && probe.SnapshotAdvance >= 8)
      CompleteProbe("saddle_server_handoff_complete uid=" + zdo.m_uid +
          " owner=" + serverPeerId + " epoch=" + current.Epoch +
          " snapshot_advance=" + probe.SnapshotAdvance);
  }

  void TickObserveServerProbe(MountProbe probe, float now) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out _) || view?.GetZDO() == null)
      return;
    ZDO zdo = view.GetZDO();
    long serverPeerId = ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
    if (serverPeerId == 0 ||
        !_clientAuthorities.TryGetValue(
            zdo.m_uid, out ClientAuthority authority) ||
        authority.OwnerPeerId != serverPeerId ||
        zdo.GetOwner() != serverPeerId || view.IsOwner() ||
        zdo.GetLong(ZDOVars.s_user, 0L) != 0)
      return;
    int sequence = ReplicaSequence(zdo.m_uid, authority.Epoch);
    if (!probe.ObservationStarted) {
      probe.ObservationStarted = true;
      probe.ControlStartedAt = now;
      probe.StartSnapshotSequence = sequence;
      probe.AuthorityEpoch = authority.Epoch;
      probe.Phase = "observing_server_owner_snapshots";
      return;
    }
    probe.SnapshotAdvance = Math.Max(
        probe.SnapshotAdvance,
        Math.Max(0, sequence - probe.StartSnapshotSequence));
    if (now - probe.ControlStartedAt >= 2.0f && probe.SnapshotAdvance >= 8)
      CompleteProbe("saddle_server_observer_complete uid=" + zdo.m_uid +
          " owner=" + serverPeerId + " epoch=" + authority.Epoch +
          " snapshot_advance=" + probe.SnapshotAdvance);
  }

  void TickRelevanceProbe(MountProbe probe, bool enter) {
    if (!_announcements.TryGetValue(
            probe.RunId, out MountAnnouncement announcement)) return;
    Player player = Player.m_localPlayer;
    GameObject mountObject =
        ZNetScene.instance?.FindInstance(announcement.Uid);
    Vector3 mountPosition = mountObject != null
        ? mountObject.transform.position
        : announcement.Position;
    Vector3 target = enter
        ? mountPosition + new Vector3(3.0f, 2.5f, 3.0f)
        : mountPosition + new Vector3(96.0f, 2.5f, 0.0f);
    if (!probe.RequestSent) {
      if (!player.TeleportTo(target, player.transform.rotation, true)) return;
      probe.RequestSent = true;
      probe.Phase = enter
          ? "teleporting_into_snapshot_relevance"
          : "teleporting_out_of_snapshot_relevance";
      return;
    }
    if (player.IsTeleporting()) return;
    float distance = Vector3.Distance(
        player.transform.position, mountPosition);
    bool reached = enter ? distance <= 12.0f : distance >= 88.0f;
    if (!reached) return;
    CompleteProbe(
        (enter ? "saddle_relevance_enter_complete" :
            "saddle_relevance_leave_complete") +
        " uid=" + announcement.Uid +
        " distance=" + distance.ToString(
            "0.###", CultureInfo.InvariantCulture));
  }

  void TickSpawnProbe(MountProbe probe) {
    if (probe.RequestSent) return;
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return;
    ZPackage request = new();
    request.Write(probe.RunId);
    request.Write(probe.ActionId);
    request.Write(probe.Mode == "spawn_untagged");
    request.Write(true);
    request.SetPos(0);
    probe.RequestSent = _routedRpc.InvokeTyped(
        probe.ActionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid,
            ValheimRoutedRpcAdmissions.CutoverSaddleSpawnRequest,
            new object[] { request }));
    if (!probe.RequestSent) FailProbe("saddle_spawn_request_queue_failed");
    else probe.Phase = "spawn_requested";
  }

  void TickWaitMountProbe(MountProbe probe, float now) {
    if (!_announcements.TryGetValue(
            probe.RunId, out MountAnnouncement announcement)) {
      if (now >= probe.NextAttemptAt) {
        probe.NextAttemptAt = now + 2.0f;
        ZNetPeer server = ZNet.instance?.GetServerPeer();
        if (server != null && server.m_uid != 0) {
          ZPackage request = new();
          request.Write(probe.RunId);
          request.Write(probe.ActionId);
          request.Write(false);
          request.Write(false);
          request.SetPos(0);
          _routedRpc.InvokeTyped(
              probe.ActionId,
              () => ZRoutedRpc.instance.InvokeRoutedRPC(
                  server.m_uid,
                  ValheimRoutedRpcAdmissions.CutoverSaddleSpawnRequest,
                  new object[] { request }));
          probe.Phase = "requesting_mount_announcement";
        }
      }
      return;
    }
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out Sadle saddle)) return;
    string shape = string.Empty;
    if (view.GetZDO().m_uid != announcement.Uid ||
        !ValidateRuntimeShape(mount, view, saddle, out shape)) {
      FailProbe("saddle_runtime_shape_invalid " + shape);
      return;
    }
    CompleteProbe("saddle_instantiated uid=" + announcement.Uid + " " + shape);
  }

  void TickRendezvousProbe(MountProbe probe) {
    if (!_announcements.TryGetValue(
            probe.RunId, out MountAnnouncement announcement)) return;
    Player player = Player.m_localPlayer;
    Vector3 target = announcement.Position + new Vector3(3.0f, 2.5f, 3.0f);
    if (!probe.RequestSent) {
      if (!player.TeleportTo(target, player.transform.rotation, true)) return;
      probe.RequestSent = true;
      probe.Phase = "teleporting_to_mount";
      return;
    }
    if (!player.IsTeleporting() &&
        Vector3.Distance(player.transform.position, target) <= 12.0f &&
        TryFindRunMount(probe.RunId, out _, out _, out _))
      CompleteProbe("saddle_rendezvous_complete uid=" + announcement.Uid);
  }

  void TickDriveProbe(MountProbe probe, float now) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out Sadle saddle)) return;
    Player player = Player.m_localPlayer;
    ZDO zdo = view.GetZDO();
    long localSession = player.GetZDOID().UserID;
    bool controlling = ReferenceEquals(player.GetDoodadController(), saddle) &&
        player.IsRiding() && player.IsAttached();

    if (!probe.ControlGranted) {
      if (!controlling) {
        if (now < probe.NextAttemptAt) return;
        probe.NextAttemptAt = now + 0.75f;
        if (saddle.m_attachPoint == null) {
          FailProbe("saddle_attach_point_missing");
          return;
        }
        Vector3 target = saddle.m_attachPoint.position +
            mount.transform.right * 1.5f + Vector3.up * 0.25f;
        Rigidbody playerBody = player.GetComponent<Rigidbody>();
        player.transform.position = target;
        if (playerBody != null) {
          playerBody.position = target;
          playerBody.linearVelocity = Vector3.zero;
          playerBody.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        _routedRpc.InvokeTyped(
            probe.ActionId,
            () => saddle.Interact(player, repeat: false, alt: false));
        probe.Phase = "requesting_saddle_control";
        return;
      }
      if (!view.IsOwner() || zdo.GetOwner() != localSession ||
          zdo.GetLong(ZDOVars.s_user, 0L) != localSession) {
        probe.Phase = "awaiting_canonical_grant";
        return;
      }
      if (!TryReadLocalRiderEdge(
              player, zdo.m_uid, saddle, out string edgeDetail)) {
        probe.Phase = "awaiting_native_parent_edge:" + edgeDetail;
        return;
      }
      probe.ControlGranted = true;
      probe.ControlStartedAt = now;
      probe.StartPosition = mount.transform.position;
      probe.StartRotation = mount.transform.rotation;
      probe.AuthorityEpoch = ClientEpoch(zdo.m_uid);
      probe.StartSnapshotSequence =
          ReplicaSequence(zdo.m_uid, probe.AuthorityEpoch);
      probe.Phase = "riding";
      Write("saddle_grant_converged", probe.ActionId,
          "uid=" + zdo.m_uid + " owner=" + zdo.GetOwner() +
          " rider=" + localSession + " " + edgeDetail);
    }

    if (!probe.ReleaseSent && !controlling) {
      FailProbe("saddle_control_attachment_lost " + Progress(probe));
      return;
    }
    ObserveMotion(probe, mount);
    if (!probe.ReleaseSent && now - probe.ControlStartedAt < probe.Duration) {
      Vector3 direction =
          (mount.transform.forward + mount.transform.right * 0.75f).normalized;
      saddle.ApplyControlls(
          new Vector3(0.35f, 0.0f, 1.0f),
          direction,
          run: true,
          autoRun: false,
          block: false);
      return;
    }
    if (!probe.ReleaseSent) {
      probe.ReleaseSent = true;
      player.StopDoodadControl();
      probe.Phase = "releasing_saddle";
      return;
    }
    if (player.GetDoodadController() == null && !player.IsRiding() &&
        !player.IsAttached() && zdo.GetLong(ZDOVars.s_user, 0L) == 0L) {
      if (zdo.GetOwner() != localSession ||
          ClientEpoch(zdo.m_uid) != probe.AuthorityEpoch) {
        FailProbe("saddle_release_did_not_retain_rider_authority " +
            Progress(probe));
        return;
      }
      if (!DriveProofPassed(probe)) {
        FailProbe("saddle_drive_semantics_missing " + Progress(probe));
        return;
      }
      CompleteProbe("saddle_drive_complete " + Progress(probe));
    }
  }

  void TickObserveProbe(MountProbe probe, float now) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out Sadle saddle)) return;
    ZDO zdo = view.GetZDO();
    if (!probe.ObservationStarted) {
      long user = zdo.GetLong(ZDOVars.s_user, 0L);
      if (user == 0 || view.IsOwner()) {
        probe.Phase = "waiting_remote_rider";
        return;
      }
      if (zdo.GetOwner() != user) {
        FailProbe("saddle_observer_owner_user_mismatch owner=" +
            zdo.GetOwner() + " user=" + user);
        return;
      }
      Player rider = FindPlayerBySession(user);
      if (rider == null) return;
      if (!_clientAuthorities.TryGetValue(
              zdo.m_uid, out ClientAuthority authority) ||
          authority.Epoch == 0 || authority.OwnerPeerId != user) {
        // Vanilla mutates owner/user before the canonical epoch response can
        // arrive. Do not start an observer against that half-committed state:
        // an in-flight prior-epoch snapshot may still clear the native user.
        probe.Phase = "awaiting_canonical_rider_authority";
        return;
      }
      probe.ObservationStarted = true;
      probe.ControlStartedAt = now;
      probe.StartPosition = mount.transform.position;
      probe.StartRotation = mount.transform.rotation;
      probe.AuthorityEpoch = authority.Epoch;
      probe.StartSnapshotSequence =
          ReplicaSequence(zdo.m_uid, probe.AuthorityEpoch);
      probe.ObservedRider = user;
      probe.Phase = "observing_saddle";
      Write("saddle_observer_started", probe.ActionId,
          "uid=" + zdo.m_uid + " owner=" + zdo.GetOwner() +
          " rider=" + user);
    }
    long currentUser = zdo.GetLong(ZDOVars.s_user, 0L);
    if (currentUser != 0 && (currentUser != probe.ObservedRider ||
        zdo.GetOwner() != currentUser ||
        ClientEpoch(zdo.m_uid) != probe.AuthorityEpoch)) {
      FailProbe("saddle_observer_authority_changed " + Progress(probe));
      return;
    }
    if (currentUser == 0 && (zdo.GetOwner() != probe.ObservedRider ||
        ClientEpoch(zdo.m_uid) != probe.AuthorityEpoch)) {
      FailProbe("saddle_observer_release_authority_changed " +
          Progress(probe));
      return;
    }
    ObserveMotion(probe, mount);
    if (now - probe.ControlStartedAt < probe.Duration &&
        zdo.GetLong(ZDOVars.s_user, 0L) != 0L) return;
    if (!ObserveProofPassed(probe)) {
      FailProbe("saddle_observer_semantics_missing " + Progress(probe));
      return;
    }
    CompleteProbe("saddle_observer_complete " + Progress(probe));
  }

  void TickWaitReleasedProbe(MountProbe probe) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out _)) return;
    ZDO zdo = view.GetZDO();
    if (zdo.GetLong(ZDOVars.s_user, 0L) != 0L) return;
    if (FindPlayerConnectedToMount(zdo.m_uid) != null) return;
    if (! _clientAuthorities.TryGetValue(
            zdo.m_uid, out ClientAuthority authority) ||
        authority.OwnerPeerId == 0 || zdo.GetOwner() != authority.OwnerPeerId) {
      FailProbe("saddle_release_authority_not_canonical");
      return;
    }
    CompleteProbe("saddle_released uid=" + zdo.m_uid +
        " owner_retained=" + zdo.GetOwner() +
        " epoch=" + authority.Epoch);
  }

  void TickDisconnectReclaimProbe(MountProbe probe, float now) {
    if (!TryFindRunMount(
            probe.RunId, out Character mount, out ZNetView view,
            out Sadle saddle)) return;
    Player player = Player.m_localPlayer;
    ZDO zdo = view.GetZDO();
    long local = player.GetZDOID().UserID;
    bool controlling = ReferenceEquals(player.GetDoodadController(), saddle) &&
        player.IsRiding() && player.IsAttached();
    if (!probe.ControlGranted) {
      if (!controlling) {
        long currentOwner = zdo.GetOwner();
        if (probe.ExpectedReclaimOwner == 0 && currentOwner != 0 &&
            currentOwner != local)
          probe.ExpectedReclaimOwner = currentOwner;
        if (now < probe.NextAttemptAt) return;
        probe.NextAttemptAt = now + 0.75f;
        Vector3 target = saddle.m_attachPoint.position +
            mount.transform.right * 1.5f + Vector3.up * 0.25f;
        player.transform.position = target;
        Rigidbody body = player.GetComponent<Rigidbody>();
        if (body != null) body.position = target;
        Physics.SyncTransforms();
        _routedRpc.InvokeTyped(
            probe.ActionId,
            () => saddle.Interact(player, false, false));
        return;
      }
      if (!view.IsOwner() || zdo.GetLong(ZDOVars.s_user, 0L) != local)
        return;
      if (probe.ExpectedReclaimOwner == 0 ||
          probe.ExpectedReclaimOwner == local) {
        FailProbe("saddle_reclaim_previous_owner_missing");
        return;
      }
      probe.ControlGranted = true;
      probe.ControlStartedAt = now;
      probe.PreReclaimEpoch = ClientEpoch(zdo.m_uid);
      probe.Phase = "holding_rider_for_reclaim_observer";
      return;
    }
    if (!probe.AbortSent) {
      saddle.ApplyControlls(
          new Vector3(0.0f, 0.0f, 1.0f),
          mount.transform.forward, true, false, false);
      if (now - probe.ControlStartedAt < Mathf.Max(3.0f, probe.Duration))
        return;
      probe.AbortSent = _gameSessionAbort();
      if (!probe.AbortSent) return;
      probe.Phase = "lumberjacks_socket_aborted_while_riding";
      Write("saddle_rider_socket_aborted", probe.ActionId,
          "uid=" + zdo.m_uid + " owner=" + local +
          " epoch=" + probe.PreReclaimEpoch);
      return;
    }
    if (zdo.GetLong(ZDOVars.s_user, 0L) == 0L &&
        zdo.GetOwner() == probe.ExpectedReclaimOwner &&
        player.GetDoodadController() == null &&
        !player.IsRiding() && !player.IsAttached() &&
        ClientEpoch(zdo.m_uid) > probe.PreReclaimEpoch) {
      CompleteProbe("saddle_disconnect_reclaimed uid=" + zdo.m_uid +
          " owner=" + zdo.GetOwner() + " epoch=" + ClientEpoch(zdo.m_uid));
    }
  }

  void TickObserveReclaimProbe(MountProbe probe) {
    if (!TryFindRunMount(
            probe.RunId, out _, out ZNetView view, out _)) return;
    ZDO zdo = view.GetZDO();
    long user = zdo.GetLong(ZDOVars.s_user, 0L);
    if (!probe.ObservationStarted) {
      if (user == 0) return;
      probe.ObservationStarted = true;
      probe.ObservedRider = user;
      probe.ExpectedReclaimOwner = ZNet.GetUID();
      probe.PreReclaimEpoch = ClientEpoch(zdo.m_uid);
      probe.Phase = "waiting_disconnect_reclaim";
      return;
    }
    if (user == 0 && zdo.GetOwner() == probe.ExpectedReclaimOwner &&
        ClientEpoch(zdo.m_uid) > probe.PreReclaimEpoch &&
        FindPlayerConnectedToMount(zdo.m_uid) == null)
      CompleteProbe("saddle_observer_reclaim_complete uid=" + zdo.m_uid +
          " owner=" + zdo.GetOwner() + " epoch=" + ClientEpoch(zdo.m_uid));
  }

  bool _gameSessionAbort() {
    try { return _gameSession?.AbortForOwnershipProbe() ?? false; }
    catch { return false; }
  }

  void EnsureHandlers() {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverSaddleSpawnRequest,
        HandleSpawnRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverSaddleSpawnResponse,
        HandleSpawnResponse);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverSaddleTransferRequest,
        HandleTransferRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverSaddleTransferResponse,
        HandleTransferResponse);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.ModSaddleSnapshot,
        HandleSnapshot);
    _registeredRpc = rpc;
    Write("handlers_registered", "unscoped", Role());
  }

  void AdoptExistingServerMounts() {
    foreach (Character mount in FindMounts()) {
      ZDO zdo = mount.GetComponent<ZNetView>()?.GetZDO();
      if (zdo != null) AdoptServerAuthority(zdo, "existing_mount_scan");
    }
  }

  void AdoptServerAuthority(ZDO zdo, string source) {
    if (zdo == null || _serverAuthorities.ContainsKey(zdo.m_uid) ||
        !IsSaddlePrefab(zdo.GetPrefab()) ||
        !zdo.GetBool(ZDOVars.s_tamed) ||
        !zdo.GetBool(ZDOVars.s_haveSaddleHash)) return;
    long owner = zdo.GetOwner();
    if (owner == 0) return;
    _serverAuthorities[zdo.m_uid] = new ServerAuthority(owner, 0, 1);
    Write("saddle_authority_adopted", "unscoped",
        "uid=" + zdo.m_uid +
        " owner=" + owner +
        " epoch=1 source=" + source +
        " run_tag=" +
        (string.IsNullOrEmpty(zdo.GetString(RunTagHash, string.Empty))
            ? "absent" : "present"));
  }

  void PublishOwnedSnapshots() {
    bool serverPublisher = IsServer();
    ZNetPeer server = serverPublisher ? null : ZNet.instance?.GetServerPeer();
    if (!serverPublisher && (server == null || server.m_uid == 0)) return;
    HashSet<ZDOID> publishedServerMounts = serverPublisher ? new() : null;
    foreach (Character mount in FindMounts()) {
      ZNetView view = mount.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      Rigidbody body = mount.GetComponent<Rigidbody>();
      Sadle saddle = FindSaddle(mount);
      if (zdo == null || body == null || saddle == null || !view.IsOwner() ||
          zdo.GetOwner() != ZNet.GetUID())
        continue;
      long authorityOwner;
      uint authorityEpoch;
      if (serverPublisher) {
        AdoptServerAuthority(zdo, "server_owner_publish");
        if (!_serverAuthorities.TryGetValue(
                zdo.m_uid, out ServerAuthority serverAuthority) ||
            serverAuthority.OwnerPeerId != ZNet.GetUID())
          continue;
        authorityOwner = serverAuthority.OwnerPeerId;
        authorityEpoch = serverAuthority.Epoch;
      } else {
        if (!_clientAuthorities.TryGetValue(
                zdo.m_uid, out ClientAuthority authority)) {
          long owner = zdo.GetOwner();
          if (owner == 0 || owner != ZNet.GetUID()) continue;
          authority = new ClientAuthority(owner, 1);
          _clientAuthorities[zdo.m_uid] = authority;
          Write("saddle_authority_adopted", "unscoped",
              "uid=" + zdo.m_uid + " owner=" + owner +
              " epoch=1 source=existing_untagged_owner");
        }
        if (authority.OwnerPeerId != ZNet.GetUID()) continue;
        authorityOwner = authority.OwnerPeerId;
        authorityEpoch = authority.Epoch;
      }

      _clientSnapshotSequences.TryGetValue(zdo.m_uid, out uint sequence);
      sequence++;
      _clientSnapshotSequences[zdo.m_uid] = sequence;
      long user = zdo.GetLong(ZDOVars.s_user, 0L);
      if (user == 0L) RepairReleasedRiderEdges(zdo.m_uid);
      ZDOID riderId = ZDOID.None;
      bool parentSync = false;
      string attachJoint = string.Empty;
      Vector3 relativePosition = Vector3.zero;
      Quaternion relativeRotation = Quaternion.identity;
      Vector3 relativeVelocity = Vector3.zero;
      if (user != 0) {
        Player rider = FindPlayerBySession(user);
        ZDO riderZdo = rider?.GetComponent<ZNetView>()?.GetZDO();
        ZSyncTransform sync = rider?.GetComponent<ZSyncTransform>();
        if (riderZdo == null || sync == null || !sync.m_characterParentSync ||
            !ReferenceEquals(rider, Player.m_localPlayer) ||
            !ReferenceEquals(rider.GetDoodadController(), saddle) ||
            !rider.IsRiding() || !rider.IsAttached() ||
            !rider.GetRelativePosition(
                out ZDOID nativeParent, out string nativeJoint,
                out Vector3 nativeRelativePosition,
                out Quaternion nativeRelativeRotation,
                out Vector3 nativeRelativeVelocity) ||
            nativeParent != zdo.m_uid ||
            !string.Equals(
                nativeJoint, saddle.m_attachPoint.name,
                StringComparison.Ordinal) ||
            !Finite(nativeRelativePosition) ||
            !Finite(nativeRelativeRotation) ||
            !Finite(nativeRelativeVelocity))
          continue;
        riderId = riderZdo.m_uid;
        parentSync = true;
        attachJoint = nativeJoint;
        relativePosition = nativeRelativePosition;
        relativeRotation = nativeRelativeRotation;
        relativeVelocity = nativeRelativeVelocity;
        bool edgeStale = riderZdo.GetConnectionZDOID(
                ZDOExtraData.ConnectionType.SyncTransform) != nativeParent ||
            !string.Equals(
                riderZdo.GetString(
                    ZDOVars.s_attachJointHash, string.Empty),
                nativeJoint,
                StringComparison.Ordinal) ||
            Vector3.Distance(
                riderZdo.GetVec3(ZDOVars.s_relPosHash, Vector3.zero),
                nativeRelativePosition) > 0.001f ||
            Quaternion.Angle(
                riderZdo.GetQuaternion(
                    ZDOVars.s_relRotHash, Quaternion.identity),
                nativeRelativeRotation) > 0.01f;
        if (edgeStale) {
          ApplyRiderEdge(
              riderZdo, nativeParent, nativeJoint,
              nativeRelativePosition, nativeRelativeRotation,
              nativeRelativeVelocity);
          Write("native_rider_edge_repaired", "unscoped",
              "uid=" + zdo.m_uid +
              " rider=" + riderId +
              " joint=" + nativeJoint +
              " rel_pos=" + Format(nativeRelativePosition) +
              " rel_rot_deg=" + Quaternion.Angle(
                  nativeRelativeRotation, Quaternion.identity).ToString(
                      "0.###", CultureInfo.InvariantCulture));
        }
      }
      Snapshot snapshot = new() {
          RunId = CurrentRunId(),
          Uid = zdo.m_uid,
          OwnerPeerId = authorityOwner,
          Epoch = authorityEpoch,
          Sequence = sequence,
          Position = mount.transform.position,
          Rotation = mount.transform.rotation,
          Velocity = body.linearVelocity,
          AngularVelocity = body.angularVelocity,
          User = user,
          Stamina = saddle.GetStamina(),
          RiderId = riderId,
          ParentSync = parentSync,
          AttachJoint = attachJoint,
          RelativePosition = relativePosition,
          RelativeRotation = relativeRotation,
          RelativeVelocity = relativeVelocity
      };
      bool hadPreviousUser = _clientSnapshotUsers.TryGetValue(
          zdo.m_uid, out long previousUser);
      bool userChanged = hadPreviousUser && previousUser != user;
      _clientSnapshotUsers[zdo.m_uid] = user;
      if (sequence == 1 || userChanged) {
        Write(userChanged ? "snapshot_owner_user_transition" :
                "snapshot_owner_published", "unscoped",
            "uid=" + zdo.m_uid + " epoch=" + authorityEpoch +
            " sequence=" + sequence +
            " owner=" + authorityOwner +
            " previous_user=" + (hadPreviousUser ? previousUser : 0L) +
            " user=" + user +
            " rider=" + riderId + " parent_sync=" + parentSync +
            " joint=" + attachJoint +
            " mount_velocity=" + Format(body.linearVelocity) +
            " mount_speed=" + body.linearVelocity.magnitude.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " mount_angular_velocity=" + Format(body.angularVelocity) +
            " mount_angular_speed=" + body.angularVelocity.magnitude.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " stamina=" + saddle.GetStamina().ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " rel_pos=" + Format(relativePosition) +
            " rel_rot_deg=" + Quaternion.Angle(
                relativeRotation, Quaternion.identity).ToString(
                    "0.###", CultureInfo.InvariantCulture) +
            " rider_velocity=" + Format(relativeVelocity) +
            " rider_speed=" + relativeVelocity.magnitude.ToString(
                "0.###", CultureInfo.InvariantCulture));
      }
      if (serverPublisher) {
        publishedServerMounts.Add(zdo.m_uid);
        PublishServerOwnedSnapshot(zdo, snapshot, "live_instance");
        continue;
      }

      ZPackage package = BuildSnapshotPackage(snapshot);
      ZRoutedRpc.instance.InvokeRoutedRPC(
          server.m_uid,
          ValheimRoutedRpcAdmissions.ModSaddleSnapshot,
          new object[] { package });
    }
    if (serverPublisher)
      PublishServerOwnedZdoSnapshots(publishedServerMounts);
  }

  void PublishServerOwnedZdoSnapshots(ISet<ZDOID> publishedMounts) {
    long serverPeerId = ZNet.GetUID();
    foreach (KeyValuePair<ZDOID, ServerAuthority> pair in
             _serverAuthorities.ToArray()) {
      if (publishedMounts.Contains(pair.Key) ||
          pair.Value.OwnerPeerId != serverPeerId)
        continue;
      ZDO zdo = ZDOMan.instance?.GetZDO(pair.Key);
      if (zdo == null || zdo.GetOwner() != serverPeerId ||
          !IsSaddlePrefab(zdo.GetPrefab()) ||
          !zdo.GetBool(ZDOVars.s_tamed) ||
          !zdo.GetBool(ZDOVars.s_haveSaddleHash))
        continue;
      long user = zdo.GetLong(ZDOVars.s_user, 0L);
      if (user != 0L) {
        Write("snapshot_server_owner_invalid", "unscoped",
            "uid=" + zdo.m_uid + " owner=" + serverPeerId +
            " user=" + user + " representation=zdo_only");
        continue;
      }

      _clientSnapshotSequences.TryGetValue(zdo.m_uid, out uint sequence);
      sequence++;
      _clientSnapshotSequences[zdo.m_uid] = sequence;
      _clientSnapshotUsers[zdo.m_uid] = 0L;
      Snapshot snapshot = new() {
          RunId = CurrentRunId(),
          Uid = zdo.m_uid,
          OwnerPeerId = serverPeerId,
          Epoch = pair.Value.Epoch,
          Sequence = sequence,
          Position = zdo.GetPosition(),
          Rotation = zdo.GetRotation(),
          Velocity = zdo.GetVec3(
              ZDOVars.s_bodyVelHash,
              zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero)),
          AngularVelocity = zdo.GetVec3(
              ZDOVars.s_bodyAVelHash, Vector3.zero),
          User = 0L,
          Stamina = zdo.GetFloat(ZDOVars.s_stamina, 0.0f),
          RiderId = ZDOID.None,
          ParentSync = false,
          AttachJoint = string.Empty,
          RelativePosition = Vector3.zero,
          RelativeRotation = Quaternion.identity,
          RelativeVelocity = Vector3.zero
      };
      if (sequence == 1)
        Write("snapshot_owner_published", "unscoped",
            "uid=" + zdo.m_uid + " epoch=" + pair.Value.Epoch +
            " sequence=1 owner=" + serverPeerId +
            " previous_user=0 user=0 rider=" + ZDOID.None +
            " parent_sync=false joint= mount_velocity=" +
            Format(snapshot.Velocity) +
            " mount_speed=" + snapshot.Velocity.magnitude.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " mount_angular_velocity=" + Format(snapshot.AngularVelocity) +
            " mount_angular_speed=" +
            snapshot.AngularVelocity.magnitude.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " stamina=" + snapshot.Stamina.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " rel_pos=(0,0,0) rel_rot_deg=0" +
            " rider_velocity=(0,0,0) rider_speed=0" +
            " representation=zdo_only");
      PublishServerOwnedSnapshot(zdo, snapshot, "zdo_only");
    }
  }

  void PublishServerOwnedSnapshot(
      ZDO zdo, Snapshot snapshot, string representation) {
    _serverSnapshotSequences[SnapshotKey(zdo.m_uid, snapshot.Epoch)] =
        snapshot.Sequence;
    ApplyCanonicalSnapshot(zdo, snapshot);
    if (!FanOutSnapshot(snapshot, "saddle-server-owner-snapshot")) {
      Write("snapshot_fanout_failed", "unscoped",
          "uid=" + zdo.m_uid +
          " source=server_owner representation=" + representation);
      return;
    }
    if (snapshot.Sequence == 1 || snapshot.Sequence % 25 == 0)
      Write("snapshot_server_accepted", "unscoped",
          "uid=" + zdo.m_uid + " epoch=" + snapshot.Epoch +
          " sequence=" + snapshot.Sequence +
          " owner=" + snapshot.OwnerPeerId +
          " source=server_owner representation=" + representation);
  }

  static void HandleSnapshot(long senderPeerId, ZPackage package) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled()) return;
    if (IsServer() && senderPeerId == ZNet.GetUID()) return;
    try {
      Snapshot snapshot = ReadSnapshot(package);
      ZDO mountZdo = ZDOMan.instance?.GetZDO(snapshot.Uid);
      if (IsServer()) {
        GameObject mountObject = ZNetScene.instance?.FindInstance(snapshot.Uid);
        Character serverCharacter = mountObject?.GetComponent<Character>();
        if (serverCharacter == null)
          serverCharacter = ZNetScene.instance?
              .GetPrefab(mountZdo?.GetPrefab() ?? 0)?
              .GetComponent<Character>();
        Sadle serverSaddle = FindSaddle(serverCharacter);
        bool senderResolved = TryResolveAuthenticatedPeer(
            senderPeerId, out ZDOID senderCharacterId,
            out Vector3 senderPosition);
        bool runMatches = string.Equals(
            snapshot.RunId, CurrentRunId(), StringComparison.Ordinal);
        if (mountZdo != null)
          active.AdoptServerAuthority(mountZdo, "snapshot");
        if (senderResolved && runMatches &&
            active._serverAuthorities.TryGetValue(
                snapshot.Uid, out ServerAuthority currentAuthority) &&
            snapshot.OwnerPeerId == senderPeerId &&
            snapshot.Epoch < currentAuthority.Epoch) {
          active.Write("snapshot_stale_authority_rejected", "unscoped",
              "uid=" + snapshot.Uid +
              " sender=" + senderPeerId +
              " stale_epoch=" + snapshot.Epoch +
              " current_epoch=" + currentAuthority.Epoch +
              " current_owner=" + currentAuthority.OwnerPeerId);
          return;
        }
        if (mountZdo == null || !IsSaddlePrefab(mountZdo.GetPrefab()) ||
            !mountZdo.GetBool(ZDOVars.s_tamed) ||
            !mountZdo.GetBool(ZDOVars.s_haveSaddleHash) ||
            serverSaddle?.m_attachPoint == null ||
            !senderResolved ||
            !string.Equals(
                snapshot.RunId, CurrentRunId(), StringComparison.Ordinal) ||
            !active._serverAuthorities.TryGetValue(
                snapshot.Uid, out ServerAuthority authority) ||
            authority.OwnerPeerId != senderPeerId ||
            authority.OwnerPeerId != snapshot.OwnerPeerId ||
            authority.Epoch != snapshot.Epoch ||
            mountZdo.GetOwner() != senderPeerId ||
            (snapshot.User != 0 && snapshot.User != senderPeerId) ||
            snapshot.Stamina > serverSaddle.GetMaxStamina() + 0.01f)
          throw new InvalidOperationException("saddle_snapshot_authority_invalid");
        active._serverCharactersByRunPeer[
            ServerCharacterKey(snapshot.RunId, senderPeerId)] =
            senderCharacterId;
        if (snapshot.User != 0) {
          if (snapshot.RiderId != senderCharacterId ||
              Vector3.Distance(senderPosition, snapshot.Position) >
                  serverSaddle.m_maxUseRange + 5.0f ||
              !string.Equals(
                  snapshot.AttachJoint,
                  serverSaddle.m_attachPoint.name,
                  StringComparison.Ordinal))
            throw new InvalidOperationException(
                "saddle_snapshot_rider_invalid");
        }
        if (Vector3.Distance(
                mountZdo.GetPosition(), snapshot.Position) >
            MaximumSnapshotStepMeters)
          throw new InvalidOperationException("saddle_snapshot_displacement_invalid");
        string key = SnapshotKey(snapshot.Uid, snapshot.Epoch);
        active._serverSnapshotSequences.TryGetValue(key, out uint previous);
        if (snapshot.Sequence <= previous)
          throw new InvalidOperationException("saddle_snapshot_sequence_stale");
        active._serverSnapshotSequences[key] = snapshot.Sequence;

        active.ApplyCanonicalSnapshot(mountZdo, snapshot);
        if (!active.FanOutSnapshot(
                snapshot, "saddle-snapshot-fanout"))
          throw new InvalidOperationException(
              "saddle_snapshot_fanout_queue_failed");
        if (snapshot.Sequence == 1 || snapshot.Sequence % 25 == 0)
          active.Write("snapshot_server_accepted", "unscoped",
              "uid=" + snapshot.Uid + " epoch=" + snapshot.Epoch +
              " sequence=" + snapshot.Sequence +
              " owner=" + snapshot.OwnerPeerId);
        return;
      }

      long serverPeer = ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
      if (serverPeer == 0) {
        active.Write("snapshot_replica_detached_ignored", "unscoped",
            "uid=" + snapshot.Uid +
            " sender=" + senderPeerId +
            " epoch=" + snapshot.Epoch +
            " sequence=" + snapshot.Sequence);
        return;
      }
      if (senderPeerId != serverPeer || mountZdo == null ||
          !IsSaddlePrefab(mountZdo.GetPrefab()) ||
          !mountZdo.GetBool(ZDOVars.s_tamed) ||
          !mountZdo.GetBool(ZDOVars.s_haveSaddleHash) ||
          !string.Equals(
              snapshot.RunId, CurrentRunId(), StringComparison.Ordinal))
        throw new InvalidOperationException(
            "saddle_snapshot_replica_authority_invalid");
      if (!active._clientAuthorities.TryGetValue(
              snapshot.Uid, out ClientAuthority clientAuthority)) {
        clientAuthority =
            new ClientAuthority(snapshot.OwnerPeerId, snapshot.Epoch);
        active._clientAuthorities[snapshot.Uid] = clientAuthority;
        active.Write("saddle_authority_adopted", "unscoped",
            "uid=" + snapshot.Uid +
            " owner=" + snapshot.OwnerPeerId +
            " epoch=" + snapshot.Epoch +
            " source=server_snapshot");
      }
      if (snapshot.Epoch < clientAuthority.Epoch) {
        active.Write("snapshot_stale_epoch_rejected", "unscoped",
            "uid=" + snapshot.Uid + " stale_epoch=" + snapshot.Epoch +
            " current_epoch=" + clientAuthority.Epoch +
            " stale_owner=" + snapshot.OwnerPeerId +
            " current_owner=" + clientAuthority.OwnerPeerId);
        return;
      }
      if (snapshot.Epoch == clientAuthority.Epoch) {
        if (snapshot.OwnerPeerId != clientAuthority.OwnerPeerId)
          throw new InvalidOperationException(
              "saddle_snapshot_replica_same_epoch_owner_invalid");
      } else if (snapshot.Epoch ==
          checked(clientAuthority.Epoch + 1)) {
        active._clientAuthorities[snapshot.Uid] =
            new ClientAuthority(snapshot.OwnerPeerId, snapshot.Epoch);
        active.Write("snapshot_authority_recovered", "unscoped",
            "uid=" + snapshot.Uid + " previous_epoch=" +
            clientAuthority.Epoch + " epoch=" + snapshot.Epoch +
            " previous_owner=" + clientAuthority.OwnerPeerId +
            " owner=" + snapshot.OwnerPeerId);
      } else {
        throw new InvalidOperationException(
            "saddle_snapshot_replica_epoch_gap");
      }
      if (!snapshot.RiderId.IsNone() &&
          ZDOMan.instance?.GetZDO(snapshot.RiderId) == null) {
        active.Write("snapshot_rider_replica_missing_deferred", "unscoped",
            "uid=" + snapshot.Uid +
            " epoch=" + snapshot.Epoch +
            " sequence=" + snapshot.Sequence +
            " rider=" + snapshot.RiderId);
        return;
      }
      string replicaKey = SnapshotKey(snapshot.Uid, snapshot.Epoch);
      active._replicaSnapshotSequences.TryGetValue(
          replicaKey, out uint previousReplica);
      if (snapshot.Sequence <= previousReplica)
        throw new InvalidOperationException(
            "saddle_snapshot_replica_sequence_stale");
      active._replicaSnapshotSequences[replicaKey] = snapshot.Sequence;
      Player local = Player.m_localPlayer;
      long localSession = local?.GetZDOID().UserID ?? 0L;
      GameObject instance = ZNetScene.instance.FindInstance(snapshot.Uid);
      ZNetView view = instance?.GetComponent<ZNetView>();
      if (view != null && view.IsOwner() &&
          snapshot.OwnerPeerId == localSession) {
        // The server fans accepted owner snapshots back to Everybody. The
        // current owner is the source of those bytes, so applying a delayed
        // echo can only roll newer native state backward. In particular, an
        // in-flight mounted snapshot used to restore s_user immediately after
        // Sadle.RPC_ReleaseControl had cleared it, leaving a detached rider
        // edge paired with a still-occupied mount. Count the canonical echo
        // for sequence proof, but never mutate the source owner from it.
        if (snapshot.Sequence == 1 || snapshot.Sequence % 25 == 0)
          active.Write("snapshot_owner_echo_ignored", "unscoped",
              "uid=" + snapshot.Uid + " epoch=" + snapshot.Epoch +
              " sequence=" + snapshot.Sequence +
              " owner=" + snapshot.OwnerPeerId +
              " user=" + snapshot.User);
        return;
      }
      Sadle localSaddle = FindSaddle(
          instance?.GetComponent<Character>());
      if (localSession != 0 && localSaddle != null &&
          ReferenceEquals(local.GetDoodadController(), localSaddle) &&
          (snapshot.OwnerPeerId != localSession ||
           snapshot.User != localSession))
        local.StopDoodadControl();
      ApplyMountFields(mountZdo, snapshot);
      active.ApplyReplicaRiderEdge(snapshot);

      if (view != null && !view.IsOwner()) {
        Rigidbody body = instance.GetComponent<Rigidbody>();
        instance.transform.position = snapshot.Position;
        instance.transform.rotation = snapshot.Rotation;
        if (body != null) {
          body.position = snapshot.Position;
          body.rotation = snapshot.Rotation;
          body.linearVelocity = snapshot.Velocity;
          body.angularVelocity = snapshot.AngularVelocity;
        }
      }
      if (snapshot.Sequence == 1 || snapshot.Sequence % 25 == 0)
        active.Write("snapshot_replica_applied", "unscoped",
            "uid=" + snapshot.Uid + " epoch=" + snapshot.Epoch +
            " sequence=" + snapshot.Sequence +
            " owner=" + snapshot.OwnerPeerId +
            " user=" + snapshot.User);
    } catch (Exception exception) {
      active.Write("snapshot_rejected", "unscoped",
          "sender=" + senderPeerId + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      throw;
    }
  }

  bool FanOutSnapshot(Snapshot snapshot, string actionId) {
    ZNetPeer[] peers = ZNet.instance?.GetPeers()?.Where(peer =>
        peer != null && peer.m_uid != 0).ToArray() ?? Array.Empty<ZNetPeer>();
    var candidates = peers.Select(peer =>
        new VehicleSnapshotRelevanceCandidate(
            peer.m_uid,
            Vector3.Distance(snapshot.Position, peer.m_refPos))).ToArray();
    float outer = Mathf.Max(
        1.0f, PluginConfig.ZdoOuterRadiusMeters?.Value ?? 64.0f);
    float hysteresis = Mathf.Max(4.0f, outer * 0.125f);
    IReadOnlyList<VehicleSnapshotRelevanceDecision> decisions =
        _snapshotRelevance.Reconcile(
            "saddle:" + snapshot.Uid, candidates, outer, hysteresis);
    int delivered = 0;
    foreach (VehicleSnapshotRelevanceDecision decision in decisions) {
      if (decision.Transition is VehicleSnapshotRelevanceTransition.Entered
          or VehicleSnapshotRelevanceTransition.Left)
        Write(
            decision.Transition == VehicleSnapshotRelevanceTransition.Entered
                ? "snapshot_relevance_entered"
                : "snapshot_relevance_left",
            "unscoped",
             "uid=" + snapshot.Uid +
             " epoch=" + snapshot.Epoch +
             " peer=" + decision.PeerId +
            " distance=" + decision.DistanceMeters.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " enter_radius=" + outer.ToString(
                "0.###", CultureInfo.InvariantCulture) +
            " leave_radius=" + (outer + hysteresis).ToString(
                "0.###", CultureInfo.InvariantCulture));
      if (!decision.Deliver) continue;

      ZPackage replica = BuildSnapshotPackage(snapshot);
      if (!_routedRpc.InvokeTyped(
              actionId,
              () => ZRoutedRpc.instance.InvokeRoutedRPC(
                  decision.PeerId,
                  ValheimRoutedRpcAdmissions.ModSaddleSnapshot,
                  new object[] { replica })))
        return false;
      delivered++;
    }
    if (snapshot.Sequence == 1 || snapshot.Sequence % 25 == 0)
      Write("snapshot_relevance_fanout", "unscoped",
          "uid=" + snapshot.Uid +
          " epoch=" + snapshot.Epoch +
          " sequence=" + snapshot.Sequence +
          " candidates=" + decisions.Count +
          " recipients=" + delivered +
          " target=direct_per_observer");
    return true;
  }

  void ApplyCanonicalSnapshot(ZDO mountZdo, Snapshot snapshot) {
    ZDOID previousRider = _serverRidersByMount.TryGetValue(
        snapshot.Uid, out ZDOID known) ? known : ZDOID.None;
    ZDO rider = snapshot.RiderId.IsNone()
        ? null : ZDOMan.instance?.GetZDO(snapshot.RiderId);
    if (rider != null && (snapshot.RiderId.UserID != snapshot.User ||
        snapshot.OwnerPeerId != snapshot.User ||
        rider.GetOwner() != snapshot.User))
      throw new InvalidOperationException("saddle_snapshot_rider_invalid");

    ZdoJournalCutoverRunner.ApplyCanonicalMutation(
        mountZdo, () => ApplyMountFields(mountZdo, snapshot));
    if (!previousRider.IsNone() && previousRider != snapshot.RiderId)
      ClearCanonicalRiderEdge(previousRider, snapshot.Uid);
    if (!snapshot.RiderId.IsNone()) {
      // Logical peers deliberately have no dedicated-server Player replica.
      // The authenticated peer binding above is the canonical rider identity;
      // update a native rider ZDO when one exists, but do not invent a server
      // presentation object solely to hold this cross-object edge.
      if (rider != null)
        ZdoJournalCutoverRunner.ApplyCanonicalMutation(rider, () =>
            ApplyRiderEdge(rider, snapshot));
      _serverRidersByMount[snapshot.Uid] = snapshot.RiderId;
    } else {
      _serverRidersByMount.Remove(snapshot.Uid);
    }
  }

  void ApplyReplicaRiderEdge(Snapshot snapshot) {
    ZDOID previous = _replicaRidersByMount.TryGetValue(
        snapshot.Uid, out ZDOID known) ? known : ZDOID.None;
    if (!previous.IsNone() && previous != snapshot.RiderId) {
      ZDO previousZdo = ZDOMan.instance?.GetZDO(previous);
      if (previousZdo != null)
        ClearRiderEdge(previousZdo, snapshot.Uid);
    }
    if (!snapshot.RiderId.IsNone()) {
      ZDO rider = ZDOMan.instance?.GetZDO(snapshot.RiderId);
      if (rider == null)
        throw new InvalidOperationException("saddle_replica_rider_missing");
      ApplyRiderEdge(rider, snapshot);
      _replicaRidersByMount[snapshot.Uid] = snapshot.RiderId;
    } else {
      _replicaRidersByMount.Remove(snapshot.Uid);
    }
  }

  void ClearCanonicalRiderEdge(ZDOID riderId, ZDOID mountId) {
    ZDO rider = ZDOMan.instance?.GetZDO(riderId);
    if (rider == null) return;
    ZdoJournalCutoverRunner.ApplyCanonicalMutation(
        rider, () => ClearRiderEdge(rider, mountId));
  }

  int ClearReplicaRiderEdges(ZDOID mountId) {
    int cleared = 0;
    if (_replicaRidersByMount.TryGetValue(
            mountId, out ZDOID knownRider)) {
      ZDO knownZdo = ZDOMan.instance?.GetZDO(knownRider);
      if (knownZdo != null && knownZdo.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.SyncTransform) == mountId) {
        ClearRiderEdge(knownZdo, mountId);
        cleared++;
      }
    }
    foreach (Player player in Player.GetAllPlayers()) {
      ZDO rider = player?.GetComponent<ZNetView>()?.GetZDO();
      if (rider != null && rider.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.SyncTransform) == mountId) {
        ClearRiderEdge(rider, mountId);
        cleared++;
      }
    }
    _replicaRidersByMount.Remove(mountId);
    return cleared;
  }

  /// <summary>
  /// Native release clears s_user immediately, but a delayed durable player
  /// snapshot can restore the old SyncTransform parent afterward. An empty
  /// canonical rider token makes that edge stale by definition. Repair it on
  /// every owner snapshot and immediately before autonomous-AI proof so the
  /// player replica cannot remain parented to an unridden mount.
  /// </summary>
  internal int RepairReleasedRiderEdges(ZDOID mountId) {
    ZDO mount = ZDOMan.instance?.GetZDO(mountId);
    if (_disposed || mount == null ||
        mount.GetLong(ZDOVars.s_user, 0L) != 0L) return 0;
    int cleared = ClearReplicaRiderEdges(mountId);
    if (cleared > 0 && _releasedRiderEdgeRepairLogged.Add(mountId))
      Write("released_rider_edge_repaired", "unscoped",
          "uid=" + mountId + " owner=" + mount.GetOwner() +
          " cleared=" + cleared);
    return cleared;
  }

  static void ApplyRiderEdge(ZDO rider, Snapshot snapshot) {
    ApplyRiderEdge(
        rider, snapshot.Uid, snapshot.AttachJoint,
        snapshot.RelativePosition, snapshot.RelativeRotation,
        snapshot.RelativeVelocity);
  }

  static void ApplyRiderEdge(
      ZDO rider,
      ZDOID parent,
      string attachJoint,
      Vector3 relativePosition,
      Quaternion relativeRotation,
      Vector3 relativeVelocity) {
    rider.SetConnection(
        ZDOExtraData.ConnectionType.SyncTransform, parent);
    rider.Set(ZDOVars.s_attachJointHash, attachJoint);
    rider.Set(ZDOVars.s_relPosHash, relativePosition);
    rider.Set(ZDOVars.s_relRotHash, relativeRotation);
    rider.Set(ZDOVars.s_velHash, relativeVelocity);
  }

  static void ClearRiderEdge(ZDO rider, ZDOID mountId) {
    if (rider.GetConnectionZDOID(
            ZDOExtraData.ConnectionType.SyncTransform) != mountId) return;
    rider.UpdateConnection(
        ZDOExtraData.ConnectionType.SyncTransform, ZDOID.None);
    rider.Set(ZDOVars.s_attachJointHash, string.Empty);
    rider.Set(ZDOVars.s_relPosHash, Vector3.zero);
    rider.Set(ZDOVars.s_relRotHash, Quaternion.identity);
  }

  static void HandleSpawnRequest(long senderPeerId, ZPackage package) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    string runId = string.Empty;
    string actionId = string.Empty;
    try {
      package.SetPos(0);
      runId = package.ReadString();
      actionId = package.ReadString();
      bool untagged = package.GetPos() < package.Size() && package.ReadBool();
      bool allowCreate =
          package.GetPos() >= package.Size() || package.ReadBool();
      if (package.GetPos() != package.Size() || !SafeToken(runId, 80) ||
          !SafeToken(actionId, 80) ||
          !string.Equals(runId, CurrentRunId(), StringComparison.Ordinal))
        throw new InvalidOperationException("saddle_spawn_request_invalid");
      if (!TryResolveAuthenticatedPeer(
              senderPeerId, out ZDOID senderCharacterId,
              out Vector3 senderPosition))
        throw new InvalidOperationException(
            "saddle_spawn_sender_reference_missing");
      active._serverCharactersByRunPeer[
          ServerCharacterKey(runId, senderPeerId)] = senderCharacterId;

      ZDO zdo = null;
      if (active._serverMountsByRun.TryGetValue(runId, out ZDOID existing))
        zdo = ZDOMan.instance?.GetZDO(existing);
      if (zdo == null) {
        if (!allowCreate) return;
        if (!TrySelectSpawnPosition(senderPosition, out Vector3 position))
          throw new InvalidOperationException("saddle_spawn_site_invalid");
        GameObject prefab = ZNetScene.instance?.GetPrefab("Lox");
        ZNetView prefabView = prefab?.GetComponent<ZNetView>();
        Character character = prefab?.GetComponent<Character>();
        MonsterAI ai = prefab?.GetComponent<MonsterAI>();
        Tameable tameable = prefab?.GetComponent<Tameable>();
        if (prefabView == null || character == null || ai == null ||
            tameable == null || tameable.m_saddle == null)
          throw new InvalidOperationException("lox_mount_prefab_invalid");
        int prefabHash = prefab.name.GetStableHashCode();
        float maxHealth = character.GetMaxHealthBase();
        long spawnTicks = ZNet.instance.GetTime().Ticks;
        zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
        ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () => {
          zdo.SetPrefab(prefabHash);
          zdo.Persistent = prefabView.m_persistent;
          zdo.SetType(prefabView.m_type);
          zdo.SetDistant(prefabView.m_distant);
          zdo.SetRotation(Quaternion.identity);
          zdo.Set(ZDOVars.s_tamed, true);
          zdo.Set(ZDOVars.s_haveSaddleHash, true);
          zdo.Set(ZDOVars.s_user, 0L);
          zdo.Set(ZDOVars.s_level, 1);
          zdo.Set(ZDOVars.s_maxHealth, maxHealth);
          zdo.Set(ZDOVars.s_health, maxHealth);
          zdo.Set(ZDOVars.s_spawnTime, spawnTicks);
          zdo.Set(ZDOVars.s_spawnPoint, position);
          if (tameable.m_randomStartingName.Count > 0) {
            zdo.Set(ZDOVars.s_tamedName,
                tameable.m_randomStartingName[0]);
            zdo.Set(ZDOVars.s_tamedNameAuthor, "host");
          }
          if (!untagged) zdo.Set(RunTagHash, runId);
          zdo.Set(ActionTagHash, actionId);
          zdo.SetOwner(senderPeerId);
        });
        active._serverMountsByRun[runId] = zdo.m_uid;
        if (untagged)
          CutoverResidueSweeper.RegisterUntagged(runId, zdo.m_uid);
        else
          active._serverAuthorities[zdo.m_uid] =
              new ServerAuthority(senderPeerId, 0, 1);
        active.Write("saddle_spawned", actionId,
            "uid=" + zdo.m_uid + " owner=" + senderPeerId +
            " prefab=Lox tamed=true saddle=true" +
            " sender_character=" + senderCharacterId +
            " position=" + Format(position) +
            " run_tag=" + (untagged ? "absent" : "present") +
            " persistent=" + prefabView.m_persistent +
            " type=" + prefabView.m_type +
            " distant=" + prefabView.m_distant);
      }
      ServerAuthority authority =
          active._serverAuthorities.TryGetValue(
              zdo.m_uid, out ServerAuthority knownAuthority)
          ? knownAuthority
          : new ServerAuthority(zdo.GetOwner(), 0, 1);
      bool authorityPreseeded = !string.IsNullOrEmpty(
          zdo.GetString(RunTagHash, string.Empty));
      active.SendSpawnResponse(
          ZRoutedRpc.Everybody,
          runId, actionId, zdo.m_uid, zdo.GetPosition(),
          authority.OwnerPeerId, authority.Epoch, true, "spawned",
          authorityPreseeded: authorityPreseeded);
    } catch (Exception exception) {
      active.Write("saddle_spawn_rejected", actionId,
          "sender=" + senderPeerId + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      if (SafeToken(runId, 80) && SafeToken(actionId, 80))
        active.SendSpawnResponse(
            senderPeerId,
            runId, actionId, ZDOID.None, Vector3.zero,
            0, 0, false, exception.Message,
            authorityPreseeded: false);
    }
  }

  void SendSpawnResponse(
      long targetPeerId,
      string runId, string actionId, ZDOID uid, Vector3 position,
      long owner, uint epoch, bool accepted, string result,
      bool authorityPreseeded) {
    ZPackage response = new();
    response.Write(runId);
    response.Write(actionId);
    response.Write(uid);
    response.Write(position);
    response.Write(owner);
    response.Write(epoch);
    response.Write(accepted);
    response.Write(result ?? string.Empty);
    response.Write(authorityPreseeded);
    response.SetPos(0);
    _routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            targetPeerId,
            ValheimRoutedRpcAdmissions.CutoverSaddleSpawnResponse,
            new object[] { response }));
  }

  static void HandleSpawnResponse(long senderPeerId, ZPackage package) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    package.SetPos(0);
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    Vector3 position = package.ReadVector3();
    long owner = package.ReadLong();
    uint epoch = package.ReadUInt();
    bool accepted = package.ReadBool();
    string result = package.ReadString();
    bool authorityPreseeded =
        package.GetPos() < package.Size() ? package.ReadBool() : true;
    MountProbe probe = active._probe;
    bool matchingProbe = probe != null && !probe.Terminal &&
        string.Equals(probe.RunId, runId, StringComparison.Ordinal) &&
        string.Equals(probe.ActionId, actionId, StringComparison.Ordinal) &&
        probe.Mode is "spawn" or "spawn_untagged" or "wait_mount";
    if (package.GetPos() != package.Size() ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        !SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
        !accepted || uid.IsNone() || owner == 0 || epoch == 0 ||
        !Finite(position)) {
      active.Write("saddle_spawn_response_rejected", actionId,
          "result=" + result + " matching_probe=" + matchingProbe);
      if (matchingProbe)
        active.FailMatchingProbe(
            "saddle_spawn_response_invalid result=" + result);
      return;
    }
    active._announcements[runId] =
        new MountAnnouncement(uid, position, owner, epoch);
    if (authorityPreseeded) {
      active._clientAuthorities[uid] = new ClientAuthority(owner, epoch);
    } else {
      active._clientAuthorities.Remove(uid);
      active.Write("saddle_untagged_announced", actionId,
          "uid=" + uid + " owner=" + owner +
          " epoch=" + epoch + " authority_preseeded=false");
    }
    if (probe != null && !probe.Terminal &&
        (probe.Mode is "spawn" or "spawn_untagged") &&
        string.Equals(probe.RunId, runId, StringComparison.Ordinal) &&
        string.Equals(probe.ActionId, actionId, StringComparison.Ordinal))
      active.CompleteProbe("saddle_spawn_accepted uid=" + uid +
          " owner=" + owner + " epoch=" + epoch +
          " authority_preseeded=" + authorityPreseeded +
          " position=" + Format(position));
  }

  bool SendTransfer(
      string actionId, string runId, ZDOID uid, long newOwner) {
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0 || uid.IsNone() || newOwner == 0)
      return false;
    ZPackage request = new();
    request.Write(runId);
    request.Write(actionId);
    request.Write(uid);
    request.Write(newOwner);
    request.SetPos(0);
    return _routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid,
            ValheimRoutedRpcAdmissions.CutoverSaddleTransferRequest,
            new object[] { request }));
  }

  void CommitServerVanillaGrant(
      ZDO zdo, string actionId, long previousOwner, long newOwner) {
    try {
      if (zdo != null && !_serverAuthorities.ContainsKey(zdo.m_uid) &&
          previousOwner != 0) {
        _serverAuthorities[zdo.m_uid] =
            new ServerAuthority(previousOwner, 0, 1);
        Write("saddle_authority_adopted", "unscoped",
            "uid=" + zdo.m_uid +
            " owner=" + previousOwner +
            " epoch=1 source=vanilla_grant_previous_owner" +
            " run_tag=" +
            (string.IsNullOrEmpty(zdo.GetString(RunTagHash, string.Empty))
                ? "absent" : "present"));
      }
      if (zdo == null || !_serverAuthorities.TryGetValue(
              zdo.m_uid, out ServerAuthority authority) ||
          authority.OwnerPeerId != previousOwner ||
          zdo.GetOwner() != newOwner ||
          zdo.GetLong(ZDOVars.s_user, 0L) != newOwner ||
          !LivePeer(newOwner, 0))
        throw new InvalidOperationException(
            "saddle_server_grant_authority_invalid");
      uint nextEpoch = checked(authority.Epoch + 1);
      ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () => {
        zdo.Set(ZDOVars.s_user, newOwner);
        zdo.SetOwner(newOwner);
      });
      _serverAuthorities[zdo.m_uid] =
          new ServerAuthority(newOwner, previousOwner, nextEpoch);
      string runId = CurrentRunId();
      SendTransferResponse(
          runId, actionId, zdo.m_uid, newOwner, newOwner,
          nextEpoch, true, "server_owner_transferred");
      SendTransferResponse(
          runId, "saddle-stale-transfer-epoch", zdo.m_uid,
          previousOwner, 0, authority.Epoch, true,
          "intentional_stale_epoch_probe");
      Write("stale_transfer_probe_sent",
          "saddle-stale-transfer-epoch",
          "uid=" + zdo.m_uid + " stale_owner=" + previousOwner +
          " stale_epoch=" + authority.Epoch);
      SendStaleAuthoritySnapshot(
          runId, zdo, previousOwner, authority.Epoch);
      Write("saddle_owner_transferred", actionId,
          "uid=" + zdo.m_uid + " old_owner=" + previousOwner +
          " new_owner=" + newOwner + " epoch=" + nextEpoch +
          " source=server_vanilla_grant");
    } catch (Exception exception) {
      Write("saddle_transfer_rejected", actionId,
          "sender=" + previousOwner + " uid=" + zdo?.m_uid +
          " reason=" + exception.GetType().Name + ":" +
          exception.Message);
    }
  }

  static void HandleTransferRequest(long senderPeerId, ZPackage package) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    string runId = string.Empty;
    string actionId = string.Empty;
    ZDOID uid = ZDOID.None;
    long newOwner = 0;
    try {
      package.SetPos(0);
      runId = package.ReadString();
      actionId = package.ReadString();
      uid = package.ReadZDOID();
      newOwner = package.ReadLong();
      ZDO zdo = ZDOMan.instance?.GetZDO(uid);
      if (package.GetPos() != package.Size() || !SafeToken(runId, 80) ||
          !SafeToken(actionId, 80) || uid.IsNone() || newOwner == 0 ||
          zdo == null || !string.Equals(
              runId, CurrentRunId(), StringComparison.Ordinal) ||
          !active._serverAuthorities.TryGetValue(
              uid, out ServerAuthority authority) ||
          authority.OwnerPeerId != senderPeerId ||
          zdo.GetOwner() != senderPeerId || !IsSaddlePrefab(zdo.GetPrefab()) ||
          !zdo.GetBool(ZDOVars.s_tamed) ||
          !zdo.GetBool(ZDOVars.s_haveSaddleHash) ||
          (newOwner != ZNet.GetUID() &&
           !(ZNet.instance?.GetPeers()?.Any(
               peer => peer.m_uid == newOwner) ?? false)))
        throw new InvalidOperationException(
            "saddle_transfer_authority_invalid");
      uint nextEpoch = checked(authority.Epoch + 1);
      long nextUser = SaddleAuthorityTransferPolicy.CanonicalUser(
          newOwner, ZNet.GetUID());
      ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () => {
        zdo.Set(ZDOVars.s_user, nextUser);
        zdo.SetOwner(newOwner);
      });
      active._serverAuthorities[uid] =
          new ServerAuthority(newOwner, senderPeerId, nextEpoch);
      active.SendTransferResponse(
          runId, actionId, uid, newOwner, nextUser,
          nextEpoch, true, "transferred");
      active.SendTransferResponse(
          runId, "saddle-stale-transfer-epoch", uid,
          senderPeerId, 0, authority.Epoch, true,
          "intentional_stale_epoch_probe");
      active.Write("stale_transfer_probe_sent",
          "saddle-stale-transfer-epoch",
          "uid=" + uid + " stale_owner=" + senderPeerId +
          " stale_epoch=" + authority.Epoch);
      active.SendStaleAuthoritySnapshot(
          runId, zdo, senderPeerId, authority.Epoch);
      active.Write("saddle_owner_transferred", actionId,
          "uid=" + uid + " old_owner=" + senderPeerId +
          " new_owner=" + newOwner + " user=" + nextUser +
          " epoch=" + nextEpoch);
    } catch (Exception exception) {
      active.Write("saddle_transfer_rejected", actionId,
          "sender=" + senderPeerId + " uid=" + uid +
          " reason=" + exception.GetType().Name + ":" + exception.Message);
      if (SafeToken(runId, 80) && SafeToken(actionId, 80))
        active.SendTransferResponse(
            runId, actionId, uid, newOwner, 0, 0,
            false, exception.Message);
    }
  }

  void SendTransferResponse(
      string runId, string actionId, ZDOID uid, long owner, long user,
      uint epoch, bool accepted, string result) {
    ZPackage response = new();
    response.Write(runId);
    response.Write(actionId);
    response.Write(uid);
    response.Write(owner);
    response.Write(user);
    response.Write(epoch);
    response.Write(accepted);
    response.Write(result ?? string.Empty);
    response.SetPos(0);
    _routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.Everybody,
            ValheimRoutedRpcAdmissions.CutoverSaddleTransferResponse,
            new object[] { response }));
  }

  static void HandleTransferResponse(long senderPeerId, ZPackage package) {
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    package.SetPos(0);
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    long owner = package.ReadLong();
    long user = package.ReadLong();
    uint epoch = package.ReadUInt();
    bool accepted = package.ReadBool();
    string result = package.ReadString();
    MountProbe probe = active._probe;
    bool matchingProbe = probe != null && !probe.Terminal &&
        string.Equals(probe.RunId, runId, StringComparison.Ordinal) &&
        string.Equals(probe.ActionId, actionId, StringComparison.Ordinal);
    if (package.GetPos() != package.Size() ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        !SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
        uid.IsNone() || !accepted || owner == 0 || epoch == 0) {
      active.Write("saddle_transfer_response_rejected", actionId,
          "result=" + result + " matching_probe=" + matchingProbe);
      if (matchingProbe)
        active.FailMatchingProbe(
            "saddle_transfer_response_invalid result=" + result);
      return;
    }
    ZDO zdo = ZDOMan.instance?.GetZDO(uid);
    if (zdo == null || !IsSaddlePrefab(zdo.GetPrefab()) ||
        !zdo.GetBool(ZDOVars.s_tamed) ||
        !zdo.GetBool(ZDOVars.s_haveSaddleHash) ||
        !string.Equals(runId, CurrentRunId(), StringComparison.Ordinal)) {
      active.FailMatchingProbe("saddle_transfer_replica_missing");
      return;
    }
    if (active._clientAuthorities.TryGetValue(
            uid, out ClientAuthority currentAuthority)) {
      if (epoch < currentAuthority.Epoch) {
        active.Write("transfer_stale_epoch_rejected", actionId,
            "uid=" + uid + " stale_epoch=" + epoch +
            " current_epoch=" + currentAuthority.Epoch +
            " stale_owner=" + owner +
            " current_owner=" + currentAuthority.OwnerPeerId);
        return;
      }
      if (epoch == currentAuthority.Epoch) {
        if (owner != currentAuthority.OwnerPeerId)
          active.FailMatchingProbe(
              "saddle_transfer_same_epoch_owner_mismatch");
        return;
      }
      if (epoch != checked(currentAuthority.Epoch + 1)) {
        active.FailMatchingProbe("saddle_transfer_epoch_gap current=" +
            currentAuthority.Epoch + " received=" + epoch);
        return;
      }
    }
    Player local = Player.m_localPlayer;
    Sadle saddle = FindSaddle(
        ZNetScene.instance.FindInstance(uid)?.GetComponent<Character>());
    if (user == 0 && local != null && saddle != null &&
        ReferenceEquals(local.GetDoodadController(), saddle))
      local.StopDoodadControl();
    if (user == 0)
      active.ClearReplicaRiderEdges(uid);
    long previousOwner = zdo.GetOwner();
    zdo.Set(ZDOVars.s_user, user);
    zdo.SetOwner(owner);
    active._clientAuthorities[uid] = new ClientAuthority(owner, epoch);
    active._clientSnapshotSequences[uid] = 0;
    active.Write(
        user == 0 ? "saddle_reclaim_applied" : "saddle_owner_applied",
        actionId,
        "uid=" + uid + " previous_owner=" + previousOwner +
        " owner=" + owner + " user=" + user + " epoch=" + epoch);
  }

  void ReclaimDetachedPeer(long peerId) {
    foreach (KeyValuePair<ZDOID, ServerAuthority> pair in
             _serverAuthorities.ToArray()) {
      ZDO zdo = ZDOMan.instance?.GetZDO(pair.Key);
      if (zdo == null) continue;
      long user = zdo.GetLong(ZDOVars.s_user, 0L);
      if (pair.Value.OwnerPeerId != peerId && user != peerId) continue;
      long fallback = LivePeer(pair.Value.PreviousOwnerPeerId, peerId)
          ? pair.Value.PreviousOwnerPeerId
          : ZNet.GetUID();
      uint nextEpoch = checked(pair.Value.Epoch + 1);
      ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () => {
        zdo.Set(ZDOVars.s_user, 0L);
        zdo.SetOwner(fallback);
      });
      if (_serverRidersByMount.TryGetValue(
              pair.Key, out ZDOID riderId)) {
        ClearCanonicalRiderEdge(riderId, pair.Key);
        _serverRidersByMount.Remove(pair.Key);
      }
      _serverAuthorities[pair.Key] =
          new ServerAuthority(fallback, 0, nextEpoch);
      string runId = CurrentRunId();
      SendTransferResponse(
          runId, "saddle-disconnect-reclaim", pair.Key,
          fallback, 0, nextEpoch, true, "peer_detached_reclaimed");
      Write("saddle_disconnect_reclaimed", "saddle-disconnect-reclaim",
          "uid=" + pair.Key + " departed=" + peerId +
          " fallback_owner=" + fallback + " epoch=" + nextEpoch);
    }
  }

  void SendStaleAuthoritySnapshot(
      string runId, ZDO zdo, long staleOwner, uint staleEpoch) {
    bool riderResolved = _serverCharactersByRunPeer.TryGetValue(
        ServerCharacterKey(runId, staleOwner), out ZDOID staleRiderId);
    if (!riderResolved) {
      riderResolved = TryResolveAuthenticatedPeer(
          staleOwner, out staleRiderId, out _);
      if (riderResolved)
        _serverCharactersByRunPeer[
            ServerCharacterKey(runId, staleOwner)] = staleRiderId;
    }
    GameObject staleMount = ZNetScene.instance?.FindInstance(zdo.m_uid);
    Character staleCharacter = staleMount?.GetComponent<Character>();
    if (staleCharacter == null)
      staleCharacter = ZNetScene.instance?
          .GetPrefab(zdo.GetPrefab())?
          .GetComponent<Character>();
    Sadle saddle = FindSaddle(staleCharacter);
    string attachJoint = riderResolved && saddle?.m_attachPoint != null
        ? saddle.m_attachPoint.name : string.Empty;
    long staleUser = string.IsNullOrEmpty(attachJoint) ? 0L : staleOwner;
    if (staleUser == 0) staleRiderId = ZDOID.None;
    Snapshot stale = new() {
        RunId = runId,
        Uid = zdo.m_uid,
        OwnerPeerId = staleOwner,
        Epoch = staleEpoch,
        Sequence = uint.MaxValue - 1,
        Position = zdo.GetPosition(),
        Rotation = zdo.GetRotation(),
        Velocity = Vector3.zero,
        AngularVelocity = Vector3.zero,
        User = staleUser,
        Stamina = zdo.GetFloat(ZDOVars.s_stamina, 0.0f),
        RiderId = staleRiderId,
        ParentSync = staleUser != 0,
        AttachJoint = attachJoint,
        RelativePosition = Vector3.zero,
        RelativeRotation = Quaternion.identity,
        RelativeVelocity = Vector3.zero
    };
    bool queued = FanOutSnapshot(
        stale, "saddle-stale-authority-epoch");
    Write(
        queued ? "stale_epoch_probe_sent" : "stale_epoch_probe_failed",
        "saddle-stale-authority-epoch",
        "uid=" + zdo.m_uid + " stale_owner=" + staleOwner +
        " stale_epoch=" + staleEpoch +
        " stale_rider=" + staleRiderId +
        " attach_joint=" + attachJoint);
  }

  static bool LivePeer(long peerId, long excluded) =>
      peerId != 0 && peerId != excluded &&
      (ZNet.instance?.GetPeers()?.Any(peer => peer.m_uid == peerId) ?? false);

  static bool TryResolveAuthenticatedPeer(
      long peerId, out ZDOID characterId, out Vector3 position) {
    characterId = ZDOID.None;
    position = Vector3.zero;
    if (LogicalPeerCutoverRunner.TryGetCanonicalPeerReference(
            peerId, out characterId, out position)) return true;

    ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
    if (peer == null || peer.m_characterID.IsNone() ||
        peer.m_characterID.UserID != peerId) return false;
    ZDO character = ZDOMan.instance?.GetZDO(peer.m_characterID);
    if (character == null || !Finite(character.GetPosition())) return false;
    characterId = peer.m_characterID;
    position = character.GetPosition();
    return true;
  }

  static bool TrySelectSpawnPosition(Vector3 origin, out Vector3 position) {
    position = Vector3.zero;
    WorldGenerator generator = WorldGenerator.instance;
    if (generator == null || !Finite(origin)) return false;
    for (int ring = 1; ring <= 8; ring++) {
      for (int x = -ring; x <= ring; x++) {
        for (int z = -ring; z <= ring; z++) {
          if (Math.Abs(x) != ring && Math.Abs(z) != ring) continue;
          float px = origin.x + x * 4.0f;
          float pz = origin.z + z * 4.0f;
          Heightmap.Biome biome = generator.GetBiome(
              px, pz, 0.02f, waterAlwaysOcean: true);
          if (biome == Heightmap.Biome.Ocean) continue;
          float height = generator.GetHeight(px, pz);
          if (!Finite(height) || height <= 30.5f) continue;
          float maxDelta = 0;
          foreach (Vector3 offset in new[] {
                     new Vector3(2, 0, 0), new Vector3(-2, 0, 0),
                     new Vector3(0, 0, 2), new Vector3(0, 0, -2) })
            maxDelta = Mathf.Max(maxDelta, Mathf.Abs(
                generator.GetHeight(px + offset.x, pz + offset.z) - height));
          if (maxDelta > 1.5f) continue;
          position = new Vector3(px, height + 1.0f, pz);
          return true;
        }
      }
    }
    return false;
  }

  static bool ValidateRuntimeShape(
      Character mount, ZNetView view, Sadle saddle, out string detail) {
    detail = string.Empty;
    Tameable tameable = mount?.GetComponent<Tameable>();
    MonsterAI ai = mount?.GetComponent<MonsterAI>();
    ZDO zdo = view?.GetZDO();
    ZSyncTransform playerSync =
        Player.m_localPlayer?.GetComponent<ZSyncTransform>();
    if (mount == null || view == null || zdo == null || saddle == null ||
        tameable == null || ai == null || tameable.m_saddle != saddle ||
        saddle.m_attachPoint == null || !saddle.gameObject.activeInHierarchy ||
        !mount.IsTamed() || !zdo.GetBool(ZDOVars.s_haveSaddleHash) ||
        playerSync == null || !playerSync.m_characterParentSync) {
      detail = "components_or_parent_sync_missing";
      return false;
    }
    detail = "prefab=Lox tamed=true saddle=true parent_sync=true" +
        " persistent=" + zdo.Persistent + " type=" + zdo.Type +
        " distant=" + zdo.Distant +
        " attach_joint=" + saddle.m_attachPoint.name;
    return true;
  }

  static bool TryReadLocalRiderEdge(
      Player player, ZDOID mountId, Sadle saddle, out string detail) {
    detail = string.Empty;
    ZSyncTransform sync = player?.GetComponent<ZSyncTransform>();
    ZDO zdo = player?.GetComponent<ZNetView>()?.GetZDO();
    if (sync == null || !sync.m_characterParentSync || zdo == null) {
      detail = "parent_sync_disabled";
      return false;
    }
    ZDOID parent = zdo.GetConnectionZDOID(
        ZDOExtraData.ConnectionType.SyncTransform);
    string joint = zdo.GetString(ZDOVars.s_attachJointHash, string.Empty);
    Vector3 rel = zdo.GetVec3(ZDOVars.s_relPosHash, Vector3.zero);
    Quaternion rot = zdo.GetQuaternion(
        ZDOVars.s_relRotHash, Quaternion.identity);
    if (parent != mountId || !string.Equals(
            joint, saddle.m_attachPoint.name, StringComparison.Ordinal) ||
        rel.magnitude > 0.01f || Quaternion.Angle(rot, Quaternion.identity) > 0.1f) {
      detail = "parent_or_relative_pose_pending";
      return false;
    }
    detail = "parent=" + parent + " joint=" + joint +
        " rel=" + Format(rel);
    return true;
  }

  static bool TryFindRunMount(
      string runId,
      out Character mount,
      out ZNetView view,
      out Sadle saddle) {
    mount = null;
    view = null;
    saddle = null;
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    if (active != null && active._announcements.TryGetValue(
            runId, out MountAnnouncement announcement)) {
      GameObject announcedObject =
          ZNetScene.instance?.FindInstance(announcement.Uid);
      Character announcedMount = announcedObject?.GetComponent<Character>();
      ZNetView announcedView = announcedObject?.GetComponent<ZNetView>();
      Sadle announcedSaddle = FindSaddle(announcedMount);
      if (announcedMount != null && announcedView?.GetZDO() != null &&
          announcedSaddle != null) {
        mount = announcedMount;
        view = announcedView;
        saddle = announcedSaddle;
        return true;
      }
    }
    foreach (Character candidate in FindRunMounts(runId)) {
      ZNetView candidateView = candidate.GetComponent<ZNetView>();
      Sadle candidateSaddle = FindSaddle(candidate);
      if (candidateView?.GetZDO() == null || candidateSaddle == null) continue;
      mount = candidate;
      view = candidateView;
      saddle = candidateSaddle;
      return true;
    }
    return false;
  }

  static IEnumerable<Character> FindRunMounts(string runId) {
    foreach (Character candidate in FindMounts()) {
      ZDO zdo = candidate.GetComponent<ZNetView>()?.GetZDO();
      if (zdo != null && string.Equals(
              zdo.GetString(RunTagHash, string.Empty),
              runId, StringComparison.Ordinal))
        yield return candidate;
    }
  }

  static IEnumerable<Character> FindMounts() {
    foreach (Character candidate in Character.GetAllCharacters().ToArray()) {
      if (candidate == null || candidate.IsPlayer() ||
          !candidate.gameObject.activeInHierarchy || !candidate.IsTamed())
        continue;
      ZDO zdo = candidate.GetComponent<ZNetView>()?.GetZDO();
      Sadle saddle = FindSaddle(candidate);
      if (zdo == null || !zdo.GetBool(ZDOVars.s_haveSaddleHash) ||
          saddle == null || !saddle.gameObject.activeInHierarchy) continue;
      yield return candidate;
    }
  }

  static Sadle FindSaddle(Character character) =>
      character == null ? null :
      character.GetComponent<Sadle>() ??
      character.GetComponentInChildren<Sadle>(includeInactive: true);

  static bool IsSaddlePrefab(int prefabHash) {
    GameObject prefab = ZNetScene.instance?.GetPrefab(prefabHash);
    Character character = prefab?.GetComponent<Character>();
    return character != null && FindSaddle(character) != null;
  }

  static bool Selected(Sadle saddle) {
    Character mount = saddle?.GetCharacter();
    ZDO zdo = mount?.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null || !mount.IsTamed() ||
        !zdo.GetBool(ZDOVars.s_haveSaddleHash)) return false;
    if (!string.IsNullOrEmpty(zdo.GetString(RunTagHash, string.Empty)))
      return true;
    SaddleCutoverRunner active = Volatile.Read(ref _active);
    return active != null && active._announcements.Values.Any(
        announcement => announcement.Uid == zdo.m_uid);
  }

  static Player FindPlayerBySession(long user) {
    foreach (Player player in Player.GetAllPlayers())
      if (player != null && player.GetZDOID().UserID == user) return player;
    return null;
  }

  static Player FindPlayerConnectedToMount(ZDOID mountId) {
    foreach (Player player in Player.GetAllPlayers()) {
      ZDO zdo = player?.GetComponent<ZNetView>()?.GetZDO();
      if (zdo != null && zdo.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.SyncTransform) == mountId)
        return player;
    }
    return null;
  }

  void ObserveMotion(MountProbe probe, Character mount) {
    probe.MaxDistance = Mathf.Max(
        probe.MaxDistance,
        Vector3.Distance(probe.StartPosition, mount.transform.position));
    probe.MaxHeading = Mathf.Max(
        probe.MaxHeading,
        Quaternion.Angle(probe.StartRotation, mount.transform.rotation));
    ZDOID uid = mount.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
    if (!uid.IsNone() && probe.AuthorityEpoch != 0) {
      int current = ReplicaSequence(uid, probe.AuthorityEpoch);
      probe.SnapshotAdvance = Math.Max(
          probe.SnapshotAdvance,
          Math.Max(0, current - probe.StartSnapshotSequence));
    }
  }

  bool DriveProofPassed(MountProbe probe) =>
      probe.MaxDistance >= MinimumProofMovementMeters &&
      probe.MaxHeading >= MinimumProofHeadingDegrees &&
      probe.LocalControlCalls > 0 && probe.NonZeroControlCalls > 0 &&
      probe.RidingTicks > 0 &&
      probe.SnapshotAdvance >= MinimumSnapshotAdvance;

  bool ObserveProofPassed(MountProbe probe) {
    if (probe.MaxDistance < MinimumProofMovementMeters ||
        probe.MaxHeading < MinimumProofHeadingDegrees ||
        probe.SnapshotAdvance < MinimumSnapshotAdvance ||
        probe.AttachmentSamples < 30 ||
        probe.MaxAttachmentError > MaximumAttachmentErrorMeters)
      return false;
    float p95 = Percentile95(probe.AttachmentErrors);
    probe.AttachmentP95 = p95;
    return p95 <= 0.10f;
  }

  static float Percentile95(List<float> values) {
    if (values == null || values.Count == 0) return float.PositiveInfinity;
    float[] ordered = values.OrderBy(value => value).ToArray();
    int index = Mathf.Clamp(
        Mathf.CeilToInt(ordered.Length * 0.95f) - 1, 0, ordered.Length - 1);
    return ordered[index];
  }

  int ReplicaSequence(ZDOID uid, uint epoch) =>
      _replicaSnapshotSequences.TryGetValue(
          SnapshotKey(uid, epoch), out uint sequence)
          ? sequence > int.MaxValue ? int.MaxValue : (int) sequence
          : 0;

  uint ClientEpoch(ZDOID uid) =>
      _clientAuthorities.TryGetValue(uid, out ClientAuthority value)
          ? value.Epoch : 0;

  /// <summary>
  /// Exposes only the accepted client-side authority tuple needed by the
  /// autonomous-creature canary. The saddle runner remains the sole owner of
  /// transfer epochs and canonical snapshot sequencing.
  /// </summary>
  internal bool TryGetClientAuthorityState(
      ZDOID uid,
      out long ownerPeerId,
      out uint epoch,
      out int replicaSequence) {
    ownerPeerId = 0;
    epoch = 0;
    replicaSequence = 0;
    if (_disposed || uid.IsNone() || !_clientAuthorities.TryGetValue(
            uid, out ClientAuthority authority) ||
        authority.OwnerPeerId == 0 || authority.Epoch == 0)
      return false;
    ownerPeerId = authority.OwnerPeerId;
    epoch = authority.Epoch;
    replicaSequence = ReplicaSequence(uid, epoch);
    return true;
  }

  static void ApplyMountFields(ZDO zdo, Snapshot snapshot) {
    zdo.SetPosition(snapshot.Position);
    zdo.SetRotation(snapshot.Rotation);
    zdo.Set(ZDOVars.s_velHash, snapshot.Velocity);
    zdo.Set(ZDOVars.s_bodyVelHash, snapshot.Velocity);
    zdo.Set(ZDOVars.s_bodyAVelHash, snapshot.AngularVelocity);
    zdo.Set(ZDOVars.s_user, snapshot.User);
    zdo.Set(ZDOVars.s_stamina, snapshot.Stamina);
    if (zdo.GetOwner() != snapshot.OwnerPeerId)
      zdo.SetOwner(snapshot.OwnerPeerId);
  }

  static ZPackage BuildSnapshotPackage(Snapshot value) =>
      BuildSnapshotPackage(
          value.RunId, value.Uid, value.OwnerPeerId, value.Epoch,
          value.Sequence, value.Position, value.Rotation, value.Velocity,
          value.AngularVelocity, value.User, value.Stamina, value.RiderId,
          value.ParentSync, value.AttachJoint, value.RelativePosition,
          value.RelativeRotation, value.RelativeVelocity);

  static ZPackage BuildSnapshotPackage(
      string runId, ZDOID uid, long ownerPeerId, uint epoch, uint sequence,
      Vector3 position, Quaternion rotation, Vector3 velocity,
      Vector3 angularVelocity, long user, float stamina, ZDOID riderId,
      bool parentSync, string attachJoint, Vector3 relativePosition,
      Quaternion relativeRotation, Vector3 relativeVelocity) {
    ZPackage package = new();
    package.Write(SnapshotSchema);
    package.Write(runId);
    package.Write(uid);
    package.Write(ownerPeerId);
    package.Write(epoch);
    package.Write(sequence);
    package.Write(position);
    package.Write(rotation);
    package.Write(velocity);
    package.Write(angularVelocity);
    package.Write(user);
    package.Write(stamina);
    package.Write(riderId);
    package.Write(parentSync);
    package.Write(attachJoint ?? string.Empty);
    package.Write(relativePosition);
    package.Write(relativeRotation);
    package.Write(relativeVelocity);
    package.SetPos(0);
    return package;
  }

  static Snapshot ReadSnapshot(ZPackage package) {
    package.SetPos(0);
    int schema = package.ReadInt();
    Snapshot value = new() {
        RunId = package.ReadString(),
        Uid = package.ReadZDOID(),
        OwnerPeerId = package.ReadLong(),
        Epoch = package.ReadUInt(),
        Sequence = package.ReadUInt(),
        Position = package.ReadVector3(),
        Rotation = package.ReadQuaternion(),
        Velocity = package.ReadVector3(),
        AngularVelocity = package.ReadVector3(),
        User = package.ReadLong(),
        Stamina = package.ReadSingle(),
        RiderId = package.ReadZDOID(),
        ParentSync = package.ReadBool(),
        AttachJoint = package.ReadString(),
        RelativePosition = package.ReadVector3(),
        RelativeRotation = package.ReadQuaternion(),
        RelativeVelocity = package.ReadVector3()
    };
    if (package.GetPos() != package.Size())
      throw SnapshotShapeInvalid(
          "package_length read=" + package.GetPos() +
          " size=" + package.Size());
    if (schema != SnapshotSchema)
      throw SnapshotShapeInvalid(
          "schema value=" + schema + " expected=" + SnapshotSchema);
    if (!SafeToken(value.RunId, 80))
      throw SnapshotShapeInvalid(
          "run_id length=" + (value.RunId?.Length ?? -1));
    if (value.Uid.IsNone())
      throw SnapshotShapeInvalid("uid value=" + value.Uid);
    if (value.OwnerPeerId == 0)
      throw SnapshotShapeInvalid("owner_peer_id value=0");
    if (value.Epoch == 0)
      throw SnapshotShapeInvalid("epoch value=0");
    if (value.Sequence == 0)
      throw SnapshotShapeInvalid("sequence value=0");
    if (!Finite(value.Position))
      throw SnapshotShapeInvalid("position value=" + Format(value.Position));
    if (!Finite(value.Rotation))
      throw SnapshotShapeInvalid("rotation_non_finite");
    if (!Finite(value.Velocity))
      throw SnapshotShapeInvalid(
          "mount_velocity_non_finite value=" + Format(value.Velocity));
    if (!Finite(value.AngularVelocity))
      throw SnapshotShapeInvalid(
          "mount_angular_velocity_non_finite value=" +
          Format(value.AngularVelocity));
    if (value.Velocity.magnitude > MaximumVelocity)
      throw SnapshotShapeInvalid(
          "mount_velocity value=" + Format(value.Velocity) +
          " magnitude=" + value.Velocity.magnitude.ToString(
              "0.###", CultureInfo.InvariantCulture) +
          " max=" + MaximumVelocity.ToString(
              "0.###", CultureInfo.InvariantCulture));
    if (value.AngularVelocity.magnitude > MaximumVelocity)
      throw SnapshotShapeInvalid(
          "mount_angular_velocity value=" + Format(value.AngularVelocity) +
          " magnitude=" + value.AngularVelocity.magnitude.ToString(
              "0.###", CultureInfo.InvariantCulture) +
          " max=" + MaximumVelocity.ToString(
              "0.###", CultureInfo.InvariantCulture));
    if (value.User < 0)
      throw SnapshotShapeInvalid("user_negative value=" + value.User);
    if (value.User != 0 && value.User != value.OwnerPeerId)
      throw SnapshotShapeInvalid(
          "user_owner_mismatch user=" + value.User +
          " owner=" + value.OwnerPeerId);
    if (!Finite(value.Stamina) || value.Stamina < 0)
      throw SnapshotShapeInvalid(
          "stamina value=" + value.Stamina.ToString(
              "0.###", CultureInfo.InvariantCulture));
    if (!Finite(value.RelativePosition))
      throw SnapshotShapeInvalid(
          "relative_position_non_finite value=" +
          Format(value.RelativePosition));
    if (!Finite(value.RelativeRotation))
      throw SnapshotShapeInvalid("relative_rotation_non_finite");
    if (!Finite(value.RelativeVelocity))
      throw SnapshotShapeInvalid(
          "relative_velocity_non_finite value=" +
          Format(value.RelativeVelocity));
    float relativeRotationDegrees = Quaternion.Angle(
        value.RelativeRotation, Quaternion.identity);
    if (value.User == 0 && (!value.RiderId.IsNone() ||
        value.ParentSync || value.AttachJoint.Length != 0 ||
        value.RelativePosition.magnitude > 0.01f ||
        relativeRotationDegrees > 0.1f ||
        value.RelativeVelocity.magnitude > 0.01f))
      throw SnapshotShapeInvalid(
          "detached_edge rider=" + value.RiderId +
          " parent_sync=" + value.ParentSync +
          " joint=" + value.AttachJoint +
          " rel_pos=" + Format(value.RelativePosition) +
          " rel_rot_deg=" + relativeRotationDegrees.ToString(
              "0.###", CultureInfo.InvariantCulture) +
          " rel_velocity=" + Format(value.RelativeVelocity) +
          " rel_speed=" + value.RelativeVelocity.magnitude.ToString(
              "0.###", CultureInfo.InvariantCulture));
    if (value.User != 0 && (value.RiderId.IsNone() ||
        value.RiderId.UserID != value.User || !value.ParentSync ||
        string.IsNullOrEmpty(value.AttachJoint) ||
        value.RelativePosition.magnitude > 0.01f ||
        relativeRotationDegrees > 0.1f ||
        value.RelativeVelocity.magnitude > MaximumVelocity))
      throw SnapshotShapeInvalid(
          "attached_edge user=" + value.User +
          " rider=" + value.RiderId +
          " parent_sync=" + value.ParentSync +
          " joint=" + value.AttachJoint +
          " rel_pos=" + Format(value.RelativePosition) +
          " rel_rot_deg=" + relativeRotationDegrees.ToString(
              "0.###", CultureInfo.InvariantCulture) +
          " rel_velocity=" + Format(value.RelativeVelocity) +
          " rel_speed=" + value.RelativeVelocity.magnitude.ToString(
              "0.###", CultureInfo.InvariantCulture));
    return value;
  }

  static InvalidOperationException SnapshotShapeInvalid(string detail) =>
      new("saddle_snapshot_shape_invalid field=" + detail);

  static string SnapshotKey(ZDOID uid, uint epoch) =>
      uid + ":" + epoch.ToString(CultureInfo.InvariantCulture);

  static string ServerCharacterKey(string runId, long peerId) =>
      runId + ":" + peerId.ToString(CultureInfo.InvariantCulture);

  void CompleteProbe(string detail) {
    if (_probe == null || _probe.Terminal) return;
    _probe.Terminal = true;
    _probe.Success = true;
    _probe.Detail = detail;
    Write("probe_passed", _probe.ActionId, detail);
  }

  void FailProbe(string detail) {
    if (_probe == null || _probe.Terminal) return;
    _probe.Terminal = true;
    _probe.Success = false;
    _probe.Detail = detail;
    Write("probe_failed", _probe.ActionId, detail);
  }

  void FailMatchingProbe(string detail) {
    if (_probe != null && !_probe.Terminal) FailProbe(detail);
  }

  static string Progress(MountProbe probe) =>
      "distance=" + probe.MaxDistance.ToString("0.###", CultureInfo.InvariantCulture)
      + " heading=" + probe.MaxHeading.ToString("0.###", CultureInfo.InvariantCulture)
      + " controls=" + probe.LocalControlCalls
      + " nonzero_controls=" + probe.NonZeroControlCalls
      + " riding_ticks=" + probe.RidingTicks
      + " epoch=" + probe.AuthorityEpoch
      + " snapshot_advance=" + probe.SnapshotAdvance
      + " attachment_samples=" + probe.AttachmentSamples
      + " attachment_p95=" + probe.AttachmentP95.ToString("0.###", CultureInfo.InvariantCulture)
      + " attachment_max=" + probe.MaxAttachmentError.ToString("0.###", CultureInfo.InvariantCulture);

  void Write(string state, string actionId, string detail) {
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] = DateTimeOffset.UtcNow.ToString(
                "o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = CurrentRunId(),
            ["role"] = Role(),
            ["action_id"] = SafeToken(actionId, 80) ? actionId : "unscoped",
            ["detail"] = detail ?? string.Empty
        });
  }

  static bool Enabled() =>
      PluginConfig.RoutedRpcCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveRoutedRpcCutover;

  static string CurrentRunId() {
    string active = NativeAutotestRequest.ActiveRunId;
    if (SafeToken(active, 80)) return active;
    string configured = PluginConfig.NativeNetworkEvidenceRunId?.Value;
    return SafeToken(configured, 80) ? configured.Trim() : "unscoped";
  }

  static bool IsServer() {
    try { return ZNet.instance != null && ZNet.instance.IsServer(); }
    catch { return false; }
  }

  static string Role() => ZNet.instance == null ? "starting" :
      (IsServer() ? "server" : "client");

  static bool SafeToken(string value, int maximum) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
      value.All(character => char.IsLetterOrDigit(character) ||
          character is '-' or '_' or '.');

  static bool Finite(float value) =>
      !float.IsNaN(value) && !float.IsInfinity(value);
  static bool Finite(Vector3 value) =>
      Finite(value.x) && Finite(value.y) && Finite(value.z);
  static bool Finite(Quaternion value) =>
      Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
  static string Format(Vector3 value) =>
      value.x.ToString("0.##", CultureInfo.InvariantCulture) + "," +
      value.y.ToString("0.##", CultureInfo.InvariantCulture) + "," +
      value.z.ToString("0.##", CultureInfo.InvariantCulture);

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  sealed class MountProbe {
    public string RunId;
    public string ActionId;
    public string Mode;
    public string Phase = "starting";
    public float StartedAt;
    public float DeadlineAt;
    public float Duration;
    public float NextAttemptAt;
    public float ControlStartedAt;
    public Vector3 StartPosition;
    public Quaternion StartRotation;
    public float MaxDistance;
    public float MaxHeading;
    public float MaxAttachmentError;
    public float AttachmentP95;
    public int StartSnapshotSequence;
    public int SnapshotAdvance;
    public int LocalControlCalls;
    public int NonZeroControlCalls;
    public int RidingTicks;
    public int AttachmentSamples;
    public long ObservedRider;
    public long ExpectedReclaimOwner;
    public uint AuthorityEpoch;
    public uint PreReclaimEpoch;
    public bool RequestSent;
    public bool ControlGranted;
    public bool ReleaseSent;
    public bool ObservationStarted;
    public bool AbortSent;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
    public readonly List<float> AttachmentErrors = new();
  }

  sealed class Snapshot {
    public string RunId;
    public ZDOID Uid;
    public long OwnerPeerId;
    public uint Epoch;
    public uint Sequence;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public long User;
    public float Stamina;
    public ZDOID RiderId;
    public bool ParentSync;
    public string AttachJoint;
    public Vector3 RelativePosition;
    public Quaternion RelativeRotation;
    public Vector3 RelativeVelocity;
  }

  readonly struct ClientAuthority {
    public ClientAuthority(long ownerPeerId, uint epoch) {
      OwnerPeerId = ownerPeerId;
      Epoch = epoch;
    }

    public long OwnerPeerId { get; }
    public uint Epoch { get; }
  }

  readonly struct ServerAuthority {
    public ServerAuthority(
        long ownerPeerId, long previousOwnerPeerId, uint epoch) {
      OwnerPeerId = ownerPeerId;
      PreviousOwnerPeerId = previousOwnerPeerId;
      Epoch = epoch;
    }

    public long OwnerPeerId { get; }
    public long PreviousOwnerPeerId { get; }
    public uint Epoch { get; }
  }

  readonly struct MountAnnouncement {
    public MountAnnouncement(
        ZDOID uid, Vector3 position, long ownerPeerId, uint epoch) {
      Uid = uid;
      Position = position;
      OwnerPeerId = ownerPeerId;
      Epoch = epoch;
    }

    public ZDOID Uid { get; }
    public Vector3 Position { get; }
    public long OwnerPeerId { get; }
    public uint Epoch { get; }
  }
}

[HarmonyPatch(typeof(Sadle), "RPC_RequestControl")]
static class SaddleCutoverGrantPatch {
  [HarmonyPrefix]
  static void Prefix(Sadle __instance, out long __state) {
    __state = __instance?.GetCharacter()?.GetComponent<ZNetView>()?
        .GetZDO()?.GetOwner() ?? 0L;
  }

  [HarmonyPostfix]
  static void Postfix(Sadle __instance, long __state) {
    long next = __instance?.GetCharacter()?.GetComponent<ZNetView>()?
        .GetZDO()?.GetOwner() ?? 0L;
    SaddleCutoverRunner.NotifyVanillaGrant(__instance, __state, next);
  }
}

[HarmonyPatch(typeof(Sadle), "RPC_ReleaseControl")]
static class SaddleCutoverReleasePatch {
  [HarmonyPrefix]
  static void Prefix(Sadle __instance, out long __state) {
    __state = __instance?.GetCharacter()?.GetComponent<ZNetView>()?
        .GetZDO()?.GetLong(ZDOVars.s_user, 0L) ?? 0L;
  }

  [HarmonyPostfix]
  static void Postfix(
      Sadle __instance, long sender, long playerID, long __state) {
    long releasedUser = __instance?.GetCharacter()?
        .GetComponent<ZNetView>()?.GetZDO()?
        .GetLong(ZDOVars.s_user, 0L) ?? 0L;
    SaddleCutoverRunner.NotifyVanillaRelease(
        __instance, sender, playerID, __state, releasedUser);
  }
}

[HarmonyPatch(typeof(Sadle), "RPC_Controls")]
static class SaddleCutoverControlsPatch {
  [HarmonyPostfix]
  static void Postfix(
      Sadle __instance, long sender, Vector3 rideDir, int rideSpeed) =>
      SaddleCutoverRunner.NotifyLocalControls(
          __instance, sender, rideDir, rideSpeed);
}

[HarmonyPatch(typeof(Sadle), nameof(Sadle.UpdateRiding))]
static class SaddleCutoverRidingPatch {
  [HarmonyPostfix]
  static void Postfix(Sadle __instance, bool __result) =>
      SaddleCutoverRunner.NotifyRidingTick(__instance, __result);
}

[HarmonyPatch(typeof(Sadle), "CalculateHaveValidUser")]
static class SaddleCutoverValidUserPatch {
  [HarmonyPostfix]
  static void Postfix(Sadle __instance, ref bool ___m_haveValidUser) {
    if (__instance == null || !SaddleCutoverRunner.AllowCanonicalParentSyncFor(
            __instance, out Player rider)) return;
    ___m_haveValidUser = Vector3.Distance(
        rider.GetZDOID().IsNone()
            ? rider.transform.position
            : rider.GetComponent<ZNetView>().GetZDO().GetPosition(),
        __instance.transform.position) < __instance.m_maxUseRange;
  }
}
