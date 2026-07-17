namespace ComfyNetworkSense.Tests;

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Xunit;

// Drives BoundedRawHttp against a real loopback socket, because the thing under test IS socket
// behaviour: the bounds only fire against a peer that misbehaves in a way no mock would reproduce.
//
// The load-bearing test here is ResponseDeadline_FiresBeforeSocketTimeout. The handshake calls this
// client from the dedicated server's main thread, so an unbounded read is a whole-server freeze, and
// M1 stage 3 makes that path fail-CLOSED - a hung Gateway would then freeze the server AND reject
// everyone. Every other test in this file guards against over-rejecting; that one guards the reason
// the file exists.
public class BoundedRawHttpTests {

  // How long a misbehaving fake peer keeps misbehaving. Both are deliberately FINITE and far beyond
  // the bounds under test: the bound always wins while it exists, and when it does not, the peer
  // eventually stops so the test fails an assertion instead of hanging CI.
  const int TrickleLifetimeMs = 4000;
  const int FloodCeilingBytes = 256 * 1024;

  [Fact]
  public void HappyPath_ReturnsBodyWithoutHeaders() {
    using TestServer server = new((stream, ct) => {
      byte[] response = Encoding.UTF8.GetBytes(
          "HTTP/1.1 200 OK\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{\"accept\":true}");
      stream.Write(response, 0, response.Length);
      // Returning closes the socket, which is what ends the client's read loop.
    });

    string body = BoundedRawHttp.PostForBody(server.Url, "{}", 2000, 2000, 64 * 1024);

    Assert.Equal("{\"accept\":true}", body);
  }

  // THE ONE THAT MATTERS. A peer trickling one byte under each ReceiveTimeout resets the socket
  // timeout forever, so the socket can never end this read - only the wall-clock deadline can.
  // Delete the ResponseDeadlineMs check from the read loop and this test FAILS on the Assert.Throws,
  // which is exactly the regression it exists to catch.
  [Fact]
  public void ResponseDeadline_FiresBeforeSocketTimeout() {
    using TestServer server = new((stream, ct) => {
      byte[] head = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
      stream.Write(head, 0, head.Length);
      // One byte every 25ms - well under the 2000ms ReceiveTimeout, so that timeout is permanently
      // reset and can never fire. The trickle STOPS after TrickleLifetimeMs on purpose: an endless
      // one would make a deleted deadline HANG this test instead of failing it, and a test that
      // hangs on regression is worse than no test. Stopping lets the client complete normally, so a
      // missing deadline surfaces as a clean "did not throw" rather than a wedged CI run.
      Stopwatch life = Stopwatch.StartNew();
      while (!ct.IsCancellationRequested && life.ElapsedMilliseconds < TrickleLifetimeMs) {
        stream.WriteByte((byte) 'a');
        stream.Flush();
        Thread.Sleep(25);
      }
    });

    Stopwatch elapsed = Stopwatch.StartNew();
    TimeoutException thrown = Assert.Throws<TimeoutException>(() =>
        BoundedRawHttp.PostForBody(server.Url, "{}", 2000, 300, 64 * 1024));
    elapsed.Stop();

    Assert.Contains("ms total", thrown.Message);
    // Proves the DEADLINE ended it, not the 2000ms socket timeout: a pass here at ~2000ms+ would
    // mean the deadline never fired and we were rescued by the socket, which the trickle defeats.
    Assert.True(elapsed.ElapsedMilliseconds < 1500,
        "expected the 300ms deadline to end the read, but it took " + elapsed.ElapsedMilliseconds + "ms");
  }

  [Fact]
  public void SizeCap_FiresOnFloodingPeer() {
    using TestServer server = new((stream, ct) => {
      byte[] head = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
      stream.Write(head, 0, head.Length);
      // Bounded for the same reason the trickle is: an endless flood would make a deleted size cap
      // grow the buffer until it OOMs rather than fail an assertion. FloodCeilingBytes is far above
      // the 4096 cap under test, so the cap always fires first while the bound exists.
      byte[] chunk = new byte[1024];
      int sent = 0;
      while (!ct.IsCancellationRequested && sent < FloodCeilingBytes) {
        stream.Write(chunk, 0, chunk.Length);
        sent += chunk.Length;
      }
    });

    InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
        BoundedRawHttp.PostForBody(server.Url, "{}", 2000, 5000, 4096));

