using System;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using UnityEngine;

namespace HOARES.IGT
{
    [RequireComponent(typeof(IGTLinkConnector))]
    public class IGTStringHandler : MonoBehaviour
    {
        public const string CMD_STREAM_AXIAL    = "STREAM_AXIAL";
        public const string CMD_STREAM_CORONAL  = "STREAM_CORONAL";
        public const string CMD_STREAM_SAGITTAL = "STREAM_SAGITTAL";
        public const string CMD_STREAM_ALL      = "STREAM_ALL";
        public const string CMD_WL_BONE         = "WL_BONE";
        public const string CMD_WL_SOFT         = "WL_SOFT";
        public const string CMD_WL_LUNG         = "WL_LUNG";

        [Header("Device Name")]
        public string outgoingDeviceName = "HOARES_Command";

        [Header("Debug")]
        public bool logSent     = true;
        public bool logReceived = true;

        public event Action<string, string> OnStringReceived;

        private IGTLinkConnector _connector;
        private ConcurrentQueue<(string device, string content)> _inboundQueue = new();

        private void Awake() => _connector = GetComponent<IGTLinkConnector>();
        private void OnEnable()  => _connector.OnMessageReceived += HandleMessage;
        private void OnDisable() { if (_connector) _connector.OnMessageReceived -= HandleMessage; }

        private void Update()
        {
            while (_inboundQueue.TryDequeue(out var pair))
            {
                if (logReceived) Debug.Log($"[HOARES] STRING received: \"{pair.content}\"");
                OnStringReceived?.Invoke(pair.device, pair.content);
            }
        }

        public bool SendCommand(string command)
        {
            if (string.IsNullOrEmpty(command) || !_connector.IsConnected) return false;

            try
            {
                byte[] packet = BuildStringPacket(command, outgoingDeviceName);
                NetworkStream stream = _connector.GetStream();
                if (stream == null) return false;

                stream.Write(packet, 0, packet.Length);
                stream.Flush();

                if (logSent) Debug.Log($"[HOARES] Sent → '{command}' (CRC Fixed)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Send failed: {ex.Message}");
                return false;
            }
        }

        private void HandleMessage(IGTMessage msg)
        {
            if (msg.Type != IGTMessageType.String || msg.RawBody == null || msg.RawBody.Length < 4) return;
            int length = (msg.RawBody[2] << 8) | msg.RawBody[3];
            int available = msg.RawBody.Length - 4;
            int readLen = Math.Min(length, available);
            if (readLen <= 0) return;

            string content = Encoding.UTF8.GetString(msg.RawBody, 4, readLen);
            _inboundQueue.Enqueue((msg.DeviceName, content));
        }

        private byte[] BuildStringPacket(string command, string deviceName)
        {
            byte[] payload = Encoding.UTF8.GetBytes(command);
            ushort encoding = 3;
            ushort strLength = (ushort)payload.Length;

            byte[] body = new byte[4 + payload.Length];
            body[0] = (byte)(encoding >> 8);
            body[1] = (byte)(encoding & 0xFF);
            body[2] = (byte)(strLength >> 8);
            body[3] = (byte)(strLength & 0xFF);
            Array.Copy(payload, 0, body, 4, payload.Length);

            byte[] header = new byte[58];
            header[0] = 0x00;
            header[1] = 0x01; // Standard V1 Protocol
            WriteAsciiFixed(header, 2, "STRING", 12);
            WriteAsciiFixed(header, 14, deviceName, 20);
            WriteBytesAt(header, 42, (ulong)body.Length, 8);

            // 1. THE ULTIMATE FIX: Compute the correct C++ CRC
            ulong crc = ComputeCRC64(body);
            WriteBytesAt(header, 50, crc, 8);

            byte[] packet = new byte[header.Length + body.Length];
            Array.Copy(header, 0, packet, 0, header.Length);
            Array.Copy(body, 0, packet, header.Length, body.Length);
            return packet;
        }

        private static void WriteAsciiFixed(byte[] buf, int offset, string value, int maxLen)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            int copy = Math.Min(bytes.Length, maxLen);
            Array.Copy(bytes, 0, buf, offset, copy);
        }

        private static void WriteBytesAt(byte[] buf, int offset, ulong value, int byteCount)
        {
            for (int i = byteCount - 1; i >= 0; i--)
            {
                buf[offset + i] = (byte)(value & 0xFF);
                value >>= 8;
            }
        }

        // 2. THE ULTIMATE FIX: This math now exactly mimics Slicer's C++ source code (igtl_crc64)
        private static ulong ComputeCRC64(byte[] data)
        {
            ulong crc = 0UL;
            ulong poly = 0x42F0E1EBA9EA3693UL;

            for (int i = 0; i < data.Length; i++)
            {
                ulong part = crc ^ (((ulong)data[i]) << 56);
                for (int j = 0; j < 8; j++)
                {
                    if ((part & 0x8000000000000000UL) != 0)
                        part = (part << 1) ^ poly;
                    else
                        part = (part << 1);
                }
                crc = part;
            }
            return crc;
        }
    }
}