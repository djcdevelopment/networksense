namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;

using HarmonyLib;

using UnityEngine;

// I1 — Interception reachability (VALHEIM-NETCODE-REPLACEMENT-WORKLOG.md).
//
// Observe-only probe that proves ComfyNetworkSense Harmony patches actually fire on the
// live ZDO send and receive funnels mapped in NETCODE-MAP.md, and can read the ZDO
// payload passing through. It changes nothing: the receive side re-parses a *copy* of the
// wire bytes (never touches the live ZPackage read cursor), and the send side only reads
// the ZDO objects Valheim already selected. No ZDOs, owners, revisions, or transforms are
// written.
//
// Three seams are patched independently so I1 resolves the standing inlining risk with
// evidence either way:
//   * Postfix ZDOMan.RPC_ZDOData  — receive funnel (delegate-registered → inlining-proof).
//   * Postfix ZDOMan.SendZDOs     — the residual-risk send helper. If this counter stays
//     zero while CreateSyncList fires, SendZDOs was inlined (a discovery, not a kill).
//   * Postfix ZDOMan.CreateSyncList — the send-side fallback seam + the source of per-ZDO
//     send detail (uid/owner/rev straight off the ZDO objects about to be sent).
public sealed class NetcodeProbeRunner : IDisposable {
  const int DefaultMaxDetailRows = 5000;

  // Set while a probe run is active; the static patch hooks read it and no-op when null,
  // so the installed patches cost only a volatile read when the probe is stopped.
  static volatile NetcodeProbeRunner _active;

  readonly object _lock = new();

  TelemetryCoordinator _coordinator;
  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  DateTime _startedUtc;
  int _maxDetailRows = DefaultMaxDetailRows;

  // Receive funnel (RPC_ZDOData).
  long _recvFunnelCalls;
  long _recvZdoRows;
  long _recvParseErrors;

  // Send funnel (SendZDOs + CreateSyncList).
  long _sendZdosCalls;
  long _sendZdosFlushed;
  long _createSyncListCalls;
  long _sendZdoRows;

  long _detailRowsWritten;
  bool _detailCapped;

  public bool IsRunning => _running;

  public string Start(TelemetryCoordinator coordinator, int? maxDetailRowsOverride = null) {
    lock (_lock) {
      if (_running) {
        return $"Netcode probe already running: {StatusLineLocked()}";
      }

      _coordinator = coordinator;
      _running = true;
      _status = "observing";
      _lastError = string.Empty;
      _startedUtc = DateTime.UtcNow;
      _maxDetailRows = Mathf.Clamp(maxDetailRowsOverride ?? DefaultMaxDetailRows, 0, 200000);
      _recvFunnelCalls = 0;
      _recvZdoRows = 0;
      _recvParseErrors = 0;
      _sendZdosCalls = 0;
      _sendZdosFlushed = 0;
      _createSyncListCalls = 0;
      _sendZdoRows = 0;
      _detailRowsWritten = 0;
      _detailCapped = false;
    }

    _active = this;
    coordinator?.RecordNetcodeProbe(BuildStatusRow("start"));
    return
        "Netcode probe observing ZDO send + receive funnels (RPC_ZDOData / SendZDOs / "
        + $"CreateSyncList). Detail-row cap={_maxDetailRows}. Observe-only; nothing is modified.";
  }

  public string Stop() {
    IDictionary<string, object> stopRow;
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return "Netcode probe is not running.";
      }