    Assert.Contains("4096", thrown.Message);
    Assert.Contains("bytes", thrown.Message);
  }

  [Fact]
  public void NonSuccessStatus_Throws() {
    using TestServer server = new((stream, ct) => {
      byte[] response = Encoding.UTF8.GetBytes(
          "HTTP/1.1 500 Internal Server Error\r\nConnection: close\r\n\r\n");
      stream.Write(response, 0, response.Length);
    });

    Exception thrown = Assert.Throws<Exception>(() =>
        BoundedRawHttp.PostForBody(server.Url, "{}", 2000, 2000, 64 * 1024));

    Assert.Contains("http status", thrown.Message);
  }

  // The consumer (ZdoAuthoritativeConsumerRunner) calls SendBounded directly rather than
  // PostForBody: it sends GET as well as POST, credential headers off PluginConfig, and a port-less
  // Host, and it decodes chunked framing itself. It shares the read loop and nothing else, so this
  // drives the loop through the consumer's own shape - a GET with a caller-built head, raw response
  // returned unparsed.
  [Fact]
  public void SendBounded_ReturnsRawResponseForACallerBuiltGet() {
    using TestServer server = new((stream, ct) => {
      byte[] response = Encoding.UTF8.GetBytes(
          "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]");
      stream.Write(response, 0, response.Length);
    });

    Uri uri = new(server.Url);
    byte[] head = Encoding.ASCII.GetBytes(
        "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Host
        + "\r\nX-Lumberjacks-Client-Key: test-key\r\nConnection: close\r\n\r\n");

    string raw = BoundedRawHttp.SendBounded(uri, head, new byte[0], 2000, 2000, 64 * 1024);

    // Raw: the consumer wants the status line and headers, because it decides about chunking itself.
    Assert.StartsWith("HTTP/1.1 200 OK", raw);
    Assert.EndsWith("[]", raw);
  }

  // A body arriving in pieces that split a multi-byte UTF-8 sequence must still decode. The loop
  // buffers bytes and decodes once at the end; decoding per read - as the consumer's own loop used
  // to - corrupts any character straddling a read boundary.
  [Fact]
  public void SendBounded_DecodesMultiByteUtf8SplitAcrossReads() {
    byte[] payload = Encoding.UTF8.GetBytes("näïve—ünïcode");
    using TestServer server = new((stream, ct) => {
      byte[] head = Encoding.ASCII.GetBytes(
          "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
      stream.Write(head, 0, head.Length);
      // One byte at a time, so multi-byte sequences are guaranteed to straddle reads.
      foreach (byte b in payload) {
        stream.WriteByte(b);
        stream.Flush();
      }
    });

    Uri uri = new(server.Url);
    byte[] reqHead = Encoding.ASCII.GetBytes(
        "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Host + "\r\nConnection: close\r\n\r\n");

    string raw = BoundedRawHttp.SendBounded(uri, reqHead, new byte[0], 2000, 5000, 64 * 1024);

    Assert.EndsWith("näïve—ünïcode", raw);
  }

  [Fact]
  public void NonHttpScheme_IsRefusedBeforeAnySocketWork() {
    NotSupportedException thrown = Assert.Throws<NotSupportedException>(() =>
        BoundedRawHttp.PostForBody("https://127.0.0.1:1/x", "{}", 2000, 2000, 64 * 1024));

    Assert.Contains("must be http", thrown.Message);
  }

  // Serves exactly one connection, then stops. Reads the request head first so the client's write
  // never blocks, then hands the stream to the behaviour under test.
  sealed class TestServer : IDisposable {
    readonly TcpListener _listener;
    readonly Thread _thread;
    readonly CancellationTokenSource _cancel = new();

    public int Port { get; }
    public string Url => "http://127.0.0.1:" + Port + "/valheim/handshake/peerinfo";

    public TestServer(Action<NetworkStream, CancellationToken> handler) {
      _listener = new TcpListener(IPAddress.Loopback, 0);
      _listener.Start();
      Port = ((IPEndPoint) _listener.LocalEndpoint).Port;

      _thread = new Thread(() => {
        try {
          while (!_cancel.IsCancellationRequested) {
            if (!_listener.Pending()) {
              Thread.Sleep(5);
              continue;
            }
            using TcpClient client = _listener.AcceptTcpClient();
            using NetworkStream stream = client.GetStream();
            byte[] request = new byte[8192];
            stream.Read(request, 0, request.Length);
            handler(stream, _cancel.Token);
            return;
          }
        } catch (Exception) {
          // The client aborting mid-write is the NORMAL outcome of every bound test here: it throws
          // and closes while we are still trickling or flooding. A faulted server thread is expected,
          // not a failure, so it must never be allowed to fail the test.
        }
      }) { IsBackground = true };
      _thread.Start();
    }

    public void Dispose() {
      _cancel.Cancel();
      try {
        _listener.Stop();
      } catch (Exception) {
        // Already stopped.
      }
      _thread.Join(2000);
      _cancel.Dispose();
    }
  }
}
