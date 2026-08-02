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
/// Typed ship-control and ship-transform replacement lane. Vanilla helm RPCs
/// still execute their real handlers, but only after the routed adapter proves
/// the target is a Ship. The current ship owner publishes authenticated body
/// snapshots to the canonical server. The server validates, journals, and
/// republishes those frames so non-owners no longer depend on native ZDOData
/// for vehicle motion.
/// </summary>
public sealed class ShipCutoverRunner : IDisposable {
  public const string ReceiptFileName = "ship-cutover.jsonl";

  const int SnapshotSchema = 2;
  const float SnapshotIntervalSeconds = 0.2f;
  const float MaximumSnapshotStepMeters = 25.0f;
  const float MaximumVelocity = 100.0f;
  const float MinimumProofMovementMeters = 0.35f;

  static readonly int RunTagHash =
      ZdoJournalCutoverRunner.ProbeTagName.GetStableHashCode();
  static readonly int ActionTagHash =
      "ComfyNetworkSense_ShipAction".GetStableHashCode();
  static ShipCutoverRunner _active;

  readonly RoutedRpcCutoverRunner _routedRpc;
  readonly TelemetryLogWriter _writer = new();
  readonly Dictionary<ZDOID, uint> _clientSnapshotSequences = new();
  readonly Dictionary<string, uint> _serverSnapshotSequences =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, uint> _replicaSnapshotSequences =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, ZDOID> _serverShipsByRun =
      new(StringComparer.Ordinal);

  ZRoutedRpc _registeredRpc;
  ShipProbe _probe;
  Vector3 _waterSite;
  bool _waterSiteKnown;
  float _nextSnapshotAt;
  bool _disposed;

