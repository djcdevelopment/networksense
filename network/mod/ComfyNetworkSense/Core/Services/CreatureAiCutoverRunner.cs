namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// Physical canary for Valheim's autonomous creature authority boundary.
/// The existing saddle lane supplies a real, tamed Lox plus canonical
/// owner epochs/snapshots. This runner proves the unridden MonsterAI branch:
/// exactly the owner executes BaseAI, the replica is denied it, and both see
/// motion carried by the canonical snapshot stream.
/// </summary>
public sealed class CreatureAiCutoverRunner : IDisposable {
  public const string ReceiptFileName = "creature-ai-cutover.jsonl";

  const float ProofRandomMoveInterval = 0.5f;
  const float ProofRandomMoveRange = 8.0f;
  static readonly int RunTagHash =
      ZdoJournalCutoverRunner.ProbeTagName.GetStableHashCode();
  static CreatureAiCutoverRunner _active;

  readonly SaddleCutoverRunner _saddle;
  readonly TelemetryLogWriter _writer = new();
  readonly HashSet<string> _firstAutonomousOwnerTicks =
      new(StringComparer.Ordinal);

  CreatureProbe _probe;
  bool _disposed;

  public CreatureAiCutoverRunner(SaddleCutoverRunner saddle) {
    _saddle = saddle;
    CreatureAiCutoverRunner previous =
        Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    TickProbe(now);
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float durationSeconds,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) ||
        !CreatureAiProofPolicy.AllowsMode(mode) ||
        durationSeconds is < 3.0f or > 20.0f) {
      detail = "creature_ai_probe_parameters_invalid";
      return false;
    }
    if (!Enabled()) {
      detail = "creature_ai_cutover_not_enabled";
      return false;
    }
    if (IsServer() || Player.m_localPlayer == null ||
        ZNetScene.instance == null || ZNet.instance == null) {
      detail = "creature_ai_probe_client_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_creature_ai_probe_active";
      return false;
    }
    string runId = CurrentRunId();
    if (!SafeToken(runId, 80)) {
      detail = "creature_ai_probe_run_missing";
      return false;
    }
    _probe = new CreatureProbe {
        RunId = runId,
        ActionId = actionId,
        Mode = mode,
        StartedAt = Time.unscaledTime,
        DeadlineAt = Time.unscaledTime +
            Mathf.Clamp(deadlineSeconds, 5.0f, 240.0f),
        Duration = Mathf.Clamp(durationSeconds, 3.0f, 20.0f)
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
      detail = "creature_ai_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  void TickProbe(float now) {
    CreatureProbe probe = _probe;
    if (probe == null || probe.Terminal || IsServer()) return;
    if (now > probe.DeadlineAt) {
      FailProbe("creature_ai_probe_deadline_exceeded phase=" +
          probe.Phase + " " + Progress(probe));
      return;
    }
    if (!TryFindRunCreature(
            probe.RunId, out Character creature,
            out ZNetView view, out MonsterAI ai)) {
      probe.Phase = "waiting_real_lox";
      return;
    }
    ZDO zdo = view.GetZDO();
    if (!_saddle.TryGetClientAuthorityState(
            zdo.m_uid, out long owner, out uint epoch, out int sequence)) {
      probe.Phase = "waiting_canonical_authority";
      return;
    }
    long local = Player.m_localPlayer.GetZDOID().UserID;
    bool localOwner = owner == local && zdo.GetOwner() == local && view.IsOwner();
    bool remoteOwner = owner != local && zdo.GetOwner() == owner && !view.IsOwner();
    probe.ReleasedEdgeRepairs += _saddle.RepairReleasedRiderEdges(zdo.m_uid);

    if (!probe.ObservationStarted) {
      if (HasRider(zdo)) {
        probe.Phase = "waiting_unridden_creature";
        return;
      }
      if ((probe.Mode == "drive" && !localOwner) ||
          (probe.Mode == "observe" && !remoteOwner)) {
        probe.Phase = probe.Mode == "drive"
            ? "waiting_local_ai_authority"
            : "waiting_remote_ai_authority";
        return;
      }
      probe.ObservationStarted = true;
      probe.ProofStartedAt = now;
      probe.Uid = zdo.m_uid;
      probe.OwnerPeerId = owner;
      probe.Epoch = epoch;
      probe.StartSequence = sequence;
      probe.StartPosition = creature.transform.position;
      probe.Phase = probe.Mode == "drive"
          ? "executing_owner_ai"
          : "observing_owner_gate";
      if (probe.Mode == "drive") {
        probe.ConfiguredAi = ai;
        probe.OriginalMoveInterval = ai.m_randomMoveInterval;
        probe.OriginalMoveRange = ai.m_randomMoveRange;
        probe.OriginalFollowTarget = ai.GetFollowTarget();
        ai.SetFollowTarget(null);
        ai.m_randomMoveInterval = ProofRandomMoveInterval;
        ai.m_randomMoveRange = ProofRandomMoveRange;
        ai.ResetRandomMovement();
      }
      Write("ai_proof_window_started", probe.ActionId,
          "mode=" + probe.Mode + " uid=" + probe.Uid +
          " prefab=Lox owner=" + owner + " epoch=" + epoch +
          " local=" + local + " rider=0 sequence=" + sequence);
      return;
    }

    if (zdo.m_uid != probe.Uid || owner != probe.OwnerPeerId ||
        epoch != probe.Epoch) {
      probe.AuthorityChanged = true;
      FailProbe("creature_ai_authority_changed " + Progress(probe));
      return;
    }
    if (HasRider(zdo)) {
      probe.RiderObserved = true;
      FailProbe("creature_ai_rider_entered_proof_window " + Progress(probe));
      return;
    }
    if ((probe.Mode == "drive" && !localOwner) ||
        (probe.Mode == "observe" && !remoteOwner)) {
      probe.AuthorityChanged = true;
      FailProbe("creature_ai_runtime_owner_mismatch " + Progress(probe));
      return;
    }

    probe.MaxDistance = Mathf.Max(
        probe.MaxDistance,
        Vector3.Distance(probe.StartPosition, creature.transform.position));
    probe.SnapshotAdvance = Math.Max(
        probe.SnapshotAdvance,
        Math.Max(0, sequence - probe.StartSequence));
    if (now - probe.ProofStartedAt < probe.Duration) return;

    bool passed = probe.Mode == "drive"
        ? CreatureAiProofPolicy.DrivePasses(
            probe.OwnerTicks, probe.BlockedTicks, probe.RiderObserved,
            probe.AuthorityChanged, probe.MaxDistance,
            probe.SnapshotAdvance)
        : CreatureAiProofPolicy.ObservePasses(
            probe.OwnerTicks, probe.BlockedTicks, probe.RiderObserved,
            probe.AuthorityChanged, probe.MaxDistance,
            probe.SnapshotAdvance);
    if (passed)
      CompleteProbe("creature_ai_" + probe.Mode + "_complete " +
          Progress(probe));
    else
      FailProbe("creature_ai_" + probe.Mode + "_semantics_missing " +
          Progress(probe));
  }

  internal static void NotifyBaseAiUpdate(BaseAI ai, bool applied) {
    CreatureAiCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || active._disposed || !Enabled() || IsServer() ||
        !TryReadRunCreature(ai, out ZNetView view, out ZDO zdo,
            out string runId) ||
        !string.Equals(runId, CurrentRunId(), StringComparison.Ordinal) ||
        !active._saddle.TryGetClientAuthorityState(
            zdo.m_uid, out long owner, out uint epoch, out _))
      return;

    long local = Player.m_localPlayer?.GetZDOID().UserID ?? 0L;
    bool localOwner = local != 0 && owner == local &&
        zdo.GetOwner() == local && view.IsOwner();
    bool rider = HasRider(zdo);
    if (applied && localOwner && !rider) {
      string key = zdo.m_uid + ":" +
          epoch.ToString(CultureInfo.InvariantCulture);
      if (active._firstAutonomousOwnerTicks.Add(key)) {
        CreatureProbe current = active._probe;
        active.Write("first_autonomous_owner_ai_tick",
            current != null && !current.Terminal ? current.ActionId : "unscoped",
            "uid=" + zdo.m_uid + " prefab=Lox owner=" + owner +
            " epoch=" + epoch + " rider=0");
      }
    }

    CreatureProbe probe = active._probe;
    if (probe == null || probe.Terminal || !probe.ObservationStarted ||
        probe.Uid != zdo.m_uid ||
        !string.Equals(probe.RunId, runId, StringComparison.Ordinal))
      return;
    if (applied) probe.OwnerTicks++;
    else probe.BlockedTicks++;
    if (rider) probe.RiderObserved = true;
    if (owner != probe.OwnerPeerId || epoch != probe.Epoch)
      probe.AuthorityChanged = true;
  }

  static bool TryReadRunCreature(
      BaseAI ai,
      out ZNetView view,
      out ZDO zdo,
      out string runId) {
    view = null;
    zdo = null;
    runId = string.Empty;
    if (ai is not MonsterAI || !ai.gameObject.activeInHierarchy) return false;
    Character creature = ai.GetComponent<Character>();
    view = creature?.GetComponent<ZNetView>();
    zdo = view?.GetZDO();
    if (creature == null || creature.IsPlayer() || zdo == null) return false;
    runId = zdo.GetString(RunTagHash, string.Empty);
    return !string.IsNullOrEmpty(runId);
  }

  static bool TryFindRunCreature(
      string runId,
      out Character creature,
      out ZNetView view,
      out MonsterAI ai) {
    creature = null;
    view = null;
    ai = null;
    foreach (Character candidate in Character.GetAllCharacters().ToArray()) {
      if (candidate == null || candidate.IsPlayer() ||
          !candidate.gameObject.activeInHierarchy) continue;
      ZNetView candidateView = candidate.GetComponent<ZNetView>();
      ZDO candidateZdo = candidateView?.GetZDO();
      MonsterAI candidateAi = candidate.GetComponent<MonsterAI>();
      if (candidateZdo == null || candidateAi == null ||
          !string.Equals(
              candidateZdo.GetString(RunTagHash, string.Empty),
              runId, StringComparison.Ordinal)) continue;
      creature = candidate;
      view = candidateView;
      ai = candidateAi;
      return true;
    }
    return false;
  }

  static bool HasRider(ZDO mount) {
    if (mount == null || mount.GetLong(ZDOVars.s_user, 0L) != 0L) return true;
    foreach (Player player in Player.GetAllPlayers()) {
      ZDO playerZdo = player?.GetComponent<ZNetView>()?.GetZDO();
      if (playerZdo != null && playerZdo.GetConnectionZDOID(
              ZDOExtraData.ConnectionType.SyncTransform) == mount.m_uid)
        return true;
    }
    return false;
  }

  void RestoreAi(CreatureProbe probe) {
    if (probe?.ConfiguredAi == null) return;
    try {
      probe.ConfiguredAi.m_randomMoveInterval = probe.OriginalMoveInterval;
      probe.ConfiguredAi.m_randomMoveRange = probe.OriginalMoveRange;
      probe.ConfiguredAi.SetFollowTarget(probe.OriginalFollowTarget);
    } catch { }
    probe.ConfiguredAi = null;
  }

  void CompleteProbe(string detail) {
    if (_probe == null || _probe.Terminal) return;
    RestoreAi(_probe);
    _probe.Terminal = true;
    _probe.Success = true;
    _probe.Detail = detail;
    Write("probe_passed", _probe.ActionId, detail);
  }

  void FailProbe(string detail) {
    if (_probe == null || _probe.Terminal) return;
    RestoreAi(_probe);
    _probe.Terminal = true;
    _probe.Success = false;
    _probe.Detail = detail;
    Write("probe_failed", _probe.ActionId, detail);
  }

  static string Progress(CreatureProbe probe) =>
      "mode=" + probe.Mode +
      " owner_ticks=" + probe.OwnerTicks +
      " blocked_ticks=" + probe.BlockedTicks +
      " distance=" + probe.MaxDistance.ToString(
          "0.###", CultureInfo.InvariantCulture) +
      " snapshot_advance=" + probe.SnapshotAdvance +
      " owner=" + probe.OwnerPeerId +
      " epoch=" + probe.Epoch +
      " rider_observed=" + probe.RiderObserved.ToString().ToLowerInvariant() +
      " authority_changed=" +
          probe.AuthorityChanged.ToString().ToLowerInvariant() +
      " released_edge_repairs=" + probe.ReleasedEdgeRepairs;

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
            ["action_id"] = SafeToken(actionId, 80)
                ? actionId : "unscoped",
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

  static string Role() => ZNet.instance == null ? "starting" :
      (IsServer() ? "server" : "client");

  static bool SafeToken(string value, int maximum) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
      value.All(character => char.IsLetterOrDigit(character) ||
          character is '-' or '_' or '.');

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    RestoreAi(_probe);
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  sealed class CreatureProbe {
    public string RunId;
    public string ActionId;
    public string Mode;
    public string Phase = "starting";
    public float StartedAt;
    public float DeadlineAt;
    public float Duration;
    public float ProofStartedAt;
    public ZDOID Uid;
    public long OwnerPeerId;
    public uint Epoch;
    public int StartSequence;
    public int SnapshotAdvance;
    public int OwnerTicks;
    public int BlockedTicks;
    public Vector3 StartPosition;
    public float MaxDistance;
    public bool ObservationStarted;
    public bool RiderObserved;
    public bool AuthorityChanged;
    public int ReleasedEdgeRepairs;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
    public MonsterAI ConfiguredAi;
    public float OriginalMoveInterval;
    public float OriginalMoveRange;
    public GameObject OriginalFollowTarget;
  }
}

[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.UpdateAI))]
static class CreatureAiAuthorityPatch {
  [HarmonyPostfix]
  static void Postfix(BaseAI __instance, bool __result) =>
      CreatureAiCutoverRunner.NotifyBaseAiUpdate(__instance, __result);
}
