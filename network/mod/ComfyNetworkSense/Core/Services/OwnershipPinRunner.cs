namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;

using HarmonyLib;

using UnityEngine;

// I2 (P3) — Ownership-seizure PIN. BEHAVIOUR-CHANGING, server-side (am4) only, rollback-gated.
//
// This is the first behaviour-changing rung. Where OwnershipObserveRunner only READ ownership
// churn, this runner SKIPS it: on a small, auto-captured set of ZDOs the pin neutralises the two
// funnels that move ownership on zone entry, so a pinned ZDO's owner does not transfer while the
// player walks. Grounded on the I2 observe capture (mod 0.5.9) and the live decompiled seams
// (assembly_valheim.dll — ZDOMan.ReleaseNearbyZDOS, ZDOMan.RPC_ZDOData, ZDO.SetOwner/SetOwnerInternal).
// See NETCODE-OWNERSHIP-MAP.md and fieldlab/evidence/i2-observe/ANALYSIS.md.
//
// SAFETY MODEL (why this cannot invent or corrupt ownership):
//   * It NEVER writes a synthetic owner or revision. It only SKIPS vanilla owner changes on
//     captured ZDOs by returning false from a Harmony prefix. The persisted owner is always a
//     value vanilla itself set — the pin just stops vanilla from overwriting it. So the save is a
//     strict subset of vanilla writes (fewer transfers), never a novel state.
//   * SCOPED to exactly the two churn seams, never create/load/convert. The ZDO.SetOwner /
//     SetOwnerInternal prefixes enforce the pin ONLY while executing inside the matching vanilla
//     funnel (tracked by ReleaseScopeDepth / RpcScopeDepth around ZDOMan.ReleaseNearbyZDOS and
//     RPC_ZDOData). Outside those funnels (CreateNewZDO@415, load/convert@1305-1554, the dead-ZDO
//     reclaim SetOwner@854) the prefixes are inert, so object creation and world load are untouched.
//   * The two seams cover BOTH caveat-#2 paths. The primary funnel (server release@640 + reseize@645)
//     is a ZDO.SetOwner call, blocked in release scope. The revision-silent remote-apply funnel
//     (RPC_ZDOData -> SetOwnerInternal at :830 AND :844) is blocked in rpc scope — and blocking
//     SetOwnerInternal directly is strictly stronger than the "keep OwnerRevision highest" strategy,
//     which has a hole at RPC_ZDOData:842-844 where a newer DataRevision applies the remote owner
//     WITHOUT the OwnerRevision gate. This runner closes that hole.
//   * TEST-SCOPED: at most ownershipPinAutoCaptureMax ZDOs are ever pinned (default 25), so the pin
//     touches a negligible slice of the 9.15M-ZDO world. Everything not captured transfers normally
//     — that stream is the built-in negative control for the P3 gate.
//   * ROLLBACK: gated by [Netcode] ownershipPinEnabled (default false). Off = the prefixes see a
//     null _active and cost only a volatile read, exactly like the observe seam.
//
// Coupled to the netcode-probe capture window (starts/stops in lockstep with the probe + the
// coupled auto-rehearsal walk), so one launch+join exercises the pin against a real zone-entry walk.
public sealed class OwnershipPinRunner : IDisposable {
  const int DefaultMaxRows = 20000;
  const int DefaultAutoCaptureMax = 25;

  // Set while a pin run is active; the static prefixes read it and no-op when null.
  static volatile OwnershipPinRunner _active;

  // Which vanilla funnel we are currently executing inside. Set by the scope patches around
  // ZDOMan.ReleaseNearbyZDOS / RPC_ZDOData; read by the ZDO.SetOwner / SetOwnerInternal prefixes so
  // the pin fires for ONLY those two funnels. Server RPC handling + ReleaseZDOS both run on the
  // Unity main thread; the depth counters are reentrancy-safe regardless of that assumption.
  internal static int ReleaseScopeDepth;
  internal static int RpcScopeDepth;

  readonly object _lock = new();
  readonly HashSet<ZDOID> _pinned = new();

  TelemetryCoordinator _coordinator;
  bool _running;
  string _status = "idle";
  string _lastError = string.Empty;
  DateTime _startedUtc;
  int _maxRows = DefaultMaxRows;
  int _autoCaptureMax = DefaultAutoCaptureMax;
  HashSet<int> _prefabFilter;   // null = no filter (any prefab eligible for capture)

  long _captured;
  long _holds;
  long _holdsViaSetOwner;
  long _holdsViaInternal;
  long _passThrough;
  long _holdRowsWritten;
  bool _capped;

  public bool IsRunning => _running;

