namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

using UnityEngine;

// I3 (P4) — Outbound REDIRECT. BEHAVIOUR-CHANGING, server-side (am4) only, rollback-gated.
//
// Where the I2 pin skipped ownership transfers, this runner suppresses the NATIVE SEND of a
// tagged class of ZDOs and emits the wire-equivalent payload to the Lumberjacks gateway instead:
// a Harmony postfix on ZDOMan.CreateSyncList removes allowlisted-prefab ZDOs from the freshly
// built toSync list before SendZDOs serializes it, replicates the native per-peer bookkeeping for
// each removed ZDO, and posts {seq, uid, owner, revisions, prefab, pos, body_b64} batches to
// POST /valheim/zdo-redirect/receipts. Grounded on the decompiled assembly (ZDOMan:724-790
// serialization loop, :893 CreateSyncList) — see the I3 design block in
// fieldlab/VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md.
//
// SAFETY MODEL (why this cannot corrupt state or the save):
//   * The send path writes NO persisted ZDO state (decompile-verified): the runner touches only
//     the same runtime peer bookkeeping the native loop would have touched (m_zdos ack,
//     m_forceSend removal). ZDO.Serialize only READS ZDOExtraData; the world save is the separate
//     PrepareSave/SaveAsync clone path. Nothing here mutates a ZDO.
//   * SUPPRESS-WITH-ACK: each suppressed ZDO gets the native ack replicated
//     (peer.m_zdos[uid] = PeerZDOInfo(DataRevision, OwnerRevision, now) — mirrors ZDOMan:780),
//     so vanilla re-offers it only when its revision actually changes. Suppressed-count therefore
//     equals exactly what native would have sent — the gate math. Without the ack, ShouldSend
//     would re-select every tick (duplicate storm). If the reflection handles for the private
//     ZDOPeer internals are unavailable, Start() REFUSES — fail-safe is vanilla behaviour.
//   * TAG-SCOPED + FAIL-SAFE EMPTY: only prefabs on the explicit allowlist are ever suppressed.
//     An EMPTY allowlist refuses to start (suppressing everything would freeze world sync for the
//     client) — the opposite default of the pin's "blank = any", deliberately.
//   * SERVER-ONLY: inert unless ZNet.IsServer() — a client with the flag accidentally on
//     changes nothing.
//   * ROLLBACK IS BUILT INTO THE WINDOW: zdoRedirectActiveSeconds < the probe window means the
//     suppression auto-disarms mid-capture, and the probe (whose CreateSyncList postfix runs
//     AFTER this one — Priority.High here — and thus sees the post-filter list) records native
//     sends of the tagged prefab RESUMING in the same window. That is P4 step 11's rollback
//     rehearsal, hands-free. Config flag zdoRedirectEnabled=false is the standing rollback.
//   * OBSERVE-DURING-CHANGE (ADR 0002): three independent measures in one window — this runner's
//     redirect-send.jsonl rows == Lumberjacks distinct-seq receipts, while the probe shows zero
//     native sends of the tagged prefab during the active sub-window.
//
// Coupled to the netcode-probe capture window (starts/stops in lockstep), so one launch+join
// exercises suppression, emission, rollback, and the negative control in a single window.
public sealed class ZdoRedirectRunner : IDisposable {
  const int DefaultMaxRows = 20000;
  const int PostBatchMax = 200;
  const int PostAttemptsMax = 3;

  // Set while a redirect run is active; the static postfix reads it and no-ops when null.
  static volatile ZdoRedirectRunner _active;

  // Reflection handles for ZDOMan's private nested ZDOPeer (members are public; the TYPE is
  // private, so the ack has to go through reflection). Resolved once; all-or-nothing checked at
  // Start so a partial resolve can never half-ack.
  static readonly Type ZdoPeerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
  static readonly Type PeerZdoInfoType =
      ZdoPeerType == null ? null : AccessTools.Inner(ZdoPeerType, "PeerZDOInfo");
  static readonly FieldInfo ZdosField =
      ZdoPeerType == null ? null : AccessTools.Field(ZdoPeerType, "m_zdos");
  static readonly FieldInfo ForceSendField =
      ZdoPeerType == null ? null : AccessTools.Field(ZdoPeerType, "m_forceSend");
  static readonly FieldInfo NetworkPeerField =
      ZdoPeerType == null ? null : AccessTools.Field(ZdoPeerType, "m_peer");
  static readonly ConstructorInfo PeerInfoCtor =
      PeerZdoInfoType == null
          ? null
          : AccessTools.Constructor(
              PeerZdoInfoType, new[] { typeof(uint), typeof(ushort), typeof(float) });
  static readonly MethodInfo ZdosSetItem =
      ZdosField == null ? null : ZdosField.FieldType.GetMethod("set_Item");

  // Read handles for the co-presence fan-out's per-observer delivered-revision check (ADR 0013): does
  // a given peer's m_zdos already hold this ZDO at this data revision? All fail-soft — a missing
  // handle yields a null delivered revision (treated as "never delivered"), never a crash.
  static readonly MethodInfo ZdosContainsKey =
      ZdosField == null ? null : ZdosField.FieldType.GetMethod("ContainsKey");
  static readonly MethodInfo ZdosGetItem =
      ZdosField == null ? null : ZdosField.FieldType.GetMethod("get_Item");
  static readonly FieldInfo PeerZdoInfoDataRev =
      PeerZdoInfoType == null ? null : PeerZdoInfoType.GetField("m_dataRevision");
  static readonly FieldInfo PeerZdoInfoOwnerRev =
      PeerZdoInfoType == null ? null : PeerZdoInfoType.GetField("m_ownerRevision");
  // ZDOMan.m_peers (List<ZDOPeer>) — the connected-observer set the fan-out serves.
  static readonly FieldInfo ZdoManPeersField = AccessTools.Field(typeof(ZDOMan), "m_peers");

  static bool ReflectionReady =>
      ZdosField != null && ForceSendField != null && PeerInfoCtor != null && ZdosSetItem != null;

  readonly object _lock = new();
  readonly ConcurrentQueue<Dictionary<string, object>> _postQueue = new();
  readonly Dictionary<int, PriorityDescriptor> _priorityDescriptors = new();

  TelemetryCoordinator _coordinator;
  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  DateTime _startedUtc;
  float _stopAtTime = -1.0f;
  int _maxRows = DefaultMaxRows;
  HashSet<int> _prefabFilter;
  bool _allPrefabs;

  // Landmark reach: prefab stable-hash -> reach in meters, parsed from ZdoLandmarkReach on arm.
  // Null or absent => the prefab is not a landmark (reach 0). A landmark is admitted at redirect
  // time when the observer is within its reach even if its rank exceeds the max — the parallel
  // reliable-lane exemption in ZdoIntegrationContract.Admits. Static, so it adds no per-tick cost.
  Dictionary<int, float> _landmarkReach;

