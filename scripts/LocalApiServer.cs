using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using AtkBattery;

namespace AtkBatteryTray {
    // Opt-in localhost-only read-only JSON-RPC 2.0 + SSE server for external consumers
    // (MyDockFinder, Rainmeter, OBS, Stream Deck, etc.).
    // Security invariants:
    // - Loopback 127.0.0.1 only (never 0.0.0.0)
    // - Per-session 256-bit random Bearer token stored in current-user-only ACL session file
    // - Constant-time token comparison; URL query-string tokens rejected (HTTP 400)
    // - Any browser Origin header rejected (HTTP 403)
    // - Strict read-only method whitelist: getSnapshot, listDevices, ping
    // - Never exposes HID writes, DPI writes, file paths, serial numbers, or stack traces
    internal sealed class LocalApiServer : IDisposable {
        const int MaxHeaderBytes = 8192;
        const int MaxBodyBytes = 16384;
        const int MaxConcurrentConnections = 8;
        const int MaxSseClients = 4;
        const int MaxSseQueueItems = 4;
        const int ReadTimeoutMs = 5000;

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);

        sealed class SseClient : IDisposable {
            internal readonly TcpClient Tcp;
            internal readonly NetworkStream Stream;
            readonly object gate = new object();
            readonly Queue<string> queue = new Queue<string>();
            readonly ManualResetEventSlim signal = new ManualResetEventSlim(false);
            bool closed;

            internal SseClient(TcpClient tcp, NetworkStream stream) {
                Tcp = tcp;
                Stream = stream;
            }

            internal void Enqueue(string payload) {
                lock (gate) {
                    if (closed) return;
                    while (queue.Count >= MaxSseQueueItems) queue.Dequeue();
                    queue.Enqueue(payload);
                    signal.Set();
                }
            }

            internal bool TryTake(int waitMs, out string payload) {
                payload = null;
                if (!signal.Wait(waitMs)) return false;
                lock (gate) {
                    if (closed) return false;
                    if (queue.Count > 0) payload = queue.Dequeue();
                    if (queue.Count == 0) signal.Reset();
                    return payload != null;
                }
            }

            internal bool IsClosed { get { lock (gate) return closed; } }

            internal void Close() {
                lock (gate) {
                    if (closed) return;
                    closed = true;
                    queue.Clear();
                    signal.Set();
                }
                try { Stream.Dispose(); } catch (Exception) { }
                try { Tcp.Close(); } catch (Exception) { }
            }

            public void Dispose() {
                Close();
                signal.Dispose();
            }
        }

        readonly object stateGate = new object();
        readonly string sessionFilePath;
        readonly List<SseClient> sseClients = new List<SseClient>();
        TcpListener listener;
        Thread acceptThread;
        string sessionToken;
        bool hasSnapshot;
        string currentSnapshotJson = "{\"schema\":1,\"generatedAtUtc\":\"\",\"appVersion\":\"\",\"deviceCount\":0,\"devices\":[]}";
        string currentDevicesJson = "[]";
        int activeConnections;
        int listeningPort;
        volatile bool running;
        volatile bool disposed;

        internal LocalApiServer(string sessionFilePath) {
            this.sessionFilePath = sessionFilePath;
        }

        internal bool IsRunning { get { return running && !disposed; } }
        internal int Port { get { return listeningPort; } }
        internal string SessionFilePath { get { return sessionFilePath; } }

        internal void Start(int requestedPort) {
            lock (stateGate) {
                if (disposed || running) return;
                int port = (requestedPort >= 1024 && requestedPort <= 65535) ? requestedPort : 0;
                sessionToken = GenerateToken();
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start(16);
                try {
                    listeningPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    WriteProtectedSessionFile(sessionFilePath, listeningPort, sessionToken);
                } catch {
                    try { tcp.Stop(); } catch (Exception) { }
                    listeningPort = 0;
                    sessionToken = null;
                    throw;
                }
                listener = tcp;
                running = true;
                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MyFinderLocalApiAccept" };
                acceptThread.Start();
            }
        }

