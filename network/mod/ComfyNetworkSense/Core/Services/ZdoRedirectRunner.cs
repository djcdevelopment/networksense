namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
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

  TelemetryCoordinator _coordinator;
  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  DateTime _startedUtc;
  float _stopAtTime = -1.0f;
  int _maxRows = DefaultMaxRows;
  HashSet<int> _prefabFilter;
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

  public bool IsRunning => _running;

  public string Start(TelemetryCoordinator coordinator, int? maxRowsOverride = null) {
    if (!ReflectionReady) {
      return "ZDO redirect REFUSED: ZDOPeer reflection handles unavailable (game update?). "
          + "Suppress-without-ack would break the gate math, so nothing was armed.";
    }
    if (ZNet.instance == null || !ZNet.instance.IsServer()) {
      return "ZDO redirect REFUSED: server-side only (this instance is not the server).";
    }

    HashSet<int> filter = BuildPrefabFilter(PluginConfig.ZdoRedirectPrefabs.Value);
    if (filter == null) {
      return "ZDO redirect REFUSED: zdoRedirectPrefabs is empty. An empty allowlist would "
          + "suppress ALL ZDO sync (world-freeze for the client); name the tagged prefab(s).";
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
    }

    _active = this;
    coordinator?.RecordZdoRedirect(BuildStatusRow("redirect_start"));
    return
        "ZDO redirect ARMED (behaviour-changing, server-side). Suppressing native send for "
        + $"{_prefabFilter.Count} prefab(s) -> {_endpoint} window={_windowId}"
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
    if (filter == null || filter.Count == 0) {
      return;
    }

    for (int i = toSync.Count - 1; i >= 0; i--) {
      ZDO zdo = toSync[i];
      if (zdo == null || !filter.Contains(SafePrefab(zdo))) {
        continue;
      }

      // Suppress: remove from the list BEFORE SendZDOs serializes it. Native never sees it.
      toSync.RemoveAt(i);
      Redirect(peer, zdo);
    }

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
        ["body_b64"] = Convert.ToBase64String(body),
        ["attempt"] = 0
    });
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

      HttpWebRequest request =
          (HttpWebRequest) WebRequest.Create(endpoint + "/valheim/zdo-redirect/receipts");
      request.Method = "POST";
      request.ContentType = "application/json";
      request.Timeout = 5000;
      request.ReadWriteTimeout = 5000;
      using (Stream requestStream = request.GetRequestStream()) {
        using StreamWriter writer = new(requestStream);
        writer.Write(body);
      }
      using WebResponse response = request.GetResponse();
      _ = response;

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
        ["requeued"] = _requeued,
        ["dropped"] = _dropped,
        ["queued"] = _postQueue.Count,
        ["rows_written"] = _rowsWritten,
        ["capped"] = _capped,
        ["prefab_filter_count"] = _prefabFilter?.Count ?? 0,
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
      "ZDO redirect is BEHAVIOUR-CHANGING (server-side). On allowlisted prefabs ONLY it removes "
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
