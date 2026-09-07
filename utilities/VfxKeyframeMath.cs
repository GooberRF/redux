using System;
using System.Collections.Generic;
using System.Numerics;

namespace redux.utilities
{
    // Evaluates the TCB keyframe lists a .vfx stores for keyframed meshes and chains. The vec3 keys
    // carry in/out handles the same way .rfa translation keys do, so they are treated as cubic
    // Bezier control points; rotation keys carry TCB parameters, which this evaluator does not
    // model - it slerps between them. Both are only used to produce the preview animation written
    // into glTF; the raw keys stay in the node extras and are what gets written back out.
    public static class VfxKeyframeMath
    {
        // Frame number * 320 is the key time unit used throughout the format.
        public const float TicksPerFrame = 320f;

        public static Vector3 EvaluateVec3(List<VfxVec3Key> keys, int time, Vector3 fallback)
        {
            if (keys == null || keys.Count == 0) return fallback;
            if (keys.Count == 1 || time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            int i = FindSegment(keys, time);
            VfxVec3Key a = keys[i];
            VfxVec3Key b = keys[i + 1];
            float span = b.Time - a.Time;
            if (span <= 0f) return a.Value;

            float u = (time - a.Time) / span;
            float iu = 1f - u;
            return iu * iu * iu * a.Value
                 + 3f * iu * iu * u * a.OutTangent
                 + 3f * iu * u * u * b.InTangent
                 + u * u * u * b.Value;
        }

        public static Quaternion EvaluateQuat(List<VfxQuatKey> keys, int time, Quaternion fallback)
        {
            if (keys == null || keys.Count == 0) return fallback;
            if (keys.Count == 1 || time <= keys[0].Time) return Normalize(keys[0].Value);
            if (time >= keys[^1].Time) return Normalize(keys[^1].Value);

            int i = FindSegment(keys, time);
            VfxQuatKey a = keys[i];
            VfxQuatKey b = keys[i + 1];
            float span = b.Time - a.Time;
            if (span <= 0f) return Normalize(a.Value);

            float u = (time - a.Time) / span;
            return Quaternion.Normalize(Quaternion.Slerp(Normalize(a.Value), Normalize(b.Value), u));
        }

        private static int FindSegment(List<VfxVec3Key> keys, int time)
        {
            for (int i = keys.Count - 2; i >= 0; i--)
                if (time >= keys[i].Time) return i;
            return 0;
        }

        private static int FindSegment(List<VfxQuatKey> keys, int time)
        {
            for (int i = keys.Count - 2; i >= 0; i--)
                if (time >= keys[i].Time) return i;
            return 0;
        }

        private static Quaternion Normalize(Quaternion q)
            => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
    }
}
