// ============================================================
//  IGTTransformHandler.cs
//  HOARES — Milestone 2.5 Update
//
//  Subscribes to IGTLinkConnector.OnMessageReceived and applies
//  incoming TRANSFORM messages to registered GameObjects.
//
//  M2.5 Fix #5 — Spine model not upright / synced with Slicer
//  ─────────────────────────────────────────────────────────────
//  Added  baseRotationOffset (Vector3 Euler angles) to TransformEntry.
//
//  When Slicer sends an identity transform, the spine model now
//  stands upright in Unity by combining the incoming Slicer rotation
//  with the fixed offset:
//
//      finalRotation = incomingRotation × Quaternion.Euler(baseRotationOffset)
//
//  For the HOARES_SpineRef entry, set baseRotationOffset = (90, 0, 180):
//    • X = 90  corrects OBJ Y-up export to Z-up surgical orientation
//    • Z = 180 flips the model to face the correct anterior direction
//
//  SETUP
//  -----
//  1. Create an empty GameObject → name it "IGTManager"
//  2. Add IGTLinkConnector component to it
//  3. Add IGTTransformHandler component to it
//  4. In targetEntries, drag "SpineModel" to 'target'
//     Set deviceName = "HOARES_SpineRef"
//     Set baseRotationOffset = (90, 0, 180)
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HOARES.IGT
{
    [Serializable]
    public class TransformEntry
    {
        [Tooltip("Must EXACTLY match the Slicer LinearTransformNode name")]
        public string    deviceName;

        [Tooltip("The GameObject whose position/rotation will be driven")]
        public Transform target;

        [Tooltip("Scale: Slicer uses mm, Unity uses metres by default")]
        public float     unitScale = 0.001f;   // mm → m

        [Tooltip("Smooth the incoming transform (0 = instant, 1 = frozen)")]
        [Range(0f, 0.99f)]
        public float     smoothing = 0.1f;

        // ── M2.5 Fix #5 ──────────────────────────────────────────
        [Tooltip(
            "M2.5 Fix #5 — Static Euler rotation offset applied ON TOP of the\n" +
            "incoming Slicer transform to correct for OBJ coordinate convention.\n" +
            "For HOARES_SpineRef set to (90, 0, 180):\n" +
            "  X=90  — OBJ Y-up → surgical Z-up\n" +
            "  Z=180 — flip anterior face to correct direction")]
        public Vector3   baseRotationOffset = Vector3.zero;
    }

    [RequireComponent(typeof(IGTLinkConnector))]
    public class IGTTransformHandler : MonoBehaviour
    {
        [Header("Tracked Objects")]
        public List<TransformEntry> targetEntries = new();

        [Header("Debug")]
        public bool logIncomingMessages = true;

        // Fast lookup: deviceName → entry
        private Dictionary<string, TransformEntry> _lookup = new();

        // Last received values for smoothing
        private Dictionary<string, (Vector3 pos, Quaternion rot)> _current = new();

        // ── Unity lifecycle ───────────────────────────────────────
        private void OnEnable()
        {
            RebuildLookup();
            GetComponent<IGTLinkConnector>().OnMessageReceived += HandleMessage;
        }

        private void OnDisable()
        {
            var c = GetComponent<IGTLinkConnector>();
            if (c) c.OnMessageReceived -= HandleMessage;
        }

        private void Update()
        {
            // Apply smoothed transforms on main thread
            foreach (var entry in targetEntries)
            {
                if (entry.target == null) continue;
                if (!_current.TryGetValue(entry.deviceName, out var cv)) continue;

                // ── M2.5 Fix #5 ───────────────────────────────────
                // Combine incoming Slicer rotation with the static base
                // rotation offset that corrects for OBJ/Slicer orientation.
                // The offset is applied in LOCAL model space (right-multiply),
                // so it acts as a permanent pose correction regardless of
                // what Slicer sends.
                Quaternion baseRot     = Quaternion.Euler(entry.baseRotationOffset);
                Quaternion targetRot   = cv.rot * baseRot;
                // ─────────────────────────────────────────────────

                entry.target.localPosition = Vector3.Lerp(
                    entry.target.localPosition,
                    cv.pos * entry.unitScale,
                    1f - entry.smoothing
                );

                entry.target.localRotation = Quaternion.Slerp(
                    entry.target.localRotation,
                    targetRot,
                    1f - entry.smoothing
                );
            }
        }

        // ── Message handler (called on main thread via queue) ─────
        private void HandleMessage(IGTMessage msg)
        {
            if (msg.Type != IGTMessageType.Transform) return;

            if (logIncomingMessages)
                Debug.Log($"[HOARES] TRANSFORM '{msg.DeviceName}' " +
                          $"pos={msg.Position} rot={msg.Rotation.eulerAngles}");

            // Update current value dict (smoothing applied in Update)
            _current[msg.DeviceName] = (msg.Position, msg.Rotation);

            if (!_lookup.ContainsKey(msg.DeviceName))
                Debug.LogWarning(
                    $"[HOARES] Received transform for unregistered device " +
                    $"'{msg.DeviceName}'. Add it to targetEntries in the Inspector.");
        }

        // ── Helpers ───────────────────────────────────────────────
        private void RebuildLookup()
        {
            _lookup.Clear();
            foreach (var e in targetEntries)
                if (!string.IsNullOrEmpty(e.deviceName))
                    _lookup[e.deviceName] = e;
        }

        /// <summary>
        /// Convenience: register a target at runtime (e.g. after instantiating a prefab).
        /// </summary>
        public void RegisterTarget(
            string    deviceName,
            Transform target,
            float     unitScale           = 0.001f,
            float     smoothing           = 0.1f,
            Vector3?  baseRotationOffset  = null)
        {
            var entry = new TransformEntry
            {
                deviceName          = deviceName,
                target              = target,
                unitScale           = unitScale,
                smoothing           = smoothing,
                baseRotationOffset  = baseRotationOffset ?? Vector3.zero
            };
            targetEntries.Add(entry);
            _lookup[deviceName] = entry;
        }

        // ── M2.5 Fix #5 convenience method ───────────────────────
        /// <summary>
        /// Register the spine model with the correct M2.5 rotation correction
        /// (X=90, Y=0, Z=180) applied automatically.
        /// </summary>
        public void RegisterSpineModel(Transform spineTransform, float unitScale = 0.001f)
        {
            RegisterTarget(
                deviceName          : "HOARES_SpineRef",
                target              : spineTransform,
                unitScale           : unitScale,
                smoothing           : 0.08f,
                baseRotationOffset  : new Vector3(90f, 0f, 180f)
            );
        }
    }
}