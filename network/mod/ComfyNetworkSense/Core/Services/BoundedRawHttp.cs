namespace ComfyNetworkSense;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

// Raw-socket HTTP POST that reads the response body, with hard bounds on the read.
//
// Why raw sockets at all: Valheim's server Mono runtime has an empty WebRequest prefix table, so
// WebRequest.Create throws "URI prefix not recognized" (ADR 0003 / valheim-server-mono-http-trap).
//
// WHY THIS FILE HAS NO UnityEngine / ZNet / ZLog REFERENCE, and must not gain one: HandshakeResponderRunner
// calls this from a Harmony prefix on the dedicated server's MAIN THREAD, so every bound below is a
// bound on how long the whole server can freeze during a join. A bound no test can drive is a bound
// nobody should trust, and while this code sat inside a Unity-bound class nothing could drive it —
// loading the assembly meant loading Valheim. Free of Unity, this file links directly into a plain
// test assembly that needs neither Valheim's assemblies nor a running game. Keep it that way: the
// moment this file touches Unity, the bounds go back to being untestable.
//
// Mechanism only. The numbers are policy and belong to the caller, because the caller is the one who
// knows what it costs to block: see HandshakeResponderRunner's consts.
static class BoundedRawHttp {

  // Reads until the server closes the connection (Connection: close). Throws — never returns a partial
  // or sentinel result — on connect timeout, non-2xx status, an over-size body, or a body that takes
  // longer than responseDeadlineMs to arrive. Callers decide what a throw means; the handshake fails
  // open on it today and will fail closed in M1 stage 3.
  //
  // responseDeadlineMs is NOT redundant with the socket's own ReceiveTimeout, and removing it would
  // restore an unbounded stall: ReceiveTimeout applies per Read, not to the loop, so a peer trickling
  // one byte under each timeout resets it forever and holds the caller's thread open indefinitely.
  // The deadline and the byte cap are what make the worst case finite (connect + responseDeadlineMs).
  public static string PostForBody(
      string url, string jsonBody, int connectTimeoutMs, int responseDeadlineMs, int maxResponseBytes) {
    Uri uri = new(url);
    byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
    string head =
        "POST " + uri.PathAndQuery + " HTTP/1.1\r\n"
        + "Host: " + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Content-Type: application/json\r\n"
        + "Accept: application/json\r\n"
        + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Connection: close\r\n\r\n";
    byte[] headBytes = Encoding.ASCII.GetBytes(head);

    string raw = SendBounded(uri, headBytes, bodyBytes, connectTimeoutMs, responseDeadlineMs, maxResponseBytes);

    string statusLine = raw.Length == 0 ? string.Empty : raw.Split('\n')[0].Trim();
    string[] parts = statusLine.Split(' ');
    if (parts.Length < 2 || parts[1].Length == 0 || parts[1][0] != '2') {
      throw new Exception("http status: " + (statusLine.Length == 0 ? "(no response)" : statusLine));
    }
    int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    // Regex on the raw body tolerates a single-chunk transfer-encoding frame around small JSON.
    return split >= 0 ? raw.Substring(split + 4) : raw;
  }

  // The socket and the bounded read - the ONLY part both callers share, and the only part that
  // needed a test. Head-building stays with each caller on purpose: the consumer sends credential
  // headers off PluginConfig and a port-less Host, the handshake sends neither, and unifying that
  // would change bytes on the wire on a benchmarked path (risk 8) to buy nothing.
  //
  // Returns the RAW response - status line, headers, body - because the two callers disagree about
  // what to do with it (the consumer decodes chunked framing; the handshake regex-matches a verdict).
  // Throws on connect timeout, an over-size body, or a body slower than responseDeadlineMs.
  public static string SendBounded(
      Uri uri, byte[] headBytes, byte[] bodyBytes,
      int connectTimeoutMs, int responseDeadlineMs, int maxResponseBytes) {
    if (uri.Scheme != "http") {
      throw new NotSupportedException("endpoint must be http (got '" + uri.Scheme + "')");
    }

    using TcpClient client = new();
    IAsyncResult connect = client.BeginConnect(uri.Host, uri.Port, null, null);
    if (!connect.AsyncWaitHandle.WaitOne(connectTimeoutMs)) {
      throw new TimeoutException("connect timeout to " + uri.Host + ":" + uri.Port);
    }
    client.EndConnect(connect);
    client.SendTimeout = connectTimeoutMs;
    client.ReceiveTimeout = connectTimeoutMs;

    using NetworkStream stream = client.GetStream();
    stream.Write(headBytes, 0, headBytes.Length);
    stream.Write(bodyBytes, 0, bodyBytes.Length);
    stream.Flush();

    using MemoryStream memory = new();
    byte[] buffer = new byte[1024];
    int read;
    Stopwatch elapsed = Stopwatch.StartNew();
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
      memory.Write(buffer, 0, read);
      if (memory.Length > maxResponseBytes) {
        throw new InvalidOperationException(
            "response exceeded " + maxResponseBytes + " bytes");
      }
      if (elapsed.ElapsedMilliseconds > responseDeadlineMs) {
        throw new TimeoutException(
            "response exceeded " + responseDeadlineMs + " ms total");
      }
    }
    // Decoded once, at the end, over the whole buffer - NOT per read. Decoding each chunk as it
    // arrives splits any multi-byte UTF-8 sequence that straddles a read boundary and silently
    // corrupts it. Buffering bytes and decoding once cannot.
    return Encoding.UTF8.GetString(memory.ToArray());
  }
}
