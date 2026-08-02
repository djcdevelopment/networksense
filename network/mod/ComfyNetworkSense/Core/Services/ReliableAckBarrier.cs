namespace ComfyNetworkSense;

using System;

/// <summary>
/// Prevents a cumulative reliable ACK from crossing a frame that an integration
/// probe deliberately leaves unacknowledged. Later processed frames are banked
/// until the held frame is replayed and explicitly acknowledged.
/// </summary>
public sealed class ReliableAckBarrier {
  readonly object _gate = new();
  long _heldSequence;
  long _deferredThrough;

  public bool TryHold(long serverSequence) {
    if (serverSequence <= 0) return false;
    lock (_gate) {
      if (_heldSequence == 0) {
        _heldSequence = serverSequence;
        _deferredThrough = 0;
        return true;
      }
      return _heldSequence == serverSequence;
    }
  }

  /// <summary>
  /// Plans one cumulative ACK. A zero <paramref name="throughSequence"/> means
  /// the request was accepted but must remain banked behind the active barrier.
  /// <paramref name="releasedSequence"/> names the barrier released by this call.
  /// </summary>
  public bool TryAcknowledge(
      long serverSequence,
      out long throughSequence,
      out long releasedSequence) {
    throughSequence = 0;
    releasedSequence = 0;
    if (serverSequence <= 0) return false;

    lock (_gate) {
      if (_heldSequence == 0) {
        throughSequence = serverSequence;
        return true;
      }
      if (serverSequence < _heldSequence) {
        throughSequence = serverSequence;
        return true;
      }
      if (serverSequence > _heldSequence) {
        _deferredThrough = Math.Max(_deferredThrough, serverSequence);
        return true;
      }

      releasedSequence = _heldSequence;
      throughSequence = Math.Max(serverSequence, _deferredThrough);
      _heldSequence = 0;
      _deferredThrough = 0;
      return true;
    }
  }

  public void Reset() {
    lock (_gate) {
      _heldSequence = 0;
      _deferredThrough = 0;
    }
  }
}
