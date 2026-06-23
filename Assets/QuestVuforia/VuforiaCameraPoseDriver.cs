using Meta.XR;
using UnityEngine;

/// <summary>
/// Drives this GameObject's transform to the PHYSICAL passthrough camera world pose
/// every frame (position + rotation, including the camera-to-head lens offset/tilt).
/// Runs in LateUpdate (after OVR has updated the head/eye poses) so the camera pose
/// for the current frame is current.
/// </summary>
[DefaultExecutionOrder(100)]
public class VuforiaCameraPoseDriver : MonoBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool logPose = false;

    private PassthroughCameraAccess cameraAccess;
    private int frameCount;

    private void Awake()
    {
        cameraAccess = GetComponent<MetaCameraProvider>().CameraAccess;
    }

    private void LateUpdate()
    {
        if (cameraAccess == null || !cameraAccess.IsPlaying)
        {
            return;
        }
        // GetCameraPose() returns the WORLD-space pose of the physical passthrough cam
        Pose camPose = cameraAccess.GetCameraPose();
        transform.SetPositionAndRotation(camPose.position, camPose.rotation);

        if (logPose && (++frameCount % 30 == 0))
        {
            Debug.Log($"[Quforia] VuforiaCameraPoseDriver -> camPose pos=" +
                      $"({camPose.position.x:F3},{camPose.position.y:F3},{camPose.position.z:F3})");
        }
    }
}
