namespace ComfyNetworkSense.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

  // https is now a supported scheme, so it is no longer the example of an unsupported one.
  [Fact]
  public void UnsupportedScheme_IsRefusedBeforeAnySocketWork() {
    NotSupportedException thrown = Assert.Throws<NotSupportedException>(() =>
        BoundedRawHttp.PostForBody("ftp://127.0.0.1:1/x", "{}", 2000, 2000, 64 * 1024));

    Assert.Contains("must be http or https", thrown.Message);
  }

  // THE TLS TEST THAT MATTERS. M1's gate is certificate-VALIDATING TLS, and the only way to show
  // validation is really on is to present a certificate that must not pass. A self-signed cert the
  // machine does not trust is exactly that. Soften CertificateIsValid to `=> true` and this test
  // fails - which is the whole reason it exists, because `=> true` is what a frustrated debugger
  // reaches for.
  [Fact]
  public void Https_RefusesAnUntrustedCertificate() {
    using X509Certificate2 cert = SelfSignedLoopbackCert();
    using TlsTestServer server = new(cert);

    Uri uri = new(server.Url);
    byte[] head = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + uri.Host + "\r\nConnection: close\r\n\r\n");

    Exception thrown = Record.Exception(() =>
        BoundedRawHttp.SendBounded(uri, head, new byte[0], 3000, 3000, 64 * 1024));

    // The TYPE is load-bearing, and an earlier version of this test got that wrong by asserting only
    // "something threw". Mutation caught it: with TLS never engaged at all (secure hard-coded false)
    // the plaintext socket still breaks against a TLS server, so "something threw" passed while the
    // client had no TLS whatsoever. AuthenticationException means the handshake ran and the peer was
    // REFUSED - the actual claim - and it fails under both "validator softened to true" and "TLS
    // never engaged".
    Assert.IsAssignableFrom<AuthenticationException>(thrown);
  }

  // Proves the refusal above is about TRUST, not about https being broken end to end: the identical
  // server and cert succeed the moment the cert is trusted for this call. Without this, a TLS client
  // that simply never works would pass the test above and look correct.
  [Fact]
  public void Https_CompletesWhenTheCertificateIsTrusted() {
    using X509Certificate2 cert = SelfSignedLoopbackCert();
    using TlsTestServer server = new(cert);

    Uri uri = new(server.Url);
    string body = TrustingTlsRoundTrip(uri, cert);

    Assert.Contains("{\"accept\":true}", body);
  }

  static X509Certificate2 SelfSignedLoopbackCert() {
    using RSA key = RSA.Create(2048);
    CertificateRequest request = new("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    SubjectAlternativeNameBuilder san = new();
    san.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(san.Build());
    X509Certificate2 cert = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Round-trip through PFX so the private key is usable by SslStream on Windows.
    return new X509Certificate2(cert.Export(X509ContentType.Pfx, "p"), "p",
        X509KeyStorageFlags.Exportable);
  }

  // Mirrors SendBounded's TLS handshake but pins THIS cert as trusted, which is what makes it a
  // control rather than a second copy of the code under test.
  static string TrustingTlsRoundTrip(Uri uri, X509Certificate2 expected) {
    using TcpClient client = new();
    client.Connect(uri.Host, uri.Port);
    using NetworkStream network = client.GetStream();
    using SslStream tls = new(network, false,
        (s, cert, chain, errors) => cert != null && cert.GetCertHashString() == expected.GetCertHashString());
    tls.AuthenticateAsClient(uri.Host, null, SslProtocols.Tls12, false);
    byte[] head = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + uri.Host + "\r\nConnection: close\r\n\r\n");
    tls.Write(head, 0, head.Length);
    tls.Flush();
    using MemoryStream memory = new();
    byte[] buffer = new byte[1024];
    int read;
    while ((read = tls.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
    return Encoding.UTF8.GetString(memory.ToArray());
  }

  // A TLS server on loopback that serves one connection with the supplied certificate.
  sealed class TlsTestServer : IDisposable {
    readonly TcpListener _listener;
    readonly Thread _thread;
    readonly CancellationTokenSource _cancel = new();

    public int Port { get; }
    public string Url => "https://127.0.0.1:" + Port + "/";

    public TlsTestServer(X509Certificate2 certificate) {
      _listener = new TcpListener(IPAddress.Loopback, 0);
      _listener.Start();
      Port = ((IPEndPoint) _listener.LocalEndpoint).Port;
      _thread = new Thread(() => {
        try {
          while (!_cancel.IsCancellationRequested) {
            if (!_listener.Pending()) { Thread.Sleep(5); continue; }
            using TcpClient client = _listener.AcceptTcpClient();
            using NetworkStream network = client.GetStream();
            using SslStream tls = new(network, false);
            // Throws when the client rejects our cert - which is the expected path for the
            // untrusted-cert test, so it must not fault anything.
            tls.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
            byte[] request = new byte[4096];
            tls.Read(request, 0, request.Length);
            byte[] response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{\"accept\":true}");
            tls.Write(response, 0, response.Length);
            tls.Flush();
            return;
          }
        } catch (Exception) {
          // A client that refuses the cert aborts the handshake. Normal here, never a failure.
        }
      }) { IsBackground = true };
      _thread.Start();
    }

    public void Dispose() {
      _cancel.Cancel();
      try { _listener.Stop(); } catch (Exception) { }
      _thread.Join(2000);
      _cancel.Dispose();
    }
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
