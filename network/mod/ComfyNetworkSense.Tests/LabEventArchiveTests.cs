namespace ComfyNetworkSense.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

using ComfyQuestLab;

using Xunit;

public sealed class LabEventArchiveTests {
  [Fact]
  public void WritesSelfDescribingPrivacySafeJsonlAndCleanSummary() {
    string dir = TempDir();
    try {
      var archive = NewArchive(dir, csvSeconds: 0);
      Assert.True(archive.TryRecord(
          new LabEvent("combat", "Character.OnDeath()", "kill", "$enemy_greyling",
              "skill Axes", LabUsability.Today),
          "kill|Greyling|Axes"));
      archive.Dispose();

      string path = Directory.GetFiles(dir, "*.jsonl").Single();
      Assert.EndsWith(
          "questlab-events-20260809T123456789Z-r24-startup-extended.jsonl", path,
          StringComparison.OrdinalIgnoreCase);
      string[] lines = File.ReadAllLines(path);
      Assert.Equal(3, lines.Length);

      using JsonDocument header = JsonDocument.Parse(lines[0]);
      Assert.Equal(LabEventArchive.Schema, header.RootElement.GetProperty("schema").GetString());
      Assert.Equal("session", header.RootElement.GetProperty("recordType").GetString());
      Assert.Equal("r24", archive.SessionId.Split('-')[1]);
      Assert.Equal(
          "startup-default",
          header.RootElement.GetProperty("runtimeProfileSemantics").GetString());
      Assert.False(header.RootElement.GetProperty("fields").GetProperty("details").GetBoolean());
      Assert.Equal(1, header.RootElement.GetProperty("segment").GetInt32());

      using JsonDocument evt = JsonDocument.Parse(lines[1]);
      Assert.Equal("event", evt.RootElement.GetProperty("recordType").GetString());
      Assert.Equal("kill", evt.RootElement.GetProperty("creatorEvent").GetString());
      Assert.Equal("combat", evt.RootElement.GetProperty("school").GetString());
      Assert.Equal("$enemy_greyling", evt.RootElement.GetProperty("target").GetString());
      Assert.False(evt.RootElement.TryGetProperty("detail", out _));
      Assert.False(evt.RootElement.TryGetProperty("diagnosticSeam", out _));
      Assert.False(evt.RootElement.TryGetProperty("actionIdentity", out _));

      using JsonDocument end = JsonDocument.Parse(lines[2]);
      Assert.Equal("sessionEnd", end.RootElement.GetProperty("recordType").GetString());
      Assert.Equal(1, end.RootElement.GetProperty("eventCount").GetInt64());
      Assert.Equal(0, end.RootElement.GetProperty("droppedEventCount").GetInt64());
      Assert.Equal("clean-shutdown", end.RootElement.GetProperty("reason").GetString());
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void OptInFieldsAndCsvProjectionUseStableEscapedContract() {
    string dir = TempDir();
    try {
      LabEventArchive archive = NewArchive(
          dir, csvSeconds: 5, details: true, diagnostic: true);
      Assert.True(archive.TryRecord(
          new LabEvent("social", "Sign.SetText(string)", "sign_written",
              "sign, \"welcome\"", "sign text redacted", LabUsability.Today),
          "sign:42"));
      archive.Dispose();

      string jsonPath = Directory.GetFiles(dir, "*.jsonl").Single();
      string eventLine = File.ReadAllLines(jsonPath)[1];
      using JsonDocument evt = JsonDocument.Parse(eventLine);
      Assert.Equal("sign text redacted", evt.RootElement.GetProperty("detail").GetString());
      Assert.Equal("Sign.SetText(string)", evt.RootElement.GetProperty("diagnosticSeam").GetString());
      Assert.Equal("sign:42", evt.RootElement.GetProperty("actionIdentity").GetString());

      string csvPath = Directory.GetFiles(dir, "*.csv").Single();
      string csv = File.ReadAllText(csvPath);
      Assert.StartsWith(LabEventArchive.CsvHeader + "\n", csv);
      Assert.Contains("\"sign, \"\"welcome\"\"\"", csv);
      Assert.Contains(",sign text redacted,today,Sign.SetText(string),sign:42", csv);
      Assert.False(File.Exists(csvPath + ".tmp"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void RotatesSelfDescribingSegmentsAndRetainsBoundedPairs() {
    string dir = TempDir();
    try {
      LabEventArchive archive = NewArchive(
          dir, csvSeconds: 5, details: true, diagnostic: true,
          maxSegmentBytes: 4096, maxSegments: 2);
      string detail = new string('d', 500);
      for (int i = 0; i < 80; i++) {
        Assert.True(archive.TryRecord(
            new LabEvent("inventory", "Humanoid.Pickup(GameObject, bool, bool)",
                "item_picked_up", "Wood-" + i, detail, LabUsability.Today),
            "pickup:" + i));
      }
      archive.Dispose();

      string[] json = Directory.GetFiles(dir, "*.jsonl");
      string[] csv = Directory.GetFiles(dir, "*.csv");
      Assert.Equal(2, json.Length);
      Assert.Equal(2, csv.Length);
      Assert.All(json, path => {
        string first = File.ReadLines(path).First();
        using JsonDocument header = JsonDocument.Parse(first);
        Assert.Equal("session", header.RootElement.GetProperty("recordType").GetString());
        Assert.Equal(archive.SessionId, header.RootElement.GetProperty("sessionId").GetString());
      });
      Assert.Equal(80, archive.AcceptedCount);
      Assert.Equal(80, archive.WrittenCount);
      Assert.Contains("part", Path.GetFileName(archive.CurrentJsonlPath));
      string[] finalLines = File.ReadAllLines(archive.CurrentJsonlPath);
      using JsonDocument finalHeader = JsonDocument.Parse(finalLines[0]);
      using JsonDocument finalSummary = JsonDocument.Parse(finalLines[finalLines.Length - 1]);
      Assert.Equal(
          finalHeader.RootElement.GetProperty("segment").GetInt32(),
          finalSummary.RootElement.GetProperty("segments").GetInt32());
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void SessionNameAddsCollisionSuffixWithoutOverwriting() {
    string dir = TempDir();
    try {
      LabEventArchive first = NewArchive(dir, csvSeconds: 0, sessionOverride: null);
      first.Dispose();
      LabEventArchive second = NewArchive(dir, csvSeconds: 0, sessionOverride: null);
      second.Dispose();

      Assert.Equal("20260809T123456789Z-r24-startup-extended", first.SessionId);
      Assert.Equal("20260809T123456789Z-r24-startup-extended-02", second.SessionId);
      Assert.Equal(2, Directory.GetFiles(dir, "*.jsonl").Length);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void CsvNeutralizesSpreadsheetFormulaPrefixesIncludingLeadingControl() {
    string dir = TempDir();
    try {
      LabEventArchive archive = NewArchive(dir, csvSeconds: 5);
      string[] targets = { "=IMPORTXML(A1)", "+SUM(A1:A2)", "-1+2", "@cmd", "\t=hidden" };
      foreach (string target in targets) {
        Assert.True(archive.TryRecord(
            new LabEvent("inventory", "Pickup", "item_picked_up", target, "", LabUsability.Today)));
      }
      archive.Dispose();

      string[] csv = File.ReadAllLines(Directory.GetFiles(dir, "*.csv").Single());
      Assert.Equal(6, csv.Length);
      Assert.Contains(",'=IMPORTXML(A1),", csv[1]);
      Assert.Contains(",'+SUM(A1:A2),", csv[2]);
      Assert.Contains(",'-1+2,", csv[3]);
      Assert.Contains(",'@cmd,", csv[4]);
      Assert.Contains(",'=hidden,", csv[5]);

      string[] json = File.ReadAllLines(Directory.GetFiles(dir, "*.jsonl").Single());
      using JsonDocument first = JsonDocument.Parse(json[1]);
      Assert.Equal("=IMPORTXML(A1)", first.RootElement.GetProperty("target").GetString());
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void FullQueueDropsWithoutBlockingAndWritesOverflowNotice() {
    string dir = TempDir();
    using var gate = new ManualResetEvent(false);
    try {
      LabEventArchive archive = NewArchive(
          dir, csvSeconds: 0, queueCapacity: 16,
          beforeWriterStart: delegate { gate.WaitOne(); });
      for (int i = 0; i < 16; i++) {
        Assert.True(archive.TryRecord(
            new LabEvent("combat", "Damage", "damage_dealt", "target", "", LabUsability.Today)));
      }
      Assert.False(archive.TryRecord(
          new LabEvent("combat", "Damage", "damage_dealt", "overflow", "", LabUsability.Today)));
      Assert.Equal(1, archive.DroppedCount);

      gate.Set();
      archive.Dispose();
      string[] lines = File.ReadAllLines(Directory.GetFiles(dir, "*.jsonl").Single());
      Assert.Contains(lines, line => line.Contains("\"recordType\":\"archiveNotice\""));
      Assert.Contains(lines, line => line.Contains("\"droppedEventCount\":1"));
      Assert.Equal(16, archive.WrittenCount);
    } finally {
      gate.Set();
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void UnknownRuntimeSignatureUsesStableArchiveVocabularyAndOptInProvenance() {
    string safeDir = TempDir();
    string diagnosticDir = TempDir();
    const string rawSignature = "FutureType.SecretMethod(string)";
    try {
      LabEvent row = new LabEvent(
          "world", rawSignature, rawSignature, "target", "drift witness",
          LabUsability.LabCandidate);

      LabEventArchive safe = NewArchive(safeDir, csvSeconds: 0);
      Assert.True(safe.TryRecord(row));
      safe.Dispose();
      string safeLine = File.ReadAllLines(Directory.GetFiles(safeDir, "*.jsonl").Single())[1];
      using (JsonDocument evt = JsonDocument.Parse(safeLine)) {
        Assert.Equal(
            "unclassified_runtime_event",
            evt.RootElement.GetProperty("creatorEvent").GetString());
        Assert.False(evt.RootElement.TryGetProperty("diagnosticSeam", out _));
      }
      Assert.DoesNotContain(rawSignature, safeLine);

      LabEventArchive diagnostic = NewArchive(
          diagnosticDir, csvSeconds: 0, diagnostic: true);
      Assert.True(diagnostic.TryRecord(row));
      diagnostic.Dispose();
      string diagnosticLine =
          File.ReadAllLines(Directory.GetFiles(diagnosticDir, "*.jsonl").Single())[1];
      using JsonDocument diagnosticEvent = JsonDocument.Parse(diagnosticLine);
      Assert.Equal(
          "unclassified_runtime_event",
          diagnosticEvent.RootElement.GetProperty("creatorEvent").GetString());
      Assert.Equal(
          rawSignature,
          diagnosticEvent.RootElement.GetProperty("diagnosticSeam").GetString());
    } finally {
      Directory.Delete(safeDir, true);
      Directory.Delete(diagnosticDir, true);
    }
  }

  static LabEventArchive NewArchive(
      string dir,
      double csvSeconds,
      bool details = false,
      bool diagnostic = false,
      int maxSegmentBytes = 1024 * 1024,
      int maxSegments = 24,
      string sessionOverride = "20260809T123456789Z-r24-startup-extended",
      int queueCapacity = 4096,
      Action beforeWriterStart = null) {
    return new LabEventArchive(new LabEventArchiveOptions {
      DirectoryPath = dir,
      ReleaseId = "questlab-v0.2.0-20260809-r24",
      RuntimeProfile = "extended",
      IncludeDetails = details,
      IncludeDiagnosticIdentity = diagnostic,
      JsonlFlushSeconds = 1,
      CsvFlushSeconds = csvSeconds,
      MaxSegmentBytes = maxSegmentBytes,
      MaxSegments = maxSegments,
      QueueCapacity = queueCapacity,
      SessionIdOverride = sessionOverride,
      BeforeWriterStart = beforeWriterStart,
      UtcNow = delegate { return new DateTime(2026, 8, 9, 12, 34, 56, 789, DateTimeKind.Utc); },
    });
  }

  static string TempDir() {
    string path = Path.Combine(Path.GetTempPath(), "questlab-event-archive-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }
}
