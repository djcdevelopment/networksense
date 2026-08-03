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
/// Physical C10 container canary backed by a production-shaped canonical
/// transaction. Vanilla Container.TakeAll is invoked by each real client, but
/// the selected tagged container never transfers ZDO ownership to either one.
/// The server serializes the competing revision-one requests, journals the
/// winning inventory mutation, and replays duplicate transaction receipts.
/// </summary>
public sealed class ContainerCutoverRunner : IDisposable {
  public const string ReceiptFileName = "container-cutover.jsonl";

  const int TransactionSchema = 1;
  const string ContainerPrefab = "piece_chest_wood";
  const string ItemPrefab = "Raspberry";
  const float MaximumRequestDistance = 12.0f;

  static readonly int RunTagHash =
      ZdoJournalCutoverRunner.ProbeTagName.GetStableHashCode();
  static readonly int ActionTagHash =
      "ComfyNetworkSense_ContainerAction".GetStableHashCode();
  static readonly int RevisionHash =
      "ComfyNetworkSense_ContainerRevision".GetStableHashCode();
  static readonly int CountHash =
      "ComfyNetworkSense_ContainerCount".GetStableHashCode();
  static ContainerCutoverRunner _active;

  readonly RoutedRpcCutoverRunner _routedRpc;
  readonly TelemetryLogWriter _writer = new();
  readonly Dictionary<string, ServerContainer> _serverByRun =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, TransactionReceipt> _serverReceipts =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, PendingTransaction> _pendingTransactions =
      new(StringComparer.Ordinal);
  readonly Dictionary<string, ContainerContentionGate> _contentionGates =
      new(StringComparer.Ordinal);
  readonly HashSet<string> _creditedTransactions =
      new(StringComparer.Ordinal);
  readonly HashSet<string> _reportedOwnerSuppressions =
      new(StringComparer.Ordinal);

  ZRoutedRpc _registeredRpc;
  ContainerProbe _probe;
  long _transactionArrivalOrder;
  bool _disposed;

