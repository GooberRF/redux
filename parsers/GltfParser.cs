using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace redux.parsers
{
    public sealed class GltfImportResult
    {
        public required Mesh Mesh { get; init; }
        public RfaFile? Animation { get; init; }
    }

    public static class GltfParser
    {
        private const string logSrc = "GltfParser";

        public static GltfImportResult ReadGltf(string path)
        {
            string json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            GltfRoot root = JsonSerializer.Deserialize<GltfRoot>(json, opts) ?? throw new InvalidDataException("Invalid glTF JSON.");

            if (root.buffers.Count == 0)
                throw new InvalidDataException("glTF has no buffers.");

            string baseDir = Path.GetDirectoryName(path) ?? string.Empty;
            byte[] buffer = LoadBuffer(root.buffers[0].uri, baseDir);

            Dictionary<int, int> parentByNode = BuildParentMap(root.nodes);
            Matrix4x4[] nodeWorldMatrices = BuildWorldMatrices(root.nodes, parentByNode);
            List<int> meshNodeIndices = GetMeshNodeIndices(root.nodes);
            if (meshNodeIndices.Count == 0)
                throw new InvalidDataException("No mesh node found in glTF.");

            var mesh = new Mesh();
            int[] jointNodes = Array.Empty<int>();
            int[] gltfToRfJoint = Array.Empty<int>();
            var nodeToBone = new Dictionary<int, int>();
            int[] boneNodeIndices = Array.Empty<int>();

            int? primarySkinIndex = FindPrimarySkinIndex(root, meshNodeIndices);
            if (primarySkinIndex.HasValue && root.skins != null)
            {
                Skin skin = root.skins[primarySkinIndex.Value];
                jointNodes = skin.joints ?? Array.Empty<int>();
                gltfToRfJoint = BuildJointToBoneRemap(root, jointNodes);
                int[] rfToGltfJoint = BuildInverseJointRemap(gltfToRfJoint);
                boneNodeIndices = Enumerable.Repeat(-1, jointNodes.Length).ToArray();

                for (int gltfJoint = 0; gltfJoint < jointNodes.Length; gltfJoint++)
                {
                    int nodeIndex = jointNodes[gltfJoint];
                    if (nodeIndex < 0 || nodeIndex >= root.nodes.Count)
                        continue;
                    int boneIndex = gltfToRfJoint[gltfJoint];
                    if (boneIndex < 0 || boneIndex >= jointNodes.Length)
                        continue;
                    nodeToBone[nodeIndex] = boneIndex;
                    boneNodeIndices[boneIndex] = nodeIndex;
                }

                bool remappedJointOrder = !IsIdentityRemap(gltfToRfJoint);
                if (remappedJointOrder)
                    Logger.Info(logSrc, "Using rf_bone_index metadata/name tags to restore original joint order.");

                List<Matrix4x4>? inverseBindMatrices = null;
                if (skin.inverseBindMatrices.HasValue)
                {
                    inverseBindMatrices = ReadMat4(root, buffer, skin.inverseBindMatrices.Value);
                    if (inverseBindMatrices.Count != jointNodes.Length)
                    {
                        Logger.Warn(logSrc, $"Skin inverseBind count ({inverseBindMatrices.Count}) does not match joint count ({jointNodes.Length}); falling back to node-local inverse.");
                        inverseBindMatrices = null;
                    }
                }

                for (int boneIndex = 0; boneIndex < jointNodes.Length; boneIndex++)
                {
                    int gltfJoint = boneIndex < rfToGltfJoint.Length ? rfToGltfJoint[boneIndex] : boneIndex;
                    if (gltfJoint < 0 || gltfJoint >= jointNodes.Length)
                        gltfJoint = boneIndex;

                    int nodeIndex = jointNodes[gltfJoint];
                    Node node = nodeIndex >= 0 && nodeIndex < root.nodes.Count
                        ? root.nodes[nodeIndex]
                        : new Node { name = $"bone_{boneIndex}" };

                    Matrix4x4 invBind;
                    if (inverseBindMatrices != null)
                    {
                        invBind = inverseBindMatrices[gltfJoint];
                    }
                    else
                    {
                        Matrix4x4 local = BuildNodeMatrix(node);
                        if (!Matrix4x4.Invert(local, out invBind))
                            invBind = Matrix4x4.Identity;
                    }

                    if (!Matrix4x4.Decompose(invBind, out Vector3 _, out Quaternion qRh, out Vector3 tRh) || qRh.LengthSquared() < 1e-8f)
                        qRh = Quaternion.Identity;
                    else
                        qRh = Quaternion.Normalize(qRh);

                    Quaternion qRf = Quaternion.Normalize(new Quaternion(-qRh.X, qRh.Y, qRh.Z, qRh.W));
                    Vector3 tRf = new Vector3(-tRh.X, tRh.Y, tRh.Z);

                    int parentBone = -1;
                    if (parentByNode.TryGetValue(nodeIndex, out int parentNode) && nodeToBone.TryGetValue(parentNode, out int pb))
                        parentBone = pb;

                    mesh.Bones.Add(new Bone
                    {
                        Name = ResolveBoneName(node.name, boneIndex),
                        BaseRotation = qRf,
                        BaseTranslation = tRf,
                        ParentIndex = parentBone
                    });
                }
            }

            bool hasSkin = jointNodes.Length > 0;
            var brushByKey = new Dictionary<string, Brush>(StringComparer.Ordinal);
            var brushOrder = new List<string>();
            var materialSlotMapByBrushKey = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            var usedBrushUids = new HashSet<int>();
            int nextGeneratedUid = 0;
            var remapBySkinIndex = new Dictionary<int, int[]>();

            foreach (int meshNodeIndex in meshNodeIndices)
            {
                Node meshNode = root.nodes[meshNodeIndex];
                if (!meshNode.mesh.HasValue)
                    continue;

                int meshIndex = meshNode.mesh.Value;
                if (meshIndex < 0 || meshIndex >= root.meshes.Count)
                    continue;

                int[] meshNodeJointRemap = gltfToRfJoint;
                if (hasSkin && root.skins != null && meshNode.skin.HasValue)
                {
                    int skinIndex = meshNode.skin.Value;
                    if (skinIndex >= 0 && skinIndex < root.skins.Count)
                    {
                        if (!remapBySkinIndex.TryGetValue(skinIndex, out meshNodeJointRemap))
                        {
                            meshNodeJointRemap = BuildJointToBoneRemapForSkin(root, root.skins[skinIndex], nodeToBone, mesh.Bones.Count);
                            remapBySkinIndex[skinIndex] = meshNodeJointRemap;
                        }
                    }
                }

                int meshNodeFallbackBone = -1;
                if (hasSkin)
                    TryFindAncestorBoneIndex(meshNodeIndex, parentByNode, nodeToBone, out meshNodeFallbackBone);

                GltfMesh gltfMesh = root.meshes[meshIndex];
                for (int primIndex = 0; primIndex < gltfMesh.primitives.Count; primIndex++)
                {
                    Primitive prim = gltfMesh.primitives[primIndex];
                    if (!prim.attributes.TryGetValue("POSITION", out int posAccessor))
                        continue;

                    List<Vector3> posRh = ReadVec3(root, buffer, posAccessor);
                    if (posRh.Count == 0)
                        continue;

                    string brushKey = BuildBrushKeyForPrimitive(
                        meshNode,
                        meshNodeIndex,
                        prim,
                        primIndex,
                        out int? requestedUid,
                        out string? requestedName,
                        out int? requestedLodIndex);
                    if (!brushByKey.TryGetValue(brushKey, out Brush? brush))
                    {
                        int uid = AllocateBrushUid(requestedUid, usedBrushUids, ref nextGeneratedUid);
                        string brushName = string.IsNullOrWhiteSpace(requestedName) ? $"Brush_{uid}" : requestedName;
                        if (requestedLodIndex.HasValue && !TryExtractLodIndexFromName(brushName, out _))
                            brushName = $"{brushName}_LOD{requestedLodIndex.Value}";
                        brush = new Brush
                        {
                            UID = uid,
                            TextureName = brushName,
                            Solid = new Solid(),
                            Vertices = new List<Vector3>(),
                            UVs = new List<Vector2>(),
                            Indices = new List<int>()
                        };
                        List<string> slotList = ReadMaterialSlotsFromExtras(meshNode.extras);
                        if (slotList.Count > 0)
                            brush.Solid.Textures.AddRange(slotList);

                        if (hasSkin)
                        {
                            brush.JointIndices = new List<Vector4>();
                            brush.JointWeights = new List<Vector4>();
                        }
                        brushByKey[brushKey] = brush;
                        brushOrder.Add(brushKey);
                    }

                    if (string.IsNullOrWhiteSpace(brush.TextureName) && !string.IsNullOrWhiteSpace(requestedName))
                        brush.TextureName = requestedName;

                    int uvSet = ResolveUvSetIndex(root, prim.material);
                    string uvSemantic = $"TEXCOORD_{uvSet}";
                    List<Vector2> primUvs;
                    if (prim.attributes.TryGetValue(uvSemantic, out int uvAccessor))
                    {
                        primUvs = ReadVec2(root, buffer, uvAccessor);
                    }
                    else if (uvSet != 0 && prim.attributes.TryGetValue("TEXCOORD_0", out int uv0Accessor))
                    {
                        primUvs = ReadVec2(root, buffer, uv0Accessor);
                    }
                    else
                    {
                        primUvs = new List<Vector2>();
                    }
                    while (primUvs.Count < posRh.Count)
                        primUvs.Add(Vector2.Zero);

                    List<int> localIndices = prim.indices.HasValue
                        ? ReadIndices(root, buffer, prim.indices.Value)
                        : Enumerable.Range(0, posRh.Count).ToList();
                    FlipTriangleWinding(localIndices);

                    List<(List<Vector4> Joints, List<Vector4> Weights)> jointWeightSets = new();
                    if (hasSkin)
                    {
                        jointWeightSets = ReadJointWeightSets(root, buffer, prim);
                        if (jointWeightSets.Count == 0)
                        {
                            int bindBone = meshNodeFallbackBone >= 0 ? meshNodeFallbackBone : 0;
                            Logger.Warn(logSrc, $"Primitive {primIndex} on mesh node {meshNodeIndex} is skinned but has no JOINTS_n/WEIGHTS_n data; binding to bone {bindBone}.");
                        }
                    }

                    int vertexBase = brush.Vertices.Count;
                    for (int i = 0; i < posRh.Count; i++)
                    {
                        Vector3 p = posRh[i];
                        brush.Vertices.Add(new Vector3(-p.X, p.Y, p.Z));
                        brush.UVs.Add(i < primUvs.Count ? primUvs[i] : Vector2.Zero);

                        if (hasSkin)
                        {
                            BuildVertexSkinInfluence(
                                i,
                                jointWeightSets,
                                meshNodeJointRemap,
                                mesh.Bones.Count,
                                meshNodeFallbackBone,
                                out Vector4 ji,
                                out Vector4 jw);
                            brush.JointIndices.Add(ji);
                            brush.JointWeights.Add(jw);
                        }
                    }

                    string rfTexture = ResolveRfTextureName(root, prim.material);
                    int textureSlot;
                    if (TryResolveTextureSlotHint(root, prim, out int explicitSlot) && explicitSlot >= 0)
                        textureSlot = EnsureTextureSlot(brush.Solid.Textures, explicitSlot, rfTexture);
                    else if (prim.material.HasValue)
                        textureSlot = GetOrCreateTextureSlotForMaterial(brush, brushKey, prim.material.Value, rfTexture, materialSlotMapByBrushKey);
                    else
                        textureSlot = GetOrAddTextureSlot(brush.Solid.Textures, rfTexture);
                    if (string.IsNullOrWhiteSpace(brush.TextureName))
                        brush.TextureName = Path.GetFileNameWithoutExtension(rfTexture);

                    for (int i = 0; i + 2 < localIndices.Count; i += 3)
                    {
                        int i0 = localIndices[i];
                        int i1 = localIndices[i + 1];
                        int i2 = localIndices[i + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= posRh.Count || i1 >= posRh.Count || i2 >= posRh.Count)
                            continue;

                        int g0 = vertexBase + i0;
                        int g1 = vertexBase + i1;
                        int g2 = vertexBase + i2;

                        brush.Indices.Add(g0);
                        brush.Indices.Add(g1);
                        brush.Indices.Add(g2);

                        brush.Solid.Faces.Add(new Face
                        {
                            TextureIndex = textureSlot,
                            Vertices = new List<int> { g0, g1, g2 },
                            UVs = new List<Vector2> { brush.UVs[g0], brush.UVs[g1], brush.UVs[g2] },
                            FaceFlags = 0
                        });
                    }
                }
            }

            foreach (string key in brushOrder)
            {
                Brush brush = brushByKey[key];
                if (brush.Solid.Textures.Count == 0)
                    brush.Solid.Textures.Add("default.tga");
                if (string.IsNullOrWhiteSpace(brush.TextureName))
                    brush.TextureName = Path.GetFileNameWithoutExtension(brush.Solid.Textures[0]);
                mesh.Brushes.Add(brush);
            }

            var brushByUid = new Dictionary<int, Brush>();
            foreach (Brush brush in mesh.Brushes)
            {
                if (!brushByUid.ContainsKey(brush.UID))
                    brushByUid[brush.UID] = brush;
            }

            int importedPropPoints = 0;
            for (int i = 0; i < root.nodes.Count; i++)
            {
                Node node = root.nodes[i];
                if (!IsPropPoint(node))
                    continue;

                string name = GetPropPointName(node);
                int parentBone = ResolveParentBoneIndex(i, parentByNode, nodeToBone, node);

                Matrix4x4 propLocalMatrix = ResolveNodeLocalMatrixForParentBone(
                    i,
                    parentBone,
                    node,
                    boneNodeIndices,
                    nodeWorldMatrices);
                if (!Matrix4x4.Decompose(propLocalMatrix, out Vector3 _, out Quaternion rotRh, out Vector3 posRh) || rotRh.LengthSquared() < 1e-8f)
                    rotRh = Quaternion.Identity;
                else
                    rotRh = Quaternion.Normalize(rotRh);

                Quaternion rotRf = Quaternion.Normalize(new Quaternion(-rotRh.X, rotRh.Y, rotRh.Z, rotRh.W));
                Vector3 posRf = new Vector3(-posRh.X, posRh.Y, posRh.Z);

                var importedProp = new PropPoint
                {
                    Name = name,
                    Orientation = rotRf,
                    Position = posRf,
                    ParentIndex = parentBone
                };

                var targets = new List<Brush>();
                if (TryGetExtraInt(node, "rf_brush_uid", out int brushUid) && brushByUid.TryGetValue(brushUid, out Brush byExtras))
                {
                    targets.Add(byExtras);
                }
                else if (TryExtractBrushUidFromPropNodeName(node.name, out int encodedUid) && brushByUid.TryGetValue(encodedUid, out Brush byName))
                {
                    targets.Add(byName);
                }
                else if (TryFindAncestorMeshBrush(i, parentByNode, root.nodes, mesh.Brushes, out Brush byAncestor))
                {
                    targets.Add(byAncestor);
                }
                else
                {
                    // Blender may drop custom extras; use a broad fallback so props survive roundtrip.
                    targets.AddRange(mesh.Brushes);
                    Logger.Warn(logSrc, $"Prop point '{name}' missing owner metadata; attaching to all brushes.");
                }

                foreach (Brush targetBrush in targets)
                {
                    if (AddPropPointDeduplicated(targetBrush, importedProp))
                        importedPropPoints++;
                }
            }

            for (int i = 0; i < root.nodes.Count; i++)
            {
                Node node = root.nodes[i];
                if (!IsCollisionSphere(node))
                    continue;

                string name = GetCollisionSphereName(node);
                int parentBone = ResolveParentBoneIndex(i, parentByNode, nodeToBone, node);
                Matrix4x4 sphereLocalMatrix = ResolveNodeLocalMatrixForParentBone(
                    i,
                    parentBone,
                    node,
                    boneNodeIndices,
                    nodeWorldMatrices);
                Vector3 scaleRh;
                Quaternion sphereRotRh;
                Vector3 posRh;
                if (!Matrix4x4.Decompose(sphereLocalMatrix, out scaleRh, out sphereRotRh, out posRh))
                {
                    scaleRh = node.scale != null && node.scale.Length >= 3
                        ? new Vector3(node.scale[0], node.scale[1], node.scale[2])
                        : Vector3.One;
                    posRh = node.translation != null && node.translation.Length >= 3
                        ? new Vector3(node.translation[0], node.translation[1], node.translation[2])
                        : Vector3.Zero;
                }

                float radius;
                if (!TryGetExtraFloat(node, "rf_radius", out radius))
                {
                    radius = MathF.Max(MathF.Abs(scaleRh.X), MathF.Max(MathF.Abs(scaleRh.Y), MathF.Abs(scaleRh.Z)));
                }

                if (radius <= 0f)
                    radius = 0.01f;

                mesh.CollisionSpheres.Add(new CollisionSphere
                {
                    Name = name,
                    ParentIndex = parentBone,
                    Position = new Vector3(-posRh.X, posRh.Y, posRh.Z),
                    Radius = radius
                });
            }

            RfaFile? anim = BuildAnimation(root, buffer, jointNodes, nodeToBone);

            Logger.Info(logSrc, $"Read glTF: brushes={mesh.Brushes.Count}, bones={mesh.Bones.Count}, props={importedPropPoints}, cspheres={mesh.CollisionSpheres.Count}, hasAnim={anim != null}");
            return new GltfImportResult { Mesh = mesh, Animation = anim };
        }

        private static List<int> GetMeshNodeIndices(List<Node> nodes)
        {
            var result = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].mesh.HasValue)
                    result.Add(i);
            }
            return result;
        }

        private static int? FindPrimarySkinIndex(GltfRoot root, List<int> meshNodeIndices)
        {
            if (root.skins == null || root.skins.Count == 0)
                return null;

            foreach (int nodeIndex in meshNodeIndices)
            {
                int? skin = root.nodes[nodeIndex].skin;
                if (skin.HasValue && skin.Value >= 0 && skin.Value < root.skins.Count)
                    return skin.Value;
            }

            return null;
        }

        private static int[] BuildJointToBoneRemap(GltfRoot root, int[] jointNodes)
        {
            int count = jointNodes.Length;
            if (count == 0)
                return Array.Empty<int>();

            int[] remap = Enumerable.Repeat(-1, count).ToArray();
            var used = new bool[count];
            bool hasExplicit = false;

            for (int gltfJoint = 0; gltfJoint < count; gltfJoint++)
            {
                int nodeIndex = jointNodes[gltfJoint];
                if (nodeIndex < 0 || nodeIndex >= root.nodes.Count)
                    continue;

                Node node = root.nodes[nodeIndex];
                if (!TryGetBoneIndexHint(node, out int requestedBone))
                    continue;
                if (requestedBone < 0 || requestedBone >= count || used[requestedBone])
                    continue;

                remap[gltfJoint] = requestedBone;
                used[requestedBone] = true;
                hasExplicit = true;
            }

            if (!hasExplicit)
            {
                for (int i = 0; i < count; i++)
                    remap[i] = i;
                return remap;
            }

            int nextFree = 0;
            for (int gltfJoint = 0; gltfJoint < count; gltfJoint++)
            {
                if (remap[gltfJoint] >= 0)
                    continue;

                while (nextFree < count && used[nextFree])
                    nextFree++;

                int assigned = nextFree < count ? nextFree : gltfJoint;
                remap[gltfJoint] = assigned;
                if (assigned >= 0 && assigned < count && !used[assigned])
                    used[assigned] = true;
            }

            return remap;
        }

        private static int[] BuildJointToBoneRemapForSkin(GltfRoot root, Skin skin, Dictionary<int, int> nodeToBone, int boneCount)
        {
            int[] joints = skin.joints ?? Array.Empty<int>();
            int count = joints.Length;
            if (count == 0)
                return Array.Empty<int>();

            int[] remap = Enumerable.Repeat(-1, count).ToArray();
            for (int gltfJoint = 0; gltfJoint < count; gltfJoint++)
            {
                int nodeIndex = joints[gltfJoint];
                if (nodeToBone.TryGetValue(nodeIndex, out int mappedBone) && mappedBone >= 0 && mappedBone < boneCount)
                {
                    remap[gltfJoint] = mappedBone;
                    continue;
                }

                if (nodeIndex >= 0 && nodeIndex < root.nodes.Count &&
                    TryGetBoneIndexHint(root.nodes[nodeIndex], out int hintedBone) &&
                    hintedBone >= 0 && hintedBone < boneCount)
                {
                    remap[gltfJoint] = hintedBone;
                }
            }

            for (int gltfJoint = 0; gltfJoint < count; gltfJoint++)
            {
                if (remap[gltfJoint] >= 0)
                    continue;

                if (gltfJoint >= 0 && gltfJoint < boneCount)
                    remap[gltfJoint] = gltfJoint;
                else
                    remap[gltfJoint] = 0;
            }

            return remap;
        }

        private static int[] BuildInverseJointRemap(int[] gltfToRfJoint)
        {
            int count = gltfToRfJoint.Length;
            if (count == 0)
                return Array.Empty<int>();

            int[] rfToGltfJoint = Enumerable.Repeat(-1, count).ToArray();
            for (int gltfJoint = 0; gltfJoint < count; gltfJoint++)
            {
                int rfBone = gltfToRfJoint[gltfJoint];
                if (rfBone < 0 || rfBone >= count)
                    continue;
                if (rfToGltfJoint[rfBone] >= 0)
                    continue;
                rfToGltfJoint[rfBone] = gltfJoint;
            }

            for (int rfBone = 0; rfBone < count; rfBone++)
            {
                if (rfToGltfJoint[rfBone] < 0)
                    rfToGltfJoint[rfBone] = Math.Clamp(rfBone, 0, count - 1);
            }

            return rfToGltfJoint;
        }

        private static bool IsIdentityRemap(int[] remap)
        {
            for (int i = 0; i < remap.Length; i++)
            {
                if (remap[i] != i)
                    return false;
            }

            return true;
        }

        private static List<(List<Vector4> Joints, List<Vector4> Weights)> ReadJointWeightSets(GltfRoot root, byte[] buffer, Primitive prim)
        {
            int maxJointSet = GetMaxAttributeSetIndex(prim.attributes, "JOINTS_");
            int maxWeightSet = GetMaxAttributeSetIndex(prim.attributes, "WEIGHTS_");
            int maxSet = Math.Max(maxJointSet, maxWeightSet);

            var sets = new List<(List<Vector4>, List<Vector4>)>();
            if (maxSet < 0)
                return sets;

            for (int set = 0; set <= maxSet; set++)
            {
                string jointSemantic = $"JOINTS_{set}";
                string weightSemantic = $"WEIGHTS_{set}";
                bool hasJoints = prim.attributes.TryGetValue(jointSemantic, out int jointsAccessor);
                bool hasWeights = prim.attributes.TryGetValue(weightSemantic, out int weightsAccessor);
                if (!hasJoints && !hasWeights)
                    continue;

                if (!hasJoints || !hasWeights)
                {
                    Logger.Warn(logSrc, $"Ignoring partial skin set {set}: {jointSemantic} present={hasJoints}, {weightSemantic} present={hasWeights}.");
                    continue;
                }

                List<Vector4> joints = ReadVec4(root, buffer, jointsAccessor, integer: true);
                List<Vector4> weights = ReadVec4(root, buffer, weightsAccessor, integer: false);
                sets.Add((joints, weights));
            }

            return sets;
        }

        private static int GetMaxAttributeSetIndex(Dictionary<string, int> attributes, string prefix)
        {
            int max = -1;
            foreach (string key in attributes.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string suffix = key[prefix.Length..];
                if (!int.TryParse(suffix, out int setIndex))
                    continue;

                if (setIndex > max)
                    max = setIndex;
            }

            return max;
        }

        private static void BuildVertexSkinInfluence(
            int vertexIndex,
            List<(List<Vector4> Joints, List<Vector4> Weights)> jointWeightSets,
            int[] gltfToRfJoint,
            int boneCount,
            int fallbackBone,
            out Vector4 joints,
            out Vector4 weights)
        {
            joints = Vector4.Zero;
            weights = new Vector4(1f, 0f, 0f, 0f);

            if (boneCount <= 0)
                return;

            var accumulated = new Dictionary<int, float>();
            foreach ((List<Vector4> setJoints, List<Vector4> setWeights) in jointWeightSets)
            {
                if (vertexIndex < 0 || vertexIndex >= setJoints.Count || vertexIndex >= setWeights.Count)
                    continue;

                Vector4 rawJoints = setJoints[vertexIndex];
                Vector4 rawWeights = setWeights[vertexIndex];
                AccumulateInfluence(accumulated, rawJoints.X, rawWeights.X, gltfToRfJoint, boneCount);
                AccumulateInfluence(accumulated, rawJoints.Y, rawWeights.Y, gltfToRfJoint, boneCount);
                AccumulateInfluence(accumulated, rawJoints.Z, rawWeights.Z, gltfToRfJoint, boneCount);
                AccumulateInfluence(accumulated, rawJoints.W, rawWeights.W, gltfToRfJoint, boneCount);
            }

            var top = accumulated
                .Where(kv => kv.Value > 1e-8f)
                .OrderByDescending(kv => kv.Value)
                .Take(4)
                .ToList();

            if (top.Count == 0)
            {
                int defaultBone = fallbackBone >= 0 && fallbackBone < boneCount ? fallbackBone : 0;
                joints = new Vector4(defaultBone, 0f, 0f, 0f);
                weights = new Vector4(1f, 0f, 0f, 0f);
                return;
            }

            float total = top.Sum(kv => kv.Value);
            if (total <= 1e-6f)
            {
                int defaultBone = fallbackBone >= 0 && fallbackBone < boneCount ? fallbackBone : 0;
                joints = new Vector4(defaultBone, 0f, 0f, 0f);
                weights = new Vector4(1f, 0f, 0f, 0f);
                return;
            }

            float[] ji = new float[4];
            float[] jw = new float[4];
            for (int i = 0; i < top.Count; i++)
            {
                ji[i] = top[i].Key;
                jw[i] = top[i].Value / total;
            }

            joints = new Vector4(ji[0], ji[1], ji[2], ji[3]);
            weights = NormalizeWeights(new Vector4(jw[0], jw[1], jw[2], jw[3]));
        }

        private static void AccumulateInfluence(
            Dictionary<int, float> accumulated,
            float rawJoint,
            float rawWeight,
            int[] gltfToRfJoint,
            int boneCount)
        {
            if (float.IsNaN(rawWeight) || float.IsInfinity(rawWeight) || rawWeight <= 0f)
                return;

            if (float.IsNaN(rawJoint) || float.IsInfinity(rawJoint))
                return;

            int gltfJoint = (int)MathF.Round(rawJoint);
            int mappedJoint = RemapJointIndex(gltfJoint, gltfToRfJoint, boneCount);
            if (mappedJoint < 0)
                return;

            if (accumulated.TryGetValue(mappedJoint, out float existing))
                accumulated[mappedJoint] = existing + rawWeight;
            else
                accumulated[mappedJoint] = rawWeight;
        }

        private static int RemapJointIndex(int gltfJoint, int[] gltfToRfJoint, int boneCount)
        {
            if (gltfJoint < 0)
                return -1;

            int mapped = gltfJoint;
            if (gltfToRfJoint.Length > 0)
            {
                if (gltfJoint >= gltfToRfJoint.Length)
                    return -1;
                mapped = gltfToRfJoint[gltfJoint];
            }

            if (mapped < 0 || mapped >= boneCount)
                return -1;
            return mapped;
        }

        private static List<Vector4> RemapJointIndices(List<Vector4> joints, int[] gltfToRfJoint)
        {
            if (joints.Count == 0 || gltfToRfJoint.Length == 0 || IsIdentityRemap(gltfToRfJoint))
                return joints;

            var remapped = new List<Vector4>(joints.Count);
            foreach (Vector4 src in joints)
            {
                remapped.Add(new Vector4(
                    RemapJointIndexComponent(src.X, gltfToRfJoint),
                    RemapJointIndexComponent(src.Y, gltfToRfJoint),
                    RemapJointIndexComponent(src.Z, gltfToRfJoint),
                    RemapJointIndexComponent(src.W, gltfToRfJoint)));
            }

            return remapped;
        }

        private static float RemapJointIndexComponent(float value, int[] gltfToRfJoint)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;

            int gltfJoint = (int)MathF.Round(value);
            if (gltfJoint < 0 || gltfJoint >= gltfToRfJoint.Length)
                return 0f;

            int rfBone = gltfToRfJoint[gltfJoint];
            if (rfBone < 0 || rfBone >= gltfToRfJoint.Length)
                return 0f;

            return rfBone;
        }

        private static bool TryGetBoneIndexHint(Node node, out int boneIndex)
        {
            if (TryGetExtraInt(node, "rf_bone_index", out boneIndex))
                return true;
            return TryExtractBoneIndexFromName(node.name, out boneIndex);
        }

        private static string ResolveBoneName(string? nodeName, int fallbackIndex)
        {
            if (string.IsNullOrWhiteSpace(nodeName))
                return $"bone_{fallbackIndex}";
            if (TryExtractBoneIndexFromName(nodeName, out _, out string baseName))
                return string.IsNullOrWhiteSpace(baseName) ? $"bone_{fallbackIndex}" : baseName;
            return nodeName;
        }

        private static bool TryExtractBoneIndexFromName(string? nodeName, out int boneIndex)
            => TryExtractBoneIndexFromName(nodeName, out boneIndex, out _);

        private static bool TryExtractBoneIndexFromName(string? nodeName, out int boneIndex, out string baseName)
        {
            boneIndex = -1;
            baseName = string.IsNullOrWhiteSpace(nodeName) ? string.Empty : nodeName.Trim();
            if (string.IsNullOrWhiteSpace(baseName))
                return false;

            const string marker = "__rfbi";
            int markerIdx = baseName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx < 0)
                return false;

            int digitsStart = markerIdx + marker.Length;
            if (digitsStart >= baseName.Length)
                return false;

            string digits = baseName[digitsStart..];
            if (!int.TryParse(digits, out boneIndex))
                return false;

            baseName = baseName[..markerIdx];
            return true;
        }

        private static string BuildBrushKeyForPrimitive(
            Node meshNode,
            int meshNodeIndex,
            Primitive prim,
            int primitiveIndex,
            out int? requestedUid,
            out string? requestedName,
            out int? requestedLodIndex)
        {
            requestedUid = null;
            requestedName = null;
            requestedLodIndex = null;

            if (TryGetExtraInt(prim.extras, "rf_brush_uid", out int primUid))
                requestedUid = primUid;
            else if (TryGetExtraInt(meshNode.extras, "rf_brush_uid", out int nodeUid))
                requestedUid = nodeUid;

            if (TryGetExtraInt(prim.extras, "rf_lod_index", out int primLod) && primLod >= 0)
                requestedLodIndex = primLod;
            else if (TryGetExtraInt(meshNode.extras, "rf_lod_index", out int nodeLod) && nodeLod >= 0)
                requestedLodIndex = nodeLod;

            if (TryGetExtraString(prim.extras, "rf_brush_name", out string primName) && !string.IsNullOrWhiteSpace(primName))
                requestedName = primName;
            else if (TryGetExtraString(meshNode.extras, "rf_brush_name", out string nodeName) && !string.IsNullOrWhiteSpace(nodeName))
                requestedName = nodeName;
            else if (!string.IsNullOrWhiteSpace(meshNode.name))
                requestedName = meshNode.name;

            if (!requestedLodIndex.HasValue)
            {
                string? lodSourceName = !string.IsNullOrWhiteSpace(requestedName) ? requestedName : meshNode.name;
                if (TryExtractLodIndexFromName(lodSourceName, out int parsedLod))
                    requestedLodIndex = parsedLod;
            }

            if (requestedUid.HasValue)
                return requestedLodIndex.HasValue
                    ? $"uid:{requestedUid.Value}:lod:{requestedLodIndex.Value}"
                    : $"uid:{requestedUid.Value}";

            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                return requestedLodIndex.HasValue
                    ? $"node:{meshNodeIndex}:{requestedName}:lod:{requestedLodIndex.Value}"
                    : $"node:{meshNodeIndex}:{requestedName}";
            }

            return requestedLodIndex.HasValue
                ? $"node:{meshNodeIndex}:lod:{requestedLodIndex.Value}"
                : $"node:{meshNodeIndex}";
        }

        private static int AllocateBrushUid(int? preferredUid, HashSet<int> used, ref int nextGeneratedUid)
        {
            if (preferredUid.HasValue && preferredUid.Value >= 0 && !used.Contains(preferredUid.Value))
            {
                used.Add(preferredUid.Value);
                nextGeneratedUid = Math.Max(nextGeneratedUid, preferredUid.Value + 1);
                return preferredUid.Value;
            }

            while (used.Contains(nextGeneratedUid))
                nextGeneratedUid++;

            int uid = nextGeneratedUid;
            used.Add(uid);
            nextGeneratedUid++;
            return uid;
        }

        private static int GetOrAddTextureSlot(List<string> textures, string textureName)
        {
            textureName = NormalizeRfTextureName(textureName);
            for (int i = 0; i < textures.Count; i++)
            {
                if (string.Equals(textures[i], textureName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            textures.Add(textureName);
            return textures.Count - 1;
        }

        private static int EnsureTextureSlot(List<string> textures, int slot, string textureName)
        {
            textureName = NormalizeRfTextureName(textureName);
            while (textures.Count <= slot)
                textures.Add("default.tga");

            string current = NormalizeRfTextureName(textures[slot]);
            if (current.Equals("default.tga", StringComparison.OrdinalIgnoreCase))
            {
                textures[slot] = textureName;
            }
            else if (!textureName.Equals("default.tga", StringComparison.OrdinalIgnoreCase) &&
                     !current.Equals(textureName, StringComparison.OrdinalIgnoreCase))
            {
                // Primitive/material-level metadata is authoritative for this slot when present.
                textures[slot] = textureName;
            }

            return slot;
        }

        private static bool TryResolveTextureSlotHint(GltfRoot root, Primitive prim, out int textureSlot)
        {
            textureSlot = -1;

            if (TryGetExtraInt(prim.extras, "rf_texture_slot", out int primSlot) && primSlot >= 0)
            {
                textureSlot = primSlot;
                return true;
            }

            if (!prim.material.HasValue || root.materials == null)
                return false;

            int materialIndex = prim.material.Value;
            if (materialIndex < 0 || materialIndex >= root.materials.Count)
                return false;

            Material material = root.materials[materialIndex];
            if (TryGetExtraInt(material.extras, "rf_texture_slot", out int materialSlot) && materialSlot >= 0)
            {
                textureSlot = materialSlot;
                return true;
            }

            return false;
        }

        private static int GetOrCreateTextureSlotForMaterial(
            Brush brush,
            string brushKey,
            int materialIndex,
            string textureName,
            Dictionary<string, Dictionary<int, int>> materialSlotMapByBrushKey)
        {
            textureName = NormalizeRfTextureName(textureName);

            if (!materialSlotMapByBrushKey.TryGetValue(brushKey, out Dictionary<int, int>? byMaterial))
            {
                byMaterial = new Dictionary<int, int>();
                materialSlotMapByBrushKey[brushKey] = byMaterial;
            }

            if (byMaterial.TryGetValue(materialIndex, out int existingSlot))
                return existingSlot;

            var usedSlots = new HashSet<int>(byMaterial.Values);
            int slot = FindPreferredUnclaimedSlot(brush.Solid.Textures, usedSlots, textureName);
            if (slot < 0)
            {
                slot = brush.Solid.Textures.Count;
                brush.Solid.Textures.Add(textureName);
            }
            else
            {
                brush.Solid.Textures[slot] = textureName;
            }

            byMaterial[materialIndex] = slot;
            return slot;
        }

        private static int FindPreferredUnclaimedSlot(List<string> textures, HashSet<int> usedSlots, string textureName)
        {
            for (int i = 0; i < textures.Count; i++)
            {
                if (usedSlots.Contains(i))
                    continue;
                if (string.Equals(NormalizeRfTextureName(textures[i]), textureName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            for (int i = 0; i < textures.Count; i++)
            {
                if (usedSlots.Contains(i))
                    continue;
                string current = NormalizeRfTextureName(textures[i]);
                if (current.Equals("default.tga", StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static bool TryExtractLodIndexFromName(string? value, out int lodIndex)
        {
            lodIndex = -1;
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

            return int.TryParse(value[digitsStart..digitsEnd], out lodIndex);
        }

        private static List<string> ReadMaterialSlotsFromExtras(JsonElement? extras)
        {
            var result = new List<string>();
            if (!extras.HasValue || extras.Value.ValueKind != JsonValueKind.Object)
                return result;
            if (!extras.Value.TryGetProperty("rf_material_slots", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    result.Add("default.tga");
                    continue;
                }

                string tex = item.GetString() ?? string.Empty;
                result.Add(NormalizeRfTextureName(tex));
            }

            return result;
        }

        private static string ResolveRfTextureName(GltfRoot root, int? materialIndex)
        {
            if (!materialIndex.HasValue || root.materials == null)
                return "default.tga";

            int mi = materialIndex.Value;
            if (mi < 0 || mi >= root.materials.Count)
                return "default.tga";

            Material material = root.materials[mi];

            if (TryGetExtraString(material.extras, "rf_texture", out string rfTexture) && !string.IsNullOrWhiteSpace(rfTexture))
                return NormalizeRfTextureName(rfTexture);

            if (material.pbrMetallicRoughness?.baseColorTexture != null && root.textures != null)
            {
                int ti = material.pbrMetallicRoughness.baseColorTexture.index;
                if (ti >= 0 && ti < root.textures.Count)
                {
                    TextureDef tex = root.textures[ti];
                    if (root.images != null && tex.source >= 0 && tex.source < root.images.Count)
                    {
                        ImageDef img = root.images[tex.source];
                        if (!string.IsNullOrWhiteSpace(img.uri) && !img.uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            return NormalizeRfTextureName(img.uri);
                        if (!string.IsNullOrWhiteSpace(img.name))
                            return NormalizeRfTextureName(img.name);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(material.name))
            {
                if (TryExtractLegacyMaterialTextureName(material.name, out string legacyTexture))
                    return NormalizeRfTextureName(legacyTexture);
                return NormalizeRfTextureName(material.name);
            }

            return $"material_{mi}.tga";
        }

        private static int ResolveUvSetIndex(GltfRoot root, int? materialIndex)
        {
            if (!materialIndex.HasValue || root.materials == null)
                return 0;

            int mi = materialIndex.Value;
            if (mi < 0 || mi >= root.materials.Count)
                return 0;

            TextureInfo? baseColorTex = root.materials[mi].pbrMetallicRoughness?.baseColorTexture;
            if (baseColorTex == null)
                return 0;

            int uvSet = baseColorTex.texCoord ?? 0;
            return uvSet < 0 ? 0 : uvSet;
        }

        private static bool TryExtractLegacyMaterialTextureName(string materialName, out string textureName)
        {
            textureName = string.Empty;
            if (string.IsNullOrWhiteSpace(materialName))
                return false;

            string value = materialName.Trim();
            if (!value.StartsWith("rf_b", StringComparison.OrdinalIgnoreCase))
                return false;

            int index = 4;
            int uidStart = index;
            while (index < value.Length && char.IsDigit(value[index]))
                index++;

            if (index == uidStart || index >= value.Length || value[index] != '_')
                return false;

            index++;
            if (index >= value.Length || (value[index] != 'm' && value[index] != 'M'))
                return false;

            index++;
            int slotStart = index;
            while (index < value.Length && char.IsDigit(value[index]))
                index++;

            if (index == slotStart || index >= value.Length || value[index] != '_')
                return false;

            index++;
            if (index >= value.Length)
                return false;

            textureName = value[index..];
            return !string.IsNullOrWhiteSpace(textureName);
        }

        private static string NormalizeRfTextureName(string? texture)
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
            else if (!ext.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                file = Path.GetFileNameWithoutExtension(file) + ".tga";

            return file;
        }

        private static byte[] LoadBuffer(string? uri, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new InvalidDataException("Binary GLB buffers are not supported.");

            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0)
                    throw new InvalidDataException("Invalid buffer data URI.");
                return Convert.FromBase64String(uri[(comma + 1)..]);
            }

            return File.ReadAllBytes(Path.Combine(baseDir, uri));
        }

        private static Dictionary<int, int> BuildParentMap(List<Node> nodes)
        {
            var parents = new Dictionary<int, int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                int[]? children = nodes[i].children;
                if (children == null)
                    continue;
                foreach (int child in children)
                {
                    if (child >= 0 && child < nodes.Count)
                        parents[child] = i;
                }
            }
            return parents;
        }

        private static Matrix4x4[] BuildWorldMatrices(List<Node> nodes, Dictionary<int, int> parentByNode)
        {
            int count = nodes.Count;
            var world = new Matrix4x4[count];
            var state = new byte[count];

            Matrix4x4 Resolve(int idx)
            {
                if (idx < 0 || idx >= count)
                    return Matrix4x4.Identity;
                if (state[idx] == 2)
                    return world[idx];
                if (state[idx] == 1)
                {
                    Matrix4x4 fallback = BuildNodeMatrix(nodes[idx]);
                    world[idx] = fallback;
                    state[idx] = 2;
                    return fallback;
                }

                state[idx] = 1;
                Matrix4x4 local = BuildNodeMatrix(nodes[idx]);
                if (parentByNode.TryGetValue(idx, out int parentIdx) && parentIdx >= 0 && parentIdx < count)
                {
                    Matrix4x4 parentWorld = Resolve(parentIdx);
                    world[idx] = local * parentWorld;
                }
                else
                {
                    world[idx] = local;
                }

                state[idx] = 2;
                return world[idx];
            }

            for (int i = 0; i < count; i++)
                Resolve(i);

            return world;
        }

        private static int ResolveParentBoneIndex(int nodeIndex, Dictionary<int, int> parentByNode, Dictionary<int, int> nodeToBone, Node node)
        {
            if (TryFindAncestorBoneIndex(nodeIndex, parentByNode, nodeToBone, out int fromHierarchy))
                return fromHierarchy;
            if (TryGetExtraInt(node, "rf_parent_bone", out int fromExtras))
                return fromExtras;
            return -1;
        }

        private static bool TryFindAncestorBoneIndex(int nodeIndex, Dictionary<int, int> parentByNode, Dictionary<int, int> nodeToBone, out int boneIndex)
        {
            boneIndex = -1;
            int current = nodeIndex;
            var visited = new HashSet<int>();

            while (parentByNode.TryGetValue(current, out int parent))
            {
                if (!visited.Add(parent))
                    break;

                if (nodeToBone.TryGetValue(parent, out boneIndex))
                    return true;

                current = parent;
            }

            return false;
        }

        private static Matrix4x4 ResolveNodeLocalMatrixForParentBone(
            int nodeIndex,
            int parentBone,
            Node node,
            int[] boneNodeIndices,
            Matrix4x4[] worldByNode)
        {
            Matrix4x4 local = BuildNodeMatrix(node);
            if (parentBone < 0 || parentBone >= boneNodeIndices.Length)
                return local;

            int boneNodeIndex = boneNodeIndices[parentBone];
            if (boneNodeIndex < 0 || boneNodeIndex >= worldByNode.Length)
                return local;

            Matrix4x4 nodeWorld = nodeIndex >= 0 && nodeIndex < worldByNode.Length
                ? worldByNode[nodeIndex]
                : local;
            Matrix4x4 boneWorld = worldByNode[boneNodeIndex];
            if (!Matrix4x4.Invert(boneWorld, out Matrix4x4 invBoneWorld))
                return local;

            return nodeWorld * invBoneWorld;
        }

        private static Matrix4x4 BuildNodeMatrix(Node n)
        {
            if (n.matrix != null && n.matrix.Length == 16)
            {
                float[] m = n.matrix;
                return new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]);
            }

            Vector3 t = n.translation != null && n.translation.Length >= 3 ? new Vector3(n.translation[0], n.translation[1], n.translation[2]) : Vector3.Zero;
            Quaternion r = n.rotation != null && n.rotation.Length >= 4
                ? Quaternion.Normalize(new Quaternion(n.rotation[0], n.rotation[1], n.rotation[2], n.rotation[3]))
                : Quaternion.Identity;
            Vector3 s = n.scale != null && n.scale.Length >= 3 ? new Vector3(n.scale[0], n.scale[1], n.scale[2]) : Vector3.One;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        private static bool IsCollisionSphere(Node node)
        {
            if (!string.IsNullOrWhiteSpace(node.name) && node.name.StartsWith("rf_csphere::", StringComparison.OrdinalIgnoreCase))
                return true;
            if (node.extras.HasValue && node.extras.Value.ValueKind == JsonValueKind.Object &&
                node.extras.Value.TryGetProperty("rf_type", out JsonElement t) &&
                t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), "collision_sphere", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool IsPropPoint(Node node)
        {
            if (!string.IsNullOrWhiteSpace(node.name) && node.name.StartsWith("rf_prop::", StringComparison.OrdinalIgnoreCase))
                return true;
            if (node.extras.HasValue && node.extras.Value.ValueKind == JsonValueKind.Object &&
                node.extras.Value.TryGetProperty("rf_type", out JsonElement t) &&
                t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), "prop_point", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static string GetCollisionSphereName(Node node)
        {
            if (TryGetExtraString(node, "rf_name", out string name) && !string.IsNullOrWhiteSpace(name))
                return name;
            if (!string.IsNullOrWhiteSpace(node.name) && node.name.StartsWith("rf_csphere::", StringComparison.OrdinalIgnoreCase))
                return node.name.Substring("rf_csphere::".Length);
            return "csphere";
        }

        private static string GetPropPointName(Node node)
        {
            if (TryGetExtraString(node, "rf_name", out string name) && !string.IsNullOrWhiteSpace(name))
                return name;
            if (!string.IsNullOrWhiteSpace(node.name) && node.name.StartsWith("rf_prop::", StringComparison.OrdinalIgnoreCase))
            {
                string payload = node.name.Substring("rf_prop::".Length);
                if (payload.StartsWith("uid", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = 3;
                    while (idx < payload.Length && char.IsDigit(payload[idx]))
                        idx++;

                    if (idx > 3 && idx + 1 < payload.Length && payload[idx] == ':' && payload[idx + 1] == ':')
                    {
                        string decoded = payload[(idx + 2)..];
                        if (!string.IsNullOrWhiteSpace(decoded))
                            return decoded;
                    }
                }

                return payload;
            }
            return "prop";
        }

        private static bool TryExtractBrushUidFromPropNodeName(string? nodeName, out int brushUid)
        {
            brushUid = -1;
            if (string.IsNullOrWhiteSpace(nodeName))
                return false;

            const string prefix = "rf_prop::";
            if (!nodeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string payload = nodeName.Substring(prefix.Length);
            if (!payload.StartsWith("uid", StringComparison.OrdinalIgnoreCase))
                return false;

            int idx = 3;
            while (idx < payload.Length && char.IsDigit(payload[idx]))
                idx++;

            if (idx == 3)
                return false;

            string digits = payload.Substring(3, idx - 3);
            return int.TryParse(digits, out brushUid) && brushUid >= 0;
        }

        private static bool TryFindAncestorMeshBrush(
            int nodeIndex,
            Dictionary<int, int> parentByNode,
            List<Node> nodes,
            List<Brush> brushes,
            out Brush brush)
        {
            brush = null!;
            int current = nodeIndex;
            var visited = new HashSet<int>();

            while (parentByNode.TryGetValue(current, out int parent))
            {
                if (!visited.Add(parent))
                    break;
                if (parent < 0 || parent >= nodes.Count)
                    break;

                Node parentNode = nodes[parent];
                if (TryGetExtraInt(parentNode, "rf_brush_uid", out int uid))
                {
                    foreach (Brush candidate in brushes)
                    {
                        if (candidate.UID == uid)
                        {
                            brush = candidate;
                            return true;
                        }
                    }
                }

                if (TryGetExtraString(parentNode, "rf_brush_name", out string brushName) &&
                    TryFindBrushByName(brushes, brushName, out brush))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(parentNode.name) &&
                    TryFindBrushByName(brushes, parentNode.name, out brush))
                {
                    return true;
                }

                current = parent;
            }

            return false;
        }

        private static bool TryFindBrushByName(List<Brush> brushes, string? nodeName, out Brush brush)
        {
            brush = null!;
            if (string.IsNullOrWhiteSpace(nodeName))
                return false;

            foreach (Brush candidate in brushes)
            {
                if (string.Equals(candidate.TextureName, nodeName, StringComparison.OrdinalIgnoreCase))
                {
                    brush = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool AddPropPointDeduplicated(Brush brush, PropPoint prop)
        {
            for (int i = 0; i < brush.PropPoints.Count; i++)
            {
                PropPoint existing = brush.PropPoints[i];
                if (!string.Equals(existing.Name, prop.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (existing.ParentIndex != prop.ParentIndex)
                    continue;
                if (!NearlyEqual(existing.Position, prop.Position))
                    continue;
                if (!NearlyEqual(existing.Orientation, prop.Orientation))
                    continue;
                return false;
            }

            brush.PropPoints.Add(prop);
            return true;
        }

        private static bool NearlyEqual(Vector3 a, Vector3 b, float epsilon = 0.0001f)
        {
            Vector3 d = a - b;
            return d.LengthSquared() <= epsilon * epsilon;
        }

        private static bool NearlyEqual(Quaternion a, Quaternion b, float epsilon = 0.0001f)
        {
            Quaternion na = a.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(a);
            Quaternion nb = b.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(b);
            float dot = MathF.Abs(Quaternion.Dot(na, nb));
            return (1f - dot) <= epsilon;
        }

        private static bool TryGetExtraString(JsonElement? extras, string key, out string value)
        {
            value = string.Empty;
            if (!extras.HasValue || extras.Value.ValueKind != JsonValueKind.Object)
                return false;
            if (!extras.Value.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.String)
                return false;
            value = e.GetString() ?? string.Empty;
            return true;
        }

        private static bool TryGetExtraInt(JsonElement? extras, string key, out int value)
        {
            value = 0;
            if (!extras.HasValue || extras.Value.ValueKind != JsonValueKind.Object)
                return false;
            if (!extras.Value.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Number)
                return false;
            return e.TryGetInt32(out value);
        }

        private static bool TryGetExtraFloat(JsonElement? extras, string key, out float value)
        {
            value = 0;
            if (!extras.HasValue || extras.Value.ValueKind != JsonValueKind.Object)
                return false;
            if (!extras.Value.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Number)
                return false;
            return e.TryGetSingle(out value);
        }

        private static bool TryGetExtraString(Node node, string key, out string value)
        {
            return TryGetExtraString(node.extras, key, out value);
        }

        private static bool TryGetExtraInt(Node node, string key, out int value)
        {
            return TryGetExtraInt(node.extras, key, out value);
        }

        private static bool TryGetExtraFloat(Node node, string key, out float value)
        {
            return TryGetExtraFloat(node.extras, key, out value);
        }

        private static List<Vector3> ReadVec3(GltfRoot root, byte[] buffer, int accessor)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<Vector3>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                result.Add(new Vector3(
                    ReadFloat(buffer, o + a.ComponentSize * 0, a.ComponentType, a.Normalized),
                    ReadFloat(buffer, o + a.ComponentSize * 1, a.ComponentType, a.Normalized),
                    ReadFloat(buffer, o + a.ComponentSize * 2, a.ComponentType, a.Normalized)));
            }
            return result;
        }

        private static List<Vector2> ReadVec2(GltfRoot root, byte[] buffer, int accessor)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<Vector2>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                result.Add(new Vector2(
                    ReadFloat(buffer, o + a.ComponentSize * 0, a.ComponentType, a.Normalized),
                    ReadFloat(buffer, o + a.ComponentSize * 1, a.ComponentType, a.Normalized)));
            }
            return result;
        }

        private static List<Vector4> ReadVec4(GltfRoot root, byte[] buffer, int accessor, bool integer)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<Vector4>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                if (integer)
                {
                    result.Add(new Vector4(
                        ReadInt(buffer, o + a.ComponentSize * 0, a.ComponentType),
                        ReadInt(buffer, o + a.ComponentSize * 1, a.ComponentType),
                        ReadInt(buffer, o + a.ComponentSize * 2, a.ComponentType),
                        ReadInt(buffer, o + a.ComponentSize * 3, a.ComponentType)));
                }
                else
                {
                    result.Add(new Vector4(
                        ReadFloat(buffer, o + a.ComponentSize * 0, a.ComponentType, a.Normalized),
                        ReadFloat(buffer, o + a.ComponentSize * 1, a.ComponentType, a.Normalized),
                        ReadFloat(buffer, o + a.ComponentSize * 2, a.ComponentType, a.Normalized),
                        ReadFloat(buffer, o + a.ComponentSize * 3, a.ComponentType, a.Normalized)));
                }
            }
            return result;
        }

        private static List<Matrix4x4> ReadMat4(GltfRoot root, byte[] buffer, int accessor)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<Matrix4x4>(a.Count);

            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                float[] m = new float[16];
                for (int j = 0; j < 16; j++)
                    m[j] = ReadFloat(buffer, o + j * sizeof(float), a.ComponentType, a.Normalized);

                // glTF matrix data is column-major. This row-wise assignment yields a row-vector matrix
                // equivalent to the transposed column-vector matrix, which System.Numerics expects.
                result.Add(new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]));
            }

            return result;
        }

        private static List<float> ReadScalars(GltfRoot root, byte[] buffer, int accessor)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<float>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                result.Add(ReadFloat(buffer, o, a.ComponentType, a.Normalized));
            }
            return result;
        }

        private static List<int> ReadIndices(GltfRoot root, byte[] buffer, int accessor)
        {
            AccessorInfo a = GetAccessor(root, accessor);
            var result = new List<int>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int o = a.Offset + i * a.Stride;
                result.Add(ReadInt(buffer, o, a.ComponentType));
            }
            return result;
        }

        private static void FlipTriangleWinding(List<int> indices)
        {
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        private static AccessorInfo GetAccessor(GltfRoot root, int accessorIndex)
        {
            Accessor a = root.accessors[accessorIndex];
            if (!a.bufferView.HasValue)
                throw new InvalidDataException("Sparse accessors are unsupported.");

            BufferView v = root.bufferViews[a.bufferView.Value];
            int compSize = ComponentSize(a.componentType);
            int compCount = ComponentCount(a.type);
            int stride = v.byteStride ?? (compSize * compCount);
            int offset = (v.byteOffset ?? 0) + (a.byteOffset ?? 0);

            return new AccessorInfo
            {
                Offset = offset,
                Stride = stride,
                Count = a.count,
                ComponentType = a.componentType,
                ComponentSize = compSize,
                Normalized = a.normalized ?? false
            };
        }

        private static int ComponentCount(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new InvalidDataException($"Unsupported accessor type {type}")
        };

        private static int ComponentSize(int type) => type switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new InvalidDataException($"Unsupported component type {type}")
        };

        private static int ReadInt(byte[] b, int o, int type) => type switch
        {
            5120 => (sbyte)b[o],
            5121 => b[o],
            5122 => BitConverter.ToInt16(b, o),
            5123 => BitConverter.ToUInt16(b, o),
            5125 => (int)BitConverter.ToUInt32(b, o),
            5126 => (int)BitConverter.ToSingle(b, o),
            _ => throw new InvalidDataException($"Unsupported integer component type {type}")
        };

        private static float ReadFloat(byte[] b, int o, int type, bool normalized) => type switch
        {
            5120 => normalized ? MathF.Max((sbyte)b[o] / 127f, -1f) : (sbyte)b[o],
            5121 => normalized ? b[o] / 255f : b[o],
            5122 => normalized ? MathF.Max(BitConverter.ToInt16(b, o) / 32767f, -1f) : BitConverter.ToInt16(b, o),
            5123 => normalized ? BitConverter.ToUInt16(b, o) / 65535f : BitConverter.ToUInt16(b, o),
            5125 => normalized ? BitConverter.ToUInt32(b, o) / 4294967295f : BitConverter.ToUInt32(b, o),
            5126 => BitConverter.ToSingle(b, o),
            _ => throw new InvalidDataException($"Unsupported float component type {type}")
        };

        private static Vector4 NormalizeWeights(Vector4 w)
        {
            float w0 = MathF.Max(0, w.X);
            float w1 = MathF.Max(0, w.Y);
            float w2 = MathF.Max(0, w.Z);
            float w3 = MathF.Max(0, w.W);
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 1e-6f)
                return new Vector4(1, 0, 0, 0);
            return new Vector4(w0 / sum, w1 / sum, w2 / sum, w3 / sum);
        }

        private static RfaFile? BuildAnimation(GltfRoot root, byte[] buffer, int[] jointNodes, Dictionary<int, int> nodeToBone)
        {
            if (jointNodes.Length == 0 || root.animations == null || root.animations.Count == 0)
                return null;

            Animation anim = root.animations[0];
            if (anim.samplers == null || anim.channels == null)
                return null;

            var bones = new List<RfaBone>(jointNodes.Length);
            for (int i = 0; i < jointNodes.Length; i++)
            {
                bones.Add(new RfaBone
                {
                    Weight = 10f,
                    RotationKeys = new List<RfaRotationKey>(),
                    TranslationKeys = new List<RfaTranslationKey>()
                });
            }

            foreach (AnimationChannel ch in anim.channels)
            {
                if (ch.target == null)
                    continue;
                if (ch.sampler < 0 || ch.sampler >= anim.samplers.Count)
                    continue;

                if (!nodeToBone.TryGetValue(ch.target.node, out int boneIndex))
                    continue;

                AnimationSampler s = anim.samplers[ch.sampler];
                List<float> times = ReadScalars(root, buffer, s.input);
                string interpolation = s.interpolation ?? "LINEAR";
                string path = ch.target.path ?? string.Empty;

                if (path.Equals("rotation", StringComparison.OrdinalIgnoreCase))
                {
                    List<Vector4> values = ReadVec4(root, buffer, s.output, integer: false);
                    for (int i = 0; i < times.Count; i++)
                    {
                        int vi = interpolation.Equals("CUBICSPLINE", StringComparison.OrdinalIgnoreCase) ? (i * 3 + 1) : i;
                        if (vi >= values.Count)
                            continue;

                        Vector4 v = values[vi];
                        Quaternion qRh = Quaternion.Normalize(new Quaternion(v.X, v.Y, v.Z, v.W));
                        Quaternion qRf = Quaternion.Normalize(new Quaternion(-qRh.X, qRh.Y, qRh.Z, qRh.W));

                        bones[boneIndex].RotationKeys.Add(new RfaRotationKey
                        {
                            Time = (int)MathF.Round(times[i] * 4800f),
                            Rotation = qRf,
                            EaseIn = 0,
                            EaseOut = 0
                        });
                    }
                }
                else if (path.Equals("translation", StringComparison.OrdinalIgnoreCase))
                {
                    List<Vector3> values = ReadVec3(root, buffer, s.output);
                    bool cubicSpline = interpolation.Equals("CUBICSPLINE", StringComparison.OrdinalIgnoreCase);
                    bool stepInterpolation = interpolation.Equals("STEP", StringComparison.OrdinalIgnoreCase);

                    Vector3 ReadTranslationValueRf(int keyIndex)
                    {
                        int valueIndex = cubicSpline ? (keyIndex * 3 + 1) : keyIndex;
                        if (valueIndex < 0 || valueIndex >= values.Count)
                            return Vector3.Zero;

                        Vector3 tr = values[valueIndex];
                        return new Vector3(-tr.X, tr.Y, tr.Z);
                    }

                    for (int i = 0; i < times.Count; i++)
                    {
                        int vi = cubicSpline ? (i * 3 + 1) : i;
                        if (vi >= values.Count)
                            continue;

                        Vector3 trRf = ReadTranslationValueRf(i);
                        Vector3 inTan = trRf;
                        Vector3 outTan = trRf;

                        if (times.Count > 1)
                        {
                            if (cubicSpline)
                            {
                                // glTF CUBICSPLINE stores derivatives; RF stores absolute Bezier handles.
                                // Convert derivatives to handles using dt/3 for adjacent spans.
                                float prevDt = i > 0 ? MathF.Max(0f, times[i] - times[i - 1]) : 0f;
                                float nextDt = i + 1 < times.Count ? MathF.Max(0f, times[i + 1] - times[i]) : 0f;

                                int inDerivIndex = i * 3;
                                if (inDerivIndex >= 0 && inDerivIndex < values.Count && prevDt > 1e-6f)
                                {
                                    Vector3 derivRh = values[inDerivIndex];
                                    Vector3 derivRf = new(-derivRh.X, derivRh.Y, derivRh.Z);
                                    inTan = trRf - (derivRf * (prevDt / 3f));
                                }

                                int outDerivIndex = i * 3 + 2;
                                if (outDerivIndex >= 0 && outDerivIndex < values.Count && nextDt > 1e-6f)
                                {
                                    Vector3 derivRh = values[outDerivIndex];
                                    Vector3 derivRf = new(-derivRh.X, derivRh.Y, derivRh.Z);
                                    outTan = trRf + (derivRf * (nextDt / 3f));
                                }
                            }
                            else if (!stepInterpolation)
                            {
                                // For LINEAR tracks, place handles on segment thirds so RF reproduces linear spans
                                // without per-key zero-velocity flattening.
                                if (i > 0)
                                {
                                    Vector3 prevRf = ReadTranslationValueRf(i - 1);
                                    inTan = trRf - ((trRf - prevRf) / 3f);
                                }

                                if (i + 1 < times.Count)
                                {
                                    Vector3 nextRf = ReadTranslationValueRf(i + 1);
                                    outTan = trRf + ((nextRf - trRf) / 3f);
                                }
                            }
                        }

                        bones[boneIndex].TranslationKeys.Add(new RfaTranslationKey
                        {
                            Time = (int)MathF.Round(times[i] * 4800f),
                            Translation = trRf,
                            InTangent = inTan,
                            OutTangent = outTan
                        });
                    }
                }
            }

            int minTime = int.MaxValue;
            int maxTime = int.MinValue;

            foreach (var bone in bones)
            {
                if (bone.RotationKeys.Count > 0)
                {
                    bone.RotationKeys = bone.RotationKeys.OrderBy(k => k.Time).GroupBy(k => k.Time).Select(g => g.Last()).ToList();
                    minTime = Math.Min(minTime, bone.RotationKeys[0].Time);
                    maxTime = Math.Max(maxTime, bone.RotationKeys[^1].Time);
                }
                if (bone.TranslationKeys.Count > 0)
                {
                    bone.TranslationKeys = bone.TranslationKeys.OrderBy(k => k.Time).GroupBy(k => k.Time).Select(g => g.Last()).ToList();
                    minTime = Math.Min(minTime, bone.TranslationKeys[0].Time);
                    maxTime = Math.Max(maxTime, bone.TranslationKeys[^1].Time);
                }

                bone.NumRotationKeys = (short)bone.RotationKeys.Count;
                bone.NumTranslationKeys = (short)bone.TranslationKeys.Count;
            }

            if (minTime == int.MaxValue || maxTime == int.MinValue)
                return null;

            int startTime = minTime;
            int endTime = maxTime;
            float posReduction = 0.001f;
            float rotReduction = 0.0001f;
            int rampInTime = 800;
            int rampOutTime = 800;

            if (TryGetExtraInt(anim.extras, "rf_start_time", out int explicitStart))
                startTime = explicitStart;
            if (TryGetExtraInt(anim.extras, "rf_end_time", out int explicitEnd))
                endTime = explicitEnd;
            if (endTime < startTime)
            {
                startTime = minTime;
                endTime = maxTime;
            }

            if (TryGetExtraFloat(anim.extras, "rf_pos_reduction", out float explicitPosReduction) && explicitPosReduction >= 0f)
                posReduction = explicitPosReduction;
            if (TryGetExtraFloat(anim.extras, "rf_rot_reduction", out float explicitRotReduction) && explicitRotReduction >= 0f)
                rotReduction = explicitRotReduction;
            if (TryGetExtraInt(anim.extras, "rf_ramp_in_time", out int explicitRampIn) && explicitRampIn >= 0)
                rampInTime = explicitRampIn;
            if (TryGetExtraInt(anim.extras, "rf_ramp_out_time", out int explicitRampOut) && explicitRampOut >= 0)
                rampOutTime = explicitRampOut;

            int duration = Math.Max(0, endTime - startTime);
            if (duration == 0)
            {
                rampInTime = 0;
                rampOutTime = 0;
            }
            else
            {
                rampInTime = Math.Clamp(rampInTime, 0, duration);
                rampOutTime = Math.Clamp(rampOutTime, 0, duration);
                if (rampInTime + rampOutTime > duration)
                {
                    float scale = duration / (float)(rampInTime + rampOutTime);
                    rampInTime = (int)MathF.Round(rampInTime * scale);
                    rampOutTime = Math.Max(0, duration - rampInTime);
                }
            }

            return new RfaFile
            {
                Header = new RfaHeader
                {
                    Magic = BitConverter.GetBytes(0x46564D56),
                    Version = 8,
                    PosReduction = posReduction,
                    RotReduction = rotReduction,
                    StartTime = startTime,
                    EndTime = endTime,
                    NumBones = bones.Count,
                    NumMorphVertices = 0,
                    NumMorphKeyframes = 0,
                    RampInTime = rampInTime,
                    RampOutTime = rampOutTime,
                    TotalRotation = Quaternion.Identity,
                    TotalTranslation = Vector3.Zero,
                    MorphVertexMappingsOffset = 0,
                    MorphVertexDataOffset = 0,
                    BoneOffsets = Array.Empty<int>()
                },
                Bones = bones,
                MorphVertexMappings = Array.Empty<short>(),
                MorphKeyframes = new List<MorphKeyframe>()
            };
        }

        private struct AccessorInfo
        {
            public int Offset;
            public int Stride;
            public int Count;
            public int ComponentType;
            public int ComponentSize;
            public bool Normalized;
        }

        private class GltfRoot
        {
            public required List<BufferDef> buffers { get; set; }
            public required List<BufferView> bufferViews { get; set; }
            public required List<Accessor> accessors { get; set; }
            public required List<GltfMesh> meshes { get; set; }
            public required List<Node> nodes { get; set; }
            public List<Skin>? skins { get; set; }
            public List<Animation>? animations { get; set; }
            public List<Material>? materials { get; set; }
            public List<TextureDef>? textures { get; set; }
            public List<ImageDef>? images { get; set; }
        }

        private class BufferDef
        {
            public string? uri { get; set; }
            public int byteLength { get; set; }
        }

        private class BufferView
        {
            public int buffer { get; set; }
            public int? byteOffset { get; set; }
            public int byteLength { get; set; }
            public int? byteStride { get; set; }
        }

        private class Accessor
        {
            public int? bufferView { get; set; }
            public int? byteOffset { get; set; }
            public int componentType { get; set; }
            public bool? normalized { get; set; }
            public int count { get; set; }
            public required string type { get; set; }
        }

        private class GltfMesh
        {
            public required List<Primitive> primitives { get; set; }
        }

        private class Primitive
        {
            public required Dictionary<string, int> attributes { get; set; }
            public int? indices { get; set; }
            public int? material { get; set; }
            public JsonElement? extras { get; set; }
        }

        private class Material
        {
            public string? name { get; set; }
            public PbrMetallicRoughness? pbrMetallicRoughness { get; set; }
            public JsonElement? extras { get; set; }
        }

        private class PbrMetallicRoughness
        {
            public TextureInfo? baseColorTexture { get; set; }
        }

        private class TextureInfo
        {
            public int index { get; set; }
            public int? texCoord { get; set; }
        }

        private class TextureDef
        {
            public int source { get; set; }
            public int? sampler { get; set; }
        }

        private class ImageDef
        {
            public string? uri { get; set; }
            public string? name { get; set; }
        }

        private class Node
        {
            public string? name { get; set; }
            public int? mesh { get; set; }
            public int? skin { get; set; }
            public int[]? children { get; set; }
            public float[]? translation { get; set; }
            public float[]? rotation { get; set; }
            public float[]? scale { get; set; }
            public float[]? matrix { get; set; }
            public JsonElement? extras { get; set; }
        }

        private class Skin
        {
            public int[]? joints { get; set; }
            public int? inverseBindMatrices { get; set; }
        }

        private class Animation
        {
            public List<AnimationSampler>? samplers { get; set; }
            public List<AnimationChannel>? channels { get; set; }
            public JsonElement? extras { get; set; }
        }

        private class AnimationSampler
        {
            public int input { get; set; }
            public int output { get; set; }
            public string? interpolation { get; set; }
        }

        private class AnimationChannel
        {
            public int sampler { get; set; }
            public AnimationTarget? target { get; set; }
        }

        private class AnimationTarget
        {
            public int node { get; set; }
            public string? path { get; set; }
        }
    }
}
