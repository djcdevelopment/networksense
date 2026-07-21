namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Server-side gameplay-event producer (Increment 1 of the community-telemetry gameplay seam).
/// Observes creature deaths via <see cref="GameplayEventPatches"/> (a postfix on
/// <c>Character.RPC_Damage</c>, which on a dedicated server runs where the server owns the
/// creature), classifies with the Unity-free <see cref="GameplayEventClassifier"/>, and emits a
/// <c>killing_blow</c> to the gateway ingress <c>POST /valheim/events</c> — which captures the
/// public-safe projection into the live feed and forwards the full event to the durable EventLog.
///
/// Transport is <see cref="BoundedRawHttp"/> off the sim thread (ADR 0003: WebRequest.Create is
/// unusable under Valheim's stripped server Mono). Gated by
/// <see cref="PluginConfig.GameplayEventProducerEnabled"/> (default false) — off ⇒ the patch body
/// no-ops because <see cref="Active"/> is null, so combat is byte-for-byte untouched.
/// </summary>
public sealed class GameplayEventProducer : IDisposable {
  /// <summary>Live instance while armed; the static Harmony patch body reaches the producer through this.</summary>
  public static GameplayEventProducer Active { get; private set; }

  readonly GameplayEventClassifier _classifier = new();
  TelemetryCoordinator _coordinator;
  int _emitted;
  int _postedOk;
  int _postedFailed;
  string _lastError = string.Empty;

  public int Emitted => _emitted;
  public int PostedOk => _postedOk;
  public int PostedFailed => _postedFailed;

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    _coordinator = coordinator;

    // Edge-driven arming off the (hot-reloadable) config flag: Active is only non-null while
    // enabled, so a disarmed producer's patch body is a no-op.
    if (PluginConfig.GameplayEventProducerEnabled.Value) {
      Active = this;
    } else if (ReferenceEquals(Active, this)) {
      Active = null;
    }
  }

  /// <summary>
  /// Called from the Harmony postfix on the server/main thread. Reads the Unity/Valheim state here,
  /// hands primitives to the pure classifier, then posts off-thread. Never throws into the patch.
  /// </summary>
  public void OnCreatureDamaged(Character creature, HitData hit) {
    if (creature == null || hit == null || creature.IsPlayer()) {
      return;
    }

    Character attacker = hit.GetAttacker();
    bool attackerIsPlayer = attacker != null && attacker.IsPlayer();
    bool died = creature.IsDead();
    double now = Time.realtimeSinceStartup;

    GameplayEventKind kind = _classifier.ClassifyCreatureDamage(
        creature.GetInstanceID(), attackerIsPlayer, died, now);
    if (kind != GameplayEventKind.KillingBlow) {
      return;
    }

    string creatureCategory = NormalizeCreatureName(creature);
    string actorId = attacker is Player player ? player.GetPlayerID().ToString() : null;
    string weapon = hit.m_skill.ToString();

    Emit(GameplayEventTypes.KillingBlow, creatureCategory, actorId, weapon, hit.m_ranged);
  }

  void Emit(string eventType, string detail, string actorId, string weapon, bool ranged) {
    Dictionary<string, object> body = new() {
        ["event_type"] = eventType,
        ["occurred_at_utc"] = DateTime.UtcNow.ToString("o"),
        ["world_id"] = "valheim-era16",
        ["region_id"] = null,
        ["actor_id"] = actorId,
        ["detail"] = detail,
        ["payload"] = new Dictionary<string, object> {
            ["creature"] = detail,
            ["weapon"] = weapon,
            ["ranged"] = ranged
        }
    };

    _coordinator?.RecordGameplayEvent(new Dictionary<string, object>(body));
    _emitted++;

    string url = NormalizeEndpoint(PluginConfig.LumberjacksGatewayUrl.Value);
    if (string.IsNullOrEmpty(url)) {
      return;
    }

    _ = Task.Run(() => Post(url + "/valheim/events", body));
  }

  void Post(string url, Dictionary<string, object> body) {
    try {
      const int connectTimeoutMs = 5000, responseDeadlineMs = 5000, maxResponseBytes = 64 * 1024;
      string payload = JsonLineSerializer.Serialize(body);
      _ = BoundedRawHttp.PostForBody(url, payload, connectTimeoutMs, responseDeadlineMs, maxResponseBytes);
      _postedOk++;
    } catch (Exception exception) {
      _postedFailed++;
      _lastError = exception.GetType().Name + ": " + exception.Message;
    }
  }

  static string NormalizeCreatureName(Character creature) {
    string name = creature.m_name;
    if (string.IsNullOrWhiteSpace(name)) {
      name = creature.name;
    }

    if (string.IsNullOrWhiteSpace(name)) {
      return "unknown";
    }

    int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
    return clone >= 0 ? name.Substring(0, clone) : name;
  }

  static string NormalizeEndpoint(string value) {
    return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
  }

  public void Dispose() {
    if (ReferenceEquals(Active, this)) {
      Active = null;
    }
  }
}

/// <summary>Canonical gameplay event-type strings, mirroring the gateway's Game.Contracts.Events.EventType.</summary>
public static class GameplayEventTypes {
  public const string FirstHit = "first_hit";
  public const string KillingBlow = "killing_blow";
  public const string WeaponUsed = "weapon_used";
}
