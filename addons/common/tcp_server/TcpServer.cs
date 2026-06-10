using System;
using System.Text;
using Godot;
using Godot.Collections;
using TcpServerPeer = Godot.TcpServer;

namespace LaunchMonitors.Common.Tcp;

[GlobalClass]
public partial class TcpServer : Node
{
    private const int MaxTcpBuffer = 65536;
    private const int DefaultPort = 49152;

    private readonly TcpServerPeer _tcpServer = new();
    private StreamPeerTcp? _tcpConnection;
    private bool _tcpConnected;
    private string _rxBuffer = string.Empty;
    private string _connectedHost = string.Empty;
    private Dictionary _shotData = new();
    private readonly Dictionary _resp200 = new() { { "Code", 200 } };
    private readonly Dictionary _resp50x = new() { { "Code", 501 }, { "Message", "Failure Occured" } };

    [Signal]
    public delegate void HitBallEventHandler(Dictionary data);

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    [Export]
    public int Port { get; set; } = DefaultPort;

    public bool IsListening => _tcpServer.IsListening();

    public bool HasConnection => _tcpConnected;

    public string ConnectedHost => _connectedHost;

    public void StartListening(int port)
    {
        ListenOnPort(port);
    }

    public override void _ExitTree()
    {
        Shutdown();
    }

    public override void _Process(double delta)
    {
        if (!_tcpConnected)
        {
            _tcpConnection = _tcpServer.TakeConnection();
            if (_tcpConnection != null)
            {
                _connectedHost = _tcpConnection.GetConnectedHost() ?? string.Empty;
                GD.Print($"We have a tcp connection at {_connectedHost}");
                _tcpConnected = true;
                _rxBuffer = string.Empty;
                var hostLabel = string.IsNullOrEmpty(_connectedHost) ? "unknown" : _connectedHost;
                EmitStatus($"Connected: {hostLabel}");
            }

            return;
        }

        if (_tcpConnection == null)
        {
            _tcpConnected = false;
            _connectedHost = string.Empty;
            return;
        }

        _tcpConnection.Poll();
        var status = _tcpConnection.GetStatus();
        if (status == StreamPeerTcp.Status.None)
        {
            _tcpConnected = false;
            _connectedHost = string.Empty;
            _rxBuffer = string.Empty;
            GD.Print("tcp disconnected");
            EmitListeningStatus();
            return;
        }

        if (status != StreamPeerTcp.Status.Connected)
        {
            return;
        }

        var bytesAvailable = (int)_tcpConnection.GetAvailableBytes();
        if (bytesAvailable <= 0)
        {
            return;
        }

        // TCP is a byte stream: a single network JSON shot payload may arrive
        // split across several reads, and several payloads may arrive coalesced
        // in one read. Accumulate into a buffer and extract whole JSON objects by
        // brace-matching rather than parsing each read as one complete document.
        _rxBuffer += _tcpConnection.GetUtf8String(bytesAvailable);

        if (_rxBuffer.Length > MaxTcpBuffer)
        {
            GD.PushWarning($"TCP buffer exceeded {MaxTcpBuffer} bytes without a complete message, dropping");
            _rxBuffer = string.Empty;
            RespondError(413, "Payload too large");
            return;
        }

        while (TryExtractJsonObject(ref _rxBuffer, out var jsonText))
        {
            ProcessMessage(jsonText);
        }
    }