  public ShipCutoverRunner(RoutedRpcCutoverRunner routedRpc) {
    _routedRpc = routedRpc;
    ShipCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  internal static void NotifyVanillaOwnerTransfer(ZDOID uid, long newOwner) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || uid.IsNone() || newOwner == 0)
      return;
    active.SendTransfer(
        "ship-owner-transfer", CurrentRunId(), uid, newOwner);
  }

  public void Update(float now) {
    if (_disposed) return;
    EnsureHandlers();
    if (Enabled() && !IsServer() && now >= _nextSnapshotAt) {
      _nextSnapshotAt = now + SnapshotIntervalSeconds;
      PublishOwnedSnapshots();
    }
    TickProbe(now);
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float durationSeconds,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) || !ShipCutoverModePolicy.Allows(mode)) {
      detail = "ship_probe_parameters_invalid";
      return false;
    }
    if (!Enabled()) {
      detail = "ship_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        ZRoutedRpc.instance == null || ZNetScene.instance == null) {
      detail = "ship_probe_client_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_ship_probe_active";
      return false;
    }
    string runId = CurrentRunId();
    if (!SafeToken(runId, 80)) {
      detail = "ship_probe_run_missing";
      return false;
    }

    _probe = new ShipProbe {
        RunId = runId,
        ActionId = actionId,
        Mode = mode,
        StartedAt = Time.unscaledTime,
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 5.0f, 180.0f),
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
      detail = "ship_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  void TickProbe(float now) {
    ShipProbe probe = _probe;
    if (probe == null || probe.Terminal || IsServer()) return;
    if (now > probe.DeadlineAt) {
      FailProbe("ship_probe_deadline_exceeded mode=" + probe.Mode +
          " phase=" + probe.Phase + " " + ProbeProgress(probe));
      return;
    }

    switch (probe.Mode) {
      case "water":
        TickWaterProbe(probe, now);
        break;
      case "spawn":
        TickSpawnProbe(probe);
        break;
      case "wait_ship":
        if (TryFindRunShip(probe.RunId, out Ship found, out _))
          CompleteProbe("ship_instantiated uid=" + ShipId(found));
        break;
      case "board":
        TickBoardProbe(probe, now);
        break;
      case "drive":
        TickDriveProbe(probe, now);
        break;
      case "observe":
        TickObserveProbe(probe, now);
        break;
      case "transfer":
        TickTransferProbe(probe);
        break;
      case "wait_owner":
        if (TryFindRunShip(probe.RunId, out Ship owned, out ZNetView ownedView)
            && ownedView.IsOwner())
          CompleteProbe("ship_owner_is_local uid=" + ShipId(owned));
        break;
      case "wait_released":
        if (TryFindRunShip(
                probe.RunId, out Ship released, out ZNetView releasedView)) {
          if (!releasedView.IsOwner()) {
            FailProbe("ship_release_observer_not_owner");
            break;
          }
          long user = releasedView.GetZDO().GetLong(ZDOVars.s_user, 0L);
          if (user == 0L)
            CompleteProbe("ship_helm_released uid=" + ShipId(released) +
                " local_owner=true");
        }
        break;
    }
  }

  void TickWaterProbe(ShipProbe probe, float now) {
    if (!_waterSiteKnown) {
      if (!TryFindWaterSite(out _waterSite, out string searchDetail)) {
        FailProbe(searchDetail);
        return;
      }
      _waterSiteKnown = true;
      Write("water_site_selected", probe.ActionId,
          "site=" + Format(_waterSite) + " " + searchDetail);
    }
    Vector3 target = _waterSite + Vector3.up * 4.0f;
    if (!probe.RequestSent) {
      if (!Player.m_localPlayer.TeleportTo(
              target,
              Player.m_localPlayer.transform.rotation,
              distantTeleport: true)) return;
      probe.RequestSent = true;
      probe.Phase = "teleporting";
      return;
    }
    if (!Player.m_localPlayer.IsTeleporting() &&
        Vector3.Distance(Player.m_localPlayer.transform.position, target) <=
            12.0f) {
      CompleteProbe("water_rendezvous_complete site=" + Format(_waterSite));
      return;
    }
    if (now >= probe.NextDiagnosticAt) {
      probe.NextDiagnosticAt = now + 5.0f;
      Write("water_rendezvous_progress", probe.ActionId,
          "target=" + Format(target) + " current=" +
          Format(Player.m_localPlayer.transform.position) +
          " teleporting=" + Player.m_localPlayer.IsTeleporting());
    }
  }

  void TickSpawnProbe(ShipProbe probe) {
    if (!_waterSiteKnown) {
      FailProbe("ship_water_site_missing");
      return;
    }
    if (probe.RequestSent) return;
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return;
    ZPackage request = new();
    request.Write(probe.RunId);
    request.Write(probe.ActionId);
    request.Write(_waterSite);
    request.SetPos(0);
    probe.RequestSent = _routedRpc.InvokeTyped(
        probe.ActionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid,
            ValheimRoutedRpcAdmissions.CutoverShipSpawnRequest,
            new object[] { request }));
    if (!probe.RequestSent)
      FailProbe("ship_spawn_request_queue_failed");
    else
      probe.Phase = "spawn_requested";
  }

  void TickBoardProbe(ShipProbe probe, float now) {
    if (!TryFindRunShip(probe.RunId, out Ship ship, out _)) return;
    Player player = Player.m_localPlayer;
    if (player.GetStandingOnShip() == ship) {
      CompleteProbe("ship_boarded uid=" + ShipId(ship) +
          " local_owner=" + ship.IsOwner());
      return;
    }
    if (now < probe.NextAttemptAt) return;
    probe.NextAttemptAt = now + 0.5f;
    ShipControlls controls = FindControls(ship);
    if (controls == null || controls.m_attachPoint == null) {
      FailProbe("ship_attach_point_missing");
      return;
    }
    Vector3 target = controls.m_attachPoint.position + ship.transform.up * 1.4f;
    Rigidbody body = player.GetComponent<Rigidbody>();
    player.transform.position = target;
    if (body != null) {
      body.position = target;
      body.linearVelocity = Vector3.zero;
      body.angularVelocity = Vector3.zero;
    }
    Physics.SyncTransforms();
    probe.Phase = "awaiting_ship_ground_contact";
    Write("ship_board_attempt", probe.ActionId,
        "uid=" + ShipId(ship) + " target=" + Format(target));
  }

  void TickDriveProbe(ShipProbe probe, float now) {
    if (!TryFindRunShip(probe.RunId, out Ship ship, out ZNetView view)) return;
    Player player = Player.m_localPlayer;
    ShipControlls controls = FindControls(ship);
    if (controls == null) {
      FailProbe("ship_controls_missing");
      return;
    }
    if (view.IsOwner()) {
      FailProbe("ship_drive_lost_remote_owner");
      return;
    }
    if (!probe.ControlGranted) {
      if (player.GetStandingOnShip() != ship) {
        probe.Phase = "waiting_onboard";
        return;
      }
      if (player.GetControlledShip() != ship) {
        if (now < probe.NextAttemptAt) return;
        probe.NextAttemptAt = now + 1.0f;
        _routedRpc.InvokeTyped(
            probe.ActionId,
            () => controls.Interact(player, repeat: false, alt: false));
        probe.Phase = "requesting_helm";
        return;
      }
      probe.ControlGranted = true;
      probe.ControlStartedAt = now;
      probe.StartPosition = ship.transform.position;
      probe.Phase = "driving";
      Write("ship_helm_granted", probe.ActionId,
          "uid=" + ShipId(ship) + " owner=" + view.GetZDO().GetOwner());
    }

    if (!probe.ReleaseSent && player.GetControlledShip() != ship) {
      FailProbe("ship_drive_control_attachment_lost " + ProbeProgress(probe));
      return;
    }

    probe.MaxDistance = Mathf.Max(
        probe.MaxDistance,
        Vector3.Distance(probe.StartPosition, ship.transform.position));
    probe.MaxRudder = Mathf.Max(probe.MaxRudder, Mathf.Abs(ship.GetRudderValue()));
    if (ship.GetSpeedSetting() != Ship.Speed.Stop) probe.SpeedChanged = true;

    if (!probe.ReleaseSent && now - probe.ControlStartedAt < probe.Duration) {
      _routedRpc.InvokeTyped(
          probe.ActionId,
          () => controls.ApplyControlls(
              new Vector3(0.8f, 0.0f, 1.0f),
              ship.transform.forward,
              run: false,
              autoRun: false,
              block: false));
      return;
    }

    if (!probe.ReleaseSent) {
      probe.ReleaseSent = true;
      _routedRpc.InvokeTyped(probe.ActionId, player.StopDoodadControl);
      probe.Phase = "releasing_helm";
      return;
    }

    long user = view.GetZDO().GetLong(ZDOVars.s_user, 0L);
    if (player.GetControlledShip() == null && user == 0L) {
      if (!probe.SpeedChanged || probe.MaxRudder < 0.02f ||
          probe.MaxDistance < MinimumProofMovementMeters) {
        FailProbe("ship_drive_semantics_missing " + ProbeProgress(probe));
        return;
      }
      CompleteProbe("ship_drive_complete remote_owner=true " +
          ProbeProgress(probe));
    }
  }

  void TickObserveProbe(ShipProbe probe, float now) {
    if (!TryFindRunShip(probe.RunId, out Ship ship, out ZNetView view)) return;
    if (!probe.ObservationStarted) {
      if (!view.IsOwner()) {
        FailProbe("ship_observer_not_owner");
        return;
      }
      long user = view.GetZDO().GetLong(ZDOVars.s_user, 0L);
      if (user == 0L) {
        probe.Phase = "waiting_helm_user";
        return;
      }
      probe.ObservationStarted = true;
      probe.ControlStartedAt = now;
      probe.StartPosition = ship.transform.position;
      probe.ObservedOwner = true;
      probe.Phase = "observing";
      Write("ship_observer_started", probe.ActionId,
          "uid=" + ShipId(ship) + " helm_user=" + user);
    }
    probe.ObservedOwner &= view.IsOwner();
    probe.MaxDistance = Mathf.Max(
        probe.MaxDistance,
        Vector3.Distance(probe.StartPosition, ship.transform.position));
    probe.MaxRudder = Mathf.Max(probe.MaxRudder, Mathf.Abs(ship.GetRudderValue()));
    if (ship.GetSpeedSetting() != Ship.Speed.Stop) probe.SpeedChanged = true;
    if (now - probe.ControlStartedAt < probe.Duration) return;
    if (!probe.ObservedOwner || !probe.SpeedChanged ||
        probe.MaxRudder < 0.02f ||
        probe.MaxDistance < MinimumProofMovementMeters) {
      FailProbe("ship_observer_semantics_missing " + ProbeProgress(probe));
      return;
    }
    CompleteProbe("ship_observer_complete local_owner=true " +
        ProbeProgress(probe));
  }

  void TickTransferProbe(ShipProbe probe) {
    if (!TryFindRunShip(probe.RunId, out Ship ship, out ZNetView view)) return;
    if (!probe.RequestSent) {
      if (!view.IsOwner()) {
        FailProbe("ship_transfer_requester_not_owner");
        return;
      }
      long newOwner = FindRemotePeer();
      if (newOwner == 0) return;
      probe.DesiredOwner = newOwner;
      probe.RequestSent = SendTransfer(
          probe.ActionId, probe.RunId, view.GetZDO().m_uid, newOwner);
      if (!probe.RequestSent)
        FailProbe("ship_transfer_request_queue_failed");
      else
        probe.Phase = "transfer_requested";
      return;
    }
    if (probe.ResponseReceived &&
        view.GetZDO().GetOwner() == probe.DesiredOwner)
      CompleteProbe("ship_transfer_complete uid=" + ShipId(ship) +
          " new_owner=" + probe.DesiredOwner);
  }

  void EnsureHandlers() {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverShipSpawnRequest,
        HandleSpawnRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverShipSpawnResponse,
        HandleSpawnResponse);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverShipTransferRequest,
        HandleTransferRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverShipTransferResponse,
        HandleTransferResponse);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.ModShipSnapshot,
        HandleSnapshot);
    _registeredRpc = rpc;
    Write("handlers_registered", "unscoped", Role());
  }

  void PublishOwnedSnapshots() {
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return;
    foreach (IMonoUpdater updater in Ship.Instances.ToArray()) {
      if (updater is not Ship ship || ship == null || !ship.gameObject.activeInHierarchy)
        continue;
      ZNetView view = ship.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      Rigidbody body = ship.GetComponent<Rigidbody>();
      if (zdo == null || body == null || !view.IsOwner()) continue;
      _clientSnapshotSequences.TryGetValue(zdo.m_uid, out uint sequence);
      sequence++;
      _clientSnapshotSequences[zdo.m_uid] = sequence;
      ZPackage package = new();
      package.Write(SnapshotSchema);
      package.Write(CurrentRunId());
      package.Write(zdo.m_uid);
      package.Write(zdo.GetOwner());
      package.Write(sequence);
      package.Write(ship.transform.position);
      package.Write(ship.transform.rotation);
      package.Write(body.linearVelocity);
      package.Write(body.angularVelocity);
      package.Write((int) ship.GetSpeedSetting());
      package.Write(ship.GetRudderValue());
      package.Write(zdo.GetLong(ZDOVars.s_user, 0L));
      package.SetPos(0);
      ZRoutedRpc.instance.InvokeRoutedRPC(
          server.m_uid,
          ValheimRoutedRpcAdmissions.ModShipSnapshot,
          new object[] { package });
    }
  }

  static void HandleSnapshot(long senderPeerId, ZPackage package) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled()) return;
    // ZRoutedRpc.Everybody invokes the registered handler locally before
    // routing. The server has already validated and journaled this frame; its
    // local echo must be a no-op so RouteRPC can carry the frame to clients.
    if (IsServer() && senderPeerId == ZNet.GetUID()) return;
    try {
      package.SetPos(0);
      int schema = package.ReadInt();
      string runId = package.ReadString();
      ZDOID uid = package.ReadZDOID();
      long ownerPeerId = package.ReadLong();
      uint sequence = package.ReadUInt();
      Vector3 position = package.ReadVector3();
      Quaternion rotation = package.ReadQuaternion();
      Vector3 velocity = package.ReadVector3();
      Vector3 angularVelocity = package.ReadVector3();
      int speed = package.ReadInt();
      float rudder = package.ReadSingle();
      long user = package.ReadLong();
      if (package.GetPos() != package.Size() || schema != SnapshotSchema ||
          !SafeToken(runId, 80) || uid.IsNone() || ownerPeerId == 0 ||
          sequence == 0 ||
          !Finite(position) || !Finite(rotation) || !Finite(velocity) ||
          !Finite(angularVelocity) || velocity.magnitude > MaximumVelocity ||
          angularVelocity.magnitude > MaximumVelocity ||
          speed < (int) Ship.Speed.Stop || speed > (int) Ship.Speed.Full ||
          !Finite(rudder) || Mathf.Abs(rudder) > 1.001f || user < 0)
        throw new InvalidOperationException("ship_snapshot_shape_invalid");

      ZDO zdo = ZDOMan.instance?.GetZDO(uid);
      if (IsServer()) {
        if (ownerPeerId != senderPeerId || zdo == null ||
            zdo.GetOwner() != ownerPeerId || !IsShipPrefab(zdo.GetPrefab()))
          throw new InvalidOperationException("ship_snapshot_authority_invalid");
        if (Vector3.Distance(zdo.GetPosition(), position) >
            MaximumSnapshotStepMeters)
          throw new InvalidOperationException("ship_snapshot_displacement_invalid");
        string key = SnapshotKey(ownerPeerId, uid);
        active._serverSnapshotSequences.TryGetValue(key, out uint previous);
        if (sequence <= previous)
          throw new InvalidOperationException("ship_snapshot_sequence_stale");
        active._serverSnapshotSequences[key] = sequence;

        ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () =>
            ApplySnapshotFields(
                zdo, position, rotation, velocity, angularVelocity,
                speed, rudder, user));
        ZPackage replica = BuildSnapshotPackage(
            runId, uid, ownerPeerId, sequence, position, rotation,
            velocity, angularVelocity, speed, rudder, user);
        if (!active._routedRpc.InvokeTyped(
                "ship-snapshot",
                () => ZRoutedRpc.instance.InvokeRoutedRPC(
                    ZRoutedRpc.Everybody,
                    ValheimRoutedRpcAdmissions.ModShipSnapshot,
                    new object[] { replica })))
          throw new InvalidOperationException("ship_snapshot_fanout_queue_failed");
        if (sequence == 1 || sequence % 25 == 0)
          active.Write("snapshot_applied", "unscoped",
              "uid=" + uid + " sender=" + senderPeerId +
              " sequence=" + sequence + " position=" + Format(position));
        return;
      }

      if (senderPeerId != ZNet.instance.GetServerPeer()?.m_uid || zdo == null ||
          zdo.GetOwner() != ownerPeerId || !IsShipPrefab(zdo.GetPrefab()) ||
          !string.Equals(
              zdo.GetString(RunTagHash, string.Empty),
              runId,
              StringComparison.Ordinal))
        throw new InvalidOperationException("ship_snapshot_replica_authority_invalid");
      if (ownerPeerId == ZDOMan.GetSessionID()) return;

      string replicaKey = SnapshotKey(ownerPeerId, uid);
      active._replicaSnapshotSequences.TryGetValue(
          replicaKey, out uint replicaPrevious);
      if (sequence <= replicaPrevious)
        throw new InvalidOperationException("ship_snapshot_replica_sequence_stale");
      active._replicaSnapshotSequences[replicaKey] = sequence;

      GameObject instance = ZNetScene.instance?.FindInstance(uid);
      Ship ship = instance?.GetComponent<Ship>();
      Rigidbody body = instance?.GetComponent<Rigidbody>();
      if (ship == null || body == null)
        throw new InvalidOperationException("ship_snapshot_replica_missing");
      ApplySnapshotFields(
          zdo, position, rotation, velocity, angularVelocity,
          speed, rudder, user);
      body.position = position;
      body.rotation = rotation;
      body.linearVelocity = velocity;
      body.angularVelocity = angularVelocity;
      if (sequence == 1 || sequence % 25 == 0)
        active.Write("snapshot_replica_applied", "unscoped",
            "uid=" + uid + " owner=" + ownerPeerId +
            " sequence=" + sequence + " position=" + Format(position));
    } catch (Exception exception) {
      active.Write("snapshot_rejected", "unscoped",
          "sender=" + senderPeerId + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      throw;
    }
  }

  static string SnapshotKey(long ownerPeerId, ZDOID uid) =>
      ownerPeerId.ToString(CultureInfo.InvariantCulture) + ":" + uid;

  static ZPackage BuildSnapshotPackage(
      string runId,
      ZDOID uid,
      long ownerPeerId,
      uint sequence,
      Vector3 position,
      Quaternion rotation,
      Vector3 velocity,
      Vector3 angularVelocity,
      int speed,
      float rudder,
      long user) {
    ZPackage package = new();
    package.Write(SnapshotSchema);
    package.Write(runId);
    package.Write(uid);
    package.Write(ownerPeerId);
    package.Write(sequence);
    package.Write(position);
    package.Write(rotation);
    package.Write(velocity);
    package.Write(angularVelocity);
    package.Write(speed);
    package.Write(rudder);
    package.Write(user);
    package.SetPos(0);
    return package;
  }

  static void ApplySnapshotFields(
      ZDO zdo,
      Vector3 position,
      Quaternion rotation,
      Vector3 velocity,
      Vector3 angularVelocity,
      int speed,
      float rudder,
      long user) {
    zdo.SetPosition(position);
    zdo.SetRotation(rotation);
    zdo.Set(ZDOVars.s_velHash, velocity);
    zdo.Set(ZDOVars.s_bodyVelHash, velocity);
    zdo.Set(ZDOVars.s_bodyAVelHash, angularVelocity);
    zdo.Set(ZDOVars.s_forward, speed);
    zdo.Set(ZDOVars.s_rudder, rudder);
    zdo.Set(ZDOVars.s_user, user);
  }

  static void HandleSpawnRequest(long senderPeerId, ZPackage package) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    string runId = string.Empty;
    string actionId = string.Empty;
    try {
      package.SetPos(0);
      runId = package.ReadString();
      actionId = package.ReadString();
      Vector3 requested = package.ReadVector3();
      if (package.GetPos() != package.Size() || !SafeToken(runId, 80) ||
          !SafeToken(actionId, 80) || !Finite(requested) ||
          !string.Equals(runId, CurrentRunId(), StringComparison.Ordinal) ||
          WorldGenerator.instance == null ||
          WorldGenerator.instance.GetBiome(
              requested.x, requested.z, 0.02f, waterAlwaysOcean: true) !=
              Heightmap.Biome.Ocean ||
          WorldGenerator.instance.GetHeight(requested.x, requested.z) > 28.0f)
        throw new InvalidOperationException("ship_spawn_request_invalid");

      ZDO zdo = null;
      if (active._serverShipsByRun.TryGetValue(runId, out ZDOID existingId))
        zdo = ZDOMan.instance.GetZDO(existingId);
      if (zdo == null) {
        GameObject prefab = ZNetScene.instance?.GetPrefab("Karve");
        if (prefab == null ||
            (prefab.GetComponent<Ship>() == null &&
             prefab.GetComponentInChildren<Ship>(includeInactive: true) == null))
          throw new InvalidOperationException("karve_prefab_missing");
        Vector3 position = new(requested.x, 30.0f, requested.z);
        int prefabHash = prefab.name.GetStableHashCode();
        zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
        ZdoJournalCutoverRunner.ApplyCanonicalMutation(zdo, () => {
          zdo.SetPrefab(prefabHash);
          zdo.Persistent = false;
          zdo.SetRotation(Quaternion.Euler(0.0f, 90.0f, 0.0f));
          zdo.Set(RunTagHash, runId);
          zdo.Set(ActionTagHash, actionId);
          zdo.SetOwner(senderPeerId);
        });
        active._serverShipsByRun[runId] = zdo.m_uid;
        active.Write("ship_spawned", actionId,
            "uid=" + zdo.m_uid + " owner=" + senderPeerId +
            " position=" + Format(position));
      }
      active.SendSpawnResponse(
          senderPeerId, runId, actionId, zdo.m_uid, zdo.GetPosition(), true,
          "spawned");
    } catch (Exception exception) {
      active.Write("ship_spawn_rejected", actionId,
          "sender=" + senderPeerId + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      if (SafeToken(runId, 80) && SafeToken(actionId, 80))
        active.SendSpawnResponse(
            senderPeerId, runId, actionId, ZDOID.None, Vector3.zero, false,
            exception.Message);
    }
  }

  void SendSpawnResponse(
      long targetPeerId,
      string runId,
      string actionId,
      ZDOID uid,
      Vector3 position,
      bool accepted,
      string result) {
    ZPackage response = new();
    response.Write(runId);
    response.Write(actionId);
    response.Write(uid);
    response.Write(position);
    response.Write(accepted);
    response.Write(result ?? string.Empty);
    response.SetPos(0);
    _routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            targetPeerId,
            ValheimRoutedRpcAdmissions.CutoverShipSpawnResponse,
            new object[] { response }));
  }

  static void HandleSpawnResponse(long senderPeerId, ZPackage package) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    ShipProbe probe = active?._probe;
    if (active == null || probe == null || probe.Terminal ||
        probe.Mode != "spawn" || IsServer()) return;
    package.SetPos(0);
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    Vector3 position = package.ReadVector3();
    bool accepted = package.ReadBool();
    string result = package.ReadString();
    if (package.GetPos() != package.Size() ||
        !string.Equals(runId, probe.RunId, StringComparison.Ordinal) ||
        !string.Equals(actionId, probe.ActionId, StringComparison.Ordinal) ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        !accepted || uid.IsNone() || !Finite(position)) {
      active.FailProbe("ship_spawn_response_invalid result=" + result);
      return;
    }
    probe.ShipId = uid;
    active.CompleteProbe("ship_spawn_accepted uid=" + uid +
        " position=" + Format(position));
  }

  bool SendTransfer(
      string actionId,
      string runId,
      ZDOID uid,
      long newOwner) {
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
            ValheimRoutedRpcAdmissions.CutoverShipTransferRequest,
            new object[] { request }));
  }

  static void HandleTransferRequest(long senderPeerId, ZPackage package) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    string runId = string.Empty;
    string actionId = string.Empty;
    ZDOID uid = ZDOID.None;
    long newOwner = 0;
    bool accepted = false;
    string result;
    try {
      package.SetPos(0);
      runId = package.ReadString();
      actionId = package.ReadString();
      uid = package.ReadZDOID();
      newOwner = package.ReadLong();
      ZDO zdo = ZDOMan.instance?.GetZDO(uid);
      if (package.GetPos() != package.Size() || !SafeToken(runId, 80) ||
          !SafeToken(actionId, 80) || uid.IsNone() || newOwner == 0 ||
          zdo == null || zdo.GetOwner() != senderPeerId ||
          !IsShipPrefab(zdo.GetPrefab()) ||
          !(ZNet.instance?.GetPeers()?.Any(peer => peer.m_uid == newOwner) ?? false))
        throw new InvalidOperationException("ship_transfer_authority_invalid");
      ZdoJournalCutoverRunner.ApplyCanonicalMutation(
          zdo, () => zdo.SetOwner(newOwner));
      accepted = true;
      result = "transferred";
      active.Write("ship_owner_transferred", actionId,
          "uid=" + uid + " old_owner=" + senderPeerId +
          " new_owner=" + newOwner);
    } catch (Exception exception) {
      result = exception.Message;
      active.Write("ship_transfer_rejected", actionId,
          "sender=" + senderPeerId + " uid=" + uid +
          " reason=" + exception.GetType().Name + ":" + exception.Message);
    }

    if (!SafeToken(runId, 80) || !SafeToken(actionId, 80)) return;
    ZPackage response = new();
    response.Write(runId);
    response.Write(actionId);
    response.Write(uid);
    response.Write(newOwner);
    response.Write(accepted);
    response.Write(result);
    response.SetPos(0);
    long responseTarget = accepted ? ZRoutedRpc.Everybody : senderPeerId;
    active._routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            responseTarget,
            ValheimRoutedRpcAdmissions.CutoverShipTransferResponse,
            new object[] { response }));
  }

  static void HandleTransferResponse(long senderPeerId, ZPackage package) {
    ShipCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    package.SetPos(0);
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    long newOwner = package.ReadLong();
    bool accepted = package.ReadBool();
    string result = package.ReadString();
    if (package.GetPos() != package.Size() ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        !SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
        uid.IsNone() || newOwner == 0) {
      active.FailMatchingTransferProbe(
          runId, actionId, newOwner,
          "ship_transfer_response_invalid result=" + result);
      return;
    }

    if (!accepted) {
      active.FailMatchingTransferProbe(
          runId, actionId, newOwner,
          "ship_transfer_rejected result=" + result);
      return;
    }

    ZDO zdo = ZDOMan.instance?.GetZDO(uid);
    if (zdo == null || !IsShipPrefab(zdo.GetPrefab()) ||
        !string.Equals(
            zdo.GetString(RunTagHash, string.Empty),
            runId,
            StringComparison.Ordinal)) {
      active.FailMatchingTransferProbe(
          runId, actionId, newOwner,
          "ship_transfer_replica_missing");
      return;
    }

    long previousOwner = zdo.GetOwner();
    zdo.SetOwner(newOwner);
    active.Write("ship_owner_applied", actionId,
        "uid=" + uid + " previous_owner=" + previousOwner +
        " new_owner=" + newOwner);

    ShipProbe probe = active._probe;
    if (probe == null || probe.Terminal || probe.Mode != "transfer" ||
        !string.Equals(runId, probe.RunId, StringComparison.Ordinal) ||
        !string.Equals(actionId, probe.ActionId, StringComparison.Ordinal) ||
        newOwner != probe.DesiredOwner)
      return;
    probe.ResponseReceived = true;
    probe.ShipId = uid;
  }

  void FailMatchingTransferProbe(
      string runId,
      string actionId,
      long newOwner,
      string detail) {
    ShipProbe probe = _probe;
    if (probe == null || probe.Terminal || probe.Mode != "transfer" ||
        !string.Equals(runId, probe.RunId, StringComparison.Ordinal) ||
        !string.Equals(actionId, probe.ActionId, StringComparison.Ordinal) ||
        (probe.DesiredOwner != 0 && newOwner != probe.DesiredOwner))
      return;
    FailProbe(detail);
  }

  static bool TryFindWaterSite(out Vector3 site, out string detail) {
    site = Vector3.zero;
    detail = string.Empty;
    WorldGenerator generator = WorldGenerator.instance;
    if (generator == null) {
      detail = "world_generator_missing";
      return false;
    }
    Vector3 origin = new(2211.0f, 0.0f, -69.0f);
    for (int ring = 1; ring <= 24; ring++) {
      for (int x = -ring; x <= ring; x++) {
        for (int z = -ring; z <= ring; z++) {
          if (Math.Abs(x) != ring && Math.Abs(z) != ring) continue;
          Vector3 candidate = origin + new Vector3(x * 64.0f, 0.0f, z * 64.0f);
          if (!DeepOcean(generator, candidate)) continue;
          site = new Vector3(candidate.x, 30.0f, candidate.z);
          detail = "ring=" + ring + " terrain_height=" +
              generator.GetHeight(candidate.x, candidate.z)
                  .ToString("0.##", CultureInfo.InvariantCulture);
          return true;
        }
      }
    }
    detail = "deep_ocean_site_not_found";
    return false;
  }

  static bool DeepOcean(WorldGenerator generator, Vector3 candidate) {
    Vector3[] samples = {
        Vector3.zero,
        new(16.0f, 0.0f, 0.0f),
        new(-16.0f, 0.0f, 0.0f),
        new(0.0f, 0.0f, 16.0f),
        new(0.0f, 0.0f, -16.0f)
    };
    foreach (Vector3 offset in samples) {
      float x = candidate.x + offset.x;
      float z = candidate.z + offset.z;
      if (generator.GetBiome(x, z, 0.02f, waterAlwaysOcean: true) !=
              Heightmap.Biome.Ocean ||
          generator.GetHeight(x, z) > 27.5f)
        return false;
    }
    return true;
  }

  static bool TryFindRunShip(
      string runId,
      out Ship ship,
      out ZNetView view) {
    ship = null;
    view = null;
    foreach (IMonoUpdater updater in Ship.Instances.ToArray()) {
      if (updater is not Ship candidate || candidate == null ||
          !candidate.gameObject.activeInHierarchy) continue;
      ZNetView candidateView = candidate.GetComponent<ZNetView>();
      ZDO zdo = candidateView?.GetZDO();
      if (zdo == null || !string.Equals(
              zdo.GetString(RunTagHash, string.Empty),
              runId,
              StringComparison.Ordinal)) continue;
      ship = candidate;
      view = candidateView;
      return true;
    }
    return false;
  }

  static ShipControlls FindControls(Ship ship) =>
      ship == null ? null :
      ship.GetComponent<ShipControlls>() ??
      ship.GetComponentInChildren<ShipControlls>(includeInactive: true);

  static bool IsShipPrefab(int prefabHash) {
    GameObject prefab = ZNetScene.instance?.GetPrefab(prefabHash);
    return prefab != null &&
        (prefab.GetComponent<Ship>() != null ||
         prefab.GetComponentInChildren<Ship>(includeInactive: true) != null);
  }

  static long FindRemotePeer() {
    Player local = Player.m_localPlayer;
    foreach (Player candidate in Player.GetAllPlayers()) {
      if (candidate == null || ReferenceEquals(candidate, local)) continue;
      ZDO zdo = candidate.GetComponent<ZNetView>()?.GetZDO();
      if (zdo != null && zdo.GetOwner() != 0) return zdo.GetOwner();
    }
    return 0;
  }

  static string ShipId(Ship ship) =>
      ship?.GetComponent<ZNetView>()?.GetZDO()?.m_uid.ToString() ?? "none";

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

  static string ProbeProgress(ShipProbe probe) =>
      "distance=" + probe.MaxDistance.ToString("0.###", CultureInfo.InvariantCulture)
      + " rudder=" + probe.MaxRudder.ToString("0.###", CultureInfo.InvariantCulture)
      + " speed_changed=" + probe.SpeedChanged
      + " control_granted=" + probe.ControlGranted;

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

  static string Role() =>
      ZNet.instance == null ? "starting" :
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

  sealed class ShipProbe {
    public string RunId;
    public string ActionId;
    public string Mode;
    public string Phase = "starting";
    public float StartedAt;
    public float DeadlineAt;
    public float Duration;
    public float NextAttemptAt;
    public float NextDiagnosticAt;
    public float ControlStartedAt;
    public Vector3 StartPosition;
    public float MaxDistance;
    public float MaxRudder;
    public long DesiredOwner;
    public ZDOID ShipId;
    public bool RequestSent;
    public bool ResponseReceived;
    public bool ControlGranted;
    public bool ReleaseSent;
    public bool ObservationStarted;
    public bool ObservedOwner;
    public bool SpeedChanged;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }
}

[HarmonyPatch(typeof(Ship), "UpdateOwner")]
static class ShipCutoverOwnerTransferPatch {
  [HarmonyPrefix]
  static void Prefix(Ship __instance, out long __state) {
    __state = __instance?.GetComponent<ZNetView>()?.GetZDO()?.GetOwner() ?? 0L;
  }

  [HarmonyPostfix]
  static void Postfix(Ship __instance, long __state) {
    ZDO zdo = __instance?.GetComponent<ZNetView>()?.GetZDO();
    long next = zdo?.GetOwner() ?? 0L;
    if (__state == 0 || next == 0 || next == __state ||
        __state != ZDOMan.GetSessionID()) return;
    ShipCutoverRunner.NotifyVanillaOwnerTransfer(zdo.m_uid, next);
  }
}
