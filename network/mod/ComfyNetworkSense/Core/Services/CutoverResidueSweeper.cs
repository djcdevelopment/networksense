namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

using HarmonyLib;

// Destroys leaked cutover-synthetic ZDOs. Completed scenario runs destroy their own probes
// (drive_complete / membership teardown); an ABORTED run leaks them, and the leaked objects
// accumulate in the spawn zone until its initial-load flood can no longer converge inside
// bootstrap deadlines (receipted: zone 35,-1 grew 1245 -> 1790 across aborted full32/full33).
//
// The identifying predicate never touches real world data:
//  - the C3/C5 probe prefab hashes have no ZNetScene prefab, so they cannot occur in vanilla
//    data (which is also why they inflate sector_objects without ever instantiating);
//  - ownership-lease targets are real "Raspberry" ItemDrops, so those additionally require a
//    non-empty ComfyNetworkSense_C3Tag string before they are considered synthetic.
public static class CutoverResidueSweeper {
  public const string AllCutoverTaggedMode = "all-cutover-tagged";

  static readonly int C3ProbePrefabHash =
      "ComfyNetworkSense_C3Probe".GetStableHashCode();
  static readonly int C5ZoneProbePrefabHash =
      "ComfyNetworkSense_C5ZoneProbe".GetStableHashCode();
  static readonly int RaspberryPrefabHash = "Raspberry".GetStableHashCode();
  static readonly HashSet<int> VehiclePrefabHashes = new() {
      "Raft".GetStableHashCode(),
      "Karve".GetStableHashCode(),
      "VikingShip".GetStableHashCode()
  };
  static readonly int MountPrefabHash = "Lox".GetStableHashCode();
  static readonly int ContainerPrefabHash =
      "piece_chest_wood".GetStableHashCode();
  static readonly int TerrainCompilerPrefabHash =
      "_TerrainCompiler".GetStableHashCode();
  static readonly int C3TagHash =
      ZdoJournalCutoverRunner.ProbeTagName.GetStableHashCode();
  static readonly int C5TagHash =
      WorldZoneCutoverRunner.MembershipTagName.GetStableHashCode();
  static readonly object UntaggedGate = new();
  static readonly Dictionary<string, HashSet<ZDOID>> UntaggedByRun =
      new(StringComparer.Ordinal);

