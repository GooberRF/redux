using System;
using System.Collections.Generic;
using System.Numerics;

namespace redux.utilities
{
    // Data model for Red Faction .vfx (animated effect mesh) files. It mirrors the on-disk format
    // documented in research/vfx.ksy rather than reusing Mesh/Brush: a .vfx carries per-frame
    // morph geometry, TCB keyframes, particle systems and spacewarps, none of which map onto the
    // static-mesh model the v3m/rfg paths use.
    public static class VfxSectionType
    {
        public const int Mesh = 0x4F584653;             // 'SFXO'
        public const int Material = 0x4C54414D;         // 'MATL'
        public const int ParticleSystem = 0x54524150;   // 'PART'
        public const int SelSet = 0x534C4553;           // 'SELS'
        public const int Light = 0x54474C41;            // 'ALGT'
        public const int Spacewarp = 0x50524157;        // 'WARP'
        public const int Chain = 0x454E4843;            // 'CHNE'
        public const int MaterialModifier = 0x444F4D4D; // 'MMOD'
        public const int Camera = 0x41524D43;           // 'CMRA'
        public const int Dummy = 0x594D4D44;            // 'DMMY'

        public static string ToTag(int type)
        {
            Span<char> c = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                int ch = (type >> (8 * i)) & 0xFF;
                c[i] = ch >= 32 && ch < 127 ? (char)ch : '?';
            }
            return new string(c);
        }
    }

    // Mesh flags (SFXO). Bit meanings come from vfx.ksy / the RF renderer.
    [Flags]
    public enum VfxMeshFlags : uint
    {
        None = 0,
        Facing = 0x0001,
        NoInterp = 0x0002,
        Morph = 0x0004,
        Fire = 0x0008,
        Fullbright = 0x0010,
        Seethrough = 0x0020,
        Corona = 0x0040,
        Sky = 0x0080,
        DumpUvs = 0x0100,
        FacingRod = 0x0800
    }

    public enum VfxMaterialType
    {
        Image = 0,
        VMix = 1,
        ColorOnly = 2
    }

    public enum VfxTextureAnimType
    {
        Loop = 0,
        PingPong = 1,
        Once = 2
    }

    public abstract class VfxSection
    {
        public abstract int TypeId { get; }
    }

    // Any section the parser does not model (SELS today, plus anything a future tool writes).
    // The bytes are kept verbatim so a round trip does not lose them.
    public sealed class VfxUnknownSection : VfxSection
    {
        public int RawTypeId { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public override int TypeId => RawTypeId;
    }

    public sealed class VfxVec3Key
    {
        public int Time { get; set; }              // frame number * 320
        public Vector3 Value { get; set; }
        public Vector3 InTangent { get; set; }
        public Vector3 OutTangent { get; set; }
    }

    public sealed class VfxQuatKey
    {
        public int Time { get; set; }              // frame number * 320
        public Quaternion Value { get; set; }
        public float Tension { get; set; }
        public float Continuity { get; set; }
        public float Bias { get; set; }
        public float EaseIn { get; set; }
        public float EaseOut { get; set; }
    }

    public sealed class VfxFace
    {
        public int[] Indices { get; set; } = new int[3];
        // Present only in files older than 0x3000D; newer files carry UVs per mesh frame.
        public Vector2[]? LegacyUvs { get; set; }
        public Vector3[] Colors { get; set; } = new Vector3[3];
        public Vector3 Normal { get; set; }
        public Vector3 Center { get; set; }
        public float Radius { get; set; }
        // >= 0x40000: 0-based index into the mesh's MaterialIndices, or -1 for none.
        public int MaterialIndex { get; set; } = -1;
        public int SmoothingGroup { get; set; }
        public int[] FaceVertexIndices { get; set; } = new int[3];
    }

    public sealed class VfxFaceVertex
    {
        public int SmoothingGroup { get; set; }
        public int VertexIndex { get; set; }
        // Documented as uninitialised in the exporter (usually 0xCDCDCDCD, but not always), so the
        // raw bit patterns are preserved rather than round-tripped through float.
        public uint URaw { get; set; } = 0xCDCDCDCD;
        public uint VRaw { get; set; } = 0xCDCDCDCD;
        public List<int> AdjacentFaces { get; set; } = new();
    }

    public sealed class VfxMeshFrame
    {
        public bool HasPositions { get; set; }
        public Vector3 Center { get; set; }
        public Vector3 PositionsMultiplier { get; set; }
        // Raw signed 16-bit stream, 3 per vertex. Kept alongside the decompressed positions so a
        // file that is not edited can be written back byte for byte.
        public short[] RawPositions { get; set; } = Array.Empty<short>();
        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();

        public bool HasSize { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }

        public bool HasUpVector { get; set; }
        public Vector3 UpVector { get; set; }

        public bool HasUvs { get; set; }
        public Vector2[] Uvs { get; set; } = Array.Empty<Vector2>();   // 3 per face

        public bool HasTransform { get; set; }
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;

        // Only present in files older than 0x30009; unused by the engine.
        public bool HasLegacyPad { get; set; }
        public byte LegacyPad { get; set; }

        // Only present in files older than 0x40005; normalised into the material opacity curve.
        public bool HasOpacity { get; set; }
        public float Opacity { get; set; } = 1f;
    }

    public sealed class VfxMesh : VfxSection
    {
        public override int TypeId => VfxSectionType.Mesh;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public bool SaveParent { get; set; }

        public int VertexCount { get; set; }
        // Files older than 0x3000A carry an unused per-vertex position array.
        public List<Vector3>? LegacyVertexPositions { get; set; }

        public List<VfxFace> Faces { get; set; } = new();

        public int FramesPerSecond { get; set; } = 15;
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public int NumFrames { get; set; }

        public List<int> MaterialIndices { get; set; } = new();

        public Vector3 BoundingCenter { get; set; }
        public float BoundingRadius { get; set; }

        // Only in files older than 0x30002.
        public int? LegacyFlags { get; set; }

        public uint Flags { get; set; }

        // Only in version 0x3000A, where facing size lived on the mesh instead of the frame.
        public bool HasLegacyFacingSize { get; set; }
        public float LegacyWidth { get; set; }
        public float LegacyHeight { get; set; }

        public List<VfxFaceVertex> FaceVertices { get; set; } = new();

        public bool IsKeyframed { get; set; }
        public List<VfxMeshFrame> Frames { get; set; } = new();

        public bool HasPivot { get; set; }
        public Vector3 PivotTranslation { get; set; }
        public Quaternion PivotRotation { get; set; } = Quaternion.Identity;
        public Vector3 PivotScale { get; set; } = Vector3.One;

        public List<VfxVec3Key> TranslationKeys { get; set; } = new();
        public List<VfxQuatKey> RotationKeys { get; set; } = new();
        public List<VfxVec3Key> ScaleKeys { get; set; } = new();

        public bool Facing => (Flags & (uint)VfxMeshFlags.Facing) != 0;
        public bool NoInterp => (Flags & (uint)VfxMeshFlags.NoInterp) != 0;
        public bool Morph => (Flags & (uint)VfxMeshFlags.Morph) != 0;
        public bool Fire => (Flags & (uint)VfxMeshFlags.Fire) != 0;
        public bool Fullbright => (Flags & (uint)VfxMeshFlags.Fullbright) != 0;
        public bool Seethrough => (Flags & (uint)VfxMeshFlags.Seethrough) != 0;
        public bool Corona => (Flags & (uint)VfxMeshFlags.Corona) != 0;
        public bool Sky => (Flags & (uint)VfxMeshFlags.Sky) != 0;
        public bool DumpUvs => (Flags & (uint)VfxMeshFlags.DumpUvs) != 0;
        public bool FacingRod => (Flags & (uint)VfxMeshFlags.FacingRod) != 0;
    }

    public sealed class VfxTexture
    {
        public string Name { get; set; } = string.Empty;
        public int StartFrame { get; set; }
        public float PlaybackRate { get; set; } = 1f;
        public int AnimType { get; set; } = (int)VfxTextureAnimType.Once;
    }

    public sealed class VfxMaterial : VfxSection
    {
        public override int TypeId => VfxSectionType.Material;

        public int Type { get; set; } = (int)VfxMaterialType.Image;
        public int FramesPerSecond { get; set; } = 15;
        public bool Additive { get; set; }

        public VfxTexture? Tex0 { get; set; }
        public VfxTexture? Tex1 { get; set; }

        public List<float> MixFrames { get; set; } = new();

        public float SpecularLevel { get; set; }
        public float Glossiness { get; set; }
        public float ReflectionAmount { get; set; }
        public string ReflTexName { get; set; } = string.Empty;

        public int[] SolidColor { get; set; } = new int[3];

        public List<float> SelfIllumination { get; set; } = new();
        public List<float> Opacity { get; set; } = new();

        public VfxMaterial Clone() => new()
        {
            Type = Type,
            FramesPerSecond = FramesPerSecond,
            Additive = Additive,
            Tex0 = Tex0 == null ? null : new VfxTexture
            {
                Name = Tex0.Name,
                StartFrame = Tex0.StartFrame,
                PlaybackRate = Tex0.PlaybackRate,
                AnimType = Tex0.AnimType
            },
            Tex1 = Tex1 == null ? null : new VfxTexture
            {
                Name = Tex1.Name,
                StartFrame = Tex1.StartFrame,
                PlaybackRate = Tex1.PlaybackRate,
                AnimType = Tex1.AnimType
            },
            MixFrames = new List<float>(MixFrames),
            SpecularLevel = SpecularLevel,
            Glossiness = Glossiness,
            ReflectionAmount = ReflectionAmount,
            ReflTexName = ReflTexName,
            SolidColor = (int[])SolidColor.Clone(),
            SelfIllumination = new List<float>(SelfIllumination),
            Opacity = new List<float>(Opacity)
        };

        public bool IsImage => Type == (int)VfxMaterialType.Image;
        public bool IsVMix => Type == (int)VfxMaterialType.VMix;
        public bool IsColorOnly => Type == (int)VfxMaterialType.ColorOnly;
        public bool HasTextures => IsImage || IsVMix;
    }

    public sealed class VfxMaterialModifier : VfxSection
    {
        public override int TypeId => VfxSectionType.MaterialModifier;
        public int MaterialIndex { get; set; } = -1;
    }

    public sealed class VfxParticleFrame
    {
        public Vector3 Pos { get; set; }
        public Quaternion Orient { get; set; } = Quaternion.Identity;
        public float Width { get; set; }
        public float Height { get; set; }
        public float DropSize { get; set; }
        public float Speed { get; set; }
        public float SpeedVariation { get; set; }
        public float BirthRate { get; set; }
        public bool HasOpacity { get; set; }
        public float Opacity { get; set; } = 1f;
    }

    [Flags]
    public enum VfxParticleFlags : uint
    {
        None = 0,
        ApplyGravity = 0x0002,
        RandomizeOrientation = 0x0010,
        NoCull = 0x0020,
        Drops = 0x0100
    }

    public sealed class VfxParticleSystem : VfxSection
    {
        public override int TypeId => VfxSectionType.ParticleSystem;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public bool SaveParent { get; set; }

        public uint Flags { get; set; }
        public List<string> Warps { get; set; } = new();

        public int StartTime { get; set; }
        public int MaterialIndex { get; set; } = -1;

        public int ParticleCount { get; set; }
        public int Start { get; set; }
        public int Lifetime { get; set; }
        public float LifetimeVariation { get; set; }
        public int EmitterType { get; set; }

        public float ShrinkAtBirth { get; set; }
        public float ShrinkAtDeath { get; set; }
        public float FadeAtBirth { get; set; }
        public float FadeAtDeath { get; set; }

        public bool HasTailDistance { get; set; }
        public float TailDistance { get; set; }

        public List<VfxParticleFrame> Frames { get; set; } = new();

        public bool ApplyGravity => (Flags & (uint)VfxParticleFlags.ApplyGravity) != 0;
        public bool RandomizeOrientation => (Flags & (uint)VfxParticleFlags.RandomizeOrientation) != 0;
        public bool NoCull => (Flags & (uint)VfxParticleFlags.NoCull) != 0;
        public bool Drops => (Flags & (uint)VfxParticleFlags.Drops) != 0;
    }

    public sealed class VfxDummyFrame
    {
        public Vector3 Pos { get; set; }
        public Quaternion Orient { get; set; } = Quaternion.Identity;
    }

    public sealed class VfxDummy : VfxSection
    {
        public override int TypeId => VfxSectionType.Dummy;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public bool SaveParent { get; set; }
        public Vector3 Pos { get; set; }
        public Quaternion Orient { get; set; } = Quaternion.Identity;
        public List<VfxDummyFrame> Frames { get; set; } = new();
    }

    public sealed class VfxLightParams
    {
        public Vector3 Pos { get; set; }
        public float Radius { get; set; }
        public float Multiplier { get; set; }
        public Vector3 Color { get; set; }
        public bool IsOn { get; set; }
    }

    public sealed class VfxLight : VfxSection
    {
        public override int TypeId => VfxSectionType.Light;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public bool SaveParent { get; set; }
        public VfxLightParams Params { get; set; } = new();
        public List<VfxLightParams> Frames { get; set; } = new();
    }

    public sealed class VfxSpacewarpFrame
    {
        public Vector3 Pos { get; set; }
        public Quaternion Orient { get; set; } = Quaternion.Identity;
        public float Strength { get; set; }
        public float Decay { get; set; }
        public float Turbulence { get; set; }
        public float Frequency { get; set; }
        public float Scale { get; set; }
    }

    public sealed class VfxSpacewarp : VfxSection
    {
        public override int TypeId => VfxSectionType.Spacewarp;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public int Type { get; set; }
        public List<VfxSpacewarpFrame> Frames { get; set; } = new();
    }

    public sealed class VfxChainFrame
    {
        public bool HasPositions { get; set; }
        public Vector3 Center { get; set; }
        public Vector3 PositionsMultiplier { get; set; }
        public short[] RawPositions { get; set; } = Array.Empty<short>();
        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();

        public bool HasTransform { get; set; }
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;

        public bool Visible { get; set; } = true;
    }

    public sealed class VfxChain : VfxSection
    {
        public override int TypeId => VfxSectionType.Chain;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public bool SaveParent { get; set; }
        public int VertexCount { get; set; }
        public List<Vector3>? LegacyPositions { get; set; }
        public float Width { get; set; }
        public string GlowName { get; set; } = string.Empty;
        public uint Flags { get; set; }
        public int FramesPerSecond { get; set; } = 15;
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public int NumFrames { get; set; }
        public bool IsKeyframed { get; set; }
        public List<VfxChainFrame> Frames { get; set; } = new();

        public bool HasBaseTransform { get; set; }
        public Vector3 BaseTranslation { get; set; }
        public Quaternion BaseRotation { get; set; } = Quaternion.Identity;
        public Vector3 BaseScale { get; set; } = Vector3.One;

        public List<VfxVec3Key> TranslationKeys { get; set; } = new();
        public List<VfxQuatKey> RotationKeys { get; set; } = new();
        public List<VfxVec3Key> ScaleKeys { get; set; } = new();

        public bool NoInterp => (Flags & 0x02) != 0;
        public bool Morph => (Flags & 0x04) != 0;
        public bool Fire => (Flags & 0x08) != 0;
    }

    public sealed class VfxCamera : VfxSection
    {
        public override int TypeId => VfxSectionType.Camera;

        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = "Scene Root";
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public List<VfxDummyFrame> Frames { get; set; } = new();
    }

    public sealed class VfxFile
    {
        public const int CurrentVersion = 0x40006;

        // Version the data was read from. The exporter always writes CurrentVersion.
        public int Version { get; set; } = CurrentVersion;
        public int HeaderFlags { get; set; }
        public int EndFrame { get; set; }
        // Only meaningful for files older than 0x3000A, where the header carries an unused field.
        public int LegacyUnk1 { get; set; }
        public int SelSetObjectCount { get; set; }

        public string SourceName { get; set; } = string.Empty;

        // Sections in file order.
        public List<VfxSection> Sections { get; set; } = new();

        public IEnumerable<VfxMesh> Meshes
        {
            get
            {
                foreach (VfxSection s in Sections)
                    if (s is VfxMesh m) yield return m;
            }
        }

        // MATL sections in file order; a material index refers to this list.
        public List<VfxMaterial> MaterialTable
        {
            get
            {
                var list = new List<VfxMaterial>();
                foreach (VfxSection s in Sections)
                    if (s is VfxMaterial m) list.Add(m);
                return list;
            }
        }
    }

    // Every invariant the RF renderer relies on when it walks a mesh. The engine dereferences a
    // per-corner vertex record for every face (FUN_00554a80 writes through it at +0x08), so a mesh
    // that has faces but an empty face_vertex table crashes on the first frame it is drawn. These
    // checks run on import, to decide whether the authored tables can be trusted, and again before
    // anything is written, so a file that would crash the game never reaches disk.
    public static class VfxValidation
    {
        public static List<string> Validate(VfxFile file)
        {
            var problems = new List<string>();
            int materialCount = file.MaterialTable.Count;

            foreach (VfxSection section in file.Sections)
            {
                switch (section)
                {
                    case VfxMesh mesh:
                        Validate(mesh, materialCount, problems);
                        break;
                    case VfxParticleSystem particles when particles.MaterialIndex < -1 || particles.MaterialIndex >= materialCount:
                        problems.Add($"particle system \"{particles.Name}\": material index {particles.MaterialIndex} is outside the {materialCount}-entry material table.");
                        break;
                    case VfxMaterialModifier modifier when modifier.MaterialIndex < -1 || modifier.MaterialIndex >= materialCount:
                        problems.Add($"material modifier: material index {modifier.MaterialIndex} is outside the {materialCount}-entry material table.");
                        break;
                }
            }

            return problems;
        }

        public static List<string> Validate(VfxMesh mesh, int materialCount)
        {
            var problems = new List<string>();
            Validate(mesh, materialCount, problems);
            return problems;
        }

        public static bool IsValid(VfxMesh mesh, int materialCount, out string reason)
        {
            var problems = new List<string>();
            Validate(mesh, materialCount, problems);
            reason = problems.Count > 0 ? problems[0] : string.Empty;
            return problems.Count == 0;
        }

        private static void Validate(VfxMesh mesh, int materialCount, List<string> problems)
        {
            string name = string.IsNullOrEmpty(mesh.Name) ? "<unnamed>" : mesh.Name;
            int nv = mesh.VertexCount;
            int nf = mesh.Faces.Count;
            int nfv = mesh.FaceVertices.Count;

            // The crash case: faces exist but there is no per-corner vertex record to point at.
            if (nf > 0 && nfv == 0)
            {
                problems.Add($"mesh \"{name}\": {nf} faces but an empty face-vertex table; RF would dereference a null vertex record.");
                return;
            }

            for (int i = 0; i < nf; i++)
            {
                VfxFace f = mesh.Faces[i];
                for (int k = 0; k < 3; k++)
                {
                    if (f.Indices[k] < 0 || f.Indices[k] >= nv)
                    {
                        problems.Add($"mesh \"{name}\": face {i} corner {k} references vertex {f.Indices[k]} of {nv}.");
                        return;
                    }
                    if (f.FaceVertexIndices[k] < 0 || f.FaceVertexIndices[k] >= nfv)
                    {
                        problems.Add($"mesh \"{name}\": face {i} corner {k} references face-vertex {f.FaceVertexIndices[k]} of {nfv}.");
                        return;
                    }
                }
                if (f.MaterialIndex < -1 || f.MaterialIndex >= mesh.MaterialIndices.Count)
                {
                    problems.Add($"mesh \"{name}\": face {i} uses material slot {f.MaterialIndex} of {mesh.MaterialIndices.Count}.");
                    return;
                }
            }

            for (int i = 0; i < nfv; i++)
            {
                VfxFaceVertex fv = mesh.FaceVertices[i];
                if (fv.VertexIndex < 0 || fv.VertexIndex >= nv)
                {
                    problems.Add($"mesh \"{name}\": face-vertex {i} references vertex {fv.VertexIndex} of {nv}.");
                    return;
                }
                foreach (int adjacent in fv.AdjacentFaces)
                {
                    if (adjacent < 0 || adjacent >= nf)
                    {
                        problems.Add($"mesh \"{name}\": face-vertex {i} lists adjacent face {adjacent} of {nf}.");
                        return;
                    }
                }
            }

            for (int i = 0; i < mesh.MaterialIndices.Count; i++)
            {
                if (mesh.MaterialIndices[i] < 0 || mesh.MaterialIndices[i] >= materialCount)
                {
                    problems.Add($"mesh \"{name}\": material slot {i} points at table entry {mesh.MaterialIndices[i]} of {materialCount}.");
                    return;
                }
            }

            for (int i = 0; i < mesh.Frames.Count; i++)
            {
                VfxMeshFrame frame = mesh.Frames[i];
                if (frame.HasPositions)
                {
                    if (frame.RawPositions.Length != nv * 3)
                    {
                        problems.Add($"mesh \"{name}\": frame {i} holds {frame.RawPositions.Length / 3} compressed positions for {nv} vertices.");
                        return;
                    }
                    if (frame.Positions.Length != nv)
                    {
                        problems.Add($"mesh \"{name}\": frame {i} holds {frame.Positions.Length} positions for {nv} vertices.");
                        return;
                    }
                }
                if (frame.HasUvs && frame.Uvs.Length != nf * 3)
                {
                    problems.Add($"mesh \"{name}\": frame {i} holds {frame.Uvs.Length} UVs for {nf} faces ({nf * 3} expected).");
                    return;
                }
            }

            // The writer stores geometry on frame 0 and, for a morph mesh, on every frame.
            if (mesh.Frames.Count > 0 && !mesh.Frames[0].HasPositions && nv > 0)
                problems.Add($"mesh \"{name}\": frame 0 carries no positions.");
            else if (mesh.Morph)
            {
                for (int i = 0; i < mesh.Frames.Count; i++)
                {
                    if (!mesh.Frames[i].HasPositions)
                    {
                        problems.Add($"mesh \"{name}\": morph frame {i} carries no positions.");
                        return;
                    }
                }
            }
        }
    }

    // Shared s16 position (de)compression. Positions are stored as center + s16 * multiplier, with
    // the multiplier derived from the per-axis half-extent. Stock files never fall below a
    // 0.1 unit half-extent, which reproduces the 0.1/32767 multiplier seen throughout the corpus.
    public static class VfxPositionCodec
    {
        public const float Scale = 32767f;
        public const float MinHalfExtent = 0.1f;

        public static Vector3[] Decompress(Vector3 center, Vector3 multiplier, short[] raw, int vertexCount)
        {
            var result = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                result[i] = new Vector3(
                    center.X + raw[i * 3 + 0] * multiplier.X,
                    center.Y + raw[i * 3 + 1] * multiplier.Y,
                    center.Z + raw[i * 3 + 2] * multiplier.Z);
            }
            return result;
        }

        public static void Compress(IReadOnlyList<Vector3> positions, out Vector3 center, out Vector3 multiplier, out short[] raw)
        {
            int n = positions.Count;
            raw = new short[n * 3];
            if (n == 0)
            {
                center = Vector3.Zero;
                multiplier = new Vector3(MinHalfExtent / Scale);
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (Vector3 p in positions)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);

            float devX = 0f, devY = 0f, devZ = 0f;
            foreach (Vector3 p in positions)
            {
                devX = MathF.Max(devX, MathF.Abs(p.X - center.X));
                devY = MathF.Max(devY, MathF.Abs(p.Y - center.Y));
                devZ = MathF.Max(devZ, MathF.Abs(p.Z - center.Z));
            }

            multiplier = new Vector3(
                MathF.Max(devX, MinHalfExtent) / Scale,
                MathF.Max(devY, MinHalfExtent) / Scale,
                MathF.Max(devZ, MinHalfExtent) / Scale);

            for (int i = 0; i < n; i++)
            {
                Vector3 p = positions[i];
                raw[i * 3 + 0] = Quantize(p.X - center.X, multiplier.X);
                raw[i * 3 + 1] = Quantize(p.Y - center.Y, multiplier.Y);
                raw[i * 3 + 2] = Quantize(p.Z - center.Z, multiplier.Z);
            }
        }

        private static short Quantize(float delta, float multiplier)
        {
            if (multiplier <= 0f)
                return 0;
            float v = MathF.Round(delta / multiplier, MidpointRounding.AwayFromZero);
            if (v > Scale) v = Scale;
            if (v < -Scale) v = -Scale;
            return (short)v;
        }
    }
}
