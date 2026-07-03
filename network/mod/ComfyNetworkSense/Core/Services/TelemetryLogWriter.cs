namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;

using BepInEx;

public sealed class TelemetryLogWriter : IDisposable {
  readonly string _rootPath;
  readonly object _syncRoot = new();

  public TelemetryLogWriter() {
    _rootPath = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
    Directory.CreateDirectory(_rootPath);
  }

  public void Write(string fileName, IDictionary<string, object> values) {
    if (!PluginConfig.WriteTelemetryLogs.Value) {
      return;
    }

    string filePath = Path.Combine(_rootPath, fileName);
    string line = JsonLineSerializer.Serialize(values) + Environment.NewLine;

    lock (_syncRoot) {
      File.AppendAllText(filePath, line);
    }
  }

  public void Dispose() {
    // no-op for append-only file writes
  }
}