        internal void UpdateSnapshot(string snapshotJson, string devicesJson) {
            if (String.IsNullOrEmpty(snapshotJson)) return;
            string normalizedDevices = String.IsNullOrEmpty(devicesJson) ? "[]" : devicesJson;
            List<SseClient> targets;
            lock (stateGate) {
                if (hasSnapshot && currentDevicesJson == normalizedDevices) return;
                hasSnapshot = true;
                currentSnapshotJson = snapshotJson;
                currentDevicesJson = normalizedDevices;
                if (!running || sseClients.Count == 0) return;
                targets = new List<SseClient>(sseClients);
            }
            string frame = "event: snapshot\ndata: " + snapshotJson + "\n\n";
            for (int i = 0; i < targets.Count; i++) targets[i].Enqueue(frame);
        }

        internal void Stop() {
            TcpListener tcp;
            List<SseClient> clients;
            lock (stateGate) {
                if (!running) {
                    TryDeleteSessionFile(sessionFilePath);
                    return;
                }
                running = false;
                tcp = listener;
                listener = null;
                listeningPort = 0;
                sessionToken = null;
                clients = new List<SseClient>(sseClients);
                sseClients.Clear();
            }
            try { if (tcp != null) tcp.Stop(); } catch (Exception) { }
            for (int i = 0; i < clients.Count; i++) clients[i].Close();
            TryDeleteSessionFile(sessionFilePath);
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            Stop();
        }

