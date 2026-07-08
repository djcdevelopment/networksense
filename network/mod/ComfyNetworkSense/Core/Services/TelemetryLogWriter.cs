namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BepInEx;

public sealed class TelemetryLogWriter : IDisposable {
  const int MaxQueuedWrites = 8192;

  readonly string _rootPath;
  readonly BlockingCollection<PendingWrite> _writeQueue = new(MaxQueuedWrites);
  readonly Task _writerTask;

  long _droppedRows;
  long _enqueuedRows;
  long _writtenRows;
  long _faultCount;
  volatile bool _disposed;

  public TelemetryLogWriter() {
    _rootPath = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
    Directory.CreateDirectory(_rootPath);
    _writerTask = Task.Factory.StartNew(WriterLoop, TaskCreationOptions.LongRunning);
  }

  public void Write(string fileName, IDictionary<string, object> values) {
    if (_disposed || !PluginConfig.WriteTelemetryLogs.Value) {
      return;
    }

    string filePath = Path.Combine(_rootPath, fileName);
    string line = JsonLineSerializer.Serialize(values) + Environment.NewLine;

    try {
      if (_writeQueue.TryAdd(new PendingWrite(filePath, line))) {
        Interlocked.Increment(ref _enqueuedRows);
      } else {
        Interlocked.Increment(ref _droppedRows);
      }
    } catch (InvalidOperationException) {
      // The game is shutting down and the writer has completed.
      Interlocked.Increment(ref _droppedRows);
    }
  }

  public int QueueDepth {
    get {
      try {
        return _writeQueue.Count;
      } catch (ObjectDisposedException) {
        return 0;
      }
    }
  }

  public long DroppedRows => Interlocked.Read(ref _droppedRows);

  public long EnqueuedRows => Interlocked.Read(ref _enqueuedRows);

  public long WrittenRows => Interlocked.Read(ref _writtenRows);

  public long FaultCount => Interlocked.Read(ref _faultCount);

  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    try {
      _writeQueue.CompleteAdding();
      _writerTask.Wait(2000);
    } catch {
      // Best-effort flush during Unity shutdown.
    }

    _writeQueue.Dispose();
  }

  void WriterLoop() {
    foreach (PendingWrite write in _writeQueue.GetConsumingEnumerable()) {
      try {
        File.AppendAllText(write.FilePath, write.Line);
        Interlocked.Increment(ref _writtenRows);
      } catch (Exception exception) {
        Interlocked.Increment(ref _faultCount);
        ComfyNetworkSense.LogWarning($"Telemetry write failed: {exception.Message}");
      }
    }
  }

  readonly struct PendingWrite {
    public readonly string FilePath;
    public readonly string Line;

    public PendingWrite(string filePath, string line) {
      FilePath = filePath;
      Line = line;
    }
  }
}