  // Mid-band thinning state: (recipient host, zdo uid) -> last emit Time.time. Consulted by
  // ZdoBandPolicy to hold a mid-band ZDO until zdoThinHz elapses. Touched only from Process (server
  // main thread, single-threaded per CreateSyncList pass), so no lock. Bounded by a coarse clear at
  // LastEmitMaxEntries rather than a per-peer disconnect prune — that finer cleanup is a v.5 item;
  // a clear only costs one extra emit per mid-band object. Cleared on arm.
  readonly Dictionary<string, float> _lastEmit = new();
  const int LastEmitMaxEntries = 50000;

  string _windowId = string.Empty;
  string _endpoint = string.Empty;
  string _sourceInstance = string.Empty;

  long _seq;
  long _suppressed;
  long _importanceAllowed;
  long _importanceRejected;
  long _bandDropped;   // far-band ZDOs suppressed from native but not emitted (zdoBandShaping)
  long _bandHeld;      // mid-band ZDOs held this pass by the thin rate (increment 2)
  long _playerFastLaneCandidates;
  long _playerFastLaneEmitted;
  long _ackFailures;
  long _postedOk;
  long _postFailedBatches;
  long _requeued;
  long _dropped;
  long _rowsWritten;
  bool _capped;
  int _postInFlight;
  int _primaryResetInFlight;
  volatile bool _primaryWindowReady;
  int _lastPrimaryPeerCount = -1;
  float _nextPrimaryResetAt;
  string _primaryResetError = string.Empty;

  public bool IsRunning => _running;

  /// <summary>
  /// Establishes a delivery epoch for the permanent primary window while no
  /// clients are connected. Redirect sequence numbers are process-local, so a
  /// durable queue from a previous Valheim process must be cleared before the
  /// counter can safely start at one again. The reset runs off the Unity thread;
  /// Start() remains fail-safe and refuses primary suppression until it succeeds.
  /// </summary>
  public void MaintainPrimaryWindow(float now) {
    if (!PluginConfig.ZdoRedirectEnabled.Value
        || !string.Equals(TelemetryCoordinator.EffectiveCutoverMode(), "lumberjacks-primary",
            StringComparison.OrdinalIgnoreCase)
        || ZNet.instance == null || !ZNet.instance.IsServer()) return;

    int peers = ZNet.instance.GetPeers()?.Count ?? 0;
    if (peers > 0) {
      _lastPrimaryPeerCount = peers;
      return;
    }

    if (_lastPrimaryPeerCount > 0) {
      _primaryWindowReady = false;
      _nextPrimaryResetAt = now + 2.0f;
    }
    _lastPrimaryPeerCount = 0;
    if (_primaryWindowReady || now < _nextPrimaryResetAt || !_postQueue.IsEmpty
        || Interlocked.CompareExchange(ref _postInFlight, 0, 0) != 0
        || Interlocked.CompareExchange(ref _primaryResetInFlight, 1, 0) != 0) return;

    string endpoint = PluginConfig.ZdoRedirectEndpoint.Value?.Trim().TrimEnd('/') ?? string.Empty;
    string window = PluginConfig.ZdoRedirectWindowId.Value?.Trim() ?? string.Empty;
    if (endpoint.Length == 0 || window.Length == 0) {
      _primaryResetError = "primary reset requires zdoRedirectEndpoint and zdoRedirectWindowId";
      Interlocked.Exchange(ref _primaryResetInFlight, 0);
      _nextPrimaryResetAt = now + 5.0f;
      return;
    }

    _ = Task.Run(() => {
      try {
        SendHttpPostViaSocket(endpoint + "/valheim/zdo-redirect/reset/"
            + Uri.EscapeDataString(window), string.Empty);
        lock (_lock) {
          _seq = 0;
          _primaryResetError = string.Empty;
        }
        _primaryWindowReady = true;
        ComfyNetworkSense.LogInfo("ZDO primary delivery window reset while server is empty: " + window);
      } catch (Exception exception) {
        _primaryWindowReady = false;
        lock (_lock) _primaryResetError = exception.GetType().Name + ": " + exception.Message;
        ComfyNetworkSense.LogWarning("ZDO primary delivery window reset failed; native path remains armed: "
            + _primaryResetError);
      } finally {
        Interlocked.Exchange(ref _primaryResetInFlight, 0);
      }
    });
  }

  public string Start(TelemetryCoordinator coordinator, int? maxRowsOverride = null) {
    if (!ReflectionReady) {
      return "ZDO redirect REFUSED: ZDOPeer reflection handles unavailable (game update?). "
          + "Suppress-without-ack would break the gate math, so nothing was armed.";
    }
    if (ZNet.instance == null || !ZNet.instance.IsServer()) {
      return "ZDO redirect REFUSED: server-side only (this instance is not the server).";
    }

    bool allPrefabs = string.Equals(PluginConfig.ZdoRedirectPrefabs.Value?.Trim(), "*", StringComparison.Ordinal);
    HashSet<int> filter = allPrefabs ? new HashSet<int>() : BuildPrefabFilter(PluginConfig.ZdoRedirectPrefabs.Value);
    if (!allPrefabs && filter == null) {
      return "ZDO redirect REFUSED: zdoRedirectPrefabs is empty. An empty allowlist would "
          + "suppress ALL ZDO sync (world-freeze for the client); name the tagged prefab(s).";
    }
    if (allPrefabs
        && string.Equals(TelemetryCoordinator.EffectiveCutoverMode(), "lumberjacks-primary",
            StringComparison.OrdinalIgnoreCase)
        && !_primaryWindowReady) {
      return "ZDO redirect REFUSED: primary delivery window has not completed its empty-server "
          + "sequence reset. Native delivery remains active."
          + (string.IsNullOrWhiteSpace(_primaryResetError) ? string.Empty : " Last error: " + _primaryResetError);
    }

    lock (_lock) {
      if (_running) {
        return $"ZDO redirect already running: {StatusLineLocked()}";
      }

      _coordinator = coordinator;
      _running = true;
      _status = "redirecting";
      _lastError = string.Empty;
      _startedUtc = DateTime.UtcNow;
      _maxRows = Mathf.Clamp(maxRowsOverride ?? DefaultMaxRows, 0, 200000);
      _prefabFilter = filter;
      _allPrefabs = allPrefabs;
      _landmarkReach = BuildLandmarkReach(PluginConfig.ZdoLandmarkReach.Value);
      _endpoint = PluginConfig.ZdoRedirectEndpoint.Value.TrimEnd('/');
      _sourceInstance = coordinator?.SessionId ?? "unknown";
      string configuredWindow = PluginConfig.ZdoRedirectWindowId.Value;
      _windowId = string.IsNullOrWhiteSpace(configuredWindow)
          ? "i3-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
          : configuredWindow.Trim();
      float activeSeconds = Math.Max(0.0f, PluginConfig.ZdoRedirectActiveSeconds.Value);
      _stopAtTime = activeSeconds > 0.0f ? Time.time + activeSeconds : -1.0f;
      _seq = 0;
      _suppressed = 0;
      _importanceAllowed = 0;
      _importanceRejected = 0;
      _bandDropped = 0;
      _bandHeld = 0;
      _ackFailures = 0;
      _postedOk = 0;
      _postFailedBatches = 0;
      _requeued = 0;
      _dropped = 0;
      _rowsWritten = 0;
      _capped = false;
      _priorityDescriptors.Clear();
      _lastEmit.Clear();
    }

    _active = this;
    coordinator?.RecordZdoRedirect(BuildStatusRow("redirect_start"));
    return
        "ZDO redirect ARMED (behaviour-changing, server-side). Suppressing native send for "
        + (_allPrefabs ? "ALL prefabs" : $"{_prefabFilter.Count} prefab(s)")
        + $" -> {_endpoint} window={_windowId}"
        + (_stopAtTime > 0.0f
            ? $", auto-disarms after {PluginConfig.ZdoRedirectActiveSeconds.Value:0.##}s (in-window rollback rehearsal)."
            : ", no active-window cap (disarms with the probe window).")
        + " Rollback: zdoRedirectEnabled=false.";
  }