        static string GenerateToken() {
            byte[] bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        internal static bool ConstantTimeEquals(string a, string b) {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        internal static void WriteProtectedSessionFile(string path, int port, string token) {
            string dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(path)) {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) {
                    throw new IOException("Refusing to write session file through reparse point.");
                }
            }
            TryDeleteSessionFile(path);

            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));

            string json = "{"
                + "\"schema\":1,"
                + "\"host\":\"127.0.0.1\","
                + "\"port\":" + port.ToString(CultureInfo.InvariantCulture) + ","
                + "\"rpcUrl\":\"http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/rpc\","
                + "\"eventsUrl\":\"http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/events\","
                + "\"token\":\"" + token + "\","
                + "\"pid\":" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ","
                + "\"startedAtUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\""
                + "}";
            byte[] data = Utf8NoBom.GetBytes(json);

            // Create atomically with restricted ACL BEFORE writing any secret bytes.
            using (var fs = File.Create(path, 4096, FileOptions.None, security)) {
                fs.Write(data, 0, data.Length);
                fs.Flush();
            }
        }

        static void TryDeleteSessionFile(string path) {
            try { if (!String.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void AcceptLoop() {
            while (running) {
                TcpClient client = null;
                try {
                    var tcp = listener;
                    if (tcp == null) break;
                    client = tcp.AcceptTcpClient();
                } catch (SocketException) {
                    if (!running) break;
                    continue;
                } catch (ObjectDisposedException) {
                    break;
                } catch (InvalidOperationException) {
                    break;
                }

                if (Interlocked.Increment(ref activeConnections) > MaxConcurrentConnections) {
                    Interlocked.Decrement(ref activeConnections);
                    try { client.Close(); } catch (Exception) { }
                    continue;
                }

                var captured = client;
                ThreadPool.QueueUserWorkItem(delegate { HandleClient(captured); });
            }
        }

        void HandleClient(TcpClient client) {
            bool keepOpenForSse = false;
            try {
                client.NoDelay = true;
                var stream = client.GetStream();
                stream.ReadTimeout = ReadTimeoutMs;
                stream.WriteTimeout = ReadTimeoutMs;

                string method, target;
                Dictionary<string, string> headers;
                byte[] body;
                string parseError;
                if (!ReadHttpRequest(stream, out method, out target, out headers, out body, out parseError)) {
                    WriteHttpResponse(stream, parseError == "body_too_large" ? 413 : 400, "application/json; charset=utf-8", "{\"error\":\"" + (parseError ?? "bad_request") + "\"}");
                    return;
                }

                // Reject missing or non-loopback Host header (DNS rebinding defense).
                string hostHeader;
                int activePort;
                lock (stateGate) activePort = listeningPort;
                if (!headers.TryGetValue("host", out hostHeader) || !IsAllowedHost(hostHeader, activePort)) {
                    WriteHttpResponse(stream, 400, "application/json; charset=utf-8", "{\"error\":\"invalid_host\"}");
                    return;
                }

                // Reject any cross-origin browser request immediately.
                if (headers.ContainsKey("origin")) {
                    WriteHttpResponse(stream, 403, "application/json; charset=utf-8", "{\"error\":\"origin_forbidden\"}");
                    return;
                }

                // Reject query-string tokens to prevent leakage in logs/history.
                if (target.IndexOf('?') >= 0) {
                    string query = target.Substring(target.IndexOf('?') + 1).ToLowerInvariant();
                    if (query.Contains("token=") || query.Contains("auth=") || query.Contains("key=")) {
                        WriteHttpResponse(stream, 400, "application/json; charset=utf-8", "{\"error\":\"query_token_forbidden\"}");
                        return;
                    }
                }

                string pathOnly = target.IndexOf('?') >= 0 ? target.Substring(0, target.IndexOf('?')) : target;
                string expectedToken;
                lock (stateGate) expectedToken = sessionToken;

                string authHeader;
                if (!headers.TryGetValue("authorization", out authHeader) ||
                    !authHeader.StartsWith("Bearer ", StringComparison.Ordinal) ||
                    !ConstantTimeEquals(authHeader.Substring(7).Trim(), expectedToken)) {
                    WriteHttpResponse(stream, 401, "application/json; charset=utf-8", "{\"error\":\"unauthorized\"}");
                    return;
                }

                if (method == "POST" && pathOnly == "/rpc") {
                    string reply = HandleJsonRpc(body);
                    WriteHttpResponse(stream, 200, "application/json; charset=utf-8", reply);
                    return;
                }

                if (method == "GET" && pathOnly == "/events") {
                    keepOpenForSse = RunSseLoop(client, stream);
                    return;
                }

                WriteHttpResponse(stream, 404, "application/json; charset=utf-8", "{\"error\":\"not_found\"}");
            } catch (Exception) {
                // Never leak internal exceptions or stack traces over the socket.
            } finally {
                if (!keepOpenForSse) {
                    try { client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                    try {
                        if (client.Available > 0) {
                            var ns = client.GetStream();
                            ns.ReadTimeout = 50;
                            byte[] drain = new byte[512];
                            ns.Read(drain, 0, Math.Min(drain.Length, client.Available));
                        }
                    } catch (Exception) { }
                    try { client.Close(); } catch (Exception) { }
                }
                Interlocked.Decrement(ref activeConnections);
            }
        }

        bool RunSseLoop(TcpClient tcp, NetworkStream stream) {
            var sse = new SseClient(tcp, stream);
            string initial = null;
            bool accepted = false;
            lock (stateGate) {
                if (running && sseClients.Count < MaxSseClients) {
                    sseClients.Add(sse);
                    initial = currentSnapshotJson;
                    accepted = true;
                }
            }
            if (!accepted) {
                WriteHttpResponse(stream, 503, "application/json; charset=utf-8", "{\"error\":\"too_many_subscribers\"}");
                sse.Dispose();
                return false;
            }
            try {
                byte[] headerBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream; charset=utf-8\r\n" +
                    "Cache-Control: no-cache, no-store\r\n" +
                    "Connection: keep-alive\r\n" +
                    "X-Content-Type-Options: nosniff\r\n\r\n");
                stream.Write(headerBytes, 0, headerBytes.Length);
                byte[] first = Utf8NoBom.GetBytes("event: snapshot\ndata: " + initial + "\n\n");
                stream.Write(first, 0, first.Length);
                stream.Flush();

                while (running && !sse.IsClosed) {
                    string next;
                    if (sse.TryTake(15000, out next)) {
                        byte[] bytes = Utf8NoBom.GetBytes(next);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    } else if (running && !sse.IsClosed) {
                        byte[] ping = Encoding.ASCII.GetBytes(": keepalive\n\n");
                        stream.Write(ping, 0, ping.Length);
                        stream.Flush();
                    }
                }
            } catch (Exception) {
            } finally {
                lock (stateGate) sseClients.Remove(sse);
                sse.Dispose();
            }
            return true;
        }

        string HandleJsonRpc(byte[] body) {
            if (body == null || body.Length == 0) return JsonRpcError("null", -32700, "Parse error");
            object parsed;
            try {
                parsed = PackJson.Parse(body);
            } catch (Exception) {
                return JsonRpcError("null", -32700, "Parse error");
            }
            var obj = parsed as Dictionary<string, object>;
            if (obj == null) return JsonRpcError("null", -32600, "Invalid Request");

            string idRaw = FormatJsonRpcId(obj);
            object verObj, methodObj;
            if (!obj.TryGetValue("jsonrpc", out verObj) || !(verObj is string) || (string)verObj != "2.0" ||
                !obj.TryGetValue("method", out methodObj) || !(methodObj is string)) {
                return JsonRpcError(idRaw, -32600, "Invalid Request");
            }

            string method = (string)methodObj;
            string snap, devs;
            lock (stateGate) {
                snap = currentSnapshotJson;
                devs = currentDevicesJson;
            }

            if (method == "getSnapshot") {
                return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + snap + "}";
            }
            if (method == "listDevices") {
                return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"devices\":" + devs + "}}";
            }
            if (method == "ping") {
                return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"ok\":true,\"schema\":1,\"appVersion\":\"" + EscapeJson(AppRuntime.Version) + "\"}}";
            }
            return JsonRpcError(idRaw, -32601, "Method not found");
        }

        static string FormatJsonRpcId(Dictionary<string, object> obj) {
            object idObj;
            if (!obj.TryGetValue("id", out idObj) || idObj == null) return "null";
            var s = idObj as string;
            if (s != null) return "\"" + EscapeJson(s.Length > 64 ? s.Substring(0, 64) : s) + "\"";
            var num = idObj as PackJson.Number;
            if (num != null && !String.IsNullOrEmpty(num.Text)) return num.Text;
            return "null";
        }

        static string JsonRpcError(string idRaw, int code, string message) {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"error\":{\"code\":" + code.ToString(CultureInfo.InvariantCulture) + ",\"message\":\"" + EscapeJson(message) + "\"}}";
        }

        internal static bool IsAllowedHost(string host, int port) {
            if (String.IsNullOrEmpty(host)) return false;
            string h = host.Trim().ToLowerInvariant();
            if (h == "127.0.0.1" || h == "localhost") return true;
            if (port > 0) {
                string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
                if (h == "127.0.0.1" + suffix || h == "localhost" + suffix) return true;
            }
            return false;
        }

        static bool IsValidHeaderName(string name) {
            if (String.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < name.Length; i++) {
                char c = name[i];
                if (c <= 0x20 || c >= 0x7F || c == ':') return false;
            }
            return true;
        }

        static bool ReadHttpRequest(NetworkStream stream, out string method, out string target, out Dictionary<string, string> headers, out byte[] body, out string error) {
            method = null; target = null; headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); body = new byte[0]; error = null;
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var headerBuf = new MemoryStream()) {
                int matched = 0;
                while (headerBuf.Length < MaxHeaderBytes) {
                    int b = stream.ReadByte();
                    if (b < 0) { error = "eof"; return false; }
                    headerBuf.WriteByte((byte)b);
                    if ((matched == 0 || matched == 2) && b == '\r') matched++;
                    else if ((matched == 1 || matched == 3) && b == '\n') matched++;
                    else matched = b == '\r' ? 1 : 0;
                    if (matched == 4) break;
                }
                if (matched != 4) { error = "header_too_large"; return false; }
                string headerText = Encoding.ASCII.GetString(headerBuf.ToArray(), 0, (int)headerBuf.Length - 4);
                string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) { error = "empty_request"; return false; }
                string[] req = lines[0].Split(' ');
                if (req.Length != 3 || !req[1].StartsWith("/", StringComparison.Ordinal)) { error = "bad_request_line"; return false; }
                method = req[0];
                target = req[1];
                for (int i = 1; i < lines.Length; i++) {
                    string line = lines[i];
                    if (line.Length == 0) { error = "bad_header"; return false; }
                    if (line[0] == ' ' || line[0] == '\t') { error = "obs_fold_forbidden"; return false; }
                    int colon = line.IndexOf(':');
                    if (colon <= 0) { error = "bad_header"; return false; }
                    string k = line.Substring(0, colon);
                    if (!IsValidHeaderName(k)) { error = "bad_header"; return false; }
                    string v = line.Substring(colon + 1).Trim();
                    if (headers.ContainsKey(k)) duplicates.Add(k);
                    headers[k] = v;
                }
            }

            if (headers.ContainsKey("transfer-encoding")) { error = "transfer_encoding_forbidden"; return false; }
            if (duplicates.Contains("host")) { error = "duplicate_host"; return false; }
            if (duplicates.Contains("content-length")) { error = "duplicate_content_length"; return false; }

            int contentLength = 0;
            string cl;
            if (headers.TryGetValue("content-length", out cl)) {
                if (!Int32.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0) {
                    error = "invalid_content_length";
                    return false;
                }
                if (contentLength > MaxBodyBytes) {
                    error = "body_too_large";
                    return false;
                }
            }

            if (contentLength > 0) {
                body = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength) {
                    int read = stream.Read(body, offset, contentLength - offset);
                    if (read <= 0) { error = "truncated_body"; return false; }
                    offset += read;
                }
            }
            return true;
        }

        static void WriteHttpResponse(NetworkStream stream, int statusCode, string contentType, string bodyText) {
            string reason = statusCode == 200 ? "OK" : statusCode == 400 ? "Bad Request" : statusCode == 401 ? "Unauthorized" : statusCode == 403 ? "Forbidden" : statusCode == 404 ? "Not Found" : statusCode == 413 ? "Payload Too Large" : "Service Unavailable";
            byte[] body = Utf8NoBom.GetBytes(bodyText ?? "");
            string head = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "X-Content-Type-Options: nosniff\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        internal static string EscapeJson(string value) {
            if (String.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    internal sealed class LocalApiController : IDisposable {
        readonly TrayContext owner;
        readonly string sessionFilePath;
        LocalApiServer server;
        int startedPort;
        string lastError;
        bool disposed;

        internal LocalApiController(TrayContext owner, string dataDirectory = null) {
            this.owner = owner;
            sessionFilePath = Path.Combine(dataDirectory ?? DeviceSettings.DataDirectory, "api-session.json");
        }

        internal bool IsRunning { get { return !disposed && server != null && server.IsRunning; } }
        internal int ListeningPort { get { return IsRunning ? server.Port : 0; } }
        internal string SessionFilePath { get { return sessionFilePath; } }
        internal string LastError { get { return lastError; } }

        internal void ApplySettings() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.LocalApi;
            if (cfg == null || !cfg.Enabled) {
                StopServer();
                return;
            }
            if (server != null && server.IsRunning && cfg.Port == startedPort) {
                NotifyDataChanged();
                return;
            }
            StopServer();
            try {
                var next = new LocalApiServer(sessionFilePath);
                string snap, devs;
                BuildSanitizedSnapshot(out snap, out devs);
                next.UpdateSnapshot(snap, devs);
                next.Start(cfg.Port);
                server = next;
                startedPort = cfg.Port;
                lastError = null;
            } catch (Exception ex) {
                lastError = ex.Message;
            }
        }

        internal void NotifyDataChanged() {
            if (disposed || server == null || !server.IsRunning) return;
            string snap, devs;
            BuildSanitizedSnapshot(out snap, out devs);
            server.UpdateSnapshot(snap, devs);
        }

        void StopServer() {
            var s = server;
            server = null;
            startedPort = 0;
            if (s != null) s.Dispose();
        }

        void BuildSanitizedSnapshot(out string snapshotJson, out string devicesJson) {
            var pref = DeviceSettings.Current;
            var layout = WirelessLayout.Build(owner, 0, 100000, false);
            var devSb = new StringBuilder("[");
            int count = 0;
            foreach (var card in layout.All) {
                if (card == null || card.Id == "empty") continue;
                if (count > 0) devSb.Append(',');
                count++;
                var bv = card.Battery;
                bool hasBattery = !card.AudioOnly;
                bool low = hasBattery && ThemePalette.IsLow(bv, pref.WarningThreshold);
                string transport = card.AudioOnly ? "audio" : card.NativeMouse || card.NativeHeadset ? "2.4 GHz" : (card.Device != null && !String.IsNullOrEmpty(card.Device.Transport) ? card.Device.Transport : "wireless");
                string connState = card.AudioOnly ? "connected" : card.Device != null && !String.IsNullOrEmpty(card.Device.ConnectionState) ? card.Device.ConnectionState : (bv != null && bv.Fresh ? "connected" : "unknown");
                bool online = card.AudioOnly ? true : card.Device != null ? card.Device.Online : (bv != null && bv.Fresh);

                devSb.Append("{");
                devSb.Append("\"id\":\"").Append(LocalApiServer.EscapeJson(card.Id)).Append("\",");
                devSb.Append("\"name\":\"").Append(LocalApiServer.EscapeJson(card.Name)).Append("\",");
                devSb.Append("\"kind\":\"").Append(LocalApiServer.EscapeJson(card.Kind ?? "device")).Append("\",");
                devSb.Append("\"transport\":\"").Append(LocalApiServer.EscapeJson(transport)).Append("\",");
                devSb.Append("\"capability\":\"").Append(hasBattery ? "battery" : "none").Append("\",");
                devSb.Append("\"percent\":").Append(hasBattery && bv != null && bv.Percent.HasValue ? bv.Percent.Value.ToString(CultureInfo.InvariantCulture) : "null").Append(",");
                devSb.Append("\"fresh\":").Append(hasBattery && bv != null && bv.Fresh ? "true" : "false").Append(",");
                devSb.Append("\"charging\":").Append(hasBattery && bv != null && bv.Charging ? "true" : "false").Append(",");
                devSb.Append("\"approximate\":").Append(hasBattery && bv != null && bv.Approximate ? "true" : "false").Append(",");
                devSb.Append("\"low\":").Append(low ? "true" : "false").Append(",");
                devSb.Append("\"online\":").Append(online ? "true" : "false").Append(",");
                devSb.Append("\"connectionState\":\"").Append(LocalApiServer.EscapeJson(connState)).Append("\",");
                devSb.Append("\"confirmedAtUtc\":").Append(hasBattery && bv != null && bv.ConfirmedAt != DateTime.MinValue ? "\"" + bv.ConfirmedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\"" : "null");
                devSb.Append("}");
            }
            devSb.Append("]");
            devicesJson = devSb.ToString();
            snapshotJson = "{"
                + "\"schema\":1,"
                + "\"generatedAtUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\","
                + "\"appVersion\":\"" + LocalApiServer.EscapeJson(AppRuntime.Version) + "\","
                + "\"deviceCount\":" + count.ToString(CultureInfo.InvariantCulture) + ","
                + "\"devices\":" + devicesJson
                + "}";
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            StopServer();
        }
    }
}
