// ============================================================
//  IGTPolyDataHandler.cs
//  HOARES — Milestone 2 Part A
//
//  Receives a POLYDATA message from 3D Slicer and builds a
//  Unity Mesh from the decoded vertices and triangles.
//
//  OpenIGTLink POLYDATA body layout (after stripping the
//  14-byte IGT v2 extended header):
//
//  HEADER SECTION (60 bytes):
//  [4]  uint32  nPoints       — number of vertices
//  [4]  uint32  nLines        — number of line strips (unused)
//  [4]  uint32  nPolygons     — number of polygon cells
//  [4]  uint32  nTriStrips    — number of triangle strips (unused)
//  [4]  uint32  nAttributes   — number of attributes (unused)
//
//  POINT DATA (nPoints * 12 bytes):
//  float32 x, y, z  per vertex (LPS, mm)
//
//  POLYGON DATA (nPolygons * 16 bytes):
//  uint32 count (always 3 for triangles), uint32 v0, v1, v2
//
//  SETUP
//  -----
//  1. Add IGTPolyDataHandler to IGTManager GameObject
//  2. Assign spineTarget (the GameObject that will receive the mesh)
//  3. Connect to IGTLinkConnector via OnEnable
// ============================================================
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace HOARES.IGT
{
    [RequireComponent(typeof(IGTLinkConnector))]
    public class IGTPolyDataHandler : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("GameObject that will receive the spine mesh")]
        public GameObject spineTarget;

        [Header("Material")]
        [Tooltip("Material to apply to the spine mesh")]
        public Material spineMaterial;

        [Header("Settings")]
        [Tooltip("Scale: Slicer is in mm, Unity in metres")]
        public float unitScale = 0.001f;

        [Header("Debug")]
        public bool logOnReceive = true;

        // ── Thread-safe mesh data queue ───────────────────────
        private struct MeshData
        {
            public Vector3[] vertices;
            public int[]     triangles;
            public Vector3[] normals;
        }
        private ConcurrentQueue<MeshData> _meshQueue = new();

        // ── Unity lifecycle ───────────────────────────────────
        private void OnEnable()
        {
            GetComponent<IGTLinkConnector>().OnMessageReceived += HandleMessage;
        }

        private void OnDisable()
        {
            var c = GetComponent<IGTLinkConnector>();
            if (c) c.OnMessageReceived -= HandleMessage;
        }

        private void Update()
        {
            // Apply mesh on main thread (Unity API requirement)
            if (_meshQueue.TryDequeue(out MeshData data))
                ApplyMesh(data);
        }

        // ── Message handler ───────────────────────────────────
        private void HandleMessage(IGTMessage msg)
        {
             Debug.Log($"[HOARES] HandleMessage called type={msg.Type} bodyLen={msg.RawBody?.Length}");
    
            if (msg.Type != IGTMessageType.PolyData) return;
            if (msg.RawBody == null || msg.RawBody.Length < 20)
            {
                Debug.LogWarning($"[HOARES] POLYDATA body too short: {msg.RawBody?.Length}");
                return;
            }

            // Debug: print first 40 bytes as hex and as uint32 big-endian
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Mathf.Min(40, msg.RawBody.Length); i++)
                sb.Append($"{msg.RawBody[i]:X2} ");
            Debug.Log($"[HOARES] POLYDATA first 40 bytes: {sb}");

            uint w0 = (uint)(msg.RawBody[0]<<24|msg.RawBody[1]<<16|msg.RawBody[2]<<8|msg.RawBody[3]);
            uint w1 = (uint)(msg.RawBody[4]<<24|msg.RawBody[5]<<16|msg.RawBody[6]<<8|msg.RawBody[7]);
            uint w2 = (uint)(msg.RawBody[8]<<24|msg.RawBody[9]<<16|msg.RawBody[10]<<8|msg.RawBody[11]);
            uint w3 = (uint)(msg.RawBody[12]<<24|msg.RawBody[13]<<16|msg.RawBody[14]<<8|msg.RawBody[15]);
            uint w4 = (uint)(msg.RawBody[16]<<24|msg.RawBody[17]<<16|msg.RawBody[18]<<8|msg.RawBody[19]);
            Debug.Log($"[HOARES] Words: w0={w0} w1={w1} w2={w2} w3={w3} w4={w4}");

            // Print words 5-15 to find polygon count
            var sb2 = new System.Text.StringBuilder();
            for (int i = 5; i < 20 && i*4+3 < msg.RawBody.Length; i++)
            {
                uint w = (uint)(msg.RawBody[i*4]<<24|msg.RawBody[i*4+1]<<16|msg.RawBody[i*4+2]<<8|msg.RawBody[i*4+3]);
                sb2.Append($"w{i}={w} ");
            }
            Debug.Log($"[HOARES] Words 5-19: {sb2}");

            if (logOnReceive)
                Debug.Log($"[HOARES] POLYDATA '{msg.DeviceName}' " +
                          $"body={msg.RawBody.Length} bytes — parsing...");

            if (TryParsePolyData(msg.RawBody, out MeshData data))
            {
                _meshQueue.Enqueue(data);
                Debug.Log($"[HOARES] Mesh parsed: {data.vertices.Length} verts, " +
                          $"{data.triangles.Length / 3} tris — queued for main thread.");
            }
            else
            {
                Debug.LogWarning("[HOARES] POLYDATA parse failed.");
            }
        }

        // ── POLYDATA parser ───────────────────────────────────
        private bool TryParsePolyData(byte[] body, out MeshData data)
        {
            data = default;
            try
            {
                int offset = 0;

                // ── Header: 7 × uint32 = 28 bytes ─────────────────────
                uint nPoints       = ReadUInt32BE(body, offset); offset += 4;
                uint nLines        = ReadUInt32BE(body, offset); offset += 4;
                uint nVertexData   = ReadUInt32BE(body, offset); offset += 4;
                uint nTriStrips    = ReadUInt32BE(body, offset); offset += 4;
                uint nPolyDataSize = ReadUInt32BE(body, offset); offset += 4;
                uint nPolygons     = ReadUInt32BE(body, offset); offset += 4;
                uint nAttributes   = ReadUInt32BE(body, offset); offset += 4;

                Debug.Log($"[HOARES] Header: points={nPoints} polygons={nPolygons} " +
                          $"attributes={nAttributes}");

                if (nPoints == 0 || nPolygons == 0) return false;

                // ── Vertex positions (nPoints × 12 bytes) ─────────────
                Vector3[] verts   = new Vector3[nPoints];
                Vector3[] normals = new Vector3[nPoints];

                for (int i = 0; i < nPoints; i++)
                {
                    float x = ReadFloatBE(body, offset); offset += 4;
                    float y = ReadFloatBE(body, offset); offset += 4;
                    float z = ReadFloatBE(body, offset); offset += 4;
                    // LPS → Unity: negate X and Y, scale mm → metres
                    verts[i] = new Vector3(-x, -y, z) * unitScale;
                }

                // ── Point normals (nPoints × 12 bytes) ────────────────
                // Slicer sends per-vertex normals as first point array
                for (int i = 0; i < nPoints; i++)
                {
                    float nx = ReadFloatBE(body, offset); offset += 4;
                    float ny = ReadFloatBE(body, offset); offset += 4;
                    float nz = ReadFloatBE(body, offset); offset += 4;
                    // LPS → Unity normal conversion
                    normals[i] = new Vector3(-nx, -ny, nz);
                }

                Debug.Log($"[HOARES] After normals: offset={offset} " +
                          $"remaining={body.Length - offset}");

                // Scan for the byte pattern: 00 00 00 03 00 00 00 02 00 00 00 01 00 00 00 00
                // This is triangle 0: [count=3, v0=2, v1=1, v2=0]
                byte[] pattern = { 0,0,0,3, 0,0,0,2, 0,0,0,1, 0,0,0,0 };
                int patternFound = -1;
                for (int s = 700000; s < body.Length - 16; s++)
                {
                    bool match = true;
                    for (int p = 0; p < 16; p++)
                        if (body[s + p] != pattern[p]) { match = false; break; }
                    if (match) { patternFound = s; break; }
                }
                Debug.Log($"[HOARES] Triangle pattern found at offset: {patternFound}");

                // ── Skip to triangle data at known offset ──────────────────
                // Calculated: header(28) + vertices(370236) + normals(370236) = 740500
                // The 103 unexplained bytes are attribute section headers
                // We jump directly to the correct offset instead of parsing them
                offset = (int)(28 + nPoints * 12 + nPoints * 12);

                // Scan for the polygon count marker to skip the 103-byte attribute headers
                // Find the position where nPolygons appears as the cell count
                uint target = nPolygons;
                int scanStart = offset;
                int foundAt = -1;
                for (int s = scanStart; s < scanStart + 200 && s + 4 <= body.Length; s += 4)
                {
                    uint val = ReadUInt32BE(body, s);
                    if (val == target)
                    {
                        foundAt = s;
                        Debug.Log($"[HOARES] Found polygon count {target} at offset {s}");
                        break;
                    }
                }

                if (foundAt >= 0)
                    offset = foundAt + 4; // skip past the count, now at first triangle
                else
                {
                    // Fallback: hardcode the known offset
                    offset = 740500;
                    Debug.LogWarning("[HOARES] Using hardcoded triangle offset 740500");
                }

                Debug.Log($"[HOARES] Scan result: foundAt={foundAt} offset used={offset}");

                // Also print the uint32 values around where triangles should start
                var dbg = new System.Text.StringBuilder();
                for (int d = 740490; d < 740530 && d + 4 <= body.Length; d += 4)
                {
                    uint v = ReadUInt32BE(body, d);
                    dbg.Append($"[{d}]={v} ");
                }
                Debug.Log($"[HOARES] Values around 740500: {dbg}");

                Debug.Log($"[HOARES] Triangle data starts at offset={offset}");

                // ── Triangles — indices are LITTLE-ENDIAN ─────────────────
                int[] tris = new int[nPolygons * 3];
                int triIdx = 0;
                for (int i = 0; i < nPolygons; i++)
                {
                    if (offset + 16 > body.Length) break;

                    // Count is big-endian, indices are little-endian
                    uint count = ReadUInt32BE(body, offset); offset += 4;
                    uint v0    = ReadUInt32LE(body, offset); offset += 4;
                    uint v1    = ReadUInt32LE(body, offset); offset += 4;
                    uint v2    = ReadUInt32LE(body, offset); offset += 4;

                    tris[triIdx++] = (int)v0;
                    tris[triIdx++] = (int)v2;
                    tris[triIdx++] = (int)v1;
                }

                Debug.Log($"[HOARES] Triangles read. offset={offset} " +
                          $"remaining={body.Length - offset}");

                data = new MeshData
                {
                    vertices  = verts,
                    triangles = tris,
                    normals   = normals
                };
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HOARES] POLYDATA exception: {ex.Message}");
                return false;
            }
        }

        // ── Apply mesh to GameObject (main thread) ────────────
        private void ApplyMesh(MeshData data)
        {
            if (spineTarget == null)
            {
                // Auto-create a SpineMesh GameObject if none assigned
                spineTarget = new GameObject("SpineMesh");
                Debug.Log("[HOARES] Created SpineMesh GameObject automatically.");
            }

            // Build Unity Mesh
            Mesh mesh = new Mesh
            {
                name = "HOARES_SpineMesh"
            };

            // Use 32-bit indices for large meshes (> 65535 verts)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices  = data.vertices;
            mesh.triangles = data.triangles;

            // Use Slicer normals if available, otherwise recalculate
            if (data.normals != null && data.normals.Length == data.vertices.Length)
                mesh.normals = data.normals;
            else
                mesh.RecalculateNormals();

            mesh.RecalculateBounds();

            // Assign or create MeshFilter
            var mf = spineTarget.GetComponent<MeshFilter>();
            if (mf == null) mf = spineTarget.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            // Assign or create MeshRenderer
            var mr = spineTarget.GetComponent<MeshRenderer>();
            if (mr == null) mr = spineTarget.AddComponent<MeshRenderer>();

            // Apply material
            if (spineMaterial != null)
            {
                mr.material = spineMaterial;
            }
            else
            {
                // Default: semi-transparent teal Standard material
                Material mat = new Material(Shader.Find("Standard"));
                mat.SetFloat("_Mode", 3); // Transparent
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
                mat.color = new Color(0.1f, 0.75f, 0.6f, 0.55f); // teal, 55% opacity
                mr.material = mat;
            }

            Debug.Log($"[HOARES] Spine mesh applied: " +
                      $"{data.vertices.Length} vertices, " +
                      $"{data.triangles.Length / 3} triangles. " +
                      $"Bounds: {mesh.bounds}");
        }

        // ── Binary read helpers (big-endian) ──────────────────
        private static uint ReadUInt32BE(byte[] b, int offset)
            => ((uint)b[offset] << 24) | ((uint)b[offset+1] << 16)
             | ((uint)b[offset+2] << 8) | b[offset+3];

        private static float ReadFloatBE(byte[] b, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                byte[] tmp = { b[offset+3], b[offset+2], b[offset+1], b[offset] };
                return BitConverter.ToSingle(tmp, 0);
            }
            return BitConverter.ToSingle(b, offset);
        }

        private static uint ReadUInt32LE(byte[] b, int offset)
            => ((uint)b[offset+3] << 24) | ((uint)b[offset+2] << 16)
             | ((uint)b[offset+1] << 8)  | b[offset];
    }
}