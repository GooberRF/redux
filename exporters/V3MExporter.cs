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
        private const uint V3D_LOD_CHARACTER = 0x02;
        private const uint V3D_LOD_TRIANGLE_PLANES = 0x20;
        // RF D3D vif render path uses dynamic buffers with practical per-batch limits
        // (~6000 vertices / ~10000 indices) and keeps extra headroom for clipping.
        // Keep chunk limits below both runtime limits and on-disk ushort allocation limits.
        private const int MaxChunkVertices = 5232; // min(65535/12, 6000-768)
        private const int MaxChunkFaces = 3077; // min(65535/8, (10000-768)/3)

        public static void ExportV3m(Mesh mesh, string outputPath)
            => ExportV3m(mesh, outputPath, forceCharacterMesh: false);

        public static void ExportV3m(Mesh mesh, string outputPath, bool forceCharacterMesh)
        {
            bool hasSkinData = mesh.Bones.Count > 0 || mesh.CollisionSpheres.Count > 0 || mesh.Brushes.Any(BrushHasJointData);
            bool writeCharacterMesh = forceCharacterMesh || hasSkinData;
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
            writer.Write(writeCharacterMesh ? mesh.CollisionSpheres.Count : 0);

            foreach (var group in submeshGroups)
                WriteSubmesh(group, writer, writeCharacterMesh);

            if (writeCharacterMesh)
            {
                foreach (var sphere in mesh.CollisionSpheres)
                    WriteCollisionSphere(sphere, writer);
                WriteBones(mesh.Bones, writer);
            }

            writer.Write(V3D_SECTION_END);
            writer.Write(0);
            Logger.Dev(logSrc, "ExportV3m complete");
        }

        private static bool BrushHasJointData(Brush brush)
        {
            if (brush.JointIndices == null || brush.JointWeights == null)
                return false;
            if (brush.JointIndices.Count == 0 || brush.JointWeights.Count == 0)
                return false;
            return true;
        }

        private static void WriteSubmesh(SubmeshExportGroup group, BinaryWriter writer, bool writeCharacterMesh)
        {
            if (group.Lods.Count == 0)
                return;

            Logger.Dev(logSrc, $"-- Submesh begin {group.Name}, lods={group.Lods.Count}");

            writer.Write(V3D_SECTION_SUBMESH);
            writer.Write(0);

            WriteFixedString(writer, group.Name, 24);
            WriteFixedString(writer, string.Empty, 24);
            writer.Write(7);

            int numLods = group.Lods.Count;
            writer.Write(numLods);
            float[] lodDistances = BuildLodDistances(numLods);
            for (int i = 0; i < lodDistances.Length; i++)
                writer.Write(lodDistances[i]);

            var lodMaterialChunks = new List<List<MaterialChunk>>(numLods);
            var allPts = new List<Vector3>();
            foreach (Brush lodBrush in group.Lods)
            {
                List<MaterialChunk> chunks = GatherGeometry(lodBrush, writeCharacterMesh);
                lodMaterialChunks.Add(chunks);
                foreach (MaterialChunk chunk in chunks)
                    allPts.AddRange(chunk.Geometry.Positions);
            }

            Vector3 aabbMin;
            Vector3 aabbMax;
            float radius;
            if (allPts.Count > 0)
            {
                aabbMin = new Vector3(float.MaxValue);
                aabbMax = new Vector3(float.MinValue);
                foreach (var p in allPts)
                {
                    aabbMin = Vector3.Min(aabbMin, p);
                    aabbMax = Vector3.Max(aabbMax, p);
                }
                radius = allPts.Max(p => p.Length());
            }
            else
            {
                aabbMin = Vector3.Zero;
                aabbMax = Vector3.Zero;
                radius = 0f;
            }

            WriteVec3(writer, Vector3.Zero);
            writer.Write(radius);
            WriteVec3(writer, aabbMin);
            WriteVec3(writer, aabbMax);

            for (int i = 0; i < numLods; i++)
                WriteLod(group.Lods[i], lodMaterialChunks[i], writer, writeCharacterMesh);

            writer.Write(group.Materials.Count);
            foreach (string tex in group.Materials)
            {
                WriteFixedString(writer, NormalizeTextureFilename(tex), 32);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                WriteFixedString(writer, string.Empty, 32);
                writer.Write((uint)1);
            }

            writer.Write(1);
            WriteFixedString(writer, group.Name, 24);
            writer.Write(0f);
        }

        private static void WriteLod(Brush brush, List<MaterialChunk> materialChunks, BinaryWriter writer, bool writeCharacterMesh)
        {
            materialChunks = ApplyChunkLimits(materialChunks);

            uint lodFlags = V3D_LOD_TRIANGLE_PLANES;
            if (writeCharacterMesh)
                lodFlags |= V3D_LOD_CHARACTER;
            writer.Write(lodFlags);

            int totalVerts = materialChunks.Sum(c => c.Geometry.Positions.Count);
            writer.Write(totalVerts);
            writer.Write((ushort)materialChunks.Count);

            using var ms = new MemoryStream();
            using var dw = new BinaryWriter(ms);
            static void Align(BinaryWriter w, int alignment)
            {
                long pad = (alignment - (w.BaseStream.Position % alignment)) % alignment;
                if (pad > 0)
                    w.Write(new byte[pad]);
            }

            int texIdx = 0;
            foreach (var _ in materialChunks)
            {
                dw.Write(new byte[0x20]);
                dw.Write(texIdx++);
                dw.Write(new byte[0x14]);
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

                foreach (var (n, d) in chunk.Planes)
                {
                    dw.Write(n.X);
                    dw.Write(n.Y);
                    dw.Write(n.Z);
                    dw.Write(d);
                }
                Align(dw, 0x10);

                dw.Write(new byte[chunk.Positions.Count * sizeof(short)]);
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
            }

            if (brush.PropPoints != null && brush.PropPoints.Count > 0)
            {
                foreach (var pp in brush.PropPoints)
                {
                    WriteFixedString(dw, pp.Name ?? string.Empty, 0x44);
                    Quaternion q = Quaternion.Normalize(pp.Orientation);
                    dw.Write(q.X);
                    dw.Write(q.Y);
                    dw.Write(q.Z);
                    dw.Write(q.W);
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
                writer.Write((ushort)(nv * 12));
                writer.Write((ushort)(nf * 8));
                writer.Write((ushort)(nv * 2));
                writer.Write((ushort)(writeCharacterMesh ? nv * 8 : 0));
                writer.Write((ushort)(nv * 8));
                writer.Write((uint)5344321);
            }

            writer.Write(brush.PropPoints?.Count ?? 0);

            writer.Write((uint)materialChunks.Count);
            foreach (var entry in materialChunks)
            {
                int slot = Math.Clamp(entry.TextureSlot, 0, byte.MaxValue);
                writer.Write((byte)slot);
                WriteZeroTerminatedString(writer, NormalizeTextureFilename(entry.TextureName));
            }
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

        private static float[] BuildLodDistances(int lodCount)
        {
            if (lodCount <= 0)
                return Array.Empty<float>();

            var distances = new float[lodCount];
            distances[0] = 0f;
            if (lodCount >= 2)
                distances[1] = 10f;
            for (int i = 2; i < lodCount; i++)
                distances[i] = distances[i - 1] * 10f;
            return distances;
        }

        private static List<SubmeshExportGroup> BuildSubmeshGroups(List<Brush> brushes)
        {
            var groupsByKey = new Dictionary<string, SubmeshExportGroup>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<SubmeshExportGroup>();

            foreach (Brush brush in brushes)
            {
                bool hasLodSuffix = TryExtractLodBrushInfo(brush.TextureName, out string baseName, out int lodIndex);
                string derivedName = hasLodSuffix
                    ? SanitizeSubmeshName(baseName, brush.UID)
                    : SanitizeSubmeshName(brush.TextureName, brush.UID);
                string key = hasLodSuffix
                    ? $"lod:{derivedName}"
                    : $"uid:{brush.UID}";

                if (!groupsByKey.TryGetValue(key, out SubmeshExportGroup? group))
                {
                    group = new SubmeshExportGroup
                    {
                        Name = derivedName
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
            }

            return ordered.Where(g => g.Lods.Count > 0).ToList();
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

            string ext = Path.GetExtension(file);
            if (string.IsNullOrWhiteSpace(ext))
                file += ".tga";
            else
                file = Path.GetFileNameWithoutExtension(file) + ".tga";

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
            var bySlot = new Dictionary<int, MaterialChunk>();
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

                    int textureSlot = face.TextureIndex >= 0 ? face.TextureIndex : 0;
                    string textureName = ResolveTextureBaseName(brush, textureSlot);

                    if (!bySlot.TryGetValue(textureSlot, out var entry))
                    {
                        entry = new MaterialChunk
                        {
                            TextureSlot = textureSlot,
                            TextureName = textureName,
                            Geometry = new Chunk()
                        };
                        bySlot[textureSlot] = entry;
                        ordered.Add(entry);
                    }

                    Chunk chunk = entry.Geometry;

                    int v0 = chunk.AddVertex(p0, n, uv0, ji0, jw0);
                    int v1 = chunk.AddVertex(p1, n, uv1, ji1, jw1);
                    int v2 = chunk.AddVertex(p2, n, uv2, ji2, jw2);

                    chunk.Triangles.Add((v0, v1, v2, face.FaceFlags));
                    chunk.Planes.Add((n, d));
                }
            }

            ordered = ordered
                .OrderBy(c => c.TextureSlot)
                .ToList();
            Logger.Dev(logSrc, $"Gathered geometry: {ordered.Count} materials, total vertices = {ordered.Sum(c => c.Geometry.Positions.Count)}");
            return ordered;
        }

        private static string ResolveTextureBaseName(Brush brush, int textureSlot)
        {
            if (brush.Solid?.Textures != null && textureSlot >= 0 && textureSlot < brush.Solid.Textures.Count)
            {
                string fromSlot = Path.GetFileNameWithoutExtension(brush.Solid.Textures[textureSlot]);
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
            int sectionSize = 4 + (bones.Count * 44);
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
            public List<LODCandidate> Candidates { get; set; } = new();
            public List<Brush> Lods { get; set; } = new();
            public List<string> Materials { get; set; } = new();
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
            public Chunk Geometry { get; set; } = new();
        }
    }
}
