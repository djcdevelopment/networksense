namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

using BepInEx;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// Fixed-file, allow-listed action driver for the physical-client cutover lane. It is intentionally
/// not a console or input bridge: every action is bounded, correlated to the expiring native
/// autotest request, and emits a durable receipt before the harness advances.
/// </summary>
public sealed class NativeCutoverScenarioController : IDisposable {
  const int CurrentSchemaVersion = 1;
  const int MaxManifestBytes = 65536;
  const int MaxActions = 64;
  const float PollSeconds = 0.25f;
  const string ManifestFileName = "native-cutover-scenario.json";
  const string ReceiptFileName = "native-cutover-scenario-receipts.jsonl";

  readonly string _directory = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
  readonly string _manifestPath;
  readonly string _receiptPath;
  readonly HashSet<string> _completedActionIds = new(StringComparer.Ordinal);
  readonly LumberjacksGameSessionRunner _gameSession;
  readonly RoutedRpcCutoverRunner _routedRpc;
  readonly ShipCutoverRunner _ship;
  readonly SaddleCutoverRunner _saddle;
  readonly ZdoJournalCutoverRunner _zdoJournal;
  readonly OwnershipLeaseCutoverRunner _ownershipLease;
  readonly WorldZoneCutoverRunner _worldZone;
  readonly LumberjacksMotionRunner _motion;
  readonly SocketQuarantineCutoverRunner _socketQuarantine;

  NativeCutoverScenarioManifest _manifest;
  NativeCutoverScenarioAction[] _actions = Array.Empty<NativeCutoverScenarioAction>();
  NativeCutoverScenarioAction _active;
  float _nextPollAt;
  float _actionStartedAt;
  float _actionDeadlineAt;
  float _nextActionDiagnosticAt;
  Vector3 _origin;
  Vector2i _originZone;
  int _inventoryBefore;
  object _ownershipTarget;
  ZDOID _portalSourceId;
  ZDOID _portalTargetId;
  Vector3 _portalSourcePosition;
  Vector3 _portalTargetPosition;
  int _portalRoundtripStage;
  float _portalNextTeleportAt;
  bool _sessionProbeStarted;
  bool _terminal;

  public NativeCutoverScenarioController(
      LumberjacksGameSessionRunner gameSession,
      RoutedRpcCutoverRunner routedRpc,
      ShipCutoverRunner ship,
      SaddleCutoverRunner saddle,
      ZdoJournalCutoverRunner zdoJournal,
      OwnershipLeaseCutoverRunner ownershipLease,
      WorldZoneCutoverRunner worldZone,
      LumberjacksMotionRunner motion,
      SocketQuarantineCutoverRunner socketQuarantine) {
    _gameSession = gameSession;
    _routedRpc = routedRpc;
    _ship = ship;
    _saddle = saddle;
    _zdoJournal = zdoJournal;
    _ownershipLease = ownershipLease;
    _worldZone = worldZone;
    _motion = motion;
    _socketQuarantine = socketQuarantine;
    Directory.CreateDirectory(_directory);
    _manifestPath = Path.Combine(_directory, ManifestFileName);
    _receiptPath = Path.Combine(_directory, ReceiptFileName);
  }

  public void Update(float now) {
    if (_terminal) return;
    if (_manifest == null) {
      if (now < _nextPollAt) return;
      _nextPollAt = now + PollSeconds;
      TryLoad();
      return;
    }

    // God mode lives on the current Player instance, so a disconnect/reconnect can discard it
    // while the Lumberjacks session probe is deliberately waiting for the peer to return. Keep
    // the survival invariant armed even while JoinedClientReady is false; otherwise a replacement
    // Player can fall from a test teleport before the next teleport action gets a chance to re-arm.
    Player localPlayer = Player.m_localPlayer;
    if (localPlayer != null) {
      if (localPlayer.IsDead()) {
        if (_active != null) FailActive("scenario_local_player_dead");
        return;
      }
      if (!localPlayer.InGodMode()
          && !EnsureTeleportSurvivalSafeguard(out string safeguardDetail)) {
        if (_active != null) FailActive(safeguardDetail);
        return;
      }
    }
    if (!JoinedClientReady()) return;

    if (_active == null) {
      NativeCutoverScenarioAction next =
          _actions.FirstOrDefault(action => !_completedActionIds.Contains(action.id));
      if (next == null) {
        WriteReceipt("scenario_complete", string.Empty, "all_actions_completed");
        TryDeleteManifest();
        _terminal = true;
        return;
      }
      Begin(next, now);
      if (_active == null || _terminal) return;
    }

    if (now > _actionDeadlineAt) {
      string detail = "deadline_exceeded";
      if (string.Equals(
              _active.kind, "teleport_to",
              StringComparison.OrdinalIgnoreCase))
        detail += " " + DescribeTeleportReadiness();
      else if (string.Equals(
                   _active.kind, "portal_roundtrip",
                   StringComparison.OrdinalIgnoreCase))
        detail += " stage=" + _portalRoundtripStage
            + " " + DescribeTeleportReadiness(
                _portalRoundtripStage >= 2
                    ? _portalSourcePosition
                    : _portalTargetPosition);
      FailActive(detail);
      return;
    }
    TickActive(now);
  }