  public string Stop() {
    return StopInternal("redirect_stop");
  }

  string StopInternal(string eventType) {
    IDictionary<string, object> stopRow;
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return "ZDO redirect is not running.";
      }

      _running = false;
      _status = eventType == "redirect_auto_stop" ? "auto_stopped" : "stopped";
      _active = null;
      coordinator = _coordinator;
      stopRow = BuildStatusRowLocked(eventType);
    }

    FlushQueue(force: true);
    coordinator?.RecordZdoRedirect(stopRow);
    return "ZDO redirect disarmed; native send path restored. "
        + "Compare redirect-send.jsonl seq against the gateway's distinct_seq for the gate.";
  }

  public string GetStatus() {
    lock (_lock) {
      return StatusLineLocked();
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_lock) {
      return BuildStatusRowLocked(eventType);
    }
  }

  // --- Static postfix entry point -------------------------------------------------------

  // Called by the CreateSyncList postfix (Priority.High, so the netcode probe's postfix on the
  // same method observes the POST-filter list). peer is ZDOMan.ZDOPeer, typed object because the
  // class is private.
  public static void HandleCreateSyncList(object peer, List<ZDO> toSync) {
    ZdoRedirectRunner active = _active;
    if (active == null || peer == null || toSync == null || toSync.Count == 0) {
      return;
    }
    active.Process(peer, toSync);
  }

  // --- Core ------------------------------------------------------------------------------

  void Process(object peer, List<ZDO> toSync) {
    if (ZNet.instance == null || !ZNet.instance.IsServer()) {
      return;
    }

    // In-window rollback rehearsal: past the active sub-window, disarm and let native resume
    // while the probe is still recording.
    float stopAt = _stopAtTime;
    if (stopAt > 0.0f && Time.time >= stopAt) {
      StopInternal("redirect_auto_stop");
      return;
    }

    HashSet<int> filter = _prefabFilter;
    bool allPrefabs = _allPrefabs;
    if (!allPrefabs && (filter == null || filter.Count == 0)) {
      return;
    }

    // The observing peer's recipient identity (SteamID host) — the mid-band thin clock is keyed on
    // (recipient, zdo uid). Computed once per pass, only when band-shaping is armed.
    string recipient = PluginConfig.ZdoBandShapingEnabled.Value ? RecipientFor(peer) : null;

    // Co-presence (ADR 0013): snapshot the connected ZDOPeers once per pass when shadow or fan-out is
    // armed. ZDOPeer (not ZNetPeer) because Redirect/SuppressNative and the delivered-revision read
    // operate on ZDOMan's per-peer bookkeeping. Null when neither is armed (zero cost by default).
    bool coPresenceShadow = PluginConfig.ZdoCoPresenceShadowEnabled.Value;
    bool coPresenceFanout = PluginConfig.ZdoCoPresenceFanoutEnabled.Value;
    List<object> coPresencePeers =
        (coPresenceShadow || coPresenceFanout) ? SafeGetZdoPeers() : null;

    // CreateSyncList has already passed through Valheim's ServerSortSendZDOS priority
    // ordering. Remove from the source list backwards for index safety, but redirect in
    // the original forward order; the old loop emitted the lowest-priority tail first.
    List<int> selectedIndexes = new();
    for (int i = 0; i < toSync.Count; i++) {
      ZDO zdo = toSync[i];
      if (zdo == null || (!allPrefabs && !filter.Contains(SafePrefab(zdo)))) {
        continue;
      }

      ClassifiedZdo candidate = ClassifyCandidate(peer, zdo);
      RecordImportanceDecision(candidate, "importance_candidate");
      // Admit if the rank gate allows OR this is a landmark within its reach of the observer. The
      // landmark exemption is the reliable-lane presence path (landmark-reach-design.md): a static
      // marked object reaches clients a hard interest cut would never announce it to, at no per-tick
      // cost. Non-landmarks (reach 0) fall back to exactly the old rank-only decision.
      if (!ZdoIntegrationContract.Admits(
              candidate.PriorityRank, PluginConfig.ZdoRedirectMaxPriorityRank.Value,
              candidate.DistanceMeters, candidate.LandmarkReachMeters)) {
        lock (_lock) _importanceRejected++;
        RecordImportanceDecision(candidate, "importance_rejected");
        continue;
      }

      lock (_lock) _importanceAllowed++;
      RecordImportanceDecision(candidate, "importance_allowed");

      // Co-presence (ADR 0013): evaluate every connected observer once. The shadow records the
      // evidence with ZERO delivery change; fan-out emits a read copy to each in-band observer so the
      // one pass that sees a contended ZDO serves ALL co-located players. Both share the same
      // evaluation, so the shadow predicts the fan-out exactly. Fan-out REPLACES the single-recipient
      // path for this candidate (it still removes the ZDO from the exposing peer's toSync and acks it).
      if (coPresencePeers != null && coPresencePeers.Count > 0) {
        IReadOnlyList<FanoutObserverDecision> decisions =
            EvaluateObservers(peer, candidate, coPresencePeers);
        if (coPresenceShadow) {
          RecordCoPresenceShadow(peer, candidate, coPresencePeers, decisions);
        }
        if (coPresenceFanout) {
          selectedIndexes.Add(i);
          ApplyFanOut(peer, candidate, coPresencePeers, decisions);
          continue;
        }
      }

      // Every path below removes the ZDO from toSync (selectedIndexes) so native never sends it.
      // Band-shaping then splits EMIT (redirect to the gateway) from SUPPRESS-only (ack, no emit).
      // Suppress-only must NOT touch the gate seq/_suppressed counters — those count EMITTED
      // redirects and the gate reads gateway distinct_seq against them, so a suppressed-but-unemitted
      // ZDO that bumped seq would read as false loss.
      selectedIndexes.Add(i);
      if (!PluginConfig.ZdoBandShapingEnabled.Value) {
        // Emit the matching redirect record before classifying the rest of a potentially enormous
        // initial sync list, so candidate/allow rows don't fill the bounded telemetry queue before
        // any submission row is observable.
        Redirect(peer, candidate);
        continue;
      }

      bool playerFastLane = PluginConfig.ZdoPlayerFastLaneEnabled.Value && IsPlayerCharacterZdo(zdo);

      // Mid-band thinning consults the per-(recipient, uid) last-emit clock; near always emits, far
      // drops, landmarks always emit (ZdoBandPolicy). A first sighting has no entry (-1) and emits.
      string emitKey = recipient + "|" + zdo.m_uid.ToString();
      float lastEmit = _lastEmit.TryGetValue(emitKey, out float t) ? t : -1.0f;
      ZdoBandAction band = ZdoBandPolicy.Classify(
          candidate.DistanceMeters,
          PluginConfig.ZdoInnerRadiusMeters.Value,
          PluginConfig.ZdoOuterRadiusMeters.Value,
          candidate.LandmarkReachMeters,
          Time.time,
          lastEmit,
          ThinIntervalSeconds(),
          playerFastLane);
      if (playerFastLane) {
        lock (_lock) _playerFastLaneCandidates++;
      }
      if (ZdoBandPolicy.Emits(band)) {
        Redirect(peer, candidate);
        if (band == ZdoBandAction.PlayerFastLane) {
          lock (_lock) _playerFastLaneEmitted++;
        } else if (band == ZdoBandAction.EmitThinned) {
          // Reset the mid-band clock only on an actual thinned emit (near/landmark don't gate on it).
          if (_lastEmit.Count >= LastEmitMaxEntries) _lastEmit.Clear();
          _lastEmit[emitKey] = Time.time;
        }
      } else {
        SuppressNative(peer, zdo);   // Drop / HoldThinned: remove+ack, do NOT emit
        lock (_lock) {
          if (band == ZdoBandAction.Drop) _bandDropped++; else _bandHeld++;
        }
      }
      RecordBandDecision(candidate, band);
    }

    for (int i = selectedIndexes.Count - 1; i >= 0; i--)
      toSync.RemoveAt(selectedIndexes[i]);

    TryFlushQueue();
  }

  // The destination peer's identity as the SERVER derives it: the socket host name, which is a
  // bare SteamID64 and the same value vanilla feeds VerifySessionTicket (ZNet.decompiled.cs:833,
  // 882) and the Lumberjacks roster gate keys on. Deliberately NOT the ZDOID owner or m_uid --
  // that is a client-supplied ZDOMan.GetSessionID() and not a SteamID at all.
  //
  // We stamp an identity, never a partition. The Gateway owns the map from SteamID to its own
  // opaque recipient id; the mod cannot know it and must not guess, which is why this stays a
  // plain host string. That also makes this change safe to deploy on its own: while
  // ValheimQueue:ProducerEmitsRecipients is false the Gateway pins ingest to `legacy` regardless
  // of what we stamp, so the mod can ship first and the cutover stays a single flag flip.
  //
  // Unresolvable peer degrades to the legacy subject rather than throwing or inventing a label:
  // a recipient-less envelope belongs where a consumer can still reach it.
  static string RecipientFor(object peer) {
    try {
      ZNetPeer netPeer = NetworkPeerField?.GetValue(peer) as ZNetPeer;
      string host = netPeer?.m_socket?.GetHostName();
      return string.IsNullOrEmpty(host) ? ZdoIntegrationContract.LegacyRecipient : host;
    } catch (Exception) {
      return ZdoIntegrationContract.LegacyRecipient;
    }
  }

  // The SUPPRESS half of a redirect, split out from Redirect so band-shaping can suppress a far or
  // held ZDO from Valheim's native send WITHOUT emitting it to the gateway. Replicates the native ack
  // (ZDOMan:767+780) — writes peer.m_zdos[uid] and drops it from m_forceSend — so vanilla re-offers
  // this ZDO only when its revision changes, never as a per-tick storm. Removing a ZDO from toSync
  // WITHOUT this ack is the duplicate-storm failure mode, so every band action that removes from
  // toSync MUST call this. Returns whether the ack succeeded (false increments _ackFailures).
  bool SuppressNative(object peer, ZDO zdo) {
    try {
      object infoBox = PeerInfoCtor.Invoke(
          new object[] { zdo.DataRevision, zdo.OwnerRevision, Time.time });
      object zdosDictionary = ZdosField.GetValue(peer);
      ZdosSetItem.Invoke(zdosDictionary, new[] { (object) zdo.m_uid, infoBox });
      HashSet<ZDOID> forceSend = (HashSet<ZDOID>) ForceSendField.GetValue(peer);
      forceSend.Remove(zdo.m_uid);
      return true;
    } catch (Exception exception) {
      lock (_lock) {
        _ackFailures++;
        _lastError = "ack: " + exception.GetType().Name + ": " + exception.Message;
      }
      return false;
    }
  }

  void Redirect(object peer, ClassifiedZdo candidate) {
    ZDO zdo = candidate.Zdo;
    // Replicate the native ack (ZDOMan:767+780) so vanilla re-offers only on revision change.
    // Note: native would only ack items that fit the tick's byte budget; we ack at selection
    // time, which is the countable "redirected at the moment native would have considered it"
    // semantic the gate is defined against.
    bool acked = SuppressNative(peer, zdo);

    byte[] body;
    try {
      ZPackage package = new();
      zdo.Serialize(package);
      body = package.GetArray();
    } catch (Exception exception) {
      body = Array.Empty<byte>();
      lock (_lock) {
        _lastError = "serialize: " + exception.GetType().Name + ": " + exception.Message;
      }
    }

    long seq;
    Dictionary<string, object> row = null;
    TelemetryCoordinator coordinator;
    Vector3 position = candidate.Position;
    lock (_lock) {
      if (!_running) {
        return;
      }
      coordinator = _coordinator;
      seq = ++_seq;
      _suppressed++;
      if (_rowsWritten < _maxRows) {
        _rowsWritten++;
        row = new Dictionary<string, object> {
            ["event"] = "redirect",
            ["correlation_id"] = candidate.CorrelationId,
            ["seq"] = seq,
            ["uid"] = zdo.m_uid.ToString(),
            ["owner"] = zdo.GetOwner().ToString(CultureInfo.InvariantCulture),
            ["owner_rev"] = zdo.OwnerRevision,
            ["data_rev"] = zdo.DataRevision,
            ["prefab"] = SafePrefab(zdo),
            ["pos_x"] = position.x,
            ["pos_y"] = position.y,
            ["pos_z"] = position.z,
            ["body_len"] = body.Length,
            ["acked"] = acked,
            ["window_id"] = _windowId,
            ["build_version"] = ComfyNetworkSense.PluginVersion
        };
      } else {
        _capped = true;
      }
    }

    if (row != null) {
      coordinator?.RecordZdoRedirect(row);
    }

    _postQueue.Enqueue(new Dictionary<string, object> {
        ["correlation_id"] = candidate.CorrelationId,
        ["created_utc"] = candidate.CreatedUtc,
        ["recipient"] = RecipientFor(peer),
        ["importance_class"] = candidate.PriorityTier,
        ["idempotency_key"] = candidate.CorrelationId,
        ["seq"] = seq,
        ["uid_user"] = zdo.m_uid.UserID,
        ["uid_id"] = zdo.m_uid.ID,
        ["owner"] = zdo.GetOwner(),
        ["owner_rev"] = zdo.OwnerRevision,
        ["data_rev"] = zdo.DataRevision,
        ["prefab"] = SafePrefab(zdo),
        ["pos"] = new List<object> { position.x, position.y, position.z },
        ["priority_rank"] = candidate.PriorityRank,
        ["priority_reason"] = candidate.PriorityReason,
        ["distance_meters"] = Math.Round(candidate.DistanceMeters, 3),
        ["body_b64"] = Convert.ToBase64String(body),
        ["attempt"] = 0
    });
  }

  ClassifiedZdo ClassifyCandidate(object peer, ZDO zdo) {
    Vector3 position = zdo.GetPosition();
    PriorityDescriptor priority = ResolvePriorityDescriptor(zdo);
    Vector3 observerPosition = PeerReferencePosition(peer, position);
    float distanceMeters = Vector3.Distance(position, observerPosition);
    float priorityRadius = Mathf.Clamp(
        PluginConfig.LumberjacksPriorityProbeRadiusMeters.Value, 8.0f, 256.0f);
    string priorityTier = LumberjacksPriorityClassifier.Classify(
        priority.ObjectName, priority.ComponentNames, position, observerPosition, priorityRadius,
        out int priorityRank, out string priorityReason);
    float landmarkReach =
        _landmarkReach != null && _landmarkReach.TryGetValue(SafePrefab(zdo), out float reach)
            ? reach
            : 0.0f;
    return new ClassifiedZdo(
        zdo,
        position,
        distanceMeters,
        priorityTier,
        priorityRank,
        priorityReason,
        landmarkReach,
        Guid.NewGuid().ToString("N"),
        DateTime.UtcNow.ToString("o"));
  }

  void RecordImportanceDecision(ClassifiedZdo candidate, string eventType) {
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (_rowsWritten >= _maxRows) {
        _capped = true;
        return;
      }
      _rowsWritten++;
      coordinator = _coordinator;
    }
    coordinator?.RecordZdoRedirect(new Dictionary<string, object> {
        ["event"] = eventType,
        ["correlation_id"] = candidate.CorrelationId,
        ["created_utc"] = candidate.CreatedUtc,
        ["uid"] = candidate.Zdo.m_uid.ToString(),
        ["prefab"] = SafePrefab(candidate.Zdo),
        ["importance_class"] = candidate.PriorityTier,
        ["priority_rank"] = candidate.PriorityRank,
        ["priority_reason"] = candidate.PriorityReason,
        ["distance_meters"] = Math.Round(candidate.DistanceMeters, 3),
        ["landmark_reach_meters"] = Math.Round(candidate.LandmarkReachMeters, 3),
        ["max_priority_rank"] = PluginConfig.ZdoRedirectMaxPriorityRank.Value,
        ["network_eligible"] = eventType == "importance_allowed",
        ["window_id"] = _windowId,
        ["mod_release"] = ComfyNetworkSense.ReleaseId
    });
  }

  // Mid-band emit interval in seconds from the configured Hz; <=0 disables thinning (mid emits every
  // pass). Increment 2 feeds this to ZdoBandPolicy.Classify alongside the per-(peer,uid) last-emit.
  static float ThinIntervalSeconds() {
    float hz = PluginConfig.ZdoThinHz.Value;
    return hz > 0.0f ? 1.0f / hz : 0.0f;
  }

  static bool IsPlayerCharacterZdo(ZDO zdo) {
    if (zdo == null || ZNet.instance == null) {
      return false;
    }

    List<ZNetPeer> peers = ZNet.instance.GetPeers();
    if (peers == null || peers.Count == 0) {
      return false;
    }

    ZDOID uid = zdo.m_uid;
    for (int i = 0; i < peers.Count; i++) {
      if (peers[i] != null && peers[i].m_characterID == uid) {
        return true;
      }
    }

    return false;
  }

  // One row per band decision on an ADMITTED ZDO, so redirect-send.jsonl shows the AoI shaping
  // directly (valheim_tail_zdo_redirect). network_eligible mirrors whether the band actually emitted.
  void RecordBandDecision(ClassifiedZdo candidate, ZdoBandAction band) {
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (_rowsWritten >= _maxRows) {
        _capped = true;
        return;
      }
      _rowsWritten++;
      coordinator = _coordinator;
    }
    coordinator?.RecordZdoRedirect(new Dictionary<string, object> {
        ["event"] = "band_decision",
        ["band"] = band.ToString(),
        ["correlation_id"] = candidate.CorrelationId,
        ["uid"] = candidate.Zdo.m_uid.ToString(),
        ["prefab"] = SafePrefab(candidate.Zdo),
        ["importance_class"] = candidate.PriorityTier,
        ["distance_meters"] = Math.Round(candidate.DistanceMeters, 3),
        ["landmark_reach_meters"] = Math.Round(candidate.LandmarkReachMeters, 3),
        ["inner_radius"] = PluginConfig.ZdoInnerRadiusMeters.Value,
        ["outer_radius"] = PluginConfig.ZdoOuterRadiusMeters.Value,
        ["network_eligible"] = ZdoBandPolicy.Emits(band),
        ["window_id"] = _windowId,
        ["mod_release"] = ComfyNetworkSense.ReleaseId
    });
  }

  // The shared per-observer evaluation for the co-presence shadow AND fan-out (ADR 0013). For each
  // connected ZDOPeer it reads the observer's position and its native delivered-revision bookkeeping,
  // then runs the pure ZdoFanoutPlan to decide, per observer: visible?, already-delivered?, emit?.
  // Decisions are returned PARALLEL to `peers` (same index), so the caller can map an Emit decision
  // back to the exact ZDOPeer it must Redirect. Ownership is never read here — visibility must not be
  // a function of who owns the ZDO (Derek's constraint; owner is logged only as evidence).
  IReadOnlyList<FanoutObserverDecision> EvaluateObservers(
      object exposingPeer, ClassifiedZdo candidate, List<object> peers) {
    Vector3 zdoPos = candidate.Position;
    List<FanoutObserverInput> inputs = new(peers.Count);
    foreach (object zdoPeer in peers) {
      string host = null;
      double dist = double.MaxValue;
      try {
        if (NetworkPeerField?.GetValue(zdoPeer) is ZNetPeer netPeer) {
          host = netPeer.m_socket?.GetHostName();
          dist = Vector3.Distance(zdoPos, netPeer.m_refPos);
        }
      } catch {
        // A peer we cannot read is simply treated as out-of-band (host null, dist max).
      }
      PeerDeliveredRevisions? delivered = DeliveredRevisions(zdoPeer, candidate.Zdo.m_uid);
      inputs.Add(new FanoutObserverInput(
          host,
          dist,
          delivered?.DataRevision,
          delivered?.OwnerRevision,
          ReferenceEquals(zdoPeer, exposingPeer)));
    }
    return ZdoFanoutPlan.Evaluate(
        candidate.Zdo.DataRevision, candidate.Zdo.OwnerRevision,
        PluginConfig.ZdoInnerRadiusMeters.Value,
        PluginConfig.ZdoOuterRadiusMeters.Value, candidate.LandmarkReachMeters, inputs);
  }

  // The SHADOW (zdoCoPresenceShadowEnabled): one 'copresence_shadow' row per observer for a contended
  // candidate, with ZERO delivery change. Records exactly the distinctions the analysis needs — the
  // exposing pass, every observer considered, its distance + AoI band, whether it is visible/selected,
  // whether a redirect would occur, whether its native bookkeeping already had the revision, and the
  // ownership state (evidence only, never used to decide visibility). Skipped unless at least two
  // observers are in-band, i.e. actual co-presence.
  void RecordCoPresenceShadow(
      object exposingPeer, ClassifiedZdo candidate, List<object> peers,
      IReadOnlyList<FanoutObserverDecision> decisions) {
    int visible = 0;
    for (int k = 0; k < decisions.Count; k++)
      if (decisions[k].Disposition != ZdoFanoutDisposition.OutOfBand) visible++;
    if (visible < 2) {
      return;   // no co-presence contention to shadow
    }

    string exposingHost = RecipientFor(exposingPeer);
    string owner = candidate.Zdo.GetOwner().ToString(CultureInfo.InvariantCulture);
    int ownerRev = candidate.Zdo.OwnerRevision;
    long dataRev = candidate.Zdo.DataRevision;
    float inner = PluginConfig.ZdoInnerRadiusMeters.Value;
    float outer = PluginConfig.ZdoOuterRadiusMeters.Value;
    float thin = ThinIntervalSeconds();

    for (int k = 0; k < decisions.Count; k++) {
      FanoutObserverDecision d = decisions[k];
      bool isExposing = ReferenceEquals(peers[k], exposingPeer);
      ZdoBandAction band = ZdoBandPolicy.Classify(
          d.DistanceMeters, inner, outer, candidate.LandmarkReachMeters, Time.time, -1.0f, thin);

      TelemetryCoordinator coordinator;
      lock (_lock) {
        if (_rowsWritten >= _maxRows) {
          _capped = true;
          return;
        }
        _rowsWritten++;
        coordinator = _coordinator;
      }
      coordinator?.RecordZdoRedirect(new Dictionary<string, object> {
          ["event"] = "copresence_shadow",
          ["uid"] = candidate.Zdo.m_uid.ToString(),
          ["prefab"] = SafePrefab(candidate.Zdo),
          ["data_rev"] = dataRev,
          ["exposing_peer"] = exposingHost,
          ["observer"] = d.Recipient ?? string.Empty,
          ["is_exposing_pass"] = isExposing,
          ["distance_meters"] = Math.Round(d.DistanceMeters, 2),
          ["band"] = band.ToString(),
          ["disposition"] = d.Disposition.ToString(),
          ["visible"] = d.Disposition != ZdoFanoutDisposition.OutOfBand,   // selected as an observer
          ["would_redirect"] = d.Emit,                                     // a redirect would occur
          ["already_delivered"] = d.Disposition == ZdoFanoutDisposition.AlreadyDelivered,
          ["selected_by_native"] = d.SelectedByNative,
          ["delivered_data_rev"] =
              d.DeliveredDataRevision.HasValue ? (object) d.DeliveredDataRevision.Value : string.Empty,
          ["delivered_owner_rev"] =
              d.DeliveredOwnerRevision.HasValue ? (object) d.DeliveredOwnerRevision.Value : string.Empty,
          ["owner"] = owner,          // EVIDENCE ONLY — never an input to the visibility decision
          ["owner_rev"] = ownerRev,
          ["window_id"] = _windowId,
          ["mod_release"] = ComfyNetworkSense.ReleaseId
      });
    }
  }

  // The FAN-OUT (zdoCoPresenceFanoutEnabled): from the one pass that saw this candidate, emit a read
  // copy to EVERY in-band observer whose bookkeeping does not already have this revision, reusing the
  // existing Redirect (recipient stamp + m_zdos ack + queue + dedup, all centralized). One logical
  // revision produces at most one redirect per recipient (ZdoFanoutPlan's Emit flag). The exposing
  // peer's ZDO was removed from its toSync by the caller, so it MUST be acked either by its own
  // Redirect (Emit) or, failing that, an explicit SuppressNative — the ADR-0011 remove-implies-ack
  // invariant, now enforced per observer.
  void ApplyFanOut(
      object exposingPeer, ClassifiedZdo candidate, List<object> peers,
      IReadOnlyList<FanoutObserverDecision> decisions) {
    bool exposingHandled = false;
    bool emittedAny = false;
    for (int k = 0; k < decisions.Count; k++) {
      bool isExposing = ReferenceEquals(peers[k], exposingPeer);
      if (decisions[k].Emit) {
        Redirect(peers[k], candidate);   // stamps RecipientFor(peer), acks this peer's m_zdos
        emittedAny = true;
        if (isExposing) exposingHandled = true;
      } else if (isExposing) {
        SuppressNative(exposingPeer, candidate.Zdo);   // removed from toSync but not emitted -> ack
        exposingHandled = true;
      }
      // Non-exposing, non-Emit observers (far, or already-delivered) are left untouched: far peers
      // re-sync on approach; already-delivered peers keep the copy they have.
    }
    if (!emittedAny) {
      // Fan-out must never consume a native-selected candidate, acknowledge it, and deliver it to
      // nobody. Fall closed to the established single-recipient redirect.
      Redirect(exposingPeer, candidate);
      exposingHandled = true;
    } else if (!exposingHandled) {
      SuppressNative(exposingPeer, candidate.Zdo);   // defensive: exposing peer absent from m_peers
    }
  }

  // This peer's native delivered data revision for a ZDO (its m_zdos[uid].m_dataRevision), or null if
  // it has no entry — the "already considered delivered" bookkeeping the fan-out dedups on. Fail-soft:
  // any missing reflection handle or read error yields null (treated as never-delivered).
  readonly struct PeerDeliveredRevisions {
    public readonly long DataRevision;
    public readonly long OwnerRevision;

    public PeerDeliveredRevisions(long dataRevision, long ownerRevision) {
      DataRevision = dataRevision;
      OwnerRevision = ownerRevision;
    }
  }

  PeerDeliveredRevisions? DeliveredRevisions(object zdoPeer, ZDOID uid) {
    if (ZdosField == null || ZdosContainsKey == null || ZdosGetItem == null
        || PeerZdoInfoDataRev == null || PeerZdoInfoOwnerRev == null) {
      return null;
    }
    try {
      object dict = ZdosField.GetValue(zdoPeer);
      if (dict == null || !(bool) ZdosContainsKey.Invoke(dict, new object[] { uid })) {
        return null;
      }
      object info = ZdosGetItem.Invoke(dict, new object[] { uid });
      return new PeerDeliveredRevisions(
          Convert.ToInt64(PeerZdoInfoDataRev.GetValue(info)),
          Convert.ToInt64(PeerZdoInfoOwnerRev.GetValue(info)));
    } catch {
      return null;
    }
  }

  // The connected ZDOPeers (ZDOMan.m_peers) as a per-call snapshot, so the caller hoists it to once
  // per pass. These are the ZDOPeer objects Redirect/SuppressNative/DeliveredDataRevision operate on.
  // Null on any failure — co-presence is opt-in diagnostics/fan-out and must never break delivery.
  static List<object> SafeGetZdoPeers() {
    if (ZdoManPeersField == null || ZDOMan.instance == null) {
      return null;
    }
    try {
      if (ZdoManPeersField.GetValue(ZDOMan.instance) is not System.Collections.IEnumerable list) {
        return null;
      }
      List<object> peers = new();
      foreach (object peer in list) peers.Add(peer);
      return peers;
    } catch {
      return null;
    }
  }

  PriorityDescriptor ResolvePriorityDescriptor(ZDO zdo) {
    int prefabHash = SafePrefab(zdo);
    if (_priorityDescriptors.TryGetValue(prefabHash, out PriorityDescriptor cached)) return cached;

    string objectName = prefabHash.ToString(CultureInfo.InvariantCulture);
    string[] componentNames = Array.Empty<string>();
    try {
      GameObject prefab = ZNetScene.instance?.GetPrefab(prefabHash);
      if (prefab) {
        objectName = prefab.name ?? objectName;
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Component component in prefab.GetComponents<Component>())
          if (component) names.Add(component.GetType().Name);
        componentNames = new string[names.Count];
        names.CopyTo(componentNames);
      }
    } catch {
      // Unknown/modded prefabs retain the bounded support-piece default.
    }

    PriorityDescriptor descriptor = new(objectName, componentNames);
    _priorityDescriptors[prefabHash] = descriptor;
    return descriptor;
  }

  static Vector3 PeerReferencePosition(object redirectPeer, Vector3 fallback) {
    try {
      if (NetworkPeerField?.GetValue(redirectPeer) is ZNetPeer peer) return peer.m_refPos;
    } catch {
      // Delivery remains valid without distance metadata.
    }
    return fallback;
  }

  // --- Poster ----------------------------------------------------------------------------

  void TryFlushQueue() {
    if (_postQueue.IsEmpty) {
      return;
    }
    if (Interlocked.CompareExchange(ref _postInFlight, 1, 0) != 0) {
      return;
    }
    FlushLocked();
  }

  void FlushQueue(bool force) {
    if (_postQueue.IsEmpty) {
      return;
    }
    if (!force && Interlocked.CompareExchange(ref _postInFlight, 1, 0) != 0) {
      return;
    }
    if (force) {
      Interlocked.Exchange(ref _postInFlight, 1);
    }
    FlushLocked();
  }

  void FlushLocked() {
    List<Dictionary<string, object>> batch = new();
    while (batch.Count < PostBatchMax && _postQueue.TryDequeue(out Dictionary<string, object> envelope)) {
      batch.Add(envelope);
    }

    if (batch.Count == 0) {
      Interlocked.Exchange(ref _postInFlight, 0);
      return;
    }

    string endpoint;
    string windowId;
    lock (_lock) {
      endpoint = _endpoint;
      windowId = _windowId;
    }

    _ = Task.Run(() => PostBatch(endpoint, windowId, batch));
  }

  void PostBatch(string endpoint, string windowId, List<Dictionary<string, object>> batch) {
    try {
      List<Dictionary<string, object>> wirePayload = new(batch.Count);
      foreach (Dictionary<string, object> queued in batch) {
        Dictionary<string, object> item = new(queued);
        item.Remove("attempt");
        wirePayload.Add(item);
      }
      Dictionary<string, object> bodyValues = new() {
          ["schema_version"] = ZdoIntegrationContract.SchemaVersion,
          ["source_instance"] = _sourceInstance,
          ["mod_release"] = ComfyNetworkSense.ReleaseId,
          ["operation"] = ZdoIntegrationContract.Operation,
          ["window_id"] = windowId,
          ["payload"] = wirePayload
      };
      string body = JsonLineSerializer.Serialize(bodyValues);

      // Valheim's stripped server Mono runtime does not register the WebRequest "http://" prefix
      // handler, so WebRequest.Create/HttpWebRequest throws NotSupportedException("The URI prefix
      // is not recognized.") on the dedicated server (observed i3-w3: 88 suppressed, 0 posted, all
      // dropped). Post over a raw socket instead - no dependency on the runtime's prefix table.
      SendHttpPostViaSocket(endpoint + "/valheim/zdo-redirect/receipts", body);

      lock (_lock) {
        _postedOk += batch.Count;
      }
    } catch (Exception exception) {
      lock (_lock) {
        _postFailedBatches++;
        _lastError = "post: " + exception.GetType().Name + ": " + exception.Message;
      }
      // Retry-safe by design: the gateway gates on DISTINCT seq, so a batch that actually
      // landed before the failure surfaced only inflates duplicates, never the gate number.
      foreach (Dictionary<string, object> envelope in batch) {
        int attempt = envelope.TryGetValue("attempt", out object value) ? Convert.ToInt32(value) : 0;
        if (attempt + 1 < PostAttemptsMax) {
          envelope["attempt"] = attempt + 1;
          _postQueue.Enqueue(envelope);
          lock (_lock) {
            _requeued++;
          }
        } else {
          lock (_lock) {
            _dropped++;
          }
        }
      }
    } finally {
      Interlocked.Exchange(ref _postInFlight, 0);
      if (!_postQueue.IsEmpty) {
        TryFlushQueue();
      }
    }
  }

  // Raw-socket HTTP POST (see PostBatch): bypasses the WebRequest prefix table, which is empty in
  // Valheim's server Mono runtime. Runs on the poster's background thread. Throws on
  // connect-timeout / write error / non-2xx so the caller's retry + last_error path is unchanged.
  static void SendHttpPostViaSocket(string url, string jsonBody) {
    Uri uri = new(url);
    if (uri.Scheme != "http") {
      throw new NotSupportedException("redirect endpoint must be http (got '" + uri.Scheme + "')");
    }

    byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
    string head =
        "POST " + uri.PathAndQuery + " HTTP/1.1\r\n"
        + "Host: " + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Content-Type: application/json\r\n"
        + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + (string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value) ? string.Empty : "X-Lumberjacks-Client-Key: " + PluginConfig.LumberjacksClientAccessKey.Value + "\r\n")
        + "Connection: close\r\n\r\n";
    byte[] headBytes = Encoding.ASCII.GetBytes(head);

    using TcpClient client = new();
    IAsyncResult connect = client.BeginConnect(uri.Host, uri.Port, null, null);
    if (!connect.AsyncWaitHandle.WaitOne(5000)) {
      throw new TimeoutException("connect timeout to " + uri.Host + ":" + uri.Port);
    }
    client.EndConnect(connect);
    client.SendTimeout = 5000;
    client.ReceiveTimeout = 5000;

    using NetworkStream stream = client.GetStream();
    stream.Write(headBytes, 0, headBytes.Length);
    stream.Write(bodyBytes, 0, bodyBytes.Length);
    stream.Flush();

    byte[] buffer = new byte[512];
    int read = stream.Read(buffer, 0, buffer.Length);
    string statusLine = read > 0
        ? Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim()
        : string.Empty;
    string[] parts = statusLine.Split(' ');
    if (parts.Length < 2 || parts[1].Length == 0 || parts[1][0] != '2') {
      throw new Exception("http status: " + (statusLine.Length == 0 ? "(no response)" : statusLine));
    }
  }

  // --- Helpers -----------------------------------------------------------------------------

  static HashSet<int> BuildPrefabFilter(string csv) {
    if (string.IsNullOrWhiteSpace(csv)) {
      return null;
    }
    HashSet<int> set = new();
    foreach (string part in csv.Split(',')) {
      string name = part.Trim();
      if (name.Length == 0) {
        continue;
      }
      set.Add(name.GetStableHashCode());
    }
    return set.Count > 0 ? set : null;
  }

  // Parse ZdoLandmarkReach — a CSV of `prefabName=reachMeters` entries — into a stable-hash -> reach
  // map, keyed the same way BuildPrefabFilter keys the allowlist so the two agree on prefab identity.
  // Entries with a missing/non-positive/unparseable reach are skipped (a landmark must grant a real
  // distance); returns null when nothing valid is configured.
  static Dictionary<int, float> BuildLandmarkReach(string csv) {
    if (string.IsNullOrWhiteSpace(csv)) {
      return null;
    }
    Dictionary<int, float> map = new();
    foreach (string part in csv.Split(',')) {
      string entry = part.Trim();
      if (entry.Length == 0) {
        continue;
      }
      int eq = entry.IndexOf('=');
      if (eq <= 0 || eq >= entry.Length - 1) {
        continue;
      }
      string name = entry.Substring(0, eq).Trim();
      string reachText = entry.Substring(eq + 1).Trim();
      if (name.Length == 0
          || !float.TryParse(reachText, NumberStyles.Float, CultureInfo.InvariantCulture, out float reach)
          || reach <= 0.0f) {
        continue;
      }
      map[name.GetStableHashCode()] = reach;
    }
    return map.Count > 0 ? map : null;
  }

  static int SafePrefab(ZDO zdo) {
    try {
      return zdo.GetPrefab();
    } catch {
      return 0;
    }
  }

  sealed class PriorityDescriptor {
    public readonly string ObjectName;
    public readonly string[] ComponentNames;
    public PriorityDescriptor(string objectName, string[] componentNames) {
      ObjectName = objectName;
      ComponentNames = componentNames;
    }
  }

  sealed class ClassifiedZdo {
    public readonly ZDO Zdo;
    public readonly Vector3 Position;
    public readonly float DistanceMeters;
    public readonly string PriorityTier;
    public readonly int PriorityRank;
    public readonly string PriorityReason;
    public readonly float LandmarkReachMeters;
    public readonly string CorrelationId;
    public readonly string CreatedUtc;

    public ClassifiedZdo(
        ZDO zdo,
        Vector3 position,
        float distanceMeters,
        string priorityTier,
        int priorityRank,
        string priorityReason,
        float landmarkReachMeters,
        string correlationId,
        string createdUtc) {
      Zdo = zdo;
      Position = position;
      DistanceMeters = distanceMeters;
      PriorityTier = priorityTier;
      PriorityRank = priorityRank;
      PriorityReason = priorityReason;
      LandmarkReachMeters = landmarkReachMeters;
      CorrelationId = correlationId;
      CreatedUtc = createdUtc;
    }
  }

  Dictionary<string, object> BuildStatusRowLocked(string eventType) {
    return new Dictionary<string, object> {
        ["event"] = eventType,
        ["status"] = _status,
        ["running"] = _running,
        ["started_utc"] = _startedUtc == default ? string.Empty : _startedUtc.ToString("o"),
        ["window_id"] = _windowId,
        ["endpoint"] = _endpoint,
        ["seq"] = _seq,
        ["suppressed"] = _suppressed,
        ["importance_allowed"] = _importanceAllowed,
        ["importance_rejected"] = _importanceRejected,
        ["band_dropped"] = _bandDropped,
        ["band_held"] = _bandHeld,
        ["player_fast_lane_candidates"] = _playerFastLaneCandidates,
        ["player_fast_lane_emitted"] = _playerFastLaneEmitted,
        ["player_fast_lane_enabled"] = PluginConfig.ZdoPlayerFastLaneEnabled.Value,
        ["max_priority_rank"] = PluginConfig.ZdoRedirectMaxPriorityRank.Value,
        ["ack_failures"] = _ackFailures,
        ["posted_ok"] = _postedOk,
        ["post_failed_batches"] = _postFailedBatches,
        ["primary_window_ready"] = _primaryWindowReady,
        ["primary_reset_in_flight"] = _primaryResetInFlight != 0,
        ["primary_reset_error"] = _primaryResetError,
        ["requeued"] = _requeued,
        ["dropped"] = _dropped,
        ["queued"] = _postQueue.Count,
        ["rows_written"] = _rowsWritten,
        ["capped"] = _capped,
        ["prefab_filter_count"] = _prefabFilter?.Count ?? 0,
        ["all_prefabs"] = _allPrefabs,
        ["active_seconds"] = PluginConfig.ZdoRedirectActiveSeconds.Value,
        ["reflection_ok"] = ReflectionReady,
        ["last_error"] = _lastError,
        ["claim"] = ScopeClaim(),
        ["build_version"] = ComfyNetworkSense.PluginVersion,
        ["mod_release"] = ComfyNetworkSense.ReleaseId,
        ["schema_version"] = ZdoIntegrationContract.SchemaVersion,
        ["source_instance"] = _sourceInstance
    };
  }

  string StatusLineLocked() {
    return
        $"ZDO redirect: status={_status}, window={_windowId}, seq={_seq}, "
        + $"suppressed={_suppressed}, importanceAllowed={_importanceAllowed}, "
        + $"importanceRejected={_importanceRejected}, postedOk={_postedOk}, queued={_postQueue.Count}, "
        + $"requeued={_requeued}, dropped={_dropped}, ackFailures={_ackFailures}, "
        + $"rows={_rowsWritten}{(_capped ? "(capped)" : string.Empty)}, error={_lastError}";
  }

  static string ScopeClaim() =>
      "ZDO redirect is BEHAVIOUR-CHANGING (server-side). On the explicit allowlist (or '*' all-prefab mode) it removes "
      + "ZDOs from CreateSyncList's toSync before native serialization, replicates the native "
      + "per-peer ack, and posts the wire-equivalent payload to the Lumberjacks gateway. It "
      + "writes no persisted ZDO state (send path is runtime bookkeeping only; save is the "
      + "separate clone path). Non-tagged ZDOs sync normally (negative control). Rollback: "
      + "zdoRedirectEnabled=false; the active-seconds sub-window auto-disarms in-window.";

  public void Dispose() {
    lock (_lock) {
      _running = false;
      _status = "disposed";
    }

    if (_active == this) {
      _active = null;
    }
  }
}

// The suppression postfix. Priority.High so it runs BEFORE the netcode probe's Normal-priority
// postfix on the same method — the probe then observes the POST-filter list (what native will
// actually send), which is exactly the independent absence-measurement the I3 gate wants.
// peer is typed object because ZDOMan.ZDOPeer is a private nested class (Harmony binds by name).
[HarmonyPatch(typeof(ZDOMan))]
static class ZdoRedirectPatches {
  [HarmonyPostfix]
  [HarmonyPriority(Priority.High)]
  [HarmonyPatch("CreateSyncList")]
  static void CreateSyncListPostfix(object peer, List<ZDO> toSync) {
    using (NetworkSensePerfProbe.MeasurePatchLoad("Patch.ZDOMan.CreateSyncList.RedirectPostfix")) {
      ZdoRedirectRunner.HandleCreateSyncList(peer, toSync);
    }
  }
}
