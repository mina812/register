using System;
using System.Collections;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// Provides camera frames and device poses to Vuforia Driver Framework.
/// Uses Meta Quest's PassthroughCameraAccess for frame capture.
/// </summary>
[DefaultExecutionOrder(-50)]
public class MetaCameraProvider : MonoBehaviour
{
    [Header("Camera Access")]
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    public PassthroughCameraAccess CameraAccess => cameraAccess;

    [Header("Settings")]
    [SerializeField] private bool autoStart = true;
    [SerializeField] private bool flipImageVertically = true;
    [SerializeField] private bool useCameraRotation = true;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = false;
    [SerializeField] private bool showFrameStats = false;
    [SerializeField] private bool showPoseDebug = false;
    [SerializeField] private float statsInterval = 1.0f;

    private byte[] imageDataRGB;
    private bool isRunning = false;
    private int frameCount = 0;
    private int width, height;
    private float[] cachedIntrinsics;

    // Frame stats
    private float lastStatsTime;
    private int framesProcessed;

    private void Start()
    {
        if (cameraAccess == null)
        {
            Debug.LogError("[Quforia] PassthroughCameraAccess not assigned!");
            return;
        }

        // Request camera permission
        if (!Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
        }

        if (autoStart)
        {
            StartCoroutine(InitializeCamera());
        }
    }

    public IEnumerator InitializeCamera()
    {
        if (isRunning) yield break;

        Log("Initializing camera...");

        if (!cameraAccess.enabled)
        {
            cameraAccess.enabled = true;
            yield return null;
        }

        // Wait for camera to start (10s timeout)
        float elapsed = 0f;
        while (!cameraAccess.IsPlaying && elapsed < 10f)
        {
            yield return null;
            elapsed += Time.deltaTime;
        }

        if (!cameraAccess.IsPlaying)
        {
            Debug.LogError("[Quforia] Camera failed to start!");
            yield break;
        }

        // Get resolution and allocate buffer
        Vector2Int resolution = cameraAccess.CurrentResolution;
        width = resolution.x;
        height = resolution.y;
        imageDataRGB = new byte[width * height * 3];

        var sensorRes = cameraAccess.Intrinsics.SensorResolution;
        Log($"Camera initialized: Current={width}x{height}, Sensor={sensorRes.x}x{sensorRes.y}");
        if (width != sensorRes.x || height != sensorRes.y)
        {
            Log($"CurrentResolution ({width}x{height}) is a crop of SensorResolution ({sensorRes.x}x{sensorRes.y}); " +
                "crop offset is accounted for in SetupCameraIntrinsics().");
        }

        // Setup intrinsics
        SetupCameraIntrinsics();

        isRunning = true;
        lastStatsTime = Time.time;
        StartCoroutine(ProcessFrames());
    }

    private void SetupCameraIntrinsics()
    {
        try
        {
            var intrinsics = cameraAccess.Intrinsics;
            var sensorRes = intrinsics.SensorResolution;


            float sfX = (float)width / sensorRes.x;
            float sfY = (float)height / sensorRes.y;
            float norm = Mathf.Max(sfX, sfY);
            float cropScaleX = sfX / norm;
            float cropScaleY = sfY / norm;
            float cropOffsetX = sensorRes.x * (1f - cropScaleX) * 0.5f;
            float cropOffsetY = sensorRes.y * (1f - cropScaleY) * 0.5f;
            float cropWidth = sensorRes.x * cropScaleX;
            float s = width / cropWidth;   // crop-region -> output scale (== height/cropHeight)

            float fx = intrinsics.FocalLength.x * s;
            float fy = intrinsics.FocalLength.y * s;                       
            float cx = (intrinsics.PrincipalPoint.x - cropOffsetX) * s;
            float cyImg = (intrinsics.PrincipalPoint.y - cropOffsetY) * s; 

            cachedIntrinsics = new float[14];
            cachedIntrinsics[0] = width;
            cachedIntrinsics[1] = height;
            cachedIntrinsics[2] = fx;
            cachedIntrinsics[3] = fy;
            cachedIntrinsics[4] = cx;

            cachedIntrinsics[5] = flipImageVertically ? (height - cyImg) : cyImg;
            
            Debug.Log($"[Quforia] RAW Meta: sensor={sensorRes.x}x{sensorRes.y} cur={width}x{height} " +
                $"f=({intrinsics.FocalLength.x:F1},{intrinsics.FocalLength.y:F1}) " +
                $"pp=({intrinsics.PrincipalPoint.x:F1},{intrinsics.PrincipalPoint.y:F1})");

            QuestVuforiaBridge.SetCameraIntrinsics(cachedIntrinsics);

            Debug.Log($"[Quforia] Vuforia intr: fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cachedIntrinsics[5]:F1} " +
                $"cropOff=({cropOffsetX:F0},{cropOffsetY:F0}) s={s:F3} flip={flipImageVertically} " +
                $"(expect fy~=fx, cy~={height * 0.5f:F0})");
            
        }
        catch (Exception e)
        {
            Debug.LogError($"[Quforia] Failed to get intrinsics: {e.Message}");
        }
    }

