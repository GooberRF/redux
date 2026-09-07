using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace redux.exporters
{
    // Writes a .vfx as glTF 2.0. The glTF carries a viewable approximation (frame 0 geometry,
    // morph targets, sampled transform animation, one material per MATL entry) while `extras`
    // carry the authored RF values verbatim, in RF space, so VfxGltfParser can rebuild the exact
    // same file. Coordinates follow the same RF -> right-handed convention the v3m exporter uses:
    // X is negated, Y stays up.
    public static class VfxGltfExporter
    {
        private const string logSrc = "VfxGltfExporter";
        private const float Fps = 15f;

        public static void ExportGltf(VfxFile file, string gltfPath)
        {
            string gltfDir = Path.GetDirectoryName(gltfPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(gltfPath);
            string binName = baseName + ".bin";
            string binPath = Path.Combine(gltfDir, binName);

            Logger.Info(logSrc, $"Writing glTF to: {gltfPath}");
            Logger.Info(logSrc, $"Writing BIN to:  {binPath}");

            var bin = new BinBuilder();
            var materials = new List<Material>();
            var textures = new List<TextureDef>();
            var images = new List<ImageDef>();
            var samplers = new List<SamplerDef>();
            var gltfMeshes = new List<GltfMesh>();

            List<VfxMaterial> materialTable = file.MaterialTable;
            BuildMaterials(materialTable, materials, textures, images, samplers, gltfDir);

            var pending = new List<PendingNode>();
            var sectionOrder = new List<Dictionary<string, object>>();

            foreach (VfxSection section in file.Sections)
            {
                switch (section)
                {
                    case VfxMesh m:
                        sectionOrder.Add(SectionEntry("SFXO", m.Name));
                        pending.Add(BuildMeshNode(m, materialTable, bin, gltfMeshes));
                        break;
                    case VfxMaterial:
                        sectionOrder.Add(SectionEntry("MATL", string.Empty));
                        break;
                    case VfxParticleSystem p:
                        sectionOrder.Add(SectionEntry("PART", p.Name));
                        pending.Add(BuildParticleNode(p));
                        break;
                    case VfxDummy d:
                        sectionOrder.Add(SectionEntry("DMMY", d.Name));
                        pending.Add(BuildDummyNode(d));
                        break;
                    case VfxLight l:
                        sectionOrder.Add(SectionEntry("ALGT", l.Name));
                        pending.Add(BuildLightNode(l));
                        break;
                    case VfxSpacewarp sw:
                        sectionOrder.Add(SectionEntry("WARP", sw.Name));
                        pending.Add(BuildSpacewarpNode(sw));
                        break;
                    case VfxChain ch:
                        sectionOrder.Add(SectionEntry("CHNE", ch.Name));
                        pending.Add(BuildChainNode(ch, bin, gltfMeshes));
                        break;
                    case VfxCamera cam:
                        sectionOrder.Add(SectionEntry("CMRA", cam.Name));
                        pending.Add(BuildCameraNode(cam));
                        break;
                    case VfxMaterialModifier mm:
                        sectionOrder.Add(SectionEntry("MMOD", string.Empty));
                        pending.Add(BuildMaterialModifierNode(mm, pending.Count));
                        break;
                    case VfxUnknownSection u:
                        sectionOrder.Add(SectionEntry(VfxSectionType.ToTag(u.RawTypeId), string.Empty));
                        pending.Add(BuildUnknownNode(u, pending.Count));
                        break;
                }
            }

            // ── node graph ────────────────────────────────────────────────────────────────────
            var nodes = new List<Node>();
            var rootChildren = new List<int>();

            var rootExtras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx",
                ["rf_version"] = file.Version,
                ["rf_header_flags"] = file.HeaderFlags,
                ["rf_end_frame"] = file.EndFrame,
                ["rf_selset_object_count"] = file.SelSetObjectCount,
                ["rf_section_order"] = sectionOrder,
                // A glTF only lists materials some primitive uses, so a material referenced by a
                // mesh slot but not by any face - or by a particle system, which has no geometry at
                // all - would not survive a viewer round trip. The whole table rides along here.
                ["rf_material_table"] = materials.Select(m => m.extras).ToList()
            };

            nodes.Add(new Node
            {
                name = string.IsNullOrWhiteSpace(file.SourceName) ? baseName : file.SourceName,
                translation = new[] { 0f, 0f, 0f },
                rotation = new[] { 0f, 0f, 0f, 1f },
                scale = new[] { 1f, 1f, 1f },
                children = rootChildren,
                extras = rootExtras
            });

            foreach (PendingNode pn in pending)
            {
                pn.NodeIndex = nodes.Count;
                nodes.Add(new Node
                {
                    name = pn.Name,
                    mesh = pn.MeshIndex >= 0 ? pn.MeshIndex : null,
                    children = new List<int>(),
                    extras = pn.Extras,
                    weights = pn.MorphTargetCount > 0 ? Enumerable.Repeat(0f, pn.MorphTargetCount).ToArray() : null
                });
            }

            AssignHierarchy(pending, nodes, rootChildren);

            // ── animation ─────────────────────────────────────────────────────────────────────
            var animSamplers = new List<AnimationSampler>();
            var animChannels = new List<AnimationChannel>();
            foreach (PendingNode pn in pending)
                EmitAnimation(pn, bin, animSamplers, animChannels);

            List<Animation>? animations = null;
            if (animChannels.Count > 0)
            {
                animations = new List<Animation>
                {
                    new Animation
                    {
                        name = (string.IsNullOrWhiteSpace(file.SourceName) ? baseName : file.SourceName) + "_vfx",
                        samplers = animSamplers,
                        channels = animChannels
                    }
                };
            }

            var gltf = new GltfRoot
            {
                asset = new Asset { version = "2.0", generator = "redux VfxGltfExporter" },
                buffers = new List<BufferDef> { new BufferDef { uri = binName, byteLength = bin.Length } },
                bufferViews = bin.BufferViews,
                accessors = bin.Accessors,
                meshes = gltfMeshes.Count > 0 ? gltfMeshes : null,
                nodes = nodes,
                animations = animations,
                materials = materials.Count > 0 ? materials : null,
                textures = textures.Count > 0 ? textures : null,
                images = images.Count > 0 ? images : null,
                samplers = samplers.Count > 0 ? samplers : null,
                scenes = new List<Scene> { new Scene { nodes = new List<int> { 0 } } },
                scene = 0
            };

            AuditExtras(nodes.Select(n => n.extras).Concat(materials.Select(m => m.extras)));

            Directory.CreateDirectory(gltfDir.Length == 0 ? "." : gltfDir);
            File.WriteAllBytes(binPath, bin.ToArray());

            // The extras carry whole face and frame tables, so indenting them would multiply the
            // file size several times over for no benefit; glTF here is a machine artifact.
            var opts = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            File.WriteAllText(gltfPath, JsonSerializer.Serialize(gltf, opts));

            int meshCount = file.Sections.Count(s => s is VfxMesh);
            Logger.Info(logSrc,
                $"glTF export complete: {meshCount} mesh, {materialTable.Count} material, " +
                $"{file.Sections.Count(s => s is VfxParticleSystem)} particle, {file.Sections.Count(s => s is VfxDummy)} dummy nodes; " +
                $"{animChannels.Count} animation channels.");
        }

        // Blender 4.x stores glTF extras as ID properties, which are 32-bit signed. A single value
        // past that range makes it fall back to keeping the whole dictionary as a Python repr
        // string, which is how a mesh once round-tripped with an empty face-vertex table. Nothing
        // this exporter writes should be able to trigger that, so say so loudly if it ever does.
        private static void AuditExtras(IEnumerable<Dictionary<string, object>?> extras)
        {
            foreach (Dictionary<string, object>? bag in extras)
            {
                if (bag == null) continue;
                foreach (KeyValuePair<string, object> entry in bag)
                    AuditValue(entry.Key, entry.Value);
            }
        }

        private static void AuditValue(string path, object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case uint u when u > int.MaxValue:
                case long l when l > int.MaxValue || l < int.MinValue:
                    Logger.Warn(logSrc, $"Extra \"{path}\" holds {value}, which is outside int32; Blender cannot store it as a property.");
                    return;
                case string or bool or float or double or int or short or byte or sbyte or ushort or uint:
                    return;
                case Dictionary<string, object> nested:
                    foreach (KeyValuePair<string, object> entry in nested)
                        AuditValue(path + "." + entry.Key, entry.Value);
                    return;
                case System.Collections.IEnumerable list:
                    foreach (object? item in list)
                        AuditValue(path + "[]", item);
                    return;
            }
        }

        private static Dictionary<string, object> SectionEntry(string tag, string name)
            => new() { ["type"] = tag, ["name"] = name ?? string.Empty };

        // ─── coordinate conversion ─────────────────────────────────────────────────────────────

        private static Vector3 RfToRh(Vector3 v) => new(-v.X, v.Y, v.Z);

        private static Quaternion RfToRh(Quaternion q)
        {
            var r = new Quaternion(-q.X, q.Y, q.Z, q.W);
            return r.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(r);
        }

        private static float[] Arr(Vector3 v) => new[] { v.X, v.Y, v.Z };
        private static float[] Arr(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };

        // ─── materials ─────────────────────────────────────────────────────────────────────────

        private static void BuildMaterials(
            List<VfxMaterial> table,
            List<Material> materials,
            List<TextureDef> textures,
            List<ImageDef> images,
            List<SamplerDef> samplers,
            string gltfDir)
        {
            for (int i = 0; i < table.Count; i++)
            {
                VfxMaterial m = table[i];
                var extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "vfx_material",
                    ["rf_material_index"] = i,
                    ["rf_mat_type"] = m.Type switch
                    {
                        (int)VfxMaterialType.Image => "image",
                        (int)VfxMaterialType.VMix => "vmix",
                        (int)VfxMaterialType.ColorOnly => "color_only",
                        _ => m.Type.ToString()
                    },
                    ["rf_mat_type_id"] = m.Type,
                    ["rf_additive"] = m.Additive,
                    ["rf_fps"] = m.FramesPerSecond,
                    ["rf_mix_frames"] = m.MixFrames,
                    ["rf_specular_level"] = m.SpecularLevel,
                    ["rf_glossiness"] = m.Glossiness,
                    ["rf_reflection_amount"] = m.ReflectionAmount,
                    ["rf_refl_tex_name"] = m.ReflTexName ?? string.Empty,
                    ["rf_solid_color"] = m.SolidColor,
                    ["rf_self_illumination"] = m.SelfIllumination,
                    ["rf_opacity"] = m.Opacity
                };
                if (m.Tex0 != null) extras["tex_0"] = TextureExtras(m.Tex0);
                if (m.Tex1 != null) extras["tex_1"] = TextureExtras(m.Tex1);

                float maxOpacity = m.Opacity.Count > 0 ? m.Opacity.Max() : 1f;
                float minOpacity = m.Opacity.Count > 0 ? m.Opacity.Min() : 1f;
                float maxSelfIllumination = m.SelfIllumination.Count > 0 ? Math.Clamp(m.SelfIllumination.Max(), 0f, 1f) : 0f;

                float[] baseColor = { 1f, 1f, 1f, Math.Clamp(maxOpacity, 0f, 1f) };
                if (m.IsColorOnly)
                {
                    baseColor[0] = Math.Clamp((m.SolidColor.Length > 0 ? m.SolidColor[0] : 255) / 255f, 0f, 1f);
                    baseColor[1] = Math.Clamp((m.SolidColor.Length > 1 ? m.SolidColor[1] : 255) / 255f, 0f, 1f);
                    baseColor[2] = Math.Clamp((m.SolidColor.Length > 2 ? m.SolidColor[2] : 255) / 255f, 0f, 1f);
                }

                // RF always blends a texture's own alpha, so a stock material can be fully opaque
                // and still need BLEND in a viewer. The texture is only inspected when it sits
                // beside the output, which is where a converted model keeps its bitmaps.
                bool textureHasAlpha = TextureHasAlpha(gltfDir, m.Tex0?.Name);

                var mat = new Material
                {
                    name = BuildMaterialName(m, i),
                    doubleSided = true,
                    alphaMode = minOpacity < 1f || m.Additive || textureHasAlpha ? "BLEND" : null,
                    pbrMetallicRoughness = new PbrMetallicRoughness
                    {
                        baseColorFactor = baseColor,
                        metallicFactor = 0f,
                        roughnessFactor = 1f
                    },
                    emissiveFactor = maxSelfIllumination > 0f
                        ? new[] { maxSelfIllumination, maxSelfIllumination, maxSelfIllumination }
                        : null,
                    extras = extras
                };

                string? texName = m.Tex0?.Name;
                if (!string.IsNullOrWhiteSpace(texName) && !texName.StartsWith("$", StringComparison.Ordinal))
                {
                    if (samplers.Count == 0)
                        samplers.Add(new SamplerDef { magFilter = 9729, minFilter = 9729, wrapS = 10497, wrapT = 10497 });
                    images.Add(new ImageDef { uri = texName.Replace('\\', '/'), name = Path.GetFileNameWithoutExtension(texName) });
                    textures.Add(new TextureDef { sampler = 0, source = images.Count - 1 });
                    mat.pbrMetallicRoughness!.baseColorTexture = new TextureInfo { index = textures.Count - 1 };
                }

                // Materials are addressed by table position, so identical entries are kept apart.
                materials.Add(mat);
            }
        }

        // True when the named bitmap sits next to the output and carries an alpha channel.
        private static bool TextureHasAlpha(string dir, string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("$", StringComparison.Ordinal))
                return false;

            string path = Path.Combine(dir.Length == 0 ? "." : dir, Path.GetFileName(name));
            if (!File.Exists(path))
                return false;

            try
            {
                using FileStream fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[32];
                int read = fs.Read(head);
                string extension = Path.GetExtension(path).ToLowerInvariant();

                if (extension == ".tga" && read >= 18)
                {
                    // Byte 16 is the pixel depth, the low nibble of byte 17 the attribute (alpha) bits.
                    int depth = head[16];
                    int attributeBits = head[17] & 0x0F;
                    return depth == 32 || (depth == 16 && attributeBits > 0);
                }

                if (extension == ".png" && read >= 26 &&
                    head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                {
                    // IHDR colour type: 4 is grey+alpha, 6 is RGBA.
                    int colorType = head[25];
                    if (colorType == 4 || colorType == 6)
                        return true;
                    return HasPngTransparencyChunk(fs);
                }
            }
            catch (IOException)
            {
                // An unreadable bitmap simply does not contribute an opinion.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        // A palette PNG carries its transparency in a tRNS chunk rather than the colour type.
        private static bool HasPngTransparencyChunk(FileStream fs)
        {
            fs.Position = 8;
            Span<byte> header = stackalloc byte[8];
            for (int guard = 0; guard < 64; guard++)
            {
                if (fs.Read(header) != 8)
                    return false;
                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                string type = $"{(char)header[4]}{(char)header[5]}{(char)header[6]}{(char)header[7]}";
                if (type == "tRNS") return true;
                if (type == "IDAT" || type == "IEND") return false;
                if (length < 0 || fs.Position + length + 4 > fs.Length) return false;
                fs.Position += length + 4;
            }
            return false;
        }

        private static Dictionary<string, object> TextureExtras(VfxTexture t) => new()
        {
            ["name"] = t.Name ?? string.Empty,
            ["start_frame"] = t.StartFrame,
            ["playback_rate"] = t.PlaybackRate,
            ["anim_type"] = t.AnimType
        };

        // What a material is, as far as identity goes: its base texture, or its colour.
        internal static string MaterialIdentity(VfxMaterial m)
        {
            if (m.Tex0 != null && !string.IsNullOrWhiteSpace(m.Tex0.Name))
                return m.Tex0.Name;
            if (m.IsColorOnly)
                return $"color_{m.SolidColor[0]}_{m.SolidColor[1]}_{m.SolidColor[2]}";
            return string.Empty;
        }

        private static string BuildMaterialName(VfxMaterial m, int index)
        {
            string label = m.Tex0 != null && !string.IsNullOrWhiteSpace(m.Tex0.Name)
                ? m.Tex0.Name
                : (m.IsColorOnly ? "color_only" : "material");
            return $"{index:D3}_{label}";
        }

        // ─── mesh ──────────────────────────────────────────────────────────────────────────────

        private static PendingNode BuildMeshNode(VfxMesh m, List<VfxMaterial> table, BinBuilder bin, List<GltfMesh> gltfMeshes)
        {
            var pn = new PendingNode
            {
                Name = m.Name,
                RfParentName = m.ParentName,
                Extras = BuildMeshExtras(m, table)
            };

            // ── the visible geometry ──────────────────────────────────────────────────────────
            VfxMeshFrame? frame0 = m.Frames.Count > 0 ? m.Frames[0] : null;
            Vector3[] basePositions = frame0 != null && frame0.HasPositions ? frame0.Positions : Array.Empty<Vector3>();
            Vector2[] baseUvs = frame0 != null && frame0.HasUvs ? frame0.Uvs : Array.Empty<Vector2>();

            if (basePositions.Length > 0 && m.Faces.Count > 0)
            {
                Vector3[] cornerNormals = ComputeCornerNormals(m);
                var split = new SplitGeometry();
                var groups = new Dictionary<int, List<int>>();   // face material index -> triangle indices

                for (int fi = 0; fi < m.Faces.Count; fi++)
                {
                    VfxFace f = m.Faces[fi];
                    var tri = new int[3];
                    bool valid = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int posIdx = f.Indices[k];
                        if (posIdx < 0 || posIdx >= basePositions.Length) { valid = false; break; }
                        Vector2 uv = 3 * fi + k < baseUvs.Length ? baseUvs[3 * fi + k] : Vector2.Zero;
                        tri[k] = split.Add(posIdx, RfToRh(basePositions[posIdx]), cornerNormals[fi * 3 + k], uv, f.Colors[k]);
                    }
                    if (!valid) continue;

                    if (!groups.TryGetValue(f.MaterialIndex, out List<int>? list))
                    {
                        list = new List<int>();
                        groups[f.MaterialIndex] = list;
                    }
                    // The X negation mirrors the model, so the winding has to flip to keep facing.
                    list.Add(tri[0]); list.Add(tri[2]); list.Add(tri[1]);
                }

                int posAcc = bin.AddVec3(split.Positions, 34962, includeMinMax: true);
                int normAcc = bin.AddVec3(split.Normals, 34962, includeMinMax: false);
                int uvAcc = bin.AddVec2(split.Uvs, 34962);
                int colorAcc = bin.AddVec4(split.Colors, 34962);

                var primitives = new List<MeshPrimitive>();
                foreach (int faceMatIdx in groups.Keys.OrderBy(k => k))
                {
                    List<int> indices = groups[faceMatIdx];
                    if (indices.Count < 3) continue;

                    int? materialIndex = null;
                    if (faceMatIdx >= 0 && faceMatIdx < m.MaterialIndices.Count)
                    {
                        int global = m.MaterialIndices[faceMatIdx];
                        if (global >= 0 && global < table.Count) materialIndex = global;
                    }

                    primitives.Add(new MeshPrimitive
                    {
                        attributes = new Dictionary<string, int>
                        {
                            ["POSITION"] = posAcc,
                            ["NORMAL"] = normAcc,
                            ["TEXCOORD_0"] = uvAcc,
                            ["COLOR_0"] = colorAcc
                        },
                        indices = bin.AddIndices(indices, split.Positions.Count > ushort.MaxValue),
                        material = materialIndex,
                        extras = new Dictionary<string, object>
                        {
                            ["rf_type"] = "vfx_mesh_primitive",
                            ["rf_face_material_index"] = faceMatIdx
                        }
                    });
                }

                // Morph meshes store a full position set per frame; frames 1..N-1 become targets.
                List<Dictionary<string, int>>? targets = null;
                if (m.Morph && m.Frames.Count > 1)
                {
                    targets = new List<Dictionary<string, int>>();
                    for (int fi = 1; fi < m.Frames.Count; fi++)
                    {
                        VfxMeshFrame fr = m.Frames[fi];
                        var deltas = new List<Vector3>(split.Positions.Count);
                        for (int v = 0; v < split.Positions.Count; v++)
                        {
                            int p = split.SourceIndices[v];
                            Vector3 d = fr.HasPositions && p < fr.Positions.Length && p < basePositions.Length
                                ? RfToRh(fr.Positions[p]) - RfToRh(basePositions[p])
                                : Vector3.Zero;
                            deltas.Add(d);
                        }
                        targets.Add(new Dictionary<string, int> { ["POSITION"] = bin.AddVec3(deltas, null, includeMinMax: true) });
                    }
                    foreach (MeshPrimitive prim in primitives)
                        prim.targets = targets;
                    pn.MorphTargetCount = targets.Count;
                    pn.MorphFrameCount = m.Frames.Count;
                    pn.MorphStartTime = m.StartTime;
                    pn.MorphFps = m.FramesPerSecond > 0 ? m.FramesPerSecond : Fps;
                }

                if (primitives.Count > 0)
                {
                    gltfMeshes.Add(new GltfMesh { primitives = primitives, name = m.Name });
                    pn.MeshIndex = gltfMeshes.Count - 1;
                    // Which VFX vertex each split glTF vertex came from. Welding by position would
                    // be ambiguous on a morph mesh, where two vertices can share frame 0 and then
                    // move apart.
                    pn.Extras["rf_gltf_vertex_source"] = split.SourceIndices.ToArray();
                }
            }

            // ── the node transform and its animation ──────────────────────────────────────────
            float fps = m.FramesPerSecond > 0 ? m.FramesPerSecond : Fps;
            if (m.IsKeyframed)
            {
                // pivot is applied before the keyframed transform, so the sampled node transform
                // is keyframe(t) * pivot.
                var pivot = new Trs(RfToRh(m.PivotTranslation), RfToRh(m.PivotRotation), m.PivotScale);
                int count = Math.Max(1, m.Frames.Count);
                for (int i = 0; i < count; i++)
                {
                    int time = (int)MathF.Round(i * 320f);
                    Trs kf = new(
                        RfToRh(VfxKeyframeMath.EvaluateVec3(m.TranslationKeys, time, Vector3.Zero)),
                        RfToRh(VfxKeyframeMath.EvaluateQuat(m.RotationKeys, time, Quaternion.Identity)),
                        VfxKeyframeMath.EvaluateVec3(m.ScaleKeys, time, Vector3.One));
                    pn.Samples.Add((m.StartTime + i / fps, Compose(kf, pivot)));
                }
            }
            else if (!m.Morph)
            {
                for (int i = 0; i < m.Frames.Count; i++)
                {
                    VfxMeshFrame fr = m.Frames[i];
                    if (!fr.HasTransform) continue;
                    pn.Samples.Add((m.StartTime + i / fps,
                        new Trs(RfToRh(fr.Translation), RfToRh(fr.Rotation), fr.Scale)));
                }
            }

            pn.World = pn.Samples.Count > 0 ? pn.Samples[0].Trs : Trs.Identity;
            return pn;
        }

        private static Dictionary<string, object> BuildMeshExtras(VfxMesh m, List<VfxMaterial> table)
        {
            int faceCount = m.Faces.Count;
            var faceIndices = new int[faceCount * 3];
            var faceColors = new float[faceCount * 9];
            var faceNormals = new float[faceCount * 3];
            var faceCenters = new float[faceCount * 3];
            var faceRadii = new float[faceCount];
            var faceMaterial = new int[faceCount];
            var smoothingGroups = new int[faceCount];
            var faceVertexIndices = new int[faceCount * 3];

            for (int i = 0; i < faceCount; i++)
            {
                VfxFace f = m.Faces[i];
                for (int k = 0; k < 3; k++)
                {
                    faceIndices[i * 3 + k] = f.Indices[k];
                    faceVertexIndices[i * 3 + k] = f.FaceVertexIndices[k];
                    faceColors[i * 9 + k * 3 + 0] = f.Colors[k].X;
                    faceColors[i * 9 + k * 3 + 1] = f.Colors[k].Y;
                    faceColors[i * 9 + k * 3 + 2] = f.Colors[k].Z;
                }
                faceNormals[i * 3 + 0] = f.Normal.X;
                faceNormals[i * 3 + 1] = f.Normal.Y;
                faceNormals[i * 3 + 2] = f.Normal.Z;
                faceCenters[i * 3 + 0] = f.Center.X;
                faceCenters[i * 3 + 1] = f.Center.Y;
                faceCenters[i * 3 + 2] = f.Center.Z;
                faceRadii[i] = f.Radius;
                faceMaterial[i] = f.MaterialIndex;
                smoothingGroups[i] = f.SmoothingGroup;
            }

            var fvSmoothing = new int[m.FaceVertices.Count];
            var fvVertexIndex = new int[m.FaceVertices.Count];
            // The uninitialised u/v words are usually 0xCDCDCDCD, which is past int32 and makes
            // Blender give up on the whole dictionary and store it as a Python repr string. They go
            // out base64-encoded for the same reason the compressed positions do.
            var fvUvBytes = new byte[m.FaceVertices.Count * 8];
            var fvAdjacentCounts = new int[m.FaceVertices.Count];
            var fvAdjacent = new List<int>();
            for (int i = 0; i < m.FaceVertices.Count; i++)
            {
                VfxFaceVertex fv = m.FaceVertices[i];
                fvSmoothing[i] = fv.SmoothingGroup;
                fvVertexIndex[i] = fv.VertexIndex;
                BitConverter.TryWriteBytes(fvUvBytes.AsSpan(i * 8 + 0), fv.URaw);
                BitConverter.TryWriteBytes(fvUvBytes.AsSpan(i * 8 + 4), fv.VRaw);
                fvAdjacentCounts[i] = fv.AdjacentFaces.Count;
                fvAdjacent.AddRange(fv.AdjacentFaces);
            }

            // Compressed position frames. The raw s16 stream is kept because requantising the
            // decompressed float positions is not always bit-exact for meshes far from the origin.
            var posFrames = new List<Dictionary<string, object>>();
            var frameSizes = new List<float[]>();
            var uvFrames = new List<Dictionary<string, object>>();
            var frameTransforms = new List<Dictionary<string, object>>();
            var frameOpacity = new List<float>();
            bool anyOpacity = false;

            for (int i = 0; i < m.Frames.Count; i++)
            {
                VfxMeshFrame fr = m.Frames[i];
                if (fr.HasPositions)
                {
                    var entry = new Dictionary<string, object>
                    {
                        ["frame"] = i,
                        ["center"] = Arr(fr.Center),
                        ["multiplier"] = Arr(fr.PositionsMultiplier),
                        ["s16"] = EncodeShorts(fr.RawPositions)
                    };
                    posFrames.Add(entry);
                }
                if (fr.HasSize) frameSizes.Add(new[] { (float)i, fr.Width, fr.Height });
                if (fr.HasUvs)
                {
                    // Frame 0 is also TEXCOORD_0, but keeping it here means the UV table survives
                    // even when a round trip drops the primitive attributes.
                    var flat = new float[fr.Uvs.Length * 2];
                    for (int j = 0; j < fr.Uvs.Length; j++)
                    {
                        flat[j * 2 + 0] = fr.Uvs[j].X;
                        flat[j * 2 + 1] = fr.Uvs[j].Y;
                    }
                    uvFrames.Add(new Dictionary<string, object> { ["frame"] = i, ["uvs"] = flat });
                }
                if (fr.HasTransform)
                {
                    frameTransforms.Add(new Dictionary<string, object>
                    {
                        ["frame"] = i,
                        ["translation"] = Arr(fr.Translation),
                        ["rotation"] = Arr(fr.Rotation),
                        ["scale"] = Arr(fr.Scale)
                    });
                }
                if (fr.HasOpacity) { anyOpacity = true; frameOpacity.Add(fr.Opacity); }
            }

            var extras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx_mesh",
                ["rf_name"] = m.Name,
                ["rf_parent_name"] = m.ParentName ?? "Scene Root",
                ["rf_save_parent"] = m.SaveParent,
                ["rf_flags"] = m.Flags,
                ["rf_flag_facing"] = m.Facing,
                ["rf_flag_no_interp"] = m.NoInterp,
                ["rf_flag_morph"] = m.Morph,
                ["rf_flag_fire"] = m.Fire,
                ["rf_flag_fullbright"] = m.Fullbright,
                ["rf_flag_seethrough"] = m.Seethrough,
                ["rf_flag_corona"] = m.Corona,
                ["rf_flag_sky"] = m.Sky,
                ["rf_flag_dump_uvs"] = m.DumpUvs,
                ["rf_flag_facing_rod"] = m.FacingRod,
                ["rf_fps"] = m.FramesPerSecond,
                ["rf_start_time"] = m.StartTime,
                ["rf_end_time"] = m.EndTime,
                ["rf_num_frames"] = m.Frames.Count,
                ["rf_vertex_count"] = m.VertexCount,
                ["rf_material_indices"] = m.MaterialIndices,
                // The name behind each slot, so the slots can be re-resolved if a viewer loses the
                // material table's ordering.
                ["rf_material_names"] = m.MaterialIndices
                    .Select(i => i >= 0 && i < table.Count ? MaterialIdentity(table[i]) : string.Empty)
                    .ToList(),
                ["rf_bounding_center"] = Arr(m.BoundingCenter),
                ["rf_bounding_radius"] = m.BoundingRadius,
                ["rf_is_keyframed"] = m.IsKeyframed,
                ["rf_smoothing_groups"] = smoothingGroups,
                ["rf_face_material_index"] = faceMaterial,
                ["rf_face_indices"] = faceIndices,
                ["rf_face_colors"] = faceColors,
                ["rf_face_normals"] = faceNormals,
                ["rf_face_centers"] = faceCenters,
                ["rf_face_radii"] = faceRadii,
                ["rf_face_vertex_indices"] = faceVertexIndices,
                ["rf_face_vertex_raw"] = new Dictionary<string, object>
                {
                    ["smoothing"] = fvSmoothing,
                    ["vertex_index"] = fvVertexIndex,
                    ["uv_bits_b64"] = Convert.ToBase64String(fvUvBytes),
                    ["adjacent_counts"] = fvAdjacentCounts,
                    ["adjacent"] = fvAdjacent
                },
                ["rf_pos_frames"] = posFrames
            };

            if (frameSizes.Count > 0) extras["rf_frame_sizes"] = frameSizes;
            if (uvFrames.Count > 0) extras["rf_uv_frames"] = uvFrames;
            if (frameTransforms.Count > 0) extras["rf_frame_transforms"] = frameTransforms;
            if (anyOpacity) extras["rf_frame_opacity"] = frameOpacity;

            VfxMeshFrame? f0 = m.Frames.Count > 0 ? m.Frames[0] : null;
            if (f0 != null && f0.HasSize)
            {
                extras["rf_width"] = f0.Width;
                extras["rf_height"] = f0.Height;
            }
            if (f0 != null && f0.HasUpVector)
                extras["rf_up_vector"] = Arr(f0.UpVector);

            if (m.HasPivot || m.IsKeyframed)
            {
                extras["rf_pivot_translation"] = Arr(m.PivotTranslation);
                extras["rf_pivot_rotation"] = Arr(m.PivotRotation);
                extras["rf_pivot_scale"] = Arr(m.PivotScale);
            }

            if (m.IsKeyframed)
                extras["rf_keyframes"] = BuildKeyframeExtras(m.TranslationKeys, m.RotationKeys, m.ScaleKeys);

            return extras;
        }

        private static Dictionary<string, object> BuildKeyframeExtras(
            List<VfxVec3Key> translation, List<VfxQuatKey> rotation, List<VfxVec3Key> scale)
        {
            static List<Dictionary<string, object>> Vec3List(List<VfxVec3Key> keys)
                => keys.Select(k => new Dictionary<string, object>
                {
                    ["time"] = k.Time,
                    ["value"] = Arr(k.Value),
                    ["in_tangent"] = Arr(k.InTangent),
                    ["out_tangent"] = Arr(k.OutTangent)
                }).ToList();

            return new Dictionary<string, object>
            {
                ["translation"] = Vec3List(translation),
                ["rotation"] = rotation.Select(k => new Dictionary<string, object>
                {
                    ["time"] = k.Time,
                    ["value"] = Arr(k.Value),
                    ["tension"] = k.Tension,
                    ["continuity"] = k.Continuity,
                    ["bias"] = k.Bias,
                    ["ease_in"] = k.EaseIn,
                    ["ease_out"] = k.EaseOut
                }).ToList(),
                ["scale"] = Vec3List(scale)
            };
        }

        // Vertex normals follow RF's own rule: a face with smoothing group 0 is flat, otherwise the
        // corner blends the normals of every face that shares the position and a smoothing bit.
        private static Vector3[] ComputeCornerNormals(VfxMesh m)
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
                    list.Add(fi);
                }
            }

            var result = new Vector3[m.Faces.Count * 3];
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                VfxFace f = m.Faces[fi];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 n = f.Normal;
                    if (f.SmoothingGroup != 0 && facesByVertex.TryGetValue(f.Indices[k], out List<int>? shared))
                    {
                        Vector3 acc = Vector3.Zero;
                        foreach (int other in shared)
                        {
                            if ((m.Faces[other].SmoothingGroup & f.SmoothingGroup) != 0)
                                acc += m.Faces[other].Normal;
                        }
                        if (acc.LengthSquared() > 1e-10f) n = Vector3.Normalize(acc);
                    }
                    Vector3 rh = RfToRh(n);
                    result[fi * 3 + k] = rh.LengthSquared() > 1e-10f ? Vector3.Normalize(rh) : new Vector3(0f, 1f, 0f);
                }
            }
            return result;
        }

        // ─── other sections ────────────────────────────────────────────────────────────────────

        private static PendingNode BuildDummyNode(VfxDummy d)
        {
            var pn = new PendingNode
            {
                Name = d.Name,
                RfParentName = d.ParentName,
                Extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "vfx_dummy",
                    ["rf_name"] = d.Name,
                    ["rf_parent_name"] = d.ParentName ?? "Scene Root",
                    ["rf_save_parent"] = d.SaveParent,
                    ["rf_pos"] = Arr(d.Pos),
                    ["rf_orient"] = Arr(d.Orient),
                    ["rf_frames"] = d.Frames.Select(f => new Dictionary<string, object>
                    {
                        ["pos"] = Arr(f.Pos),
                        ["orient"] = Arr(f.Orient)
                    }).ToList()
                }
            };

            pn.World = new Trs(RfToRh(d.Pos), RfToRh(d.Orient), Vector3.One);
            for (int i = 0; i < d.Frames.Count; i++)
                pn.Samples.Add((i / Fps, new Trs(RfToRh(d.Frames[i].Pos), RfToRh(d.Frames[i].Orient), Vector3.One)));
            if (pn.Samples.Count > 0) pn.World = pn.Samples[0].Trs;
            return pn;
        }

        private static PendingNode BuildParticleNode(VfxParticleSystem p)
        {
            var extras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx_particle_system",
                ["rf_name"] = p.Name,
                ["rf_parent_name"] = p.ParentName ?? "Scene Root",
                ["rf_save_parent"] = p.SaveParent,
                ["rf_flags"] = p.Flags,
                ["rf_flag_apply_gravity"] = p.ApplyGravity,
                ["rf_flag_randomize_orientation"] = p.RandomizeOrientation,
                ["rf_flag_no_cull"] = p.NoCull,
                ["rf_flag_drops"] = p.Drops,
                ["rf_warps"] = p.Warps,
                ["rf_start_time"] = p.StartTime,
                ["rf_num_frames"] = p.Frames.Count,
                ["rf_material_index"] = p.MaterialIndex,
                ["rf_particle_count"] = p.ParticleCount,
                ["rf_start"] = p.Start,
                ["rf_lifetime"] = p.Lifetime,
                ["rf_lifetime_variation"] = p.LifetimeVariation,
                ["rf_emitter_type"] = p.EmitterType,
                ["rf_shrink_at_birth"] = p.ShrinkAtBirth,
                ["rf_shrink_at_death"] = p.ShrinkAtDeath,
                ["rf_fade_at_birth"] = p.FadeAtBirth,
                ["rf_fade_at_death"] = p.FadeAtDeath,
                ["rf_frame_pos"] = p.Frames.SelectMany(f => new[] { f.Pos.X, f.Pos.Y, f.Pos.Z }).ToArray(),
                ["rf_frame_orient"] = p.Frames.SelectMany(f => new[] { f.Orient.X, f.Orient.Y, f.Orient.Z, f.Orient.W }).ToArray(),
                ["rf_frame_width"] = p.Frames.Select(f => f.Width).ToArray(),
                ["rf_frame_height"] = p.Frames.Select(f => f.Height).ToArray(),
                ["rf_frame_drop_size"] = p.Frames.Select(f => f.DropSize).ToArray(),
                ["rf_frame_speed"] = p.Frames.Select(f => f.Speed).ToArray(),
                ["rf_frame_speed_variation"] = p.Frames.Select(f => f.SpeedVariation).ToArray(),
                ["rf_frame_birth_rate"] = p.Frames.Select(f => f.BirthRate).ToArray()
            };
            if (p.HasTailDistance) extras["rf_tail_distance"] = p.TailDistance;
            if (p.Frames.Count > 0 && p.Frames[0].HasOpacity)
                extras["rf_frame_opacity"] = p.Frames.Select(f => f.Opacity).ToArray();

            var pn = new PendingNode { Name = p.Name, RfParentName = p.ParentName, Extras = extras };
            for (int i = 0; i < p.Frames.Count; i++)
                pn.Samples.Add(((p.StartTime + i) / Fps,
                    new Trs(RfToRh(p.Frames[i].Pos), RfToRh(p.Frames[i].Orient), Vector3.One)));
            pn.World = pn.Samples.Count > 0 ? pn.Samples[0].Trs : Trs.Identity;
            return pn;
        }

        private static PendingNode BuildLightNode(VfxLight l)
        {
            static Dictionary<string, object> Params(VfxLightParams p) => new()
            {
                ["pos"] = Arr(p.Pos),
                ["radius"] = p.Radius,
                ["multiplier"] = p.Multiplier,
                ["color"] = Arr(p.Color),
                ["is_on"] = p.IsOn
            };

            var pn = new PendingNode
            {
                Name = l.Name,
                RfParentName = l.ParentName,
                Extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "vfx_light",
                    ["rf_name"] = l.Name,
                    ["rf_parent_name"] = l.ParentName ?? "Scene Root",
                    ["rf_save_parent"] = l.SaveParent,
                    ["rf_params"] = Params(l.Params),
                    ["rf_frames"] = l.Frames.Select(Params).ToList()
                }
            };
            pn.World = new Trs(RfToRh(l.Params.Pos), Quaternion.Identity, Vector3.One);
            for (int i = 0; i < l.Frames.Count; i++)
                pn.Samples.Add((i / Fps, new Trs(RfToRh(l.Frames[i].Pos), Quaternion.Identity, Vector3.One)));
            if (pn.Samples.Count > 0) pn.World = pn.Samples[0].Trs;
            return pn;
        }

        private static PendingNode BuildSpacewarpNode(VfxSpacewarp w)
        {
            var pn = new PendingNode
            {
                Name = w.Name,
                RfParentName = w.ParentName,
                Extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "vfx_spacewarp",
                    ["rf_name"] = w.Name,
                    ["rf_parent_name"] = w.ParentName ?? "Scene Root",
                    ["rf_warp_type"] = w.Type,
                    ["rf_frame_pos"] = w.Frames.SelectMany(f => new[] { f.Pos.X, f.Pos.Y, f.Pos.Z }).ToArray(),
                    ["rf_frame_orient"] = w.Frames.SelectMany(f => new[] { f.Orient.X, f.Orient.Y, f.Orient.Z, f.Orient.W }).ToArray(),
                    ["rf_frame_strength"] = w.Frames.Select(f => f.Strength).ToArray(),
                    ["rf_frame_decay"] = w.Frames.Select(f => f.Decay).ToArray(),
                    ["rf_frame_turbulence"] = w.Frames.Select(f => f.Turbulence).ToArray(),
                    ["rf_frame_frequency"] = w.Frames.Select(f => f.Frequency).ToArray(),
                    ["rf_frame_scale"] = w.Frames.Select(f => f.Scale).ToArray()
                }
            };
            for (int i = 0; i < w.Frames.Count; i++)
                pn.Samples.Add((i / Fps, new Trs(RfToRh(w.Frames[i].Pos), RfToRh(w.Frames[i].Orient), Vector3.One)));
            pn.World = pn.Samples.Count > 0 ? pn.Samples[0].Trs : Trs.Identity;
            return pn;
        }

        private static PendingNode BuildCameraNode(VfxCamera c)
        {
            var pn = new PendingNode
            {
                Name = c.Name,
                RfParentName = c.ParentName,
                Extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "vfx_camera",
                    ["rf_name"] = c.Name,
                    ["rf_parent_name"] = c.ParentName ?? "Scene Root",
                    ["rf_start_frame"] = c.StartFrame,
                    ["rf_end_frame"] = c.EndFrame,
                    ["rf_frames"] = c.Frames.Select(f => new Dictionary<string, object>
                    {
                        ["pos"] = Arr(f.Pos),
                        ["orient"] = Arr(f.Orient)
                    }).ToList()
                }
            };
            for (int i = 0; i < c.Frames.Count; i++)
                pn.Samples.Add(((c.StartFrame + i) / Fps,
                    new Trs(RfToRh(c.Frames[i].Pos), RfToRh(c.Frames[i].Orient), Vector3.One)));
            pn.World = pn.Samples.Count > 0 ? pn.Samples[0].Trs : Trs.Identity;
            return pn;
        }

        private static PendingNode BuildChainNode(VfxChain c, BinBuilder bin, List<GltfMesh> gltfMeshes)
        {
            var posFrames = new List<Dictionary<string, object>>();
            for (int i = 0; i < c.Frames.Count; i++)
            {
                VfxChainFrame fr = c.Frames[i];
                if (!fr.HasPositions) continue;
                posFrames.Add(new Dictionary<string, object>
                {
                    ["frame"] = i,
                    ["center"] = Arr(fr.Center),
                    ["multiplier"] = Arr(fr.PositionsMultiplier),
                    ["s16"] = EncodeShorts(fr.RawPositions)
                });
            }

            var extras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx_chain",
                ["rf_name"] = c.Name,
                ["rf_parent_name"] = c.ParentName ?? "Scene Root",
                ["rf_save_parent"] = c.SaveParent,
                ["rf_vertex_count"] = c.VertexCount,
                ["rf_width"] = c.Width,
                ["rf_glow_name"] = c.GlowName ?? string.Empty,
                ["rf_flags"] = c.Flags,
                ["rf_fps"] = c.FramesPerSecond,
                ["rf_start_time"] = c.StartTime,
                ["rf_end_time"] = c.EndTime,
                ["rf_num_frames"] = c.Frames.Count,
                ["rf_is_keyframed"] = c.IsKeyframed,
                ["rf_pos_frames"] = posFrames,
                ["rf_frame_visible"] = c.Frames.Select(f => f.Visible).ToArray(),
                ["rf_frame_transforms"] = c.Frames
                    .Select((f, i) => (f, i))
                    .Where(x => x.f.HasTransform)
                    .Select(x => new Dictionary<string, object>
                    {
                        ["frame"] = x.i,
                        ["translation"] = Arr(x.f.Translation),
                        ["rotation"] = Arr(x.f.Rotation),
                        ["scale"] = Arr(x.f.Scale)
                    }).ToList()
            };
            if (c.HasBaseTransform || c.IsKeyframed)
            {
                extras["rf_base_translation"] = Arr(c.BaseTranslation);
                extras["rf_base_rotation"] = Arr(c.BaseRotation);
                extras["rf_base_scale"] = Arr(c.BaseScale);
            }
            if (c.IsKeyframed)
                extras["rf_keyframes"] = BuildKeyframeExtras(c.TranslationKeys, c.RotationKeys, c.ScaleKeys);

            var pn = new PendingNode { Name = c.Name, RfParentName = c.ParentName, Extras = extras };

            // A line strip through frame 0 so the spline is visible in a viewer.
            if (c.Frames.Count > 0 && c.Frames[0].HasPositions && c.Frames[0].Positions.Length >= 2)
            {
                var pts = c.Frames[0].Positions.Select(RfToRh).ToList();
                int posAcc = bin.AddVec3(pts, 34962, includeMinMax: true);
                var strip = Enumerable.Range(0, pts.Count).ToList();
                gltfMeshes.Add(new GltfMesh
                {
                    name = c.Name,
                    primitives = new List<MeshPrimitive>
                    {
                        new MeshPrimitive
                        {
                            attributes = new Dictionary<string, int> { ["POSITION"] = posAcc },
                            indices = bin.AddIndices(strip, pts.Count > ushort.MaxValue),
                            mode = 3 // LINE_STRIP
                        }
                    }
                });
                pn.MeshIndex = gltfMeshes.Count - 1;
            }

            float fps = c.FramesPerSecond > 0 ? c.FramesPerSecond : Fps;
            for (int i = 0; i < c.Frames.Count; i++)
            {
                VfxChainFrame fr = c.Frames[i];
                if (!fr.HasTransform) continue;
                pn.Samples.Add((c.StartTime + i / fps, new Trs(RfToRh(fr.Translation), RfToRh(fr.Rotation), fr.Scale)));
            }
            pn.World = pn.Samples.Count > 0 ? pn.Samples[0].Trs : Trs.Identity;
            return pn;
        }

        private static PendingNode BuildMaterialModifierNode(VfxMaterialModifier mm, int ordinal) => new()
        {
            Name = $"rf_mmod_{ordinal}",
            RfParentName = "Scene Root",
            Extras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx_material_modifier",
                ["rf_material_index"] = mm.MaterialIndex
            }
        };

        private static PendingNode BuildUnknownNode(VfxUnknownSection u, int ordinal) => new()
        {
            Name = $"rf_{VfxSectionType.ToTag(u.RawTypeId)}_{ordinal}",
            RfParentName = "Scene Root",
            Extras = new Dictionary<string, object>
            {
                ["rf_type"] = "vfx_unknown",
                ["rf_section_type"] = u.RawTypeId,
                ["rf_section_tag"] = VfxSectionType.ToTag(u.RawTypeId),
                ["rf_raw_base64"] = Convert.ToBase64String(u.Data)
            }
        };

        // ─── hierarchy and animation ───────────────────────────────────────────────────────────

        // Sections carry absolute transforms, so a node parented under a moving object would pick
        // up motion that is not in the source. Parenting is applied only under a static parent,
        // with the parent's transform divided out; anything else stays on the root. rf_parent_name
        // records the authored hierarchy either way.
        private static void AssignHierarchy(List<PendingNode> pending, List<Node> nodes, List<int> rootChildren)
        {
            var byName = new Dictionary<string, PendingNode>(StringComparer.Ordinal);
            foreach (PendingNode pn in pending)
            {
                if (!string.IsNullOrEmpty(pn.Name) && !byName.ContainsKey(pn.Name))
                    byName[pn.Name] = pn;
            }

            foreach (PendingNode pn in pending)
            {
                PendingNode? parent = null;
                string parentName = pn.RfParentName ?? "Scene Root";
                if (!string.Equals(parentName, "Scene Root", StringComparison.Ordinal) &&
                    byName.TryGetValue(parentName, out PendingNode? candidate) &&
                    !ReferenceEquals(candidate, pn) &&
                    !candidate.IsAnimated &&
                    !CreatesCycle(candidate, pn, byName))
                {
                    parent = candidate;
                }

                Trs local = pn.World;
                if (parent != null)
                {
                    local = Relative(pn.World, parent.World);
                    nodes[parent.NodeIndex].children!.Add(pn.NodeIndex);
                }
                else
                {
                    rootChildren.Add(pn.NodeIndex);
                }

                pn.ParentStatic = parent?.World ?? Trs.Identity;
                Node node = nodes[pn.NodeIndex];
                node.translation = Arr(local.T);
                node.rotation = Arr(local.R);
                node.scale = Arr(local.S);
            }
        }

        private static bool CreatesCycle(PendingNode candidateParent, PendingNode child, Dictionary<string, PendingNode> byName)
        {
            PendingNode? cur = candidateParent;
            for (int guard = 0; cur != null && guard < 64; guard++)
            {
                if (ReferenceEquals(cur, child)) return true;
                string p = cur.RfParentName ?? "Scene Root";
                if (string.Equals(p, "Scene Root", StringComparison.Ordinal)) return false;
                byName.TryGetValue(p, out cur);
            }
            return false;
        }

        private static void EmitAnimation(
            PendingNode pn,
            BinBuilder bin,
            List<AnimationSampler> samplers,
            List<AnimationChannel> channels)
        {
            if (pn.MorphTargetCount > 0 && pn.MorphFrameCount > 1)
            {
                // One-hot weights, held with STEP so a frame shows exactly its own shape.
                var times = new List<float>(pn.MorphFrameCount);
                var weights = new List<float>(pn.MorphFrameCount * pn.MorphTargetCount);
                for (int i = 0; i < pn.MorphFrameCount; i++)
                {
                    times.Add(pn.MorphStartTime + i / (pn.MorphFps > 0 ? pn.MorphFps : Fps));
                    for (int t = 0; t < pn.MorphTargetCount; t++)
                        weights.Add(t == i - 1 ? 1f : 0f);
                }
                samplers.Add(new AnimationSampler
                {
                    input = bin.AddScalar(times),
                    output = bin.AddScalarRaw(weights),
                    interpolation = "STEP"
                });
                channels.Add(new AnimationChannel
                {
                    sampler = samplers.Count - 1,
                    target = new AnimationTarget { node = pn.NodeIndex, path = "weights" }
                });
            }

            if (pn.Samples.Count < 2) return;

            var trs = pn.Samples.Select(s => Relative(s.Trs, pn.ParentStatic)).ToList();
            var sampleTimes = pn.Samples.Select(s => s.Time).ToList();

            bool movesT = trs.Any(v => (v.T - trs[0].T).LengthSquared() > 1e-12f);
            bool movesR = trs.Any(v => MathF.Abs(Quaternion.Dot(v.R, trs[0].R)) < 1f - 1e-7f);
            bool movesS = trs.Any(v => (v.S - trs[0].S).LengthSquared() > 1e-12f);

            if (movesT)
            {
                samplers.Add(new AnimationSampler
                {
                    input = bin.AddScalar(sampleTimes),
                    output = bin.AddVec3(trs.Select(v => v.T).ToList(), null, includeMinMax: false),
                    interpolation = "LINEAR"
                });
                channels.Add(new AnimationChannel
                {
                    sampler = samplers.Count - 1,
                    target = new AnimationTarget { node = pn.NodeIndex, path = "translation" }
                });
            }
            if (movesR)
            {
                samplers.Add(new AnimationSampler
                {
                    input = bin.AddScalar(sampleTimes),
                    output = bin.AddQuat(trs.Select(v => v.R).ToList()),
                    interpolation = "LINEAR"
                });
                channels.Add(new AnimationChannel
                {
                    sampler = samplers.Count - 1,
                    target = new AnimationTarget { node = pn.NodeIndex, path = "rotation" }
                });
            }
            if (movesS)
            {
                samplers.Add(new AnimationSampler
                {
                    input = bin.AddScalar(sampleTimes),
                    output = bin.AddVec3(trs.Select(v => v.S).ToList(), null, includeMinMax: false),
                    interpolation = "LINEAR"
                });
                channels.Add(new AnimationChannel
                {
                    sampler = samplers.Count - 1,
                    target = new AnimationTarget { node = pn.NodeIndex, path = "scale" }
                });
            }
        }

        private static Trs Compose(Trs outer, Trs inner)
        {
            Quaternion r = Quaternion.Normalize(Quaternion.Multiply(outer.R, inner.R));
            Vector3 s = outer.S * inner.S;
            Vector3 t = outer.T + Vector3.Transform(outer.S * inner.T, outer.R);
            return new Trs(t, r, s);
        }

        private static Trs Relative(Trs world, Trs parent)
        {
            if (parent.IsIdentity) return world;
            Quaternion invR = Quaternion.Conjugate(Quaternion.Normalize(parent.R));
            Vector3 invS = new(
                MathF.Abs(parent.S.X) > 1e-9f ? 1f / parent.S.X : 1f,
                MathF.Abs(parent.S.Y) > 1e-9f ? 1f / parent.S.Y : 1f,
                MathF.Abs(parent.S.Z) > 1e-9f ? 1f / parent.S.Z : 1f);
            Vector3 t = Vector3.Transform(world.T - parent.T, invR) * invS;
            Quaternion r = Quaternion.Normalize(Quaternion.Multiply(invR, world.R));
            Vector3 s = world.S * invS;
            return new Trs(t, r, s);
        }

        private static string EncodeShorts(short[] values)
        {
            var bytes = new byte[values.Length * 2];
            System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        // ─── helper types ──────────────────────────────────────────────────────────────────────

        internal readonly struct Trs
        {
            public readonly Vector3 T;
            public readonly Quaternion R;
            public readonly Vector3 S;

            public Trs(Vector3 t, Quaternion r, Vector3 s)
            {
                T = t;
                R = r.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(r);
                S = s;
            }

            public static Trs Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

            public bool IsIdentity =>
                T.LengthSquared() < 1e-16f &&
                MathF.Abs(R.W - 1f) < 1e-7f && new Vector3(R.X, R.Y, R.Z).LengthSquared() < 1e-14f &&
                (S - Vector3.One).LengthSquared() < 1e-14f;
        }

        private sealed class PendingNode
        {
            public string Name = string.Empty;
            public string? RfParentName;
            public Dictionary<string, object> Extras = new();
            public int MeshIndex = -1;
            public int NodeIndex = -1;
            public Trs World = Trs.Identity;
            public Trs ParentStatic = Trs.Identity;
            public List<(float Time, Trs Trs)> Samples = new();
            public int MorphTargetCount;
            public int MorphFrameCount;
            public float MorphStartTime;
            public float MorphFps = Fps;

            public bool IsAnimated
            {
                get
                {
                    if (MorphTargetCount > 0) return true;
                    if (Samples.Count < 2) return false;
                    Trs first = Samples[0].Trs;
                    foreach ((float _, Trs t) in Samples)
                    {
                        if ((t.T - first.T).LengthSquared() > 1e-12f) return true;
                        if (MathF.Abs(Quaternion.Dot(t.R, first.R)) < 1f - 1e-7f) return true;
                        if ((t.S - first.S).LengthSquared() > 1e-12f) return true;
                    }
                    return false;
                }
            }
        }

        private sealed class SplitGeometry
        {
            public readonly List<Vector3> Positions = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector2> Uvs = new();
            public readonly List<Vector4> Colors = new();
            public readonly List<int> SourceIndices = new();
            private readonly Dictionary<(int, long, long, long, long, long, long, long, long), int> map = new();

            public int Add(int sourceIndex, Vector3 position, Vector3 normal, Vector2 uv, Vector3 color)
            {
                var key = (sourceIndex,
                    Q(uv.X), Q(uv.Y),
                    Q(color.X), Q(color.Y), Q(color.Z),
                    Q(normal.X), Q(normal.Y), Q(normal.Z));
                if (map.TryGetValue(key, out int existing))
                    return existing;

                int index = Positions.Count;
                map[key] = index;
                Positions.Add(position);
                Normals.Add(normal);
                Uvs.Add(uv);
                Colors.Add(new Vector4(color.X, color.Y, color.Z, 1f));
                SourceIndices.Add(sourceIndex);
                return index;
            }

            private static long Q(float v) => float.IsFinite(v) ? (long)MathF.Round(v * 1000000f) : 0L;
        }

        // ─── buffer building ───────────────────────────────────────────────────────────────────

        private sealed class BinBuilder
        {
            private readonly List<byte> data = new();
            public readonly List<BufferView> BufferViews = new();
            public readonly List<Accessor> Accessors = new();

            public int Length => data.Count;
            public byte[] ToArray() => data.ToArray();

            private int AddView(byte[] bytes, int? target)
            {
                while (data.Count % 4 != 0) data.Add(0);
                int offset = data.Count;
                data.AddRange(bytes);
                BufferViews.Add(new BufferView { buffer = 0, byteOffset = offset, byteLength = bytes.Length, target = target });
                return BufferViews.Count - 1;
            }

            public int AddVec3(List<Vector3> values, int? target, bool includeMinMax)
            {
                var bytes = new byte[values.Count * 12];
                for (int i = 0; i < values.Count; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 12 + 0), values[i].X);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 12 + 4), values[i].Y);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 12 + 8), values[i].Z);
                }
                int view = AddView(bytes, target);
                float[]? min = null, max = null;
                if (includeMinMax && values.Count > 0)
                {
                    min = new[] { values.Min(v => v.X), values.Min(v => v.Y), values.Min(v => v.Z) };
                    max = new[] { values.Max(v => v.X), values.Max(v => v.Y), values.Max(v => v.Z) };
                }
                Accessors.Add(new Accessor { bufferView = view, componentType = 5126, count = values.Count, type = "VEC3", min = min, max = max });
                return Accessors.Count - 1;
            }

            public int AddVec2(List<Vector2> values, int? target)
            {
                var bytes = new byte[values.Count * 8];
                for (int i = 0; i < values.Count; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 0), values[i].X);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), values[i].Y);
                }
                int view = AddView(bytes, target);
                Accessors.Add(new Accessor { bufferView = view, componentType = 5126, count = values.Count, type = "VEC2" });
                return Accessors.Count - 1;
            }

            public int AddVec4(List<Vector4> values, int? target)
            {
                var bytes = new byte[values.Count * 16];
                for (int i = 0; i < values.Count; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 0), values[i].X);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 4), values[i].Y);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 8), values[i].Z);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 12), values[i].W);
                }
                int view = AddView(bytes, target);
                Accessors.Add(new Accessor { bufferView = view, componentType = 5126, count = values.Count, type = "VEC4" });
                return Accessors.Count - 1;
            }

            public int AddQuat(List<Quaternion> values)
            {
                var bytes = new byte[values.Count * 16];
                for (int i = 0; i < values.Count; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 0), values[i].X);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 4), values[i].Y);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 8), values[i].Z);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 12), values[i].W);
                }
                int view = AddView(bytes, null);
                Accessors.Add(new Accessor { bufferView = view, componentType = 5126, count = values.Count, type = "VEC4" });
                return Accessors.Count - 1;
            }

            public int AddScalar(List<float> values)
            {
                int acc = AddScalarRaw(values);
                if (values.Count > 0)
                {
                    Accessors[acc].min = new[] { values.Min() };
                    Accessors[acc].max = new[] { values.Max() };
                }
                return acc;
            }

            public int AddScalarRaw(List<float> values)
            {
                var bytes = new byte[values.Count * 4];
                for (int i = 0; i < values.Count; i++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), values[i]);
                int view = AddView(bytes, null);
                Accessors.Add(new Accessor { bufferView = view, componentType = 5126, count = values.Count, type = "SCALAR" });
                return Accessors.Count - 1;
            }

            public int AddIndices(List<int> indices, bool useU32)
            {
                byte[] bytes;
                if (useU32)
                {
                    bytes = new byte[indices.Count * 4];
                    for (int i = 0; i < indices.Count; i++)
                        BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (uint)Math.Max(0, indices[i]));
                }
                else
                {
                    bytes = new byte[indices.Count * 2];
                    for (int i = 0; i < indices.Count; i++)
                        BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (ushort)Math.Clamp(indices[i], 0, ushort.MaxValue));
                }
                int view = AddView(bytes, 34963);
                Accessors.Add(new Accessor
                {
                    bufferView = view,
                    componentType = useU32 ? 5125 : 5123,
                    count = indices.Count,
                    type = "SCALAR",
                    min = indices.Count > 0 ? new[] { (float)indices.Min() } : null,
                    max = indices.Count > 0 ? new[] { (float)indices.Max() } : null
                });
                return Accessors.Count - 1;
            }
        }

        // ─── glTF DTOs ─────────────────────────────────────────────────────────────────────────

        private class GltfRoot
        {
            public required Asset asset { get; set; }
            public required List<BufferDef> buffers { get; set; }
            public required List<BufferView> bufferViews { get; set; }
            public required List<Accessor> accessors { get; set; }
            public List<GltfMesh>? meshes { get; set; }
            public required List<Node> nodes { get; set; }
            public List<Animation>? animations { get; set; }
            public List<Material>? materials { get; set; }
            public List<TextureDef>? textures { get; set; }
            public List<ImageDef>? images { get; set; }
            public List<SamplerDef>? samplers { get; set; }
            public required List<Scene> scenes { get; set; }
            public int scene { get; set; }
        }

        private class Asset
        {
            public required string version { get; set; }
            public required string generator { get; set; }
        }

        private class BufferDef
        {
            public required string uri { get; set; }
            public int byteLength { get; set; }
        }

        internal class BufferView
        {
            public int buffer { get; set; }
            public int byteOffset { get; set; }
            public int byteLength { get; set; }
            public int? target { get; set; }
        }

        internal class Accessor
        {
            public int bufferView { get; set; }
            public int byteOffset { get; set; }
            public int componentType { get; set; }
            public int count { get; set; }
            public required string type { get; set; }
            public float[]? min { get; set; }
            public float[]? max { get; set; }
        }

        private class GltfMesh
        {
            public string? name { get; set; }
            public required List<MeshPrimitive> primitives { get; set; }
        }

        private class MeshPrimitive
        {
            public required Dictionary<string, int> attributes { get; set; }
            public int indices { get; set; }
            public int? material { get; set; }
            public int? mode { get; set; }
            public List<Dictionary<string, int>>? targets { get; set; }
            public Dictionary<string, object>? extras { get; set; }
        }

        private class Material
        {
            public string? name { get; set; }
            public bool? doubleSided { get; set; }
            public string? alphaMode { get; set; }
            public float[]? emissiveFactor { get; set; }
            public PbrMetallicRoughness? pbrMetallicRoughness { get; set; }
            public Dictionary<string, object>? extras { get; set; }
        }

        private class PbrMetallicRoughness
        {
            public float[]? baseColorFactor { get; set; }
            public float? metallicFactor { get; set; }
            public float? roughnessFactor { get; set; }
            public TextureInfo? baseColorTexture { get; set; }
        }

        private class TextureInfo
        {
            public int index { get; set; }
        }

        private class TextureDef
        {
            public int? sampler { get; set; }
            public int source { get; set; }
        }

        private class ImageDef
        {
            public string? uri { get; set; }
            public string? name { get; set; }
        }

        private class SamplerDef
        {
            public int? magFilter { get; set; }
            public int? minFilter { get; set; }
            public int? wrapS { get; set; }
            public int? wrapT { get; set; }
        }

        private class Node
        {
            public string? name { get; set; }
            public int? mesh { get; set; }
            public float[]? translation { get; set; }
            public float[]? rotation { get; set; }
            public float[]? scale { get; set; }
            public float[]? weights { get; set; }
            public List<int>? children { get; set; }
            public Dictionary<string, object>? extras { get; set; }
        }

        private class Scene
        {
            public required List<int> nodes { get; set; }
        }

        private class Animation
        {
            public string? name { get; set; }
            public required List<AnimationSampler> samplers { get; set; }
            public required List<AnimationChannel> channels { get; set; }
        }

        private class AnimationSampler
        {
            public int input { get; set; }
            public int output { get; set; }
            public string interpolation { get; set; } = "LINEAR";
        }

        private class AnimationChannel
        {
            public int sampler { get; set; }
            public required AnimationTarget target { get; set; }
        }

        private class AnimationTarget
        {
            public int node { get; set; }
            public required string path { get; set; }
        }
    }
}
