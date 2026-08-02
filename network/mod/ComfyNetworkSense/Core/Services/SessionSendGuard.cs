namespace ComfyNetworkSense;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Turns a stalled or faulted WebSocket send into an explicit transport abort.
/// A receive loop cannot otherwise observe that its sibling sender died, leaving
/// a half-open session that continues to receive reliable frames but never ACKs.
/// </summary>
public static class SessionSendGuard {
  public static async Task RunAsync(
      Func<CancellationToken, Task> send,
      Action abort,
      TimeSpan timeout,
      CancellationToken connectionToken) {
    if (send == null) throw new ArgumentNullException(nameof(send));
    if (abort == null) throw new ArgumentNullException(nameof(abort));
    if (timeout <= TimeSpan.Zero)
      throw new ArgumentOutOfRangeException(nameof(timeout));

    using CancellationTokenSource sendCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
    Task sendTask;
    try {
      sendTask = send(sendCancellation.Token)
          ?? throw new InvalidOperationException("send delegate returned no task");
    } catch when (!connectionToken.IsCancellationRequested) {
      abort();
      throw;
    }

    Task deadline = Task.Delay(timeout, connectionToken);
    Task completed = await Task.WhenAny(sendTask, deadline).ConfigureAwait(false);
    if (completed == sendTask) {
      try {
        await sendTask.ConfigureAwait(false);
        return;
      } catch when (!connectionToken.IsCancellationRequested) {
        abort();
        throw;
      }
    }

    sendCancellation.Cancel();
    if (connectionToken.IsCancellationRequested)
      throw new OperationCanceledException(connectionToken);

    abort();
    _ = sendTask.ContinueWith(
        task => _ = task.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted |
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
    throw new TimeoutException(
        "WebSocket send did not complete within "
        + timeout.TotalMilliseconds.ToString("0") + "ms");
  }
}
