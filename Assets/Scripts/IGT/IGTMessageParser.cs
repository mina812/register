// ============================================================
//  IGTMessageParser.cs
//  HOARES — Updated for Milestone 2
//
//  CHANGES FROM M1:
//  - Added IMAGE and POLYDATA to BuildMessage switch
//  - Both message types store raw body for their handlers
//  - All M1 TRANSFORM logic unchanged
// ============================================================
using System;
using System.Text;
using UnityEngine;

namespace HOARES.IGT
{
    // ── Data structures ───────────────────────────────────────
    public enum IGTMessageType { Unknown, Transform, Image, PolyData, String }

    public class IGTMessage
    {
        public IGTMessageType Type;
        public string         DeviceName;
        // TRANSFORM payload
        public Vector3        Position;
        public Quaternion     Rotation;
        // Raw body — used by PolyData and Image handlers
        public byte[]         RawBody;
    }

    // ── Parser ────────────────────────────────────────────────
    public static class IGTMessageParser
    {
        public const int HEADER_SIZE = 58;

        // ── Header parsing ─────────────────────────────────────
        public static bool TryParseHeader(
            byte[]     header,
            out string msgType,
            out string deviceName,
            out long   bodySize)
        {
            msgType    = string.Empty;
            deviceName = string.Empty;
            bodySize   = 0;

            if (header == null || header.Length < HEADER_SIZE)
                return false;

            ushort version = ReadUInt16BE(header, 0);
            // Accept IGT v1 and v2 (Slicer 5.x sends v2)
            if (version != 1 && version != 2)
                Debug.LogWarning($"[HOARES] Unexpected IGT version: {version}");

            msgType    = ReadNullTerminatedString(header, 2,  12);
            deviceName = ReadNullTerminatedString(header, 14, 20);
            bodySize   = (long)ReadUInt64BE(header, 42);

            return true;
        }

        // ── Message construction ───────────────────────────────
        public static IGTMessage BuildMessage(
            string msgType, string deviceName, byte[] fullBody)
        {
            byte[] body = fullBody;
            if (fullBody != null && fullBody.Length >= 2)
            {
                ushort extHeaderSize = (ushort)((fullBody[0] << 8) | fullBody[1]);
                int skip = 2 + extHeaderSize;
                Debug.Log($"[HOARES] {msgType} bytes[0]={fullBody[0]} bytes[1]={fullBody[1]} extSize={(fullBody[0]<<8)|fullBody[1]} skip={skip}");
                body = (skip > 0 && skip < fullBody.Length) ? fullBody[skip..] : fullBody;
            }

            var msg = new IGTMessage
            {
                DeviceName = deviceName,
                RawBody    = body
            };

            switch (msgType.Trim().ToUpperInvariant())
            {
                case "TRANSFORM":
                    msg.Type = IGTMessageType.Transform;
                    if (!TryParseTransform(body, out msg.Position, out msg.Rotation))
                        return null;
                    break;
                case "IMAGE":
                    msg.Type = IGTMessageType.Image;
                    break;
                case "POLYDATA":
                    msg.Type = IGTMessageType.PolyData;
                    break;
                case "STRING":
                    msg.Type = IGTMessageType.String;
                    break;
                default:
                    msg.Type = IGTMessageType.Unknown;
                    break;
            }
            return msg;
        }

        // ── TRANSFORM body decoder (column-major, IGT v2) ──────
        public static bool TryParseTransform(
            byte[]         body,
            out Vector3    position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (body == null || body.Length < 48)
                return false;

            // 12 floats, big-endian, column-major 4x4 matrix (upper 3 rows)
            // col0=[f0,f1,f2]  col1=[f3,f4,f5]  col2=[f6,f7,f8]
            // col3=[f9,f10,f11] = TRANSLATION (TX, TY, TZ in mm)
            float[] f = new float[12];
            for (int i = 0; i < 12; i++)
                f[i] = ReadFloatBE(body, i * 4);

            // Translation — LPS → Unity: negate X and Y
            position = new Vector3(-f[9], -f[10], f[11]);

            // Rotation matrix (column-major → Unity row-major)
            Matrix4x4 m = Matrix4x4.identity;
            m[0, 0] = -f[0]; m[0, 1] = -f[3]; m[0, 2] = -f[6];
            m[1, 0] = -f[1]; m[1, 1] = -f[4]; m[1, 2] = -f[7];
            m[2, 0] =  f[2]; m[2, 1] =  f[5]; m[2, 2] =  f[8];

            rotation = QuaternionFromMatrix(m);
            return true;
        }

        // ── Quaternion from rotation Matrix4x4 (Shepperd) ─────
        private static Quaternion QuaternionFromMatrix(Matrix4x4 m)
        {
            float tr = m[0,0] + m[1,1] + m[2,2];
            float w, x, y, z;
            if (tr > 0f)
            {
                float s = Mathf.Sqrt(tr + 1f) * 2f;
                w = 0.25f * s;
                x = (m[2,1] - m[1,2]) / s;
                y = (m[0,2] - m[2,0]) / s;
                z = (m[1,0] - m[0,1]) / s;
            }
            else if (m[0,0] > m[1,1] && m[0,0] > m[2,2])
            {
                float s = Mathf.Sqrt(1f + m[0,0] - m[1,1] - m[2,2]) * 2f;
                w = (m[2,1] - m[1,2]) / s;
                x = 0.25f * s;
                y = (m[0,1] + m[1,0]) / s;
                z = (m[0,2] + m[2,0]) / s;
            }
            else if (m[1,1] > m[2,2])
            {
                float s = Mathf.Sqrt(1f + m[1,1] - m[0,0] - m[2,2]) * 2f;
                w = (m[0,2] - m[2,0]) / s;
                x = (m[0,1] + m[1,0]) / s;
                y = 0.25f * s;
                z = (m[1,2] + m[2,1]) / s;
            }
            else
            {
                float s = Mathf.Sqrt(1f + m[2,2] - m[0,0] - m[1,1]) * 2f;
                w = (m[1,0] - m[0,1]) / s;
                x = (m[0,2] + m[2,0]) / s;
                y = (m[1,2] + m[2,1]) / s;
                z = 0.25f * s;
            }
            return new Quaternion(x, y, z, w);
        }

        // ── Binary read helpers (big-endian) ──────────────────
        private static ushort ReadUInt16BE(byte[] b, int offset)
            => (ushort)((b[offset] << 8) | b[offset+1]);

        private static ulong ReadUInt64BE(byte[] b, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++)
                v = (v << 8) | b[offset + i];
            return v;
        }

        private static float ReadFloatBE(byte[] b, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                byte[] tmp = { b[offset+3], b[offset+2], b[offset+1], b[offset] };
                return BitConverter.ToSingle(tmp, 0);
            }
            return BitConverter.ToSingle(b, offset);
        }

        private static string ReadNullTerminatedString(
            byte[] b, int offset, int maxLen)
        {
            int len = 0;
            while (len < maxLen && offset+len < b.Length && b[offset+len] != 0)
                len++;
            return Encoding.ASCII.GetString(b, offset, len);
        }
    }
}