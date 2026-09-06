using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace redux.exporters
{
    public static class V3mExporter
    {
        private const string logSrc = "V3mExporter";
        private const int V3M_SIGNATURE = 0x52463344; // 'RF3D'
        private const int V3C_SIGNATURE = 0x5246434D; // 'RFCM'
        private const int V3D_VERSION = 0x40000;
        private const int V3D_SECTION_SUBMESH = 0x5355424D; // 'SUBM'
        private const int V3D_SECTION_CSPHERE = 0x43535048; // 'CSPH'
        private const int V3D_SECTION_BONES = 0x424F4E45; // 'BONE'
        private const int V3D_SECTION_END = 0x00000000;
        private const uint V3D_LOD_ORIG_MAP = 0x01;
        private const uint V3D_LOD_CHARACTER = 0x02;
        private const uint V3D_LOD_COLLISION = 0x10;
        private const uint V3D_LOD_TRIANGLE_PLANES = 0x20;
        // Stock character LODs are always CHARACTER | ORIG_MAP with no triangle-plane block.
        private const uint V3D_LOD_CHARACTER_FLAGS = V3D_LOD_CHARACTER | V3D_LOD_ORIG_MAP;
        // RF D3D vif render path uses dynamic buffers with practical per-batch limits
        // (~6000 vertices / ~10000 indices) and keeps extra headroom for clipping.
        // Keep chunk limits below both runtime limits and on-disk ushort allocation limits.
        private const int MaxChunkVertices = 5232; // min(65535/12, 6000-768)
        private const int MaxChunkFaces = 3077; // min(65535/8, (10000-768)/3)

        private static int Align16(int value) => (value + 15) & ~15;

        // RED's same_pos_vertex_offsets allocation: the nv int16 entries plus four bytes of slack for
        // every vertex needed to round nv up to a multiple of 4. Derived from all 1552 chunks in the
        // stock vehicle mesh set with zero exceptions.
        private static int SamePosAlloc(int numVertices) => (numVertices * 2) + (4 * ((4 - (numVertices % 4)) % 4));

        public static void ExportV3m(Mesh mesh, string outputPath)
            => ExportV3m(mesh, outputPath, forceCharacterMesh: false);

        public static void ExportV3m(Mesh mesh, string outputPath, bool forceCharacterMesh)
        {
            // Only a real skeleton makes this a character mesh. Collision spheres are legal in static
            // V3M files, and joint arrays are filled in by default for every brush the V3M parser reads,
            // so neither is evidence of skinning.
            bool writeCharacterMesh = forceCharacterMesh || mesh.Bones.Count > 0;
            if (!forceCharacterMesh && writeCharacterMesh)
                Logger.Info(logSrc, $"Writing '{Path.GetFileName(outputPath)}' as a character mesh (RFCM) because the source has a skeleton ({mesh.Bones.Count} bones).");
            List<SubmeshExportGroup> submeshGroups = BuildSubmeshGroups(mesh.Brushes);

            Logger.Dev(logSrc, $"ExportV3m: '{outputPath}', submesh count={submeshGroups.Count}, character={writeCharacterMesh}");
            using var writer = new BinaryWriter(File.Create(outputPath));

            writer.Write(writeCharacterMesh ? V3C_SIGNATURE : V3M_SIGNATURE);
            writer.Write(V3D_VERSION);
            writer.Write(submeshGroups.Count);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            int totalMaterials = submeshGroups.Sum(g => g.Materials.Count);
            writer.Write(totalMaterials);
            writer.Write(0);
            writer.Write(0);
            // Collision spheres are valid in static meshes too, so always advertise them.
            writer.Write(mesh.CollisionSpheres.Count);

            foreach (var group in submeshGroups)
                WriteSubmesh(group, writer, writeCharacterMesh);

            foreach (var sphere in mesh.CollisionSpheres)
                WriteCollisionSphere(sphere, writer);

            if (writeCharacterMesh)
                WriteBones(mesh.Bones, writer);

            writer.Write(V3D_SECTION_END);
            writer.Write(0);
            Logger.Dev(logSrc, "ExportV3m complete");
        }

        private static void WriteSubmesh(SubmeshExportGroup group, BinaryWriter writer, bool writeCharacterMesh)
        {
            if (group.Lods.Count == 0)
                return;

            Logger.Dev(logSrc, $"-- Submesh begin {group.Name}, lods={group.Lods.Count}");

            writer.Write(V3D_SECTION_SUBMESH);
            writer.Write(0);

            WriteFixedString(writer, group.Name, 24);
            // RED writes the literal string "None" when a submesh has no parent, but weapon meshes
            // repeat the submesh's own name here, so carry whatever the source had.
            string parentName = group.Lods
                .Select(b => b.SubmeshParent)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "None";
            WriteFixedString(writer, parentName, 24);
            writer.Write(7);

            int numLods = group.Lods.Count;
            writer.Write(numLods);
            float[] lodDistances = BuildLodDistances(group.Lods);
            for (int i = 0; i < lodDistances.Length; i++)
                writer.Write(lodDistances[i]);

            var lodMaterialChunks = new List<List<MaterialChunk>>(numLods);
            var lod0Pts = new List<Vector3>();
            for (int i = 0; i < numLods; i++)
            {
                List<MaterialChunk> chunks = ApplyChunkLimits(GatherGeometry(group.Lods[i], writeCharacterMesh));
                lodMaterialChunks.Add(chunks);
                if (i == 0)
                {
                    foreach (MaterialChunk chunk in chunks)
                        lod0Pts.AddRange(chunk.Geometry.Positions);
                }
            }

            // RED derives the submesh bounds from LOD0 only; lower LODs may stick outside them.
            // Static meshes are re-centred on the LOD0 AABB centre (bbox = +-half extents); character
            // meshes keep the model origin and store the raw, asymmetric LOD0 AABB.
            Vector3 aabbMin = Vector3.Zero;
            Vector3 aabbMax = Vector3.Zero;
            if (lod0Pts.Count > 0)
            {
                aabbMin = new Vector3(float.MaxValue);
                aabbMax = new Vector3(float.MinValue);
                foreach (var p in lod0Pts)
                {
                    aabbMin = Vector3.Min(aabbMin, p);
                    aabbMax = Vector3.Max(aabbMax, p);
                }
            }

            // RED's pivot is usually the LOD0 AABB centre but not always (APC), so prefer the authored
            // value when the source carried one. Character meshes always pivot on the model origin.
            Vector3 offset = writeCharacterMesh
                ? Vector3.Zero
                : group.Lods.Select(b => b.SubmeshOffset).FirstOrDefault(o => o.HasValue)
                    ?? (aabbMin + aabbMax) * 0.5f;

            Vector3 localMin = aabbMin - offset;
            Vector3 localMax = aabbMax - offset;
            // Static submeshes store a symmetric box of the largest absolute local extent per axis
            // (581/581 stock static submeshes); character submeshes store the raw local AABB.
            Vector3 absExtent = Vector3.Max(Vector3.Abs(localMin), Vector3.Abs(localMax));
            Vector3 bboxMin = writeCharacterMesh ? localMin : -absExtent;
            Vector3 bboxMax = writeCharacterMesh ? localMax : absExtent;

            // Rebase vertices and planes into submesh-local space (a no-op when offset is zero).
            // Prop points are NOT rebased: RF reads them straight from the file in model space.
            float radius = 0f;
            for (int li = 0; li < lodMaterialChunks.Count; li++)
            {
                foreach (MaterialChunk entry in lodMaterialChunks[li])
                {
                    Chunk c = entry.Geometry;
                    for (int i = 0; i < c.Positions.Count; i++)
                    {
                        Vector3 local = c.Positions[i] - offset;
                        c.Positions[i] = local;
                        if (li == 0)
                        {
                            float len = local.Length();
                            if (len > radius)
                                radius = len;
                        }
                    }
                    for (int i = 0; i < c.Planes.Count; i++)
                    {
                        (Vector3 n, float d) = c.Planes[i];
                        c.Planes[i] = (n, d + Vector3.Dot(n, offset));
                    }
                }
            }

            WriteVec3(writer, offset);
            writer.Write(radius);
            WriteVec3(writer, bboxMin);
            WriteVec3(writer, bboxMax);

            List<LodTextureRef> textureTable = BuildLodTextureTable(group, writeCharacterMesh);
            for (int i = 0; i < numLods; i++)
                WriteLod(group.Lods[i], lodMaterialChunks[i], writer, writeCharacterMesh,
                         BuildLodTextureRefs(group.Lods[i], group, lodMaterialChunks[i], textureTable, writeCharacterMesh));

            writer.Write(group.Materials.Count);
            for (int slot = 0; slot < group.Materials.Count; slot++)
            {
                V3mMaterialProps? props = slot < group.MaterialProps.Count ? group.MaterialProps[slot] : null;
                WriteFixedString(writer, NormalizeTextureFilename(group.Materials[slot]), 32);
                writer.Write(props?.Emissive ?? 0f);
                writer.Write(props?.Specular ?? 0f);
                writer.Write(props?.Glossiness ?? 0f);
                writer.Write(props?.Reflection ?? 0f);
                WriteFixedString(writer, props?.ReflectionMap ?? string.Empty, 32);
                writer.Write(props?.Flags ?? 1u);
            }

            writer.Write(1);
            WriteFixedString(writer, group.Name, 24);
            writer.Write(0f);
        }

        private static void WriteLod(Brush brush, List<MaterialChunk> materialChunks, BinaryWriter writer, bool writeCharacterMesh, List<LodTextureRef> textureRefs)
        {
            // Stock character LODs are always 0x03 (CHARACTER | ORIG_MAP) with no plane block; static
            // LODs are 0x20, plus 0x10 when the source marked the LOD as the collision LOD.
            uint sourceFlags = brush.LodFlags ?? 0;
            uint lodFlags = writeCharacterMesh
                ? V3D_LOD_CHARACTER_FLAGS
                : V3D_LOD_TRIANGLE_PLANES | (sourceFlags & V3D_LOD_COLLISION);
            bool writePlanes = (lodFlags & V3D_LOD_TRIANGLE_PLANES) != 0;
            bool writeOrigMap = (lodFlags & V3D_LOD_ORIG_MAP) != 0;
            writer.Write(lodFlags);

            // num_vertices is the count of unique positions across all chunks of this LOD, not the sum
            // of the per-chunk vertex counts. The same first-appearance ordering indexes orig_map.
            var uniquePositionIndex = new Dictionary<Vector3, int>();
            foreach (var c in materialChunks)
            {
                foreach (Vector3 p in c.Geometry.Positions)
                {
                    if (!uniquePositionIndex.ContainsKey(p))
                        uniquePositionIndex[p] = uniquePositionIndex.Count;
                }
            }
            int uniquePositionCount = uniquePositionIndex.Count;
            writer.Write(uniquePositionCount);
            writer.Write((ushort)materialChunks.Count);

            using var ms = new MemoryStream();
            using var dw = new BinaryWriter(ms);
            static void Align(BinaryWriter w, int alignment)
            {
                long pad = (alignment - (w.BaseStream.Position % alignment)) % alignment;
                if (pad > 0)
                    w.Write(new byte[pad]);
            }

            foreach (var entry in materialChunks)
            {
                int hnv = entry.Geometry.Positions.Count;
                int hnf = entry.Geometry.Triangles.Count;
                dw.Write(entry.RenderFlags);     // +0x00
                dw.Write(new byte[0x1C]);        // +0x04..+0x1F: stale runtime pointers in RED files
                dw.Write(ResolveTextureRefIndex(textureRefs, entry)); // +0x20
                dw.Write(0);                     // +0x24
                dw.Write((ushort)hnv);           // +0x28
                dw.Write((ushort)hnf);
                dw.Write((ushort)Align16(hnv * 12)); // vecs_alloc
                dw.Write((ushort)(hnf * 8));         // faces_alloc (RED stores the exact array size here)
                dw.Write((ushort)Align16(hnv * 8));  // uvs_alloc
                dw.Write((ushort)Align16(hnv * 8));  // wi_alloc
                dw.Write((ushort)SamePosAlloc(hnv)); // same_pos_alloc
                dw.Write((ushort)0);             // +0x36
            }
            Align(dw, 0x10);

            foreach (var entry in materialChunks)
            {
                Chunk chunk = entry.Geometry;

                foreach (var v in chunk.Positions)
                {
                    dw.Write(v.X);
                    dw.Write(v.Y);
                    dw.Write(v.Z);
                }
                Align(dw, 0x10);

                foreach (var n in chunk.Normals)
                {
                    dw.Write(n.X);
                    dw.Write(n.Y);
                    dw.Write(n.Z);
                }
                Align(dw, 0x10);

                foreach (var uv in chunk.UVs)
                {
                    dw.Write(uv.X);
                    dw.Write(uv.Y);
                }
                Align(dw, 0x10);

                foreach (var (i0, i1, i2, flags) in chunk.Triangles)
                {
                    dw.Write((ushort)i0);
                    dw.Write((ushort)i1);
                    dw.Write((ushort)i2);
                    dw.Write(flags);
                }
                Align(dw, 0x10);

                if (writePlanes)
                {
                    foreach (var (n, d) in chunk.Planes)
                    {
                        dw.Write(n.X);
                        dw.Write(n.Y);
                        dw.Write(n.Z);
                        dw.Write(d);
                    }
                    Align(dw, 0x10);
                }

                // same_pos_vertex_offsets: distance back to the first vertex in this chunk that shares
                // an identical position, or 0 when this vertex is the first with that position.
                var firstIndexByPosition = new Dictionary<Vector3, int>(chunk.Positions.Count);
                for (int i = 0; i < chunk.Positions.Count; i++)
                {
                    Vector3 p = chunk.Positions[i];
                    if (firstIndexByPosition.TryGetValue(p, out int firstIndex))
                    {
                        dw.Write((short)(i - firstIndex));
                    }
                    else
                    {
                        firstIndexByPosition[p] = i;
                        dw.Write((short)0);
                    }
                }
                // Pad out to the declared allocation so the block stays consistent with same_pos_alloc.
                int samePosSlack = SamePosAlloc(chunk.Positions.Count) - (chunk.Positions.Count * 2);
                if (samePosSlack > 0)
                    dw.Write(new byte[samePosSlack]);
                Align(dw, 0x10);

                if (writeCharacterMesh)
                {
                    for (int i = 0; i < chunk.Positions.Count; i++)
                    {
                        Vector4 weights = i < chunk.JointWeights.Count ? chunk.JointWeights[i] : new Vector4(1, 0, 0, 0);
                        Vector4 joints = i < chunk.JointIndices.Count ? chunk.JointIndices[i] : Vector4.Zero;

                        byte[] packedWeights = QuantizeWeights(weights);
                        byte[] packedJoints = QuantizeJoints(joints, packedWeights);

                        dw.Write(packedWeights);
                        dw.Write(packedJoints);
                    }
                    Align(dw, 0x10);
                }
                else
                {
                    // RED still reserves the bone-link block (all zeros) in static meshes.
                    dw.Write(new byte[chunk.Positions.Count * 8]);
                    Align(dw, 0x10);
                }

                if (writeOrigMap)
                {
                    // One int16 per unique position in the LOD: the index of the first vertex in this
                    // chunk carrying that position, or -1 when the chunk does not use it.
                    var firstByPosition = new Dictionary<Vector3, int>(chunk.Positions.Count);
                    for (int i = chunk.Positions.Count - 1; i >= 0; i--)
                        firstByPosition[chunk.Positions[i]] = i;

                    var origMap = new short[uniquePositionCount];
                    for (int i = 0; i < origMap.Length; i++)
                        origMap[i] = -1;
                    foreach (var kv in firstByPosition)
                    {
                        if (uniquePositionIndex.TryGetValue(kv.Key, out int slot) && slot < origMap.Length)
                            origMap[slot] = (short)kv.Value;
                    }
                    foreach (short v in origMap)
                        dw.Write(v);
                    Align(dw, 0x10);
                }
            }

            if (brush.PropPoints != null && brush.PropPoints.Count > 0)
            {
                foreach (var pp in brush.PropPoints)
                {
                    WriteFixedString(dw, pp.Name ?? string.Empty, 0x44);
                    // Written verbatim: RED does not always store unit-length prop quaternions.
                    Quaternion q = pp.Orientation.LengthSquared() < 1e-12f ? Quaternion.Identity : pp.Orientation;
                    dw.Write(q.X);
                    dw.Write(q.Y);
                    dw.Write(q.Z);
                    dw.Write(q.W);
                    // Model space, not the offset-relative space the vertices use.
                    dw.Write(pp.Position.X);
                    dw.Write(pp.Position.Y);
                    dw.Write(pp.Position.Z);
                    dw.Write(pp.ParentIndex);
                }
            }

            writer.Write((int)ms.Length);
            writer.Write(ms.ToArray());
            writer.Write(-1);

            foreach (var entry in materialChunks)
            {
                Chunk chunk = entry.Geometry;
                int nv = chunk.Positions.Count;
                int nf = chunk.Triangles.Count;
                writer.Write((ushort)nv);
                writer.Write((ushort)nf);
                writer.Write((ushort)Align16(nv * 12)); // vecs_alloc
                writer.Write((ushort)(nf * 8));         // faces_alloc (RED stores the exact array size here)
                writer.Write((ushort)SamePosAlloc(nv)); // same_pos_vertex_offsets_alloc
                writer.Write((ushort)Align16(nv * 8));  // wi_alloc (reserved even for static meshes)
                writer.Write((ushort)Align16(nv * 8));  // uvs_alloc
                writer.Write(entry.RenderFlags);
            }

            writer.Write(brush.PropPoints?.Count ?? 0);

            writer.Write((uint)textureRefs.Count);
            foreach (LodTextureRef tex in textureRefs)
            {
                writer.Write((byte)Math.Clamp(tex.Slot, 0, byte.MaxValue));
                WriteZeroTerminatedString(writer, tex.Name);
            }
        }

        // RED writes one LOD texture ref per unique diffuse filename. Static submeshes list every
        // name in the submesh material table (735/766 stock static LODs); character submeshes list
        // only the names their own chunks reference, because each LOD uses its own mip set
        // (151/170 stock character LODs).
        private static List<LodTextureRef> BuildLodTextureTable(SubmeshExportGroup group, bool writeCharacterMesh)
        {
            var table = new List<LodTextureRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int slot = 0; slot < group.Materials.Count; slot++)
            {
                string name = NormalizeTextureFilename(group.Materials[slot]);
                if (seen.Add(name))
                    table.Add(new LodTextureRef { Slot = slot, Name = name });
            }
            return table;
        }

        private static List<LodTextureRef> BuildLodTextureRefs(
            Brush lodBrush,
            SubmeshExportGroup group,
            List<MaterialChunk> materialChunks,
            List<LodTextureRef> submeshTable,
            bool writeCharacterMesh)
        {
            var usedSlots = new HashSet<int>(materialChunks.Select(c => c.TextureSlot));

            // Prefer the authored table: a LOD can reference textures that appear nowhere in the
            // material table (Fighter01 LOD2 -> 'Fighter_LOD2.tga'). Only use it when it still covers
            // every slot the chunks reference, so edited meshes fall back to a derived table.
            if (lodBrush.LodTextures != null && lodBrush.LodTextures.Count > 0 &&
                usedSlots.All(s => lodBrush.LodTextures.Any(t => t.Slot == s)))
            {
                return lodBrush.LodTextures
                    .Select(t => new LodTextureRef { Slot = t.Slot, Name = NormalizeTextureFilename(t.Name) })
                    .ToList();
            }

            if (!writeCharacterMesh)
                return submeshTable;

            // Each character LOD carries its own mip set, so only the referenced names belong here.
            var refs = new List<LodTextureRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int slot = 0; slot < group.Materials.Count; slot++)
            {
                if (!usedSlots.Contains(slot))
                    continue;
                string name = NormalizeTextureFilename(group.Materials[slot]);
                if (seen.Add(name))
                    refs.Add(new LodTextureRef { Slot = slot, Name = name });
            }
            if (refs.Count == 0 && submeshTable.Count > 0)
                refs.Add(submeshTable[0]);
            return refs;
        }

        // A chunk's 0x38 header stores an index into the LOD texture table, not a material slot.
        private static int ResolveTextureRefIndex(List<LodTextureRef> textureRefs, MaterialChunk entry)
        {
            for (int i = 0; i < textureRefs.Count; i++)
            {
                if (textureRefs[i].Slot == entry.TextureSlot)
                    return i;
            }

            string wanted = NormalizeTextureFilename(entry.TextureName);
            for (int i = 0; i < textureRefs.Count; i++)
            {
                if (string.Equals(textureRefs[i].Name, wanted, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        private static List<MaterialChunk> ApplyChunkLimits(List<MaterialChunk> input)
        {
            var output = new List<MaterialChunk>();
            foreach (MaterialChunk chunk in input)
            {
                List<MaterialChunk> split = SplitMaterialChunk(chunk);
                if (split.Count > 1)
                {
                    Logger.Warn(
                        logSrc,
                        $"Chunk for texture slot {chunk.TextureSlot} exceeded V3D limits and was split into {split.Count} chunks.");
                }

                output.AddRange(split);
            }
            return output;
        }

        private static List<MaterialChunk> SplitMaterialChunk(MaterialChunk source)
        {
            Chunk src = source.Geometry;
            if (src.Positions.Count <= MaxChunkVertices && src.Triangles.Count <= MaxChunkFaces)
                return new List<MaterialChunk> { source };

            var result = new List<MaterialChunk>();
            MaterialChunk current = CreateEmptyMaterialChunk(source);
            var vertexRemap = new Dictionary<int, int>();

            for (int triIndex = 0; triIndex < src.Triangles.Count; triIndex++)
            {
                var tri = src.Triangles[triIndex];
                int[] srcIndices = [tri.Item1, tri.Item2, tri.Item3];
                if (srcIndices.Any(idx => idx < 0 || idx >= src.Positions.Count))
                    continue;

                int vertsToAdd = 0;
                foreach (int idx in srcIndices)
                {
                    if (!vertexRemap.ContainsKey(idx))
                        vertsToAdd++;
                }

                bool wouldOverflow = current.Geometry.Positions.Count > 0 &&
                    (current.Geometry.Positions.Count + vertsToAdd > MaxChunkVertices ||
                     current.Geometry.Triangles.Count + 1 > MaxChunkFaces);
                if (wouldOverflow)
                {
                    result.Add(current);
                    current = CreateEmptyMaterialChunk(source);
                    vertexRemap.Clear();
                }

                int[] dstIndices = new int[3];
                for (int i = 0; i < 3; i++)
                    dstIndices[i] = MapVertex(src, current.Geometry, vertexRemap, srcIndices[i]);

                current.Geometry.Triangles.Add((dstIndices[0], dstIndices[1], dstIndices[2], tri.Item4));
                if (triIndex < src.Planes.Count)
                {
                    current.Geometry.Planes.Add(src.Planes[triIndex]);
                }
                else
                {
                    Vector3 p0 = current.Geometry.Positions[dstIndices[0]];
                    Vector3 p1 = current.Geometry.Positions[dstIndices[1]];
                    Vector3 p2 = current.Geometry.Positions[dstIndices[2]];
                    Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                    Vector3 normal = cross.LengthSquared() > 1e-8f ? Vector3.Normalize(cross) : Vector3.UnitZ;
                    float dist = -Vector3.Dot(normal, p0);
                    current.Geometry.Planes.Add((normal, dist));
                }
            }

            if (current.Geometry.Triangles.Count > 0)
                result.Add(current);

            if (result.Count == 0)
                result.Add(source);

            return result;
        }

        private static MaterialChunk CreateEmptyMaterialChunk(MaterialChunk source)
        {
            return new MaterialChunk
            {
                TextureSlot = source.TextureSlot,
                TextureName = source.TextureName,
                RenderFlags = source.RenderFlags,
                Geometry = new Chunk()
            };
        }

        private static int MapVertex(Chunk src, Chunk dst, Dictionary<int, int> remap, int srcIndex)
        {
            if (remap.TryGetValue(srcIndex, out int mapped))
                return mapped;

            Vector3 pos = src.Positions[srcIndex];
            Vector3 normal = srcIndex < src.Normals.Count ? src.Normals[srcIndex] : Vector3.UnitZ;
            Vector2 uv = srcIndex < src.UVs.Count ? src.UVs[srcIndex] : Vector2.Zero;
            Vector4 joints = srcIndex < src.JointIndices.Count ? src.JointIndices[srcIndex] : Vector4.Zero;
            Vector4 weights = srcIndex < src.JointWeights.Count ? src.JointWeights[srcIndex] : new Vector4(1, 0, 0, 0);

            mapped = dst.AddVertex(pos, normal, uv, joints, weights);
            remap[srcIndex] = mapped;
            return mapped;
        }

        private static float[] BuildLodDistances(List<Brush> lodBrushes)
        {
            int lodCount = lodBrushes.Count;
            if (lodCount <= 0)
                return Array.Empty<float>();

            float[]? configured = Config.LodDistances;
            int configuredCount = configured?.Length ?? 0;
            if (configuredCount > lodCount)
                Logger.Dev(logSrc, $"Configured LOD distances ({configuredCount}) exceed submesh LOD count ({lodCount}); extra values ignored.");

            // Priority per LOD: explicit -loddistances > distance carried on the source brush > the
            // built-in progression (0, 10, 100, 1000, ...).
            var distances = new float[lodCount];
            var sources = new string[lodCount];
            for (int i = 0; i < lodCount; i++)
            {
                if (i < configuredCount)
                {
                    distances[i] = configured![i];
                    sources[i] = "config";
                }
                else if (lodBrushes[i].LodDistance.HasValue)
                {
                    distances[i] = lodBrushes[i].LodDistance!.Value;
                    sources[i] = "source";
                }
                else if (i == 0)
                {
                    distances[i] = 0f;
                    sources[i] = "default";
                }
                else
                {
                    distances[i] = distances[i - 1] > 0f ? distances[i - 1] * 10f : 10f;
                    sources[i] = "default";
                }
            }

            Logger.Dev(logSrc, $"LOD distances: {string.Join(", ", distances.Select((d, i) => $"{d} ({sources[i]})"))}");
            return distances;
        }

        private static List<SubmeshExportGroup> BuildSubmeshGroups(List<Brush> brushes)
        {
            var groupsByKey = new Dictionary<string, SubmeshExportGroup>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<SubmeshExportGroup>();

            bool useSubmeshIndex = brushes.Any(b => b.SubmeshIndex.HasValue);
            foreach (Brush brush in brushes)
            {
                bool hasLodSuffix = TryExtractLodBrushInfo(brush.TextureName, out string baseName, out int lodIndex);
                string derivedName = hasLodSuffix
                    ? SanitizeSubmeshName(baseName, brush.UID)
                    : SanitizeSubmeshName(brush.TextureName, brush.UID);
                // Submesh names are truncated to 24 chars and can collide (LavaTester01_dbris has three
                // submeshes with the same truncated name), so prefer the source SUBM ordinal when present.
                string key = brush.SubmeshIndex.HasValue
                    ? $"sub:{brush.SubmeshIndex.Value}"
                    : useSubmeshIndex
                        ? $"uid:{brush.UID}"
                        : hasLodSuffix
                            ? $"lod:{derivedName}"
                            : $"uid:{brush.UID}";

                if (!groupsByKey.TryGetValue(key, out SubmeshExportGroup? group))
                {
                    group = new SubmeshExportGroup
                    {
                        Name = derivedName,
                        SubmeshIndex = brush.SubmeshIndex
                    };
                    groupsByKey[key] = group;
                    ordered.Add(group);
                }

                group.Candidates.Add(new LODCandidate
                {
                    Brush = brush,
                    LodIndex = Math.Max(0, lodIndex)
                });
            }

            foreach (SubmeshExportGroup group in ordered)
            {
                var lodByIndex = new SortedDictionary<int, LODCandidate>();
                foreach (LODCandidate candidate in group.Candidates.OrderBy(c => c.LodIndex).ThenBy(c => c.Brush.UID))
                {
                    if (!lodByIndex.TryGetValue(candidate.LodIndex, out LODCandidate? existing))
                    {
                        lodByIndex[candidate.LodIndex] = candidate;
                        continue;
                    }

                    int existingFaces = existing.Brush.Solid?.Faces?.Count ?? 0;
                    int candidateFaces = candidate.Brush.Solid?.Faces?.Count ?? 0;
                    if (candidateFaces > existingFaces)
                        lodByIndex[candidate.LodIndex] = candidate;
                }

                group.Lods = lodByIndex.Values.Select(v => v.Brush).ToList();
                if (group.Lods.Count == 0 && group.Candidates.Count > 0)
                    group.Lods.Add(group.Candidates[0].Brush);

                group.Materials = BuildSubmeshMaterialTable(group.Lods);
                group.MaterialProps = BuildSubmeshMaterialProps(group.Lods, group.Materials.Count);
            }

            return ordered
                .Where(g => g.Lods.Count > 0)
                .OrderBy(g => g.SubmeshIndex ?? int.MaxValue)
                .ToList();
        }

        private static List<string> BuildSubmeshMaterialTable(List<Brush> lodBrushes)
        {
            int maxSlot = -1;
            foreach (Brush brush in lodBrushes)
            {
                int slotCount = brush.Solid?.Textures?.Count ?? 0;
                maxSlot = Math.Max(maxSlot, slotCount - 1);
                if (brush.Solid?.Faces != null)
                {
                    foreach (Face face in brush.Solid.Faces)
                        maxSlot = Math.Max(maxSlot, face.TextureIndex);
                }
            }

            if (maxSlot < 0)
                maxSlot = 0;

            var materials = Enumerable.Repeat("default.tga", maxSlot + 1).ToList();
            foreach (Brush brush in lodBrushes)
            {
                if (brush.Solid?.Textures != null)
                {
                    for (int slot = 0; slot < brush.Solid.Textures.Count && slot < materials.Count; slot++)
                    {
                        string candidate = NormalizeTextureFilename(brush.Solid.Textures[slot]);
                        if (IsDefaultTexture(materials[slot]) || !IsDefaultTexture(candidate))
                            materials[slot] = candidate;
                    }
                }

                if (brush.Solid?.Faces == null)
                    continue;

                foreach (Face face in brush.Solid.Faces)
                {
                    int slot = face.TextureIndex;
                    if (slot < 0 || slot >= materials.Count)
                        continue;

                    if (IsDefaultTexture(materials[slot]))
                    {
                        string fallback = NormalizeTextureFilename(ResolveTextureBaseName(brush, slot));
                        materials[slot] = fallback;
                    }
                }
            }

            return materials;
        }

        // Per-slot V3M material properties (emissive/specular/... /flags), taken from the first LOD brush
        // that carries them. Slots without source data stay null and fall back to RED's defaults.
        private static List<V3mMaterialProps?> BuildSubmeshMaterialProps(List<Brush> lodBrushes, int slotCount)
        {
            var props = Enumerable.Repeat<V3mMaterialProps?>(null, Math.Max(0, slotCount)).ToList();
            foreach (Brush brush in lodBrushes)
            {
                List<V3mMaterialProps>? source = brush.Solid?.MaterialProps;
                if (source == null)
                    continue;
                for (int slot = 0; slot < props.Count && slot < source.Count; slot++)
                {
                    if (props[slot] == null && source[slot] != null)
                        props[slot] = source[slot];
                }
            }
            return props;
        }

        private static bool TryExtractLodBrushInfo(string? name, out string baseName, out int lodIndex)
        {
            baseName = string.Empty;
            lodIndex = 0;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            string value = Path.GetFileNameWithoutExtension(name.Trim());
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int marker = value.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;

            int digitsStart = marker + 4;
            if (digitsStart >= value.Length)
                return false;

            int digitsEnd = digitsStart;
            while (digitsEnd < value.Length && char.IsDigit(value[digitsEnd]))
                digitsEnd++;

            if (digitsEnd == digitsStart)
                return false;
            if (!int.TryParse(value[digitsStart..digitsEnd], out lodIndex))
                return false;

            baseName = value[..marker];
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = value;
            return true;
        }

        private static string SanitizeSubmeshName(string? name, int uid)
        {
            string value = string.IsNullOrWhiteSpace(name)
                ? $"Brush_{uid}"
                : Path.GetFileNameWithoutExtension(name.Trim());
            if (string.IsNullOrWhiteSpace(value))
                value = $"Brush_{uid}";
            return value;
        }

        private static string NormalizeTextureFilename(string? texture)
        {
            if (string.IsNullOrWhiteSpace(texture))
                return "default.tga";

            string value = texture.Replace('\\', '/');
            int query = value.IndexOf('?');
            int fragment = value.IndexOf('#');
            if (query < 0 || (fragment >= 0 && fragment < query))
                query = fragment;
            if (query >= 0)
                value = value[..query];

            string file = Path.GetFileName(value);
            if (string.IsNullOrWhiteSpace(file))
                file = value;
            if (string.IsNullOrWhiteSpace(file))
                file = "default";

            // Keep whatever extension the source carried: .vbm animated textures must not be
            // rewritten to .tga. Only supply .tga when there is no extension at all.
            if (string.IsNullOrWhiteSpace(Path.GetExtension(file)))
                file += ".tga";

            return file;
        }

        private static bool IsDefaultTexture(string? texture)
        {
            if (string.IsNullOrWhiteSpace(texture))
                return true;
            return string.Equals(
                Path.GetFileNameWithoutExtension(texture),
                "default",
                StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] QuantizeWeights(Vector4 w)
        {
            float w0 = MathF.Max(0f, w.X);
            float w1 = MathF.Max(0f, w.Y);
            float w2 = MathF.Max(0f, w.Z);
            float w3 = MathF.Max(0f, w.W);

            float sum = w0 + w1 + w2 + w3;
            if (sum <= 1e-6f)
                return new byte[] { 255, 0, 0, 0 };

            w0 /= sum;
            w1 /= sum;
            w2 /= sum;
            w3 /= sum;

            int b0 = (int)MathF.Round(w0 * 255f);
            int b1 = (int)MathF.Round(w1 * 255f);
            int b2 = (int)MathF.Round(w2 * 255f);
            int b3 = (int)MathF.Round(w3 * 255f);

            int total = b0 + b1 + b2 + b3;
            int delta = 255 - total;
            b0 = Math.Clamp(b0 + delta, 0, 255);

            return new byte[] { (byte)b0, (byte)b1, (byte)b2, (byte)b3 };
        }

        private static byte[] QuantizeJoints(Vector4 joints, byte[] packedWeights)
        {
            byte j0 = packedWeights[0] == 0 ? (byte)0xFF : FloatToJoint(joints.X);
            byte j1 = packedWeights[1] == 0 ? (byte)0xFF : FloatToJoint(joints.Y);
            byte j2 = packedWeights[2] == 0 ? (byte)0xFF : FloatToJoint(joints.Z);
            byte j3 = packedWeights[3] == 0 ? (byte)0xFF : FloatToJoint(joints.W);
            return new byte[] { j0, j1, j2, j3 };
        }

        private static byte FloatToJoint(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                return 0xFF;
            int rounded = (int)MathF.Round(v);
            if (rounded < 0 || rounded > 255)
                return 0xFF;
            return (byte)rounded;
        }

        private static List<MaterialChunk> GatherGeometry(Brush brush, bool includeSkin)
        {
            // When the source supplied per-vertex normals, honour them verbatim - including the
            // degenerate ones. Substituting a flat face normal there would split every shared vertex.
            bool hasAuthoredNormals = brush.Normals != null
                && brush.Normals.Count >= brush.Vertices.Count
                && brush.Normals.Any(n => float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z) && n.LengthSquared() > 1e-8f);

            var bySlot = new Dictionary<(int Slot, uint RenderFlags), MaterialChunk>();
            var ordered = new List<MaterialChunk>();
            foreach (var face in brush.Solid.Faces)
            {
                var idx = face.Vertices;
                if (idx.Count < 3)
                    continue;

                int tris = idx.Count > 3 ? idx.Count - 2 : 1;
                for (int i = 0; i < tris; i++)
                {
                    Vector3 p0 = Transform(brush, idx[0]);
                    Vector3 p1 = Transform(brush, idx[i + 1]);
                    Vector3 p2 = Transform(brush, idx[i + 2]);

                    Vector2 uv0 = idx[0] < brush.UVs.Count ? brush.UVs[idx[0]] : Vector2.Zero;
                    Vector2 uv1 = idx[i + 1] < brush.UVs.Count ? brush.UVs[idx[i + 1]] : Vector2.Zero;
                    Vector2 uv2 = idx[i + 2] < brush.UVs.Count ? brush.UVs[idx[i + 2]] : Vector2.Zero;

                    Vector4 ji0 = includeSkin ? GetJointIndices(brush, idx[0]) : Vector4.Zero;
                    Vector4 ji1 = includeSkin ? GetJointIndices(brush, idx[i + 1]) : Vector4.Zero;
                    Vector4 ji2 = includeSkin ? GetJointIndices(brush, idx[i + 2]) : Vector4.Zero;

                    Vector4 jw0 = includeSkin ? GetJointWeights(brush, idx[0]) : new Vector4(1, 0, 0, 0);
                    Vector4 jw1 = includeSkin ? GetJointWeights(brush, idx[i + 1]) : new Vector4(1, 0, 0, 0);
                    Vector4 jw2 = includeSkin ? GetJointWeights(brush, idx[i + 2]) : new Vector4(1, 0, 0, 0);

                    Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                    Vector3 n = cross.LengthSquared() > 1e-8f ? Vector3.Normalize(cross) : Vector3.UnitZ;
                    float d = -Vector3.Dot(n, p0);

                    // Prefer per-vertex normals (preserves smooth shading from the source); fall back to the flat face normal.
                    Vector3 n0 = ResolveVertexNormal(brush, idx[0], n, hasAuthoredNormals);
                    Vector3 n1 = ResolveVertexNormal(brush, idx[i + 1], n, hasAuthoredNormals);
                    Vector3 n2 = ResolveVertexNormal(brush, idx[i + 2], n, hasAuthoredNormals);

                    int textureSlot = face.TextureIndex >= 0 ? face.TextureIndex : 0;
                    string textureName = ResolveTextureBaseName(brush, textureSlot);

                    var chunkKey = (textureSlot, face.RenderFlags);
                    if (!bySlot.TryGetValue(chunkKey, out var entry))
                    {
                        entry = new MaterialChunk
                        {
                            TextureSlot = textureSlot,
                            TextureName = textureName,
                            RenderFlags = face.RenderFlags,
                            Geometry = new Chunk()
                        };
                        bySlot[chunkKey] = entry;
                        ordered.Add(entry);
                    }

                    Chunk chunk = entry.Geometry;

                    int v0 = chunk.AddVertex(p0, n0, uv0, ji0, jw0);
                    int v1 = chunk.AddVertex(p1, n1, uv1, ji1, jw1);
                    int v2 = chunk.AddVertex(p2, n2, uv2, ji2, jw2);

                    chunk.Triangles.Add((v0, v1, v2, face.FaceFlags));
                    chunk.Planes.Add((n, d));
                }
            }

            ordered = ordered
                .OrderBy(c => c.TextureSlot)
                .ThenBy(c => c.RenderFlags)
                .ToList();
            Logger.Dev(logSrc, $"Gathered geometry: {ordered.Count} materials, total vertices = {ordered.Sum(c => c.Geometry.Positions.Count)}");
            return ordered;
        }

        private static string ResolveTextureBaseName(Brush brush, int textureSlot)
        {
            if (brush.Solid?.Textures != null && textureSlot >= 0 && textureSlot < brush.Solid.Textures.Count)
            {
                string fromSlot = brush.Solid.Textures[textureSlot];
                if (!string.IsNullOrWhiteSpace(fromSlot))
                    return fromSlot;
            }

            if (!string.IsNullOrWhiteSpace(brush.TextureName))
            {
                string fromBrush = Path.GetFileNameWithoutExtension(brush.TextureName);
                if (!string.IsNullOrWhiteSpace(fromBrush))
                    return fromBrush;
            }

            return "default";
        }

        private static Vector4 GetJointIndices(Brush brush, int vertexIndex)
        {
            if (brush.JointIndices == null || vertexIndex < 0 || vertexIndex >= brush.JointIndices.Count)
                return Vector4.Zero;
            return brush.JointIndices[vertexIndex];
        }

        private static Vector4 GetJointWeights(Brush brush, int vertexIndex)
        {
            if (brush.JointWeights == null || vertexIndex < 0 || vertexIndex >= brush.JointWeights.Count)
                return new Vector4(1, 0, 0, 0);
            return brush.JointWeights[vertexIndex];
        }

        private static void WriteCollisionSphere(CollisionSphere sphere, BinaryWriter writer)
        {
            writer.Write(V3D_SECTION_CSPHERE);
            writer.Write(44);
            WriteFixedString(writer, sphere.Name ?? string.Empty, 24);
            writer.Write(sphere.ParentIndex);
            writer.Write(sphere.Position.X);
            writer.Write(sphere.Position.Y);
            writer.Write(sphere.Position.Z);
            writer.Write(sphere.Radius);
        }

        private static void WriteBones(List<Bone> bones, BinaryWriter writer)
        {
            // Each bone record is 24 (name) + 16 (quat) + 12 (translation) + 4 (parent) = 56 bytes.
            int sectionSize = 4 + (bones.Count * 56);
            writer.Write(V3D_SECTION_BONES);
            writer.Write(sectionSize);
            writer.Write(bones.Count);

            foreach (var bone in bones)
            {
                WriteFixedString(writer, bone.Name ?? string.Empty, 24);
                Quaternion q = Quaternion.Normalize(bone.BaseRotation);
                writer.Write(q.X);
                writer.Write(q.Y);
                writer.Write(q.Z);
                writer.Write(q.W);
                writer.Write(bone.BaseTranslation.X);
                writer.Write(bone.BaseTranslation.Y);
                writer.Write(bone.BaseTranslation.Z);
                writer.Write(bone.ParentIndex);
            }
        }

        private static Vector3 Transform(Brush b, int vi)
            => Vector3.Transform(b.Vertices[vi], b.RotationMatrix) + b.Position;

        // Returns a usable per-vertex normal in the same space as Transform()-ed positions, or the flat-face fallback.
        private static Vector3 ResolveVertexNormal(Brush b, int vi, Vector3 faceFallback, bool hasAuthoredNormals)
        {
            if (b.Normals == null || vi < 0 || vi >= b.Normals.Count)
                return faceFallback;
            Vector3 src = b.Normals[vi];
            if (!float.IsFinite(src.X) || !float.IsFinite(src.Y) || !float.IsFinite(src.Z))
                return hasAuthoredNormals ? Vector3.Zero : faceFallback;
            if (src.LengthSquared() < 1e-6f)
            {
                // A degenerate normal inside an otherwise authored set is data, not a gap to fill:
                // substituting the face normal here would split the vertex per face.
                return hasAuthoredNormals ? Vector3.Zero : faceFallback;
            }
            Vector3 rotated = Vector3.TransformNormal(src, b.RotationMatrix);
            if (rotated.LengthSquared() > 1e-8f)
                return Vector3.Normalize(rotated);
            return hasAuthoredNormals ? Vector3.Zero : faceFallback;
        }

        private static void WriteFixedString(BinaryWriter w, string s, int len)
        {
            byte[] bs = System.Text.Encoding.ASCII.GetBytes(s ?? string.Empty);
            int count = Math.Min(bs.Length, len - 1);
            w.Write(bs, 0, count);
            for (int i = count; i < len; i++)
                w.Write((byte)0);
        }

        private static void WriteZeroTerminatedString(BinaryWriter w, string s)
        {
            byte[] bs = System.Text.Encoding.ASCII.GetBytes(s ?? string.Empty);
            w.Write(bs);
            w.Write((byte)0);
        }

        private static void WriteVec3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        private class SubmeshExportGroup
        {
            public string Name { get; set; } = string.Empty;
            public int? SubmeshIndex { get; set; }
            public List<LODCandidate> Candidates { get; set; } = new();
            public List<Brush> Lods { get; set; } = new();
            public List<string> Materials { get; set; } = new();
            public List<V3mMaterialProps?> MaterialProps { get; set; } = new();
        }

        private class LODCandidate
        {
            public required Brush Brush { get; set; }
            public int LodIndex { get; set; }
        }

        private class Chunk
        {
            public readonly List<Vector3> Positions = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector2> UVs = new();
            public readonly List<Vector4> JointIndices = new();
            public readonly List<Vector4> JointWeights = new();
            public readonly List<(int, int, int, ushort)> Triangles = new();
            public readonly List<(Vector3, float)> Planes = new();

            private readonly Dictionary<(Vector3, Vector3, Vector2, Vector4, Vector4), int> map = new();

            public int AddVertex(Vector3 p, Vector3 n, Vector2 uv, Vector4 joints, Vector4 weights)
            {
                var key = (p, n, uv, joints, weights);
                if (map.TryGetValue(key, out int idx))
                    return idx;

                idx = Positions.Count;
                Positions.Add(p);
                Normals.Add(n);
                UVs.Add(uv);
                JointIndices.Add(joints);
                JointWeights.Add(weights);
                map[key] = idx;
                return idx;
            }
        }

        private class MaterialChunk
        {
            public int TextureSlot { get; set; }
            public string TextureName { get; set; } = "default";
            public uint RenderFlags { get; set; } = V3mRenderFlags.Default;
            public Chunk Geometry { get; set; } = new();
        }

        private class LodTextureRef
        {
            public int Slot { get; set; }
            public string Name { get; set; } = "default.tga";
        }
    }
}
