namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;

using HarmonyLib;

using UnityEngine;

// I2 (P3) — Ownership-seizure grounding, OBSERVE ONLY.
//
// Records every ZDO ownership change so the P3 pin gates ("owner stays pinned across
// trigger" / "unpinned transfers normally") are measured against real churn before any
// behaviour-changing pin code is written. It changes NOTHING: both hooks are postfixes that
// only read the ZDO after Valheim already applied the change. No owners, revisions, or
// transforms are written. See NETCODE-OWNERSHIP-MAP.md.
//
// Two seams are patched independently, tagged by `via`, so the two ownership funnels are
// distinguishable in the log (this also empirically confirms the ReleaseNearbyZDOS churn
// funnel actually reaches ZDO.SetOwner through a Harmony patch before the pin relies on it):
//   * ZDO.SetOwner(long)         — the authoritative setter. Fires on local server-driven
//     changes (ReleaseNearbyZDOS release/seize); calls SetOwnerInternal then bumps
//     OwnerRevision, so a via=SetOwner row carries the POST-bump revision.
//   * ZDO.SetOwnerInternal(long) — applies a remote owner WITHOUT bumping the revision. A
//     lone via=SetOwnerInternal row (no paired SetOwner) is the RPC_ZDOData remote-apply
//     path from NETCODE-OWNERSHIP-MAP.md caveat #2 (the revision-silent path a SetOwner
//     guard would NOT cover). Confirms whether the pin's revision race holds.
public sealed class OwnershipObserveRunner : IDisposable {
  const int DefaultMaxRows = 20000;

  // Set while an observe run is active; the static hooks read it and no-op when null, so the
  // installed patches cost only a volatile read (+ the prefix's cheap GetOwner) when stopped.
  static volatile OwnershipObserveRunner _active;

  readonly object _lock = new();

  TelemetryCoordinator _coordinator;
  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  DateTime _startedUtc;
  int _maxRows = DefaultMaxRows;

  long _setOwnerRows;
  long _setOwnerInternalRows;
  long _noOpSkipped;
  long _rowsWritten;
  bool _capped;

  public bool IsRunning => _running;

  public string Start(TelemetryCoordinator coordinator, int? maxRowsOverride = null) {
    lock (_lock) {
      if (_running) {
        return $"Ownership observe already running: {StatusLineLocked()}";
      }

      _coordinator = coordinator;
      _running = true;
      _status = "observing";
      _lastError = string.Empty;
      _startedUtc = DateTime.UtcNow;
      _maxRows = Mathf.Clamp(maxRowsOverride ?? DefaultMaxRows, 0, 200000);
      _setOwnerRows = 0;
      _setOwnerInternalRows = 0;
      _noOpSkipped = 0;
      _rowsWritten = 0;
      _capped = false;
    }

    _active = this;
    coordinator?.RecordOwnershipChurn(BuildStatusRow("start"));
    return
        "Ownership observe watching ZDO.SetOwner / SetOwnerInternal (postfix). "
        + $"Row cap={_maxRows}. Observe-only; no owners/revisions/transforms are modified.";
  }

  public string Stop() {
    IDictionary<string, object> stopRow;
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return "Ownership observe is not running.";
      }

