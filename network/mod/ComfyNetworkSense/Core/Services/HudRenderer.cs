namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using UnityEngine;

public sealed class HudRenderer {
  GUIStyle _boxStyle;
  GUIStyle _labelStyle;
  GUIStyle _mutedStyle;

  public void Draw(
      bool isVisible,
      HudDetailLevel detailLevel,
      NetworkSenseMode mode,
      bool benchmarkRunning,
      ClientTelemetrySample clientSample,
      ServerPulseSnapshot serverPulse,
      ScoreSnapshot scores) {
    if (!isVisible || clientSample == null) {
      return;
    }

    EnsureStyles();

    List<string> lines = [
        $"Mode: {mode} | HUD: {detailLevel} | Benchmark: {(benchmarkRunning ? "Running" : "Idle")}",
        $"Client: ping {clientSample.RttMs:0} ms | jitter {clientSample.JitterMs:0} ms | fps {clientSample.Fps:0} | p95 {clientSample.FrameTimeP95Ms:0.0} ms",
        $"Area: players {clientSample.NearbyPlayers} | entities {clientSample.NearbyEntities} | pieces {clientSample.NearbyBuildPieces} | zone {clientSample.RegionId}",
        $"Signals: connection {scores?.ConnectionLabel ?? "n/a"} | owner fit {scores?.OwnerFitLabel ?? "n/a"} | pressure {scores?.PressureLabel ?? "n/a"}"
    ];

    if (scores != null && scores.LowImpactRecommended) {
      lines.Add("Hint: Low Impact is recommended for the current local conditions.");
    }

    if (serverPulse != null) {
      lines.Add(
          $"Server pulse: owner {Fallback(serverPulse.OwnerId)} ({serverPulse.OwnerReason}) | pressure {serverPulse.RegionPressureScore:0.00} | observers {serverPulse.RegionObserverCount} | age {GetPulseAgeSeconds(serverPulse):0.0}s");
    } else {
      lines.Add("Server pulse: waiting for server view.");
    }

    if (detailLevel >= HudDetailLevel.Diagnostic) {
      lines.Add(
          $"Rates: in {clientSample.BytesInPerSec:0} B/s, out {clientSample.BytesOutPerSec:0} B/s, pkts {clientSample.PacketsInPerSec:0.0}/{clientSample.PacketsOutPerSec:0.0} per s");
      lines.Add(
          $"Load: cpu {clientSample.CpuBoundEstimate:0.00} | danger {(clientSample.DangerNearby ? "yes" : "no")} | confidence {scores?.ConfidenceLabel ?? "Low"}");
    }

    if (detailLevel >= HudDetailLevel.DeepDebug) {
      lines.Add(
          $"Scores: net {scores?.NetworkQuality:0.00} | frame {scores?.FrameStability:0.00} | cpu {scores?.CpuHeadroom:0.00} | owner {scores?.OwnerScore:0.00}");
      lines.Add(
          $"Pressure math: region {scores?.RegionPressure:0.00} | load penalty {scores?.CurrentLoadPenalty:0.00} | merge risk {scores?.MergeRisk:0.00}");
      lines.Add(
          $"Server detail: bytes {serverPulse?.BytesSentPerSec ?? 0.0f:0} B/s | msgs {serverPulse?.MessagesSentPerSec ?? 0.0f:0.0}/s | stability {serverPulse?.AuthorityStabilitySec ?? 0.0f:0.0}s");
    }

    float scale = Mathf.Max(0.75f, PluginConfig.HudScale.Value);
    int margin = Mathf.Max(0, PluginConfig.HudMarginPixels.Value);
    float width = 720.0f * scale;
    float lineHeight = 22.0f * scale;
    float height = (lines.Count + 1) * lineHeight;

    Rect boxRect = new(margin, margin, width, height);
    GUI.Box(boxRect, "ComfyNetworkSense", _boxStyle);

    Rect lineRect = new(boxRect.x + 12.0f, boxRect.y + 28.0f, boxRect.width - 24.0f, lineHeight);

    for (int i = 0; i < lines.Count; i++) {
      GUI.Label(lineRect, lines[i], i == lines.Count - 1 && serverPulse == null ? _mutedStyle : _labelStyle);
      lineRect.y += lineHeight;
    }
  }

  void EnsureStyles() {
    if (_boxStyle != null) {
      return;
    }

    _boxStyle = new(GUI.skin.box) {
        alignment = TextAnchor.UpperLeft,
        fontSize = 15,
        richText = false,
        padding = new RectOffset(10, 10, 8, 10)
    };

    _labelStyle = new(GUI.skin.label) {
        fontSize = 14,
        richText = false,
        normal = {
            textColor = new Color(0.92f, 0.95f, 0.97f, 1.0f)
        }
    };

    _mutedStyle = new(_labelStyle) {
        normal = {
            textColor = new Color(0.75f, 0.78f, 0.80f, 1.0f)
        }
    };
  }

  static string Fallback(string value) {
    return string.IsNullOrWhiteSpace(value) ? "n/a" : value;
  }

  static double GetPulseAgeSeconds(ServerPulseSnapshot pulse) {
    if (pulse == null || string.IsNullOrWhiteSpace(pulse.TimestampUtc)) {
      return 0.0d;
    }

    if (!DateTime.TryParse(pulse.TimestampUtc, out DateTime timestamp)) {
      return 0.0d;
    }

    return Math.Max(0.0d, (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalSeconds);
  }
}
