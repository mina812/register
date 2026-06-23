// ============================================================
//  IGTImageHandler.cs
//  HOARES — Milestone 2 Part B
//
//  Receives an IMAGE message from 3D Slicer and builds a
//  Unity Texture2D, displayed on a World Space Canvas panel.
//
//  OpenIGTLink IMAGE body layout (after stripping 14-byte
//  IGT v2 extended header):
//
//  SCALAR HEADER (72 bytes):
//  [2]  uint16  version
//  [1]  uint8   numComponents (1 = scalar/grayscale)
//  [1]  uint8   scalarType    (2=uint8, 3=uint16, 5=float)
//  [2]  uint16  endian        (1=big, 2=little)
//  [1]  uint8   coord         (1=LPS, 2=RAS)
//  [2]  uint16  size[0]       width  (i dimension)
//  [2]  uint16  size[1]       height (j dimension)
//  [2]  uint16  size[2]       depth  (k dimension, 1 for 2D)
//  [4]  float32 spacing[0]    mm per pixel X
//  [4]  float32 spacing[1]    mm per pixel Y
//  [4]  float32 spacing[2]    mm per pixel Z
//  [12] float32[3] origin     image origin (TX, TY, TZ)
//  [36] float32[9] direction  3x3 direction cosine matrix
//  PIXEL DATA:
//  width * height * bytesPerPixel bytes
//
//  SETUP
//  -----
//  1. Add IGTImageHandler to IGTManager
//  2. Create a World Space Canvas in scene
//  3. Add a RawImage child to Canvas, assign to ctSliceImage
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HOARES.IGT
{
    [System.Serializable]
    public class CTSliceSettings
    {
        public string deviceName;
        public bool   flipHorizontal;
        public bool   flipVertical;
    }

    [RequireComponent(typeof(IGTLinkConnector))]
    public class IGTImageHandler : MonoBehaviour
    {
        [Header("Display")]
        [Tooltip("RawImage UI component that will show the CT slice")]
        public RawImage ctSliceImage;

        [Tooltip("Canvas containing the CT slice panel")]
        public Canvas ctSliceCanvas;

        [Header("Settings")]
        [Tooltip("Scale of the CT panel in world space (metres)")]
        public float panelWorldSize = 0.3f;   // 30cm panel

        [Header("Panel Size")]
        [Tooltip("Fixed width of CT panels in world space (metres)")]
        public float panelWidthMetres  = 0.20f;  // 20cm panel width
        [Tooltip("Fixed height of CT panels in world space (metres) — used when image is taller than wide")]
        public float panelHeightMetres = 0.20f;  // 20cm panel height

        [Header("3D Quad Display")]
        [Tooltip("Drag the CTSlicePlane Quad here")]
        public MeshRenderer ctSliceQuad;
        public Material ctSliceMaterial;

        public MeshRenderer ctCoronalQuad;
        public Material ctCoronalMaterial;

        public MeshRenderer ctSagittalQuad;
        public Material ctSagittalMaterial;

        [Header("Flip Settings Per View")]
        public List<CTSliceSettings> sliceSettings = new List<CTSliceSettings>
        {
            new CTSliceSettings { deviceName = "HOARES_CT_Axial", flipHorizontal = true,  flipVertical = false },
            new CTSliceSettings { deviceName = "HOARES_CT_Cor",   flipHorizontal = false, flipVertical = true  },
            new CTSliceSettings { deviceName = "HOARES_CT_Sag",   flipHorizontal = false, flipVertical = false },
        };

        [Header("Debug")]
        public bool logOnReceive = true;

        // ── Thread-safe texture data queue ────────────────────
        private struct TextureData
        {
            public int     width, height;
            public byte[]  pixels;      // uint8 grayscale (numComp=1) or RGB interleaved (numComp=3)
            public byte    scalarType;
            public byte    numComponents; // 1=grayscale, 3=RGB
            public string  deviceName;
            public double  sendEpoch;    // Slicer send time (Unix epoch seconds)
            public long    receiveTicksUtc; // Unity receive time (UTC ticks)
        }
        private ConcurrentQueue<TextureData> _textureQueue = new();

        // ── Texture cache — destroy old textures to prevent memory leak ──
        private Dictionary<string, Texture2D> _textureCache = new();

        // ── Latency tracking (public — read by DebugHUD) ─────────
        /// <summary>Last measured end-to-end latency in milliseconds.</summary>
        public float LatencyMs       { get; private set; }
        /// <summary>Last measured queue latency (receive thread → main thread) in ms.</summary>
        public float QueueLatencyMs  { get; private set; }
        /// <summary>Images received per second.</summary>
        public float ImageFPS        { get; private set; }

        private int   _fpsCount;
        private float _fpsTimer;

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

        private void OnDestroy()
        {
            // Free all cached textures
            foreach (var tex in _textureCache.Values)
                if (tex != null) Destroy(tex);
            _textureCache.Clear();
        }

        private void Update()
        {
            // FPS counter
            _fpsTimer += Time.deltaTime;
            if (_fpsTimer >= 1f)
            {
                ImageFPS  = _fpsCount / _fpsTimer;
                _fpsCount = 0;
                _fpsTimer = 0f;
            }

            if (_textureQueue.TryDequeue(out TextureData td))
            {
                _fpsCount++;

                // Queue latency: receive thread → main thread
                long nowTicks = System.DateTime.UtcNow.Ticks;
                QueueLatencyMs = (float)(nowTicks - td.receiveTicksUtc) / TimeSpan.TicksPerMillisecond;

                // End-to-end latency: Slicer send → Unity display
                if (td.sendEpoch > 0)
                {
                    double nowMod = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) % 100000.0;
                    double diff = nowMod - td.sendEpoch;
                    if (diff < 0) diff += 100000.0; // handle wrap
                    LatencyMs = (float)(diff * 1000.0);
                }

                Debug.Log($"[HOARES] LATENCY '{td.deviceName}' " +
                          $"e2e={LatencyMs:F0}ms queue={QueueLatencyMs:F1}ms fps={ImageFPS:F1}");

                ApplyTexture(td);
            }
        }

        // ── Message handler ───────────────────────────────────
        private void HandleMessage(IGTMessage msg)
        {
            if (msg.Type != IGTMessageType.Image) return;
            if (msg.RawBody == null || msg.RawBody.Length < 72) return;

            if (logOnReceive)
                Debug.Log($"[HOARES] IMAGE '{msg.DeviceName}' " +
                          $"body={msg.RawBody.Length} bytes — parsing...");

            if (TryParseImage(msg.RawBody, out TextureData td))
            {
                td.deviceName      = msg.DeviceName;
                td.receiveTicksUtc = System.DateTime.UtcNow.Ticks;
                _textureQueue.Enqueue(td);
                Debug.Log($"[HOARES] CT slice parsed: {td.width}x{td.height} px " +
                          $"type={td.scalarType} — queued.");
            }
            else
            {
                Debug.LogWarning("[HOARES] IMAGE parse failed.");
            }
        }

        // ── IMAGE parser ──────────────────────────────────────
        private bool TryParseImage(byte[] body, out TextureData td)
        {
            td = default;
            try
            {
                var db = new System.Text.StringBuilder();
                for (int i = 0; i < 25; i++)
                    db.Append($"[{i}]={body[i]:X2} ");
                Debug.Log($"[HOARES] Full header: {db}");

                int offset = 0;

                byte scalarType = body[offset++]; // [0] = 3
                byte endian     = body[offset++]; // [1] = 2
                byte numComp    = body[offset++]; // [2] = 1 (grayscale) or 3 (RGB)
                bool le = (endian == 2);

                // Dimensions: big-endian uint16
                int width  = (body[offset] << 8) | body[offset+1]; offset += 2;
                int height = (body[offset] << 8) | body[offset+1]; offset += 2;
                int depth  = (body[offset] << 8) | body[offset+1]; offset += 2;

                // Read spacing (12 bytes) — skip
                offset += 12;

                // Read origin (12 bytes) — contains Slicer send timestamp
                // origin[0] = epoch % 100000 (fits in float32 precision)
                double sendEpoch = 0;
                if (offset + 12 <= body.Length)
                {
                    float originSecsMod = le ? BitConverter.ToSingle(body, offset)
                                             : BitConverter.ToSingle(new byte[]
                                                 {body[offset+3],body[offset+2],body[offset+1],body[offset]}, 0);
                    sendEpoch = originSecsMod; // epoch % 100000
                }
                offset += 12;

                // Skip direction matrix (36 bytes)
                offset += 36;

                int dataStart = offset;

                Debug.Log($"[HOARES] IMAGE: {width}x{height}x{depth} " +
                          $"type={scalarType} endian={endian} comp={numComp} dataOffset={dataStart}");

                if (width <= 0 || height <= 0)
                {
                    Debug.LogWarning("[HOARES] Invalid image dimensions");
                    return false;
                }

                int pixelCount  = width * height;
                int scalarBytes = scalarType == 10 ? 4 :
                                  (scalarType == 5 || scalarType == 6) ? 2 : 1;

                // depth==3 with 1 component means RGB encoded as 3 depth planes
                bool isRGB = (depth == 3 && numComp == 1 && scalarBytes == 1);
                int totalVoxels = isRGB ? pixelCount * 3 : pixelCount * numComp;

                Debug.Log($"[HOARES] dataStart={dataStart} isRGB={isRGB} " +
                          $"needed={totalVoxels * scalarBytes} available={body.Length - dataStart}");

                if (dataStart + totalVoxels * scalarBytes > body.Length)
                {
                    if (!isRGB)
                    {
                        int bytesPerPx = scalarBytes * numComp;
                        int rows = (body.Length - dataStart) / (width * bytesPerPx);
                        height     = Mathf.Max(1, rows);
                        pixelCount = width * height;
                        totalVoxels = pixelCount * numComp;
                    }
                    Debug.LogWarning($"[HOARES] Partial read: {height} rows");
                }

                byte[] pixels;

                if (isRGB)
                {
                    // Reconstruct interleaved RGB from 3 depth planes (R, G, B)
                    pixels = new byte[pixelCount * 3];
                    int planeSize = pixelCount * scalarBytes;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int rIdx = dataStart + i;
                        int gIdx = dataStart + planeSize + i;
                        int bIdx = dataStart + 2 * planeSize + i;
                        pixels[i * 3]     = (rIdx < body.Length) ? body[rIdx] : (byte)0;
                        pixels[i * 3 + 1] = (gIdx < body.Length) ? body[gIdx] : (byte)0;
                        pixels[i * 3 + 2] = (bIdx < body.Length) ? body[bIdx] : (byte)0;
                    }
                }
                else
                {
                    pixels = new byte[totalVoxels];
                    switch (scalarType)
                    {
                        case 2: // int8
                        case 3: // uint8
                            int copyLen = Mathf.Min(totalVoxels, body.Length - dataStart);
                            Array.Copy(body, dataStart, pixels, 0, copyLen);
                            break;

                        case 5: // int16
                        case 6: // uint16
                            for (int i = 0; i < totalVoxels; i++)
                            {
                                int idx = dataStart + i * 2;
                                if (idx + 2 > body.Length) break;
                                short v = le
                                    ? (short)(body[idx] | (body[idx+1]<<8))
                                    : (short)((body[idx]<<8) | body[idx+1]);
                                pixels[i] = (byte)(Mathf.Clamp01((v+500f)/1800f)*255f);
                            }
                            break;

                        case 10: // float32 HU
                            for (int i = 0; i < totalVoxels; i++)
                            {
                                int idx = dataStart + i * 4;
                                if (idx + 4 > body.Length) break;
                                float hu = le
                                    ? BitConverter.ToSingle(body, idx)
                                    : BitConverter.ToSingle(new byte[]
                                        {body[idx+3],body[idx+2],body[idx+1],body[idx]},0);
                                pixels[i] = (byte)(Mathf.Clamp01((hu+500f)/1800f)*255f);
                            }
                            break;

                        default:
                            Debug.LogWarning($"[HOARES] Unsupported type: {scalarType}");
                            return false;
                    }
                }

                td = new TextureData
                {
                    width         = width,
                    height        = height,
                    pixels        = pixels,
                    scalarType    = scalarType,
                    numComponents = isRGB ? (byte)3 : numComp,
                    sendEpoch     = sendEpoch
                };
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HOARES] IMAGE error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ── Apply texture to Quad and/or RawImage (main thread) ─
        private void ApplyTexture(TextureData td)
        {
            Debug.Log($"[HOARES] ApplyTexture: device={td.deviceName} " +
                      $"w={td.width} h={td.height} pixels={td.pixels?.Length}");

            // Destroy previous texture for this device to prevent memory leak
            if (_textureCache.TryGetValue(td.deviceName, out var oldTex) && oldTex != null)
                Destroy(oldTex);

            var tex = BuildTexture(td);
            _textureCache[td.deviceName] = tex;

            // Route to correct Material based on device name
            Material targetMat = td.deviceName switch
            {
                "HOARES_CT_Cor" => ctCoronalMaterial,
                "HOARES_CT_Sag" => ctSagittalMaterial,
                _               => ctSliceMaterial
            };

            if (targetMat != null)
            {
                targetMat.mainTexture = tex;
                Debug.Log($"[HOARES] CT applied to {td.deviceName}: {td.width}x{td.height}");
            }

            // Also apply to RawImage if present
            if (ctSliceImage != null)
                ctSliceImage.texture = tex;

            // Auto-adjust Quad scale to match image aspect ratio
            MeshRenderer targetQuad = td.deviceName switch
            {
                "HOARES_CT_Cor" => ctCoronalQuad,
                "HOARES_CT_Sag" => ctSagittalQuad,
                _               => ctSliceQuad
            };

            if (targetQuad != null)
            {
                float aspect = (float)td.width / td.height;
                float newX, newY;

                if (aspect >= 1f)  // wider than tall
                {
                    newX = panelWidthMetres;
                    newY = panelWidthMetres / aspect;
                }
                else  // taller than wide
                {
                    newY = panelHeightMetres;
                    newX = panelHeightMetres * aspect;
                }

                Vector3 s = targetQuad.transform.localScale;
                targetQuad.transform.localScale = new Vector3(newX, newY, s.z);
            }

            // Auto-position panels side by side with small gap
            float gap = 0.01f; // 1cm gap between panels

            if (ctSliceQuad != null && ctCoronalQuad != null && ctSagittalQuad != null)
            {
                float axialW    = ctSliceQuad.transform.localScale.x;
                float coronalW  = ctCoronalQuad.transform.localScale.x;
                float sagittalW = ctSagittalQuad.transform.localScale.x;

                float totalWidth = axialW + coronalW + sagittalW + gap * 2;
                float startX     = -totalWidth / 2f;

                // Position each panel
                Vector3 ap = ctSliceQuad.transform.localPosition;
                ctSliceQuad.transform.localPosition = new Vector3(
                    startX + axialW / 2f, ap.y, ap.z);

                Vector3 cp = ctCoronalQuad.transform.localPosition;
                ctCoronalQuad.transform.localPosition = new Vector3(
                    startX + axialW + gap + coronalW / 2f, cp.y, cp.z);

                Vector3 sp = ctSagittalQuad.transform.localPosition;
                ctSagittalQuad.transform.localPosition = new Vector3(
                    startX + axialW + gap + coronalW + gap + sagittalW / 2f, sp.y, sp.z);
            }
        }

        private Texture2D BuildTexture(TextureData td)
        {
            int pixelCount = td.width * td.height;
            int comp = Mathf.Max(1, td.numComponents);
            byte[] pixels = td.pixels;

            // Apply configurable flips from Inspector
            var setting = sliceSettings.Find(s => s.deviceName == td.deviceName);
            Debug.Log($"[HOARES] {td.deviceName}: {td.width}x{td.height} comp={comp} " +
                      $"flipH={setting?.flipHorizontal} flipV={setting?.flipVertical}");
            if (setting != null)
            {
                if (setting.flipHorizontal)
                    pixels = FlipHorizontal(pixels, td.width, td.height, comp);
                if (setting.flipVertical)
                    pixels = FlipVertical(pixels, td.width, td.height, comp);
            }

            var tex = new Texture2D(td.width, td.height, TextureFormat.RGB24, false);
            tex.filterMode = FilterMode.Bilinear;

            byte[] rgb;
            if (comp >= 3)
            {
                // RGB data — use directly
                int needed = pixelCount * 3;
                rgb = new byte[needed];
                int copyLen = Mathf.Min(needed, pixels.Length);
                Array.Copy(pixels, 0, rgb, 0, copyLen);
            }
            else
            {
                // Grayscale — expand to RGB
                rgb = new byte[pixelCount * 3];
                for (int i = 0; i < pixelCount && i < pixels.Length; i++)
                {
                    rgb[i*3] = rgb[i*3+1] = rgb[i*3+2] = pixels[i];
                }
            }

            tex.SetPixelData(rgb, 0);
            tex.Apply(false);
            return tex;
        }

        private byte[] FlipVertical(byte[] pixels, int width, int height, int comp = 1)
        {
            int stride = width * comp;
            byte[] flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                Array.Copy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            return flipped;
        }

        private byte[] FlipHorizontal(byte[] pixels, int width, int height, int comp = 1)
        {
            byte[] flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int srcOff = (y * width + x) * comp;
                    int dstOff = (y * width + (width - 1 - x)) * comp;
                    for (int c = 0; c < comp; c++)
                        flipped[dstOff + c] = pixels[srcOff + c];
                }
            return flipped;
        }

    }
}