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
using System.Threading.Tasks;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// C3's semantic ZDO path. The server captures a complete body at the mutation boundary, before
/// Valheim chooses a peer's CreateSyncList. Enrolled clients declare explicit zone interest,
/// poll Lumberjacks snapshots/deltas/tombstones off-thread, and apply them directly on Unity's
/// main thread. No delivery is sourced by CreateSyncList and no apply calls RPC_ZDOData.
/// </summary>
public sealed class ZdoJournalCutoverRunner : IDisposable {
  public const string RequestMethod = RoutedRpcCutoverRunner.JournalRequestMethod;
  public const string ReceiptFileName = "zdo-journal-cutover.jsonl";
  public const string ProbeTagName = "ComfyNetworkSense_C3Tag";
  public const string ProbeValueName = "ComfyNetworkSense_C3Value";

  const int MaxBodyBytes = 4 * 1024 * 1024;
  const int MaxResponseBytes = 16 * 1024 * 1024;
  const int HttpTimeoutMs = 5000;
  const int ResponseDeadlineMs = 5000;
  const float InterestSeconds = 2.0f;
  const float WorkerSeconds = 0.20f;
  const int ApplyPerUpdate = 32;
  const int TeleportApplyPerUpdate = 256;
  const int BulkProgressInterval = 1024;
  // Vanilla's active+distant CreateSyncList square is two zones from the
  // player. Keep the live stream equally bounded. C3 alone expands to three
  // zones to preserve its explicit outside-native-ring negative control.
  const int WorldInterestRadiusZones = 2;
  const int ProbeInterestRadiusZones = 3;

  static readonly int ProbeTagHash = ProbeTagName.GetStableHashCode();
  [ThreadStatic] static int _canonicalMutationBatchDepth;
  static readonly int ProbeValueHash = ProbeValueName.GetStableHashCode();
  static readonly int ProbePrefabHash =
      "ComfyNetworkSense_C3Probe".GetStableHashCode();
  static readonly int PlayerPrefabHash = "Player".GetStableHashCode();
  static readonly HashSet<int> PortalPrefabHashes = new() {
      "portal_wood".GetStableHashCode(),
      "portal".GetStableHashCode(),
      "portal_stone".GetStableHashCode()
  };
  static readonly MethodInfo CreateNewZdoMethod =
      AccessTools.Method(
          typeof(ZDOMan), "CreateNewZDO",
          new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
  static readonly MethodInfo HandleDestroyedZdoMethod =
      AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO",
          new[] { typeof(ZDOID) });

  static ZdoJournalCutoverRunner _active;

  readonly ConcurrentQueue<JournalObject> _serverOutbound = new();
  readonly ConcurrentQueue<JournalDelivery> _clientInbound = new();
  readonly ConcurrentQueue<long> _clientAcks = new();
  readonly ConcurrentQueue<CanonicalAck> _canonicalAcks = new();
  readonly ConcurrentQueue<CanonicalEvent> _canonicalEvents = new();
  readonly Dictionary<string, long> _appliedRevisions =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, ServerInterest> _serverInterests =
      new(StringComparer.Ordinal);
  readonly TelemetryLogWriter _writer = new();
  readonly object _gate = new();
  readonly LumberjacksGameSessionRunner _gameSession;

  ZRoutedRpc _registeredRpc;
  DriveState _drive;
  ClientProbe _probe;
  string _worldEpoch = string.Empty;
  string _runId = string.Empty;
  string _endpoint = string.Empty;
  string _recipientId = string.Empty;
  string _watchedZdo = string.Empty;
  string _role = "starting";
  float _nextWorker;
  float _nextInterest;
  int _workerInFlight;
  int _interestedRecipients;
  int _interestZoneX;
  int _interestZoneY;
  int _registeredInterestZoneX;
  int _registeredInterestZoneY;
  int _registeredInterestRadius;
  int _prefetchInterestZoneX;
  int _prefetchInterestZoneY;
  long _interestZoneEpoch;
  bool _hasRegisteredInterest;
  bool _hasPrefetchInterest;
  long _sourceSequence;
  long _createSyncLists;
  long _nativeCandidates;
  long _nativeRpcDeliveries;
  long _canonicalDeliveriesBanked;
  long _canonicalAcksQueued;
  long _serverSnapshotsQueued;
  long _canonicalMutationsQueued;
  long _canonicalMutationBackpressure;
  long _snapshotsApplied;
  long _deltasApplied;
  long _receiptSnapshotArrivals;
  long _tombstonesApplied;
  long _staleRejected;
  long _malformedRejected;
  long _superseded;
  long _applyFailures;
  int _nextThrottledApplyTick;
  bool _disposed;

  public ZdoJournalCutoverRunner(LumberjacksGameSessionRunner gameSession) {
    _gameSession = gameSession ?? throw new ArgumentNullException(nameof(gameSession));
    ZdoJournalCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    EnsureHandler();
    if (!Enabled()) return;
    RefreshContext();
    bool server = IsServer();
    if (string.IsNullOrWhiteSpace(_runId)
        || string.IsNullOrWhiteSpace(_worldEpoch)
        || (!CanonicalEnabled() && string.IsNullOrWhiteSpace(_endpoint))
        || (!server && string.IsNullOrWhiteSpace(_recipientId))) return;

    DrainCanonicalEvents();

    bool clientReady = false;
    bool bootstrapInterestReady = false;
    Vector2i bootstrapZone = default;
    if (server) {
      DriveServer(now);
    } else {
      DrainClient();
      EvaluateProbe();
      clientReady =
          Player.m_localPlayer != null &&
          ZNet.instance != null &&
          (ZNet.instance.GetPeers()?.Count ?? 0) > 0;
      bootstrapInterestReady =
          !clientReady &&
          ZNet.instance != null &&
          WorldZoneCutoverRunner.TryGetInitialZone(out bootstrapZone);
      if (!clientReady && !bootstrapInterestReady) return;
    }

    bool registerInterest = false;
    Vector2i currentZone = default;
    int interestRadius = WorldInterestRadiusZones;
    if (!server && (clientReady || bootstrapInterestReady)) {
      Vector2i playerZone = clientReady
          ? ZoneSystem.GetZone(
              ((Component) Player.m_localPlayer).transform.position)
          : bootstrapZone;
      if (clientReady && _hasPrefetchInterest &&
          playerZone.x == _prefetchInterestZoneX &&
          playerZone.y == _prefetchInterestZoneY &&
          !Player.m_localPlayer.IsTeleporting())
        _hasPrefetchInterest = false;
      currentZone = _hasPrefetchInterest
          ? new Vector2i(_prefetchInterestZoneX, _prefetchInterestZoneY)
          : playerZone;
      interestRadius = _probe != null && !_probe.Terminal
          ? ProbeInterestRadiusZones
          : WorldInterestRadiusZones;
      registerInterest =
          now >= _nextInterest &&
          (!_hasRegisteredInterest ||
           currentZone.x != _registeredInterestZoneX ||
           currentZone.y != _registeredInterestZoneY ||
           interestRadius != _registeredInterestRadius);
    }
    if (registerInterest) {
      Volatile.Write(ref _interestZoneX, currentZone.x);
      Volatile.Write(ref _interestZoneY, currentZone.y);
    }
    bool refreshInterest = registerInterest &&
        ZdoJournalInterestPolicy.ShouldRefreshProcessRegistration(
            _hasRegisteredInterest);

    if (CanonicalEnabled()) {
      if (server) {
        QueueCanonicalMutations();
      } else {
        if (registerInterest &&
            QueueCanonicalInterest(interestRadius, refreshInterest))
          MarkInterestRegistered(currentZone, interestRadius, now);
        FlushCanonicalAcks();
      }
      return;
    }

    if (now >= _nextWorker
        && Interlocked.CompareExchange(ref _workerInFlight, 1, 0) == 0) {
      try {
        _nextWorker = now + WorkerSeconds;
        _ = Task.Run(() => Work(
            server, registerInterest, refreshInterest));
        if (registerInterest) MarkInterestRegistered(
            currentZone,
            _probe != null && !_probe.Terminal
                ? ProbeInterestRadiusZones
                : WorldInterestRadiusZones,
            now);
      } catch {
        Interlocked.Exchange(ref _workerInFlight, 0);
        throw;
      }
    }
  }