  static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> _objectsByIdRef =
      AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
  static readonly MethodInfo HandleDestroyedZdoMethod =
      AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO",
          new[] { typeof(ZDOID) });

  // The spawn zone whose initial-load convergence the leak broke; its before/after count is
  // always receipted so the sweep verdict is one read, matching the teleport-readiness
  // sector_objects call shape (FindSectorObjects area=1) so the numbers stay comparable.
  static readonly Vector2i ReferenceZone = new(35, -1);

  /// <summary>
  /// Registers an exact synthetic object without writing a test tag into its
  /// ZDO. This lets the physical generalization gate exercise the same shape
  /// as an ordinary existing world mount while retaining exact cleanup.
  /// </summary>
  public static void RegisterUntagged(string runId, ZDOID uid) {
    if (string.IsNullOrWhiteSpace(runId) || uid.IsNone()) return;
    lock (UntaggedGate) {
      if (!UntaggedByRun.TryGetValue(runId, out HashSet<ZDOID> ids)) {
        ids = new HashSet<ZDOID>();
        UntaggedByRun[runId] = ids;
      }
      ids.Add(uid);
    }
  }

  /// <summary>
  /// Sweeps synthetic cutover residue. <paramref name="mode"/> is either a run id (destroy
  /// only that run's leftovers) or <see cref="AllCutoverTaggedMode"/> (destroy every tagged
  /// synthetic object — the one-time world sweep). Refused while a different run is active.
  /// </summary>
  public static bool TrySweep(string mode, out string before, out string after,
      out string effect, out string refusal) {
    before = string.Empty;
    after = string.Empty;
    effect = string.Empty;
    refusal = string.Empty;

    if (ZDOMan.instance == null) {
      refusal = "zdoman_unavailable";
      return false;
    }
    bool sweepAll = string.Equals(mode, AllCutoverTaggedMode, StringComparison.Ordinal);
    string activeRunId = NativeAutotestRequest.ActiveRunId;
    if (!string.IsNullOrEmpty(activeRunId) && !sweepAll &&
        !string.Equals(activeRunId, mode, StringComparison.Ordinal)) {
      refusal = "different_run_active";
      return false;
    }
    if (!string.IsNullOrEmpty(activeRunId) && sweepAll) {
      refusal = "run_active_refusing_world_sweep";
      return false;
    }

    Dictionary<ZDOID, ZDO> objects = _objectsByIdRef(ZDOMan.instance);
    if (objects == null) {
      refusal = "zdo_dictionary_unavailable";
      return false;
    }

    HashSet<ZDOID> trackedUntagged = new();
    lock (UntaggedGate) {
      if (sweepAll) {
        foreach (HashSet<ZDOID> ids in UntaggedByRun.Values)
          trackedUntagged.UnionWith(ids);
      } else if (UntaggedByRun.TryGetValue(mode, out HashSet<ZDOID> ids)) {
        trackedUntagged.UnionWith(ids);
      }
    }

    HashSet<long> liveOwners = new();
    List<ZNetPeer> peers = ZNet.instance?.GetPeers();
    if (peers != null) {
      foreach (ZNetPeer peer in peers) {
        liveOwners.Add(peer.m_uid);
      }
    }

    before = "zone_" + ReferenceZone.x + "," + ReferenceZone.y
        + "_before=" + CountReferenceZone();

    // Collect first, destroy after: HandleDestroyedZDO mutates the dictionary.
    List<ZDOID> matches = new();
    Dictionary<string, int> matchedZones = new(StringComparer.Ordinal);
    HashSet<string> tags = new(StringComparer.Ordinal);
    int scanned = 0;
    int c3Probe = 0;
    int c5ZoneProbe = 0;
    int ownershipItem = 0;
    int vehicle = 0;
    int mount = 0;
    int container = 0;
    int untaggedTracked = 0;
    int skippedLiveOwner = 0;
    foreach (ZDO zdo in objects.Values) {
      scanned++;
      if (zdo == null) continue;
      int prefab = zdo.GetPrefab();
      string tag;
      bool isC3 = prefab == C3ProbePrefabHash;
      bool isC5 = !isC3 && prefab == C5ZoneProbePrefabHash;
      bool isItem = !isC3 && !isC5 && prefab == RaspberryPrefabHash;
      bool isVehicle = !isC3 && !isC5 && !isItem &&
          VehiclePrefabHashes.Contains(prefab);
      bool isMount = !isC3 && !isC5 && !isItem && !isVehicle &&
          prefab == MountPrefabHash;
      bool isContainer = !isC3 && !isC5 && !isItem && !isVehicle &&
          !isMount && prefab == ContainerPrefabHash;
      bool isTrackedUntagged = trackedUntagged.Contains(zdo.m_uid) &&
          (isVehicle || isMount || isContainer);
      if (isTrackedUntagged) {
        tag = "tracked-untagged";
      } else if (isC3) {
        tag = zdo.GetString(C3TagHash, string.Empty);
      } else if (isC5) {
        tag = zdo.GetString(C5TagHash, string.Empty);
      } else if (isItem || isVehicle || isMount || isContainer) {
        tag = zdo.GetString(C3TagHash, string.Empty);
        if (string.IsNullOrEmpty(tag)) continue;
      } else {
        continue;
      }
      if (!isTrackedUntagged && !sweepAll &&
          !string.Equals(tag, mode, StringComparison.Ordinal)) continue;
      // A failed container canary may still be owned by the dedicated server
      // because the canonical take never reached its final owner=0 mutation.
      // Its exact run tag makes it safe to sweep; leaving it behind would make
      // the next no-dupe gate contend with stale real inventory.
      if (liveOwners.Contains(zdo.GetOwner()) && !isContainer) {
        skippedLiveOwner++;
        continue;
      }
      if (isC3) c3Probe++;
      else if (isC5) c5ZoneProbe++;
      else if (isItem) ownershipItem++;
      else if (isVehicle) vehicle++;
      else if (isMount) mount++;
      else container++;
      if (isTrackedUntagged) untaggedTracked++;
      if (!string.IsNullOrEmpty(tag)) tags.Add(tag);
      Vector2i sector = zdo.GetSector();
      string zoneKey = sector.x + "," + sector.y;
      matchedZones.TryGetValue(zoneKey, out int zoneCount);
      matchedZones[zoneKey] = zoneCount + 1;
      matches.Add(zdo.m_uid);
    }

    foreach (ZDOID uid in matches) {
      HandleDestroyedZdoMethod.Invoke(ZDOMan.instance, new object[] { uid });
    }
    if (untaggedTracked > 0) {
      lock (UntaggedGate) {
        foreach (HashSet<ZDOID> ids in UntaggedByRun.Values)
          ids.ExceptWith(matches);
        foreach (string emptyRun in UntaggedByRun
                     .Where(pair => pair.Value.Count == 0)
                     .Select(pair => pair.Key)
                     .ToArray())
          UntaggedByRun.Remove(emptyRun);
      }
    }

    // Duplicate terrain compilers: a client that cannot yet see the zone's compiler
    // through the journal creates its own persistent, client-owned one; an abort orphans
    // it. Vanilla's self-heal (TerrainComp "removing it") only resolves when the removing
    // client OWNS the duplicate, so later clients livelock on an uninstantiable ZDO and
    // IsAreaReady never turns true (full36: 612 removals in 25s, spawn deadline missed).
    // Keep the compiler with the highest data revision per zone; destroy the rest.
    Dictionary<string, List<ZDO>> compilersByZone = new(StringComparer.Ordinal);
    foreach (ZDO zdo in objects.Values) {
      if (zdo == null || zdo.GetPrefab() != TerrainCompilerPrefabHash) continue;
      Vector2i sector = zdo.GetSector();
      string zoneKey = sector.x + "," + sector.y;
      if (!compilersByZone.TryGetValue(zoneKey, out List<ZDO> group)) {
        group = new List<ZDO>();
        compilersByZone[zoneKey] = group;
      }
      group.Add(zdo);
    }
    int compilerDuplicatesDestroyed = 0;
    int compilerDuplicatesSkippedLiveOwner = 0;
    List<string> dedupedZones = new();
    foreach (KeyValuePair<string, List<ZDO>> zone in compilersByZone) {
      if (zone.Value.Count < 2) continue;
      ZDO keep = zone.Value[0];
      foreach (ZDO candidate in zone.Value) {
        if (candidate.DataRevision > keep.DataRevision) keep = candidate;
      }
      int destroyedHere = 0;
      foreach (ZDO candidate in zone.Value) {
        if (ReferenceEquals(candidate, keep)) continue;
        if (liveOwners.Contains(candidate.GetOwner())) {
          compilerDuplicatesSkippedLiveOwner++;
          continue;
        }
        HandleDestroyedZdoMethod.Invoke(
            ZDOMan.instance, new object[] { candidate.m_uid });
        destroyedHere++;
      }
      compilerDuplicatesDestroyed += destroyedHere;
      dedupedZones.Add(zone.Key + ":" + zone.Value.Count + "->"
          + (zone.Value.Count - destroyedHere));
    }

    after = "zone_" + ReferenceZone.x + "," + ReferenceZone.y
        + "_after=" + CountReferenceZone();

    StringBuilder detail = new();
    detail.Append("residue_cleanup mode=").Append(sweepAll ? "all" : "run")
        .Append(" scanned=").Append(scanned)
        .Append(" matched=").Append(matches.Count)
        .Append(" destroyed=").Append(matches.Count)
        .Append(" skipped_live_owner=").Append(skippedLiveOwner)
        .Append(" c3_probe=").Append(c3Probe)
        .Append(" c5_zone_probe=").Append(c5ZoneProbe)
        .Append(" ownership_item=").Append(ownershipItem)
        .Append(" vehicle=").Append(vehicle)
        .Append(" mount=").Append(mount)
        .Append(" container=").Append(container)
        .Append(" untagged_tracked=").Append(untaggedTracked)
        .Append(" tags=").Append(tags.Count == 0 ? "none" : string.Join("|", tags))
        .Append(" zones=");
    if (matchedZones.Count == 0) {
      detail.Append("none");
    } else {
      bool first = true;
      foreach (KeyValuePair<string, int> zone in matchedZones) {
        if (!first) detail.Append('|');
        detail.Append(zone.Key).Append(':').Append(zone.Value);
        first = false;
      }
    }
    detail.Append(" terrain_compiler_dupes_destroyed=")
        .Append(compilerDuplicatesDestroyed)
        .Append(" terrain_compiler_dupes_skipped_live_owner=")
        .Append(compilerDuplicatesSkippedLiveOwner)
        .Append(" terrain_compiler_zones=")
        .Append(dedupedZones.Count == 0 ? "none" : string.Join("|", dedupedZones));
    effect = detail.ToString();
    return true;
  }

  static int CountReferenceZone() {
    List<ZDO> sectorObjects = new();
    ZDOMan.instance?.FindSectorObjects(ReferenceZone, 1, 0, sectorObjects);
    return sectorObjects.Count;
  }
}