  void TryLoad() {
    if (!File.Exists(_manifestPath)
        || string.IsNullOrEmpty(NativeAutotestRequest.ActiveRunId)
        || string.IsNullOrEmpty(NativeAutotestRequest.ActiveClient)) {
      return;
    }

    try {
      FileInfo info = new(_manifestPath);
      if (info.Length is <= 0 or > MaxManifestBytes) {
        RejectManifest("manifest_size_invalid");
        return;
      }
      NativeCutoverScenarioManifest parsed = DeserializeManifest(File.ReadAllText(_manifestPath));
      string validation = Validate(parsed);
      if (!string.IsNullOrEmpty(validation)) {
        RejectManifest(validation);
        return;
      }

      _manifest = parsed;
      _actions = parsed.actions
          .Where(action => string.Equals(action.client, NativeAutotestRequest.ActiveClient,
                               StringComparison.OrdinalIgnoreCase)
              || string.Equals(action.client, "*", StringComparison.Ordinal))
          .ToArray();
      LoadCompletedActionIds();
      WriteReceipt("scenario_loaded", string.Empty, "actions=" + _actions.Length);
    } catch (Exception exception) {
      RejectManifest("manifest_read_failed:" + exception.GetType().Name);
    }
  }

  string Validate(NativeCutoverScenarioManifest parsed) {
    if (parsed == null || parsed.schema_version != CurrentSchemaVersion)
      return "manifest_schema_invalid";
    if (!SafeToken(parsed.run_id, 80)
        || !string.Equals(parsed.run_id, NativeAutotestRequest.ActiveRunId,
            StringComparison.Ordinal))
      return "manifest_run_mismatch";
    if (parsed.actions == null || parsed.actions.Length == 0
        || parsed.actions.Length > MaxActions)
      return "manifest_action_count_invalid:"
          + (parsed.actions == null ? "null" : parsed.actions.Length.ToString(CultureInfo.InvariantCulture));
    if (!DateTimeOffset.TryParseExact(
            parsed.expires_utc ?? string.Empty, "o", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset expires)
        || expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddHours(2))
      return "manifest_expiry_invalid";

    HashSet<string> ids = new(StringComparer.Ordinal);
    foreach (NativeCutoverScenarioAction action in parsed.actions) {
      if (action == null || !SafeToken(action.id, 80) ||
          !ids.Add((action.client ?? string.Empty) + ":" + action.id))
        return "manifest_action_id_invalid";
      if (!SafeClient(action.client)) return "manifest_action_client_invalid";
      if (action.deadline_seconds is < 1.0f or > 300.0f)
        return "manifest_action_deadline_invalid";
      switch ((action.kind ?? string.Empty).Trim().ToLowerInvariant()) {
        case "wait":
          if (action.duration_seconds is < 0.1f or > 60.0f)
            return "manifest_wait_duration_invalid";
          break;
        case "move":
          if (action.duration_seconds is < 0.25f or > 60.0f
              || action.distance_meters is < 0.1f or > 64.0f
              || !AllowedDirection(action.direction))
            return "manifest_move_invalid";
          break;
        case "pickup_nearest":
          if (action.radius_meters is < 0.1f or > 8.0f)
            return "manifest_pickup_radius_invalid";
          break;
        case "ownership_target":
          if (action.radius_meters is < 0.1f or > 16.0f
              || !SafeTargetTag(action.target_tag, parsed.run_id))
            return "manifest_ownership_target_invalid";
          break;
        case "zone_cross":
          if (action.distance_meters is < 65.0f or > 160.0f
              || !AllowedDirection(action.direction))
            return "manifest_zone_cross_invalid";
          break;
        case "teleport_to":
          if (action.world_x is < -20000.0f or > 20000.0f
              || action.world_y is < -100.0f or > 2000.0f
              || action.world_z is < -20000.0f or > 20000.0f)
            return "manifest_teleport_target_invalid";
          break;
        case "disconnect":
        case "disconnect_resume":
          break;
        case "session_resume_probe":
        case "session_timeout_probe":
        case "gateway_restart_resume":
        case "direct_control_pulse":
        case "direct_control_withhold":
        case "routed_request":
        case "routed_broadcast":
        case "routed_target_zdo":
        case "routed_remote_stamina":
        case "routed_withhold":
        case "zdo_journal_drive":
        case "zdo_journal_observe":
          break;
        case "ship_water_rendezvous":
        case "ship_spawn":
        case "ship_wait":
        case "ship_board":
        case "ship_transfer":
        case "ship_wait_owner":
        case "ship_wait_released":
          break;
        case "ship_drive":
        case "ship_observe":
          if (action.duration_seconds is < 3.0f or > 20.0f)
            return "manifest_ship_duration_invalid";
          break;
        case "saddle_spawn":
        case "saddle_wait":
        case "saddle_rendezvous":
        case "saddle_wait_released":
        case "saddle_observe_reclaim":
          break;
        case "saddle_disconnect_reclaim":
          if (action.duration_seconds is < 3.0f or > 15.0f)
            return "manifest_saddle_reclaim_hold_invalid";
          break;
        case "saddle_drive":
        case "saddle_observe":
          if (action.duration_seconds is < 3.0f or > 20.0f)
            return "manifest_saddle_duration_invalid";
          break;
        case "ownership_lease_pickup":
        case "ownership_contention":
        case "zone_membership_resume":
        case "zone_membership_withhold":
          break;
        case "motion_rendezvous":
          break;
        case "portal_roundtrip":
          if (action.radius_meters is < 1.0f or > 256.0f)
            return "manifest_portal_radius_invalid";
          break;
        case "motion_drive":
        case "motion_observe":
        case "motion_drive_gap":
        case "motion_observe_gap":
          if (action.duration_seconds is < 2.0f or > 20.0f ||
              action.distance_meters is < 0.0f or > 24.0f ||
              ((action.kind == "motion_drive" ||
                action.kind == "motion_drive_gap") &&
               action.distance_meters < 0.5f) ||
              !AllowedDirection(action.direction) ||
              !SafeToken(action.target_tag, 80))
             return "manifest_motion_probe_invalid";
           break;
        case "socket_quarantine":
          if (action.duration_seconds is < 60.0f or > 120.0f)
            return "manifest_socket_quarantine_invalid";
          break;
        default:
          return "manifest_action_kind_invalid";
      }
    }
    return string.Empty;
  }