      _running = false;
      _status = "stopped";
      _active = null;
      coordinator = _coordinator;
      stopRow = BuildStatusRowLocked("stop");
    }

    coordinator?.RecordOwnershipChurn(stopRow);
    return "Ownership observe stopped; no owners, revisions, or transforms were modified.";
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

  // --- Static patch entry points (called from OwnershipObservePatches) -----------------

  // Cheap pre-state capture: returns 0 when no run is active so the always-installed prefix
  // costs only a volatile read while stopped.
  public static long CaptureOldOwner(ZDO zdo) {
    if (_active == null || zdo == null) {
      return 0L;
    }
    return zdo.GetOwner();
  }

  public static void ObserveSetOwner(ZDO zdo, long oldOwner) {
    _active?.Handle(zdo, oldOwner, "SetOwner");
  }

  public static void ObserveSetOwnerInternal(ZDO zdo, long oldOwner) {
    _active?.Handle(zdo, oldOwner, "SetOwnerInternal");
  }

  // --- Handler ------------------------------------------------------------------------

  void Handle(ZDO zdo, long oldOwner, string via) {
    if (zdo == null) {
      return;
    }

    long newOwner;
    try {
      newOwner = zdo.GetOwner();
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "get_owner: " + exception.GetType().Name + ": " + exception.Message;
      }
      return;
    }

    // Only real transitions are interesting; SetOwner no-ops when uid == current owner.
    if (newOwner == oldOwner) {
      lock (_lock) {
        if (_running) {
          _noOpSkipped++;
        }
      }
      return;
    }

    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return;
      }

      if (_rowsWritten >= _maxRows) {
        _capped = true;
        return;
      }

      _rowsWritten++;
      if (string.Equals(via, "SetOwner", StringComparison.Ordinal)) {
        _setOwnerRows++;
      } else {
        _setOwnerInternalRows++;
      }
      coordinator = _coordinator;
    }

    coordinator?.RecordOwnershipChurn(BuildOwnershipRow(zdo, oldOwner, newOwner, via));
  }

  // --- Helpers ------------------------------------------------------------------------

  static Dictionary<string, object> BuildOwnershipRow(ZDO zdo, long oldOwner, long newOwner, string via) {
    Vector2i sector = zdo.GetSector();
    bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
    return new Dictionary<string, object> {
        ["event"] = "ownership",
        ["via"] = via,
        ["uid"] = zdo.m_uid.ToString(),
        ["old_owner"] = oldOwner.ToString(CultureInfo.InvariantCulture),
        ["new_owner"] = newOwner.ToString(CultureInfo.InvariantCulture),
        ["owner_revision"] = zdo.OwnerRevision,
        ["data_revision"] = zdo.DataRevision,
        // is_owner_now == "the new owner is THIS session" (server seized) vs a peer uid.
        ["is_owner_now"] = zdo.IsOwner(),
        ["sector_x"] = sector.x,
        ["sector_y"] = sector.y,
        ["is_server"] = isServer,
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  Dictionary<string, object> BuildStatusRowLocked(string eventType) {
    return new Dictionary<string, object> {
        ["event"] = eventType,
        ["status"] = _status,
        ["running"] = _running,
        ["started_utc"] = _startedUtc == default ? string.Empty : _startedUtc.ToString("o"),
        ["set_owner_rows"] = _setOwnerRows,
        ["set_owner_internal_rows"] = _setOwnerInternalRows,
        ["no_op_skipped"] = _noOpSkipped,
        ["rows_written"] = _rowsWritten,
        ["capped"] = _capped,
        ["max_rows"] = _maxRows,
        ["last_error"] = _lastError,
        ["claim"] = ScopeClaim(),
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  string StatusLineLocked() {
    return
        $"Ownership observe: status={_status}, "
        + $"setOwner={_setOwnerRows}, setOwnerInternal={_setOwnerInternalRows}, "
        + $"noOpSkipped={_noOpSkipped}, rows={_rowsWritten}{(_capped ? "(capped)" : string.Empty)}, "
        + $"error={_lastError}";
  }

  static string ScopeClaim() =>
      "Ownership observe reads ZDO ownership only in postfix (after Valheim applied it). It "
      + "writes no ZDOs, does not change ownership/revisions, and does not alter vanilla "
      + "replication. Pure measurement for the P3/I2 ownership-seizure work.";

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
// NetcodeProbePatches). Every hook forwards to the runner, which no-ops unless an observe
// run is active — so these stay installed for the whole session at negligible idle cost.
// The prefix stashes the pre-change owner into __state (postfix time GetOwner() is already
// the new owner); the types array disambiguates the (long) overload.
[HarmonyPatch(typeof(ZDO))]
static class OwnershipObservePatches {
  [HarmonyPrefix]
  [HarmonyPatch("SetOwner", new[] { typeof(long) })]
  static void SetOwnerPrefix(ZDO __instance, out long __state) {
    __state = OwnershipObserveRunner.CaptureOldOwner(__instance);
  }

  [HarmonyPostfix]
  [HarmonyPatch("SetOwner", new[] { typeof(long) })]
  static void SetOwnerPostfix(ZDO __instance, long __state) {
    OwnershipObserveRunner.ObserveSetOwner(__instance, __state);
  }

  [HarmonyPrefix]
  [HarmonyPatch("SetOwnerInternal", new[] { typeof(long) })]
  static void SetOwnerInternalPrefix(ZDO __instance, out long __state) {
    __state = OwnershipObserveRunner.CaptureOldOwner(__instance);
  }

  [HarmonyPostfix]
  [HarmonyPatch("SetOwnerInternal", new[] { typeof(long) })]
  static void SetOwnerInternalPostfix(ZDO __instance, long __state) {
    OwnershipObserveRunner.ObserveSetOwnerInternal(__instance, __state);
  }
}