  public ContainerCutoverRunner(RoutedRpcCutoverRunner routedRpc) {
    _routedRpc = routedRpc;
    ContainerCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    EnsureHandlers();
    TickProbe(now);
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) ||
        !ContainerTransactionPolicy.AllowsMode(mode)) {
      detail = "container_probe_parameters_invalid";
      return false;
    }
    if (!Enabled()) {
      detail = "container_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        ZRoutedRpc.instance == null || ZNetScene.instance == null) {
      detail = "container_probe_client_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_container_probe_active";
      return false;
    }
    string runId = CurrentRunId();
    if (!SafeToken(runId, 80)) {
      detail = "container_probe_run_missing";
      return false;
    }

    _probe = new ContainerProbe {
        RunId = runId,
        ActionId = actionId,
        Mode = mode,
        StartedAt = Time.unscaledTime,
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 5.0f, 180.0f),
        NextDiagnosticAt = Time.unscaledTime + 3.0f,
        InventoryBefore = CountLocalItemUnits()
    };
    Write("probe_started", actionId,
        "mode=" + mode + " inventory_before=" + _probe.InventoryBefore);
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
      detail = "container_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  void TickProbe(float now) {
    ContainerProbe probe = _probe;
    if (probe == null || probe.Terminal || IsServer()) return;
    if (now > probe.DeadlineAt) {
      FailProbe("container_probe_deadline_exceeded mode=" + probe.Mode +
          " original=" + probe.SawOriginal +
          " duplicate=" + probe.SawDuplicate + " " +
          DescribeRunContainer(probe.RunId));
      return;
    }

    switch (probe.Mode) {
      case "spawn":
        TickSpawn(probe);
        break;
      case "wait_container":
        if (TryFindRunContainer(probe.RunId, out Container waiting,
                out ZDO waitingZdo) &&
            CanonicalShape(waiting, waitingZdo,
                ContainerTransactionPolicy.InitialRevision,
                ContainerTransactionPolicy.InitialCount))
          CompleteProbe("container_ready uid=" + waitingZdo.m_uid +
              " revision=1 count=1 actual_inventory=1");
        else
          MaybeWriteProgress(probe, now);
        break;
      case "contend_take":
        TickContend(probe);
        break;
      case "observe_empty":
        if (TryFindRunContainer(probe.RunId, out Container empty,
                out ZDO emptyZdo) &&
            CanonicalShape(empty, emptyZdo, 2, 0) &&
            emptyZdo.GetOwner() == 0)
          CompleteProbe("container_reconstructed_empty uid=" +
              emptyZdo.m_uid +
              " revision=2 count=0 actual_inventory=0 owner=0");
        else
          MaybeWriteProgress(probe, now);
        break;
    }
  }

  void TickSpawn(ContainerProbe probe) {
    if (probe.RequestSent) return;
    Player player = Player.m_localPlayer;
    Vector3 requested = ((Component) player).transform.position +
        ((Component) player).transform.forward * 2.0f;
    ZPackage package = new();
    package.Write(probe.RunId);
    package.Write(probe.ActionId);
    package.Write(requested);
    package.SetPos(0);
    probe.RequestSent = _routedRpc.InvokeTyped(
        probe.ActionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            ZNet.instance.GetServerPeer().m_uid,
            ValheimRoutedRpcAdmissions.CutoverContainerSpawnRequest,
            new object[] { package }));
    if (!probe.RequestSent)
      FailProbe("container_spawn_request_queue_failed");
  }

  void TickContend(ContainerProbe probe) {
    if (!probe.NativeTakeAllSuppressed) {
      if (!TryFindRunContainer(
              probe.RunId, out Container container, out ZDO zdo)) {
        MaybeWriteProgress(probe, Time.unscaledTime);
        return;
      }
      probe.ContainerId = zdo.m_uid;
      bool invoked = container.TakeAll(Player.m_localPlayer);
      if (!invoked && !probe.NativeTakeAllSuppressed)
        FailProbe("container_takeall_was_not_intercepted");
      return;
    }
    if (!probe.SawOriginal || !probe.SawDuplicate) {
      MaybeWriteProgress(probe, Time.unscaledTime);
      return;
    }
    if (!TryFindRunContainer(
            probe.RunId, out Container current, out ZDO currentZdo) ||
        currentZdo.m_uid != probe.ContainerId ||
        !CanonicalShape(current, currentZdo, 2, 0)) {
      MaybeWriteProgress(probe, Time.unscaledTime);
      return;
    }

    int after = CountLocalItemUnits();
    int expectedAfter = probe.InventoryBefore + probe.GrantedCount;
    if (probe.InventoryBefore < 0 || after != expectedAfter ||
        probe.CanonicalRevision != 2 || probe.RemainingCount != 0 ||
        probe.DuplicateCanonicalRevision != probe.CanonicalRevision ||
        probe.DuplicateRemainingCount != probe.RemainingCount ||
        probe.DuplicateGrantedCount != probe.GrantedCount ||
        probe.DuplicateAccepted != probe.Accepted ||
        !string.Equals(
            probe.DuplicateResult, probe.Result, StringComparison.Ordinal) ||
        (probe.Accepted &&
            (probe.GrantedCount != 1 || probe.Result != "committed")) ||
        (!probe.Accepted &&
            (probe.GrantedCount != 0 || probe.Result != "stale_revision"))) {
      FailProbe("container_transaction_invariant_failed accepted=" +
          probe.Accepted + " result=" + probe.Result +
          " granted=" + probe.GrantedCount +
          " inventory_before=" + probe.InventoryBefore +
          " inventory_after=" + after);
      return;
    }
    CompleteProbe("container_contention_decision accepted=" +
        probe.Accepted.ToString().ToLowerInvariant() +
        " result=" + probe.Result +
        " revision=2 remaining=0 granted=" + probe.GrantedCount +
        " duplicate_replayed=true native_takeall_suppressed=true" +
        " inventory_before=" + probe.InventoryBefore +
        " inventory_after=" + after);
  }

  internal static bool TryHandleTakeAll(
      Container container,
      Humanoid character,
      ref bool result) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
    ContainerProbe probe = active?._probe;
    if (active == null || probe == null || probe.Terminal ||
        probe.Mode != "contend_take" || !Enabled() || IsServer() ||
        container == null || character == null ||
        !ReferenceEquals(character, Player.m_localPlayer))
      return false;
    ZDO zdo = container.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null || !string.Equals(
            zdo.GetString(RunTagHash, string.Empty),
            probe.RunId, StringComparison.Ordinal))
      return false;

    probe.NativeTakeAllSuppressed = true;
    probe.ContainerId = zdo.m_uid;
    bool first = active.QueueTakeRequest(probe, zdo.m_uid);
    bool duplicate = first && active.QueueTakeRequest(probe, zdo.m_uid);
    result = first && duplicate;
    active.Write("native_takeall_suppressed", probe.ActionId,
        "uid=" + zdo.m_uid + " first_queued=" + first +
        " duplicate_queued=" + duplicate);
    if (!result)
      active.FailProbe("container_transaction_request_queue_failed");
    return true;
  }

  internal static bool ShouldBlockNativeOwnerReassignment(
      ZDO zdo, long attemptedOwner) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || active._disposed || !Enabled() || zdo == null)
      return false;
    string runId = CurrentRunId();
    bool tagged = SafeToken(runId, 80) && string.Equals(
        zdo.GetString(RunTagHash, string.Empty),
        runId,
        StringComparison.Ordinal);
    long currentOwner = zdo.GetOwner();
    bool blocked = ContainerTransactionPolicy.BlocksNativeOwnerReassignment(
        OwnershipLeaseCutoverRunner.ReleaseScopeDepth > 0,
        tagged,
        currentOwner,
        attemptedOwner);
    if (!blocked) return false;
    string actionId = zdo.GetString(ActionTagHash, "container-owner");
    string suppressionKey = zdo.m_uid + ":" + attemptedOwner;
    if (active._reportedOwnerSuppressions.Add(suppressionKey))
      active.Write("native_owner_reassignment_suppressed", actionId,
          "uid=" + zdo.m_uid +
          " held_owner=" + currentOwner +
          " attempted_owner=" + attemptedOwner +
          " source=ZDOMan.ReleaseNearbyZDOS");
    return true;
  }

  bool QueueTakeRequest(ContainerProbe probe, ZDOID uid) {
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return false;
    ZPackage package = new();
    package.Write(TransactionSchema);
    package.Write(probe.RunId);
    package.Write(probe.ActionId);
    package.Write(uid);
    package.Write(ContainerTransactionPolicy.InitialRevision);
    package.Write("take");
    package.Write(ItemPrefab);
    package.Write(1);
    package.SetPos(0);
    return _routedRpc.InvokeTyped(
        probe.ActionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid,
            ValheimRoutedRpcAdmissions.ModContainerTransactionRequest,
            new object[] { package }));
  }

  void EnsureHandlers() {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverContainerSpawnRequest,
        HandleSpawnRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.CutoverContainerSpawnResponse,
        HandleSpawnResponse);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.ModContainerTransactionRequest,
        HandleTransactionRequest);
    rpc.Register<ZPackage>(
        ValheimRoutedRpcAdmissions.ModContainerTransactionResult,
        HandleTransactionResult);
    _registeredRpc = rpc;
    Write("handlers_registered", "unscoped", Role());
  }

  static void HandleSpawnRequest(long senderPeerId, ZPackage package) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
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
          !LogicalPeerCutoverRunner.TryGetCanonicalPeerReference(
              senderPeerId, out _, out Vector3 peerPosition) ||
          Vector3.Distance(peerPosition, requested) > MaximumRequestDistance)
        throw new InvalidOperationException("container_spawn_request_invalid");

      if (!active._serverByRun.TryGetValue(
              runId, out ServerContainer state) || state.Zdo == null ||
          ZDOMan.instance?.GetZDO(state.Zdo.m_uid) == null) {
        GameObject prefab = ZNetScene.instance?.GetPrefab(ContainerPrefab);
        GameObject itemPrefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
        if (prefab == null || itemPrefab == null)
          throw new InvalidOperationException("container_canary_prefab_missing");
        Vector3 spawnPosition = requested;
        float groundHeight;
        string groundSource;
        if (ZoneSystem.instance != null &&
            ZoneSystem.instance.GetGroundHeight(
                new Vector3(requested.x, 0.0f, requested.z),
                out groundHeight)) {
          groundSource = "terrain_collider";
        } else if (WorldGenerator.instance != null) {
          // Steam-free dedicated peers intentionally have no native scene
          // presentation, so their logical reference position can be inside a
          // zone whose terrain collider is not instantiated on the server.
          // WorldGenerator is the deterministic height authority for the same
          // world seed and is already used by the ship canary's server checks.
          groundHeight = WorldGenerator.instance.GetHeight(
              requested.x, requested.z);
          groundSource = "world_generator";
        } else {
          throw new InvalidOperationException(
              "container_canary_ground_height_missing");
        }
        if (!Finite(groundHeight))
          throw new InvalidOperationException(
              "container_canary_ground_height_invalid");
        spawnPosition.y = groundHeight;
        GameObject instance = UnityEngine.Object.Instantiate(
            prefab, spawnPosition, Quaternion.identity);
        Container container =
            instance.GetComponent<Container>() ??
            instance.GetComponentInChildren<Container>(includeInactive: true);
        ZNetView view = instance.GetComponent<ZNetView>() ??
            instance.GetComponentInChildren<ZNetView>(includeInactive: true);
        ZDO zdo = view?.GetZDO();
        if (container == null || zdo == null)
          throw new InvalidOperationException("container_canary_instance_invalid");
        WearNTear wear = instance.GetComponent<WearNTear>() ??
            instance.GetComponentInChildren<WearNTear>(includeInactive: true);
        if (wear != null) {
          // This gate measures server-serialized container inventory, not the
          // building-support system. The player-built prefab otherwise applies
          // a full-health unsupported-piece hit on its first owner tick when it
          // is created outside Hammer placement choreography.
          wear.m_noSupportWear = false;
          wear.OnPlaced();
        }
        zdo.SetOwner(ZNet.GetUID());
        Inventory inventory = container.GetInventory();
        string itemName = SharedItemName(itemPrefab);
        if (inventory == null || string.IsNullOrEmpty(itemName))
          throw new InvalidOperationException("container_canary_inventory_invalid");
        ZdoJournalCutoverRunner.ApplyCanonicalMutationWithReceipt(zdo, () => {
          // A real player chest is persistent. Exact run-tag cleanup removes
          // this canary even after an aborted probe, so honoring that native
          // lifetime does not leak it into the retained world.
          zdo.Persistent = true;
          zdo.Set(RunTagHash, runId);
          zdo.Set(ActionTagHash, actionId);
          zdo.Set(RevisionHash, ContainerTransactionPolicy.InitialRevision);
          zdo.Set(CountHash, ContainerTransactionPolicy.InitialCount);
          inventory.RemoveAll();
          if (!inventory.AddItem(itemPrefab, 1))
            throw new InvalidOperationException("container_seed_add_failed");
          StoreInventoryPayload(inventory, zdo);
        });
        if (inventory.CountItems(itemName) != 1 ||
            !InventoryPayloadMatches(inventory, zdo))
          throw new InvalidOperationException("container_seed_count_invalid");
        state = new ServerContainer {
            RunId = runId,
            ActionId = actionId,
            Instance = instance,
            Container = container,
            Zdo = zdo,
            ItemName = itemName,
            Revision = ContainerTransactionPolicy.InitialRevision,
            RemainingCount = ContainerTransactionPolicy.InitialCount
        };
        active._serverByRun[runId] = state;
        active.Write("container_spawned", actionId,
            "uid=" + zdo.m_uid + " owner=" + zdo.GetOwner() +
            " revision=1 count=1 item_prefab=" + ItemPrefab +
            " inventory_serialization=explicit" +
            " persistent=" + zdo.Persistent +
            " structural_wear=" + (wear != null ? "isolated" : "absent") +
            " requested_y=" + requested.y.ToString(
                "0.##", CultureInfo.InvariantCulture) +
            " ground_y=" + groundHeight.ToString(
                "0.##", CultureInfo.InvariantCulture) +
            " ground_source=" + groundSource +
            " position=" + Format(spawnPosition));
      }
      active.SendSpawnResponse(
          senderPeerId, runId, actionId, state.Zdo.m_uid,
          state.Zdo.GetPosition(), state.Revision, state.RemainingCount,
          true, "spawned");
    } catch (Exception exception) {
      active.Write("container_spawn_rejected", actionId,
          "sender=" + senderPeerId + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      if (SafeToken(runId, 80) && SafeToken(actionId, 80))
        active.SendSpawnResponse(
            senderPeerId, runId, actionId, ZDOID.None, Vector3.zero,
            0, 0, false, exception.Message);
    }
  }

  void SendSpawnResponse(
      long targetPeerId,
      string runId,
      string actionId,
      ZDOID uid,
      Vector3 position,
      int revision,
      int count,
      bool accepted,
      string result) {
    ZPackage response = new();
    response.Write(runId);
    response.Write(actionId);
    response.Write(uid);
    response.Write(position);
    response.Write(revision);
    response.Write(count);
    response.Write(accepted);
    response.Write(result ?? string.Empty);
    response.SetPos(0);
    _routedRpc.InvokeTyped(
        actionId,
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            targetPeerId,
            ValheimRoutedRpcAdmissions.CutoverContainerSpawnResponse,
            new object[] { response }));
  }

  static void HandleSpawnResponse(long senderPeerId, ZPackage package) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
    ContainerProbe probe = active?._probe;
    if (active == null || probe == null || probe.Terminal ||
        probe.Mode != "spawn" || IsServer()) return;
    package.SetPos(0);
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    Vector3 position = package.ReadVector3();
    int revision = package.ReadInt();
    int count = package.ReadInt();
    bool accepted = package.ReadBool();
    string result = package.ReadString();
    if (package.GetPos() != package.Size() ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        !string.Equals(runId, probe.RunId, StringComparison.Ordinal) ||
        !string.Equals(actionId, probe.ActionId, StringComparison.Ordinal) ||
        !accepted || uid.IsNone() || !Finite(position) ||
        revision != 1 || count != 1) {
      active.FailProbe("container_spawn_response_invalid result=" + result);
      return;
    }
    probe.ContainerId = uid;
    active.CompleteProbe("container_spawn_accepted uid=" + uid +
        " revision=1 count=1 position=" + Format(position));
  }

  static void HandleTransactionRequest(long senderPeerId, ZPackage package) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Enabled() || !IsServer()) return;
    string runId = string.Empty;
    string actionId = string.Empty;
    ZDOID uid = ZDOID.None;
    try {
      package.SetPos(0);
      int schema = package.ReadInt();
      runId = package.ReadString();
      actionId = package.ReadString();
      uid = package.ReadZDOID();
      int expectedRevision = package.ReadInt();
      string operation = package.ReadString();
      string itemPrefab = package.ReadString();
      int requestedCount = package.ReadInt();
      if (package.GetPos() != package.Size() || schema != TransactionSchema ||
          !SafeToken(runId, 80) || !SafeToken(actionId, 80) || uid.IsNone() ||
          !string.Equals(runId, CurrentRunId(), StringComparison.Ordinal) ||
          operation != "take" || itemPrefab != ItemPrefab ||
          !active._serverByRun.TryGetValue(runId, out ServerContainer state) ||
          state.Zdo == null || state.Zdo.m_uid != uid ||
          !string.Equals(state.Zdo.GetString(RunTagHash, string.Empty),
              runId, StringComparison.Ordinal) ||
          !LogicalPeerCutoverRunner.TryGetCanonicalPeerReference(
              senderPeerId, out _, out Vector3 peerPosition) ||
          Vector3.Distance(peerPosition, state.Zdo.GetPosition()) >
              MaximumRequestDistance)
        throw new InvalidOperationException(
            "container_transaction_authority_invalid");

      PendingTransaction request = new() {
          RunId = runId,
          ActionId = actionId,
          Uid = uid,
          RequesterPeerId = senderPeerId,
          ExpectedRevision = expectedRevision,
          RequestedCount = requestedCount,
          BarrierKey = ContentionKey(runId, actionId, uid),
          ArrivalOrder = Interlocked.Increment(
              ref active._transactionArrivalOrder)
      };
      string receiptKey = TransactionKey(runId, senderPeerId, actionId);
      if (active._serverReceipts.TryGetValue(
              receiptKey, out TransactionReceipt prior)) {
        if (!SameTransaction(prior, request))
          throw new InvalidOperationException(
              "container_duplicate_payload_mismatch");
        active.Write("transaction_duplicate_replayed", actionId,
            "sender=" + senderPeerId + " uid=" + uid +
            " accepted=" + prior.Decision.Accepted +
            " result=" + prior.Decision.Result +
            " revision=" + prior.Decision.CanonicalRevision +
            " remaining=" + prior.Decision.RemainingCount +
            " granted=" + prior.Decision.GrantedCount);
        active.SendTransactionResult(senderPeerId, prior, duplicate: true);
        return;
      }

      if (active._pendingTransactions.TryGetValue(
              receiptKey, out PendingTransaction pending)) {
        if (!SameTransaction(pending, request))
          throw new InvalidOperationException(
              "container_duplicate_payload_mismatch");
      } else {
        active._pendingTransactions[receiptKey] = request;
      }

      if (!active._contentionGates.TryGetValue(
              request.BarrierKey, out ContainerContentionGate gate)) {
        gate = new ContainerContentionGate();
        active._contentionGates[request.BarrierKey] = gate;
      }
      ContainerContentionGateResult gateResult = gate.Register(senderPeerId);
      if (gateResult is ContainerContentionGateResult.InvalidPeer or
          ContainerContentionGateResult.TooManyPeers or
          ContainerContentionGateResult.ExcessCopy or
          ContainerContentionGateResult.AlreadyReleased)
        throw new InvalidOperationException(
            "container_contention_gate_" +
            gateResult.ToString().ToLowerInvariant());

      active.Write(
          gateResult == ContainerContentionGateResult.Held
              ? "transaction_contender_held"
              : gateResult == ContainerContentionGateResult.DuplicateHeld
                  ? "transaction_duplicate_held"
                  : "transaction_barrier_final_copy_held",
          actionId,
          "sender=" + senderPeerId + " uid=" + uid +
          " distinct_peers=" + gate.DistinctPeers +
          " total_copies=" + gate.TotalCopies +
          " mutation_held=" + (!gate.Released));
      if (gateResult == ContainerContentionGateResult.Released)
        active.ResolveContention(state, request.BarrierKey, gate);
    } catch (Exception exception) {
      active.Write("transaction_request_rejected", actionId,
          "sender=" + senderPeerId + " uid=" + uid + " reason=" +
          exception.GetType().Name + ":" + exception.Message);
      throw;
    }
  }

  void ResolveContention(
      ServerContainer state,
      string barrierKey,
      ContainerContentionGate gate) {
    List<PendingTransaction> contenders = _pendingTransactions.Values
        .Where(value => string.Equals(
            value.BarrierKey, barrierKey, StringComparison.Ordinal))
        .OrderBy(value => value.ArrivalOrder)
        .ToList();
    if (!gate.Released || contenders.Count !=
            ContainerContentionGate.RequiredPeers ||
        gate.TotalCopies != ContainerContentionGate.RequiredPeers *
            ContainerContentionGate.RequiredCopiesPerPeer)
      throw new InvalidOperationException(
          "container_contention_barrier_release_invalid");

    Write("contention_barrier_released", contenders[0].ActionId,
        "uid=" + state.Zdo.m_uid +
        " distinct_peers=" + gate.DistinctPeers +
        " total_copies=" + gate.TotalCopies +
        " mutation_held_until_release=true");

    List<TransactionReceipt> decisions = new();
    foreach (PendingTransaction contender in contenders) {
      ContainerTransactionDecision decision =
          ContainerTransactionPolicy.AdjudicateTake(
              contender.ExpectedRevision, state.Revision,
              state.RemainingCount, contender.RequestedCount);
      if (decision.Accepted) {
        Inventory inventory = state.Container?.GetInventory();
        if (inventory == null ||
            inventory.CountItems(state.ItemName) != state.RemainingCount)
          throw new InvalidOperationException(
              "container_canonical_inventory_precondition_failed");
        state.Zdo.SetOwner(ZNet.GetUID());
        ZdoJournalCutoverRunner.ApplyCanonicalMutationWithReceipt(
            state.Zdo, () => {
              inventory.RemoveItem(
                  state.ItemName, contender.RequestedCount);
              StoreInventoryPayload(inventory, state.Zdo);
              state.Zdo.Set(RevisionHash, decision.CanonicalRevision);
              state.Zdo.Set(CountHash, decision.RemainingCount);
              state.Zdo.SetOwner(0L);
            });
        if (inventory.CountItems(state.ItemName) != decision.RemainingCount ||
            !InventoryPayloadMatches(inventory, state.Zdo))
          throw new InvalidOperationException(
              "container_canonical_inventory_postcondition_failed");
        state.Revision = decision.CanonicalRevision;
        state.RemainingCount = decision.RemainingCount;
      }
      TransactionReceipt receipt = new() {
          RunId = contender.RunId,
          ActionId = contender.ActionId,
          Uid = contender.Uid,
          RequesterPeerId = contender.RequesterPeerId,
          ExpectedRevision = contender.ExpectedRevision,
          RequestedCount = contender.RequestedCount,
          Decision = decision
      };
      _serverReceipts[TransactionKey(
          contender.RunId,
          contender.RequesterPeerId,
          contender.ActionId)] = receipt;
      decisions.Add(receipt);
      Write(
          decision.Accepted ? "transaction_committed" : "transaction_rejected",
          contender.ActionId,
          "sender=" + contender.RequesterPeerId +
          " uid=" + contender.Uid +
          " expected_revision=" + contender.ExpectedRevision +
          " accepted=" + decision.Accepted +
          " result=" + decision.Result +
          " revision=" + decision.CanonicalRevision +
          " remaining=" + decision.RemainingCount +
          " granted=" + decision.GrantedCount +
          " inventory_serialization=explicit" +
          " owner=" + state.Zdo.GetOwner());
    }

    foreach (TransactionReceipt receipt in decisions) {
      SendTransactionResult(
          receipt.RequesterPeerId, receipt, duplicate: false);
      Write("transaction_duplicate_replayed", receipt.ActionId,
          "sender=" + receipt.RequesterPeerId + " uid=" + receipt.Uid +
          " accepted=" + receipt.Decision.Accepted +
          " result=" + receipt.Decision.Result +
          " revision=" + receipt.Decision.CanonicalRevision +
          " remaining=" + receipt.Decision.RemainingCount +
          " granted=" + receipt.Decision.GrantedCount);
      SendTransactionResult(
          receipt.RequesterPeerId, receipt, duplicate: true);
    }
    foreach (PendingTransaction contender in contenders)
      _pendingTransactions.Remove(TransactionKey(
          contender.RunId,
          contender.RequesterPeerId,
          contender.ActionId));
  }

  static bool SameTransaction(
      TransactionReceipt receipt, PendingTransaction request) =>
      receipt.Uid == request.Uid &&
      receipt.ExpectedRevision == request.ExpectedRevision &&
      receipt.RequestedCount == request.RequestedCount &&
      string.Equals(
          receipt.RunId, request.RunId, StringComparison.Ordinal) &&
      string.Equals(
          receipt.ActionId, request.ActionId, StringComparison.Ordinal) &&
      receipt.RequesterPeerId == request.RequesterPeerId;

  static bool SameTransaction(
      PendingTransaction held, PendingTransaction request) =>
      held.Uid == request.Uid &&
      held.ExpectedRevision == request.ExpectedRevision &&
      held.RequestedCount == request.RequestedCount &&
      string.Equals(held.RunId, request.RunId, StringComparison.Ordinal) &&
      string.Equals(
          held.ActionId, request.ActionId, StringComparison.Ordinal) &&
      held.RequesterPeerId == request.RequesterPeerId;

  void SendTransactionResult(
      long targetPeerId,
      TransactionReceipt receipt,
      bool duplicate) {
    ContainerTransactionDecision decision = receipt.Decision;
    ZPackage result = new();
    result.Write(TransactionSchema);
    result.Write(receipt.RunId);
    result.Write(receipt.ActionId);
    result.Write(receipt.Uid);
    result.Write(receipt.RequesterPeerId);
    result.Write(receipt.ExpectedRevision);
    result.Write(decision.CanonicalRevision);
    result.Write(decision.RemainingCount);
    result.Write(decision.GrantedCount);
    result.Write(decision.Accepted);
    result.Write(duplicate);
    result.Write(decision.Result);
    result.SetPos(0);
    if (!_routedRpc.InvokeTyped(
            receipt.ActionId,
            () => ZRoutedRpc.instance.InvokeRoutedRPC(
                targetPeerId,
                ValheimRoutedRpcAdmissions.ModContainerTransactionResult,
                new object[] { result })))
      throw new InvalidOperationException(
          "container_transaction_result_queue_failed");
  }

  static void HandleTransactionResult(long senderPeerId, ZPackage package) {
    ContainerCutoverRunner active = Volatile.Read(ref _active);
    ContainerProbe probe = active?._probe;
    if (active == null || probe == null || probe.Terminal ||
        probe.Mode != "contend_take" || IsServer()) return;
    package.SetPos(0);
    int schema = package.ReadInt();
    string runId = package.ReadString();
    string actionId = package.ReadString();
    ZDOID uid = package.ReadZDOID();
    long requesterPeerId = package.ReadLong();
    int expectedRevision = package.ReadInt();
    int canonicalRevision = package.ReadInt();
    int remainingCount = package.ReadInt();
    int grantedCount = package.ReadInt();
    bool accepted = package.ReadBool();
    bool duplicate = package.ReadBool();
    string result = package.ReadString();
    if (package.GetPos() != package.Size() || schema != TransactionSchema ||
        senderPeerId != ZNet.instance.GetServerPeer()?.m_uid ||
        requesterPeerId != ZDOMan.GetSessionID() ||
        !string.Equals(runId, probe.RunId, StringComparison.Ordinal) ||
        !string.Equals(actionId, probe.ActionId, StringComparison.Ordinal) ||
        uid != probe.ContainerId || expectedRevision != 1 ||
        canonicalRevision != 2 || remainingCount != 0 ||
        grantedCount is < 0 or > 1 ||
        result is not ("committed" or "stale_revision")) {
      active.FailProbe("container_transaction_result_invalid");
      return;
    }

    if (!duplicate) {
      if (probe.SawOriginal) {
        active.FailProbe("container_duplicate_original_result");
        return;
      }
      probe.SawOriginal = true;
      probe.Accepted = accepted;
      probe.CanonicalRevision = canonicalRevision;
      probe.RemainingCount = remainingCount;
      probe.GrantedCount = grantedCount;
      probe.Result = result;
      if (accepted) {
        string creditKey = ReceiptKey(requesterPeerId, actionId);
        GameObject prefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
        if (prefab == null || !active._creditedTransactions.Add(creditKey) ||
            Player.m_localPlayer.GetInventory().AddItem(prefab, grantedCount) ==
                false) {
          active.FailProbe("container_authoritative_inventory_credit_failed");
          return;
        }
      }
    } else {
      if (!probe.SawOriginal || probe.SawDuplicate) {
        active.FailProbe("container_duplicate_result_order_invalid");
        return;
      }
      probe.SawDuplicate = true;
      probe.DuplicateAccepted = accepted;
      probe.DuplicateCanonicalRevision = canonicalRevision;
      probe.DuplicateRemainingCount = remainingCount;
      probe.DuplicateGrantedCount = grantedCount;
      probe.DuplicateResult = result;
    }
    active.Write(
        duplicate ? "transaction_duplicate_received" :
            "transaction_result_received",
        actionId,
        "uid=" + uid + " accepted=" + accepted +
        " duplicate=" + duplicate + " result=" + result +
        " revision=" + canonicalRevision +
        " remaining=" + remainingCount + " granted=" + grantedCount +
        " inventory_now=" + CountLocalItemUnits());
  }

  static bool TryFindRunContainer(
      string runId, out Container container, out ZDO zdo) {
    container = null;
    zdo = null;
    foreach (Container candidate in Resources.FindObjectsOfTypeAll<Container>()) {
      if (candidate == null || candidate.gameObject == null ||
          !candidate.gameObject.activeInHierarchy) continue;
      ZNetView candidateView = candidate.GetComponent<ZNetView>() ??
          candidate.GetComponentInParent<ZNetView>();
      ZDO candidateZdo = candidateView?.GetZDO();
      if (candidateZdo == null || !string.Equals(
              candidateZdo.GetString(RunTagHash, string.Empty),
              runId, StringComparison.Ordinal)) continue;
      container = candidate;
      zdo = candidateZdo;
      return true;
    }
    return false;
  }

  static bool CanonicalShape(
      Container container, ZDO zdo, int revision, int count) {
    if (container == null || zdo == null ||
        zdo.GetInt(RevisionHash, 0) != revision ||
        zdo.GetInt(CountHash, -1) != count) return false;
    GameObject prefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
    string itemName = SharedItemName(prefab);
    Inventory inventory = container.GetInventory();
    return inventory != null && !string.IsNullOrEmpty(itemName) &&
        inventory.CountItems(itemName) == count;
  }

  void MaybeWriteProgress(ContainerProbe probe, float now) {
    if (probe == null || now < probe.NextDiagnosticAt) return;
    probe.NextDiagnosticAt = now + 5.0f;
    Write("probe_progress", probe.ActionId,
        "mode=" + probe.Mode +
        " original=" + probe.SawOriginal +
        " duplicate=" + probe.SawDuplicate + " " +
        DescribeRunContainer(probe.RunId));
  }

  static string DescribeRunContainer(string runId) {
    if (!TryFindRunContainer(
            runId, out Container container, out ZDO zdo))
      return "container_found=false";
    GameObject prefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
    string itemName = SharedItemName(prefab);
    Inventory inventory = container.GetInventory();
    int actual = inventory == null || string.IsNullOrEmpty(itemName)
        ? -1 : inventory.CountItems(itemName);
    return "container_found=true uid=" + zdo.m_uid +
        " revision=" + zdo.GetInt(RevisionHash, 0) +
        " count=" + zdo.GetInt(CountHash, -1) +
        " actual_inventory=" + actual +
        " owner=" + zdo.GetOwner() +
        " data_revision=" + zdo.DataRevision +
        " owner_revision=" + zdo.OwnerRevision;
  }

  static string SharedItemName(GameObject prefab) =>
      prefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name ??
      string.Empty;

  static void StoreInventoryPayload(Inventory inventory, ZDO zdo) {
    if (inventory == null || zdo == null)
      throw new InvalidOperationException(
          "container_inventory_serialization_target_missing");
    ZPackage package = new();
    inventory.Save(package);
    zdo.Set(ZDOVars.s_items, package.GetBase64());
  }

  static bool InventoryPayloadMatches(Inventory inventory, ZDO zdo) {
    if (inventory == null || zdo == null) return false;
    ZPackage package = new();
    inventory.Save(package);
    return string.Equals(
        zdo.GetString(ZDOVars.s_items, string.Empty),
        package.GetBase64(),
        StringComparison.Ordinal);
  }

  static int CountLocalItemUnits() {
    try {
      Inventory inventory = Player.m_localPlayer?.GetInventory();
      GameObject prefab = ObjectDB.instance?.GetItemPrefab(ItemPrefab);
      string itemName = SharedItemName(prefab);
      return inventory == null || string.IsNullOrEmpty(itemName)
          ? -1 : inventory.CountItems(itemName);
    } catch {
      return -1;
    }
  }

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

  static string ReceiptKey(long peerId, string actionId) =>
      peerId.ToString(CultureInfo.InvariantCulture) + ":" + actionId;

  static string TransactionKey(
      string runId, long peerId, string actionId) =>
      runId + ":" + ReceiptKey(peerId, actionId);

  static string ContentionKey(
      string runId, string actionId, ZDOID uid) =>
      runId + ":" + actionId + ":" + uid;

  static bool Enabled() =>
      (PluginConfig.RoutedRpcCutoverEnabled?.Value == true ||
       NativeAutotestRequest.ActiveRoutedRpcCutover) &&
      (PluginConfig.ZdoJournalCutoverEnabled?.Value == true ||
       NativeAutotestRequest.ActiveZdoJournalCutover);

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

  sealed class ContainerProbe {
    public string RunId;
    public string ActionId;
    public string Mode;
    public float StartedAt;
    public float DeadlineAt;
    public float NextDiagnosticAt;
    public int InventoryBefore;
    public ZDOID ContainerId;
    public bool RequestSent;
    public bool NativeTakeAllSuppressed;
    public bool SawOriginal;
    public bool SawDuplicate;
    public bool Accepted;
    public bool DuplicateAccepted;
    public int CanonicalRevision;
    public int DuplicateCanonicalRevision;
    public int RemainingCount;
    public int DuplicateRemainingCount;
    public int GrantedCount;
    public int DuplicateGrantedCount;
    public string Result = string.Empty;
    public string DuplicateResult = string.Empty;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class ServerContainer {
    public string RunId;
    public string ActionId;
    public GameObject Instance;
    public Container Container;
    public ZDO Zdo;
    public string ItemName;
    public int Revision;
    public int RemainingCount;
  }

  sealed class TransactionReceipt {
    public string RunId;
    public string ActionId;
    public ZDOID Uid;
    public long RequesterPeerId;
    public int ExpectedRevision;
    public int RequestedCount;
    public ContainerTransactionDecision Decision;
  }

  sealed class PendingTransaction {
    public string RunId;
    public string ActionId;
    public ZDOID Uid;
    public long RequesterPeerId;
    public int ExpectedRevision;
    public int RequestedCount;
    public string BarrierKey;
    public long ArrivalOrder;
  }
}

[HarmonyPatch(typeof(Container), nameof(Container.TakeAll))]
static class ContainerTakeAllCutoverPatch {
  [HarmonyPrefix]
  static bool Prefix(
      Container __instance, Humanoid character, ref bool __result) =>
      !ContainerCutoverRunner.TryHandleTakeAll(
          __instance, character, ref __result);
}