      _running = false;
      _status = "stopped";
      _active = null;
      coordinator = _coordinator;
      stopRow = BuildStatusRowLocked("stop");
    }

    coordinator?.RecordNetcodeProbe(stopRow);
    return "Netcode probe stopped; no ZDOs, owners, revisions, or transforms were modified.";
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

  // --- Static patch entry points (called from NetcodeProbePatches) --------------------

  public static void ObserveReceive(ZPackage pkg) {
    _active?.HandleReceive(pkg);
  }

  public static void ObserveSendZDOs(bool result) {
    _active?.HandleSendZDOs(result);
  }

  public static void ObserveCreateSyncList(List<ZDO> toSync) {
    _active?.HandleCreateSyncList(toSync);
  }

  // --- Handlers -----------------------------------------------------------------------

  void HandleReceive(ZPackage pkg) {
    if (pkg == null) {
      return;
    }

    // Re-parse a copy of the wire bytes so the live packet's read cursor is never touched;
    // by postfix time the original RPC_ZDOData has already applied these ZDOs.
    List<Dictionary<string, object>> rows = null;
    long parsedCount = 0;
    try {
      ZPackage replay = new(pkg.GetArray());
      int invalidSectorCount = replay.ReadInt();
      for (int i = 0; i < invalidSectorCount; i++) {
        replay.ReadZDOID();
      }

      ZPackage body = new();
      while (true) {
        ZDOID uid = replay.ReadZDOID();
        if (uid.IsNone()) {
          break;
        }

        ushort ownerRevision = replay.ReadUShort();
        uint dataRevision = replay.ReadUInt();
        long owner = replay.ReadLong();
        Vector3 position = replay.ReadVector3();
        replay.ReadPackage(ref body);

        parsedCount++;
        Dictionary<string, object> row =
            BuildZdoRow("recv", uid, owner, ownerRevision, dataRevision, position);
        (rows ??= []).Add(row);
      }
    } catch (Exception exception) {
      lock (_lock) {
        _recvParseErrors++;
        _lastError = "recv_parse: " + exception.GetType().Name + ": " + exception.Message;
      }
    }

    TelemetryCoordinator coordinator;
    List<Dictionary<string, object>> emit = null;
    lock (_lock) {
      if (!_running) {
        return;
      }

      _recvFunnelCalls++;
      _recvZdoRows += parsedCount;
      coordinator = _coordinator;
      emit = TakeDetailRowsLocked(rows);
    }

    EmitRows(coordinator, emit);
  }

  void HandleSendZDOs(bool result) {
    lock (_lock) {
      if (!_running) {
        return;
      }

      _sendZdosCalls++;
      if (result) {
        _sendZdosFlushed++;
      }
    }
  }

  void HandleCreateSyncList(List<ZDO> toSync) {
    if (toSync == null) {
      return;
    }

    List<Dictionary<string, object>> rows = null;
    long parsedCount = 0;
    try {
      foreach (ZDO zdo in toSync) {
        if (zdo == null) {
          continue;
        }

        parsedCount++;
        Dictionary<string, object> row = BuildZdoRow(
            "send",
            zdo.m_uid,
            zdo.GetOwner(),
            zdo.OwnerRevision,
            zdo.DataRevision,
            zdo.GetPosition());
        (rows ??= []).Add(row);
      }
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "send_read: " + exception.GetType().Name + ": " + exception.Message;
      }
    }

    TelemetryCoordinator coordinator;
    List<Dictionary<string, object>> emit;
    lock (_lock) {
      if (!_running) {
        return;
      }

      _createSyncListCalls++;
      _sendZdoRows += parsedCount;
      coordinator = _coordinator;
      emit = TakeDetailRowsLocked(rows);
    }

    EmitRows(coordinator, emit);
  }

  // --- Helpers ------------------------------------------------------------------------

  // Trims the batch to the remaining detail-row budget under the lock, so the jsonl stays
  // bounded during dense play while the aggregate counters keep counting past the cap.
  List<Dictionary<string, object>> TakeDetailRowsLocked(List<Dictionary<string, object>> rows) {
    if (rows == null || rows.Count == 0) {
      return null;
    }

    long budget = _maxDetailRows - _detailRowsWritten;
    if (budget <= 0) {
      _detailCapped = true;
      return null;
    }

    if (rows.Count > budget) {
      _detailCapped = true;
      rows.RemoveRange((int) budget, rows.Count - (int) budget);
    }

    _detailRowsWritten += rows.Count;
    return rows;
  }

  static void EmitRows(TelemetryCoordinator coordinator, List<Dictionary<string, object>> rows) {
    if (coordinator == null || rows == null) {
      return;
    }

    foreach (Dictionary<string, object> row in rows) {
      coordinator.RecordNetcodeProbe(row);
    }
  }

  static Dictionary<string, object> BuildZdoRow(
      string direction, ZDOID uid, long owner, ushort ownerRevision, uint dataRevision, Vector3 position) {
    return new Dictionary<string, object> {
        ["event"] = "zdo",
        ["dir"] = direction,
        ["uid"] = uid.ToString(),
        ["owner"] = owner.ToString(CultureInfo.InvariantCulture),
        ["owner_revision"] = ownerRevision,
        ["data_revision"] = dataRevision,
        ["pos_x"] = Math.Round(position.x, 2),
        ["pos_y"] = Math.Round(position.y, 2),
        ["pos_z"] = Math.Round(position.z, 2),
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  Dictionary<string, object> BuildStatusRowLocked(string eventType) {
    return new Dictionary<string, object> {
        ["event"] = eventType,
        ["status"] = _status,
        ["running"] = _running,
        ["started_utc"] = _startedUtc == default ? string.Empty : _startedUtc.ToString("o"),
        ["recv_funnel_calls"] = _recvFunnelCalls,
        ["recv_zdo_rows"] = _recvZdoRows,
        ["recv_parse_errors"] = _recvParseErrors,
        ["send_zdos_calls"] = _sendZdosCalls,
        ["send_zdos_flushed"] = _sendZdosFlushed,
        ["create_sync_list_calls"] = _createSyncListCalls,
        ["send_zdo_rows"] = _sendZdoRows,
        ["detail_rows_written"] = _detailRowsWritten,
        ["detail_capped"] = _detailCapped,
        ["max_detail_rows"] = _maxDetailRows,
        ["last_error"] = _lastError,
        ["claim"] = ScopeClaim(),
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  string StatusLineLocked() {
    return
        $"Netcode probe: status={_status}, "
        + $"recv[calls={_recvFunnelCalls}, zdos={_recvZdoRows}, parseErr={_recvParseErrors}], "
        + $"send[sendZDOs={_sendZdosCalls}/flushed={_sendZdosFlushed}, "
        + $"createSyncList={_createSyncListCalls}, zdos={_sendZdoRows}], "
        + $"detailRows={_detailRowsWritten}{(_detailCapped ? "(capped)" : string.Empty)}, "
        + $"error={_lastError}";
  }

  static string ScopeClaim() =>
      "Netcode probe observes the live ZDO send/receive funnels only: it re-parses a copy of "
      + "received wire bytes and reads outbound ZDO objects Valheim already selected. It writes "
      + "no ZDOs, does not change ownership/revisions, and does not alter vanilla replication.";

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

// Auto-applied by Harmony.CreateAndPatchAll in ComfyNetworkSense.Awake (mirrors
// SpawnerConnectionCachePatches). Every hook forwards to the runner, which no-ops unless a
// probe run is active — so these stay installed for the whole session at negligible idle cost.
[HarmonyPatch(typeof(ZDOMan))]
static class NetcodeProbePatches {
  [HarmonyPostfix]
  [HarmonyPatch("RPC_ZDOData")]
  static void RpcZdoDataPostfix(ZPackage pkg) {
    NetcodeProbeRunner.ObserveReceive(pkg);
  }

  [HarmonyPostfix]
  [HarmonyPatch("SendZDOs")]
  static void SendZDOsPostfix(bool __result) {
    NetcodeProbeRunner.ObserveSendZDOs(__result);
  }

  [HarmonyPostfix]
  [HarmonyPatch("CreateSyncList")]
  static void CreateSyncListPostfix(List<ZDO> toSync) {
    NetcodeProbeRunner.ObserveCreateSyncList(toSync);
  }
}
