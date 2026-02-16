using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace redux.exporters
{
    public static class RfaExporter
    {
        private const string logSrc = "RfaExporter";
        private const int Magic = 0x46564D56; // VMVF
        private const int Version = 8;
        private const float ShortQuatScale = 16383f;

        public static void ExportRfa(RfaFile file, string outputPath)
        {
            if (file.Bones == null)
                file.Bones = new List<RfaBone>();

            int numBones = file.Bones.Count;
            int headerSize = 80 + (numBones * 4);

            int[] boneOffsets = new int[numBones];
            int runningOffset = headerSize;
            for (int i = 0; i < numBones; i++)
            {
                boneOffsets[i] = runningOffset;
                runningOffset += ComputeBoneSize(file.Bones[i]);
            }

            var header = file.Header ?? new RfaHeader();
            header.Magic = BitConverter.GetBytes(Magic);
            header.Version = header.Version == 7 ? 7 : Version;
            header.PosReduction = header.PosReduction;
            header.RotReduction = header.RotReduction;
            header.NumBones = numBones;
            header.NumMorphVertices = 0;
            header.NumMorphKeyframes = 0;
            header.MorphVertexMappingsOffset = 0;
            header.MorphVertexDataOffset = 0;
            header.BoneOffsets = boneOffsets;

            if (header.TotalRotation == default)
                header.TotalRotation = Quaternion.Identity;

            file.Header = header;

            if (header.EndTime <= header.StartTime)
            {
                (int minTime, int maxTime) = GetAnimationTimeBounds(file.Bones);
                header.StartTime = minTime;
                header.EndTime = maxTime;
            }

            using var writer = new BinaryWriter(File.Create(outputPath));
            WriteHeader(writer, header);

            for (int i = 0; i < numBones; i++)
                WriteBone(writer, file.Bones[i]);

            Logger.Info(logSrc, $"Wrote RFA: {outputPath} ({numBones} bones)");
        }

        private static (int min, int max) GetAnimationTimeBounds(List<RfaBone> bones)
        {
            int min = int.MaxValue;
            int max = int.MinValue;

            foreach (var bone in bones)
            {
                if (bone.RotationKeys != null)
                {
                    foreach (var rk in bone.RotationKeys)
                    {
                        if (rk.Time < min) min = rk.Time;
                        if (rk.Time > max) max = rk.Time;
                    }
                }

                if (bone.TranslationKeys != null)
                {
                    foreach (var tk in bone.TranslationKeys)
                    {
                        if (tk.Time < min) min = tk.Time;
                        if (tk.Time > max) max = tk.Time;
                    }
                }
            }

            if (min == int.MaxValue || max == int.MinValue)
                return (0, 0);

            return (min, max);
        }

        private static int ComputeBoneSize(RfaBone bone)
        {
            int rotCount = bone.RotationKeys?.Count ?? 0;
            int transCount = bone.TranslationKeys?.Count ?? 0;
            return 8 + (rotCount * 16) + (transCount * 40);
        }

        private static void WriteHeader(BinaryWriter w, RfaHeader h)
        {
            w.Write(Magic);
            w.Write(h.Version == 7 ? 7 : Version);
            w.Write(h.PosReduction);
            w.Write(h.RotReduction);
            w.Write(h.StartTime);
            w.Write(h.EndTime);
            w.Write(h.NumBones);
            w.Write(0); // num morph vertices
            w.Write(0); // num morph keyframes
            w.Write(h.RampInTime);
            w.Write(h.RampOutTime);

            w.Write(h.TotalRotation.X);
            w.Write(h.TotalRotation.Y);
            w.Write(h.TotalRotation.Z);
            w.Write(h.TotalRotation.W);

            w.Write(h.TotalTranslation.X);
            w.Write(h.TotalTranslation.Y);
            w.Write(h.TotalTranslation.Z);

            w.Write(0); // morph vertex mapping offset
            w.Write(0); // morph vertex data offset

            for (int i = 0; i < h.NumBones; i++)
                w.Write(h.BoneOffsets[i]);
        }

        private static void WriteBone(BinaryWriter w, RfaBone bone)
        {
            var rotKeys = bone.RotationKeys ?? new List<RfaRotationKey>();
            var transKeys = bone.TranslationKeys ?? new List<RfaTranslationKey>();

            w.Write(bone.Weight);
            w.Write((short)rotKeys.Count);
            w.Write((short)transKeys.Count);

            for (int i = 0; i < rotKeys.Count; i++)
            {
                var k = rotKeys[i];
                Quaternion q = k.Rotation;
                if (q.LengthSquared() > 1e-8f)
                    q = Quaternion.Normalize(q);
                else
                    q = Quaternion.Identity;

                w.Write(k.Time);
                w.Write(FloatToShortQuat(q.X));
                w.Write(FloatToShortQuat(q.Y));
                w.Write(FloatToShortQuat(q.Z));
                w.Write(FloatToShortQuat(q.W));
                w.Write(k.EaseIn);
                w.Write(k.EaseOut);
                w.Write((short)0);
            }

            for (int i = 0; i < transKeys.Count; i++)
            {
                var k = transKeys[i];
                w.Write(k.Time);

                w.Write(k.Translation.X);
                w.Write(k.Translation.Y);
                w.Write(k.Translation.Z);

                w.Write(k.InTangent.X);
                w.Write(k.InTangent.Y);
                w.Write(k.InTangent.Z);

                w.Write(k.OutTangent.X);
                w.Write(k.OutTangent.Y);
                w.Write(k.OutTangent.Z);
            }
        }

        private static short FloatToShortQuat(float v)
        {
            int q = (int)MathF.Round(v * ShortQuatScale);
            if (q < short.MinValue) q = short.MinValue;
            if (q > short.MaxValue) q = short.MaxValue;
            return (short)q;
        }
    }
}
