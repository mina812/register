// ============================================================
//  IGTLinkConnector.cs
//  HOARES — Milestone 1
//  Manages the TCP connection to the 3D Slicer IGT server.
//  Attach to a persistent GameObject (e.g. "IGTManager").
// ============================================================
using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

namespace HOARES.IGT
{
    public class IGTLinkConnector : MonoBehaviour
    {
        // ── Inspector fields ──────────────────────────────────────
        [Header("Slicer Server")]
        [Tooltip("IP of the machine running 3D Slicer (use 127.0.0.1 if same PC)")]
        public string slicerIP   = "127.0.0.1";
        public int    slicerPort = 18944;

        [Header("Behaviour")]
        [Tooltip("Automatically connect on Start()")]
        public bool autoConnect  = true;
        [Tooltip("Seconds between reconnection attempts")]
        public float reconnectDelay = 3f;

        // ── Public state (read by other scripts) ──────────────────
        public bool IsConnected { get; private set; }

        // ── Events ────────────────────────────────────────────────
        /// <summary>Fired on main thread when a TRANSFORM message arrives.</summary>
        public event Action<IGTMessage> OnMessageReceived;

        // ── Internals ─────────────────────────────────────────────
        private TcpClient          _client;
        private NetworkStream      _stream;
        private Thread             _receiveThread;
        private volatile bool      _running;
        private ConcurrentQueue<IGTMessage> _messageQueue = new();
        private float              _reconnectTimer;

        // ── Unity lifecycle ───────────────────────────────────────
        private void Start()
        {
            if (autoConnect)
                Connect();
        }

        private void Update()
        {
            // Dequeue messages on the main thread so Unity API is safe
            while (_messageQueue.TryDequeue(out IGTMessage msg))
                OnMessageReceived?.Invoke(msg);

            // Auto-reconnect
            if (!IsConnected && autoConnect)
            {
                _reconnectTimer += Time.deltaTime;
                if (_reconnectTimer >= reconnectDelay)
                {
                    _reconnectTimer = 0f;
                    Connect();
                }
            }
        }

        private void OnDestroy() => Disconnect();

        // ── Public API ────────────────────────────────────────────
        public void Connect()
        {
            if (IsConnected) return;
            try
            {
                _client = new TcpClient();
                _client.Connect(slicerIP, slicerPort);
                _stream  = _client.GetStream();
                IsConnected = true;
                _running    = true;
                _reconnectTimer = 0f;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "IGTLink-Recv"
                };
                _receiveThread.Start();

                Debug.Log($"[HOARES] Connected to Slicer IGT server at {slicerIP}:{slicerPort}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HOARES] Connection failed: {ex.Message}. Retrying in {reconnectDelay}s…");
                IsConnected = false;
            }
        }

        public void Disconnect()
        {
            _running    = false;
            IsConnected = false;
            _stream?.Close();
            _client?.Close();
            _receiveThread?.Join(500);
            Debug.Log("[HOARES] Disconnected from IGT server.");
        }

        /// <summary>
        /// Returns the active NetworkStream so sibling components
        /// (e.g. IGTStringHandler) can write outgoing packets.
        /// Returns null if not connected.
        /// </summary>
        public NetworkStream GetStream()
        {
            return IsConnected ? _stream : null;
        }

        // ── Background receive thread ─────────────────────────────
        private void ReceiveLoop()
        {
            // IGT v2 header is exactly 58 bytes
            byte[] header = new byte[IGTMessageParser.HEADER_SIZE];

            while (_running)
            {
                try
                {
                    // 1. Read fixed-size header
                    if (!ReadExact(header, IGTMessageParser.HEADER_SIZE))
                        break;

                    // 2. Parse header to find body size and message type
                    if (!IGTMessageParser.TryParseHeader(header,
                            out string msgType,
                            out string deviceName,
                            out long   bodySize))
                    {
                        Debug.LogWarning("[HOARES] Invalid IGT header, skipping.");
                        continue;
                    }

                    // 3. Read body — handle IGT v2 extended header (14 bytes)
                    byte[] fullBody = new byte[bodySize];
                    if (bodySize > 0 && !ReadExact(fullBody, (int)bodySize))
                        break;

                    // Strip 14-byte extended header for version 2
                    byte[] body;
                    ushort extHeaderSize = (ushort)((fullBody[0] << 8) | fullBody[1]);
                    if (extHeaderSize > 0 && bodySize > extHeaderSize)
                        body = fullBody[extHeaderSize..];
                    else
                        body = fullBody;

                    // 4. Build message and enqueue for main thread
                    IGTMessage msg = IGTMessageParser.BuildMessage(
                        msgType, deviceName, body);
                    if (msg != null)
                        _messageQueue.Enqueue(msg);
                }
                catch (Exception ex) when (_running)
                {
                    Debug.LogWarning($"[HOARES] Receive error: {ex.Message}");
                    break;
                }
            }

            // Connection dropped
            IsConnected = false;
            Debug.LogWarning("[HOARES] IGT receive loop ended.");
        }

        /// <summary>Reads exactly count bytes into buf. Returns false on EOF/error.</summary>
        private bool ReadExact(byte[] buf, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buf, offset, count - offset);
                if (read == 0) return false;   // server closed connection
                offset += read;
            }
            return true;
        }
    }
}