    private IEnumerator ProcessFrames()
    {
        while (isRunning)
        {
            // Only process when the camera delivered a new image (PCA: 60Hz),
            // avoiding redundant pixel conversions and duplicate frames to Vuforia.
            if (cameraAccess.IsPlaying && cameraAccess.IsUpdatedThisFrame)
            {
                try
                {
                    ProcessCurrentFrame();
                    framesProcessed++;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Quforia] Frame processing error: {e.Message}");
                }
            }

            // Stats logging
            if (showFrameStats && Time.time - lastStatsTime >= statsInterval)
            {
                float fps = framesProcessed / (Time.time - lastStatsTime);
                Log($"Processing: {fps:F1} FPS | Total: {frameCount}");
                lastStatsTime = Time.time;
                framesProcessed = 0;
            }

            yield return null;
        }
    }

    private void ProcessCurrentFrame()
    {
        // Get camera frame pixels
        NativeArray<Color32> pixels = cameraAccess.GetColors();

        int pixelCount = width * height;
        if (!pixels.IsCreated || pixels.Length < pixelCount)
        {
            return;
        }

        // Convert Color32 to RGB888 (only the valid leading pixelCount pixels)
        for (int i = 0; i < pixelCount; i++)
        {
            int rgbIndex = i * 3;
            Color32 pixel = pixels[i];
            imageDataRGB[rgbIndex] = pixel.r;
            imageDataRGB[rgbIndex + 1] = pixel.g;
            imageDataRGB[rgbIndex + 2] = pixel.b;
        }

        // Flip Y-axis if needed
        if (flipImageVertically)
        {
            FlipImageVertically(imageDataRGB, width, height);
        }

        // Get synchronized timestamp and pose
        DateTime currentTime = DateTime.Now;
        long timestampNs = currentTime.Ticks * 100;
        Pose cameraPose = cameraAccess.GetCameraPose();

        // Choose rotation based on setting
        Quaternion rotation = useCameraRotation ? cameraPose.rotation : Quaternion.identity;

        // Debug pose info
        if (showPoseDebug && frameCount % 30 == 0)
        {
            Quaternion deltaRot = Quaternion.Inverse(transform.rotation) * cameraPose.rotation;
            Vector3 deltaEul = deltaRot.eulerAngles;
            float deltaPos = Vector3.Distance(transform.position, cameraPose.position);
            Debug.Log($"[Quforia] CAM vs EYE: " +
                     $"camPos=({cameraPose.position.x:F3},{cameraPose.position.y:F3},{cameraPose.position.z:F3}) " +
                     $"eyePos=({transform.position.x:F3},{transform.position.y:F3},{transform.position.z:F3}) " +
                     $"deltaPos={deltaPos:F3}m deltaRotEul=({deltaEul.x:F1},{deltaEul.y:F1},{deltaEul.z:F1})");
        }

        // Feed to Vuforia (pose first, then frame with same timestamp)
        QuestVuforiaBridge.FeedDevicePose(cameraPose.position, rotation, timestampNs);
        QuestVuforiaBridge.FeedCameraFrame(imageDataRGB, width, height, null, timestampNs);

        frameCount++;
    }

    private void FlipImageVertically(byte[] imageData, int width, int height)
    {
        int stride = width * 3;
        byte[] rowBuffer = new byte[stride];

        for (int row = 0; row < height / 2; row++)
        {
            int topRow = row * stride;
            int bottomRow = (height - 1 - row) * stride;

            Array.Copy(imageData, topRow, rowBuffer, 0, stride);
            Array.Copy(imageData, bottomRow, imageData, topRow, stride);
            Array.Copy(rowBuffer, 0, imageData, bottomRow, stride);
        }
    }

    public void StopCamera()
    {
        if (!isRunning) return;

        isRunning = false;
        if (cameraAccess != null && cameraAccess.enabled)
        {
            cameraAccess.enabled = false;
        }
        Log("Camera stopped");
    }

    private void OnDestroy() => StopCamera();

    private void OnApplicationPause(bool isPaused)
    {
        if (cameraAccess == null) return;

        if (isPaused && cameraAccess.enabled)
        {
            cameraAccess.enabled = false;
        }
        else if (!isPaused && isRunning && !cameraAccess.enabled)
        {
            cameraAccess.enabled = true;
        }
    }

    private void Log(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[Quforia] {message}");
        }
    }
}
