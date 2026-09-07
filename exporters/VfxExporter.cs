using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace redux.exporters
{
    // Writes a VfxFile back out as a version 0x40006 .vfx. Every allocation count in the header is
    // recomputed from the section contents, and each chunk length counts its own 4-byte field.
    public static class VfxExporter
    {
        private const string logSrc = "VfxExporter";

        public static void ExportVfx(VfxFile file, string outputPath)
        {
            // A .vfx that breaks any of these invariants makes RF dereference a null vertex record
            // the first time it draws the effect, so nothing is written until they all hold.
            List<string> problems = VfxValidation.Validate(file);
            if (problems.Count > 0)
            {
                Logger.Error(logSrc, $"Refusing to write \"{outputPath}\": the model would crash Red Faction.");
                foreach (string problem in problems)
                    Logger.Error(logSrc, "  " + problem);
                throw new InvalidDataException($"VFX model failed validation ({problems.Count} problem(s)); nothing was written.");
            }

            Logger.Info(logSrc, $"Writing VFX to: {outputPath}");

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                WriteHeader(w, file);
                foreach (VfxSection section in file.Sections)
                    WriteSection(w, section);
            }

            string dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, ms.ToArray());

            var counts = VfxCounts.From(file);
            Logger.Info(logSrc,
                $"VFX export complete: {counts.Meshes} mesh, {counts.Materials} material, {counts.ParticleSystems} particle, " +
                $"{counts.Dummies} dummy, {counts.Spacewarps} warp, {counts.Lights} light sections; " +
                $"{counts.Faces} faces, {counts.MeshFrames} mesh frames.");
        }

        // ─── header ────────────────────────────────────────────────────────────────────────────

        private sealed class VfxCounts
        {
            public int Meshes, Lights, Dummies, ParticleSystems, Spacewarps, Cameras, SelSets, Materials;
            public int MixFrames, SelfIlluminationFrames, OpacityFrames;
            public int Faces, MeshMaterialIndices, VertexNormals, AdjacentFaces, MeshFrames, UvFrames;
            public int MeshTransformFrames, MeshTransformKeyframeLists, TranslationKeys, RotationKeys, ScaleKeys;
            public int LightFrames, DummyFrames, PartSysFrames, SpacewarpFrames, CameraFrames;

            public static VfxCounts From(VfxFile file)
            {
                var c = new VfxCounts();
                foreach (VfxSection s in file.Sections)
                {
                    switch (s)
                    {
                        case VfxMesh m:
                            // num_meshes counts meshes and chains together: both allocate a chunk slot.
                            c.Meshes++;
                            c.Faces += m.Faces.Count;
                            c.MeshMaterialIndices += m.MaterialIndices.Count;
                            c.VertexNormals += m.FaceVertices.Count;
                            foreach (VfxFaceVertex fv in m.FaceVertices)
                                c.AdjacentFaces += fv.AdjacentFaces.Count;
                            c.MeshFrames += m.Frames.Count;
                            // Mirror WriteMesh exactly: a transform is written for every frame of a
                            // mesh that is neither morphed nor keyframed, and for no other frame.
                            if (!m.Morph && !m.IsKeyframed)
                                c.MeshTransformFrames += m.Frames.Count;
                            for (int i = 0; i < m.Frames.Count; i++)
                            {
                                if (m.DumpUvs || i == 0) c.UvFrames++;
                            }
                            if (m.IsKeyframed)
                            {
                                c.MeshTransformKeyframeLists++;
                                c.TranslationKeys += m.TranslationKeys.Count;
                                c.RotationKeys += m.RotationKeys.Count;
                                c.ScaleKeys += m.ScaleKeys.Count;
                            }
                            break;
                        case VfxChain:
                            c.Meshes++;
                            break;
                        case VfxMaterial mat:
                            c.Materials++;
                            c.MixFrames += mat.MixFrames.Count;
                            c.SelfIlluminationFrames += mat.SelfIllumination.Count;
                            c.OpacityFrames += mat.Opacity.Count;
                            break;
                        case VfxParticleSystem p:
                            c.ParticleSystems++;
                            c.PartSysFrames += p.Frames.Count;
                            break;
                        case VfxDummy d:
                            c.Dummies++;
                            c.DummyFrames += d.Frames.Count;
                            break;
                        case VfxLight l:
                            c.Lights++;
                            c.LightFrames += l.Frames.Count;
                            break;
                        case VfxSpacewarp sw:
                            c.Spacewarps++;
                            c.SpacewarpFrames += sw.Frames.Count;
                            break;
                        case VfxCamera cam:
                            c.Cameras++;
                            c.CameraFrames += cam.Frames.Count;
                            break;
                        case VfxUnknownSection u when u.RawTypeId == VfxSectionType.SelSet:
                            c.SelSets++;
                            break;
                    }
                }
                return c;
            }
        }

        private static void WriteHeader(BinaryWriter w, VfxFile file)
        {
            VfxCounts c = VfxCounts.From(file);

            w.Write((byte)'V'); w.Write((byte)'S'); w.Write((byte)'F'); w.Write((byte)'X');
            w.Write(VfxFile.CurrentVersion);
            w.Write(file.HeaderFlags);
            w.Write(file.EndFrame);
            w.Write(c.Meshes);
            w.Write(c.Lights);
            w.Write(c.Dummies);
            w.Write(c.ParticleSystems);
            w.Write(c.Spacewarps);
            w.Write(c.Cameras);
            w.Write(c.SelSets);
            w.Write(c.Materials);
            w.Write(c.MixFrames);
            w.Write(c.SelfIlluminationFrames);
            w.Write(c.OpacityFrames);
            w.Write(c.Faces);
            w.Write(c.MeshMaterialIndices);
            w.Write(c.VertexNormals);
            w.Write(c.AdjacentFaces);
            w.Write(c.MeshFrames);
            w.Write(c.UvFrames);
            w.Write(c.MeshTransformFrames);
            w.Write(c.MeshTransformKeyframeLists);
            w.Write(c.TranslationKeys);
            w.Write(c.RotationKeys);
            w.Write(c.ScaleKeys);
            w.Write(c.LightFrames);
            w.Write(c.DummyFrames);
            w.Write(c.PartSysFrames);
            w.Write(c.SpacewarpFrames);
            w.Write(c.CameraFrames);
            w.Write(file.SelSetObjectCount);
        }

        // ─── sections ──────────────────────────────────────────────────────────────────────────

        private static void WriteSection(BinaryWriter w, VfxSection section)
        {
            Stream s = w.BaseStream;
            w.Write(section.TypeId);
            long lenPos = s.Position;
            w.Write(0); // placeholder
            long bodyStart = s.Position;

            switch (section)
            {
                case VfxMesh m: WriteMesh(w, m); break;
                case VfxMaterial mat: WriteMaterial(w, mat); break;
                case VfxParticleSystem p: WriteParticleSystem(w, p); break;
                case VfxDummy d: WriteDummy(w, d); break;
                case VfxLight l: WriteLight(w, l); break;
                case VfxSpacewarp sw: WriteSpacewarp(w, sw); break;
                case VfxChain ch: WriteChain(w, ch); break;
                case VfxCamera cam: WriteCamera(w, cam); break;
                case VfxMaterialModifier mm: w.Write(mm.MaterialIndex); break;
                case VfxUnknownSection u: w.Write(u.Data); break;
                default: throw new InvalidDataException($"Unhandled VFX section type {section.GetType().Name}.");
            }

            long end = s.Position;
            // The stored length includes the length field itself.
            int len = (int)(end - bodyStart) + 4;
            s.Position = lenPos;
            w.Write(len);
            s.Position = end;
        }

        private static void WriteCString(BinaryWriter w, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                foreach (char ch in value)
                    w.Write((byte)ch);
            }
            w.Write((byte)0);
        }

        private static void WriteVec3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
        }

        private static void WriteQuat(BinaryWriter w, Quaternion q)
        {
            w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
        }

        private static void WriteVec3Key(BinaryWriter w, VfxVec3Key k)
        {
            w.Write(k.Time);
            WriteVec3(w, k.Value);
            WriteVec3(w, k.InTangent);
            WriteVec3(w, k.OutTangent);
        }

        private static void WriteQuatKey(BinaryWriter w, VfxQuatKey k)
        {
            w.Write(k.Time);
            WriteQuat(w, k.Value);
            w.Write(k.Tension);
            w.Write(k.Continuity);
            w.Write(k.Bias);
            w.Write(k.EaseIn);
            w.Write(k.EaseOut);
        }

        private static void WriteTexture(BinaryWriter w, VfxTexture? t)
        {
            t ??= new VfxTexture();
            WriteCString(w, t.Name);
            w.Write(t.StartFrame);
            w.Write(t.PlaybackRate);
            w.Write(t.AnimType);
        }

        private static void WriteMesh(BinaryWriter w, VfxMesh m)
        {
            WriteCString(w, m.Name);
            WriteCString(w, m.ParentName);
            w.Write((byte)(m.SaveParent ? 1 : 0));
            w.Write(m.VertexCount);

            w.Write(m.Faces.Count);
            foreach (VfxFace f in m.Faces)
            {
                w.Write(f.Indices[0]); w.Write(f.Indices[1]); w.Write(f.Indices[2]);
                for (int i = 0; i < 3; i++) WriteVec3(w, f.Colors[i]);
                WriteVec3(w, f.Normal);
                WriteVec3(w, f.Center);
                w.Write(f.Radius);
                w.Write(f.MaterialIndex);
                w.Write(f.SmoothingGroup);
                w.Write(f.FaceVertexIndices[0]); w.Write(f.FaceVertexIndices[1]); w.Write(f.FaceVertexIndices[2]);
            }

            w.Write(m.FramesPerSecond);
            w.Write(m.StartTime);
            w.Write(m.EndTime);
            w.Write(m.Frames.Count);

            w.Write(m.MaterialIndices.Count);
            foreach (int idx in m.MaterialIndices) w.Write(idx);

            WriteVec3(w, m.BoundingCenter);
            w.Write(m.BoundingRadius);
            w.Write(m.Flags);

            w.Write(m.FaceVertices.Count);
            foreach (VfxFaceVertex fv in m.FaceVertices)
            {
                w.Write(fv.SmoothingGroup);
                w.Write(fv.VertexIndex);
                w.Write(fv.URaw);
                w.Write(fv.VRaw);
                w.Write(fv.AdjacentFaces.Count);
                foreach (int a in fv.AdjacentFaces) w.Write(a);
            }

            w.Write((byte)(m.IsKeyframed ? 1 : 0));

            for (int i = 0; i < m.Frames.Count; i++)
            {
                VfxMeshFrame fr = m.Frames[i];
                bool storesGeometry = m.Morph || i == 0;
                if (storesGeometry)
                {
                    WriteVec3(w, fr.Center);
                    WriteVec3(w, fr.PositionsMultiplier);
                    short[] raw = fr.RawPositions;
                    int expected = m.VertexCount * 3;
                    for (int j = 0; j < expected; j++)
                        w.Write(j < raw.Length ? raw[j] : (short)0);

                    if (m.Facing || m.FacingRod)
                    {
                        w.Write(fr.Width);
                        w.Write(fr.Height);
                    }
                    if (m.FacingRod && i == 0)
                        WriteVec3(w, fr.UpVector);
                }

                if (m.DumpUvs || i == 0)
                {
                    int expected = 3 * m.Faces.Count;
                    for (int j = 0; j < expected; j++)
                    {
                        Vector2 uv = j < fr.Uvs.Length ? fr.Uvs[j] : Vector2.Zero;
                        w.Write(uv.X); w.Write(uv.Y);
                    }
                }

                if (!m.Morph && !m.IsKeyframed)
                {
                    WriteVec3(w, fr.Translation);
                    WriteQuat(w, fr.Rotation);
                    WriteVec3(w, fr.Scale);
                }
            }

            if (m.IsKeyframed)
            {
                WriteVec3(w, m.PivotTranslation);
                WriteQuat(w, m.PivotRotation);
                WriteVec3(w, m.PivotScale);

                w.Write(m.TranslationKeys.Count);
                foreach (VfxVec3Key k in m.TranslationKeys) WriteVec3Key(w, k);
                w.Write(m.RotationKeys.Count);
                foreach (VfxQuatKey k in m.RotationKeys) WriteQuatKey(w, k);
                w.Write(m.ScaleKeys.Count);
                foreach (VfxVec3Key k in m.ScaleKeys) WriteVec3Key(w, k);
            }
        }

        private static void WriteMaterial(BinaryWriter w, VfxMaterial mat)
        {
            w.Write(mat.Type);
            w.Write(mat.FramesPerSecond);
            w.Write((byte)(mat.Additive ? 1 : 0));

            if (mat.HasTextures) WriteTexture(w, mat.Tex0);
            if (mat.IsVMix)
            {
                WriteTexture(w, mat.Tex1);
                w.Write(mat.MixFrames.Count);
                foreach (float f in mat.MixFrames) w.Write(f);
            }
            if (mat.HasTextures)
            {
                w.Write(mat.SpecularLevel);
                w.Write(mat.Glossiness);
                w.Write(mat.ReflectionAmount);
                WriteCString(w, mat.ReflTexName);
            }
            if (mat.IsColorOnly)
            {
                w.Write(mat.SolidColor.Length > 0 ? mat.SolidColor[0] : 0);
                w.Write(mat.SolidColor.Length > 1 ? mat.SolidColor[1] : 0);
                w.Write(mat.SolidColor.Length > 2 ? mat.SolidColor[2] : 0);
            }

            w.Write(mat.SelfIllumination.Count);
            foreach (float f in mat.SelfIllumination) w.Write(f);
            w.Write(mat.Opacity.Count);
            foreach (float f in mat.Opacity) w.Write(f);
        }

        private static void WriteParticleSystem(BinaryWriter w, VfxParticleSystem p)
        {
            WriteCString(w, p.Name);
            WriteCString(w, p.ParentName);
            w.Write((byte)(p.SaveParent ? 1 : 0));
            w.Write(p.Flags);
            w.Write(p.Warps.Count);
            foreach (string s in p.Warps) WriteCString(w, s);
            w.Write(p.StartTime);
            w.Write(p.Frames.Count);
            w.Write(p.MaterialIndex);
            w.Write(p.ParticleCount);
            w.Write(p.Start);
            w.Write(p.Lifetime);
            w.Write(p.LifetimeVariation);
            w.Write(p.EmitterType);
            w.Write(p.ShrinkAtBirth);
            w.Write(p.ShrinkAtDeath);
            w.Write(p.FadeAtBirth);
            w.Write(p.FadeAtDeath);
            if (p.Drops) w.Write(p.TailDistance);

            foreach (VfxParticleFrame fr in p.Frames)
            {
                WriteVec3(w, fr.Pos);
                WriteQuat(w, fr.Orient);
                w.Write(fr.Width);
                w.Write(fr.Height);
                w.Write(fr.DropSize);
                w.Write(fr.Speed);
                w.Write(fr.SpeedVariation);
                w.Write(fr.BirthRate);
            }
        }

        private static void WriteDummy(BinaryWriter w, VfxDummy d)
        {
            WriteCString(w, d.Name);
            WriteCString(w, d.ParentName);
            w.Write((byte)(d.SaveParent ? 1 : 0));
            WriteVec3(w, d.Pos);
            WriteQuat(w, d.Orient);
            w.Write(d.Frames.Count);
            foreach (VfxDummyFrame fr in d.Frames)
            {
                WriteVec3(w, fr.Pos);
                WriteQuat(w, fr.Orient);
            }
        }

        private static void WriteLightParams(BinaryWriter w, VfxLightParams p)
        {
            WriteVec3(w, p.Pos);
            w.Write(p.Radius);
            w.Write(p.Multiplier);
            WriteVec3(w, p.Color);
            w.Write((byte)(p.IsOn ? 1 : 0));
        }

        private static void WriteLight(BinaryWriter w, VfxLight l)
        {
            WriteCString(w, l.Name);
            WriteCString(w, l.ParentName);
            w.Write((byte)(l.SaveParent ? 1 : 0));
            WriteLightParams(w, l.Params);
            w.Write(l.Frames.Count);
            foreach (VfxLightParams p in l.Frames) WriteLightParams(w, p);
        }

        private static void WriteSpacewarp(BinaryWriter w, VfxSpacewarp sw)
        {
            WriteCString(w, sw.Name);
            WriteCString(w, sw.ParentName);
            w.Write(sw.Type);
            w.Write(sw.Frames.Count);
            foreach (VfxSpacewarpFrame fr in sw.Frames)
            {
                WriteVec3(w, fr.Pos);
                WriteQuat(w, fr.Orient);
                w.Write(fr.Strength);
                w.Write(fr.Decay);
                w.Write(fr.Turbulence);
                w.Write(fr.Frequency);
                w.Write(fr.Scale);
            }
        }

        private static void WriteCamera(BinaryWriter w, VfxCamera c)
        {
            WriteCString(w, c.Name);
            WriteCString(w, c.ParentName);
            // 0x3000E+ stores an inclusive frame range, so the end frame follows from the count.
            w.Write(c.StartFrame);
            w.Write(c.StartFrame + Math.Max(0, c.Frames.Count - 1));
            foreach (VfxDummyFrame fr in c.Frames)
            {
                WriteVec3(w, fr.Pos);
                WriteQuat(w, fr.Orient);
            }
        }

        private static void WriteChain(BinaryWriter w, VfxChain c)
        {
            WriteCString(w, c.Name);
            WriteCString(w, c.ParentName);
            w.Write((byte)(c.SaveParent ? 1 : 0));
            w.Write(c.VertexCount);
            w.Write(c.Width);
            WriteCString(w, c.GlowName);
            w.Write(c.Flags);
            w.Write(c.FramesPerSecond);
            w.Write(c.StartTime);
            w.Write(c.EndTime);
            w.Write(c.Frames.Count);
            w.Write((byte)(c.IsKeyframed ? 1 : 0));

            for (int i = 0; i < c.Frames.Count; i++)
            {
                VfxChainFrame fr = c.Frames[i];
                if (c.Morph || i == 0)
                {
                    WriteVec3(w, fr.Center);
                    WriteVec3(w, fr.PositionsMultiplier);
                    int expected = c.VertexCount * 3;
                    for (int j = 0; j < expected; j++)
                        w.Write(j < fr.RawPositions.Length ? fr.RawPositions[j] : (short)0);
                }
                if (!c.Morph && !c.IsKeyframed)
                {
                    WriteVec3(w, fr.Translation);
                    WriteQuat(w, fr.Rotation);
                    WriteVec3(w, fr.Scale);
                }
                w.Write((byte)(fr.Visible ? 1 : 0));
            }

            if (c.IsKeyframed)
            {
                WriteVec3(w, c.BaseTranslation);
                WriteQuat(w, c.BaseRotation);
                WriteVec3(w, c.BaseScale);
                w.Write(c.TranslationKeys.Count);
                foreach (VfxVec3Key k in c.TranslationKeys) WriteVec3Key(w, k);
                w.Write(c.RotationKeys.Count);
                foreach (VfxQuatKey k in c.RotationKeys) WriteQuatKey(w, k);
                w.Write(c.ScaleKeys.Count);
                foreach (VfxVec3Key k in c.ScaleKeys) WriteVec3Key(w, k);
            }
        }

        // ─── derived geometry ──────────────────────────────────────────────────────────────────

        // Rebuilds everything a .vfx stores redundantly about its geometry. Only used when the
        // model did not come from a parsed .vfx (a glTF that lost the extras), since a parsed file
        // already carries the authored values and must be written back verbatim.
        public static void RecomputeDerivedGeometry(VfxMesh m)
        {
            RecomputeFaceGeometry(m);
            RecomputeBounds(m, m.Frames.Count > 0 && m.Frames[0].HasPositions ? m.Frames[0].Positions : Array.Empty<Vector3>());
            RecomputeAdjacency(m);
        }

        // Face plane, centroid and bounding radius, all derived from frame 0.
        public static void RecomputeFaceGeometry(VfxMesh m)
        {
            Vector3[] positions = m.Frames.Count > 0 && m.Frames[0].HasPositions
                ? m.Frames[0].Positions
                : Array.Empty<Vector3>();

            foreach (VfxFace f in m.Faces)
            {
                Vector3 p0 = SafeGet(positions, f.Indices[0]);
                Vector3 p1 = SafeGet(positions, f.Indices[1]);
                Vector3 p2 = SafeGet(positions, f.Indices[2]);

                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                f.Normal = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : new Vector3(0f, 1f, 0f);
                f.Center = (p0 + p1 + p2) / 3f;
                f.Radius = MathF.Max(
                    (p0 - f.Center).Length(),
                    MathF.Max((p1 - f.Center).Length(), (p2 - f.Center).Length()));
            }
        }

        public static void RecomputeBounds(VfxMesh m, Vector3[] positions)
        {
            if (positions.Length == 0)
            {
                m.BoundingCenter = Vector3.Zero;
                m.BoundingRadius = 0f;
                return;
            }

            Vector3 min = positions[0];
            Vector3 max = positions[0];
            foreach (Vector3 p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            Vector3 center = (min + max) * 0.5f;
            float radius = 0f;
            foreach (Vector3 p in positions)
                radius = MathF.Max(radius, (p - center).Length());
            m.BoundingCenter = center;
            m.BoundingRadius = radius;
        }

        // Each face_vertex lists the faces that share its position, which is what RF uses to blend
        // vertex normals across a smoothing group.
        public static void RecomputeAdjacency(VfxMesh m)
        {
            var facesByVertex = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                foreach (int vi in m.Faces[fi].Indices)
                {
                    if (!facesByVertex.TryGetValue(vi, out List<int>? list))
                    {
                        list = new List<int>();
                        facesByVertex[vi] = list;
                    }
                    if (!list.Contains(fi))
                        list.Add(fi);
                }
            }

            foreach (VfxFaceVertex fv in m.FaceVertices)
            {
                fv.AdjacentFaces = facesByVertex.TryGetValue(fv.VertexIndex, out List<int>? list)
                    ? new List<int>(list)
                    : new List<int>();
            }
        }

        private static Vector3 SafeGet(Vector3[] positions, int index)
            => index >= 0 && index < positions.Length ? positions[index] : Vector3.Zero;
    }
}
