namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

public sealed class LumberjacksPriorityMirrorRunner : IDisposable {
  readonly object _lock = new();
  readonly List<Dictionary<string, object>> _pendingObjects = [];

  bool _running;
  string _eventLogUrl = string.Empty;
  string _manifestId = string.Empty;
  string _currentSampleId = string.Empty;
  string _currentRouteStopId = string.Empty;
  int _eventSeq;
  int _sampleEvents;
  int _objectBatchEvents;
  int _objectRecords;
  int _postedOk;
  int _postedFailed;
  int _queuedPosts;
  string _lastError = string.Empty;

  public bool IsRunning => _running;

  public string Start(string eventLogUrl, TelemetryCoordinator coordinator = null) {
    lock (_lock) {
      if (_running) {
        return GetStatus();
      }

      _running = true;
      _eventLogUrl = NormalizeEventLogUrl(eventLogUrl);
      _manifestId = "valheim-live-priority-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
      _currentSampleId = string.Empty;
      _currentRouteStopId = string.Empty;
      _eventSeq = 0;
      _sampleEvents = 0;
      _objectBatchEvents = 0;
      _objectRecords = 0;
      _postedOk = 0;
      _postedFailed = 0;
      _queuedPosts = 0;
      _lastError = string.Empty;
      _pendingObjects.Clear();
    }

    coordinator?.RecordLumberjacksPriorityMirror(BuildStatusRow("start"));
    return $"Lumberjacks priority live mirror started: {_eventLogUrl}, manifest={_manifestId}.";
  }

  public string Stop(TelemetryCoordinator coordinator = null) {
    string manifestId;
    lock (_lock) {
      if (!_running) {
        return "Lumberjacks priority live mirror is not running.";
      }

      manifestId = _manifestId;
    }

    FlushPendingObjectBatch();
    PostCompletionEvent();

    lock (_lock) {
      _running = false;
    }

    coordinator?.RecordLumberjacksPriorityMirror(BuildStatusRow("stop"));
    return $"Lumberjacks priority live mirror stopped: manifest={manifestId}.";
  }

  public void ObservePriorityRow(IDictionary<string, object> values) {
    if (!_running || values == null) {
      return;
    }

    string eventName = ReadString(values, "event");
    if (string.Equals(eventName, "sample", StringComparison.OrdinalIgnoreCase)) {
      FlushPendingObjectBatch();
      MirrorSample(values);
      return;
    }

    if (string.Equals(eventName, "object", StringComparison.OrdinalIgnoreCase)) {
      MirrorObject(values);
      return;
    }

    FlushPendingObjectBatch();
  }

  public string GetStatus() {
    lock (_lock) {
      return
          $"Lumberjacks priority live mirror: running={_running}, manifest={_manifestId}, "
          + $"samples={_sampleEvents}, objectBatches={_objectBatchEvents}, objectRecords={_objectRecords}, "
          + $"postedOk={_postedOk}, postedFailed={_postedFailed}, queued={_queuedPosts}, error={_lastError}";
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_lock) {
      return new Dictionary<string, object> {
          ["event"] = eventType,
          ["manifest_id"] = _manifestId,
          ["eventlog_url"] = _eventLogUrl,
          ["running"] = _running,
          ["sample_events"] = _sampleEvents,
          ["object_batch_events"] = _objectBatchEvents,
          ["object_records"] = _objectRecords,
          ["posted_ok"] = _postedOk,
          ["posted_failed"] = _postedFailed,
          ["queued_posts"] = _queuedPosts,
          ["last_error"] = _lastError,
          ["claim"] = "Live mirror posts priority manifest metadata to Lumberjacks EventLog only; it does not alter Valheim ZDOs, ownership, transforms, or vanilla replication."
      };
    }
  }

  void MirrorSample(IDictionary<string, object> values) {
    Dictionary<string, object> record = Clone(values);
    string sampleId = ReadString(record, "sample_id");
    string routeStopId = ReadString(record, "route_stop_id");

    Dictionary<string, object> payload;
    lock (_lock) {
      _currentSampleId = sampleId;
      _currentRouteStopId = routeStopId;
      _sampleEvents++;
      payload = EventPayload("valheim.priority_manifest.sample", routeStopId, new Dictionary<string, object> {
          ["record"] = record
      });
    }

    QueuePost(payload);
  }

  void MirrorObject(IDictionary<string, object> values) {
    lock (_lock) {
      _pendingObjects.Add(Clone(values));
    }
  }