  public string Start(TelemetryCoordinator coordinator, int? maxRowsOverride = null) {
    lock (_lock) {
      if (_running) {
        return $"Ownership pin already running: {StatusLineLocked()}";
      }

      _coordinator = coordinator;
      _running = true;
      _status = "pinning";
      _lastError = string.Empty;
      _startedUtc = DateTime.UtcNow;
      _maxRows = Mathf.Clamp(maxRowsOverride ?? DefaultMaxRows, 0, 200000);
      _autoCaptureMax = Mathf.Clamp(PluginConfig.OwnershipPinAutoCaptureMax.Value, 0, 100000);
      _prefabFilter = BuildPrefabFilter(PluginConfig.OwnershipPinPrefabs.Value);
      _pinned.Clear();
      _captured = 0;
      _holds = 0;
      _holdsViaSetOwner = 0;
      _holdsViaInternal = 0;
      _passThrough = 0;
      _holdRowsWritten = 0;
      _capped = false;
    }

    _active = this;
    coordinator?.RecordOwnershipPin(BuildStatusRow("pin_start"));
    string filterText = _prefabFilter == null
        ? "any prefab"
        : $"{_prefabFilter.Count} prefab(s)";
    return
        "Ownership pin ARMED (behaviour-changing, server-side). Skips ReleaseNearbyZDOS "
        + $"release/reseize + RPC_ZDOData remote-apply on up to {_autoCaptureMax} captured ZDOs "
        + $"({filterText}). Rollback: ownershipPinEnabled=false.";
  }

  public string Stop() {
    IDictionary<string, object> stopRow;
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return "Ownership pin is not running.";
      }

