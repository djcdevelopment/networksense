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
  string _windowId = string.Empty;
  string _endpoint = string.Empty;

  long _seq;
  long _suppressed;
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
      _endpoint = PluginConfig.ZdoRedirectEndpoint.Value.TrimEnd('/');
      string configuredWindow = PluginConfig.ZdoRedirectWindowId.Value;
      _windowId = string.IsNullOrWhiteSpace(configuredWindow)
          ? "i3-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
          : configuredWindow.Trim();
      float activeSeconds = Math.Max(0.0f, PluginConfig.ZdoRedirectActiveSeconds.Value);
      _stopAtTime = activeSeconds > 0.0f ? Time.time + activeSeconds : -1.0f;
      _seq = 0;
      _suppressed = 0;
      _ackFailures = 0;
      _postedOk = 0;
      _postFailedBatches = 0;
      _requeued = 0;
      _dropped = 0;
      _rowsWritten = 0;
      _capped = false;
      _priorityDescriptors.Clear();
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

    // CreateSyncList has already passed through Valheim's ServerSortSendZDOS priority
    // ordering. Remove from the source list backwards for index safety, but redirect in
    // the original forward order; the old loop emitted the lowest-priority tail first.
    List<ZDO> selected = new();
    List<int> selectedIndexes = new();
    for (int i = 0; i < toSync.Count; i++) {
      ZDO zdo = toSync[i];
      if (zdo == null || (!allPrefabs && !filter.Contains(SafePrefab(zdo)))) {
        continue;
      }
      selected.Add(zdo);
      selectedIndexes.Add(i);
    }

    for (int i = selectedIndexes.Count - 1; i >= 0; i--)
      toSync.RemoveAt(selectedIndexes[i]);
    foreach (ZDO zdo in selected) Redirect(peer, zdo);

    TryFlushQueue();
  }

  void Redirect(object peer, ZDO zdo) {
    // Replicate the native ack (ZDOMan:767+780) so vanilla re-offers only on revision change.
    // Note: native would only ack items that fit the tick's byte budget; we ack at selection
    // time, which is the countable "redirected at the moment native would have considered it"
    // semantic the gate is defined against.
    bool acked = true;
    try {
      object infoBox = PeerInfoCtor.Invoke(
          new object[] { zdo.DataRevision, zdo.OwnerRevision, Time.time });
      object zdosDictionary = ZdosField.GetValue(peer);
      ZdosSetItem.Invoke(zdosDictionary, new[] { (object) zdo.m_uid, infoBox });
      HashSet<ZDOID> forceSend = (HashSet<ZDOID>) ForceSendField.GetValue(peer);
      forceSend.Remove(zdo.m_uid);
    } catch (Exception exception) {
      acked = false;
      lock (_lock) {
        _ackFailures++;
        _lastError = "ack: " + exception.GetType().Name + ": " + exception.Message;
      }
    }

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
    Vector3 position = zdo.GetPosition();
    PriorityDescriptor priority = ResolvePriorityDescriptor(zdo);
    Vector3 observerPosition = PeerReferencePosition(peer, position);
    float distanceMeters = Vector3.Distance(position, observerPosition);
    float priorityRadius = Mathf.Clamp(PluginConfig.LumberjacksPriorityProbeRadiusMeters.Value, 8.0f, 256.0f);
    string priorityTier = LumberjacksPriorityClassifier.Classify(
        priority.ObjectName, priority.ComponentNames, position, observerPosition, priorityRadius,
        out int priorityRank, out string priorityReason);
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
        ["seq"] = seq,
        ["uid_user"] = zdo.m_uid.UserID,
        ["uid_id"] = zdo.m_uid.ID,
        ["owner"] = zdo.GetOwner(),
        ["owner_rev"] = zdo.OwnerRevision,
        ["data_rev"] = zdo.DataRevision,
        ["prefab"] = SafePrefab(zdo),
        ["pos"] = new List<object> { position.x, position.y, position.z },
        ["priority_tier"] = priorityTier,
        ["priority_rank"] = priorityRank,
        ["priority_reason"] = priorityReason,
        ["distance_meters"] = Math.Round(distanceMeters, 3),
        ["body_b64"] = Convert.ToBase64String(body),
        ["attempt"] = 0
    });
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
      Dictionary<string, object> bodyValues = new() {
          ["source"] = "am4-server",
          ["window_id"] = windowId,
          ["envelopes"] = batch
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
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  string StatusLineLocked() {
    return
        $"ZDO redirect: status={_status}, window={_windowId}, seq={_seq}, "
        + $"suppressed={_suppressed}, postedOk={_postedOk}, queued={_postQueue.Count}, "
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
    ZdoRedirectRunner.HandleCreateSyncList(peer, toSync);
  }
}