  public bool BeginProbe(
      string actionId, string mode, float deadlineSeconds, out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) || mode is not ("drive" or "observe")) {
      detail = "zdo_journal_probe_parameters_invalid";
      return false;
    }
    if (!Enabled()) {
      detail = "zdo_journal_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        ZRoutedRpc.instance == null) {
      detail = "zdo_journal_client_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_zdo_journal_probe_active";
      return false;
    }
    _probe = new() {
        ActionId = actionId,
        Mode = mode,
        DeadlineAt = Time.unscaledTime + Mathf.Clamp(deadlineSeconds, 5.0f, 300.0f),
        Snapshots = Interlocked.Read(ref _snapshotsApplied),
        Deltas = Interlocked.Read(ref _deltasApplied),
        Tombstones = Interlocked.Read(ref _tombstonesApplied),
        Stale = Interlocked.Read(ref _staleRejected),
        Malformed = Interlocked.Read(ref _malformedRejected),
        ReceiptArrivals = Interlocked.Read(ref _receiptSnapshotArrivals)
    };
    Write("probe_started", actionId, "mode=" + mode);
    if (mode == "observe") return true;

    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) {
      detail = "zdo_journal_server_peer_missing";
      _probe.Terminal = true;
      return false;
    }
    Vector3 origin = ((Component) Player.m_localPlayer).transform.position;
    ZPackage request = new();
    request.Write(_runId);
    request.Write(actionId);
    request.Write("drive");
    request.Write(origin);
    request.SetPos(0);
    ZRoutedRpc.instance.InvokeRoutedRPC(
        server.m_uid, RequestMethod, new object[] { request });
    return true;
  }

  public bool TryGetProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    if (_probe == null ||
        !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "zdo_journal_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  public static void EnqueueCanonicalDelivery(string json) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !CanonicalEnabled() || IsServer()) return;
    CanonicalDeliveryEnvelope envelope =
        Deserialize<CanonicalDeliveryEnvelope>(json);
    JournalDelivery delivery = envelope?.payload;
    if (envelope == null || envelope.seq <= 0 ||
        delivery?.@object == null || delivery.seq <= 0)
      throw new InvalidDataException("canonical_zdo_delivery_invalid");
    delivery.reliable_seq = envelope.seq;
    active._clientInbound.Enqueue(delivery);
    long banked = Interlocked.Increment(
        ref active._canonicalDeliveriesBanked);
    if (delivery.@object.receipt_required ||
        banked % BulkProgressInterval == 0)
      active.Write("canonical_delivery_progress",
          active._probe?.ActionId ?? "client",
          "banked=" + banked
          + " inbound=" + active._clientInbound.Count
          + " reliable_seq=" + envelope.seq
          + " delivery_seq=" + delivery.seq
          + " logical_peer_id=" + active._gameSession.LogicalPeerId);
  }

  public static void EnqueueCanonicalMutationReceipt(
      long reliableSequence,
      string runId,
      string worldEpoch,
      long sourceSequence,
      bool accepted,
      string result,
      long recipients) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !CanonicalEnabled() || !IsServer()) return;
    active._canonicalEvents.Enqueue(new CanonicalEvent {
        Kind = "mutation_receipt",
        ReliableSequence = reliableSequence,
        RunId = runId,
        WorldEpoch = worldEpoch,
        SourceSequence = sourceSequence,
        Accepted = accepted,
        Result = result,
        Count = recipients
    });
  }

  public static void EnqueueCanonicalInterestReceipt(
      long reliableSequence,
      string runId,
      string worldEpoch,
      string logicalPeerId,
      bool accepted,
      string result,
      long snapshots,
      long pending) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !CanonicalEnabled() || IsServer()) return;
    active._canonicalEvents.Enqueue(new CanonicalEvent {
        Kind = "interest_receipt",
        ReliableSequence = reliableSequence,
        RunId = runId,
        WorldEpoch = worldEpoch,
        LogicalPeerId = logicalPeerId,
        Count = snapshots,
        Pending = pending,
        Accepted = accepted,
        Result = result
    });
  }

  public static void EnqueueCanonicalInterestStatus(
      long reliableSequence,
      string runId,
      string worldEpoch,
      string logicalPeerId,
      long zoneEpoch,
      int zoneX,
      int zoneY,
      int radiusZones,
      long interested,
      long pending,
      long durable) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !CanonicalEnabled() || !IsServer()) return;
    active._canonicalEvents.Enqueue(new CanonicalEvent {
        Kind = "interest_status",
        ReliableSequence = reliableSequence,
        RunId = runId,
        WorldEpoch = worldEpoch,
        LogicalPeerId = logicalPeerId,
        ZoneEpoch = zoneEpoch,
        ZoneX = zoneX,
        ZoneY = zoneY,
        RadiusZones = radiusZones,
        Count = interested,
        Pending = pending,
        Durable = durable,
        Accepted = true
    });
  }

  public static void CaptureMutation(ZDO zdo) =>
      CaptureMutation(zdo, receiptRequired: false);

  static void CaptureMutation(ZDO zdo, bool receiptRequired) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer() || zdo == null
        || _canonicalMutationBatchDepth > 0) return;
    try {
      if (!active.ServerInterestContains(zdo.GetSector())) return;
      JournalObject snapshot = active.Snapshot(zdo, tombstone: false);
      snapshot.receipt_required = receiptRequired;
      active._serverOutbound.Enqueue(snapshot);
    } catch (Exception exception) {
      active.Write("capture_failed", "mutation",
          exception.GetType().Name + ":" + exception.Message);
    }
  }

  /// <summary>
  /// Applies one authenticated client-owned object snapshot to the server's
  /// canonical ZDO while emitting one journal object, not one full object for
  /// every individual position/rotation/velocity field mutation.
  /// </summary>
  public static void ApplyCanonicalMutation(ZDO zdo, Action apply) =>
      ApplyCanonicalMutation(zdo, apply, receiptRequired: false);

  /// <summary>
  /// Applies one canonical mutation and requests an observable Gateway receipt
  /// plus per-recipient delivery telemetry. Physical cutover canaries use this
  /// overload for the exact objects whose fan-out is part of the verdict.
  /// </summary>
  public static void ApplyCanonicalMutationWithReceipt(
      ZDO zdo, Action apply) =>
      ApplyCanonicalMutation(zdo, apply, receiptRequired: true);

  static void ApplyCanonicalMutation(
      ZDO zdo, Action apply, bool receiptRequired) {
    if (zdo == null) throw new ArgumentNullException(nameof(zdo));
    if (apply == null) throw new ArgumentNullException(nameof(apply));
    _canonicalMutationBatchDepth++;
    try {
      apply();
    } finally {
      _canonicalMutationBatchDepth--;
    }
    CaptureMutation(zdo, receiptRequired);
  }

  public static void NotifyHardRelocation(Vector3 target) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || IsServer() || !Finite(target))
      return;
    Vector2i zone = ZoneSystem.GetZone(target);
    active._prefetchInterestZoneX = zone.x;
    active._prefetchInterestZoneY = zone.y;
    active._hasPrefetchInterest = true;
    active._nextInterest = 0.0f;
    active.Write("hard_relocation_interest_requested", "client",
        "zone=" + zone.x + "," + zone.y
        + " target=" + target.x.ToString("0.##", CultureInfo.InvariantCulture)
        + "," + target.y.ToString("0.##", CultureInfo.InvariantCulture)
        + "," + target.z.ToString("0.##", CultureInfo.InvariantCulture));
  }

  /// A reincarnated Gateway process keeps only the durable object journal;
  /// every in-memory interest set is gone, and interest is only re-sent when
  /// the player's zone changes. Without this re-arm, AoI delivery never
  /// resumes after reincarnation and the next dependent probe starves in
  /// await_target (run full26).
  public static void NotifySessionReincarnated() {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || IsServer()) return;
    active._hasRegisteredInterest = false;
    active._nextInterest = 0.0f;
    active.Write("reincarnation_interest_rearm_requested", "client",
        "last_zone=" + active._registeredInterestZoneX
        + "," + active._registeredInterestZoneY);
  }

  public static void NotifyWorldEpochChanged(
      string previousWorldEpoch, string currentWorldEpoch) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer() ||
        string.Equals(previousWorldEpoch, currentWorldEpoch,
            StringComparison.Ordinal)) return;
    active.ResetClientEpochState();
    active._worldEpoch = currentWorldEpoch ?? string.Empty;
    active.Write("world_session_epoch_changed", "client",
        "previous=" + previousWorldEpoch
        + " current=" + currentWorldEpoch
        + " stale_deliveries_discarded=true");
  }

  public static void CaptureTombstone(ZDOID uid) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer() ||
        ZDOMan.instance == null) return;
    try {
      ZDO zdo = ZDOMan.instance.GetZDO(uid);
      if (zdo == null) return;
      if (!active.ServerInterestContains(zdo.GetSector())) return;
      JournalObject tombstone = active.Snapshot(zdo, tombstone: true);
      tombstone.object_revision++;
      tombstone.source_seq = Interlocked.Increment(ref active._sourceSequence);
      active._serverOutbound.Enqueue(tombstone);
      active.Write("tombstone_captured", "server",
          "uid=" + uid + " object_revision=" + tombstone.object_revision);
    } catch (Exception exception) {
      active.Write("capture_failed", "tombstone",
          exception.GetType().Name + ":" + exception.Message);
    }
  }

  public static void ObserveCreateSyncList(List<ZDO> toSync) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    Interlocked.Increment(ref active._createSyncLists);
    if (toSync == null) return;
    foreach (ZDO zdo in toSync) {
      if (zdo == null) continue;
      try {
        if (string.Equals(
                zdo.GetString(ProbeTagHash, string.Empty),
                active._runId, StringComparison.Ordinal))
          Interlocked.Increment(ref active._nativeCandidates);
      } catch { }
    }
  }

  public static void ObserveNativeZdoData(ZPackage package) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || IsServer() || package == null ||
        string.IsNullOrWhiteSpace(active._watchedZdo)) return;
    try {
      ZPackage copy = new(package.GetArray());
      int invalid = copy.ReadInt();
      if (invalid is < 0 or > 100000) return;
      for (int i = 0; i < invalid; i++) copy.ReadZDOID();
      while (copy.GetPos() < copy.Size()) {
        ZDOID uid = copy.ReadZDOID();
        if (uid.IsNone()) break;
        copy.ReadUShort();
        copy.ReadUInt();
        copy.ReadLong();
        copy.ReadVector3();
        ZPackage body = copy.ReadPackage();
        if (!string.Equals(uid.ToString(), active._watchedZdo,
                StringComparison.Ordinal) &&
            !BodyHasProbeTag(body, active._runId)) continue;
        active._watchedZdo = uid.ToString();
        Interlocked.Increment(ref active._nativeRpcDeliveries);
        active.Write("native_rpc_zdo_data_tripwire", "client",
            "uid=" + uid);
      }
    } catch {
      // Observe-only parser. A malformed native packet remains the native handler's decision.
    }
  }

  void EnsureHandler() {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
    rpc.Register<ZPackage>(RequestMethod, HandleDriveRequest);
    _registeredRpc = rpc;
  }

  static void HandleDriveRequest(long senderPeerId, ZPackage package) {
    ZdoJournalCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    try {
      package.SetPos(0);
      string runId = package.ReadString();
      string actionId = package.ReadString();
      string mode = package.ReadString();
      Vector3 origin = package.ReadVector3();
      if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
          mode != "drive" || !Finite(origin) ||
          !string.Equals(runId, active._runId, StringComparison.Ordinal))
        throw new InvalidDataException("drive_request_invalid");
      active.StartDrive(runId, actionId, origin);
    } catch (Exception exception) {
      active.Write("drive_request_rejected", "server",
          exception.GetType().Name + ":" + exception.Message);
    }
  }

  void StartDrive(string runId, string actionId, Vector3 origin) {
    if (_drive != null && !_drive.Complete) {
      Write("drive_request_duplicate", actionId, "existing_drive_active");
      return;
    }
    // Three zones east: inside Lumberjacks' explicit interest but outside Valheim's
    // near/distant CreateSyncList rings. This is the acceptance object's negative control.
    Vector3 position = origin + new Vector3(192.0f, 0.0f, 0.0f);
    ZDO zdo = ZDOMan.instance.CreateNewZDO(position, ProbePrefabHash);
    _watchedZdo = zdo.m_uid.ToString();
    _drive = new() {
        RunId = runId,
        ActionId = actionId,
        Zdo = zdo,
        Phase = "waiting_for_second_interest",
        NextAt = Time.unscaledTime + 1.0f
    };
    zdo.Set(ProbeTagHash, runId);
    zdo.Set(ProbeValueHash, 1);
    Write("drive_created", actionId,
        "uid=" + zdo.m_uid + " zone=" + zdo.GetSector().x + "," + zdo.GetSector().y
        + " value=1");
  }

  void DriveServer(float now) {
    if (_drive == null || _drive.Complete || now < _drive.NextAt) return;
    if (_drive.Phase == "waiting_for_second_interest") {
      if (Volatile.Read(ref _interestedRecipients) < 2) {
        _drive.NextAt = now + 0.5f;
        return;
      }
      JournalObject current = Snapshot(_drive.Zdo, tombstone: false);
      current.receipt_required = true;
      // The negative controls must not satisfy the receipt-arrival check.
      JournalObject stale = Clone(current);
      stale.delivery_only = true;
      stale.receipt_required = false;
      stale.source_seq = Interlocked.Increment(ref _sourceSequence);
      stale.object_revision = Math.Max(1, current.object_revision - 1);
      _serverOutbound.Enqueue(stale);

      JournalObject malformed = Clone(current);
      malformed.delivery_only = true;
      malformed.receipt_required = false;
      malformed.source_seq = Interlocked.Increment(ref _sourceSequence);
      malformed.object_revision = current.object_revision + 1;
      malformed.body_b64 = "%%%C3-MALFORMED%%%";
      _serverOutbound.Enqueue(malformed);

      // The valid receipt-required full-body delivery was constructed but never enqueued —
      // the observe verdict historically passed on Gateway interest-snapshot replay of an
      // area the bank happened to hold (bank WARMTH, not the drive protocol). A cold bank
      // (routine since the wall-11 restart-wipe rule) made that structurally unsatisfiable
      // (full38: snapshot=0 receipt_snapshot_arrivals=0). Deliver it explicitly; it rides
      // the mutation lane as kind=delta and the client counts its validated arrival on
      // whatever branch it lands in.
      current.delivery_only = true;
      current.source_seq = Interlocked.Increment(ref _sourceSequence);
      _serverOutbound.Enqueue(current);

      _drive.Zdo.Set(ProbeValueHash, 2);
      _drive.Phase = "waiting_to_destroy";
      _drive.NextAt = now + 3.0f;
      Write("drive_faults_and_valid_queued", _drive.ActionId,
          "interested_recipients=" + _interestedRecipients + " value=2");
      return;
    }
    if (_drive.Phase == "waiting_to_destroy") {
      ZDOID uid = _drive.Zdo.m_uid;
      HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
      _drive.Complete = true;
      Write("drive_complete", _drive.ActionId,
          "uid=" + uid
          + " create_sync_lists=" + Interlocked.Read(ref _createSyncLists)
          + " native_candidates=" + Interlocked.Read(ref _nativeCandidates));
    }
  }

  JournalObject Snapshot(ZDO zdo, bool tombstone) {
    Vector3 position = zdo.GetPosition();
    Vector2i zone = zdo.GetSector();
    ZPackage body = new();
    if (!tombstone) zdo.Serialize(body);
    return new() {
        run_id = _runId,
        world_epoch = _worldEpoch,
        zone_epoch = 1,
        source_seq = Interlocked.Increment(ref _sourceSequence),
        object_revision = Revision(zdo.DataRevision, zdo.OwnerRevision),
        uid_user = zdo.m_uid.UserID,
        uid_id = zdo.m_uid.ID,
        prefab = zdo.GetPrefab(),
        owner = zdo.GetOwner(),
        owner_rev = zdo.OwnerRevision,
        data_rev = zdo.DataRevision,
        position_x = position.x,
        position_y = position.y,
        position_z = position.z,
        zone_x = zone.x,
        zone_y = zone.y,
        body_b64 = tombstone ? string.Empty : body.GetBase64(),
        tombstone = tombstone,
        delivery_only = false,
        receipt_required = false,
        delivery_recipient_id = string.Empty
    };
  }

  void QueueInterestSnapshot(CanonicalEvent item) {
    if (!SafeToken(item.LogicalPeerId, 80) || item.ZoneEpoch < 1 ||
        item.RadiusZones is < 0 or > ProbeInterestRadiusZones ||
        ZDOMan.instance == null)
      throw new InvalidDataException("canonical_interest_snapshot_invalid");

    if (_serverInterests.TryGetValue(
            item.LogicalPeerId, out ServerInterest existing) &&
        string.Equals(existing.RunId, item.RunId, StringComparison.Ordinal) &&
        existing.ZoneEpoch == item.ZoneEpoch &&
        existing.ZoneX == item.ZoneX && existing.ZoneY == item.ZoneY &&
        existing.RadiusZones == item.RadiusZones &&
        item.Durable > 0) {
      Write("interest_snapshot_duplicate", "server",
          "recipient_id=" + item.LogicalPeerId
          + " zone_epoch=" + item.ZoneEpoch);
      return;
    }

    _serverInterests[item.LogicalPeerId] = new() {
        RunId = item.RunId,
        ZoneEpoch = item.ZoneEpoch,
        ZoneX = item.ZoneX,
        ZoneY = item.ZoneY,
        RadiusZones = item.RadiusZones
    };

    List<ZDO> selected = new();
    ZDOMan.instance.FindSectorObjects(
        new Vector2i(item.ZoneX, item.ZoneY),
        item.RadiusZones, 0, selected);
    Vector3 center =
        ZoneSystem.GetZonePos(new Vector2i(item.ZoneX, item.ZoneY));
    List<ZDO> ordered = selected
        .Where(zdo => zdo != null && zdo.GetPrefab() != 0 &&
            zdo.GetPrefab() != PlayerPrefabHash)
        .GroupBy(zdo => zdo.m_uid)
        .Select(group => group.First())
        .OrderBy(zdo => PortalPrefabHashes.Contains(zdo.GetPrefab()) ? 0 : 1)
        .ThenBy(zdo => (zdo.GetPosition() - center).sqrMagnitude)
        .ToList();
    HashSet<ZDOID> selectedIds = new(ordered.Select(zdo => zdo.m_uid));
    HashSet<ZDOID> dependencyIds = new();
    int linkedDependencies = 0;
    foreach (ZDO portal in ordered.Where(
                 zdo => PortalPrefabHashes.Contains(zdo.GetPrefab()))) {
      _serverOutbound.Enqueue(Snapshot(portal, tombstone: false));
      ZDOID targetId = portal.GetConnectionZDOID(
          ZDOExtraData.ConnectionType.Portal);
      if (targetId == ZDOID.None || selectedIds.Contains(targetId) ||
          !dependencyIds.Add(targetId))
        continue;
      ZDO target = ZDOMan.instance.GetZDO(targetId);
      if (target == null || target.GetPrefab() == 0) continue;
      JournalObject dependency = Snapshot(target, tombstone: false);
      dependency.delivery_recipient_id = item.LogicalPeerId;
      _serverOutbound.Enqueue(dependency);
      linkedDependencies++;
    }
    foreach (ZDO zdo in ordered.Where(
                 zdo => !PortalPrefabHashes.Contains(zdo.GetPrefab())))
      _serverOutbound.Enqueue(Snapshot(zdo, tombstone: false));
    long queued = Interlocked.Add(
        ref _serverSnapshotsQueued, ordered.Count + linkedDependencies);
    Write("interest_snapshot_queued", "server",
        "recipient_id=" + item.LogicalPeerId
        + " zone_epoch=" + item.ZoneEpoch
        + " zone=" + item.ZoneX + "," + item.ZoneY
        + " radius_zones=" + item.RadiusZones
        + " objects=" + ordered.Count
        + " portals=" +
        ordered.Count(zdo => PortalPrefabHashes.Contains(zdo.GetPrefab()))
        + " linked_dependencies=" + linkedDependencies
        + " server_outbound=" + _serverOutbound.Count
        + " queued_total=" + queued);
  }

  bool ServerInterestContains(Vector2i zone) =>
      _serverInterests.Values.Any(value =>
          string.Equals(value.RunId, _runId, StringComparison.Ordinal) &&
          Math.Abs(value.ZoneX - zone.x) <= value.RadiusZones &&
          Math.Abs(value.ZoneY - zone.y) <= value.RadiusZones);

  void QueueCanonicalMutations() {
    int sent = 0;
    while (sent++ < 32 && _serverOutbound.TryDequeue(out JournalObject value)) {
      string payload = JsonLineSerializer.Serialize(ToDictionary(value));
      if (payload.Length < 2 || payload[0] != '{' ||
          payload[payload.Length - 1] != '}') {
        _serverOutbound.Enqueue(value);
        throw new InvalidDataException("canonical_mutation_payload_invalid");
      }
      if (!_gameSession.TryQueueZdoJournalMutation(
              payload.Substring(1, payload.Length - 2))) {
        _serverOutbound.Enqueue(value);
        long refused = Interlocked.Increment(
            ref _canonicalMutationBackpressure);
        if (refused == 1 || refused % 128 == 0) {
          IDictionary<string, object> session = _gameSession.Snapshot();
          Write("canonical_mutation_backpressure", "server",
              "refused=" + refused
              + " source_seq=" + value.source_seq
              + " payload_bytes=" + payload.Length
              + " server_outbound=" + _serverOutbound.Count
              + " session_state=" + session["state"]
              + " websocket_connected=" + session["websocket_connected"]
              + " session_outbound=" + session["outbound_queue_depth"]);
        }
        break;
      }
      long queued = Interlocked.Increment(
          ref _canonicalMutationsQueued);
      if (queued == 1 || queued % BulkProgressInterval == 0)
        Write("canonical_mutation_queue_progress", "server",
            "queued=" + queued
            + " source_seq=" + value.source_seq
            + " payload_bytes=" + payload.Length
            + " server_outbound=" + _serverOutbound.Count);
      if (value.receipt_required || value.tombstone || value.delivery_only)
        Write("mutation_posted", "server",
            "transport=canonical_session source_seq=" + value.source_seq
            + " object_revision=" + value.object_revision
            + " uid=" + value.uid_user + ":" + value.uid_id
            + " tombstone=" + value.tombstone
            + " delivery_only=" + value.delivery_only
            + " receipt_required=" + value.receipt_required);
    }
  }

  bool QueueCanonicalInterest(int radiusZones, bool refresh) {
    int zoneX = Volatile.Read(ref _interestZoneX);
    int zoneY = Volatile.Read(ref _interestZoneY);
    long zoneEpoch = Interlocked.Read(ref _interestZoneEpoch) + 1;
    string fields =
        "\"run_id\":\"" + Escape(_runId)
        + "\",\"world_epoch\":\"" + Escape(_worldEpoch)
        + "\",\"zone_epoch\":" + zoneEpoch.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_x\":" + zoneX.ToString(CultureInfo.InvariantCulture)
        + ",\"zone_y\":" + zoneY.ToString(CultureInfo.InvariantCulture)
        + ",\"radius_zones\":"
        + radiusZones.ToString(CultureInfo.InvariantCulture)
        + ",\"refresh\":" +
            refresh.ToString().ToLowerInvariant();
    if (!_gameSession.TryQueueZdoJournalInterest(fields)) return false;
    Interlocked.Exchange(ref _interestZoneEpoch, zoneEpoch);
    Write("canonical_interest_queued", _probe?.ActionId ?? "client",
        "zone_epoch=" + zoneEpoch + " zone=" + zoneX + "," + zoneY
        + " radius_zones=" + radiusZones
        + " refresh=" + refresh.ToString().ToLowerInvariant()
        + " logical_peer_id=" + _gameSession.LogicalPeerId);
    return true;
  }

  void MarkInterestRegistered(Vector2i zone, int radiusZones, float now) {
    _registeredInterestZoneX = zone.x;
    _registeredInterestZoneY = zone.y;
    _registeredInterestRadius = radiusZones;
    _hasRegisteredInterest = true;
    _nextInterest = now + InterestSeconds;
  }

  void FlushCanonicalAcks() {
    int count = 0;
    while (count++ < 64 && _canonicalAcks.TryDequeue(out CanonicalAck ack)) {
      if (_gameSession.QueueZdoJournalAck(
              ack.WorldEpoch, ack.JournalSequence, ack.ReliableSequence)) {
        long queued = Interlocked.Increment(ref _canonicalAcksQueued);
        if ((_probe != null && !_probe.Terminal) ||
            queued % BulkProgressInterval == 0)
          Write("canonical_ack_progress", _probe?.ActionId ?? "client",
              "queued=" + queued
              + " remaining=" + _canonicalAcks.Count
              + " delivery_seq=" + ack.JournalSequence
              + " reliable_seq=" + ack.ReliableSequence);
        continue;
      }
      _canonicalAcks.Enqueue(ack);
      break;
    }
  }

  void DrainCanonicalEvents() {
    int count = 0;
    while (count++ < 64 && _canonicalEvents.TryDequeue(out CanonicalEvent item)) {
      if (!string.Equals(item.RunId, _runId, StringComparison.Ordinal) ||
          !string.Equals(item.WorldEpoch, _worldEpoch, StringComparison.Ordinal)) {
        Write("canonical_control_rejected", item.Kind,
            "scope_mismatch reliable_seq=" + item.ReliableSequence);
        continue;
      }
      switch (item.Kind) {
        case "mutation_receipt":
          Write(item.Accepted
                  ? "canonical_mutation_accepted"
                  : "canonical_mutation_rejected",
              "server",
              "source_seq=" + item.SourceSequence
              + " result=" + item.Result
              + " recipients=" + item.Count);
          break;
        case "interest_receipt":
          if (!string.Equals(
                  item.LogicalPeerId, _gameSession.LogicalPeerId,
                  StringComparison.Ordinal)) {
            Write("canonical_control_rejected", "client",
                "logical_peer_mismatch");
            continue;
          }
          Write(item.Accepted
                  ? "interest_registered"
                  : "interest_rejected",
              "client",
              "transport=canonical_session logical_peer_id=" + item.LogicalPeerId
              + " result=" + item.Result
              + " snapshot_count=" + item.Count
              + " pending=" + item.Pending);
          break;
        case "interest_status":
          Volatile.Write(ref _interestedRecipients, checked((int) item.Count));
          QueueInterestSnapshot(item);
          Write("canonical_interest_status", "server",
              "recipient_id=" + item.LogicalPeerId
              + " zone_epoch=" + item.ZoneEpoch
              + " zone=" + item.ZoneX + "," + item.ZoneY
              + " radius_zones=" + item.RadiusZones
              + " interested_recipients=" + item.Count
              + " pending=" + item.Pending
              + " durable_objects=" + item.Durable);
          break;
      }
      _gameSession.QueueReliableAck(item.ReliableSequence);
    }
  }

  void Work(bool server, bool registerInterest, bool refreshInterest) {
    try {
      if (server) {
        PostServerMutations();
        PollRunStatus();
      } else {
        if (registerInterest) PostInterest(refreshInterest);
        FlushAcks();
        PollClient();
      }
    } catch (Exception exception) {
      Write("http_cycle_failed", server ? "server" : "client",
          exception.GetType().Name + ":" + exception.Message);
    } finally {
      Interlocked.Exchange(ref _workerInFlight, 0);
    }
  }

  void PostServerMutations() {
    int sent = 0;
    while (sent++ < 32 && _serverOutbound.TryDequeue(out JournalObject value)) {
      try {
        Send(_endpoint + "/valheim/zdo-journal/mutations", "POST",
            JsonLineSerializer.Serialize(ToDictionary(value)), credentials: false);
        Write("mutation_posted", "server",
            "source_seq=" + value.source_seq
            + " object_revision=" + value.object_revision
            + " uid=" + value.uid_user + ":" + value.uid_id
            + " tombstone=" + value.tombstone
            + " delivery_only=" + value.delivery_only
            + " receipt_required=" + value.receipt_required);
      } catch {
        _serverOutbound.Enqueue(value);
        throw;
      }
    }
  }

  void PollRunStatus() {
    if (_drive == null || _drive.Complete) return;
    string response = Send(
        _endpoint + "/valheim/zdo-journal/status/" + _runId + "/" + _worldEpoch,
        "GET", string.Empty, credentials: false);
    Match match = Regex.Match(response,
        "\"interested_recipients\"\\s*:\\s*(\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (match.Success &&
        int.TryParse(match.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out int count))
      Volatile.Write(ref _interestedRecipients, count);
  }

  void PostInterest(bool refresh) {
    int zoneX = Volatile.Read(ref _interestZoneX);
    int zoneY = Volatile.Read(ref _interestZoneY);
    Dictionary<string, object> interest = new() {
        ["recipient_id"] = _recipientId,
        ["run_id"] = _runId,
        ["world_epoch"] = _worldEpoch,
        ["zone_epoch"] = Math.Max(
            1, Interlocked.Read(ref _interestZoneEpoch)),
        ["zone_x"] = zoneX,
        ["zone_y"] = zoneY,
        ["radius_zones"] = _probe != null && !_probe.Terminal
            ? ProbeInterestRadiusZones
            : WorldInterestRadiusZones,
        ["refresh"] = refresh
    };
    string response = Send(
        _endpoint + "/valheim/zdo-journal/interest", "POST",
        JsonLineSerializer.Serialize(interest), credentials: true);
    Write("interest_registered", "client",
        "zone=" + zoneX + "," + zoneY + " response=" + Compact(response));
  }

  void PollClient() {
    string response = Send(
        _endpoint + "/valheim/zdo-journal/pending/" + _worldEpoch
        + "?recipient_id=" + _recipientId + "&limit=64",
        "GET", string.Empty, credentials: true);
    JournalPendingResponse parsed = Deserialize<JournalPendingResponse>(response);
    if (parsed?.deliveries == null) return;
    foreach (JournalDelivery delivery in parsed.deliveries)
      if (delivery?.@object != null && delivery.seq > 0)
        _clientInbound.Enqueue(delivery);
  }

  void FlushAcks() {
    List<long> sequences = new();
    while (sequences.Count < 256 && _clientAcks.TryDequeue(out long sequence))
      sequences.Add(sequence);
    if (sequences.Count == 0) return;
    try {
      string body =
          "{\"recipient_id\":\"" + Escape(_recipientId)
          + "\",\"world_epoch\":\"" + Escape(_worldEpoch)
          + "\",\"sequences\":[" + string.Join(",", sequences) + "]}";
      Send(_endpoint + "/valheim/zdo-journal/ack", "POST", body,
          credentials: true);
    } catch {
      foreach (long sequence in sequences) _clientAcks.Enqueue(sequence);
      throw;
    }
  }

  void DrainClient() {
    // Harness fault injection: a throttled client applies (and ACKs) one delivery per
    // interval, reproducing the slow-WAN-consumer wedge on the local loop. See the
    // zdoJournalApplyThrottleMs config description.
    int throttleMs = PluginConfig.ZdoJournalApplyThrottleMs?.Value ?? 0;
    if (throttleMs > 0) {
      if (Environment.TickCount - _nextThrottledApplyTick < 0) return;
      if (_clientInbound.TryDequeue(out JournalDelivery throttled)) {
        _nextThrottledApplyTick = Environment.TickCount + throttleMs;
        Apply(throttled);
      }
      return;
    }
    int budget =
        Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()
            ? TeleportApplyPerUpdate
            : ApplyPerUpdate;
    int count = 0;
    while (count++ < budget &&
        _clientInbound.TryDequeue(out JournalDelivery delivery))
      Apply(delivery);
  }

  void Apply(JournalDelivery delivery) {
    JournalObject value = delivery.@object;
    string key = value.uid_user + ":" + value.uid_id;
    _watchedZdo = key;
    try {
      if (!string.Equals(value.run_id, _runId, StringComparison.Ordinal) ||
          !string.Equals(value.world_epoch, _worldEpoch, StringComparison.Ordinal))
        throw new InvalidDataException("delivery_scope_mismatch");
      if (value.object_revision <= 0)
        throw new InvalidDataException("object_revision_invalid");

      byte[] body = null;
      if (!value.tombstone) {
        try { body = Convert.FromBase64String(value.body_b64 ?? string.Empty); }
        catch { throw new InvalidDataException("body_base64_invalid"); }
        ValidateBody(body, value.prefab);
        // A validated receipt-required delivery counts as arrival on ANY branch below —
        // applied, superseded, or stale-rejected are all legal idempotent outcomes; the
        // observe verdict needs proof of delivery, not of first-arrival ordering.
        if (value.receipt_required)
          Interlocked.Increment(ref _receiptSnapshotArrivals);
      }

      long previous;
      lock (_gate) _appliedRevisions.TryGetValue(key, out previous);
      ZDOID uid = new(value.uid_user, checked((uint) value.uid_id));
      ZDO existing = ZDOMan.instance.GetZDO(uid);
      long actualRevision = existing == null
          ? 0 : Revision(existing.DataRevision, existing.OwnerRevision);
      long current = Math.Max(previous, actualRevision);
      if (value.object_revision <= current) {
        if (delivery.kind == "snapshot") {
          long superseded = Interlocked.Increment(ref _superseded);
          if (value.receipt_required ||
              superseded % BulkProgressInterval == 0)
            Write("snapshot_superseded_progress",
                _probe?.ActionId ?? "client",
                "superseded=" + superseded
                + " seq=" + delivery.seq + " uid=" + key
                + " incoming=" + value.object_revision
                + " current=" + current);
        } else {
          Interlocked.Increment(ref _staleRejected);
          Write("stale_rejected_before_mutation", _probe?.ActionId ?? "client",
              "seq=" + delivery.seq + " uid=" + key
              + " incoming=" + value.object_revision + " current=" + current);
        }
        Ack(delivery);
        return;
      }

      if (value.tombstone) {
        if (existing != null) {
          OwnershipLeaseCutoverRunner.EnterCanonicalDestroy();
          try {
            HandleDestroyedZdoMethod.Invoke(
                ZDOMan.instance, new object[] { uid });
          } finally {
            OwnershipLeaseCutoverRunner.ExitCanonicalDestroy();
          }
        }
        if (ZDOMan.instance.GetZDO(uid) != null)
          throw new InvalidOperationException("tombstone_readback_present");
        lock (_gate) _appliedRevisions[key] = value.object_revision;
        Interlocked.Increment(ref _tombstonesApplied);
        Ack(delivery);
        Write("tombstone_applied_typed", _probe?.ActionId ?? "client",
            "seq=" + delivery.seq + " uid=" + key);
        return;
      }

      Vector3 position =
          new(value.position_x, value.position_y, value.position_z);
      ZDO target = existing ??
          (ZDO) CreateNewZdoMethod.Invoke(
              ZDOMan.instance, new object[] { uid, position, value.prefab });
      target.OwnerRevision = checked((ushort) value.owner_rev);
      target.DataRevision = checked((uint) value.data_rev);
      target.SetOwnerInternal(value.owner);
      target.InternalSetPosition(position);
      target.Deserialize(new ZPackage(body));
      if (target.GetPrefab() != value.prefab ||
          target.DataRevision != checked((uint) value.data_rev) ||
          target.OwnerRevision != checked((ushort) value.owner_rev) ||
          target.GetOwner() != value.owner)
        throw new InvalidOperationException("typed_apply_readback_mismatch");
      lock (_gate) _appliedRevisions[key] = value.object_revision;
      if (delivery.kind == "snapshot")
        Interlocked.Increment(ref _snapshotsApplied);
      else
        Interlocked.Increment(ref _deltasApplied);
      Ack(delivery);
      long applied =
          Interlocked.Read(ref _snapshotsApplied) +
          Interlocked.Read(ref _deltasApplied);
      if (value.receipt_required || value.prefab == ProbePrefabHash ||
          applied % BulkProgressInterval == 0)
        Write("typed_apply_progress", _probe?.ActionId ?? "client",
            "applied=" + applied
            + " inbound=" + _clientInbound.Count
            + " seq=" + delivery.seq + " uid=" + key
            + " kind=" + delivery.kind
            + " object_revision=" + value.object_revision
            + " value=" + target.GetInt(ProbeValueHash, -1));
    } catch (InvalidDataException exception) {
      Interlocked.Increment(ref _malformedRejected);
      Ack(delivery);
      Write("malformed_rejected_before_mutation",
          _probe?.ActionId ?? "client",
          "seq=" + delivery.seq + " uid=" + key
          + " error=" + exception.Message);
    } catch (Exception exception) {
      Interlocked.Increment(ref _applyFailures);
      Write("typed_apply_failed", _probe?.ActionId ?? "client",
          "seq=" + delivery.seq + " uid=" + key
          + " error=" + (exception.InnerException ?? exception).GetType().Name
          + ":" + (exception.InnerException ?? exception).Message);
    }
  }

  void Ack(JournalDelivery delivery) {
    if (CanonicalEnabled()) {
      if (delivery.reliable_seq <= 0)
        throw new InvalidDataException("canonical_reliable_sequence_missing");
      _canonicalAcks.Enqueue(new CanonicalAck {
          WorldEpoch = delivery.@object.world_epoch,
          JournalSequence = delivery.seq,
          ReliableSequence = delivery.reliable_seq
      });
    } else {
      _clientAcks.Enqueue(delivery.seq);
    }
  }

  public string DescribeRuntimeProgress() =>
      "banked=" + Interlocked.Read(ref _canonicalDeliveriesBanked)
      + " inbound=" + _clientInbound.Count
      + " applied=" +
          (Interlocked.Read(ref _snapshotsApplied) +
           Interlocked.Read(ref _deltasApplied))
      + " superseded=" + Interlocked.Read(ref _superseded)
      + " apply_failures=" + Interlocked.Read(ref _applyFailures);

  public static string DescribeActiveRuntimeProgress() =>
      Volatile.Read(ref _active)?.DescribeRuntimeProgress()
      ?? "zdo_journal=unavailable";

  static void ValidateBody(byte[] body, int expectedPrefab) {
    if (body == null || body.Length is < 6 or > MaxBodyBytes)
      throw new InvalidDataException("body_size_invalid");
    try {
      ZPackage package = new(body);
      ushort flags = package.ReadUShort();
      if ((flags & 0xE000) != 0)
        throw new InvalidDataException("body_flags_invalid");
      int prefab = package.ReadInt();
      if (prefab != expectedPrefab)
        throw new InvalidDataException("body_prefab_mismatch");
      if ((flags & 0x1000) != 0) package.ReadVector3();
      if ((flags & 1) != 0) {
        package.ReadByte();
        package.ReadZDOID();
      }
      ReadEntries(package, flags, 2, 8, p => { p.ReadInt(); p.ReadSingle(); });
      ReadEntries(package, flags, 4, 16, p => { p.ReadInt(); p.ReadVector3(); });
      ReadEntries(package, flags, 8, 20, p => { p.ReadInt(); p.ReadQuaternion(); });
      ReadEntries(package, flags, 0x10, 8, p => { p.ReadInt(); p.ReadInt(); });
      ReadEntries(package, flags, 0x20, 12, p => { p.ReadInt(); p.ReadLong(); });
      ReadEntries(package, flags, 0x40, 5, p => { p.ReadInt(); p.ReadString(); });
      ReadEntries(package, flags, 0x80, 8, p => { p.ReadInt(); p.ReadByteArray(); });
      if (package.GetPos() != package.Size())
        throw new InvalidDataException("body_trailing_bytes");
    } catch (InvalidDataException) {
      throw;
    } catch (Exception exception) {
      throw new InvalidDataException(
          "body_parse_failed:" + exception.GetType().Name, exception);
    }
  }

  static void ReadEntries(
      ZPackage package, ushort flags, int flag, int minimumBytes,
      Action<ZPackage> read) {
    if ((flags & flag) == 0) return;
    int count = package.ReadByte();
    if (count <= 0 || count > 255 ||
        package.Size() - package.GetPos() < count * minimumBytes)
      throw new InvalidDataException("body_entry_count_invalid");
    for (int i = 0; i < count; i++) read(package);
  }

  static bool BodyHasProbeTag(ZPackage package, string runId) {
    if (package == null || !SafeToken(runId, 80)) return false;
    package.SetPos(0);
    ushort flags = package.ReadUShort();
    package.ReadInt();
    if ((flags & 0x1000) != 0) package.ReadVector3();
    if ((flags & 1) != 0) {
      package.ReadByte();
      package.ReadZDOID();
    }
    ReadEntries(package, flags, 2, 8, value => {
      value.ReadInt();
      value.ReadSingle();
    });
    ReadEntries(package, flags, 4, 16, value => {
      value.ReadInt();
      value.ReadVector3();
    });
    ReadEntries(package, flags, 8, 20, value => {
      value.ReadInt();
      value.ReadQuaternion();
    });
    ReadEntries(package, flags, 0x10, 8, value => {
      value.ReadInt();
      value.ReadInt();
    });
    ReadEntries(package, flags, 0x20, 12, value => {
      value.ReadInt();
      value.ReadLong();
    });
    if ((flags & 0x40) == 0) return false;
    int count = package.ReadByte();
    if (count <= 0 || count > 255) return false;
    for (int index = 0; index < count; index++) {
      int key = package.ReadInt();
      string value = package.ReadString();
      if (key == ProbeTagHash &&
          string.Equals(value, runId, StringComparison.Ordinal))
        return true;
    }
    return false;
  }

  void EvaluateProbe() {
    if (_probe == null || _probe.Terminal) return;
    long snapshot = Interlocked.Read(ref _snapshotsApplied) - _probe.Snapshots;
    long delta = Interlocked.Read(ref _deltasApplied) - _probe.Deltas;
    long tombstone = Interlocked.Read(ref _tombstonesApplied) - _probe.Tombstones;
    long stale = Interlocked.Read(ref _staleRejected) - _probe.Stale;
    long malformed = Interlocked.Read(ref _malformedRejected) - _probe.Malformed;
    long receiptArrivals =
        Interlocked.Read(ref _receiptSnapshotArrivals) - _probe.ReceiptArrivals;
    bool positive = _probe.Mode == "observe"
        ? (snapshot >= 1 || receiptArrivals >= 1) && delta >= 1
        : delta >= 1;
    if (positive && tombstone >= 1 && stale >= 1 && malformed >= 1 &&
        Interlocked.Read(ref _nativeRpcDeliveries) == 0 &&
        Interlocked.Read(ref _applyFailures) == 0) {
      _probe.Terminal = true;
      _probe.Success = true;
      _probe.Detail =
          "typed_journal_sequence_applied snapshot=" + snapshot
          + " receipt_snapshot_arrivals=" + receiptArrivals
          + " delta=" + delta + " stale=" + stale
          + " malformed=" + malformed + " tombstone=" + tombstone
          + " native_rpc_zdo_data=0";
      Write("probe_passed", _probe.ActionId, _probe.Detail);
      return;
    }
    if (Time.unscaledTime <= _probe.DeadlineAt) return;
    _probe.Terminal = true;
    _probe.Success = false;
    _probe.Detail =
        "zdo_journal_deadline snapshot=" + snapshot
        + " receipt_snapshot_arrivals=" + receiptArrivals
        + " delta=" + delta + " stale=" + stale
        + " malformed=" + malformed + " tombstone=" + tombstone
        + " native_rpc_zdo_data=" + Interlocked.Read(ref _nativeRpcDeliveries)
        + " apply_failures=" + Interlocked.Read(ref _applyFailures);
    Write("probe_failed", _probe.ActionId, _probe.Detail);
  }

  void RefreshContext() {
    bool server = IsServer();
    _role = server ? "server" : "client";
    string runId = server
        ? PluginConfig.NativeNetworkEvidenceRunId?.Value
        : NativeAutotestRequest.ActiveRunId;
    if (SafeToken(runId, 80)) _runId = runId.Trim();
    if (!server) {
      if (CanonicalEnabled() && SafeToken(_gameSession.LogicalPeerId, 80)) {
        _recipientId = _gameSession.LogicalPeerId;
      } else {
        string client = NativeAutotestRequest.ActiveClient;
        string recipient = _runId + "." + client;
        if (SafeToken(client, 24) && SafeToken(recipient, 96))
          _recipientId = recipient;
      }
    }
    string configured = server
        ? PluginConfig.LumberjacksGatewayUrl?.Value
        : NativeAutotestRequest.ActiveGatewayUrl;
    if (!string.IsNullOrWhiteSpace(configured))
      _endpoint = configured.Trim().TrimEnd('/')
          .Replace("ws://", "http://").Replace("wss://", "https://");
    string previousWorldEpoch = _worldEpoch;
    string currentWorldEpoch = WorldZoneCutoverRunner.TryGetCurrentWorldEpoch(
        out string acceptedWorldEpoch)
        ? acceptedWorldEpoch : string.Empty;
    if (!string.Equals(previousWorldEpoch, currentWorldEpoch,
            StringComparison.Ordinal)) {
      if (!server && !string.IsNullOrEmpty(previousWorldEpoch))
        ResetClientEpochState();
      _worldEpoch = currentWorldEpoch;
      if (!string.IsNullOrEmpty(previousWorldEpoch) &&
          !string.IsNullOrEmpty(currentWorldEpoch))
        Write("world_session_epoch_changed", _role,
            "previous=" + previousWorldEpoch
            + " current=" + currentWorldEpoch);
    }
  }

  void ResetClientEpochState() {
    while (_clientInbound.TryDequeue(out _)) { }
    while (_clientAcks.TryDequeue(out _)) { }
    while (_canonicalAcks.TryDequeue(out _)) { }
    while (_canonicalEvents.TryDequeue(out _)) { }
    lock (_gate) _appliedRevisions.Clear();
    _hasRegisteredInterest = false;
    _hasPrefetchInterest = false;
    _nextInterest = 0.0f;
  }

  static bool Enabled() =>
      PluginConfig.ZdoJournalCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveZdoJournalCutover;

  static bool CanonicalEnabled() =>
      PluginConfig.ZdoJournalCanonicalSessionEnabled?.Value == true ||
      NativeAutotestRequest.ActiveZdoJournalCanonicalSession;

  static bool IsServer() {
    try { return ZNet.instance != null && ZNet.instance.IsServer(); }
    catch { return false; }
  }

  static bool Finite(Vector3 value) =>
      !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
      !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
      !float.IsNaN(value.z) && !float.IsInfinity(value.z);

  static long Revision(uint dataRevision, ushort ownerRevision) =>
      Math.Max(1L, ((long) dataRevision << 16) | ownerRevision);

  static JournalObject Clone(JournalObject value) =>
      new() {
          run_id = value.run_id,
          world_epoch = value.world_epoch,
          zone_epoch = value.zone_epoch,
          source_seq = value.source_seq,
          object_revision = value.object_revision,
          uid_user = value.uid_user,
          uid_id = value.uid_id,
          prefab = value.prefab,
          owner = value.owner,
          owner_rev = value.owner_rev,
          data_rev = value.data_rev,
          position_x = value.position_x,
          position_y = value.position_y,
          position_z = value.position_z,
          zone_x = value.zone_x,
          zone_y = value.zone_y,
          body_b64 = value.body_b64,
          tombstone = value.tombstone,
          delivery_only = value.delivery_only,
          receipt_required = value.receipt_required,
          delivery_recipient_id = value.delivery_recipient_id
      };

  static Dictionary<string, object> ToDictionary(JournalObject value) =>
      new() {
          ["run_id"] = value.run_id,
          ["world_epoch"] = value.world_epoch,
          ["zone_epoch"] = value.zone_epoch,
          ["source_seq"] = value.source_seq,
          ["object_revision"] = value.object_revision,
          ["uid_user"] = value.uid_user,
          ["uid_id"] = value.uid_id,
          ["prefab"] = value.prefab,
          ["owner"] = value.owner,
          ["owner_rev"] = value.owner_rev,
          ["data_rev"] = value.data_rev,
          ["position_x"] = value.position_x,
          ["position_y"] = value.position_y,
          ["position_z"] = value.position_z,
          ["zone_x"] = value.zone_x,
          ["zone_y"] = value.zone_y,
          ["body_b64"] = value.body_b64 ?? string.Empty,
          ["tombstone"] = value.tombstone,
          ["delivery_only"] = value.delivery_only,
          ["receipt_required"] = value.receipt_required,
          ["delivery_recipient_id"] =
              value.delivery_recipient_id ?? string.Empty
      };

  static T Deserialize<T>(string raw) where T : class {
    DataContractJsonSerializer serializer = new(typeof(T));
    using MemoryStream stream =
        new(Encoding.UTF8.GetBytes(raw ?? string.Empty));
    return serializer.ReadObject(stream) as T;
  }

  static string Send(
      string url, string method, string body, bool credentials) {
    if (!AlphaTransportSwitches.LumberjacksHttpEnabled)
      throw new InvalidOperationException("Lumberjacks HTTP paused");
    Uri uri = new(url);
    byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
    string request =
        method + " " + uri.PathAndQuery + " HTTP/1.1\r\n"
        + "Host: " + uri.Host + ":" + uri.Port + "\r\n"
        + "Content-Type: application/json\r\n"
        + "Accept: application/json\r\n"
        + "Content-Length: " + bytes.Length + "\r\n"
        + (credentials &&
           !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value)
            ? "X-Lumberjacks-Client-Key: "
              + PluginConfig.LumberjacksClientAccessKey.Value + "\r\n"
            : string.Empty)
        + (credentials &&
           !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentId.Value)
            ? "X-Lumberjacks-Enrollment-Id: "
              + PluginConfig.LumberjacksEnrollmentId.Value + "\r\n"
            : string.Empty)
        + "Connection: close\r\n\r\n";
    string raw = BoundedRawHttp.SendBounded(
        uri, Encoding.ASCII.GetBytes(request), bytes,
        HttpTimeoutMs, ResponseDeadlineMs, MaxResponseBytes);
    string status = raw.Length == 0 ? string.Empty : raw.Split('\n')[0].Trim();
    if (!status.StartsWith("HTTP/1.1 2", StringComparison.Ordinal))
      throw new InvalidOperationException("http_status:" + status);
    int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    if (split < 0) return string.Empty;
    string headers = raw.Substring(0, split);
    string payload = raw.Substring(split + 4);
    if (headers.IndexOf(
            "transfer-encoding: chunked",
            StringComparison.OrdinalIgnoreCase) >= 0)
      return WireParsers.DecodeChunkedBody(payload);
    return payload;
  }

  void Write(string state, string actionId, string detail) {
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = SafeToken(_runId, 80) ? _runId : "unscoped",
            ["role"] = _role,
            ["client"] = NativeAutotestRequest.ActiveClient,
            ["action_id"] = actionId ?? string.Empty,
            ["world_epoch"] = _worldEpoch,
            ["transport"] = CanonicalEnabled() ? "canonical_session" : "http",
            ["logical_peer_id"] = _gameSession.LogicalPeerId,
            ["watched_zdo"] = _watchedZdo,
            ["native_create_sync_candidates"] =
                Interlocked.Read(ref _nativeCandidates),
            ["native_rpc_zdo_data"] =
                Interlocked.Read(ref _nativeRpcDeliveries),
            ["detail"] = detail ?? string.Empty
        });
  }

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
    foreach (char character in value)
      if (!char.IsLetterOrDigit(character)
          && character is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

  static string Compact(string value) {
      if (string.IsNullOrWhiteSpace(value)) return "empty";
      string flat = value.Replace('\r', ' ').Replace('\n', ' ');
      return flat.Length <= 160 ? flat : flat.Substring(0, 160);
  }

  public Dictionary<string, object> Snapshot() =>
      new() {
          ["enabled"] = Enabled(),
          ["world_epoch"] = _worldEpoch,
          ["run_id"] = _runId,
          ["transport"] = CanonicalEnabled() ? "canonical_session" : "http",
          ["logical_peer_id"] = _gameSession.LogicalPeerId,
          ["create_sync_lists"] = Interlocked.Read(ref _createSyncLists),
          ["native_candidates"] = Interlocked.Read(ref _nativeCandidates),
          ["native_rpc_zdo_data"] = Interlocked.Read(ref _nativeRpcDeliveries),
          ["server_snapshots_queued"] =
              Interlocked.Read(ref _serverSnapshotsQueued),
          ["server_outbound"] = _serverOutbound.Count,
          ["canonical_mutations_queued"] =
              Interlocked.Read(ref _canonicalMutationsQueued),
          ["canonical_mutation_backpressure"] =
              Interlocked.Read(ref _canonicalMutationBackpressure),
          ["snapshots_applied"] = Interlocked.Read(ref _snapshotsApplied),
          ["deltas_applied"] = Interlocked.Read(ref _deltasApplied),
          ["tombstones_applied"] = Interlocked.Read(ref _tombstonesApplied),
          ["stale_rejected"] = Interlocked.Read(ref _staleRejected),
          ["malformed_rejected"] = Interlocked.Read(ref _malformedRejected),
          ["apply_failures"] = Interlocked.Read(ref _applyFailures)
      };

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  [DataContract]
  sealed class JournalPendingResponse {
    [DataMember(Name = "deliveries")]
    public JournalDelivery[] deliveries { get; set; }
  }

  [DataContract]
  sealed class JournalDelivery {
    [DataMember(Name = "seq")]
    public long seq { get; set; }

    [DataMember(Name = "kind")]
    public string kind { get; set; }

    [DataMember(Name = "object")]
    public JournalObject @object { get; set; }

    [IgnoreDataMember]
    public long reliable_seq;
  }

  [DataContract]
  sealed class CanonicalDeliveryEnvelope {
    [DataMember(Name = "seq")]
    public long seq { get; set; }

    [DataMember(Name = "payload")]
    public JournalDelivery payload { get; set; }
  }

  [DataContract]
  sealed class JournalObject {
    [DataMember(Name = "run_id")] public string run_id;
    [DataMember(Name = "world_epoch")] public string world_epoch;
    [DataMember(Name = "zone_epoch")] public long zone_epoch;
    [DataMember(Name = "source_seq")] public long source_seq;
    [DataMember(Name = "object_revision")] public long object_revision;
    [DataMember(Name = "uid_user")] public long uid_user;
    [DataMember(Name = "uid_id")] public long uid_id;
    [DataMember(Name = "prefab")] public int prefab;
    [DataMember(Name = "owner")] public long owner;
    [DataMember(Name = "owner_rev")] public int owner_rev;
    [DataMember(Name = "data_rev")] public long data_rev;
    [DataMember(Name = "position_x")] public float position_x;
    [DataMember(Name = "position_y")] public float position_y;
    [DataMember(Name = "position_z")] public float position_z;
    [DataMember(Name = "zone_x")] public int zone_x;
    [DataMember(Name = "zone_y")] public int zone_y;
    [DataMember(Name = "body_b64")] public string body_b64;
    [DataMember(Name = "tombstone")] public bool tombstone;
    [DataMember(Name = "delivery_only")] public bool delivery_only;
    [DataMember(Name = "receipt_required")] public bool receipt_required;
    [DataMember(Name = "delivery_recipient_id")]
    public string delivery_recipient_id;
  }

  sealed class DriveState {
    public string RunId;
    public string ActionId;
    public ZDO Zdo;
    public string Phase;
    public float NextAt;
    public bool Complete;
  }

  sealed class ClientProbe {
    public string ActionId;
    public string Mode;
    public float DeadlineAt;
    public long ReceiptArrivals;
    public long Snapshots;
    public long Deltas;
    public long Tombstones;
    public long Stale;
    public long Malformed;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class CanonicalAck {
    public string WorldEpoch;
    public long JournalSequence;
    public long ReliableSequence;
  }

  sealed class CanonicalEvent {
    public string Kind;
    public long ReliableSequence;
    public string RunId;
    public string WorldEpoch;
    public string LogicalPeerId;
    public long SourceSequence;
    public bool Accepted;
    public string Result;
    public long Count;
    public long Pending;
    public long Durable;
    public long ZoneEpoch;
    public int ZoneX;
    public int ZoneY;
    public int RadiusZones;
  }

  sealed class ServerInterest {
    public string RunId;
    public long ZoneEpoch;
    public int ZoneX;
    public int ZoneY;
    public int RadiusZones;
  }
}

[HarmonyPatch]
static class ZdoJournalCutoverPatches {
  [HarmonyPatch(typeof(ZDO), "IncreaseDataRevision")]
  [HarmonyPostfix]
  static void DataRevisionPostfix(ZDO __instance) =>
      ZdoJournalCutoverRunner.CaptureMutation(__instance);

  [HarmonyPatch(typeof(ZDO), "IncreaseOwnerRevision")]
  [HarmonyPostfix]
  static void OwnerRevisionPostfix(ZDO __instance) =>
      ZdoJournalCutoverRunner.CaptureMutation(__instance);

  [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
  [HarmonyPrefix]
  static bool DestroyPrefix(ZDOID uid) {
    if (OwnershipLeaseCutoverRunner.ShouldBlockHandleDestroyed(uid))
      return false;
    ZdoJournalCutoverRunner.CaptureTombstone(uid);
    return true;
  }

  [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
  [HarmonyPostfix]
  static void CreateSyncListPostfix(object peer, List<ZDO> toSync) {
    ZdoJournalCutoverRunner.ObserveCreateSyncList(toSync);
    OwnershipLeaseCutoverRunner.FilterNativeSync(peer, toSync);
    WorldZoneCutoverRunner.FilterNativeSync(toSync);
  }

  [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
  [HarmonyPrefix]
  static void RpcZdoDataPrefix(ZPackage pkg) =>
      ZdoJournalCutoverRunner.ObserveNativeZdoData(pkg);
}

[HarmonyPatch(
    typeof(Player), nameof(Player.TeleportTo),
    new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) })]
static class ZdoJournalHardRelocationPatches {
  [HarmonyPostfix]
  static void TeleportToPostfix(
      Player __instance, Vector3 pos, bool __result) {
    if (__result && ReferenceEquals(__instance, Player.m_localPlayer))
      ZdoJournalCutoverRunner.NotifyHardRelocation(pos);
  }
}
