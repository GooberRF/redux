using redux.exporters;
using redux.utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace redux.parsers
{
    // Rebuilds a VfxFile from a glTF written by VfxGltfExporter. Node and material `extras` are
    // authoritative for everything they carry; the glTF geometry and animation are only consulted
    // when the extras are missing (a viewer stripped them) or when they disagree with the extras,
    // which is how an edit made in Blender gets picked up.
    public static class VfxGltfParser
    {
        private const string logSrc = "VfxGltfParser";
        private const float Fps = 15f;
        // A transform sample counts as edited once it moves further than this from the authored one.
        private const float TransformEpsilon = 1e-4f;

        public static bool IsVfxGltf(string path)
        {
            try
            {
                using FileStream fs = File.OpenRead(path);
                using JsonDocument doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("nodes", out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    if (!node.TryGetProperty("extras", out JsonElement extras) || extras.ValueKind != JsonValueKind.Object)
                        continue;
                    if (extras.TryGetProperty("rf_type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                    {
                        string? v = t.GetString();
                        if (v == "vfx" || (v != null && v.StartsWith("vfx_", StringComparison.Ordinal)))
                            return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static VfxFile ReadVfxGltf(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            var ctx = new Context
            {
                Root = root,
                BaseDir = Path.GetDirectoryName(path) ?? string.Empty,
                SourceName = Path.GetFileNameWithoutExtension(path)
            };
            ctx.Load();

            var file = new VfxFile { SourceName = ctx.SourceName, Version = VfxFile.CurrentVersion };

            JsonElement? rootExtras = ctx.FindRootExtras();
            if (rootExtras.HasValue)
            {
                file.HeaderFlags = GetInt(rootExtras.Value, "rf_header_flags", 0);
                file.EndFrame = GetInt(rootExtras.Value, "rf_end_frame", 0);
                file.SelSetObjectCount = GetInt(rootExtras.Value, "rf_selset_object_count", 0);
            }

            List<VfxMaterial> materials = BuildMaterials(root, rootExtras, out int[] gltfMaterialToGlobal);
            ctx.GltfMaterialToGlobal = gltfMaterialToGlobal;
            ctx.MaterialTable = materials;
            List<VfxSection> nodeSections = BuildNodeSections(ctx, materials.Count);

            // Section order is recorded on the root node so the file layout can be reproduced.
            var order = new List<(string Type, string Name)>();
            JsonElement so = rootExtras.HasValue ? Resolve(rootExtras.Value, "rf_section_order") : default;
            if (so.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in so.EnumerateArray())
                    order.Add((GetString(e, "type", string.Empty), GetString(e, "name", string.Empty)));
            }

            if (order.Count > 0)
            {
                // Blender sorts objects by name, so the glTF node order says nothing about the file
                // layout. Each recorded entry claims the section of that type with that name.
                var pending = new List<VfxSection>(nodeSections);
                var materialQueue = new Queue<VfxMaterial>(materials);

                foreach ((string tag, string name) in order)
                {
                    if (tag == "MATL")
                    {
                        if (materialQueue.Count > 0) file.Sections.Add(materialQueue.Dequeue());
                        continue;
                    }

                    int pick = pending.FindIndex(sec => VfxSectionType.ToTag(sec.TypeId) == tag &&
                                                        string.Equals(SectionName(sec), name, StringComparison.Ordinal));
                    if (pick < 0)
                        pick = pending.FindIndex(sec => VfxSectionType.ToTag(sec.TypeId) == tag &&
                                                        string.Equals(SectionName(sec), name, StringComparison.OrdinalIgnoreCase));
                    if (pick < 0)
                        pick = pending.FindIndex(sec => VfxSectionType.ToTag(sec.TypeId) == tag);
                    if (pick < 0)
                        continue;

                    file.Sections.Add(pending[pick]);
                    pending.RemoveAt(pick);
                }

                // Sections the order list does not mention - anything added since - go on the end.
                file.Sections.AddRange(pending);
                while (materialQueue.Count > 0) file.Sections.Add(materialQueue.Dequeue());
            }
            else
            {
                file.Sections.AddRange(nodeSections);
                file.Sections.AddRange(materials);
            }

            if (file.EndFrame == 0)
            {
                int maxFrames = 0;
                foreach (VfxMesh m in file.Meshes)
                    maxFrames = Math.Max(maxFrames, m.Frames.Count);
                file.EndFrame = Math.Max(0, maxFrames - 1);
            }

            Logger.Info(logSrc,
                $"Read VFX glTF \"{Path.GetFileName(path)}\": {file.Sections.Count(s => s is VfxMesh)} mesh, " +
                $"{materials.Count} material, {file.Sections.Count(s => s is VfxParticleSystem)} particle, " +
                $"{file.Sections.Count(s => s is VfxDummy)} dummy sections.");
            return file;
        }

        // A new object parented under another mesh keeps that relationship; anything under the root
        // node - or under nothing - sits at the scene root.
        private static string ParentSectionName(Context ctx, int nodeIndex)
        {
            int parent = nodeIndex >= 0 && nodeIndex < ctx.ParentOf.Length ? ctx.ParentOf[nodeIndex] : -1;
            if (parent < 0 || parent >= ctx.Nodes.Count || parent == ctx.RootNodeIndex)
                return "Scene Root";

            JsonElement node = ctx.Nodes[parent];
            if (node.TryGetProperty("extras", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
            {
                string type = GetString(e, "rf_type", string.Empty);
                if (type == "vfx") return "Scene Root";
                string name = GetString(e, "rf_name", string.Empty);
                if (name.Length > 0) return name;
            }
            return GetString(node, "name", "Scene Root");
        }

        private static string SectionName(VfxSection section) => section switch
        {
            VfxMesh m => m.Name,
            VfxDummy d => d.Name,
            VfxParticleSystem p => p.Name,
            VfxLight l => l.Name,
            VfxSpacewarp w => w.Name,
            VfxChain c => c.Name,
            VfxCamera c => c.Name,
            _ => string.Empty
        };

        // ─── glTF plumbing ─────────────────────────────────────────────────────────────────────

        private sealed class Context
        {
            public JsonElement Root;
            public string BaseDir = string.Empty;
            public string SourceName = string.Empty;
            public byte[] Buffer = Array.Empty<byte>();
            // glTF material index -> index in the VFX material table. Blender reorders materials by
            // first use, drops unused ones and renames duplicates, so position means nothing.
            public int[] GltfMaterialToGlobal = Array.Empty<int>();
            public List<VfxMaterial> MaterialTable = new();
            public int RootNodeIndex = -1;

            public int GlobalMaterial(int gltfMaterialIndex)
                => gltfMaterialIndex >= 0 && gltfMaterialIndex < GltfMaterialToGlobal.Length
                    ? GltfMaterialToGlobal[gltfMaterialIndex]
                    : -1;
            public List<JsonElement> Nodes = new();
            public int[] ParentOf = Array.Empty<int>();

            public void Load()
            {
                if (Root.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
                    Nodes = nodes.EnumerateArray().ToList();

                ParentOf = Enumerable.Repeat(-1, Nodes.Count).ToArray();
                for (int i = 0; i < Nodes.Count; i++)
                {
                    if (!Nodes[i].TryGetProperty("children", out JsonElement kids) || kids.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement k in kids.EnumerateArray())
                    {
                        int idx = k.GetInt32();
                        if (idx >= 0 && idx < ParentOf.Length) ParentOf[idx] = i;
                    }
                }

                if (Root.TryGetProperty("buffers", out JsonElement buffers) && buffers.ValueKind == JsonValueKind.Array &&
                    buffers.GetArrayLength() > 0)
                {
                    JsonElement b0 = buffers[0];
                    string? uri = b0.TryGetProperty("uri", out JsonElement u) ? u.GetString() : null;
                    Buffer = LoadBuffer(uri, BaseDir);
                }
            }

            public JsonElement? FindRootExtras()
            {
                for (int i = 0; i < Nodes.Count; i++)
                {
                    if (Nodes[i].TryGetProperty("extras", out JsonElement e) && e.ValueKind == JsonValueKind.Object &&
                        GetString(e, "rf_type", string.Empty) == "vfx")
                    {
                        RootNodeIndex = i;
                        return e;
                    }
                }
                return null;
            }

            // The absolute transform a section was authored with: the glTF exporter divides the
            // parent's static transform out, so composing the chain back gives the RF value.
            public Trs WorldStatic(int nodeIndex)
            {
                Trs acc = LocalStatic(nodeIndex);
                int parent = ParentOf[nodeIndex];
                int guard = 0;
                while (parent >= 0 && guard++ < 64)
                {
                    acc = Compose(LocalStatic(parent), acc);
                    parent = ParentOf[parent];
                }
                return acc;
            }

            public Trs ParentWorldStatic(int nodeIndex)
            {
                int parent = ParentOf[nodeIndex];
                return parent >= 0 ? WorldStatic(parent) : Trs.Identity;
            }

            public Trs LocalStatic(int nodeIndex)
            {
                JsonElement n = Nodes[nodeIndex];
                Vector3 t = ReadVec3Prop(n, "translation", Vector3.Zero);
                Quaternion r = ReadQuatProp(n, "rotation", Quaternion.Identity);
                Vector3 s = ReadVec3Prop(n, "scale", Vector3.One);
                return new Trs(t, r, s);
            }
        }

        private static byte[] LoadBuffer(string? uri, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(uri)) return Array.Empty<byte>();
            const string prefix = "data:";
            if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                return comma >= 0 ? Convert.FromBase64String(uri[(comma + 1)..]) : Array.Empty<byte>();
            }
            string full = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
            return File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
        }

        // ─── materials ─────────────────────────────────────────────────────────────────────────

        private static List<VfxMaterial> BuildMaterials(JsonElement root, JsonElement? rootExtras, out int[] gltfMaterialToGlobal)
        {
            gltfMaterialToGlobal = Array.Empty<int>();

            // The root node keeps the authored table verbatim, including materials no primitive
            // references, so prefer it and use the glTF materials only to map primitive -> slot.
            List<VfxMaterial>? authoredTable = ReadAuthoredMaterialTable(rootExtras);

            if (!root.TryGetProperty("materials", out JsonElement mats) || mats.ValueKind != JsonValueKind.Array)
                return authoredTable ?? new List<VfxMaterial>();

            int count = mats.GetArrayLength();
            gltfMaterialToGlobal = Enumerable.Repeat(-1, count).ToArray();

            var declared = new int[count];
            for (int i = 0; i < count; i++)
                declared[i] = ReadDeclaredMaterialIndex(mats[i]);

            // How much of the table the glTF can actually vouch for: either the recorded table, or
            // the unbroken run of authored indices starting at 0. An index past that - the gap a
            // hand-edited duplicate leaves behind - names no authored entry.
            int authoredCount = authoredTable?.Count ?? ContiguousAuthoredCount(declared);

            var table = new List<VfxMaterial>();
            if (authoredTable != null)
            {
                table.AddRange(authoredTable);
            }
            else
            {
                var byIndex = new VfxMaterial?[authoredCount];
                for (int i = 0; i < count; i++)
                {
                    if (declared[i] >= 0 && declared[i] < authoredCount && byIndex[declared[i]] == null)
                        byIndex[declared[i]] = MaterialFromGltf(root, mats[i], null);
                }
                for (int i = 0; i < authoredCount; i++)
                    table.Add(byIndex[i] ?? new VfxMaterial { Tex0 = new VfxTexture(), SelfIllumination = { 0f }, Opacity = { 1f } });
            }

            // Authored materials keep their slot; everything else is new.
            var isNew = new bool[count];
            for (int i = 0; i < count; i++)
            {
                if (declared[i] >= 0 && declared[i] < authoredCount && MaterialMatchesTable(root, mats[i], table[declared[i]]))
                    gltfMaterialToGlobal[i] = declared[i];
                else
                    isNew[i] = true;
            }

            // Fall back to the name for a material that lost its index but is still one of ours.
            for (int i = 0; i < count; i++)
            {
                if (!isNew[i] || declared[i] >= 0) continue;
                string name = NormalizeMaterialName(mats[i].TryGetProperty("name", out JsonElement n) ? n.GetString() : null);
                if (name.Length == 0) continue;
                // A table may legitimately hold the same texture twice, so take the first entry
                // with that name no other material has claimed yet.
                for (int t = 0; t < table.Count; t++)
                {
                    if (gltfMaterialToGlobal.Contains(t)) continue;
                    if (!string.Equals(NormalizeMaterialName(VfxGltfExporter.MaterialIdentity(table[t])), name,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!MaterialMatchesTable(root, mats[i], table[t])) continue;
                    gltfMaterialToGlobal[i] = t;
                    isNew[i] = false;
                    break;
                }
            }

            foreach (int i in FirstUseOrder(root, count))
            {
                if (!isNew[i]) continue;
                VfxMaterial created = MaterialFromGltf(root, mats[i], TextureNameFromImage(root, mats[i], announce: true));
                table.Add(created);
                gltfMaterialToGlobal[i] = table.Count - 1;
                isNew[i] = false;
                string label = mats[i].TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? "?" : "?";
                Logger.Info(logSrc, $"Material \"{label}\" appended as table entry {table.Count - 1} (texture {VfxGltfExporter.MaterialIdentity(created)}).");
            }

            return table;
        }

        private static int ReadDeclaredMaterialIndex(JsonElement material)
        {
            if (!material.TryGetProperty("extras", out JsonElement extras) || extras.ValueKind != JsonValueKind.Object)
                return -1;
            return extras.TryGetProperty("rf_material_index", out _) ? GetInt(extras, "rf_material_index", -1) : -1;
        }

        // The authored table is the unbroken run 0..K-1; a declared index above it names no entry.
        private static int ContiguousAuthoredCount(int[] declared)
        {
            var present = new HashSet<int>(declared.Where(d => d >= 0));
            int k = 0;
            while (present.Contains(k)) k++;
            return k;
        }

        // Every glTF material that some primitive uses, in the order the primitives first name it,
        // then anything unused so nothing is silently lost.
        private static IEnumerable<int> FirstUseOrder(JsonElement root, int count)
        {
            var seen = new List<int>();
            if (root.TryGetProperty("meshes", out JsonElement meshes) && meshes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement mesh in meshes.EnumerateArray())
                {
                    if (!mesh.TryGetProperty("primitives", out JsonElement prims) || prims.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement prim in prims.EnumerateArray())
                    {
                        if (!prim.TryGetProperty("material", out JsonElement mi)) continue;
                        int index = mi.GetInt32();
                        if (index >= 0 && index < count && !seen.Contains(index))
                            seen.Add(index);
                    }
                }
            }
            for (int i = 0; i < count; i++)
                if (!seen.Contains(i)) seen.Add(i);
            return seen;
        }

        // A duplicated material whose image was swapped still carries the original's extras, so the
        // bitmap the glTF actually points at decides whether it is still the entry it claims.
        private static bool MaterialMatchesTable(JsonElement root, JsonElement material, VfxMaterial entry)
        {
            string image = TextureNameFromImage(root, material) ?? string.Empty;
            if (image.Length == 0)
                return true;
            string authored = VfxGltfExporter.MaterialIdentity(entry);
            return string.Equals(Path.GetFileNameWithoutExtension(image),
                                 Path.GetFileNameWithoutExtension(authored),
                                 StringComparison.OrdinalIgnoreCase);
        }

        // The bitmap behind a glTF material, named the way RF needs it: the game only loads .tga and
        // .vbm, so anything else keeps its stem and becomes a .tga.
        private static string? TextureNameFromImage(JsonElement root, JsonElement material, bool announce = false)
        {
            string? uri = ResolveBaseColorImageUri(root, material);
            if (string.IsNullOrWhiteSpace(uri)) return null;

            string name = Path.GetFileName(uri.Replace('\\', '/'));
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".tga" || extension == ".vbm")
                return name;

            string converted = Path.GetFileNameWithoutExtension(name) + ".tga";
            if (announce)
                Logger.Info(logSrc, $"Texture \"{name}\" is not a format RF loads; referencing \"{converted}\" instead.");
            return converted;
        }

        private static string? ResolveBaseColorImageUri(JsonElement root, JsonElement material)
        {
            if (!material.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) ||
                !pbr.TryGetProperty("baseColorTexture", out JsonElement bct) ||
                !bct.TryGetProperty("index", out JsonElement idx))
                return null;
            if (!root.TryGetProperty("textures", out JsonElement textures) || textures.ValueKind != JsonValueKind.Array)
                return null;
            int ti = idx.GetInt32();
            if (ti < 0 || ti >= textures.GetArrayLength()) return null;
            if (!textures[ti].TryGetProperty("source", out JsonElement src)) return null;
            if (!root.TryGetProperty("images", out JsonElement images) || images.ValueKind != JsonValueKind.Array) return null;
            int ii = src.GetInt32();
            if (ii < 0 || ii >= images.GetArrayLength()) return null;
            JsonElement image = images[ii];
            if (image.TryGetProperty("uri", out JsonElement uri)) return uri.GetString();
            if (image.TryGetProperty("name", out JsonElement name)) return name.GetString();
            return null;
        }

        // Builds a material from a glTF entry: its rf_* extras when it has them (a duplicate carries
        // the original's settings), otherwise plain opaque defaults.
        private static VfxMaterial MaterialFromGltf(JsonElement root, JsonElement material, string? textureOverride)
        {
            bool hasExtras = material.TryGetProperty("extras", out JsonElement extras) &&
                             extras.ValueKind == JsonValueKind.Object &&
                             extras.TryGetProperty("rf_type", out _);

            VfxMaterial m;
            if (hasExtras)
            {
                m = MaterialFromExtras(extras);
            }
            else
            {
                m = new VfxMaterial
                {
                    Type = (int)VfxMaterialType.Image,
                    FramesPerSecond = 15,
                    Additive = false,
                    Tex0 = new VfxTexture()
                };
                m.SelfIllumination.Add(1f);
                m.Opacity.Add(1f);
            }

            string? texture = textureOverride ?? TextureNameFromImage(root, material);
            if (!string.IsNullOrEmpty(texture))
            {
                m.Tex0 ??= new VfxTexture();
                m.Tex0.Name = texture;
                if (m.IsColorOnly) m.Type = (int)VfxMaterialType.Image;
            }
            if (m.HasTextures && m.Tex0 == null) m.Tex0 = new VfxTexture();
            if (m.SelfIllumination.Count == 0) m.SelfIllumination.Add(hasExtras ? 0f : 1f);
            if (m.Opacity.Count == 0) m.Opacity.Add(1f);
            return m;
        }

        private static List<VfxMaterial>? ReadAuthoredMaterialTable(JsonElement? rootExtras)
        {
            if (!rootExtras.HasValue) return null;
            JsonElement list = Resolve(rootExtras.Value, "rf_material_table");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                return null;

            var table = new List<VfxMaterial>();
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return null;
                table.Add(MaterialFromExtras(entry));
            }
            return table;
        }

        // Strips the exporter's "NNN_" prefix and Blender's ".NNN" duplicate suffix so a material
        // can be recognised by name when its rf_material_index did not survive.
        private static string NormalizeMaterialName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string value = name.Trim();

            int underscore = value.IndexOf('_');
            if (underscore == 3 && value.Length > 4 && value.Take(3).All(char.IsDigit))
                value = value[(underscore + 1)..];

            // ".001" style suffixes Blender appends to duplicate names.
            if (value.Length > 4 && value[^4] == '.' && value[^3..].All(char.IsDigit))
                value = value[..^4];

            return value;
        }

        private static VfxMaterial MaterialFromExtras(JsonElement extras)
        {
            var m = new VfxMaterial
            {
                Type = GetInt(extras, "rf_mat_type_id", TypeFromName(GetString(extras, "rf_mat_type", "image"))),
                FramesPerSecond = GetInt(extras, "rf_fps", 15),
                Additive = GetBool(extras, "rf_additive", false),
                SpecularLevel = GetFloat(extras, "rf_specular_level", 0f),
                Glossiness = GetFloat(extras, "rf_glossiness", 0f),
                ReflectionAmount = GetFloat(extras, "rf_reflection_amount", 0f),
                ReflTexName = GetString(extras, "rf_refl_tex_name", string.Empty),
                MixFrames = GetFloatList(extras, "rf_mix_frames"),
                SelfIllumination = GetFloatList(extras, "rf_self_illumination"),
                Opacity = GetFloatList(extras, "rf_opacity"),
                Tex0 = ReadTextureExtras(extras, "tex_0"),
                Tex1 = ReadTextureExtras(extras, "tex_1")
            };
            int[] color = GetIntArray(extras, "rf_solid_color");
            if (color.Length == 3) m.SolidColor = color;
            if (m.HasTextures && m.Tex0 == null) m.Tex0 = new VfxTexture();
            if (m.IsVMix && m.Tex1 == null) m.Tex1 = new VfxTexture();
            if (m.SelfIllumination.Count == 0) m.SelfIllumination.Add(0f);
            if (m.Opacity.Count == 0) m.Opacity.Add(1f);
            return m;
        }

        private static int TypeFromName(string name) => name switch
        {
            "vmix" => (int)VfxMaterialType.VMix,
            "color_only" => (int)VfxMaterialType.ColorOnly,
            _ => (int)VfxMaterialType.Image
        };

        private static VfxTexture? ReadTextureExtras(JsonElement extras, string key)
        {
            JsonElement t = Resolve(extras, key);
            if (t.ValueKind != JsonValueKind.Object)
                return null;
            return new VfxTexture
            {
                Name = GetString(t, "name", string.Empty),
                StartFrame = GetInt(t, "start_frame", 0),
                PlaybackRate = GetFloat(t, "playback_rate", 1f),
                AnimType = GetInt(t, "anim_type", (int)VfxTextureAnimType.Once)
            };
        }

        // ─── sections from nodes ───────────────────────────────────────────────────────────────

        private static List<VfxSection> BuildNodeSections(Context ctx, int materialCount)
        {
            var sections = new List<VfxSection>();
            for (int i = 0; i < ctx.Nodes.Count; i++)
            {
                JsonElement node = ctx.Nodes[i];
                if (!node.TryGetProperty("extras", out JsonElement extras) || extras.ValueKind != JsonValueKind.Object)
                {
                    // A plain object someone added in Blender: no extras, just geometry.
                    if (node.TryGetProperty("mesh", out _))
                    {
                        Logger.Info(logSrc, $"Node \"{GetString(node, "name", "?")}\" is a new object with no VFX data; importing it as a mesh with defaults.");
                        sections.Add(BuildMeshFromGeometry(ctx, i, node, default));
                    }
                    continue;
                }

                string type = GetString(extras, "rf_type", string.Empty);
                switch (type)
                {
                    case "vfx":
                        // Duplicating the root in Blender leaves a second one; the first is the file
                        // and anything hanging off the others is just more sections.
                        if (i != ctx.RootNodeIndex)
                            Logger.Warn(logSrc, $"glTF has more than one VFX root node; \"{GetString(node, "name", "?")}\" is treated as a plain group and its children as ordinary sections.");
                        break;
                    case "vfx_mesh":
                        // A viewer that kept the node's custom properties but dropped the face and
                        // frame tables leaves nothing to rebuild from but the glTF geometry.
                        sections.Add(extras.TryGetProperty("rf_face_indices", out _) && extras.TryGetProperty("rf_pos_frames", out _)
                            ? BuildMesh(ctx, i, node, extras, materialCount)
                            : BuildMeshFromGeometry(ctx, i, node, extras));
                        break;
                    case "vfx_dummy":
                        sections.Add(BuildDummy(ctx, i, node, extras));
                        break;
                    case "vfx_particle_system":
                        sections.Add(BuildParticleSystem(ctx, i, node, extras));
                        break;
                    case "vfx_light":
                        sections.Add(BuildLight(node, extras));
                        break;
                    case "vfx_spacewarp":
                        sections.Add(BuildSpacewarp(node, extras));
                        break;
                    case "vfx_chain":
                        sections.Add(BuildChain(node, extras));
                        break;
                    case "vfx_camera":
                        sections.Add(BuildCamera(node, extras));
                        break;
                    case "vfx_material_modifier":
                        sections.Add(new VfxMaterialModifier { MaterialIndex = GetInt(extras, "rf_material_index", -1) });
                        break;
                    case "vfx_unknown":
                        sections.Add(new VfxUnknownSection
                        {
                            RawTypeId = GetInt(extras, "rf_section_type", 0),
                            Data = Convert.FromBase64String(GetString(extras, "rf_raw_base64", string.Empty))
                        });
                        break;
                    default:
                        if (node.TryGetProperty("mesh", out _))
                        {
                            Logger.Info(logSrc, $"Node \"{GetString(node, "name", "?")}\" carries geometry but no rf_type; importing it as a new mesh.");
                            sections.Add(BuildMeshFromGeometry(ctx, i, node, extras));
                        }
                        break;
                }
            }
            return sections;
        }

        // ─── mesh ──────────────────────────────────────────────────────────────────────────────

        private static VfxMesh BuildMesh(Context ctx, int nodeIndex, JsonElement node, JsonElement extras, int materialCount)
        {
            var m = new VfxMesh
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Object")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                SaveParent = GetBool(extras, "rf_save_parent", false),
                Flags = (uint)GetLong(extras, "rf_flags", 0),
                FramesPerSecond = GetInt(extras, "rf_fps", 15),
                StartTime = GetFloat(extras, "rf_start_time", 0f),
                EndTime = GetFloat(extras, "rf_end_time", 0f),
                VertexCount = GetInt(extras, "rf_vertex_count", 0),
                BoundingCenter = GetVec3(extras, "rf_bounding_center", Vector3.Zero),
                BoundingRadius = GetFloat(extras, "rf_bounding_radius", 0f),
                IsKeyframed = GetBool(extras, "rf_is_keyframed", false),
                MaterialIndices = GetIntArray(extras, "rf_material_indices").ToList()
            };

            int frameCount = GetInt(extras, "rf_num_frames", 0);

            // faces
            int[] faceIndices = GetIntArray(extras, "rf_face_indices");
            float[] faceColors = GetFloatArray(extras, "rf_face_colors");
            float[] faceNormals = GetFloatArray(extras, "rf_face_normals");
            float[] faceCenters = GetFloatArray(extras, "rf_face_centers");
            float[] faceRadii = GetFloatArray(extras, "rf_face_radii");
            int[] faceMaterial = GetIntArray(extras, "rf_face_material_index");
            int[] smoothing = GetIntArray(extras, "rf_smoothing_groups");
            int[] faceVertexIndices = GetIntArray(extras, "rf_face_vertex_indices");
            int faceCount = faceIndices.Length / 3;

            for (int i = 0; i < faceCount; i++)
            {
                var f = new VfxFace
                {
                    Indices = new[] { faceIndices[i * 3], faceIndices[i * 3 + 1], faceIndices[i * 3 + 2] },
                    Colors = new[]
                    {
                        SafeVec3(faceColors, i * 9 + 0),
                        SafeVec3(faceColors, i * 9 + 3),
                        SafeVec3(faceColors, i * 9 + 6)
                    },
                    Normal = SafeVec3(faceNormals, i * 3),
                    Center = SafeVec3(faceCenters, i * 3),
                    Radius = i < faceRadii.Length ? faceRadii[i] : 0f,
                    MaterialIndex = i < faceMaterial.Length ? faceMaterial[i] : -1,
                    SmoothingGroup = i < smoothing.Length ? smoothing[i] : 0,
                    FaceVertexIndices = faceVertexIndices.Length >= (i + 1) * 3
                        ? new[] { faceVertexIndices[i * 3], faceVertexIndices[i * 3 + 1], faceVertexIndices[i * 3 + 2] }
                        : new[] { i * 3, i * 3 + 1, i * 3 + 2 }
                };
                m.Faces.Add(f);
            }

            // face vertices
            JsonElement fvr = Resolve(extras, "rf_face_vertex_raw");
            if (fvr.ValueKind == JsonValueKind.Object)
            {
                int[] sg = GetIntArray(fvr, "smoothing");
                int[] vi = GetIntArray(fvr, "vertex_index");
                // uv_bits_b64 is the current spelling; uv_bits is the pre-base64 form.
                uint[] uvBits = DecodeUInts(GetString(fvr, "uv_bits_b64", string.Empty));
                if (uvBits.Length == 0) uvBits = GetUIntArray(fvr, "uv_bits");
                int[] counts = GetIntArray(fvr, "adjacent_counts");
                int[] adjacent = GetIntArray(fvr, "adjacent");
                int cursor = 0;
                for (int i = 0; i < vi.Length; i++)
                {
                    var fv = new VfxFaceVertex
                    {
                        SmoothingGroup = i < sg.Length ? sg[i] : 1,
                        VertexIndex = vi[i],
                        URaw = uvBits.Length > i * 2 ? uvBits[i * 2] : 0xCDCDCDCDu,
                        VRaw = uvBits.Length > i * 2 + 1 ? uvBits[i * 2 + 1] : 0xCDCDCDCDu
                    };
                    int n = i < counts.Length ? counts[i] : 0;
                    for (int j = 0; j < n && cursor < adjacent.Length; j++)
                        fv.AdjacentFaces.Add(adjacent[cursor++]);
                    m.FaceVertices.Add(fv);
                }
            }

            // frames
            Dictionary<int, JsonElement> posFrames = IndexByFrame(extras, "rf_pos_frames");
            Dictionary<int, float[]> uvFrames = IndexUvFrames(extras);
            Dictionary<int, (float W, float H)> sizes = IndexSizes(extras);
            Dictionary<int, JsonElement> frameTransforms = IndexByFrame(extras, "rf_frame_transforms");
            float[] frameOpacity = GetFloatArray(extras, "rf_frame_opacity");
            Vector3 upVector = GetVec3(extras, "rf_up_vector", new Vector3(0f, 1f, 0f));

            for (int i = 0; i < frameCount; i++)
            {
                var fr = new VfxMeshFrame();
                bool storesGeometry = m.Morph || i == 0;
                if (storesGeometry && posFrames.TryGetValue(i, out JsonElement pf))
                {
                    fr.HasPositions = true;
                    fr.Center = GetVec3(pf, "center", Vector3.Zero);
                    fr.PositionsMultiplier = GetVec3(pf, "multiplier", new Vector3(VfxPositionCodec.MinHalfExtent / VfxPositionCodec.Scale));
                    fr.RawPositions = DecodeShorts(GetString(pf, "s16", string.Empty));
                    fr.Positions = VfxPositionCodec.Decompress(fr.Center, fr.PositionsMultiplier, fr.RawPositions, m.VertexCount);
                }
                if (fr.HasPositions && (m.Facing || m.FacingRod))
                {
                    fr.HasSize = true;
                    if (sizes.TryGetValue(i, out (float W, float H) wh)) { fr.Width = wh.W; fr.Height = wh.H; }
                    else { fr.Width = GetFloat(extras, "rf_width", 1f); fr.Height = GetFloat(extras, "rf_height", 1f); }
                }
                if (fr.HasPositions && m.FacingRod && i == 0)
                {
                    fr.HasUpVector = true;
                    fr.UpVector = upVector;
                }
                if (m.DumpUvs || i == 0)
                {
                    fr.HasUvs = true;
                    if (uvFrames.TryGetValue(i, out float[]? flat))
                    {
                        var uvs = new Vector2[faceCount * 3];
                        for (int j = 0; j < uvs.Length; j++)
                            uvs[j] = new Vector2(
                                flat.Length > j * 2 ? flat[j * 2] : 0f,
                                flat.Length > j * 2 + 1 ? flat[j * 2 + 1] : 0f);
                        fr.Uvs = uvs;
                    }
                    else
                    {
                        fr.Uvs = new Vector2[faceCount * 3];
                    }
                }
                if (!m.Morph && !m.IsKeyframed)
                {
                    fr.HasTransform = true;
                    if (frameTransforms.TryGetValue(i, out JsonElement ft))
                    {
                        fr.Translation = GetVec3(ft, "translation", Vector3.Zero);
                        fr.Rotation = GetQuat(ft, "rotation", Quaternion.Identity);
                        fr.Scale = GetVec3(ft, "scale", Vector3.One);
                    }
                    else
                    {
                        fr.Scale = Vector3.One;
                    }
                }
                if (i < frameOpacity.Length)
                {
                    fr.HasOpacity = true;
                    fr.Opacity = frameOpacity[i];
                }
                m.Frames.Add(fr);
            }

            m.NumFrames = m.Frames.Count;

            // pivot and keyframes
            if (extras.TryGetProperty("rf_pivot_translation", out _))
            {
                m.HasPivot = true;
                m.PivotTranslation = GetVec3(extras, "rf_pivot_translation", Vector3.Zero);
                m.PivotRotation = GetQuat(extras, "rf_pivot_rotation", Quaternion.Identity);
                m.PivotScale = GetVec3(extras, "rf_pivot_scale", Vector3.One);
            }
            JsonElement kf = Resolve(extras, "rf_keyframes");
            if (kf.ValueKind == JsonValueKind.Object)
            {
                m.TranslationKeys = ReadVec3Keys(kf, "translation");
                m.RotationKeys = ReadQuatKeys(kf, "rotation");
                m.ScaleKeys = ReadVec3Keys(kf, "scale");
            }

            RealignMaterialSlots(ctx, m, extras);
            RebuildMaterialSlots(ctx, node, m);
            ApplyTransformPolicy(ctx, nodeIndex, m);
            ResolveGeometry(ctx, node, extras, m, materialCount);
            return m;
        }

        // rf_material_indices holds authored table positions, which only mean anything while the
        // table is in its authored order. rf_material_names says what each slot actually pointed at,
        // so a slot whose entry no longer matches is re-pointed at the entry that does.
        private static void RealignMaterialSlots(Context ctx, VfxMesh m, JsonElement extras)
        {
            JsonElement names = Resolve(extras, "rf_material_names");
            if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() != m.MaterialIndices.Count)
                return;

            var wanted = names.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            for (int slot = 0; slot < m.MaterialIndices.Count; slot++)
            {
                string want = NormalizeMaterialName(wanted[slot]);
                if (want.Length == 0)
                    continue;

                int current = m.MaterialIndices[slot];
                if (current >= 0 && current < ctx.MaterialTable.Count &&
                    string.Equals(NormalizeMaterialName(VfxGltfExporter.MaterialIdentity(ctx.MaterialTable[current])), want,
                                  StringComparison.OrdinalIgnoreCase))
                    continue;

                int match = ctx.MaterialTable.FindIndex(mat =>
                    string.Equals(NormalizeMaterialName(VfxGltfExporter.MaterialIdentity(mat)), want, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                    m.MaterialIndices[slot] = match;
            }
        }

        // The slot list is whatever the mesh actually needs: the authored slots that still resolve,
        // in their authored order, plus any material its primitives name that is not already there.
        // A hand-edited rf_material_indices is never required - assigning a material in Blender is
        // enough - and a slot pointing nowhere is dropped rather than left dangling.
        private static void RebuildMaterialSlots(Context ctx, JsonElement node, VfxMesh m)
        {
            int count = ctx.MaterialTable.Count;

            var slotRemap = new int[m.MaterialIndices.Count];
            var kept = new List<int>();
            for (int slot = 0; slot < m.MaterialIndices.Count; slot++)
            {
                int global = m.MaterialIndices[slot];
                if (global < 0 || global >= count)
                {
                    slotRemap[slot] = -1;
                    continue;
                }
                int existing = kept.IndexOf(global);
                if (existing >= 0)
                {
                    slotRemap[slot] = existing;
                    continue;
                }
                slotRemap[slot] = kept.Count;
                kept.Add(global);
            }

            var primitiveGlobals = PrimitiveMaterials(ctx, node).ToList();
            foreach (int global in primitiveGlobals)
            {
                if (global < 0 || global >= count || kept.Contains(global)) continue;
                kept.Add(global);
                Logger.Info(logSrc, $"Mesh \"{m.Name}\": material table entry {global} " +
                                    $"({VfxGltfExporter.MaterialIdentity(ctx.MaterialTable[global])}) added as slot {kept.Count - 1}.");
            }

            // The exporter emits one primitive per used slot, in ascending slot order, so the two
            // lists line up: a face whose authored slot no longer resolves follows its primitive.
            var usedSlots = m.Faces.Select(f => f.MaterialIndex).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
            if (usedSlots.Count == primitiveGlobals.Count)
            {
                for (int i = 0; i < usedSlots.Count; i++)
                {
                    if (usedSlots[i] < slotRemap.Length)
                        slotRemap[usedSlots[i]] = kept.IndexOf(primitiveGlobals[i]);
                }
            }

            if (kept.SequenceEqual(m.MaterialIndices) && slotRemap.Select((v, i) => v == i).All(x => x))
                return;

            foreach (VfxFace f in m.Faces)
                f.MaterialIndex = f.MaterialIndex >= 0 && f.MaterialIndex < slotRemap.Length
                    ? slotRemap[f.MaterialIndex]
                    : -1;

            m.MaterialIndices.Clear();
            m.MaterialIndices.AddRange(kept);
        }

        // The table entries a node's primitives reference, in primitive order.
        private static IEnumerable<int> PrimitiveMaterials(Context ctx, JsonElement node)
        {
            if (!node.TryGetProperty("mesh", out JsonElement meshRef)) yield break;
            if (!ctx.Root.TryGetProperty("meshes", out JsonElement meshes) || meshes.ValueKind != JsonValueKind.Array) yield break;
            int index = meshRef.GetInt32();
            if (index < 0 || index >= meshes.GetArrayLength()) yield break;
            if (!meshes[index].TryGetProperty("primitives", out JsonElement prims) || prims.ValueKind != JsonValueKind.Array) yield break;

            foreach (JsonElement prim in prims.EnumerateArray())
            {
                if (prim.TryGetProperty("material", out JsonElement mi))
                    yield return ctx.GlobalMaterial(mi.GetInt32());
            }
        }

        // The authored tables in the extras are only usable when they are internally consistent AND
        // still describe the geometry sitting in the glTF. Blender re-welds vertices on every
        // export, so a mismatch is the normal outcome of editing a model - the glTF wins and the
        // tables are rebuilt from it rather than the edit being dropped.
        private static void ResolveGeometry(Context ctx, JsonElement node, JsonElement extras, VfxMesh m, int materialCount)
        {
            int[] sourceHint = GetIntArray(extras, "rf_gltf_vertex_source");
            GltfGeometry geometry = new();
            bool hasGltfGeometry = node.TryGetProperty("mesh", out JsonElement meshRef) &&
                                   TryReadGltfGeometry(ctx, meshRef.GetInt32(), out geometry);

            string reason;
            bool authoredConsistent = VfxValidation.IsValid(m, materialCount, out reason);
            bool matchesGltf = !hasGltfGeometry ||
                               (geometry.Triangles.Count == m.Faces.Count &&
                                AuthoredGeometryStillMatches(m, geometry, sourceHint));

            if (authoredConsistent && matchesGltf)
                return;

            if (!hasGltfGeometry)
            {
                // Nothing to rebuild from; the writer's validation gate will catch it.
                if (!authoredConsistent)
                    Logger.Error(logSrc, $"Mesh \"{m.Name}\" has inconsistent authored data and no glTF geometry to rebuild from: {reason}");
                return;
            }

            string why = !authoredConsistent
                ? reason
                : $"the glTF holds {geometry.Positions.Count} vertices and {geometry.Triangles.Count} triangles, " +
                  $"the authored tables {sourceHint.Length} and {m.Faces.Count}";
            Logger.Info(logSrc, $"Mesh \"{m.Name}\": rebuilding faces, face vertices and positions from the glTF geometry ({why}).");
            RebuildGeometryFromGltf(ctx, m, extras, geometry, materialCount);
        }

        private sealed class GltfGeometry
        {
            public List<Vector3> Positions = new();
            public List<Vector3> Normals = new();
            public List<Vector2> Uvs = new();
            public List<Vector4> Colors = new();
            public List<GltfTriangle> Triangles = new();
            public List<List<Vector3>> MorphTargets = new();
        }

        private readonly struct GltfTriangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;
            public readonly int Material;   // glTF material index, -1 when the primitive has none
            public readonly int SlotHint;   // rf_face_material_index from the primitive extras, else -2

            public GltfTriangle(int a, int b, int c, int material, int slotHint)
            {
                A = a; B = b; C = c; Material = material; SlotHint = slotHint;
            }
        }

        private static bool TryReadGltfGeometry(Context ctx, int meshIndex, out GltfGeometry geometry)
        {
            geometry = new GltfGeometry();
            ReadPrimitiveGeometry(ctx, meshIndex, geometry);
            return geometry.Positions.Count > 0 && geometry.Triangles.Count > 0;
        }

        // Rebuilds every geometry table from the glTF while keeping the metadata the extras carry
        // (name, parent, flags, timing, transforms, keyframes, material slots).
        private static void RebuildGeometryFromGltf(Context ctx, VfxMesh m, JsonElement extras, GltfGeometry g, int materialCount)
        {
            // Weld the split glTF vertices back into VFX vertices. The finest quantisation a .vfx
            // can represent is 0.1/32767, so a 1e-6 grid never merges two distinct positions. Two
            // corners that share a position at frame 0 are only the same vertex if they also share
            // it in every morph frame, so the grouping is refined target by target.
            var remap = new int[g.Positions.Count];
            var groups = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < g.Positions.Count; i++)
            {
                var key = WeldKey(RhToRf(g.Positions[i]));
                if (!groups.TryGetValue(key, out int group))
                {
                    group = groups.Count;
                    groups[key] = group;
                }
                remap[i] = group;
            }

            foreach (List<Vector3> delta in g.MorphTargets)
            {
                var refined = new Dictionary<(int, long, long, long), int>();
                for (int i = 0; i < remap.Length; i++)
                {
                    Vector3 d = i < delta.Count ? RhToRf(delta[i]) : Vector3.Zero;
                    (long x, long y, long z) = WeldKey(d);
                    var key = (remap[i], x, y, z);
                    if (!refined.TryGetValue(key, out int group))
                    {
                        group = refined.Count;
                        refined[key] = group;
                    }
                    remap[i] = -1 - group; // negative marks "already refined this pass"
                }
                for (int i = 0; i < remap.Length; i++)
                    remap[i] = -1 - remap[i];
            }

            // Renumber the groups in first-appearance order and take one position per group.
            var compacted = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            for (int i = 0; i < remap.Length; i++)
            {
                if (!compacted.TryGetValue(remap[i], out int index))
                {
                    index = vertices.Count;
                    compacted[remap[i]] = index;
                    vertices.Add(RhToRf(g.Positions[i]));
                }
                remap[i] = index;
            }

            m.VertexCount = vertices.Count;
            m.Faces.Clear();
            m.FaceVertices.Clear();

            // Smoothing groups survive when the face count is unchanged; the exporter emits faces
            // grouped by material slot, so the authored per-face values are read back in that order.
            int[] authoredSmoothing = GetIntArray(extras, "rf_smoothing_groups");
            int[] authoredFaceMaterial = GetIntArray(extras, "rf_face_material_index");
            // authoredFaceOrder[i] is the authored face that became rebuilt face i. The exporter
            // emits faces grouped by material slot, so anything indexed by authored face order has
            // to be read back through this permutation.
            int[]? authoredFaceOrder = null;
            if (authoredFaceMaterial.Length == g.Triangles.Count)
            {
                authoredFaceOrder = Enumerable.Range(0, authoredFaceMaterial.Length)
                    .OrderBy(i => authoredFaceMaterial[i] < 0 ? int.MaxValue : authoredFaceMaterial[i])
                    .ThenBy(i => i)
                    .ToArray();
            }

            var uvs = new List<Vector2>(g.Triangles.Count * 3);
            for (int t = 0; t < g.Triangles.Count; t++)
            {
                GltfTriangle tri = g.Triangles[t];
                // Undo the winding flip the exporter applies for the mirrored X axis.
                int[] corners = { tri.A, tri.C, tri.B };

                int slot = ResolveMaterialSlot(ctx, m, tri, materialCount);

                var face = new VfxFace
                {
                    Indices = new[] { remap[corners[0]], remap[corners[1]], remap[corners[2]] },
                    MaterialIndex = slot,
                    SmoothingGroup = SmoothingFromNormals(g, corners),
                    Colors = corners.Select(i => i < g.Colors.Count
                        ? new Vector3(g.Colors[i].X, g.Colors[i].Y, g.Colors[i].Z)
                        : Vector3.One).ToArray(),
                    FaceVertexIndices = new int[3]
                };
                foreach (int i in corners)
                    uvs.Add(i < g.Uvs.Count ? g.Uvs[i] : Vector2.Zero);
                m.Faces.Add(face);
            }

            // Faces arrive grouped by material because that is how the exporter emits primitives.
            // Putting them back in the authored order keeps face indices - and everything the
            // extras store per face - lined up with the file this model came from.
            if (authoredFaceOrder != null)
            {
                var byAuthored = new VfxFace[m.Faces.Count];
                var reorderedUvs = new Vector2[uvs.Count];
                for (int i = 0; i < authoredFaceOrder.Length; i++)
                {
                    byAuthored[authoredFaceOrder[i]] = m.Faces[i];
                    for (int k = 0; k < 3; k++)
                        reorderedUvs[authoredFaceOrder[i] * 3 + k] = uvs[i * 3 + k];
                }
                m.Faces.Clear();
                m.Faces.AddRange(byAuthored);
                uvs.Clear();
                uvs.AddRange(reorderedUvs);
            }

            // With the faces back in authored order the recorded smoothing groups index directly.
            if (authoredSmoothing.Length == m.Faces.Count)
            {
                for (int i = 0; i < m.Faces.Count; i++)
                    m.Faces[i].SmoothingGroup = authoredSmoothing[i];
            }

            // One face-vertex record per (vertex, smoothing group); adjacency lists the faces that
            // use it, which is what RF averages vertex normals over.
            var recordByKey = new Dictionary<(int Vertex, int Smoothing), int>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                VfxFace face = m.Faces[f];
                for (int k = 0; k < 3; k++)
                {
                    var key = (face.Indices[k], face.SmoothingGroup);
                    if (!recordByKey.TryGetValue(key, out int record))
                    {
                        record = m.FaceVertices.Count;
                        recordByKey[key] = record;
                        m.FaceVertices.Add(new VfxFaceVertex
                        {
                            SmoothingGroup = Math.Max(1, face.SmoothingGroup),
                            VertexIndex = face.Indices[k]
                        });
                    }
                    face.FaceVertexIndices[k] = record;
                    List<int> adjacent = m.FaceVertices[record].AdjacentFaces;
                    if (adjacent.Count == 0 || adjacent[^1] != f)
                        adjacent.Add(f);
                }
            }

            // Per-frame geometry: frame 0 from POSITION, the rest from the morph targets.
            bool morph = g.MorphTargets.Count > 0;
            if (morph) m.Flags |= (uint)VfxMeshFlags.Morph;
            int geometryFrames = morph ? 1 + g.MorphTargets.Count : 1;

            if (morph)
            {
                while (m.Frames.Count < geometryFrames) m.Frames.Add(new VfxMeshFrame());
                if (m.Frames.Count > geometryFrames) m.Frames.RemoveRange(geometryFrames, m.Frames.Count - geometryFrames);
            }
            else if (m.Frames.Count == 0)
            {
                m.Frames.Add(new VfxMeshFrame());
            }

            Vector2[] frameUvs = uvs.ToArray();
            Dictionary<int, float[]> authoredUvFrames = IndexUvFrames(extras);

            for (int i = 0; i < m.Frames.Count; i++)
            {
                VfxMeshFrame frame = m.Frames[i];
                bool storesGeometry = morph || i == 0;
                if (storesGeometry)
                {
                    var positions = new Vector3[vertices.Count];
                    vertices.CopyTo(positions);
                    if (i > 0 && i - 1 < g.MorphTargets.Count)
                    {
                        List<Vector3> delta = g.MorphTargets[i - 1];
                        for (int v = 0; v < g.Positions.Count && v < delta.Count; v++)
                            positions[remap[v]] = RhToRf(g.Positions[v] + delta[v]);
                    }

                    VfxPositionCodec.Compress(positions, out Vector3 center, out Vector3 multiplier, out short[] raw);
                    frame.HasPositions = true;
                    frame.Center = center;
                    frame.PositionsMultiplier = multiplier;
                    frame.RawPositions = raw;
                    frame.Positions = VfxPositionCodec.Decompress(center, multiplier, raw, vertices.Count);

                    if (m.Facing || m.FacingRod)
                    {
                        if (!frame.HasSize)
                        {
                            frame.HasSize = true;
                            frame.Width = GetFloat(extras, "rf_width", 1f);
                            frame.Height = GetFloat(extras, "rf_height", 1f);
                        }
                        if (m.FacingRod && i == 0 && !frame.HasUpVector)
                        {
                            frame.HasUpVector = true;
                            frame.UpVector = GetVec3(extras, "rf_up_vector", new Vector3(0f, 1f, 0f));
                        }
                    }
                }
                else
                {
                    frame.HasPositions = false;
                    frame.RawPositions = Array.Empty<short>();
                    frame.Positions = Array.Empty<Vector3>();
                }

                if (m.DumpUvs || i == 0)
                {
                    frame.HasUvs = true;
                    // Frame 0 always comes from TEXCOORD_0, which is already per rebuilt corner. A
                    // dump_uvs mesh has no glTF source for its later frames, so those are read back
                    // from the extras through the authored face order.
                    frame.Uvs = i == 0
                        ? frameUvs
                        : ReorderUvFrame(authoredUvFrames, i, frameUvs);
                }
                else
                {
                    frame.HasUvs = false;
                    frame.Uvs = Array.Empty<Vector2>();
                }

                if (morph)
                {
                    frame.HasTransform = false;
                }
                else if (!m.IsKeyframed && !frame.HasTransform)
                {
                    frame.HasTransform = true;
                    frame.Scale = Vector3.One;
                }
            }

            m.NumFrames = m.Frames.Count;
            VfxExporter.RecomputeFaceGeometry(m);
            VfxExporter.RecomputeBounds(m, m.Frames[0].Positions);
        }

        // A primitive names a glTF material; the mesh stores slots into the file's material table.
        // The bridge is the material's own rf_material_index (or, failing that, its name), never its
        // position in the glTF materials array.
        private static int ResolveMaterialSlot(Context ctx, VfxMesh m, GltfTriangle tri, int materialCount)
        {
            if (tri.SlotHint >= -1)
                return tri.SlotHint < m.MaterialIndices.Count ? tri.SlotHint : -1;
            if (tri.Material < 0)
                return -1;

            int global = ctx.GlobalMaterial(tri.Material);
            if (global < 0)
                return -1;

            int slot = m.MaterialIndices.IndexOf(global);
            if (slot >= 0)
                return slot;
            if (global >= materialCount)
                return -1;

            m.MaterialIndices.Add(global);
            return m.MaterialIndices.Count - 1;
        }

        // The rebuilt faces are back in authored order, so a recorded UV frame indexes directly.
        private static Vector2[] ReorderUvFrame(Dictionary<int, float[]> frames, int index, Vector2[] fallback)
        {
            if (!frames.TryGetValue(index, out float[]? flat) || flat.Length != fallback.Length * 2)
                return fallback;
            return Enumerable.Range(0, fallback.Length).Select(j => new Vector2(flat[j * 2], flat[j * 2 + 1])).ToArray();
        }

        private static (long, long, long) WeldKey(Vector3 v)
            => ((long)MathF.Round(v.X * 1000000f), (long)MathF.Round(v.Y * 1000000f), (long)MathF.Round(v.Z * 1000000f));

        // A corner whose glTF normal differs from the flat face normal belongs to a smoothing group.
        private static int SmoothingFromNormals(GltfGeometry g, int[] corners)
        {
            if (g.Normals.Count == 0) return 1;

            Vector3 p0 = RhToRf(g.Positions[corners[0]]);
            Vector3 p1 = RhToRf(g.Positions[corners[1]]);
            Vector3 p2 = RhToRf(g.Positions[corners[2]]);
            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
            if (cross.LengthSquared() < 1e-12f) return 0;
            Vector3 faceNormal = Vector3.Normalize(cross);

            foreach (int corner in corners)
            {
                if (corner >= g.Normals.Count) continue;
                Vector3 n = RhToRf(g.Normals[corner]);
                if (n.LengthSquared() < 1e-10f) continue;
                if (Vector3.Dot(Vector3.Normalize(n), faceNormal) < 0.9995f)
                    return 1;
            }
            return 0;
        }

        // If the glTF animation still agrees with what the authored keys/frames produce, the
        // authored data is written back untouched. Otherwise the animation was re-baked outside
        // this tool and the sampled values become the source.
        private static void ApplyTransformPolicy(Context ctx, int nodeIndex, VfxMesh m)
        {
            if (m.Morph || m.Frames.Count == 0) return;
            if (nodeIndex < 0 || nodeIndex >= ctx.Nodes.Count) return;

            float fps = m.FramesPerSecond > 0 ? m.FramesPerSecond : Fps;
            var times = new List<float>(m.Frames.Count);
            for (int i = 0; i < m.Frames.Count; i++)
                times.Add(m.StartTime + i / fps);

            List<Trs> authored = AuthoredSamples(m);
            List<Trs> sampled = SampleNodeWorld(ctx, nodeIndex, times);
            if (sampled.Count == 0) return;
            if (SamplesMatch(authored, sampled)) return;

            Logger.Dev(logSrc, $"Mesh \"{m.Name}\": glTF animation differs from the authored keys; rebuilding from the animation.");

            if (m.IsKeyframed)
            {
                // A user who simply moves or rotates an object in Blender produces the same
                // transform on every frame; that stays a single key rather than one per frame.
                bool constantT = sampled.All(v => (v.T - sampled[0].T).LengthSquared() <= TransformEpsilon * TransformEpsilon);
                bool constantR = sampled.All(v => MathF.Abs(Quaternion.Dot(v.R, sampled[0].R)) >= 1f - TransformEpsilon);
                bool constantS = sampled.All(v => (v.S - sampled[0].S).LengthSquared() <= TransformEpsilon * TransformEpsilon);

                m.TranslationKeys.Clear();
                m.RotationKeys.Clear();
                m.ScaleKeys.Clear();
                for (int i = 0; i < sampled.Count; i++)
                {
                    int time = (int)MathF.Round(i * VfxKeyframeMath.TicksPerFrame);
                    Vector3 t = RhToRf(sampled[i].T);
                    Quaternion r = RhToRf(sampled[i].R);
                    Vector3 s = sampled[i].S;
                    if (i == 0 || !constantT)
                        m.TranslationKeys.Add(new VfxVec3Key { Time = time, Value = t, InTangent = t, OutTangent = t });
                    if (i == 0 || !constantR)
                        m.RotationKeys.Add(new VfxQuatKey { Time = time, Value = r });
                    if (i == 0 || !constantS)
                        m.ScaleKeys.Add(new VfxVec3Key { Time = time, Value = s, InTangent = s, OutTangent = s });
                }
                // The sampled transform already includes the pivot, so it must not be applied twice.
                m.HasPivot = true;
                m.PivotTranslation = Vector3.Zero;
                m.PivotRotation = Quaternion.Identity;
                m.PivotScale = Vector3.One;
            }
            else
            {
                for (int i = 0; i < m.Frames.Count && i < sampled.Count; i++)
                {
                    m.Frames[i].HasTransform = true;
                    m.Frames[i].Translation = RhToRf(sampled[i].T);
                    m.Frames[i].Rotation = RhToRf(sampled[i].R);
                    m.Frames[i].Scale = sampled[i].S;
                }
            }
        }

        private static List<Trs> AuthoredSamples(VfxMesh m)
        {
            var result = new List<Trs>(m.Frames.Count);
            if (m.IsKeyframed)
            {
                var pivot = new Trs(RfToRh(m.PivotTranslation), RfToRh(m.PivotRotation), m.PivotScale);
                for (int i = 0; i < m.Frames.Count; i++)
                {
                    int time = (int)MathF.Round(i * VfxKeyframeMath.TicksPerFrame);
                    var kf = new Trs(
                        RfToRh(VfxKeyframeMath.EvaluateVec3(m.TranslationKeys, time, Vector3.Zero)),
                        RfToRh(VfxKeyframeMath.EvaluateQuat(m.RotationKeys, time, Quaternion.Identity)),
                        VfxKeyframeMath.EvaluateVec3(m.ScaleKeys, time, Vector3.One));
                    result.Add(Compose(kf, pivot));
                }
            }
            else
            {
                foreach (VfxMeshFrame fr in m.Frames)
                    result.Add(new Trs(RfToRh(fr.Translation), RfToRh(fr.Rotation), fr.Scale));
            }
            return result;
        }

        // Decides whether the authored compressed positions still describe the glTF geometry. The
        // per-split-vertex source map only means anything while the glTF vertex order is the one
        // this exporter wrote; a viewer that re-welds keeps the count but scrambles the order, so
        // the positions themselves have to agree before the authored stream can be kept.
        private static bool AuthoredGeometryStillMatches(VfxMesh m, GltfGeometry g, int[] sourceHint)
        {
            if (m.Frames.Count == 0 || !m.Frames[0].HasPositions) return false;
            if (sourceHint.Length != g.Positions.Count) return false;

            Vector3[] authored = m.Frames[0].Positions;
            foreach (int index in sourceHint)
            {
                if (index < 0 || index >= authored.Length)
                    return false;
            }

            // Frame 0 has to land in the same quantisation cell, and every morph target has to match
            // the delta the exporter would have written for it. Comparing deltas rather than rebuilt
            // absolute positions avoids the float32 rounding a base+delta sum introduces.
            Vector3 tol0 = m.Frames[0].PositionsMultiplier * 0.51f;
            for (int i = 0; i < g.Positions.Count; i++)
            {
                if (!Within(RhToRf(g.Positions[i]), authored[sourceHint[i]], tol0))
                    return false;
            }

            int targetIndex = 0;
            for (int f = 1; f < m.Frames.Count; f++)
            {
                VfxMeshFrame frame = m.Frames[f];
                if (!frame.HasPositions) continue;
                if (targetIndex >= g.MorphTargets.Count) break;
                List<Vector3> delta = g.MorphTargets[targetIndex++];
                Vector3 tol = frame.PositionsMultiplier * 0.51f;
                for (int i = 0; i < g.Positions.Count && i < delta.Count; i++)
                {
                    Vector3 authoredDelta = frame.Positions[sourceHint[i]] - authored[sourceHint[i]];
                    if (!Within(RhToRf(delta[i]), authoredDelta, tol))
                        return false;
                }
            }
            return true;
        }

        private static bool Within(Vector3 a, Vector3 b, Vector3 tolerance)
            => MathF.Abs(a.X - b.X) <= MathF.Max(tolerance.X, 1e-6f)
            && MathF.Abs(a.Y - b.Y) <= MathF.Max(tolerance.Y, 1e-6f)
            && MathF.Abs(a.Z - b.Z) <= MathF.Max(tolerance.Z, 1e-6f);

        // Builds a mesh from glTF geometry alone; used when the extras were stripped by a viewer.
        private static VfxMesh BuildMeshFromGeometry(Context ctx, int nodeIndex, JsonElement node, JsonElement extras)
        {
            bool hasExtras = extras.ValueKind == JsonValueKind.Object;
            var m = new VfxMesh
            {
                Name = hasExtras ? GetString(extras, "rf_name", GetString(node, "name", $"Object{nodeIndex:D2}")) : GetString(node, "name", $"Object{nodeIndex:D2}"),
                ParentName = hasExtras
                    ? GetString(extras, "rf_parent_name", "Scene Root")
                    : ParentSectionName(ctx, nodeIndex),
                SaveParent = hasExtras && GetBool(extras, "rf_save_parent", false),
                Flags = hasExtras ? (uint)GetLong(extras, "rf_flags", 0) : 0u,
                FramesPerSecond = hasExtras ? GetInt(extras, "rf_fps", 15) : 15,
                StartTime = hasExtras ? GetFloat(extras, "rf_start_time", 0f) : 0f,
                IsKeyframed = false
            };

            if (!node.TryGetProperty("mesh", out JsonElement meshRef) ||
                !TryReadGltfGeometry(ctx, meshRef.GetInt32(), out GltfGeometry geometry))
                return m;

            // Vertex positions are object-local, so the node transform is the mesh's frame
            // transform - dropping it would collapse the object onto the origin.
            Trs world = ctx.WorldStatic(nodeIndex);
            m.Frames.Add(new VfxMeshFrame
            {
                HasTransform = true,
                Translation = RhToRf(world.T),
                Rotation = RhToRf(world.R),
                Scale = world.S
            });

            RebuildGeometryFromGltf(ctx, m, extras, geometry, int.MaxValue);
            if (m.Morph)
            {
                foreach (VfxMeshFrame frame in m.Frames)
                    frame.HasTransform = false;
            }
            m.EndTime = m.Frames.Count > 1 ? m.StartTime + (m.Frames.Count - 1) / Fps : m.StartTime;
            return m;
        }

        // ─── other sections ────────────────────────────────────────────────────────────────────

        private static VfxDummy BuildDummy(Context ctx, int nodeIndex, JsonElement node, JsonElement extras)
        {
            var d = new VfxDummy
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Dummy")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                SaveParent = GetBool(extras, "rf_save_parent", false),
                Pos = GetVec3(extras, "rf_pos", Vector3.Zero),
                Orient = GetQuat(extras, "rf_orient", Quaternion.Identity)
            };

            JsonElement frames = Resolve(extras, "rf_frames");
            if (frames.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in frames.EnumerateArray())
                {
                    d.Frames.Add(new VfxDummyFrame
                    {
                        Pos = GetVec3(f, "pos", Vector3.Zero),
                        Orient = GetQuat(f, "orient", Quaternion.Identity)
                    });
                }
            }

            if (d.Frames.Count > 0)
            {
                var times = Enumerable.Range(0, d.Frames.Count).Select(i => i / Fps).ToList();
                List<Trs> authored = d.Frames
                    .Select(f => new Trs(RfToRh(f.Pos), RfToRh(f.Orient), Vector3.One))
                    .ToList();
                List<Trs> sampled = SampleNodeWorld(ctx, nodeIndex, times);
                if (sampled.Count == authored.Count && !SamplesMatch(authored, sampled))
                {
                    for (int i = 0; i < d.Frames.Count; i++)
                    {
                        d.Frames[i].Pos = RhToRf(sampled[i].T);
                        d.Frames[i].Orient = RhToRf(sampled[i].R);
                    }
                    d.Pos = d.Frames[0].Pos;
                    d.Orient = d.Frames[0].Orient;
                }
            }

            return d;
        }

        private static VfxParticleSystem BuildParticleSystem(Context ctx, int nodeIndex, JsonElement node, JsonElement extras)
        {
            var p = new VfxParticleSystem
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Particles")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                SaveParent = GetBool(extras, "rf_save_parent", false),
                Flags = (uint)GetLong(extras, "rf_flags", 0),
                StartTime = GetInt(extras, "rf_start_time", 0),
                MaterialIndex = GetInt(extras, "rf_material_index", -1),
                ParticleCount = GetInt(extras, "rf_particle_count", 0),
                Start = GetInt(extras, "rf_start", 0),
                Lifetime = GetInt(extras, "rf_lifetime", 0),
                LifetimeVariation = GetFloat(extras, "rf_lifetime_variation", 0f),
                EmitterType = GetInt(extras, "rf_emitter_type", 0),
                ShrinkAtBirth = GetFloat(extras, "rf_shrink_at_birth", 0f),
                ShrinkAtDeath = GetFloat(extras, "rf_shrink_at_death", 0f),
                FadeAtBirth = GetFloat(extras, "rf_fade_at_birth", 0f),
                FadeAtDeath = GetFloat(extras, "rf_fade_at_death", 0f)
            };

            JsonElement warps = Resolve(extras, "rf_warps");
            if (warps.ValueKind == JsonValueKind.Array)
                foreach (JsonElement w in warps.EnumerateArray())
                    p.Warps.Add(w.GetString() ?? string.Empty);

            if (p.Drops)
            {
                p.HasTailDistance = true;
                p.TailDistance = GetFloat(extras, "rf_tail_distance", 0f);
            }

            float[] pos = GetFloatArray(extras, "rf_frame_pos");
            float[] orient = GetFloatArray(extras, "rf_frame_orient");
            float[] width = GetFloatArray(extras, "rf_frame_width");
            float[] height = GetFloatArray(extras, "rf_frame_height");
            float[] drop = GetFloatArray(extras, "rf_frame_drop_size");
            float[] speed = GetFloatArray(extras, "rf_frame_speed");
            float[] speedVar = GetFloatArray(extras, "rf_frame_speed_variation");
            float[] birth = GetFloatArray(extras, "rf_frame_birth_rate");
            float[] opacity = GetFloatArray(extras, "rf_frame_opacity");
            int count = GetInt(extras, "rf_num_frames", width.Length);

            for (int i = 0; i < count; i++)
            {
                var fr = new VfxParticleFrame
                {
                    Pos = SafeVec3(pos, i * 3),
                    Orient = SafeQuat(orient, i * 4),
                    Width = i < width.Length ? width[i] : 0f,
                    Height = i < height.Length ? height[i] : 0f,
                    DropSize = i < drop.Length ? drop[i] : 0f,
                    Speed = i < speed.Length ? speed[i] : 0f,
                    SpeedVariation = i < speedVar.Length ? speedVar[i] : 0f,
                    BirthRate = i < birth.Length ? birth[i] : 0f
                };
                if (i < opacity.Length) { fr.HasOpacity = true; fr.Opacity = opacity[i]; }
                p.Frames.Add(fr);
            }

            if (p.Frames.Count > 0)
            {
                var times = Enumerable.Range(0, p.Frames.Count).Select(i => (p.StartTime + i) / Fps).ToList();
                List<Trs> authored = p.Frames.Select(f => new Trs(RfToRh(f.Pos), RfToRh(f.Orient), Vector3.One)).ToList();
                List<Trs> sampled = SampleNodeWorld(ctx, nodeIndex, times);
                if (sampled.Count == authored.Count && !SamplesMatch(authored, sampled))
                {
                    for (int i = 0; i < p.Frames.Count; i++)
                    {
                        p.Frames[i].Pos = RhToRf(sampled[i].T);
                        p.Frames[i].Orient = RhToRf(sampled[i].R);
                    }
                }
            }

            return p;
        }

        private static VfxLight BuildLight(JsonElement node, JsonElement extras)
        {
            static VfxLightParams Params(JsonElement e) => new()
            {
                Pos = GetVec3(e, "pos", Vector3.Zero),
                Radius = GetFloat(e, "radius", 0f),
                Multiplier = GetFloat(e, "multiplier", 0f),
                Color = GetVec3(e, "color", Vector3.One),
                IsOn = GetBool(e, "is_on", true)
            };

            var l = new VfxLight
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Light")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                SaveParent = GetBool(extras, "rf_save_parent", false)
            };
            JsonElement pe = Resolve(extras, "rf_params");
            if (pe.ValueKind == JsonValueKind.Object)
                l.Params = Params(pe);
            JsonElement frames = Resolve(extras, "rf_frames");
            if (frames.ValueKind == JsonValueKind.Array)
                foreach (JsonElement f in frames.EnumerateArray())
                    l.Frames.Add(Params(f));
            return l;
        }

        private static VfxSpacewarp BuildSpacewarp(JsonElement node, JsonElement extras)
        {
            var w = new VfxSpacewarp
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Warp")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                Type = GetInt(extras, "rf_warp_type", 0)
            };
            float[] pos = GetFloatArray(extras, "rf_frame_pos");
            float[] orient = GetFloatArray(extras, "rf_frame_orient");
            float[] strength = GetFloatArray(extras, "rf_frame_strength");
            float[] decay = GetFloatArray(extras, "rf_frame_decay");
            float[] turbulence = GetFloatArray(extras, "rf_frame_turbulence");
            float[] frequency = GetFloatArray(extras, "rf_frame_frequency");
            float[] scale = GetFloatArray(extras, "rf_frame_scale");
            for (int i = 0; i < strength.Length; i++)
            {
                w.Frames.Add(new VfxSpacewarpFrame
                {
                    Pos = SafeVec3(pos, i * 3),
                    Orient = SafeQuat(orient, i * 4),
                    Strength = strength[i],
                    Decay = i < decay.Length ? decay[i] : 0f,
                    Turbulence = i < turbulence.Length ? turbulence[i] : 0f,
                    Frequency = i < frequency.Length ? frequency[i] : 0f,
                    Scale = i < scale.Length ? scale[i] : 0f
                });
            }
            return w;
        }

        private static VfxCamera BuildCamera(JsonElement node, JsonElement extras)
        {
            var c = new VfxCamera
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Camera")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                StartFrame = GetInt(extras, "rf_start_frame", 0),
                EndFrame = GetInt(extras, "rf_end_frame", 0)
            };
            JsonElement frames = Resolve(extras, "rf_frames");
            if (frames.ValueKind == JsonValueKind.Array)
                foreach (JsonElement f in frames.EnumerateArray())
                    c.Frames.Add(new VfxDummyFrame
                    {
                        Pos = GetVec3(f, "pos", Vector3.Zero),
                        Orient = GetQuat(f, "orient", Quaternion.Identity)
                    });
            return c;
        }

        private static VfxChain BuildChain(JsonElement node, JsonElement extras)
        {
            var c = new VfxChain
            {
                Name = GetString(extras, "rf_name", GetString(node, "name", "Chain")),
                ParentName = GetString(extras, "rf_parent_name", "Scene Root"),
                SaveParent = GetBool(extras, "rf_save_parent", false),
                VertexCount = GetInt(extras, "rf_vertex_count", 0),
                Width = GetFloat(extras, "rf_width", 0f),
                GlowName = GetString(extras, "rf_glow_name", string.Empty),
                Flags = (uint)GetLong(extras, "rf_flags", 0),
                FramesPerSecond = GetInt(extras, "rf_fps", 15),
                StartTime = GetFloat(extras, "rf_start_time", 0f),
                EndTime = GetFloat(extras, "rf_end_time", 0f),
                IsKeyframed = GetBool(extras, "rf_is_keyframed", false)
            };

            int frameCount = GetInt(extras, "rf_num_frames", 0);
            Dictionary<int, JsonElement> posFrames = IndexByFrame(extras, "rf_pos_frames");
            Dictionary<int, JsonElement> transforms = IndexByFrame(extras, "rf_frame_transforms");
            bool[] visible = GetBoolArray(extras, "rf_frame_visible");

            for (int i = 0; i < frameCount; i++)
            {
                var fr = new VfxChainFrame { Visible = i < visible.Length ? visible[i] : true };
                if (posFrames.TryGetValue(i, out JsonElement pf))
                {
                    fr.HasPositions = true;
                    fr.Center = GetVec3(pf, "center", Vector3.Zero);
                    fr.PositionsMultiplier = GetVec3(pf, "multiplier", new Vector3(VfxPositionCodec.MinHalfExtent / VfxPositionCodec.Scale));
                    fr.RawPositions = DecodeShorts(GetString(pf, "s16", string.Empty));
                    fr.Positions = VfxPositionCodec.Decompress(fr.Center, fr.PositionsMultiplier, fr.RawPositions, c.VertexCount);
                }
                if (transforms.TryGetValue(i, out JsonElement ft))
                {
                    fr.HasTransform = true;
                    fr.Translation = GetVec3(ft, "translation", Vector3.Zero);
                    fr.Rotation = GetQuat(ft, "rotation", Quaternion.Identity);
                    fr.Scale = GetVec3(ft, "scale", Vector3.One);
                }
                c.Frames.Add(fr);
            }
            c.NumFrames = c.Frames.Count;

            if (extras.TryGetProperty("rf_base_translation", out _))
            {
                c.HasBaseTransform = true;
                c.BaseTranslation = GetVec3(extras, "rf_base_translation", Vector3.Zero);
                c.BaseRotation = GetQuat(extras, "rf_base_rotation", Quaternion.Identity);
                c.BaseScale = GetVec3(extras, "rf_base_scale", Vector3.One);
            }
            JsonElement kf = Resolve(extras, "rf_keyframes");
            if (kf.ValueKind == JsonValueKind.Object)
            {
                c.TranslationKeys = ReadVec3Keys(kf, "translation");
                c.RotationKeys = ReadQuatKeys(kf, "rotation");
                c.ScaleKeys = ReadVec3Keys(kf, "scale");
            }
            return c;
        }

        // ─── animation sampling ────────────────────────────────────────────────────────────────

        private static bool SamplesMatch(List<Trs> a, List<Trs> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if ((a[i].T - b[i].T).Length() > TransformEpsilon) return false;
                if ((a[i].S - b[i].S).Length() > TransformEpsilon) return false;
                float dot = MathF.Abs(Quaternion.Dot(a[i].R, b[i].R));
                if (dot < 1f - TransformEpsilon) return false;
            }
            return true;
        }

        private static List<Trs> SampleNodeWorld(Context ctx, int nodeIndex, List<float> times)
        {
            if (nodeIndex < 0 || nodeIndex >= ctx.Nodes.Count) return new List<Trs>();

            Trs local = ctx.LocalStatic(nodeIndex);
            Trs parentWorld = ctx.ParentWorldStatic(nodeIndex);

            List<(float T, Vector3 V)>? translation = null;
            List<(float T, Quaternion V)>? rotation = null;
            List<(float T, Vector3 V)>? scale = null;

            if (ctx.Root.TryGetProperty("animations", out JsonElement anims) && anims.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement anim in anims.EnumerateArray())
                {
                    if (!anim.TryGetProperty("channels", out JsonElement channels) || channels.ValueKind != JsonValueKind.Array)
                        continue;
                    if (!anim.TryGetProperty("samplers", out JsonElement samplers) || samplers.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement ch in channels.EnumerateArray())
                    {
                        if (!ch.TryGetProperty("target", out JsonElement target)) continue;
                        if (GetInt(target, "node", -1) != nodeIndex) continue;
                        string path = GetString(target, "path", string.Empty);
                        int samplerIndex = GetInt(ch, "sampler", -1);
                        if (samplerIndex < 0 || samplerIndex >= samplers.GetArrayLength()) continue;
                        JsonElement sampler = samplers[samplerIndex];
                        int input = GetInt(sampler, "input", -1);
                        int output = GetInt(sampler, "output", -1);
                        if (input < 0 || output < 0) continue;

                        List<float> keyTimes = ReadScalarAccessor(ctx, input);
                        switch (path)
                        {
                            case "translation":
                                translation = Zip(keyTimes, ReadVec3Accessor(ctx, output));
                                break;
                            case "rotation":
                                rotation = Zip(keyTimes, ReadQuatAccessor(ctx, output));
                                break;
                            case "scale":
                                scale = Zip(keyTimes, ReadVec3Accessor(ctx, output));
                                break;
                        }
                    }
                }
            }

            var result = new List<Trs>(times.Count);
            foreach (float t in times)
            {
                Vector3 tr = translation != null ? SampleVec3(translation, t) : local.T;
                Quaternion rot = rotation != null ? SampleQuat(rotation, t) : local.R;
                Vector3 sc = scale != null ? SampleVec3(scale, t) : local.S;
                result.Add(Compose(parentWorld, new Trs(tr, rot, sc)));
            }
            return result;
        }

        private static List<(float, T)> Zip<T>(List<float> times, List<T> values)
        {
            var result = new List<(float, T)>(Math.Min(times.Count, values.Count));
            for (int i = 0; i < times.Count && i < values.Count; i++)
                result.Add((times[i], values[i]));
            return result;
        }

        private static Vector3 SampleVec3(List<(float T, Vector3 V)> keys, float time)
        {
            if (keys.Count == 0) return Vector3.Zero;
            if (time <= keys[0].T) return keys[0].V;
            if (time >= keys[^1].T) return keys[^1].V;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time <= keys[i + 1].T)
                {
                    float span = keys[i + 1].T - keys[i].T;
                    float u = span > 1e-9f ? (time - keys[i].T) / span : 0f;
                    return Vector3.Lerp(keys[i].V, keys[i + 1].V, u);
                }
            }
            return keys[^1].V;
        }

        private static Quaternion SampleQuat(List<(float T, Quaternion V)> keys, float time)
        {
            if (keys.Count == 0) return Quaternion.Identity;
            if (time <= keys[0].T) return keys[0].V;
            if (time >= keys[^1].T) return keys[^1].V;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time <= keys[i + 1].T)
                {
                    float span = keys[i + 1].T - keys[i].T;
                    float u = span > 1e-9f ? (time - keys[i].T) / span : 0f;
                    return Quaternion.Normalize(Quaternion.Slerp(keys[i].V, keys[i + 1].V, u));
                }
            }
            return keys[^1].V;
        }

        // ─── accessors ─────────────────────────────────────────────────────────────────────────

        private static void ReadPrimitiveGeometry(Context ctx, int meshIndex, GltfGeometry g)
        {
            if (!ctx.Root.TryGetProperty("meshes", out JsonElement meshes) || meshes.ValueKind != JsonValueKind.Array) return;
            if (meshIndex < 0 || meshIndex >= meshes.GetArrayLength()) return;
            JsonElement mesh = meshes[meshIndex];
            if (!mesh.TryGetProperty("primitives", out JsonElement prims) || prims.ValueKind != JsonValueKind.Array) return;

            // redux shares one vertex buffer between the primitives of a mesh while Blender gives
            // each primitive its own, so a primitive is only appended when its POSITION accessor
            // has not been seen yet; otherwise its triangles index the range already read.
            var baseByPositionAccessor = new Dictionary<int, int>();

            foreach (JsonElement prim in prims.EnumerateArray())
            {
                if (prim.TryGetProperty("mode", out JsonElement mode) && mode.GetInt32() != 4)
                    continue;
                if (!prim.TryGetProperty("attributes", out JsonElement attrs) ||
                    !attrs.TryGetProperty("POSITION", out JsonElement posAcc))
                    continue;

                int positionAccessor = posAcc.GetInt32();
                if (baseByPositionAccessor.TryGetValue(positionAccessor, out int sharedBase))
                {
                    AppendTriangles(ctx, prim, g, sharedBase, ReadAccessorCount(ctx, positionAccessor));
                    continue;
                }

                int baseIndex = g.Positions.Count;
                baseByPositionAccessor[positionAccessor] = baseIndex;
                List<Vector3> p = ReadVec3Accessor(ctx, positionAccessor);
                g.Positions.AddRange(p);

                List<Vector3> n = attrs.TryGetProperty("NORMAL", out JsonElement na)
                    ? ReadVec3Accessor(ctx, na.GetInt32())
                    : new List<Vector3>();
                while (n.Count < p.Count) n.Add(Vector3.Zero);
                g.Normals.AddRange(n.Take(p.Count));

                List<Vector2> u = attrs.TryGetProperty("TEXCOORD_0", out JsonElement ua)
                    ? ReadVec2Accessor(ctx, ua.GetInt32())
                    : new List<Vector2>();
                while (u.Count < p.Count) u.Add(Vector2.Zero);
                g.Uvs.AddRange(u.Take(p.Count));

                List<Vector4> c = attrs.TryGetProperty("COLOR_0", out JsonElement ca)
                    ? ReadColorAccessor(ctx, ca.GetInt32())
                    : new List<Vector4>();
                while (c.Count < p.Count) c.Add(Vector4.One);
                g.Colors.AddRange(c.Take(p.Count));

                // Morph deltas are per primitive; they are gathered into one array covering every
                // vertex, so a mesh split across primitives still reassembles.
                if (prim.TryGetProperty("targets", out JsonElement tgts) && tgts.ValueKind == JsonValueKind.Array)
                {
                    int t = 0;
                    foreach (JsonElement tg in tgts.EnumerateArray())
                    {
                        while (g.MorphTargets.Count <= t) g.MorphTargets.Add(new List<Vector3>());
                        List<Vector3> slot = g.MorphTargets[t++];
                        while (slot.Count < baseIndex) slot.Add(Vector3.Zero);
                        List<Vector3> d = tg.TryGetProperty("POSITION", out JsonElement tp)
                            ? ReadVec3Accessor(ctx, tp.GetInt32())
                            : new List<Vector3>();
                        for (int i = 0; i < p.Count; i++)
                            slot.Add(i < d.Count ? d[i] : Vector3.Zero);
                    }
                }

                AppendTriangles(ctx, prim, g, baseIndex, p.Count);
            }
        }

        private static void AppendTriangles(Context ctx, JsonElement prim, GltfGeometry g, int baseIndex, int vertexCount)
        {
            int material = prim.TryGetProperty("material", out JsonElement ma) ? ma.GetInt32() : -1;
            // -2 means "no hint"; Blender drops primitive extras, so this is usually absent.
            int slotHint = -2;
            if (prim.TryGetProperty("extras", out JsonElement pex) && pex.ValueKind == JsonValueKind.Object)
                slotHint = GetInt(pex, "rf_face_material_index", -2);

            if (prim.TryGetProperty("indices", out JsonElement ia))
            {
                List<int> idx = ReadIndexAccessor(ctx, ia.GetInt32());
                for (int i = 0; i + 2 < idx.Count; i += 3)
                    g.Triangles.Add(new GltfTriangle(baseIndex + idx[i], baseIndex + idx[i + 1], baseIndex + idx[i + 2], material, slotHint));
            }
            else
            {
                for (int i = 0; i + 2 < vertexCount; i += 3)
                    g.Triangles.Add(new GltfTriangle(baseIndex + i, baseIndex + i + 1, baseIndex + i + 2, material, slotHint));
            }
        }

        private static int ReadAccessorCount(Context ctx, int accessorIndex)
            => GetAccessor(ctx, accessorIndex).Count;

        private readonly struct AccessorInfo
        {
            public readonly int Offset;
            public readonly int Stride;
            public readonly int Count;
            public readonly int ComponentType;
            public readonly int ComponentSize;
            public readonly bool Normalized;

            public AccessorInfo(int offset, int stride, int count, int componentType, int componentSize, bool normalized)
            {
                Offset = offset; Stride = stride; Count = count;
                ComponentType = componentType; ComponentSize = componentSize; Normalized = normalized;
            }
        }

        private static AccessorInfo GetAccessor(Context ctx, int index)
        {
            JsonElement a = ctx.Root.GetProperty("accessors")[index];
            int componentType = GetInt(a, "componentType", 5126);
            string type = GetString(a, "type", "SCALAR");
            int count = GetInt(a, "count", 0);
            bool normalized = GetBool(a, "normalized", false);
            int componentSize = componentType switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                _ => 4
            };
            int componentCount = type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                _ => 1
            };

            int offset = GetInt(a, "byteOffset", 0);
            int stride = componentSize * componentCount;
            if (a.TryGetProperty("bufferView", out JsonElement bv))
            {
                JsonElement view = ctx.Root.GetProperty("bufferViews")[bv.GetInt32()];
                offset += GetInt(view, "byteOffset", 0);
                stride = GetInt(view, "byteStride", 0);
                if (stride <= 0) stride = componentSize * componentCount;
            }
            return new AccessorInfo(offset, stride, count, componentType, componentSize, normalized);
        }

        private static float ReadComponent(byte[] b, int offset, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case 5120: return normalized ? MathF.Max((sbyte)b[offset] / 127f, -1f) : (sbyte)b[offset];
                case 5121: return normalized ? b[offset] / 255f : b[offset];
                case 5122:
                    {
                        short v = BitConverter.ToInt16(b, offset);
                        return normalized ? MathF.Max(v / 32767f, -1f) : v;
                    }
                case 5123:
                    {
                        ushort v = BitConverter.ToUInt16(b, offset);
                        return normalized ? v / 65535f : v;
                    }
                case 5125: return BitConverter.ToUInt32(b, offset);
                default: return BitConverter.ToSingle(b, offset);
            }
        }

        private static List<Vector3> ReadVec3Accessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            var result = new List<Vector3>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + 3 * a.ComponentSize > ctx.Buffer.Length) break;
                result.Add(new Vector3(
                    ReadComponent(ctx.Buffer, o, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize * 2, a.ComponentType, a.Normalized)));
            }
            return result;
        }

        private static List<Vector2> ReadVec2Accessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            var result = new List<Vector2>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + 2 * a.ComponentSize > ctx.Buffer.Length) break;
                result.Add(new Vector2(
                    ReadComponent(ctx.Buffer, o, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize, a.ComponentType, a.Normalized)));
            }
            return result;
        }

        private static List<Vector4> ReadColorAccessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            JsonElement acc = ctx.Root.GetProperty("accessors")[index];
            int components = GetString(acc, "type", "VEC4") == "VEC3" ? 3 : 4;
            var result = new List<Vector4>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + components * a.ComponentSize > ctx.Buffer.Length) break;
                float x = ReadComponent(ctx.Buffer, o, a.ComponentType, a.Normalized);
                float y = ReadComponent(ctx.Buffer, o + a.ComponentSize, a.ComponentType, a.Normalized);
                float z = ReadComponent(ctx.Buffer, o + a.ComponentSize * 2, a.ComponentType, a.Normalized);
                float w = components == 4 ? ReadComponent(ctx.Buffer, o + a.ComponentSize * 3, a.ComponentType, a.Normalized) : 1f;
                result.Add(new Vector4(x, y, z, w));
            }
            return result;
        }

        private static List<Quaternion> ReadQuatAccessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            var result = new List<Quaternion>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + 4 * a.ComponentSize > ctx.Buffer.Length) break;
                result.Add(new Quaternion(
                    ReadComponent(ctx.Buffer, o, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize * 2, a.ComponentType, a.Normalized),
                    ReadComponent(ctx.Buffer, o + a.ComponentSize * 3, a.ComponentType, a.Normalized)));
            }
            return result;
        }

        private static List<float> ReadScalarAccessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            var result = new List<float>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + a.ComponentSize > ctx.Buffer.Length) break;
                result.Add(ReadComponent(ctx.Buffer, o, a.ComponentType, a.Normalized));
            }
            return result;
        }

        private static List<int> ReadIndexAccessor(Context ctx, int index)
        {
            AccessorInfo a = GetAccessor(ctx, index);
            var result = new List<int>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (o + a.ComponentSize > ctx.Buffer.Length) break;
                result.Add((int)ReadComponent(ctx.Buffer, o, a.ComponentType, false));
            }
            return result;
        }

        // ─── extras helpers ────────────────────────────────────────────────────────────────────

        // Blender stores glTF extras as ID properties. When a dictionary holds something it cannot
        // represent it keeps the whole thing as a Python repr string instead, so a container-valued
        // extra has to be accepted in three shapes: the real thing, a JSON string, or a Python repr
        // string. Anything that will not parse is reported as absent, which sends the caller down
        // the rebuild-from-glTF path rather than letting a half-read table through.
        private static JsonElement Resolve(JsonElement parent, string key)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out JsonElement value))
                return default;
            if (value.ValueKind != JsonValueKind.String)
                return value;

            string text = value.GetString() ?? string.Empty;
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return value; // a genuine string, e.g. rf_name

            if (TryParseContainer(text, out JsonElement parsed))
                return parsed;

            Logger.Warn(logSrc, $"Extra \"{key}\" arrived as an unparseable string; treating it as absent.");
            return default;
        }

        private static bool TryParseContainer(string text, out JsonElement parsed)
        {
            foreach (string candidate in new[] { text, PythonReprToJson(text) })
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(candidate);
                    // Clone so the element outlives the document it was parsed from.
                    parsed = doc.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    // fall through and try the next spelling
                }
            }
            parsed = default;
            return false;
        }

        // Converts a Python repr - single-quoted strings plus True/False/None - into JSON. Only the
        // literal forms Blender produces are handled; anything else fails to parse and the extra is
        // then treated as absent.
        private static string PythonReprToJson(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\"')
                {
                    // Already a JSON string: copy it through verbatim.
                    sb.Append(c);
                    for (i++; i < text.Length; i++)
                    {
                        sb.Append(text[i]);
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
                        if (text[i] == '\"') break;
                    }
                    continue;
                }
                if (c == '\'')
                {
                    sb.Append('\"');
                    for (i++; i < text.Length; i++)
                    {
                        char d = text[i];
                        if (d == '\\' && i + 1 < text.Length)
                        {
                            char e = text[++i];
                            if (e == '\'') sb.Append('\'');
                            else { sb.Append('\\'); sb.Append(e); }
                            continue;
                        }
                        if (d == '\'') { sb.Append('\"'); break; }
                        if (d == '\"') { sb.Append('\\'); sb.Append('\"'); continue; }
                        sb.Append(d);
                    }
                    continue;
                }
                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < text.Length && char.IsLetter(text[i])) i++;
                    string word = text[start..i];
                    sb.Append(word switch
                    {
                        "True" => "true",
                        "False" => "false",
                        "None" => "null",
                        _ => word
                    });
                    i--;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static Dictionary<int, JsonElement> IndexByFrame(JsonElement extras, string key)
        {
            var result = new Dictionary<int, JsonElement>();
            JsonElement arr = Resolve(extras, key);
            if (arr.ValueKind != JsonValueKind.Array)
                return result;
            int fallback = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                int frame = GetInt(e, "frame", fallback);
                result[frame] = e;
                fallback = frame + 1;
            }
            return result;
        }

        private static Dictionary<int, float[]> IndexUvFrames(JsonElement extras)
        {
            var result = new Dictionary<int, float[]>();
            JsonElement arr = Resolve(extras, "rf_uv_frames");
            if (arr.ValueKind != JsonValueKind.Array)
                return result;
            int fallback = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Array)
                {
                    // Older layout: a bare array per frame, starting at frame 1.
                    result[fallback + 1] = e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                    fallback++;
                    continue;
                }
                int frame = GetInt(e, "frame", fallback);
                result[frame] = GetFloatArray(e, "uvs");
                fallback = frame + 1;
            }
            return result;
        }

        private static Dictionary<int, (float W, float H)> IndexSizes(JsonElement extras)
        {
            var result = new Dictionary<int, (float, float)>();
            JsonElement arr = Resolve(extras, "rf_frame_sizes");
            if (arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3) continue;
                int frame = (int)e[0].GetDouble();
                result[frame] = ((float)e[1].GetDouble(), (float)e[2].GetDouble());
            }
            return result;
        }

        private static List<VfxVec3Key> ReadVec3Keys(JsonElement keyframes, string key)
        {
            var result = new List<VfxVec3Key>();
            JsonElement arr = Resolve(keyframes, key);
            if (arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                result.Add(new VfxVec3Key
                {
                    Time = GetInt(e, "time", 0),
                    Value = GetVec3(e, "value", Vector3.Zero),
                    InTangent = GetVec3(e, "in_tangent", Vector3.Zero),
                    OutTangent = GetVec3(e, "out_tangent", Vector3.Zero)
                });
            }
            return result;
        }

        private static List<VfxQuatKey> ReadQuatKeys(JsonElement keyframes, string key)
        {
            var result = new List<VfxQuatKey>();
            JsonElement arr = Resolve(keyframes, key);
            if (arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                result.Add(new VfxQuatKey
                {
                    Time = GetInt(e, "time", 0),
                    Value = GetQuat(e, "value", Quaternion.Identity),
                    Tension = GetFloat(e, "tension", 0f),
                    Continuity = GetFloat(e, "continuity", 0f),
                    Bias = GetFloat(e, "bias", 0f),
                    EaseIn = GetFloat(e, "ease_in", 0f),
                    EaseOut = GetFloat(e, "ease_out", 0f)
                });
            }
            return result;
        }

        private static uint[] DecodeUInts(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return Array.Empty<uint>();
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                var result = new uint[bytes.Length / 4];
                System.Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
                return result;
            }
            catch (FormatException)
            {
                return Array.Empty<uint>();
            }
        }

        private static short[] DecodeShorts(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return Array.Empty<short>();
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                var result = new short[bytes.Length / 2];
                System.Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 2);
                return result;
            }
            catch (FormatException)
            {
                return Array.Empty<short>();
            }
        }

        private static Vector3 SafeVec3(float[] values, int offset)
            => offset + 2 < values.Length
                ? new Vector3(values[offset], values[offset + 1], values[offset + 2])
                : Vector3.Zero;

        private static Quaternion SafeQuat(float[] values, int offset)
            => offset + 3 < values.Length
                ? new Quaternion(values[offset], values[offset + 1], values[offset + 2], values[offset + 3])
                : Quaternion.Identity;

        private static string GetString(JsonElement e, string key, string fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback
                : fallback;

        private static int GetInt(JsonElement e, string key, int fallback)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out int i) ? i : (int)v.GetDouble(),
                JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : fallback,
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => fallback
            };
        }

        private static long GetLong(JsonElement e, string key, long fallback)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt64(out long i) ? i : (long)v.GetDouble(),
                JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : fallback,
                _ => fallback
            };
        }

        private static float GetFloat(JsonElement e, string key, float fallback)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.Number => (float)v.GetDouble(),
                JsonValueKind.String => float.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : fallback,
                _ => fallback
            };
        }

        private static bool GetBool(JsonElement e, string key, bool fallback)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.GetDouble() != 0,
                _ => fallback
            };
        }

        private static float[] GetFloatArray(JsonElement e, string key)
        {
            JsonElement v = Resolve(e, key);
            if (v.ValueKind != JsonValueKind.Array)
                return Array.Empty<float>();
            var result = new float[v.GetArrayLength()];
            int i = 0;
            foreach (JsonElement x in v.EnumerateArray())
                result[i++] = x.ValueKind == JsonValueKind.Number ? (float)x.GetDouble() : 0f;
            return result;
        }

        private static List<float> GetFloatList(JsonElement e, string key) => GetFloatArray(e, key).ToList();

        private static int[] GetIntArray(JsonElement e, string key)
        {
            JsonElement v = Resolve(e, key);
            if (v.ValueKind != JsonValueKind.Array)
                return Array.Empty<int>();
            var result = new int[v.GetArrayLength()];
            int i = 0;
            foreach (JsonElement x in v.EnumerateArray())
                result[i++] = x.ValueKind == JsonValueKind.Number ? (x.TryGetInt32(out int n) ? n : (int)x.GetDouble()) : 0;
            return result;
        }

        private static uint[] GetUIntArray(JsonElement e, string key)
        {
            JsonElement v = Resolve(e, key);
            if (v.ValueKind != JsonValueKind.Array)
                return Array.Empty<uint>();
            var result = new uint[v.GetArrayLength()];
            int i = 0;
            foreach (JsonElement x in v.EnumerateArray())
                result[i++] = x.ValueKind == JsonValueKind.Number ? (x.TryGetUInt32(out uint n) ? n : (uint)x.GetDouble()) : 0u;
            return result;
        }

        private static bool[] GetBoolArray(JsonElement e, string key)
        {
            JsonElement v = Resolve(e, key);
            if (v.ValueKind != JsonValueKind.Array)
                return Array.Empty<bool>();
            var result = new bool[v.GetArrayLength()];
            int i = 0;
            foreach (JsonElement x in v.EnumerateArray())
                result[i++] = x.ValueKind == JsonValueKind.True || (x.ValueKind == JsonValueKind.Number && x.GetDouble() != 0);
            return result;
        }

        private static Vector3 GetVec3(JsonElement e, string key, Vector3 fallback)
        {
            float[] v = GetFloatArray(e, key);
            return v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : fallback;
        }

        private static Quaternion GetQuat(JsonElement e, string key, Quaternion fallback)
        {
            float[] v = GetFloatArray(e, key);
            return v.Length >= 4 ? new Quaternion(v[0], v[1], v[2], v[3]) : fallback;
        }

        private static Vector3 ReadVec3Prop(JsonElement node, string key, Vector3 fallback)
        {
            float[] v = GetFloatArray(node, key);
            return v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : fallback;
        }

        private static Quaternion ReadQuatProp(JsonElement node, string key, Quaternion fallback)
        {
            float[] v = GetFloatArray(node, key);
            return v.Length >= 4 ? new Quaternion(v[0], v[1], v[2], v[3]) : fallback;
        }

        // ─── math ──────────────────────────────────────────────────────────────────────────────

        private static Vector3 RfToRh(Vector3 v) => new(-v.X, v.Y, v.Z);
        private static Vector3 RhToRf(Vector3 v) => new(-v.X, v.Y, v.Z);

        private static Quaternion RfToRh(Quaternion q)
        {
            var r = new Quaternion(-q.X, q.Y, q.Z, q.W);
            return r.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(r);
        }

        private static Quaternion RhToRf(Quaternion q) => RfToRh(q);

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
        }

        private static Trs Compose(Trs outer, Trs inner)
        {
            Quaternion r = Quaternion.Normalize(Quaternion.Multiply(outer.R, inner.R));
            Vector3 s = outer.S * inner.S;
            Vector3 t = outer.T + Vector3.Transform(outer.S * inner.T, outer.R);
            return new Trs(t, r, s);
        }
    }
}
