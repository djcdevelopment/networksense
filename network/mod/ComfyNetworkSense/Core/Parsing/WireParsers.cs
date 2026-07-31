namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Pure wire-format parsing shared by the cutover runners and the telemetry
/// coordinator. No Unity, BepInEx, or game references may appear here: this
/// file compiles into the mod (net48 + System.Memory) and is source-linked
/// into the host-run golden test project (net8.0), which is what keeps
/// hot-path parser changes provable in seconds instead of composition runs.
/// </summary>
public static class WireParsers {
  /// <summary>
  /// Strict chunk-size hex: rejects empty input and any non-hex character
  /// loudly, with checked overflow. Convert.ToInt32's "0x" prefix tolerance
  /// is deliberately not replicated; HTTP chunk sizes are bare hex, and a
  /// malformed size line must fail the request rather than silently
  /// truncate the stream.
  /// </summary>
  public static int ParseHexStrict(ReadOnlySpan<char> hex) {
    if (hex.Length == 0) throw new FormatException("empty_chunk_size");
    int value = 0;
    for (int i = 0; i < hex.Length; i++) {
      char c = hex[i];
      int digit =
          (c >= '0' && c <= '9') ? (c - '0') :
          (c >= 'a' && c <= 'f') ? (c - 'a' + 10) :
          (c >= 'A' && c <= 'F') ? (c - 'A' + 10) : -1;
      if (digit < 0) throw new FormatException("non_hex_chunk_size:" + c);
      value = checked((value * 16) + digit);
    }
    return value;
  }

  /// <summary>
  /// HTTP/1.1 chunked transfer decoding over an already-received body.
  /// A malformed size line or a chunk extending past the received bytes
  /// throws; a body whose final size line was cut off by the bounded reader
  /// returns what was fully decoded, which is the historical contract.
  /// </summary>
  public static string DecodeChunkedBody(string payload) {
    StringBuilder result = new();
    int offset = 0;
    while (offset < payload.Length) {
      int end = payload.IndexOf("\r\n", offset, StringComparison.Ordinal);
      if (end < 0) break;
      int size = ParseHexStrict(payload.AsSpan(offset, end - offset).Trim());
      if (size == 0) break;
      offset = end + 2;
      if (offset + size > payload.Length)
        throw new InvalidDataException("chunked_body_truncated");
      result.Append(payload, offset, size);
      offset += size + 2;
    }
    return result.ToString();
  }

  /// <summary>
  /// Collects every "name":"value" occurrence in document order, honoring
  /// backslash escapes when locating the closing quote and unescaping \" and
  /// \\ in the returned values. The telemetry vocabulary is flat, so matching
  /// is name-based regardless of nesting depth - the same contract the
  /// pre-Span scanners had.
  /// </summary>
  public static string[] ExtractJsonPropertyStringValues(string json, string propertyName) {
    List<string> values = [];
    string marker = "\"" + propertyName + "\":\"";
    int index = 0;
    while (index < json.Length) {
      int start = json.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
      if (start < 0) break;
      start += marker.Length;
      int end = FindClosingQuote(json, start);
      if (end < 0) break;
      values.Add(Unescape(json.Substring(start, end - start)));
      index = end + 1;
    }
    return values.ToArray();
  }

  /// <summary>
  /// First "name":[ ... ] occurrence, string elements only. The closing
  /// bracket search is flat (no nested arrays/objects), matching the
  /// telemetry payload shapes this has always served.
  /// </summary>
  public static string[] ExtractJsonStringArray(string json, string propertyName) {
    string marker = "\"" + propertyName + "\":[";
    int start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0) return [];
    start += marker.Length;
    int end = json.IndexOf(']', start);
    if (end < 0) return [];
    List<string> values = [];
    int index = start;
    while (index < end) {
      if (json[index] == '"') {
        int close = FindClosingQuote(json, index + 1);
        if (close < 0 || close > end) break;
        values.Add(Unescape(json.Substring(index + 1, close - index - 1)));
        index = close + 1;
      } else {
        index++;
      }
    }
    return values.ToArray();
  }

  /// <summary>
  /// Verbatim numeric-token extraction over the "0123456789.-" charset,
  /// byte-for-byte what the pre-Span scanner produced: values pass through
  /// unreformatted, and an exponent suffix stops the scan exactly as it
  /// always did.
  /// </summary>
  public static string ExtractJsonPropertyNumber(string json, string propertyName) {
    string marker = "\"" + propertyName + "\":";
    int start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0) return string.Empty;
    start += marker.Length;
    int end = start;
    while (end < json.Length && "0123456789.-".IndexOf(json[end]) >= 0) end++;
    return json.Substring(start, end - start);
  }

  static int FindClosingQuote(string json, int start) {
    for (int i = start; i < json.Length; i++) {
      if (json[i] == '\\') { i++; continue; }
      if (json[i] == '"') return i;
    }
    return -1;
  }

  static string Unescape(string value) =>
      value.Replace("\\\"", "\"").Replace("\\\\", "\\");
}
