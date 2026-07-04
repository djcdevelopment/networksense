namespace ComfyNetworkSense;

using System;

public static class ScoreCalculator {
  public static ScoreSnapshot Calculate(
      ClientTelemetrySample clientSample, ServerPulseSnapshot serverPulse, NetworkSenseMode mode) {
    float networkQuality =
        Weighted(
            NormalizeInverse(clientSample.RttMs, 50.0f, 250.0f),
            0.50f,
            NormalizeInverse(clientSample.JitterMs, 5.0f, 50.0f),
            0.35f,
            NormalizeInverse(serverPulse?.HeartbeatGapMs ?? clientSample.RttMs, 150.0f, 3000.0f),
            0.15f);

    float frameStability =
        Weighted(
            NormalizeInverse(clientSample.FrameTimeMs, 16.0f, 60.0f),
            0.35f,
            NormalizeInverse(clientSample.FrameTimeP95Ms, 20.0f, 80.0f),
            0.45f,
            NormalizeInverse(Math.Abs(clientSample.FrameTimeP95Ms - clientSample.FrameTimeMs), 2.0f, 30.0f),
            0.20f);

    float cpuHeadroom = NormalizeInverse(clientSample.CpuBoundEstimate, 0.30f, 1.00f);
    float proximity = 1.0f;

    float localDensity =
        Normalize(clientSample.NearbyBuildPieces, 0.0f, 250.0f) * 0.50f
            + Normalize(clientSample.NearbyEntities, 0.0f, 30.0f) * 0.30f
            + Normalize(clientSample.NearbyPlayers, 0.0f, 8.0f) * 0.20f;

    float currentLoadPenalty =
        Clamp01(localDensity * 0.65f + Normalize(clientSample.CorrectionCountRecent, 0.0f, 10.0f) * 0.35f);

    float regionPressure =
        serverPulse != null
            ? Clamp01(serverPulse.RegionPressureScore)
            : Clamp01(
                Normalize(clientSample.NearbyPlayers, 0.0f, 8.0f) * 0.35f
                    + Normalize(clientSample.NearbyEntities, 0.0f, 30.0f) * 0.25f
                    + Normalize(clientSample.NearbyBuildPieces, 0.0f, 250.0f) * 0.40f);

    float ownerScore =
        Clamp01(
            networkQuality * 0.35f
                + frameStability * 0.20f
                + cpuHeadroom * 0.20f
                + proximity * 0.20f
                - currentLoadPenalty * 0.25f);

    float combatReadiness =
        Clamp01(
            networkQuality * 0.40f
                + frameStability * 0.30f
                + cpuHeadroom * 0.20f
                + NormalizeInverse(clientSample.CorrectionCountRecent, 0.0f, 8.0f) * 0.10f);

    float mergeRisk =
        Clamp01(
            Normalize(clientSample.NearbyPlayers + (serverPulse?.RegionPlayerCount ?? 0), 1.0f, 10.0f) * 0.50f
                + regionPressure * 0.35f
                + (mode == NetworkSenseMode.Town ? 0.15f : 0.0f));

    bool lowImpactRecommended =
        mode == NetworkSenseMode.Solo
            && !clientSample.DangerNearby
            && clientSample.NearbyPlayers <= 1
            && regionPressure < 0.60f
            && ownerScore < 0.72f;

    return new() {
        NetworkQuality = networkQuality,
        FrameStability = frameStability,
        CpuHeadroom = cpuHeadroom,
        Proximity = proximity,
        CurrentLoadPenalty = currentLoadPenalty,
        RegionPressure = regionPressure,
        OwnerScore = ownerScore,
        CombatReadiness = combatReadiness,
        MergeRisk = mergeRisk,
        LowImpactRecommended = lowImpactRecommended,
        ConfidenceLabel = GetConfidenceLabel(serverPulse),
        ConnectionLabel = GetLabel(networkQuality, "Stable", "Mixed", "Poor"),
        OwnerFitLabel = GetLabel(ownerScore, "Strong", "Maybe", "Weak"),
        PressureLabel = GetLabelInverse(regionPressure, "Low", "Rising", "High")
    };
  }

  public static float CalculateRegionPressure(int players, int entities, int pieces) {
    return Clamp01(
        Normalize(players, 0.0f, 8.0f) * 0.35f
            + Normalize(entities, 0.0f, 30.0f) * 0.25f
            + Normalize(pieces, 0.0f, 300.0f) * 0.40f);
  }

  static string GetConfidenceLabel(ServerPulseSnapshot serverPulse) {
    if (serverPulse == null || string.IsNullOrWhiteSpace(serverPulse.TimestampUtc)) {
      return "Low";
    }

    if (!DateTime.TryParse(serverPulse.TimestampUtc, out DateTime pulseTime)) {
      return "Low";
    }

    double ageSeconds = (DateTime.UtcNow - pulseTime.ToUniversalTime()).TotalSeconds;

    if (ageSeconds <= 5.0d) {
      return "High";
    }

    if (ageSeconds <= 12.0d) {
      return "Medium";
    }

    return "Low";
  }

  static string GetLabel(float value, string strong, string mixed, string weak) {
    if (value >= 0.70f) {
      return strong;
    }

    if (value >= 0.45f) {
      return mixed;
    }

    return weak;
  }

  static string GetLabelInverse(float value, string low, string rising, string high) {
    if (value <= 0.35f) {
      return low;
    }

    if (value <= 0.65f) {
      return rising;
    }

    return high;
  }

  static float Weighted(float valueA, float weightA, float valueB, float weightB, float valueC, float weightC) {
    float totalWeight = weightA + weightB + weightC;

    if (totalWeight <= 0.0f) {
      return 0.0f;
    }

    return Clamp01((valueA * weightA + valueB * weightB + valueC * weightC) / totalWeight);
  }

  static float Normalize(float value, float min, float max) {
    if (max <= min) {
      return 0.0f;
    }

    return Clamp01((value - min) / (max - min));
  }

  static float NormalizeInverse(float value, float good, float bad) {
    if (bad <= good) {
      return 0.0f;
    }

    return Clamp01(1.0f - ((value - good) / (bad - good)));
  }

  static float Clamp01(float value) {
    if (value < 0.0f) {
      return 0.0f;
    }

    if (value > 1.0f) {
      return 1.0f;
    }

    return value;
  }
}