      _running = false;
      _status = "stopped";
      _active = null;
      coordinator = _coordinator;
      stopRow = BuildStatusRowLocked("pin_stop");
    }

    coordinator?.RecordOwnershipPin(stopRow);
    return "Ownership pin disarmed; vanilla ownership transfer restored.";
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

  // --- Static prefix entry points (called from the patch classes) ----------------------

  // True => the caller must BLOCK this SetOwner (skip the vanilla owner change on a pinned ZDO).
  // Gated on the release funnel scope so only ReleaseNearbyZDOS release@640 / reseize@645 are
  // affected, never CreateNewZDO / load / convert SetOwner calls.
  public static bool ShouldBlockSetOwner(ZDO zdo, long uid) {
    OwnershipPinRunner active = _active;
    if (active == null || ReleaseScopeDepth <= 0 || zdo == null) {
      return false;
    }
    return active.Handle(zdo, uid, "SetOwner");
  }

  // True => block this SetOwnerInternal (the revision-silent remote-apply path, caveat #2). Gated
  // on the RPC_ZDOData scope so only inbound peer claims are affected, never the SetOwnerInternal
  // that CreateNewZDO / load / convert paths call to establish an initial owner.
  public static bool ShouldBlockSetOwnerInternal(ZDO zdo, long uid) {
    OwnershipPinRunner active = _active;
    if (active == null || RpcScopeDepth <= 0 || zdo == null) {
      return false;
    }
    return active.Handle(zdo, uid, "SetOwnerInternal");
  }

  // --- Handler ------------------------------------------------------------------------

  internal bool Handle(ZDO zdo, long attemptedUid, string via) {
    long current;
    try {
      current = zdo.GetOwner();
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "get_owner: " + exception.GetType().Name + ": " + exception.Message;
      }
      return false;
    }

    // SetOwner no-ops when uid == current; nothing to hold, and blocking would be a semantic no-op.
    if (attemptedUid == current) {
      return false;
    }

    ZDOID id = zdo.m_uid;
    Dictionary<string, object> row = null;
    TelemetryCoordinator coordinator;
    lock (_lock) {
      if (!_running) {
        return false;
      }

      coordinator = _coordinator;
      bool firstCapture = false;
      if (!_pinned.Contains(id)) {
        // Capture eligibility. We pin objects that currently have a REAL owner (so the frozen
        // owner is a concrete peer/server, giving a crisp "owner stays" gate), optionally
        // restricted to a prefab allowlist, up to the capture cap. Everything else transfers
        // normally — the negative control.
        if (current == 0L) {
          _passThrough++;
          return false;
        }
        if (_prefabFilter != null && !_prefabFilter.Contains(SafePrefab(zdo))) {
          _passThrough++;
          return false;
        }
        if (_pinned.Count >= _autoCaptureMax) {
          _passThrough++;
          return false;
        }
        _pinned.Add(id);
        _captured++;
        firstCapture = true;
      }

      _holds++;
      if (string.Equals(via, "SetOwner", StringComparison.Ordinal)) {
        _holdsViaSetOwner++;
      } else {
        _holdsViaInternal++;
      }

      if (_holdRowsWritten < _maxRows) {
        _holdRowsWritten++;
        row = BuildPinRow(firstCapture ? "pin_capture" : "pin_hold", zdo, current, attemptedUid, via);
      } else {
        _capped = true;
      }
    }

    if (row != null) {
      coordinator?.RecordOwnershipPin(row);
    }
    return true;
  }

  // --- Helpers ------------------------------------------------------------------------

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

  Dictionary<string, object> BuildPinRow(string eventType, ZDO zdo, long heldOwner, long attempted, string via) {
    Vector2i sector = zdo.GetSector();
    bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
    return new Dictionary<string, object> {
        ["event"] = eventType,   // pin_capture | pin_hold
        ["via"] = via,           // SetOwner (release funnel) | SetOwnerInternal (rpc funnel)
        ["uid"] = zdo.m_uid.ToString(),
        // held_owner is the owner we are PRESERVING; attempted_owner is the transfer we blocked.
        ["held_owner"] = heldOwner.ToString(CultureInfo.InvariantCulture),
        ["attempted_owner"] = attempted.ToString(CultureInfo.InvariantCulture),
        ["owner_revision"] = zdo.OwnerRevision,
        ["data_revision"] = zdo.DataRevision,
        ["prefab"] = SafePrefab(zdo),
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
        ["pinned_count"] = _pinned.Count,
        ["captured"] = _captured,
        ["holds"] = _holds,
        ["holds_via_set_owner"] = _holdsViaSetOwner,
        ["holds_via_internal"] = _holdsViaInternal,
        ["pass_through"] = _passThrough,
        ["auto_capture_max"] = _autoCaptureMax,
        ["prefab_filter_count"] = _prefabFilter?.Count ?? 0,
        ["hold_rows_written"] = _holdRowsWritten,
        ["capped"] = _capped,
        ["max_rows"] = _maxRows,
        ["last_error"] = _lastError,
        ["claim"] = ScopeClaim(),
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
  }

  string StatusLineLocked() {
    return
        $"Ownership pin: status={_status}, pinned={_pinned.Count}, "
        + $"holds={_holds} (setOwner={_holdsViaSetOwner}, internal={_holdsViaInternal}), "
        + $"passThrough={_passThrough}, rows={_holdRowsWritten}{(_capped ? "(capped)" : string.Empty)}, "
        + $"error={_lastError}";
  }

  static string ScopeClaim() =>
      "Ownership pin is BEHAVIOUR-CHANGING (server-side). On <=autoCaptureMax captured ZDOs it "
      + "SKIPS the vanilla ReleaseNearbyZDOS release/reseize (SetOwner prefix, release scope) and "
      + "the RPC_ZDOData remote-apply (SetOwnerInternal prefix, rpc scope) so ownership does not "
      + "transfer on zone entry. It writes no synthetic owners/revisions - it only skips vanilla "
      + "owner changes; the persisted owner is a value vanilla set. Uncaptured ZDOs transfer "
      + "normally (negative control). Rollback: ownershipPinEnabled=false.";

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

// Scope patches: bracket the two vanilla ownership funnels so the ZDO prefixes below can tell
// whether a SetOwner/SetOwnerInternal call is part of the churn we mean to pin (vs a create/load
// call we must leave alone). Finalizers guarantee the depth is released even if the vanilla method
// throws. Auto-applied by Harmony.CreateAndPatchAll (mirrors the observe patches); the increments
// are unconditional and negligible (both methods fire at most per release tick / per rpc packet).
[HarmonyPatch(typeof(ZDOMan))]
static class OwnershipPinScopePatches {
  [HarmonyPrefix]
  [HarmonyPatch("ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) })]
  static void ReleaseNearbyPrefix() {
    OwnershipPinRunner.ReleaseScopeDepth++;
  }

  [HarmonyFinalizer]
  [HarmonyPatch("ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) })]
  static void ReleaseNearbyFinalizer() {
    if (OwnershipPinRunner.ReleaseScopeDepth > 0) {
      OwnershipPinRunner.ReleaseScopeDepth--;
    }
  }

  [HarmonyPrefix]
  [HarmonyPatch("RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static void RpcZdoDataPrefix() {
    OwnershipPinRunner.RpcScopeDepth++;
  }

  [HarmonyFinalizer]
  [HarmonyPatch("RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static void RpcZdoDataFinalizer() {
    if (OwnershipPinRunner.RpcScopeDepth > 0) {
      OwnershipPinRunner.RpcScopeDepth--;
    }
  }
}

// The enforcement prefixes. Returning false skips the vanilla owner change (the pin holds the
// current owner). Priority.Last guarantees these run AFTER the observe prefix on the same methods,
// so the observe patch's __state (pre-change owner) is always captured before a blocked change is
// skipped — a blocked change then reads as a no-op to the observer (correct: nothing changed) and
// is counted by the pin instead. The types array disambiguates the (long) overload.
[HarmonyPatch(typeof(ZDO))]
static class OwnershipPinPatches {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.Last)]
  [HarmonyPatch("SetOwner", new[] { typeof(long) })]
  static bool SetOwnerPrefix(ZDO __instance, long uid) {
    return !OwnershipPinRunner.ShouldBlockSetOwner(__instance, uid);
  }

  [HarmonyPrefix]
  [HarmonyPriority(Priority.Last)]
  [HarmonyPatch("SetOwnerInternal", new[] { typeof(long) })]
  static bool SetOwnerInternalPrefix(ZDO __instance, long uid) {
    return !OwnershipPinRunner.ShouldBlockSetOwnerInternal(__instance, uid);
  }
}
