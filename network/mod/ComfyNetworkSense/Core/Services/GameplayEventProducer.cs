namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Gameplay-event producer for the community-telemetry seam. Capture runs CLIENT-side (where the
/// acting player owns/simulates the creature it fights — the server never sees those hooks), and
/// the event is relayed to the server over a ComfyNetworkSense routed RPC. The server-side handler
/// is the only thing that can reach the gateway (private plane), so it does the POST.
///
/// Flow: client combat hook (<see cref="GameplayEventPatches"/>) -> <see cref="OnCreatureDamaged"/>
/// -> <see cref="GameplayEventClassifier"/> -> on killing_blow, <see cref="SendToServer"/> via
/// <c>ZRoutedRpc.InvokeRoutedRPC(GetServerPeerID(), ...)</c> -> <see cref="HandleGameplayEvent"/> on
/// the server -> POST <c>/valheim/events</c> (feed + EventLog) -> dashboard. Same DLL on both ends;
/// the hook naturally fires only where a creature is owned, and only the server POSTs.
///
/// Gated by <see cref="PluginConfig.GameplayEventProducerEnabled"/> (default false): off ⇒ the hook
/// no-ops (<see cref="Active"/> null) and combat is byte-for-byte untouched.
/// Discrete lifecycle lines (RPC registered / event sent / server received) log at Info; the per-hit
/// and per-death [gp] firehose is gated behind <see cref="PluginConfig.GameplayEventVerboseLogging"/>
/// (default false) — flip it on only when debugging why an event did or didn't emit.
/// </summary>
public sealed class GameplayEventProducer : IDisposable {
  public const string GameplayEventRpc = "ComfyNetworkSense_GameplayEvent";

  /// <summary>Live while armed; the static client-side patch bodies reach the producer through this.</summary>
  public static GameplayEventProducer Active { get; private set; }

  /// <summary>Always-set (arm-independent) instance so the static server-side RPC handler can POST.</summary>
  static GameplayEventProducer _live;

  const double RecentHitSeconds = 15.0;
  const int LastHitMaxEntries = 256;

  readonly GameplayEventClassifier _classifier = new();
  readonly QuestTriggerEvaluator _questEvaluator = new();
  readonly Dictionary<int, LastHit> _lastHit = new();
  TelemetryCoordinator _coordinator;
  ZRoutedRpc _registeredRpc;
  int _sent;
  int _received;
  int _postedOk;
  int _postedFailed;
  string _lastError = string.Empty;

  public int Sent => _sent;
  public int Received => _received;
  public int PostedOk => _postedOk;

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    _coordinator = coordinator;
    _live = this;

    // Register the routed-RPC handler once ZRoutedRpc is up (both ends; only the server is targeted,
    // so a client's registered handler simply never fires).
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc != null && _registeredRpc != rpc) {
      rpc.Register(GameplayEventRpc, new Action<long, ZPackage>(HandleGameplayEvent));
      _registeredRpc = rpc;
      ComfyNetworkSense.LogInfo("Registered routed RPC: " + GameplayEventRpc);
    }

    // Edge-driven arming: the client-side hook only acts while enabled.
    if (PluginConfig.GameplayEventProducerEnabled.Value) {
      Active = this;
    } else if (ReferenceEquals(Active, this)) {
      Active = null;
    }
  }

  /// <summary>
  /// Client-side combat-hook entry (from <see cref="GameplayEventPatches"/>). Reads Unity/Valheim
  /// state here, classifies with the pure classifier, and on a killing blow relays to the server.
  /// Fires for BOTH Character.Damage and RPC_Damage; the classifier dedups so one death emits once.
  /// </summary>
  public void OnCreatureDamaged(Character creature, HitData hit, string source) {
    if (creature == null || hit == null) {
      return;
    }

    Character attacker = hit.GetAttacker();
    bool attackerIsPlayer = attacker != null && attacker.IsPlayer();
    bool targetIsPlayer = creature.IsPlayer();
    bool died = creature.IsDead();

    // DIAGNOSTIC (verbose only): every damage this hook sees — a firehose during combat.
    if (PluginConfig.GameplayEventVerboseLogging.Value) {
      ComfyNetworkSense.LogInfo("[gp] " + source + " dmg target=" + creature.m_name
          + " targetIsPlayer=" + targetIsPlayer
          + " attacker=" + (attacker == null ? "null" : attacker.m_name)
          + " attackerIsPlayer=" + attackerIsPlayer + " died=" + died);
    }

    // IsDead() is not yet true at a Damage/RPC_Damage postfix, so we do NOT decide the kill here.
    // Instead remember the last player-attributed hit; the killing blow is emitted from OnDeath.
    if (targetIsPlayer || !attackerIsPlayer) {
      return;
    }

    int id = creature.GetInstanceID();
    double now = Time.realtimeSinceStartup;
    string creatureName = NormalizeCreatureName(creature);
    string weapon = hit.m_skill.ToString();
    long playerId = attacker is Player player ? player.GetPlayerID() : 0L;

    _lastHit[id] = new LastHit {
      PlayerId = playerId,
      Creature = creatureName,
      Weapon = weapon,
      Ranged = hit.m_ranged,
      TimeSeconds = now,
    };
    PruneLastHit();

    // first_hit: the first player-attributed hit on this creature (combat engaged).
    if (_classifier.RegisterHit(id, now) == GameplayEventKind.FirstHit) {
      SendToServer(GameplayEventTypes.FirstHit, creatureName, creatureName, weapon, hit.m_ranged, playerId);
    }
  }

  /// <summary>
  /// Client-side death hook (from <see cref="GameplayEventPatches"/> on <c>Character.OnDeath</c>).
  /// Fires exactly once when the creature dies, on its owner — the reliable kill signal. Emits a
  /// killing_blow attributed to the most recent player hit on that creature.
  /// </summary>
  public void OnCreatureDied(Character creature) {
    if (creature == null || creature.IsPlayer()) {
      return;
    }

    int id = creature.GetInstanceID();
    bool tracked = _lastHit.TryGetValue(id, out LastHit hit);
    if (PluginConfig.GameplayEventVerboseLogging.Value) {
      ComfyNetworkSense.LogInfo("[gp] death target=" + creature.m_name + " playerKill=" + tracked);
    }
    if (!tracked) {
      return;
    }

    _lastHit.Remove(id);
    double now = Time.realtimeSinceStartup;
    if (now - hit.TimeSeconds > RecentHitSeconds) {
      return;
    }

    if (_classifier.RegisterDeath(id, now) != GameplayEventKind.KillingBlow) {
      return;
    }

    // The kill, plus a weapon-usage event (detail = the weapon skill) for the alpha weapon stream.
    SendToServer(GameplayEventTypes.KillingBlow, hit.Creature, hit.Creature, hit.Weapon, hit.Ranged, hit.PlayerId);
    SendToServer(GameplayEventTypes.WeaponUsed, hit.Weapon, hit.Creature, hit.Weapon, hit.Ranged, hit.PlayerId);

    // Quest evaluator (arm-gated separately): does this kill complete any tracked quest? A match
    // relays a quest_completed over the same RPC seam. The quest name is the public-safe detail;
    // quest_id/guild/command ride the payload to the durable EventLog only.
    if (PluginConfig.QuestEvaluatorEnabled.Value) {
      IReadOnlyList<QuestCompletion> completions =
          _questEvaluator.OnCreatureKilled(QuestViewLoader.Quests, hit.Creature, hit.Weapon, hit.Ranged, now);
      foreach (QuestCompletion completion in completions) {
        SendToServer(GameplayEventTypes.QuestCompleted, completion.Name, hit.Creature, hit.Weapon, hit.Ranged,
            hit.PlayerId, completion.QuestId, completion.Guild, completion.Category, completion.BotCommand);
        ComfyNetworkSense.LogInfo("[gp] quest complete: " + completion.QuestId + " (" + completion.Name + ")");
      }
    }
  }

  void PruneLastHit() {
    if (_lastHit.Count < LastHitMaxEntries) {
      return;
    }

    double now = Time.realtimeSinceStartup;
    List<int> stale = new();
    foreach (KeyValuePair<int, LastHit> entry in _lastHit) {
      if (now - entry.Value.TimeSeconds > RecentHitSeconds) {
        stale.Add(entry.Key);
      }
    }

    foreach (int key in stale) {
      _lastHit.Remove(key);
    }
  }

  /// <summary>Relay one gameplay event to the server via the routed RPC (client→server). The trailing
  /// quest fields are empty for combat events and set only for a quest_completed.</summary>
  void SendToServer(string eventType, string detail, string creature, string weapon, bool ranged, long playerId,
      string questId = "", string questGuild = "", string questCategory = "", string questCommand = "") {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null) {
      return;
    }

    ZPackage package = new();
    package.Write(eventType);
    package.Write(detail ?? string.Empty);
    package.Write(creature ?? string.Empty);
    package.Write(weapon ?? string.Empty);
    package.Write(ranged);
    package.Write(playerId);
    package.Write(questId ?? string.Empty);
    package.Write(questGuild ?? string.Empty);
    package.Write(questCategory ?? string.Empty);
    package.Write(questCommand ?? string.Empty);

    // Target 0 (Everybody): a client's RouteRPC always routes to the server, which handles a
    // target==0 packet (NETCODE-MAP Funnel 4). The server then relays to all clients, whose
    // HandleGameplayEvent no-ops on the !IsServer gate — so only the server POSTs. A server-owned
    // kill is handled locally the same way. (GetServerPeerID is not a public ZRoutedRpc member.)
    rpc.InvokeRoutedRPC(0L, GameplayEventRpc, new object[] { package });
    _sent++;
    ComfyNetworkSense.LogInfo("[gp] sent " + eventType + " detail=" + detail + " -> server");
  }

  /// <summary>
  /// Server-side RPC handler: reached on the server when a client relays a gameplay event (or
  /// locally for a server-owned kill). Only the server POSTs — it is the sole peer on the gateway's
  /// private plane.
  /// </summary>
  static void HandleGameplayEvent(long sender, ZPackage package) {
    try {
      if (package == null || ZNet.instance == null || !ZNet.instance.IsServer()) {
        return;
      }

      GameplayEventProducer producer = _live;
      if (producer == null) {
        return;
      }

      string eventType = package.ReadString();
      string detail = package.ReadString();
      string creature = package.ReadString();
      string weapon = package.ReadString();
      bool ranged = package.ReadBool();
      long playerId = package.ReadLong();
      string questId = package.ReadString();
      string questGuild = package.ReadString();
      string questCategory = package.ReadString();
      string questCommand = package.ReadString();

      producer._received++;
      ComfyNetworkSense.LogInfo("[gp] server received " + eventType + " detail=" + detail
          + " player=" + playerId + " from peer=" + sender);
      producer.PostToGateway(eventType, detail, creature, weapon, ranged, playerId,
          questId, questGuild, questCategory, questCommand);
    } catch {
      // Telemetry is observational; never let a malformed relay disturb the server.
    }
  }

  void PostToGateway(string eventType, string detail, string creature, string weapon, bool ranged, long playerId,
      string questId = "", string questGuild = "", string questCategory = "", string questCommand = "") {
    Dictionary<string, object> payload = new() {
        ["creature"] = creature,
        ["weapon"] = weapon,
        ["ranged"] = ranged
    };

    // Quest fields ride the payload to the durable EventLog only (not the public feed's detail).
    if (!string.IsNullOrEmpty(questId)) {
      payload["quest_id"] = questId;
      payload["guild"] = questGuild;
      payload["category"] = questCategory;
      payload["bot_command"] = questCommand;
    }

    Dictionary<string, object> body = new() {
        ["event_type"] = eventType,
        ["occurred_at_utc"] = DateTime.UtcNow.ToString("o"),
        ["world_id"] = "valheim-era16",
        ["region_id"] = null,
        ["actor_id"] = playerId == 0L ? null : playerId.ToString(),
        ["detail"] = detail,
        ["payload"] = payload
    };

    _coordinator?.RecordGameplayEvent(new Dictionary<string, object>(body));

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
    if (ReferenceEquals(_live, this)) {
      _live = null;
    }
  }
}

/// <summary>The most recent player-attributed hit on a creature, carried from the damage hook to OnDeath.</summary>
public struct LastHit {
  public long PlayerId;
  public string Creature;
  public string Weapon;
  public bool Ranged;
  public double TimeSeconds;
}

/// <summary>Canonical gameplay event-type strings, mirroring the gateway's Game.Contracts.Events.EventType.</summary>
public static class GameplayEventTypes {
  public const string FirstHit = "first_hit";
  public const string KillingBlow = "killing_blow";
  public const string WeaponUsed = "weapon_used";
  public const string QuestCompleted = "quest_completed";
}
