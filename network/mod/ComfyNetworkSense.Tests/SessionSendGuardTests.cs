namespace ComfyNetworkSense.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class SessionSendGuardTests {
  [Fact]
  public async Task CompletedSendDoesNotAbort() {
    int aborts = 0;

    await SessionSendGuard.RunAsync(
        _ => Task.CompletedTask,
        () => aborts++,
        TimeSpan.FromSeconds(1),
        CancellationToken.None);

    Assert.Equal(0, aborts);
  }

  [Fact]
  public async Task FaultedSendAbortsAndPreservesFailure() {
    int aborts = 0;

    InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
        () => SessionSendGuard.RunAsync(
            _ => Task.FromException(new InvalidOperationException("send failed")),
            () => aborts++,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

    Assert.Equal("send failed", failure.Message);
    Assert.Equal(1, aborts);
  }

  [Fact]
  public async Task SynchronousSendFailureAbortsAndPreservesFailure() {
    int aborts = 0;

    InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
        () => SessionSendGuard.RunAsync(
            _ => throw new InvalidOperationException("send threw"),
            () => aborts++,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

    Assert.Equal("send threw", failure.Message);
    Assert.Equal(1, aborts);
  }

  [Fact]
  public async Task StalledSendTimesOutAndAborts() {
    int aborts = 0;
    TaskCompletionSource<bool> stalled = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(
        () => SessionSendGuard.RunAsync(
            _ => stalled.Task,
            () => aborts++,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None));

    Assert.Contains("25ms", failure.Message);
    Assert.Equal(1, aborts);
  }

  [Fact]
  public async Task ConnectionCancellationDoesNotReportTransportAbort() {
    int aborts = 0;
    using CancellationTokenSource cancellation = new();
    TaskCompletionSource<bool> stalled = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => SessionSendGuard.RunAsync(
            _ => stalled.Task,
            () => aborts++,
            TimeSpan.FromSeconds(1),
            cancellation.Token));

    Assert.Equal(0, aborts);
  }
}
