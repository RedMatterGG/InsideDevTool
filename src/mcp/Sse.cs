using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InsideDev.McpExt
{
    // Sse.cs (owner SERVER, signatures frozen): Server-Sent Events framing for Streamable HTTP. Unity-free (also in the
    // stdio proxy). A POST that carries params._meta.progressToken and accepts text/event-stream is answered as a stream:
    // zero or more notifications/progress events, then the JSON-RPC response, then the connection closes. GET on an MCP
    // path with Accept: text/event-stream opens a long-lived stream for notifications/tools/list_changed (keep-alive
    // comment every 15 s). Events carry no id: the server does not offer resumability.
    public static class Sse
    {
        // one event: "event: message\n" + ("id: <id>\n") + one "data: " line per line of data + "\n"
        public static string Frame(string data, string id)
        {
            var sb = new StringBuilder();
            sb.Append("event: message\n");
            if (!string.IsNullOrEmpty(id)) sb.Append("id: ").Append(id.Replace("\r", "").Replace("\n", "")).Append('\n');
            foreach (var line in (data ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) sb.Append("data: ").Append(line).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        // ": <text>\n\n" keep-alive comment
        public static string Comment(string text)
        {
            return ": " + (text ?? "").Replace("\r", " ").Replace("\n", " ") + "\n\n";
        }
    }

    // Writes an SSE response on an HTTP connection. Thread-safe: progress comes from the call's worker thread. A failed
    // write marks the writer Broken and later writes are dropped.
    public sealed class SseWriter : IDisposable
    {
        readonly Stream s;
        readonly object gate = new object();
        volatile bool broken, begun;
        DateTime lastWrite = DateTime.UtcNow;

        public SseWriter(Stream s) { if (s == null) throw new ArgumentNullException("s"); this.s = s; }

        // HTTP/1.1 200 with Content-Type: text/event-stream, Cache-Control: no-store, Connection: close (no Content-Length)
        public void Begin(string sessionId)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\nX-Accel-Buffering: no\r\n");
            if (!string.IsNullOrEmpty(sessionId)) sb.Append("Mcp-Session-Id: ").Append(sessionId).Append("\r\n");
            sb.Append("\r\n");
            lock (gate) { begun = true; Raw(sb.ToString()); }
        }

        // one JSON-RPC message (notification or the final response) as one event
        public void Send(object jsonRpcMessage)
        {
            string data = jsonRpcMessage as string ?? JsonOut.Write(jsonRpcMessage);
            lock (gate) Raw(Sse.Frame(data, null));
        }

        public void KeepAlive() { lock (gate) Raw(Sse.Comment("keep-alive")); }

        public bool Broken { get { return broken; } }
        internal bool Begun { get { return begun; } }
        internal double IdleMs { get { lock (gate) return (DateTime.UtcNow - lastWrite).TotalMilliseconds; } }

        // marks the writer broken so loops that feed it stop; the connection itself is closed by its owner
        internal void Close() { broken = true; }

        public void Dispose() { lock (gate) { if (broken) return; try { s.Flush(); } catch { broken = true; } } }

        void Raw(string text)
        {
            if (broken) return;
            try
            {
                var b = Encoding.UTF8.GetBytes(text);
                s.Write(b, 0, b.Length);
                s.Flush();
                lastWrite = DateTime.UtcNow;
            }
            catch { broken = true; }
        }
    }

    // Reads events from an SSE response (the stdio proxy relays each data payload to stdout). Lines are split on bytes and
    // decoded as UTF-8 per line, so multi-byte characters never break across reads.
    public sealed class SseReader
    {
        readonly Stream s;
        readonly byte[] buf = new byte[16384];
        int pos, len;
        bool eof;

        public SseReader(Stream s) { if (s == null) throw new ArgumentNullException("s"); this.s = s; }

        // false at end of stream; data = the joined data lines of one event (comments are skipped)
        public bool Next(out string eventName, out string data)
        {
            eventName = "message"; data = null;
            var lines = new List<string>();
            string name = null;
            while (true)
            {
                string line = ReadLine();
                if (line == null)
                {
                    if (lines.Count == 0) return false;
                    break;
                }
                if (line.Length == 0)
                {
                    if (lines.Count > 0) break;
                    name = null;
                    continue;
                }
                if (line[0] == ':') continue;
                int c = line.IndexOf(':');
                string field = c < 0 ? line : line.Substring(0, c);
                string value = c < 0 ? "" : line.Substring(c + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
                if (field == "data") lines.Add(value);
                else if (field == "event") name = value;
            }
            if (!string.IsNullOrEmpty(name)) eventName = name;
            data = string.Join("\n", lines.ToArray());
            return true;
        }

        string ReadLine()
        {
            if (eof && pos >= len) return null;
            var ms = new MemoryStream();
            while (true)
            {
                if (pos >= len)
                {
                    if (eof) break;
                    int k;
                    try { k = s.Read(buf, 0, buf.Length); } catch (IOException) { k = 0; } catch (ObjectDisposedException) { k = 0; }
                    if (k <= 0) { eof = true; break; }
                    pos = 0; len = k;
                }
                int start = pos;
                while (pos < len && buf[pos] != (byte)'\n') pos++;
                ms.Write(buf, start, pos - start);
                if (pos < len) { pos++; return Decode(ms); }
            }
            if (ms.Length == 0) return null;
            return Decode(ms);
        }

        static string Decode(MemoryStream ms)
        {
            var b = ms.ToArray();
            int n = b.Length;
            if (n > 0 && b[n - 1] == (byte)'\r') n--;
            return Encoding.UTF8.GetString(b, 0, n);
        }
    }
}
