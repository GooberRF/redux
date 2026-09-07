using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace redux.parsers
{
    // Reads Red Faction .vfx (animated effect mesh) files. Every version the game ships is handled
    // (0x30008 .. 0x40006) and older constructs are normalised in memory to the 0x40006 shape:
    // per-mesh inline materials become entries in the file-level material table, 1-based face
    // material indices become 0-based, and per-frame mesh opacity becomes a material opacity curve.
    public static class VfxParser
    {
        private const string logSrc = "VfxParser";

        // The engine's own loader refuses anything below this; a few 3ds max era files in the wild
        // are 0x30001..0x30004 and do not match the documented layout.
        public const int MinSupportedVersion = 0x30008;

        public static VfxFile ReadVfx(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var file = new VfxFile { SourceName = Path.GetFileNameWithoutExtension(path) };

            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

            uint magic = r.ReadUInt32();
            if (magic != 0x58465356u) // 'VSFX'
                throw new InvalidDataException($"\"{Path.GetFileName(path)}\" is not a VFX file (bad magic 0x{magic:X8}).");

            int version = r.ReadInt32();
            file.Version = version;
            if (version < MinSupportedVersion)
                throw new InvalidDataException($"VFX version 0x{version:X} is older than the earliest version the game loads (0x{MinSupportedVersion:X}).");
            if (version > VfxFile.CurrentVersion)
                Logger.Warn(logSrc, $"VFX version 0x{version:X} is newer than 0x{VfxFile.CurrentVersion:X}; parsing may be incomplete.");

            ReadHeader(r, file, version);

            // Legacy files carry their materials inside the objects that use them. They are hoisted
            // into a table here and appended after the model sections, matching how the 0x40006
            // exporter lays a file out.
            var hoistedMaterials = new List<VfxMaterial>();

            while (ms.Position + 8 <= ms.Length)
            {
                int type = r.ReadInt32();
                int len = r.ReadInt32();
                long bodyStart = ms.Position;
                // The chunk length counts itself, so the payload is len - 4 bytes.
                long bodyEnd = bodyStart + len - 4;
                if (len < 4 || bodyEnd > ms.Length)
                    throw new InvalidDataException($"VFX section '{VfxSectionType.ToTag(type)}' at 0x{bodyStart - 8:X} has an invalid length {len}.");

                VfxSection section = ReadSection(r, type, version, bodyEnd, file, hoistedMaterials);

                if (ms.Position != bodyEnd)
                {
                    Logger.Warn(logSrc, $"Section '{VfxSectionType.ToTag(type)}' consumed {ms.Position - bodyStart} of {bodyEnd - bodyStart} bytes; skipping the remainder.");
                    ms.Position = bodyEnd;
                }

                file.Sections.Add(section);
            }

            foreach (VfxMaterial m in hoistedMaterials)
                file.Sections.Add(m);

            MigrateFrameOpacityToMaterials(file, version);

            if (ms.Position != ms.Length)
                Logger.Warn(logSrc, $"{ms.Length - ms.Position} trailing bytes after the last VFX section.");

            return file;
        }

        // From 0x40000 to 0x40004 materials already live in a shared table but opacity is still a
        // per-frame value on each mesh and particle system. 0x40005 moved it onto the material, so
        // the curve is copied there; a material two objects animate differently gets duplicated.
        private static void MigrateFrameOpacityToMaterials(VfxFile file, int version)
        {
            if (version < 0x40000 || version >= 0x40005)
                return;

            List<VfxMaterial> table = file.MaterialTable;
            var claimed = new Dictionary<int, List<float>>();

            int Claim(int index, List<float> curve)
            {
                if (index < 0 || index >= table.Count)
                    return index;
                if (!claimed.TryGetValue(index, out List<float>? existing))
                {
                    table[index].Opacity = new List<float>(curve);
                    claimed[index] = curve;
                    return index;
                }
                if (CurvesEqual(existing, curve))
                    return index;

                VfxMaterial clone = table[index].Clone();
                clone.Opacity = new List<float>(curve);
                file.Sections.Add(clone);
                table.Add(clone);
                int cloned = table.Count - 1;
                claimed[cloned] = curve;
                Logger.Dev(logSrc, $"Duplicated material {index} as {cloned}: two objects animate its opacity differently.");
                return cloned;
            }

            foreach (VfxSection section in new List<VfxSection>(file.Sections))
            {
                switch (section)
                {
                    case VfxMesh mesh when mesh.Frames.Count > 0 && mesh.Frames[0].HasOpacity:
                        {
                            var curve = mesh.Frames.ConvertAll(f => f.Opacity);
                            for (int i = 0; i < mesh.MaterialIndices.Count; i++)
                                mesh.MaterialIndices[i] = Claim(mesh.MaterialIndices[i], curve);
                            break;
                        }
                    case VfxParticleSystem particles when particles.Frames.Count > 0 && particles.Frames[0].HasOpacity:
                        {
                            var curve = particles.Frames.ConvertAll(f => f.Opacity);
                            particles.MaterialIndex = Claim(particles.MaterialIndex, curve);
                            break;
                        }
                }
            }
        }

        private static bool CurvesEqual(List<float> a, List<float> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (MathF.Abs(a[i] - b[i]) > 1e-6f)
                    return false;
            }
            return true;
        }

        // The header is an allocation manifest: every count is recomputed from content on export.
        // Only version, flags and end_frame carry information the sections do not.
        private static void ReadHeader(BinaryReader r, VfxFile file, int version)
        {
            if (version >= 0x30008) file.HeaderFlags = r.ReadInt32();
            file.EndFrame = r.ReadInt32();

            r.ReadInt32(); // num_meshes
            r.ReadInt32(); // num_lights
            r.ReadInt32(); // num_dummies
            r.ReadInt32(); // num_particle_systems
            r.ReadInt32(); // num_spacewarps
            r.ReadInt32(); // num_cameras
            if (version >= 0x3000F) r.ReadInt32(); // num_selsets
            if (version >= 0x40000) r.ReadInt32(); // num_materials
            if (version >= 0x40002) r.ReadInt32(); // num_mix_frames
            if (version >= 0x40003) r.ReadInt32(); // num_self_illumination_frames
            if (version >= 0x40005) r.ReadInt32(); // num_opacity_frames
            if (version < 0x3000A) file.LegacyUnk1 = r.ReadInt32();

            r.ReadInt32(); // num_faces
            r.ReadInt32(); // num_mesh_material_indices
            r.ReadInt32(); // num_vertex_normals
            r.ReadInt32(); // num_adjacent_faces
            r.ReadInt32(); // num_mesh_frames
            if (version >= 0x3000D) r.ReadInt32(); // num_uv_frames
            if (version >= 0x30009)
            {
                r.ReadInt32(); // num_mesh_transform_frames
                r.ReadInt32(); // num_mesh_transform_keyframe_lists
                r.ReadInt32(); // num_mesh_translation_keys
                r.ReadInt32(); // num_mesh_rotation_keys
                r.ReadInt32(); // num_mesh_scale_keys
            }
            r.ReadInt32(); // num_light_frames
            r.ReadInt32(); // num_dummy_frames
            r.ReadInt32(); // num_part_sys_frames
            r.ReadInt32(); // num_spacewarp_frames
            r.ReadInt32(); // num_camera_frames
            if (version >= 0x3000F) file.SelSetObjectCount = r.ReadInt32();
        }

        private static VfxSection ReadSection(
            BinaryReader r,
            int type,
            int version,
            long bodyEnd,
            VfxFile file,
            List<VfxMaterial> hoisted)
        {
            switch (type)
            {
                case VfxSectionType.Mesh: return ReadMesh(r, version, file, hoisted);
                case VfxSectionType.Material: return ReadMaterial(r, version);
                case VfxSectionType.ParticleSystem: return ReadParticleSystem(r, version, file, hoisted);
                case VfxSectionType.Dummy: return ReadDummy(r);
                case VfxSectionType.Light: return ReadLight(r);
                case VfxSectionType.Spacewarp: return ReadSpacewarp(r);
                case VfxSectionType.Chain: return ReadChain(r, version);
                case VfxSectionType.Camera: return ReadCamera(r, version);
                case VfxSectionType.MaterialModifier: return ReadMaterialModifier(r, version, hoisted);
                default:
                    {
                        int size = (int)(bodyEnd - r.BaseStream.Position);
                        return new VfxUnknownSection { RawTypeId = type, Data = r.ReadBytes(Math.Max(0, size)) };
                    }
            }
        }

        // ─── primitives ────────────────────────────────────────────────────────────────────────

        private static string ReadCString(BinaryReader r)
        {
            var sb = new StringBuilder();
            while (true)
            {
                byte b = r.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static Vector3 ReadVec3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Quaternion ReadQuat(BinaryReader r)
        {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            float w = r.ReadSingle();
            return new Quaternion(x, y, z, w);
        }

        private static Vector2 ReadUv(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());

        private static VfxVec3Key ReadVec3Key(BinaryReader r) => new()
        {
            Time = r.ReadInt32(),
            Value = ReadVec3(r),
            InTangent = ReadVec3(r),
            OutTangent = ReadVec3(r)
        };

        private static VfxQuatKey ReadQuatKey(BinaryReader r) => new()
        {
            Time = r.ReadInt32(),
            Value = ReadQuat(r),
            Tension = r.ReadSingle(),
            Continuity = r.ReadSingle(),
            Bias = r.ReadSingle(),
            EaseIn = r.ReadSingle(),
            EaseOut = r.ReadSingle()
        };

        private static VfxTexture ReadTexture(BinaryReader r, int version)
        {
            var t = new VfxTexture { Name = ReadCString(r) };
            if (version >= 0x30012)
            {
                t.StartFrame = r.ReadInt32();
                t.PlaybackRate = r.ReadSingle();
                t.AnimType = r.ReadInt32();
            }
            return t;
        }

        // ─── mesh ──────────────────────────────────────────────────────────────────────────────

        private static VfxMesh ReadMesh(BinaryReader r, int version, VfxFile file, List<VfxMaterial> hoisted)
        {
            var m = new VfxMesh
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                SaveParent = r.ReadByte() != 0
            };

            m.VertexCount = r.ReadInt32();
            if (version < 0x3000A)
            {
                m.LegacyVertexPositions = new List<Vector3>(m.VertexCount);
                for (int i = 0; i < m.VertexCount; i++)
                    m.LegacyVertexPositions.Add(ReadVec3(r));
            }

            int numFaces = r.ReadInt32();
            for (int i = 0; i < numFaces; i++)
            {
                var f = new VfxFace();
                f.Indices = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                if (version < 0x3000D)
                    f.LegacyUvs = new[] { ReadUv(r), ReadUv(r), ReadUv(r) };
                f.Colors = new[] { ReadVec3(r), ReadVec3(r), ReadVec3(r) };
                f.Normal = ReadVec3(r);
                f.Center = ReadVec3(r);
                f.Radius = r.ReadSingle();
                f.MaterialIndex = r.ReadInt32();
                f.SmoothingGroup = r.ReadInt32();
                f.FaceVertexIndices = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                m.Faces.Add(f);
            }

            m.FramesPerSecond = version >= 0x30009 ? r.ReadInt32() : 15;

            int legacyStartFrame = 0, legacyEndFrame = 0;
            if (version >= 0x40004)
            {
                m.StartTime = r.ReadSingle();
                m.EndTime = r.ReadSingle();
                m.NumFrames = r.ReadInt32();
            }
            else
            {
                legacyStartFrame = r.ReadInt32();
                legacyEndFrame = r.ReadInt32();
            }

            int numMaterials = r.ReadInt32();
            var inlineMaterials = new List<VfxMaterial>();
            if (version >= 0x40000)
            {
                for (int i = 0; i < numMaterials; i++)
                    m.MaterialIndices.Add(r.ReadInt32());
            }
            else
            {
                int mixFrameCount = version >= 0x3000C
                    ? legacyEndFrame - legacyStartFrame + 1
                    : legacyEndFrame - legacyStartFrame;
                for (int i = 0; i < numMaterials; i++)
                    inlineMaterials.Add(ReadInlineMeshMaterial(r, version, Math.Max(0, mixFrameCount), m.FramesPerSecond));
            }

            m.BoundingCenter = ReadVec3(r);
            m.BoundingRadius = r.ReadSingle();

            if (version < 0x30002) m.LegacyFlags = r.ReadInt32();
            m.Flags = r.ReadUInt32();

            if (m.Facing && version == 0x3000A)
            {
                m.HasLegacyFacingSize = true;
                m.LegacyWidth = r.ReadSingle();
                m.LegacyHeight = r.ReadSingle();
            }

            int numFaceVertices = r.ReadInt32();
            for (int i = 0; i < numFaceVertices; i++)
            {
                var fv = new VfxFaceVertex
                {
                    SmoothingGroup = r.ReadInt32(),
                    VertexIndex = r.ReadInt32(),
                    URaw = r.ReadUInt32(),
                    VRaw = r.ReadUInt32()
                };
                int adj = r.ReadInt32();
                for (int j = 0; j < adj; j++)
                    fv.AdjacentFaces.Add(r.ReadInt32());
                m.FaceVertices.Add(fv);
            }

            m.IsKeyframed = version >= 0x30009 && r.ReadByte() != 0;

            int frameCount = version >= 0x40004
                ? m.NumFrames
                : (version >= 0x3000C ? legacyEndFrame - legacyStartFrame + 1 : legacyEndFrame - legacyStartFrame);
            frameCount = Math.Max(0, frameCount);

            for (int i = 0; i < frameCount; i++)
                m.Frames.Add(ReadMeshFrame(r, version, m, i));

            if (m.IsKeyframed && version >= 0x3000A)
            {
                m.HasPivot = true;
                m.PivotTranslation = ReadVec3(r);
                m.PivotRotation = ReadQuat(r);
                m.PivotScale = ReadVec3(r);
            }

            if (m.IsKeyframed)
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) m.TranslationKeys.Add(ReadVec3Key(r));
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) m.RotationKeys.Add(ReadQuatKey(r));
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) m.ScaleKeys.Add(ReadVec3Key(r));
            }

            if (version < 0x40004)
            {
                float fps = m.FramesPerSecond > 0 ? m.FramesPerSecond : 15f;
                m.StartTime = legacyStartFrame / fps;
                m.EndTime = legacyEndFrame / fps;
                m.NumFrames = m.Frames.Count;
            }

            NormalizeLegacyMesh(m, version, inlineMaterials, hoisted);
            return m;
        }

        private static VfxMeshFrame ReadMeshFrame(BinaryReader r, int version, VfxMesh m, int index)
        {
            var fr = new VfxMeshFrame();
            bool storesGeometry = m.Morph || index == 0;

            if (storesGeometry)
            {
                fr.HasPositions = true;
                fr.Center = ReadVec3(r);
                fr.PositionsMultiplier = ReadVec3(r);
                var raw = new short[m.VertexCount * 3];
                for (int i = 0; i < raw.Length; i++)
                    raw[i] = r.ReadInt16();
                fr.RawPositions = raw;
                fr.Positions = VfxPositionCodec.Decompress(fr.Center, fr.PositionsMultiplier, raw, m.VertexCount);

                if ((m.Facing || m.FacingRod) && version >= 0x3000B)
                {
                    fr.HasSize = true;
                    fr.Width = r.ReadSingle();
                    fr.Height = r.ReadSingle();
                }
                if (m.FacingRod && index == 0 && version >= 0x40001)
                {
                    fr.HasUpVector = true;
                    fr.UpVector = ReadVec3(r);
                }
            }

            if ((m.DumpUvs || index == 0) && version >= 0x3000D)
            {
                fr.HasUvs = true;
                var uvs = new Vector2[3 * m.Faces.Count];
                for (int i = 0; i < uvs.Length; i++)
                    uvs[i] = ReadUv(r);
                fr.Uvs = uvs;
            }

            if (!m.Morph && (!m.IsKeyframed || (version < 0x3000E && index == 0)))
            {
                fr.HasTransform = true;
                fr.Translation = ReadVec3(r);
                fr.Rotation = ReadQuat(r);
                fr.Scale = ReadVec3(r);
            }

            if (version < 0x30009)
            {
                fr.HasLegacyPad = true;
                fr.LegacyPad = r.ReadByte();
            }

            if (version < 0x40005)
            {
                fr.HasOpacity = true;
                fr.Opacity = r.ReadSingle();
            }

            return fr;
        }

        // Pulls a pre-0x40000 mesh onto the 0x40006 shape: inline materials move into the table,
        // face material indices lose their 1-based bias, UVs move from the face to frame 0 and the
        // per-frame opacity ramp becomes the material opacity curve.
        private static void NormalizeLegacyMesh(VfxMesh m, int version, List<VfxMaterial> inlineMaterials, List<VfxMaterial> hoisted)
        {
            if (version < 0x40000)
            {
                foreach (VfxMaterial mat in inlineMaterials)
                {
                    m.MaterialIndices.Add(hoisted.Count);
                    hoisted.Add(mat);
                }

                // Old face material indices are 1-based into the mesh's own material array.
                foreach (VfxFace f in m.Faces)
                    f.MaterialIndex = f.MaterialIndex >= 1 ? f.MaterialIndex - 1 : -1;
            }

            if (version < 0x3000D)
            {
                // UVs used to live on the face; 0x3000D+ stores them per mesh frame.
                var uvs = new Vector2[3 * m.Faces.Count];
                for (int i = 0; i < m.Faces.Count; i++)
                {
                    Vector2[] src = m.Faces[i].LegacyUvs ?? new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero };
                    uvs[i * 3 + 0] = src[0];
                    uvs[i * 3 + 1] = src[1];
                    uvs[i * 3 + 2] = src[2];
                }
                if (m.Frames.Count > 0)
                {
                    m.Frames[0].HasUvs = true;
                    m.Frames[0].Uvs = uvs;
                }
            }

            if (version < 0x40005 && inlineMaterials.Count > 0)
            {
                // Per-frame mesh opacity becomes the material opacity curve. Legacy materials are
                // per mesh, so there is exactly one owner and no sharing to worry about.
                var curve = new List<float>(m.Frames.Count);
                foreach (VfxMeshFrame fr in m.Frames)
                    curve.Add(fr.Opacity);
                if (curve.Count == 0)
                    curve.Add(1f);
                foreach (VfxMaterial mat in inlineMaterials)
                    mat.Opacity = new List<float>(curve);
            }

            // Before 0x3000E a keyframed mesh also stored a transform on frame 0, which the 0x40006
            // layout has no room for. The pivot is applied before the keyframe transform, so
            // folding it in there reproduces the same pose - and in every stock file of that era
            // the keyframe lists are empty, making that frame-0 transform the only placement data.
            if (version < 0x3000E && m.IsKeyframed && m.Frames.Count > 0 && m.Frames[0].HasTransform)
            {
                VfxMeshFrame f0 = m.Frames[0];
                Quaternion rotation = Quaternion.Normalize(Quaternion.Multiply(NormalizeQuat(f0.Rotation), NormalizeQuat(m.PivotRotation)));
                Vector3 scale = f0.Scale * m.PivotScale;
                Vector3 translation = f0.Translation + Vector3.Transform(f0.Scale * m.PivotTranslation, NormalizeQuat(f0.Rotation));
                m.HasPivot = true;
                m.PivotTranslation = translation;
                m.PivotRotation = rotation;
                m.PivotScale = scale;
                f0.HasTransform = false;
            }

            // Facing meshes need a size on every frame that carries geometry; 0x3000A kept it on
            // the mesh and anything older had none at all.
            if ((m.Facing || m.FacingRod) && m.Frames.Count > 0)
            {
                if (!m.Frames[0].HasSize)
                {
                    m.Frames[0].HasSize = true;
                    m.Frames[0].Width = m.HasLegacyFacingSize ? m.LegacyWidth : 1f;
                    m.Frames[0].Height = m.HasLegacyFacingSize ? m.LegacyHeight : 1f;
                }
                foreach (VfxMeshFrame fr in m.Frames)
                {
                    if (!fr.HasPositions || fr.HasSize) continue;
                    fr.HasSize = true;
                    fr.Width = m.Frames[0].Width;
                    fr.Height = m.Frames[0].Height;
                }
            }
            if (m.FacingRod && m.Frames.Count > 0 && !m.Frames[0].HasUpVector)
            {
                m.Frames[0].HasUpVector = true;
                m.Frames[0].UpVector = new Vector3(0f, 1f, 0f);
            }
        }

        private static Quaternion NormalizeQuat(Quaternion q)
            => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

        private static VfxMaterial ReadInlineMeshMaterial(BinaryReader r, int version, int mixFrameCount, int fps)
        {
            var mat = new VfxMaterial { FramesPerSecond = fps };
            mat.Type = r.ReadInt32();
            bool textured = mat.Type == (int)VfxMaterialType.Image || mat.Type == (int)VfxMaterialType.VMix;

            if (version >= 0x30003 && textured) mat.Additive = r.ReadByte() != 0;
            if (textured) mat.Tex0 = ReadTexture(r, version);
            if (mat.Type == (int)VfxMaterialType.VMix) mat.Tex1 = ReadTexture(r, version);
            if (textured && version < 0x30012)
            {
                int startFrame = r.ReadInt32();
                int animType = r.ReadInt32();
                if (mat.Tex0 != null) { mat.Tex0.StartFrame = startFrame; mat.Tex0.AnimType = animType; }
                if (mat.Tex1 != null) { mat.Tex1.StartFrame = startFrame; mat.Tex1.AnimType = animType; }
            }
            if (textured && version >= 0x30007)
            {
                mat.SpecularLevel = r.ReadSingle();
                mat.Glossiness = r.ReadSingle();
                mat.ReflectionAmount = r.ReadSingle();
            }
            if (textured) mat.ReflTexName = ReadCString(r);
            if (mat.Type == (int)VfxMaterialType.VMix)
            {
                for (int i = 0; i < mixFrameCount; i++)
                    mat.MixFrames.Add(r.ReadSingle());
            }
            if (mat.Type == (int)VfxMaterialType.ColorOnly)
                mat.SolidColor = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };

            float selfIllumination = version >= 0x30011 ? r.ReadSingle() : 0f;
            mat.SelfIllumination.Add(selfIllumination);
            mat.Opacity.Add(1f);
            return mat;
        }

        // ─── material ──────────────────────────────────────────────────────────────────────────

        private static VfxMaterial ReadMaterial(BinaryReader r, int version)
        {
            var mat = new VfxMaterial();
            mat.Type = r.ReadInt32();
            bool textured = mat.Type == (int)VfxMaterialType.Image || mat.Type == (int)VfxMaterialType.VMix;

            mat.FramesPerSecond = version >= 0x40003 ? r.ReadInt32() : 15;
            if (textured || version >= 0x40006) mat.Additive = r.ReadByte() != 0;
            if (textured) mat.Tex0 = ReadTexture(r, version);
            if (mat.Type == (int)VfxMaterialType.VMix)
            {
                mat.Tex1 = ReadTexture(r, version);
                int numMix = r.ReadInt32();
                if (version < 0x40003) mat.FramesPerSecond = r.ReadInt32();
                for (int i = 0; i < numMix; i++)
                    mat.MixFrames.Add(r.ReadSingle());
            }
            if (textured)
            {
                mat.SpecularLevel = r.ReadSingle();
                mat.Glossiness = r.ReadSingle();
                mat.ReflectionAmount = r.ReadSingle();
                mat.ReflTexName = ReadCString(r);
            }
            if (mat.Type == (int)VfxMaterialType.ColorOnly)
                mat.SolidColor = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };

            int numSelfIllumination = version >= 0x40003 ? r.ReadInt32() : 1;
            for (int i = 0; i < numSelfIllumination; i++)
                mat.SelfIllumination.Add(r.ReadSingle());

            if (version >= 0x40005)
            {
                int numOpacity = r.ReadInt32();
                for (int i = 0; i < numOpacity; i++)
                    mat.Opacity.Add(r.ReadSingle());
            }
            else
            {
                mat.Opacity.Add(1f);
            }

            return mat;
        }

        private static VfxMaterialModifier ReadMaterialModifier(BinaryReader r, int version, List<VfxMaterial> hoisted)
        {
            var mod = new VfxMaterialModifier();
            if (version >= 0x40000)
            {
                mod.MaterialIndex = r.ReadInt32();
                return mod;
            }

            var mat = new VfxMaterial();
            if (version >= 0x30009) mat.FramesPerSecond = r.ReadInt32();
            int numMix = r.ReadInt32();
            mat.Type = r.ReadInt32();
            if (version >= 0x30012) mat.Additive = r.ReadByte() != 0;
            mat.Tex0 = ReadTexture(r, version);
            if (mat.Type == (int)VfxMaterialType.VMix) mat.Tex1 = ReadTexture(r, version);
            if (mat.Type == (int)VfxMaterialType.VMix && version >= 0x30012)
                for (int i = 0; i < numMix; i++) mat.MixFrames.Add(r.ReadSingle());
            if (version < 0x30012)
            {
                int startFrame = r.ReadInt32();
                int animType = r.ReadInt32();
                mat.Tex0.StartFrame = startFrame;
                mat.Tex0.AnimType = animType;
                if (mat.Tex1 != null) { mat.Tex1.StartFrame = startFrame; mat.Tex1.AnimType = animType; }
            }
            if (version >= 0x30007)
            {
                mat.SpecularLevel = r.ReadSingle();
                mat.Glossiness = r.ReadSingle();
                mat.ReflectionAmount = r.ReadSingle();
            }
            mat.ReflTexName = ReadCString(r);
            float si = version >= 0x30012 ? r.ReadSingle() : 0f;
            if (mat.Type == (int)VfxMaterialType.VMix && version < 0x30012)
                for (int i = 0; i < numMix; i++) mat.MixFrames.Add(r.ReadSingle());

            mat.SelfIllumination.Add(si);
            mat.Opacity.Add(1f);

            mod.MaterialIndex = hoisted.Count;
            hoisted.Add(mat);
            return mod;
        }

        // ─── particle system ───────────────────────────────────────────────────────────────────

        private static VfxParticleSystem ReadParticleSystem(BinaryReader r, int version, VfxFile file, List<VfxMaterial> hoisted)
        {
            var p = new VfxParticleSystem
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                SaveParent = r.ReadByte() != 0
            };

            if (version >= 0x30010) p.Flags = r.ReadUInt32();

            int numWarps = r.ReadInt32();
            for (int i = 0; i < numWarps; i++)
                p.Warps.Add(ReadCString(r));

            p.StartTime = r.ReadInt32();
            int numFrames = r.ReadInt32();

            VfxMaterial? inlineMaterial = null;
            if (version >= 0x40000)
                p.MaterialIndex = r.ReadInt32();
            else
                inlineMaterial = ReadInlineParticleMaterial(r, version, numFrames, p.Drops);

            p.ParticleCount = r.ReadInt32();
            p.Start = r.ReadInt32();
            p.Lifetime = r.ReadInt32();
            p.LifetimeVariation = r.ReadSingle();
            p.EmitterType = r.ReadInt32();

            if (version < 0x30010) p.Flags = r.ReadUInt32();

            if (version >= 0x30005)
            {
                p.ShrinkAtBirth = r.ReadSingle();
                p.ShrinkAtDeath = r.ReadSingle();
            }
            else
            {
                p.ShrinkAtBirth = r.ReadInt32() / 100f;
                p.ShrinkAtDeath = r.ReadInt32() / 100f;
            }

            if (version >= 0x30006)
            {
                p.FadeAtBirth = r.ReadSingle();
                p.FadeAtDeath = r.ReadSingle();
            }

            if (p.Drops)
            {
                p.HasTailDistance = true;
                p.TailDistance = r.ReadSingle();
            }

            if (version < 0x3000D) r.ReadBytes(56);

            for (int i = 0; i < numFrames; i++)
            {
                var fr = new VfxParticleFrame
                {
                    Pos = ReadVec3(r),
                    Orient = ReadQuat(r),
                    Width = r.ReadSingle(),
                    Height = r.ReadSingle(),
                    DropSize = r.ReadSingle(),
                    Speed = r.ReadSingle(),
                    SpeedVariation = r.ReadSingle(),
                    BirthRate = r.ReadSingle()
                };
                if (version < 0x40005)
                {
                    fr.HasOpacity = true;
                    fr.Opacity = r.ReadSingle();
                }
                p.Frames.Add(fr);
            }

            if (inlineMaterial != null)
            {
                if (version < 0x40005)
                {
                    var curve = new List<float>(p.Frames.Count);
                    foreach (VfxParticleFrame fr in p.Frames) curve.Add(fr.Opacity);
                    if (curve.Count == 0) curve.Add(1f);
                    inlineMaterial.Opacity = curve;
                }
                p.MaterialIndex = hoisted.Count;
                hoisted.Add(inlineMaterial);
            }

            return p;
        }

        private static VfxMaterial ReadInlineParticleMaterial(BinaryReader r, int version, int numFrames, bool drops)
        {
            var mat = new VfxMaterial();
            int type = -1;
            if (!drops)
            {
                type = r.ReadInt32();
                mat.Type = type;
            }
            else
            {
                mat.Type = (int)VfxMaterialType.ColorOnly;
            }

            bool textured = type == (int)VfxMaterialType.Image || type == (int)VfxMaterialType.VMix;
            if (version >= 0x30003 && textured) mat.Additive = r.ReadByte() != 0;
            if (textured)
            {
                mat.Tex0 = new VfxTexture { Name = ReadCString(r) };
                if (version >= 0x30012) mat.Tex0.PlaybackRate = r.ReadInt32();
            }
            if (type == (int)VfxMaterialType.VMix)
            {
                mat.Tex1 = new VfxTexture { Name = ReadCString(r) };
                if (version >= 0x30012) mat.Tex1.PlaybackRate = r.ReadInt32();
                for (int i = 0; i < numFrames; i++)
                    mat.MixFrames.Add(r.ReadSingle());
            }
            if (drops)
                mat.SolidColor = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };

            float si = version >= 0x30011 ? r.ReadSingle() : 0f;
            mat.SelfIllumination.Add(si);
            mat.Opacity.Add(1f);
            return mat;
        }

        // ─── simple sections ───────────────────────────────────────────────────────────────────

        private static VfxDummy ReadDummy(BinaryReader r)
        {
            var d = new VfxDummy
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                SaveParent = r.ReadByte() != 0,
                Pos = ReadVec3(r),
                Orient = ReadQuat(r)
            };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                d.Frames.Add(new VfxDummyFrame { Pos = ReadVec3(r), Orient = ReadQuat(r) });
            return d;
        }

        private static VfxLightParams ReadLightParams(BinaryReader r) => new()
        {
            Pos = ReadVec3(r),
            Radius = r.ReadSingle(),
            Multiplier = r.ReadSingle(),
            Color = ReadVec3(r),
            IsOn = r.ReadByte() != 0
        };

        private static VfxLight ReadLight(BinaryReader r)
        {
            var l = new VfxLight
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                SaveParent = r.ReadByte() != 0
            };
            l.Params = ReadLightParams(r);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                l.Frames.Add(ReadLightParams(r));
            return l;
        }

        private static VfxSpacewarp ReadSpacewarp(BinaryReader r)
        {
            var w = new VfxSpacewarp
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                Type = r.ReadInt32()
            };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                w.Frames.Add(new VfxSpacewarpFrame
                {
                    Pos = ReadVec3(r),
                    Orient = ReadQuat(r),
                    Strength = r.ReadSingle(),
                    Decay = r.ReadSingle(),
                    Turbulence = r.ReadSingle(),
                    Frequency = r.ReadSingle(),
                    Scale = r.ReadSingle()
                });
            }
            return w;
        }

        private static VfxCamera ReadCamera(BinaryReader r, int version)
        {
            var c = new VfxCamera
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                StartFrame = r.ReadInt32(),
                EndFrame = r.ReadInt32()
            };
            int count = version >= 0x3000E ? c.EndFrame - c.StartFrame + 1 : c.EndFrame - c.StartFrame;
            for (int i = 0; i < Math.Max(0, count); i++)
                c.Frames.Add(new VfxDummyFrame { Pos = ReadVec3(r), Orient = ReadQuat(r) });
            return c;
        }

        private static VfxChain ReadChain(BinaryReader r, int version)
        {
            var c = new VfxChain
            {
                Name = ReadCString(r),
                ParentName = ReadCString(r),
                SaveParent = r.ReadByte() != 0
            };
            c.VertexCount = r.ReadInt32();
            if (version < 0x3000A)
            {
                c.LegacyPositions = new List<Vector3>(c.VertexCount);
                for (int i = 0; i < c.VertexCount; i++)
                    c.LegacyPositions.Add(ReadVec3(r));
            }
            c.Width = r.ReadSingle();
            c.GlowName = ReadCString(r);
            c.Flags = r.ReadUInt32();
            c.FramesPerSecond = r.ReadInt32();

            int legacyStart = 0, legacyEnd = 0;
            if (version >= 0x40004)
            {
                c.StartTime = r.ReadSingle();
                c.EndTime = r.ReadSingle();
                c.NumFrames = r.ReadInt32();
            }
            else
            {
                legacyStart = r.ReadInt32();
                legacyEnd = r.ReadInt32();
            }

            c.IsKeyframed = version >= 0x30009 && r.ReadByte() != 0;

            int frameCount = version >= 0x40004
                ? c.NumFrames
                : (version >= 0x3000C ? legacyEnd - legacyStart + 1 : legacyEnd - legacyStart);

            for (int i = 0; i < Math.Max(0, frameCount); i++)
            {
                var fr = new VfxChainFrame();
                if (c.Morph || i == 0)
                {
                    fr.HasPositions = true;
                    fr.Center = ReadVec3(r);
                    fr.PositionsMultiplier = ReadVec3(r);
                    var raw = new short[c.VertexCount * 3];
                    for (int j = 0; j < raw.Length; j++) raw[j] = r.ReadInt16();
                    fr.RawPositions = raw;
                    fr.Positions = VfxPositionCodec.Decompress(fr.Center, fr.PositionsMultiplier, raw, c.VertexCount);
                }
                if (!c.Morph && (!c.IsKeyframed || (version < 0x3000E && i == 0)))
                {
                    fr.HasTransform = true;
                    fr.Translation = ReadVec3(r);
                    fr.Rotation = ReadQuat(r);
                    fr.Scale = ReadVec3(r);
                }
                fr.Visible = r.ReadByte() != 0;
                c.Frames.Add(fr);
            }

            if (c.IsKeyframed && version >= 0x3000A)
            {
                c.HasBaseTransform = true;
                c.BaseTranslation = ReadVec3(r);
                c.BaseRotation = ReadQuat(r);
                c.BaseScale = ReadVec3(r);
            }
            if (c.IsKeyframed)
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) c.TranslationKeys.Add(ReadVec3Key(r));
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) c.RotationKeys.Add(ReadQuatKey(r));
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) c.ScaleKeys.Add(ReadVec3Key(r));
            }

            if (version < 0x40004)
            {
                float fps = c.FramesPerSecond > 0 ? c.FramesPerSecond : 15f;
                c.StartTime = legacyStart / fps;
                c.EndTime = legacyEnd / fps;
                c.NumFrames = c.Frames.Count;
            }

            return c;
        }
    }
}
