using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

public sealed class TcpService : IDisposable
{
    private readonly CadDispatcher _dispatcher;
    private TcpListener? _listener; private CancellationTokenSource? _cts; private Mutex? _instanceMutex; private string? _token;
    public TcpService(CadDispatcher dispatcher) => _dispatcher = dispatcher;
    public bool IsRunning => _listener != null;
    public int Port { get; private set; }
    public void Start()
    {
        if (IsRunning) return;
        _instanceMutex = new Mutex(true, "Global\\CADMCP.AutoCAD2022.Service", out var created);
        if (!created) { _instanceMutex.Dispose(); _instanceMutex = null; throw new InvalidOperationException("另一 AutoCAD 进程已经开启 CADMCP 服务"); }
        Port = SettingsStore.Current.Port; _token = CreateToken(); _cts = new CancellationTokenSource();
        try { _listener = new TcpListener(IPAddress.Loopback, Port); _listener.Start(); WriteSession(); _ = AcceptLoopAsync(_cts.Token); }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        try { _cts?.Cancel(); _listener?.Stop(); } catch { }
        _listener = null; _cts?.Dispose(); _cts = null; _token = null;
        try { if (File.Exists(RuntimePaths.SessionFile)) File.Delete(RuntimePaths.SessionFile); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { } _instanceMutex?.Dispose(); _instanceMutex = null;
    }
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try { client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false); _ = HandleClientAsync(client, token); }
            catch when (token.IsCancellationRequested) { client?.Dispose(); break; }
            catch { client?.Dispose(); }
        }
    }
    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var auth = await FrameProtocol.ReadAsync(stream, token).ConfigureAwait(false);
                var valid = auth?.Value<string>("type") == "authenticate" && auth.Value<int?>("protocolVersion") == 1 && FixedEquals(auth.Value<string>("token"), _token);
                await FrameProtocol.WriteAsync(stream, new JObject { ["type"] = "authenticated", ["success"] = valid, ["message"] = valid ? null : "认证失败" }, token).ConfigureAwait(false);
                if (!valid) return;
                while (!token.IsCancellationRequested)
                {
                    var request = await FrameProtocol.ReadAsync(stream, token).ConfigureAwait(false); if (request == null) return;
                    var response = await ProcessAsync(request).ConfigureAwait(false); await FrameProtocol.WriteAsync(stream, response, token).ConfigureAwait(false);
                }
            }
            catch { }
        }
    }
    private async Task<JObject> ProcessAsync(JObject request)
    {
        var id = request["id"]?.DeepClone(); var method = request.Value<string>("method") ?? ""; var parameters = request["params"] as JObject ?? new JObject();
        var callId = parameters.Value<string>("callId") ?? Guid.NewGuid().ToString();
        try
        {
            if (request.Value<string>("jsonrpc") != "2.0" || string.IsNullOrWhiteSpace(method)) throw new InvalidDataException("无效 JSON-RPC 请求");
            var result = await _dispatcher.ExecuteAsync(method, parameters, callId).ConfigureAwait(false); AuditLogger.Write(method, callId, parameters, result);
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception error) { return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = "invalid_request", ["message"] = error.Message } }; }
    }
    private void WriteSession()
    {
        Directory.CreateDirectory(RuntimePaths.RuntimeDirectory);
        var session = new JObject { ["processId"] = Process.GetCurrentProcess().Id, ["port"] = Port, ["token"] = _token, ["protocolVersion"] = 1, ["startedAt"] = DateTimeOffset.Now };
        File.WriteAllText(RuntimePaths.SessionFile, session.ToString(Formatting.Indented)); RestrictSessionFile();
    }
    private static void RestrictSessionFile()
    {
        var identity = WindowsIdentity.GetCurrent().User; if (identity == null) throw new InvalidOperationException("无法识别当前 Windows 用户");
        var security = new FileSecurity(); security.SetOwner(identity); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(RuntimePaths.SessionFile).SetAccessControl(security);
    }
    private static string CreateToken() { var bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    private static bool FixedEquals(string? left, string? right)
    {
        if (left == null || right == null) return false; var a = System.Text.Encoding.UTF8.GetBytes(left); var b = System.Text.Encoding.UTF8.GetBytes(right); if (a.Length != b.Length) return false;
        var diff = 0; for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
    }
    public void Dispose() => Stop();
}