    private void ProcessMessage(string jsonText)
    {
        var json = new Json();
        if (json.Parse(jsonText) != Error.Ok)
        {
            RespondError(501, "Bad JSON data");
            return;
        }

        var data = json.GetData();
        if (data.VariantType != Variant.Type.Dictionary)
        {
            RespondError(501, "Expected JSON object");
            return;
        }

        _shotData = data.AsGodotDictionary();
        GD.Print($"Launch monitor payload: {jsonText}");

        if (_shotData.TryGetValue("ShotDataOptions", out var shotDataOptionsVar)
            && shotDataOptionsVar.VariantType == Variant.Type.Dictionary)
        {
            var shotDataOptions = shotDataOptionsVar.AsGodotDictionary();
            if (shotDataOptions.TryGetValue("ContainsBallData", out var containsBallDataVar)
                && containsBallDataVar.VariantType == Variant.Type.Bool
                && (bool)containsBallDataVar
                && _shotData.TryGetValue("BallData", out var ballDataVar)
                && ballDataVar.VariantType == Variant.Type.Dictionary)
            {
                // No ack here: the HitBall consumer decides. A scene that
                // validates shot data calls RespondShotAccepted/RespondShotRejected
                // after validating; the launch-monitor manager acks on forward.
                EmitSignal(SignalName.HitBall, ballDataVar.AsGodotDictionary());
                return;
            }
        }

        RespondError(501, "Missing or invalid shot data");
    }

    // Extracts the first complete top-level {...} object from the buffer,
    // tracking string literals + escapes so braces inside JSON strings are not
    // miscounted. Returns the object and advances the buffer past it; leaves a
    // partial trailing object buffered for the next read. Drops leading junk so
    // a stream that never opens an object cannot grow unbounded.
    private static bool TryExtractJsonObject(ref string buffer, out string json)
    {
        json = string.Empty;
        var depth = 0;
        var inString = false;
        var escape = false;
        var start = -1;

        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            json = buffer.Substring(start, i - start + 1);
                            buffer = buffer[(i + 1)..];
                            return true;
                        }
                    }

                    break;
            }
        }

        if (start == -1)
        {
            // No object start anywhere in the buffer: it is whitespace/junk only.
            buffer = string.Empty;
        }

        return false;
    }

    // Ack methods for the HitBall consumer: the sender's 200/501 reflects shot
    // validation by whoever consumes the payload, not parse validity.
    public void RespondShotAccepted()
    {
        if (!EnsureConnectedForWrite())
        {
            return;
        }

        _tcpConnection!.PutData(Encoding.UTF8.GetBytes(Json.Stringify(_resp200)));
    }

    public void RespondShotRejected()
    {
        RespondError(501, "Invalid ball data");
    }

    public void RespondError(int code, string message)
    {
        if (!EnsureConnectedForWrite())
        {
            return;
        }

        _resp50x["Code"] = code;
        _resp50x["Message"] = message;
        _tcpConnection!.PutData(Encoding.UTF8.GetBytes(Json.Stringify(_resp50x)));
    }

    private bool EnsureConnectedForWrite()
    {
        if (_tcpConnection == null)
        {
            return false;
        }

        _tcpConnection.Poll();
        var status = _tcpConnection.GetStatus();
        if (status == StreamPeerTcp.Status.None)
        {
            _tcpConnected = false;
            return false;
        }

        return status == StreamPeerTcp.Status.Connected;
    }

    public void StopListening()
    {
        Shutdown();
        EmitStatus("Stopped");
    }

    public bool GetIsListening()
    {
        return IsListening;
    }

    private void ListenOnPort(int port)
    {
        Port = Math.Clamp(port, 1, 65535);
        Shutdown();

        var error = _tcpServer.Listen((ushort)Port);
        if (error != Error.Ok)
        {
            GD.PushError($"TCP server failed to listen on port {Port}. Error: {error}");
            EmitStatus($"Failed: {error}");
            return;
        }

        EmitListeningStatus();
    }

    private void Shutdown()
    {
        if (_tcpConnection != null)
        {
            _tcpConnection.DisconnectFromHost();
            _tcpConnection = null;
        }

        _tcpConnected = false;
        _connectedHost = string.Empty;
        _shotData.Clear();
        _rxBuffer = string.Empty;

        if (_tcpServer.IsListening())
        {
            _tcpServer.Stop();
        }
    }

    private void EmitListeningStatus()
    {
        EmitStatus(_tcpServer.IsListening() ? $"Listening on {Port}" : "Stopped");
    }

    private void EmitStatus(string status)
    {
        EmitSignal(SignalName.StatusChanged, status);
    }
}