  void Begin(NativeCutoverScenarioAction action, float now) {
    _active = action;
    _actionStartedAt = now;
    _actionDeadlineAt = now + action.deadline_seconds;
    _nextActionDiagnosticAt = now + 5.0f;
    _origin = ((Component)Player.m_localPlayer).transform.position;
    _originZone = ZoneSystem.GetZone(_origin);
    _inventoryBefore = InventoryCount();
    _ownershipTarget = null;
    _portalSourceId = ZDOID.None;
    _portalTargetId = ZDOID.None;
    _portalSourcePosition = Vector3.zero;
    _portalTargetPosition = Vector3.zero;
    _portalRoundtripStage = 0;
    _portalNextTeleportAt = now + 2.1f;
    _sessionProbeStarted = false;
    WriteReceipt("action_started", action.id, "kind=" + action.kind);

    string actionKind = action.kind.Trim().ToLowerInvariant();
    if (actionKind is "teleport_to" or "portal_roundtrip"
        && !EnsureTeleportSurvivalSafeguard(out string safeguardDetail)) {
      FailActive(safeguardDetail);
      return;
    }

    switch (actionKind) {
      case "pickup_nearest":
        if (!TryInteractNearest(action, out string pickupDetail))
          FailActive(pickupDetail);
        break;
      case "ownership_target":
        if (!TryClaimOwnership(action, out string ownershipDetail))
          FailActive(ownershipDetail);
        break;
      case "disconnect":
        CompleteActive("disconnect_requested");
        Game.instance?.Logout();
        break;
      case "disconnect_resume":
        _completedActionIds.Add(action.id);
        WriteReceipt("resume_requested", action.id, "disconnect_then_relaunch");
        _active = null;
        Game.instance?.Logout();
        break;
    }
  }

