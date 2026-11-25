using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class LocalApiTests {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks, failures;
    static string output;

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static void Run(string name, Action action) {
        try {
            action();
            Console.WriteLine("PASS " + name);
        } catch (Exception e) {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": " + (e.InnerException ?? e).Message);
        }
    }

    static object Field(object target, string name) {
        return target.GetType().GetField(name, Private).GetValue(target);
    }

    static string SendRawHttp(int port, string rawRequest, out int statusCode) {
        statusCode = 0;
        using (var client = new TcpClient()) {
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 4000;
            client.SendTimeout = 4000;
            using (var stream = client.GetStream()) {
                byte[] req = Encoding.UTF8.GetBytes(rawRequest);
                stream.Write(req, 0, req.Length);
                stream.Flush();
                using (var ms = new MemoryStream()) {
                    byte[] buf = new byte[4096];
                    int read;
                    while ((read = stream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, read);
                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    int sp1 = text.IndexOf(' ');
                    int sp2 = sp1 > 0 ? text.IndexOf(' ', sp1 + 1) : -1;
                    if (sp1 > 0 && sp2 > sp1) Int32.TryParse(text.Substring(sp1 + 1, sp2 - sp1 - 1), out statusCode);
                    int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return split >= 0 ? text.Substring(split + 4) : text;
                }
            }
        }
    }

    static string PostRpc(int port, string token, string body, out int statusCode, string extraHeaders = "") {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string req = "POST /rpc HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\n"
            + (token != null ? "Authorization: Bearer " + token + "\r\n" : "")
            + extraHeaders
            + "Connection: close\r\n\r\n" + body;
        return SendRawHttp(port, req, out statusCode);
    }

    static void LifecycleAuthAclRpcAndSse() {
        string dataDir = Path.Combine(output, "api-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        string settingsPath = Path.Combine(dataDir, "settings.json");

        var pref = new AudioBindings { ThemeStyle = "graphite", RememberReadings = false };
        Check(pref.LocalApi != null && !pref.LocalApi.Enabled && pref.LocalApi.Port == 0, "LocalApi defaults disabled with ephemeral port");
        DeviceSettings.UseTransientConfiguration(pref);

        using (var owner = new TrayContext(false,
            delegate(bool b) { return new MouseSnapshot { Battery = new BatteryReading { Percent = 88, TimestampUtc = DateTime.UtcNow } }; },
            delegate(int s) { return null; },
            null, null, false, null, null, null, delegate { return false; },
            null, null, null, null,
            delegate(AudioBindings s) { DeviceSettings.SaveBindings(settingsPath, s); DeviceSettings.UseTransientConfiguration(s); },
            false, null, dataDir)) {
            ((System.Windows.Forms.Timer)Field(owner, "refresh")).Stop();
            owner.Current = new BatteryReading { Percent = 88, TimestampUtc = DateTime.UtcNow };
            owner.NativeMousePresent = true;
            owner.GenericReady = true;
            owner.WirelessDevices = new[] {
                new WirelessDevice { Id = "dev-1", Name = "ROG Delta II", Kind = "headset", Transport = "2.4 GHz", Percent = 52, Fresh = true, Online = true, ConnectionState = "connected", ConfirmedAtUtc = DateTime.UtcNow.ToString("o") }
            };

            Check(!owner.LocalApiRunning, "disabled by default -> not running");
            string sessionPath = owner.LocalApiSessionPath;
            Check(!File.Exists(sessionPath), "no session file while disabled");

            // Enable API
            var next = DeviceSettings.Current;
            next.LocalApi.Enabled = true;
            next.LocalApi.Port = 0;
            owner.SavePreferences(next);
            owner.ConfigurationChanged(false);

            Check(owner.LocalApiRunning && owner.LocalApiPort >= 1024, "API starts on loopback ephemeral port");
            Check(File.Exists(sessionPath), "api-session.json created");

            // Verify ACL is restricted strictly to current Windows user (no Users / Everyone)
            var acl = File.GetAccessControl(sessionPath);
            Check(acl.AreAccessRulesProtected, "session file disables inherited ACLs");
            var currentSid = WindowsIdentity.GetCurrent().User;
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            Check(rules.Count == 1, "session file has exactly one ACL rule");
            foreach (FileSystemAccessRule rule in rules) {
                Check(rule.IdentityReference.Equals(currentSid), "session file ACL grants access only to current user");
            }

            var sessionObj = (Dictionary<string, object>)PackJson.Parse(File.ReadAllBytes(sessionPath));
            string token = (string)sessionObj["token"];
            int port = owner.LocalApiPort;
            Check(!String.IsNullOrEmpty(token) && token.Length == 64, "token is 256-bit hex");

            // Security checks
            int status;
            PostRpc(port, null, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", out status);
            Check(status == 401, "missing Bearer token returns 401");

            PostRpc(port, "0000000000000000000000000000000000000000000000000000000000000000", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", out status);
            Check(status == 401, "wrong Bearer token returns 401");

            PostRpc(port, token, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", out status, "Origin: https://evil.example\r\n");
            Check(status == 403, "Origin header is rejected with 403");

            SendRawHttp(port, "POST /rpc?token=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + token + "\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", out status);
            Check(status == 400, "query-string token is rejected with 400");

            SendRawHttp(port, "POST /rpc HTTP/1.1\r\nHost: evil.example\r\nAuthorization: Bearer " + token + "\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", out status);
            Check(status == 400, "non-loopback Host header is rejected with 400");

            SendRawHttp(port, "POST /rpc HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nTransfer-Encoding: chunked\r\nAuthorization: Bearer " + token + "\r\nConnection: close\r\n\r\n0\r\n\r\n", out status);
            Check(status == 400, "Transfer-Encoding header is rejected with 400");

            SendRawHttp(port, "POST http://127.0.0.1:" + port + "/rpc HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + token + "\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", out status);
            Check(status == 400, "absolute-form proxy request target is rejected with 400");

            // Valid RPC: ping / getSnapshot / listDevices
            string pingReply = PostRpc(port, token, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", out status);
            Check(status == 200 && pingReply.Contains("\"ok\":true"), "ping succeeds");

            string snapReply = PostRpc(port, token, "{\"jsonrpc\":\"2.0\",\"id\":\"s1\",\"method\":\"getSnapshot\"}", out status);
            Check(status == 200 && snapReply.Contains("\"ROG Delta II\"") && snapReply.Contains("\"percent\":52") && !snapReply.Contains(dataDir), "getSnapshot returns live sanitized devices without file paths");

            string listReply = PostRpc(port, token, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"listDevices\"}", out status);
            Check(status == 200 && listReply.Contains("\"devices\":[") && listReply.Contains("\"percent\":88"), "listDevices returns native + wireless devices");

            // Write/unknown method blocked
            string blockedReply = PostRpc(port, token, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"setDpi\"}", out status);
            Check(status == 200 && blockedReply.Contains("-32601"), "non-whitelisted/write methods rejected with -32601");

            // Live SSE stream test
            using (var sseClient = new TcpClient()) {
                sseClient.Connect(IPAddress.Loopback, port);
                sseClient.ReceiveTimeout = 4000;
                var stream = sseClient.GetStream();
                byte[] req = Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + token + "\r\n\r\n");
                stream.Write(req, 0, req.Length);
                stream.Flush();
                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    string statusLine = reader.ReadLine();
                    Check(statusLine != null && statusLine.Contains("200"), "SSE returns 200 OK");
                    string line;
                    bool gotInitial = false;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"percent\":52")) { gotInitial = true; break; }
                    }
                    Check(gotInitial, "SSE immediately pushes initial snapshot");

                    // Push updated device reading
                    owner.WirelessDevices[0].Percent = 47;
                    owner.ConfigurationChanged(false);
                    bool gotUpdate = false;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"percent\":47")) { gotUpdate = true; break; }
                    }
                    Check(gotUpdate, "SSE pushes live snapshot update on battery change");
                }
            }

            // Disable -> stops server and revokes session file
            next = DeviceSettings.Current;
            next.LocalApi.Enabled = false;
            owner.SavePreferences(next);
            owner.ConfigurationChanged(false);
            Check(!owner.LocalApiRunning, "disabling stops API server");
            Check(!File.Exists(sessionPath), "disabling deletes api-session.json");
            owner.ExitThread();
        }
    }

    [STAThread]
    static int Main(string[] args) {
        output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "myfinder-api-tests");
        Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        NativeDisplay.EnableDpiAwareness();

        Run("LocalApiServer lifecycle, ACL, security gates, JSON-RPC 2.0 and live SSE", LifecycleAuthAclRpcAndSse);
        Console.WriteLine(checks + " checks, " + failures + " failed groups");
        return failures == 0 ? 0 : 1;
    }
}
