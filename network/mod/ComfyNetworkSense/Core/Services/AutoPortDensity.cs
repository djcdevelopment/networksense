namespace ComfyNetworkSense;

using System.Collections.Generic;

using HarmonyLib;

using UnityEngine;

// Server-side "where is the world densest with ZDOs" for the auto-port test harness. Only the server
// holds the full ZDO set (m_objectsByID — the client sees only what is loaded near it), so the
// densest region must be computed here and the coordinate pushed to the joining client.
//
// Bins every ZDO into CellSize-square cells, returns the center of the fullest cell (with that cell's
// mean Y as a ground reference; the client adds its own height offset). The scan is O(all ZDOs) — on
// an Era16-scale world that is millions of objects, so the result is cached for CacheTtlSeconds and
// reused across joins; a join at most pays one scan every few minutes.
public static class AutoPortDensity {
  const float CellSize = 32.0f;
  const float CacheTtlSeconds = 300.0f;

  static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> _objectsByIdRef =
      AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

  static Vector3 _cachedCenter;
  static bool _hasCache;
  static float _cachedAt = -99999.0f;
  static int _cachedCount;

  /// <summary>
  /// Center of the densest CellSize cell, or false if no ZDOs / not the server. <paramref name="count"/>
  /// is that cell's ZDO count (for telemetry). Cached for CacheTtlSeconds.
  /// </summary>
  public static bool TryDensestCenter(float now, out Vector3 center, out int count) {
    if (_hasCache && now - _cachedAt < CacheTtlSeconds) {
      center = _cachedCenter;
      count = _cachedCount;
      return true;
    }

    center = Vector3.zero;
    count = 0;
    if (ZDOMan.instance == null) {
      return false;
    }

    Dictionary<ZDOID, ZDO> objects = _objectsByIdRef(ZDOMan.instance);
    if (objects == null || objects.Count == 0) {
      return false;
    }

    Dictionary<long, int> cellCounts = new();
    Dictionary<long, float> cellYSum = new();
    foreach (ZDO zdo in objects.Values) {
      if (zdo == null) {
        continue;
      }
      Vector3 position = zdo.GetPosition();
      long key = ((long)Mathf.FloorToInt(position.x / CellSize) << 32)
          | (uint)Mathf.FloorToInt(position.z / CellSize);
      cellCounts.TryGetValue(key, out int c);
      cellCounts[key] = c + 1;
      cellYSum.TryGetValue(key, out float ys);
      cellYSum[key] = ys + position.y;
    }

    long bestKey = 0;
    int bestCount = -1;
    foreach (KeyValuePair<long, int> cell in cellCounts) {
      if (cell.Value > bestCount) {
        bestCount = cell.Value;
        bestKey = cell.Key;
      }
    }
    if (bestCount <= 0) {
      return false;
    }

    int cellX = (int)(bestKey >> 32);
    int cellZ = (int)(uint)(bestKey & 0xFFFFFFFFL);
    float meanY = cellYSum[bestKey] / bestCount;
    center = new Vector3(cellX * CellSize + CellSize * 0.5f, meanY, cellZ * CellSize + CellSize * 0.5f);
    count = bestCount;

    _cachedCenter = center;
    _cachedCount = bestCount;
    _cachedAt = now;
    _hasCache = true;
    return true;
  }
}