  void TickActive(float now) {
    string kind = _active.kind.Trim().ToLowerInvariant();
    switch (kind) {
      case "wait":
        if (now - _actionStartedAt >= _active.duration_seconds)
          CompleteActive("wait_elapsed");
        break;
      case "move": {
        float fraction = Mathf.Clamp01((now - _actionStartedAt) / _active.duration_seconds);
        Vector3 target = _origin + Direction(_active.direction) * (_active.distance_meters * fraction);
        target.y = _origin.y;
        ((Component)Player.m_localPlayer).transform.position = target;
        if (fraction >= 1.0f) CompleteActive("distance_reached");
        break;
      }
      case "pickup_nearest":
        if (_inventoryBefore >= 0 && InventoryCount() > _inventoryBefore)
          CompleteActive("inventory_incremented");
        break;
      case "ownership_target":
        if (OwnershipReached(_ownershipTarget))
          CompleteActive("local_owner_observed");
        break;
      case "zone_cross": {
        // The raw position write can be reverted by the character's own
        // per-tick processing, and completing off the intended target let
        // the next action sample the pre-cross zone (full31's stale
        // membership enter). Complete only when the zone read at tick entry
        // - before this tick's write - already reports the new zone, so the
        // move has demonstrably survived a full engine tick before any
        // dependent action samples it.
        Vector2i settledZone = ZoneSystem.GetZone(
            ((Component)Player.m_localPlayer).transform.position);
        if (settledZone.x != _originZone.x || settledZone.y != _originZone.y) {
          CompleteActive(
              "zone_changed_from=" + _originZone.x + "," + _originZone.y
              + "_to=" + settledZone.x + "," + settledZone.y);
          break;
        }
        Vector3 target = _origin + Direction(_active.direction) * _active.distance_meters;
        target.y = _origin.y;
        ((Component)Player.m_localPlayer).transform.position = target;
        break;
      }
      case "teleport_to": {
        Vector3 target =
            new(_active.world_x, _active.world_y, _active.world_z);
        if (!_sessionProbeStarted) {
          bool accepted = Player.m_localPlayer.TeleportTo(
              target,
              ((Component)Player.m_localPlayer).transform.rotation,
              distantTeleport: true);
          if (!accepted) return;
          _sessionProbeStarted = true;
          WriteReceipt(
              "coordinate_teleport_started", _active.id,
              "target=" + FormatVector(target));
          return;
        }
        if (now >= _nextActionDiagnosticAt) {
          _nextActionDiagnosticAt = now + 5.0f;
          WriteReceipt(
              "coordinate_teleport_progress", _active.id,
              DescribeTeleportReadiness());
        }
        if (!Player.m_localPlayer.IsTeleporting()
            && Vector3.Distance(
                   ((Component)Player.m_localPlayer).transform.position,
                   target) <= 12.0f)
          CompleteActive("coordinate_teleport_complete target=" + FormatVector(target));
        break;
      }
      case "session_resume_probe":
      case "session_timeout_probe":
      case "gateway_restart_resume": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "session_resume_probe" => "resume",
              "session_timeout_probe" => "withhold_receipt",
              _ => "external_resume"
          };
          if (!_gameSession.BeginProbe(
                  _active.id, mode, Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "lumberjacks_session_not_connected") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_gameSession.TryGetProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "direct_control_pulse":
      case "direct_control_withhold": {
        if (!_sessionProbeStarted) {
          string mode = kind == "direct_control_pulse" ? "deliver" : "withhold";
          if (!_gameSession.BeginDirectPulseProbe(
                  _active.id, mode, Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "lumberjacks_session_not_connected") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_gameSession.TryGetDirectPulseProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "routed_request":
      case "routed_broadcast":
      case "routed_target_zdo":
      case "routed_remote_stamina":
      case "routed_withhold": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "routed_request" => "request",
              "routed_broadcast" => "broadcast",
              "routed_target_zdo" => "target_zdo",
              "routed_remote_stamina" => "remote_stamina",
              _ => "withhold"
          };
          if (!_routedRpc.BeginProbe(
                  _active.id, mode,
                  Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is "lumberjacks_session_not_connected"
                or "routed_probe_client_not_ready"
                or "routed_remote_player_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_routedRpc.TryGetProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "zdo_journal_drive":
      case "zdo_journal_observe": {
        if (!_sessionProbeStarted) {
          string mode =
              kind == "zdo_journal_drive" ? "drive" : "observe";
          if (!_zdoJournal.BeginProbe(
                  _active.id, mode,
                  Mathf.Max(5.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "zdo_journal_client_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_zdoJournal.TryGetProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "ship_water_rendezvous":
      case "ship_spawn":
      case "ship_wait":
      case "ship_board":
      case "ship_drive":
      case "ship_observe":
      case "ship_transfer":
      case "ship_wait_owner":
      case "ship_wait_released": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "ship_water_rendezvous" => "water",
              "ship_spawn" => "spawn",
              "ship_wait" => "wait_ship",
              "ship_board" => "board",
              "ship_drive" => "drive",
              "ship_observe" => "observe",
              "ship_transfer" => "transfer",
              "ship_wait_owner" => "wait_owner",
              _ => "wait_released"
          };
          if (!_ship.BeginProbe(
                  _active.id,
                  mode,
                  _active.duration_seconds,
                  Mathf.Max(5.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is
                "ship_probe_client_not_ready" or
                "ship_cutover_not_enabled") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_ship.TryGetProbeResult(
                _active.id,
                out bool terminal,
                out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "saddle_spawn":
      case "saddle_wait":
      case "saddle_rendezvous":
      case "saddle_drive":
      case "saddle_observe":
      case "saddle_wait_released":
      case "saddle_disconnect_reclaim":
      case "saddle_observe_reclaim": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "saddle_spawn" => "spawn",
              "saddle_wait" => "wait_mount",
              "saddle_rendezvous" => "rendezvous",
              "saddle_drive" => "drive",
              "saddle_observe" => "observe",
              "saddle_wait_released" => "wait_released",
              "saddle_disconnect_reclaim" => "disconnect_reclaim",
              _ => "observe_reclaim"
          };
          if (!_saddle.BeginProbe(
                  _active.id,
                  mode,
                  _active.duration_seconds,
                  Mathf.Max(5.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is
                "saddle_probe_client_not_ready" or
                "saddle_cutover_not_enabled") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_saddle.TryGetProbeResult(
                _active.id,
                out bool terminal,
                out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "ownership_lease_pickup": {
        if (!_sessionProbeStarted) {
          if (!_ownershipLease.BeginProbe(
                  _active.id,
                  Mathf.Max(20.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is "lumberjacks_session_not_connected"
                or "ownership_lease_client_not_ready"
                or "ownership_world_epoch_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_ownershipLease.TryGetProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "ownership_contention": {
        if (!_sessionProbeStarted) {
          if (!_ownershipLease.BeginContentionProbe(
                  _active.id,
                  Mathf.Max(10.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is "lumberjacks_session_not_connected"
                or "ownership_lease_client_not_ready"
                or "ownership_world_epoch_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_ownershipLease.TryGetProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "zone_membership_resume":
      case "zone_membership_withhold": {
        if (!_sessionProbeStarted) {
          string mode =
              kind == "zone_membership_resume" ? "resume_once" : "withhold";
          if (!_worldZone.BeginMembershipProbe(
                  _active.id, mode,
                  Mathf.Max(10.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is
                "world_zone_client_not_ready" or
                "world_zone_enter_queue_full")
              return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_worldZone.TryGetMembershipProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "motion_rendezvous": {
        if (!_motion.TryRendezvous(
                _active.id, out bool complete, out string rendezvousDetail)) {
          FailActive(rendezvousDetail);
          return;
        }
        if (complete) CompleteActive(rendezvousDetail);
        break;
      }
      case "portal_roundtrip":
        TickPortalRoundtrip(now);
        break;
      case "motion_drive":
      case "motion_observe":
      case "motion_drive_gap":
      case "motion_observe_gap": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "motion_drive" => "drive",
              "motion_observe" => "observe",
              "motion_drive_gap" => "drive_gap",
              _ => "observe_gap"
          };
          if (!_motion.BeginAuthorityProbe(
                  _active.id,
                  mode,
                  _active.target_tag,
                  _active.duration_seconds,
                  Mathf.Max(
                      _active.duration_seconds + 2.0f,
                      _active.deadline_seconds - 1.0f),
                  _active.distance_meters,
                  _active.direction,
                  out string startDetail)) {
            if (startDetail is
                "lumberjacks_session_not_connected" or
                "motion_remote_not_ready" or
                "motion_local_player_not_ready")
              return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_motion.TryGetAuthorityProbeResult(
                _active.id,
                out bool terminal,
                out bool success,
                out string probeDetail) ||
            !terminal)
          return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "socket_quarantine":
        if (!_sessionProbeStarted) {
          if (!_socketQuarantine.Begin(
                  _active.id, _active.duration_seconds,
                  out string startDetail)) {
            if (startDetail is
                "lumberjacks_session_not_ready" or
                "lumberjacks_world_descriptor_not_ready" or
                "valheim_scene_not_ready" or
                "connected_native_server_socket_not_found")
              return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_socketQuarantine.TryGetResult(
                _active.id, out bool quarantineTerminal,
                out bool quarantineSuccess, out string quarantineDetail)
            || !quarantineTerminal)
          return;
        if (quarantineSuccess) CompleteActive(quarantineDetail);
        else FailActive(quarantineDetail);
        break;
      }
  }

  bool TryInteractNearest(NativeCutoverScenarioAction action, out string detail) {
    detail = string.Empty;
    Type pickableType = AccessTools.TypeByName("Pickable");
    if (pickableType == null) {
      detail = "pickable_type_unavailable";
      return false;
    }

    Component nearest = null;
    float nearestDistance = float.MaxValue;
    foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(pickableType)) {
      if (candidate is not Component component || component.gameObject == null
          || !component.gameObject.activeInHierarchy) continue;
      float distance =
          Vector3.Distance(((Component)Player.m_localPlayer).transform.position,
              component.transform.position);
      if (distance <= action.radius_meters && distance < nearestDistance) {
        nearest = component;
        nearestDistance = distance;
      }
    }
    if (nearest == null) {
      detail = "pickable_not_found";
      return false;
    }

    MethodInfo interact = nearest.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .FirstOrDefault(method => method.Name == "Interact"
            && method.GetParameters().Length == 3);
    if (interact == null) {
      detail = "pickable_interact_unavailable";
      return false;
    }
    try {
      object result = interact.Invoke(nearest, new object[] { Player.m_localPlayer, false, false });
      if (result is bool accepted && !accepted) {
        detail = "pickable_interact_refused";
        return false;
      }
      return true;
    } catch (Exception exception) {
      detail = "pickable_interact_failed:" + (exception.InnerException ?? exception).GetType().Name;
      return false;
    }
  }

  void TickPortalRoundtrip(float now) {
    Player player = Player.m_localPlayer;
    if (player == null) {
      FailActive("portal_local_player_missing");
      return;
    }

    if (_portalRoundtripStage == 0) {
      if (now < _portalNextTeleportAt) return;
      if (ZoneSystem.instance != null
          && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals)) {
        FailActive("portal_global_key_disabled");
        return;
      }

      TeleportWorld source = FindPortal(
          ZDOID.None, _active.radius_meters, requireLocalTarget: true,
          out ZDO sourceZdo, out ZDO targetZdo, out string findDetail);
      if (source == null) {
        // Far-portal dependency delivery is AoI-transient right after a
        // relocation: full32 failed 2s into a 22s deadline while the linked
        // portal's ZDO was still in flight. Wait for the correlated arrival,
        // bounded by the action deadline, instead of failing on capacity
        // timing; malformed portal state still fails immediately below.
        if (findDetail is
            "portal_target_descriptor_missing" or
            "distant_portal_pair_not_in_aoi") {
          if (now >= _nextActionDiagnosticAt) {
            _nextActionDiagnosticAt = now + 5.0f;
            WriteReceipt("portal_pair_await_dependencies", _active.id, findDetail);
          }
          return;
        }
        FailActive(findDetail);
        return;
      }

      _portalSourceId = sourceZdo.m_uid;
      _portalTargetId = targetZdo.m_uid;
      _portalSourcePosition = sourceZdo.GetPosition();
      _portalTargetPosition = targetZdo.GetPosition();
      if (_portalSourceId == _portalTargetId
          || targetZdo.GetConnectionZDOID(
                 ZDOExtraData.ConnectionType.Portal) != _portalSourceId) {
        FailActive(
            "portal_reverse_link_mismatch source=" + _portalSourceId
            + " target=" + _portalTargetId);
        return;
      }

      source.Teleport(player);
      _portalRoundtripStage = 1;
      _portalNextTeleportAt = 0.0f;
      _nextActionDiagnosticAt = now + 1.0f;
      WriteReceipt(
          "portal_outbound_started", _active.id,
          "source=" + _portalSourceId + " target=" + _portalTargetId
          + " distance=" +
          Vector3.Distance(_portalSourcePosition, _portalTargetPosition)
              .ToString("0.##", CultureInfo.InvariantCulture));
      return;
    }

    if (_portalRoundtripStage == 1) {
      if (now >= _nextActionDiagnosticAt) {
        _nextActionDiagnosticAt = now + 1.0f;
        WriteReceipt(
            "portal_outbound_progress", _active.id,
            "stage=outbound god=" + player.InGodMode()
            + " dead=" + player.IsDead() + " "
            + DescribeTeleportReadiness(_portalTargetPosition));
      }
      if (player.IsTeleporting()
          || Vector3.Distance(
                 ((Component)player).transform.position,
                 _portalTargetPosition) > 12.0f)
        return;

      if (_portalNextTeleportAt <= 0.0f) {
        _portalNextTeleportAt = now + 2.1f;
        return;
      }
      if (now < _portalNextTeleportAt) return;

      TeleportWorld target = FindPortal(
          _portalTargetId, 32.0f, requireLocalTarget: false,
          out ZDO targetZdo, out ZDO sourceZdo, out string findDetail);
      if (target == null) {
        if (findDetail == "portal_not_in_aoi") return;
        FailActive(findDetail);
        return;
      }
      if (targetZdo.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.Portal) != _portalSourceId
          || sourceZdo == null || sourceZdo.m_uid != _portalSourceId) {
        FailActive(
            "portal_reverse_link_mismatch source=" + _portalSourceId
            + " target=" + _portalTargetId);
        return;
      }

      target.Teleport(player);
      _portalRoundtripStage = 2;
      _portalNextTeleportAt = 0.0f;
      _nextActionDiagnosticAt = now + 1.0f;
      WriteReceipt(
          "portal_return_started", _active.id,
          "source=" + _portalTargetId + " target=" + _portalSourceId);
      return;
    }

    if (now >= _nextActionDiagnosticAt) {
      _nextActionDiagnosticAt = now + 1.0f;
      WriteReceipt(
          "portal_return_progress", _active.id,
          "stage=return god=" + player.InGodMode()
          + " dead=" + player.IsDead() + " "
          + DescribeTeleportReadiness(_portalSourcePosition));
    }
    if (!player.IsTeleporting()
        && Vector3.Distance(
               ((Component)player).transform.position,
               _portalSourcePosition) <= 12.0f) {
      CompleteActive(
          "portal_roundtrip_complete source=" + _portalSourceId
          + " target=" + _portalTargetId + " forward=true reverse=true");
    }
  }

  static TeleportWorld FindPortal(
      ZDOID requiredId,
      float radius,
      bool requireLocalTarget,
      out ZDO portalZdo,
      out ZDO targetZdo,
      out string detail) {
    portalZdo = null;
    targetZdo = null;
    detail = "portal_not_in_aoi";
    Vector3 playerPosition =
        ((Component)Player.m_localPlayer).transform.position;
    bool connectedPortalSeen = false;
    bool missingTargetSeen = false;
    TeleportWorld selected = null;
    float selectedDistance = float.MaxValue;

    foreach (TeleportWorld candidate
             in Resources.FindObjectsOfTypeAll<TeleportWorld>()) {
      if (candidate == null || candidate.gameObject == null
          || !candidate.gameObject.activeInHierarchy)
        continue;
      ZNetView view = candidate.GetComponent<ZNetView>();
      ZDO candidateZdo = view?.GetZDO();
      if (candidateZdo == null
          || (requiredId != ZDOID.None && candidateZdo.m_uid != requiredId))
        continue;
      float distance =
          Vector3.Distance(playerPosition, candidate.transform.position);
      if (distance > radius) continue;
      ZDOID targetId = candidateZdo.GetConnectionZDOID(
          ZDOExtraData.ConnectionType.Portal);
      if (targetId == ZDOID.None) continue;
      connectedPortalSeen = true;
      ZDO candidateTarget = ZDOMan.instance?.GetZDO(targetId);
      if (candidateTarget == null) {
        missingTargetSeen = true;
        continue;
      }
      if (requireLocalTarget
          && Vector3.Distance(
                 candidateZdo.GetPosition(), candidateTarget.GetPosition())
             < 32.0f)
        continue;
      if (distance >= selectedDistance) continue;
      selected = candidate;
      selectedDistance = distance;
      portalZdo = candidateZdo;
      targetZdo = candidateTarget;
    }

    if (selected != null) {
      detail = string.Empty;
      return selected;
    }
    if (missingTargetSeen)
      detail = "portal_target_descriptor_missing";
    else if (connectedPortalSeen && requireLocalTarget)
      detail = "distant_portal_pair_not_in_aoi";
    return null;
  }

  bool TryClaimOwnership(NativeCutoverScenarioAction action, out string detail) {
    detail = string.Empty;
    ZNetView nearest = null;
    float nearestDistance = float.MaxValue;
    foreach (ZNetView candidate in Resources.FindObjectsOfTypeAll<ZNetView>()) {
      if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy
          || candidate.gameObject.name.IndexOf(action.target_tag,
                 StringComparison.OrdinalIgnoreCase) < 0) continue;
      float distance =
          Vector3.Distance(((Component)Player.m_localPlayer).transform.position,
              candidate.transform.position);
      if (distance <= action.radius_meters && distance < nearestDistance) {
        nearest = candidate;
        nearestDistance = distance;
      }
    }
    if (nearest == null) {
      detail = "run_tagged_ownership_target_not_found";
      return false;
    }
    try {
      nearest.ClaimOwnership();
      _ownershipTarget = nearest;
      return true;
    } catch (Exception exception) {
      detail = "ownership_claim_failed:" + exception.GetType().Name;
      return false;
    }
  }

  static bool OwnershipReached(object target) {
    if (target is not ZNetView view || view == null) return false;
    try {
      object zdo = view.GetZDO();
      if (zdo == null) return false;
      MethodInfo isOwner = AccessTools.Method(zdo.GetType(), "IsOwner", Type.EmptyTypes);
      return isOwner?.Invoke(zdo, null) is bool owned && owned;
    } catch {
      return false;
    }
  }

  static int InventoryCount() {
    try {
      object inventory = AccessTools.Method(Player.m_localPlayer.GetType(), "GetInventory")
          ?.Invoke(Player.m_localPlayer, null);
      if (inventory == null) return -1;
      object count = AccessTools.Method(inventory.GetType(), "NrOfItems", Type.EmptyTypes)
          ?.Invoke(inventory, null);
      return count == null ? -1 : Convert.ToInt32(count, CultureInfo.InvariantCulture);
    } catch {
      return -1;
    }
  }

  bool EnsureTeleportSurvivalSafeguard(out string detail) {
    Player player = Player.m_localPlayer;
    if (player == null) {
      detail = "teleport_safeguard_local_player_missing";
      return false;
    }
    try {
      if (!player.InGodMode()) player.SetGodMode(true);
      bool enabled = player.InGodMode();
      bool alive = !player.IsDead();
      detail = "god=" + enabled
          + " fly=" + player.InDebugFlyMode()
          + " dead=" + !alive;
      WriteReceipt(
          enabled && alive
              ? "teleport_safeguard_enabled"
              : "teleport_safeguard_failed",
          _active?.id ?? string.Empty, detail);
      return enabled && alive;
    } catch (Exception exception) {
      detail = "teleport_safeguard_failed=" + exception.GetType().Name
          + ":" + exception.Message;
      return false;
    }
  }

  string DescribeTeleportReadiness() {
    Vector3 target =
        new(_active.world_x, _active.world_y, _active.world_z);
    return DescribeTeleportReadiness(target);
  }

  string DescribeTeleportReadiness(Vector3 target) {
    try {
      Vector3 current =
          ((Component) Player.m_localPlayer).transform.position;
      Vector3 reference = ZNet.instance.GetReferencePosition();
      Vector2i targetZone = ZoneSystem.GetZone(target);
      Vector2i referenceZone = ZoneSystem.GetZone(reference);
      bool zoneLoaded =
          ZoneSystem.instance != null &&
          ZoneSystem.instance.IsZoneLoaded(targetZone);
      List<ZDO> objects = new();
      ZDOMan.instance?.FindSectorObjects(targetZone, 1, 0, objects);
      int valid = 0;
      int instantiated = 0;
      // When only a handful of objects hold area_ready false, name them:
      // full34's i5 sat at missing=1 of 1,245 for ten stable seconds and the
      // receipt could not say which object or prefab the readiness tail was.
      List<string> missingObjects = new();
      if (ZNetScene.instance != null) {
        foreach (ZDO zdo in objects) {
          if (zdo == null || zdo.GetPrefab() == 0 ||
              ZNetScene.instance.GetPrefab(zdo.GetPrefab()) == null)
            continue;
          valid++;
          if (ZNetScene.instance.FindInstance(zdo) != null) {
            instantiated++;
          } else if (missingObjects.Count < 5) {
            UnityEngine.GameObject missingPrefab =
                ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            missingObjects.Add(
                zdo.m_uid + ":"
                + (missingPrefab != null ? missingPrefab.name : "unknown"));
          }
        }
      }
      bool areaReady =
          ZNetScene.instance != null &&
          ZNetScene.instance.IsAreaReady(target);
      return
          "teleporting=" + Player.m_localPlayer.IsTeleporting()
          + " distance=" +
              Vector3.Distance(current, target)
                  .ToString("0.##", CultureInfo.InvariantCulture)
          + " target_zone=" + targetZone.x + "," + targetZone.y
          + " reference_zone=" + referenceZone.x + "," + referenceZone.y
          + " zone_loaded=" + zoneLoaded
          + " area_ready=" + areaReady
          + " sector_objects=" + objects.Count
          + " valid_prefabs=" + valid
          + " instantiated=" + instantiated
          + " missing=" + Math.Max(0, valid - instantiated)
          + (missingObjects.Count > 0
              ? " missing_objects=" + string.Join("|", missingObjects.ToArray())
              : string.Empty)
          + " " + (_zdoJournal?.DescribeRuntimeProgress()
              ?? "zdo_progress=unavailable");
    } catch (Exception exception) {
      return "teleport_readiness_failed=" + exception.GetType().Name
          + ":" + exception.Message;
    }
  }

  void CompleteActive(string detail) {
    string id = _active?.id ?? string.Empty;
    if (!string.IsNullOrEmpty(id)) _completedActionIds.Add(id);
    WriteReceipt("completed", id, detail);
    _active = null;
  }

  void FailActive(string detail) {
    WriteReceipt("failed", _active?.id ?? string.Empty, detail);
    _active = null;
    _terminal = true;
  }

  void RejectManifest(string detail) {
    WriteReceipt("manifest_rejected", string.Empty, detail);
    _terminal = true;
  }

  void LoadCompletedActionIds() {
    if (!File.Exists(_receiptPath)) return;
    try {
      foreach (string line in File.ReadLines(_receiptPath)) {
        NativeCutoverScenarioReceipt receipt =
            JsonUtility.FromJson<NativeCutoverScenarioReceipt>(line);
        if (receipt == null
            || !string.Equals(receipt.run_id, NativeAutotestRequest.ActiveRunId,
                StringComparison.Ordinal)
            || !string.Equals(receipt.client, NativeAutotestRequest.ActiveClient,
                StringComparison.OrdinalIgnoreCase)
            || receipt.state is not ("completed" or "resume_requested")
            || !SafeToken(receipt.action_id, 80)) continue;
        _completedActionIds.Add(receipt.action_id);
      }
    } catch (Exception exception) {
      RejectManifest("receipt_resume_read_failed:" + exception.GetType().Name);
    }
  }

  void WriteReceipt(string state, string actionId, string detail) {
    try {
      string line =
          "{\"schema_version\":1,\"timestamp_utc\":\""
          + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
          + "\",\"state\":\"" + Escape(state)
          + "\",\"run_id\":\"" + Escape(NativeAutotestRequest.ActiveRunId)
          + "\",\"client\":\"" + Escape(NativeAutotestRequest.ActiveClient)
          + "\",\"action_id\":\"" + Escape(actionId)
          + "\",\"detail\":\"" + Escape(detail) + "\"}" + Environment.NewLine;
      File.AppendAllText(_receiptPath, line);
      ComfyNetworkSense.LogInfo(
          "CUTOVER_SCENARIO state=" + SafeMarker(state)
          + " run_id=" + SafeMarker(NativeAutotestRequest.ActiveRunId)
          + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
          + " action=" + SafeMarker(actionId)
          + " detail=" + SafeMarker(detail));
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning(
          "Native cutover scenario receipt failed: " + exception.GetType().Name);
    }
  }

  void TryDeleteManifest() {
    try {
      if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning(
          "Native cutover scenario cleanup failed: " + exception.GetType().Name);
    }
  }

  static NativeCutoverScenarioManifest DeserializeManifest(string raw) {
    DataContractJsonSerializer serializer =
        new(typeof(NativeCutoverScenarioManifest));
    using MemoryStream stream = new(Encoding.UTF8.GetBytes(raw ?? string.Empty));
    return serializer.ReadObject(stream) as NativeCutoverScenarioManifest;
  }

  static bool JoinedClientReady() {
    ZNet znet = ZNet.instance;
    return znet != null && !znet.IsServer() && Player.m_localPlayer != null
        && (znet.GetPeers()?.Count ?? 0) > 0;
  }

  static bool SafeToken(string value, int maxLength) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength
      && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

  static bool SafeClient(string value) =>
      string.Equals(value, "*", StringComparison.Ordinal)
      || SafeToken(value, 32);

  static bool SafeTargetTag(string value, string runId) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= 96
      && value.StartsWith("cutover-" + runId, StringComparison.Ordinal)
      && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

  static bool AllowedDirection(string direction) =>
      (direction ?? string.Empty).Trim().ToLowerInvariant()
          is "north" or "east" or "south" or "west";

  static Vector3 Direction(string direction) {
    return (direction ?? string.Empty).Trim().ToLowerInvariant() switch {
        "east" => Vector3.right,
        "south" => Vector3.back,
        "west" => Vector3.left,
        _ => Vector3.forward
    };
  }

  static string SafeMarker(string value) =>
      string.IsNullOrWhiteSpace(value)
          ? "none"
          : value.Trim().Replace(' ', '_').Replace('\t', '_')
              .Replace('\r', '_').Replace('\n', '_');

  static string FormatVector(Vector3 value) =>
      value.x.ToString("0.##", CultureInfo.InvariantCulture) + ","
      + value.y.ToString("0.##", CultureInfo.InvariantCulture) + ","
      + value.z.ToString("0.##", CultureInfo.InvariantCulture);

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
          .Replace("\r", "_").Replace("\n", "_");

  public void Dispose() {
    if (_active != null && !_terminal)
      WriteReceipt("failed", _active.id, "plugin_disposed");
  }
}

[Serializable, DataContract]
public sealed class NativeCutoverScenarioManifest {
  [DataMember(Name = "schema_version")]
  public int schema_version;
  [DataMember(Name = "run_id")]
  public string run_id;
  [DataMember(Name = "expires_utc")]
  public string expires_utc;
  [DataMember(Name = "actions")]
  public NativeCutoverScenarioAction[] actions;
}

[Serializable, DataContract]
public sealed class NativeCutoverScenarioAction {
  [DataMember(Name = "id")]
  public string id;
  [DataMember(Name = "client")]
  public string client;
  [DataMember(Name = "kind")]
  public string kind;
  [DataMember(Name = "deadline_seconds")]
  public float deadline_seconds;
  [DataMember(Name = "duration_seconds")]
  public float duration_seconds;
  [DataMember(Name = "distance_meters")]
  public float distance_meters;
  [DataMember(Name = "radius_meters")]
  public float radius_meters;
  [DataMember(Name = "direction")]
  public string direction;
  [DataMember(Name = "target_tag")]
  public string target_tag;
  [DataMember(Name = "world_x")]
  public float world_x;
  [DataMember(Name = "world_y")]
  public float world_y;
  [DataMember(Name = "world_z")]
  public float world_z;
}

[Serializable]
public sealed class NativeCutoverScenarioReceipt {
  public string state;
  public string run_id;
  public string client;
  public string action_id;
}