  void FlushPendingObjectBatch() {
    Dictionary<string, object> payload = null;

    lock (_lock) {
      if (!_running || _pendingObjects.Count <= 0) {
        return;
      }

      List<object> records = [];
      foreach (Dictionary<string, object> row in _pendingObjects) {
        records.Add(Clone(row));
      }

      string routeStopId = _currentRouteStopId;
      Dictionary<string, object> routeStop = new() {
          ["route_stop_id"] = routeStopId,
          ["sample_id"] = _currentSampleId,
          ["object_rows"] = records.Count
      };

      _objectBatchEvents++;
      _objectRecords += records.Count;
      payload = EventPayload("valheim.priority_manifest.objects", routeStopId, new Dictionary<string, object> {
          ["route_stop"] = routeStop,
          ["records"] = records
      });
      _pendingObjects.Clear();
    }

    QueuePost(payload);
  }

  void PostCompletionEvent() {
    Dictionary<string, object> payload;
    lock (_lock) {
      payload = EventPayload("valheim.priority_manifest.complete", "valheim-live-route", new Dictionary<string, object> {
          ["sample_events"] = _sampleEvents,
          ["object_batch_events"] = _objectBatchEvents,
          ["object_records"] = _objectRecords,
          ["posted_ok"] = _postedOk,
          ["posted_failed"] = _postedFailed
      });
    }

    QueuePost(payload);
  }

  Dictionary<string, object> EventPayload(string eventType, string regionId, Dictionary<string, object> payloadExtras) {
    _eventSeq++;
    Dictionary<string, object> payload = new() {
        ["manifest_id"] = _manifestId,
        ["event_seq"] = _eventSeq
    };

    foreach (KeyValuePair<string, object> pair in payloadExtras) {
      payload[pair.Key] = pair.Value;
    }

    return new Dictionary<string, object> {
        ["event_id"] = Guid.NewGuid().ToString(),
        ["event_type"] = eventType,
        ["occurred_at"] = DateTime.UtcNow.ToString("o"),
        ["world_id"] = "valheim-era16",
        ["region_id"] = string.IsNullOrWhiteSpace(regionId) ? "valheim-live" : regionId,
        ["actor_id"] = "comfy-network-sense",
        ["guild_id"] = null,
        ["source_service"] = "ComfyNetworkSense.BepInEx",
        ["schema_version"] = 1,
        ["payload"] = payload
    };
  }

  void QueuePost(Dictionary<string, object> eventPayload) {
    string url;
    lock (_lock) {
      if (!_running && !string.Equals(ReadString(eventPayload, "event_type"), "valheim.priority_manifest.complete", StringComparison.OrdinalIgnoreCase)) {
        return;
      }

      url = _eventLogUrl;
      _queuedPosts++;
    }

    _ = Task.Run(() => PostEvent(url, eventPayload));
  }

  void PostEvent(string eventLogUrl, Dictionary<string, object> eventPayload) {
    try {
      HttpWebRequest request = (HttpWebRequest) WebRequest.Create(eventLogUrl + "/events");
      request.Method = "POST";
      request.ContentType = "application/json";
      request.Timeout = 5000;
      request.ReadWriteTimeout = 5000;

      string body = JsonLineSerializer.Serialize(eventPayload);
      using (Stream requestStream = request.GetRequestStream()) {
        using StreamWriter writer = new(requestStream);
        writer.Write(body);
      }

      using WebResponse response = request.GetResponse();
      _ = response;
      lock (_lock) {
        _postedOk++;
        _queuedPosts = Math.Max(0, _queuedPosts - 1);
      }
    } catch (Exception exception) {
      lock (_lock) {
        _postedFailed++;
        _queuedPosts = Math.Max(0, _queuedPosts - 1);
        _lastError = exception.GetType().Name + ": " + exception.Message;
      }
    }
  }

  static Dictionary<string, object> Clone(IDictionary<string, object> values) {
    Dictionary<string, object> clone = new();
    foreach (KeyValuePair<string, object> pair in values) {
      clone[pair.Key] = pair.Value;
    }

    return clone;
  }

  static string ReadString(IDictionary<string, object> values, string key) =>
      values != null && values.TryGetValue(key, out object value)
          ? Convert.ToString(value) ?? string.Empty
          : string.Empty;

  static string NormalizeEventLogUrl(string value) {
    string url = string.IsNullOrWhiteSpace(value)
        ? PluginConfig.LumberjacksEventLogUrl.Value
        : value.Trim();
    return url.TrimEnd('/');
  }

  public void Dispose() {
    if (_running) {
      Stop();
    }
  }
}
