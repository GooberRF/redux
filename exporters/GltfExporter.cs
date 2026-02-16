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
    public static class GltfExporter
    {
        private const string logSrc = "GltfExporter";

        public static void ExportGltf(Mesh mesh, string gltfPath)
            => ExportGltf(mesh, gltfPath, null, null);

        public static void ExportGltf(Mesh mesh, string gltfPath, RfaFile? animation, string? animationName)
        {
            string gltfDir = Path.GetDirectoryName(gltfPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(gltfPath);
            string binName = baseName + ".bin";
            string binPath = Path.Combine(gltfDir, binName);

            Logger.Info(logSrc, $"Writing glTF to: {gltfPath}");
            Logger.Info(logSrc, $"Writing BIN to:  {binPath}");

            bool hasBones = mesh.Bones.Count > 0;

            var binData = new List<byte>();
            var bufferViews = new List<BufferView>();
            var accessors = new List<Accessor>();
            var gltfMeshes = new List<GltfMesh>();
            var meshNodeSpecs = new List<ExportedBrushMesh>();
            var materials = new List<Material>();
            var textures = new List<TextureDef>();
            var images = new List<ImageDef>();
            var samplers = new List<SamplerDef>();

            foreach (var brush in mesh.Brushes)
            {
                List<MaterialTriangleGroup> groups = BuildMaterialTriangleGroups(brush);
                if (groups.Count == 0)
                    continue;

                CompactedBrushGeometry compact = CompactBrushGeometry(brush, groups, hasBones);
                if (compact.PositionsRh.Count == 0)
                    continue;

                foreach (var g in groups)
                    FlipTriangleWinding(g.Indices);

                var allIndices = new List<int>();
                foreach (var g in groups)
                    allIndices.AddRange(g.Indices);

                if (allIndices.Count < 3)
                    continue;

                int vertCount = compact.PositionsRh.Count;
                List<Vector3> positions = compact.PositionsRh;

                List<Vector3> normals = ComputeVertexNormals(allIndices, positions);

                List<Vector2> uvs = compact.UVs;
                List<Vector4> joints = compact.Joints;
                List<Vector4> weights = compact.Weights;

                int posAccessor = AddVec3Accessor(binData, bufferViews, accessors, positions, 34962, includeMinMax: true);
                int normAccessor = AddVec3Accessor(binData, bufferViews, accessors, normals, 34962, includeMinMax: false);
                int uvAccessor = AddVec2Accessor(binData, bufferViews, accessors, uvs, 34962);

                int jointsAccessor = -1;
                int weightsAccessor = -1;
                if (hasBones)
                {
                    jointsAccessor = AddUShortVec4Accessor(binData, bufferViews, accessors, joints, 34962);
                    weightsAccessor = AddVec4Accessor(binData, bufferViews, accessors, weights, 34962);
                }

                var brushPrimitives = new List<MeshPrimitive>();
                foreach (var group in groups)
                {
                    if (group.Indices.Count < 3)
                        continue;

                    bool useU32Indices = vertCount > ushort.MaxValue;
                    int indexAccessor = AddIndexAccessor(binData, bufferViews, accessors, group.Indices, useU32Indices);

                    var attrs = new Dictionary<string, int>
                    {
                        { "POSITION", posAccessor },
                        { "NORMAL", normAccessor },
                        { "TEXCOORD_0", uvAccessor }
                    };

                    if (hasBones)
                    {
                        attrs["JOINTS_0"] = jointsAccessor;
                        attrs["WEIGHTS_0"] = weightsAccessor;
                    }

                    int materialIndex = GetOrCreateMaterial(
                        group.TextureName,
                        brush.UID,
                        group.TextureSlot,
                        materials,
                        textures,
                        images,
                        samplers);

                    brushPrimitives.Add(new MeshPrimitive
                    {
                        attributes = attrs,
                        indices = indexAccessor,
                        material = materialIndex,
                        extras = new Dictionary<string, object>
                        {
                            ["rf_brush_uid"] = brush.UID,
                            ["rf_brush_name"] = string.IsNullOrWhiteSpace(brush.TextureName) ? $"Brush_{brush.UID}" : brush.TextureName,
                            ["rf_texture"] = group.TextureName,
                            ["rf_texture_slot"] = group.TextureSlot
                        }
                    });
                }

                if (brushPrimitives.Count == 0)
                    continue;

                int meshIndex = gltfMeshes.Count;
                gltfMeshes.Add(new GltfMesh
                {
                    primitives = brushPrimitives
                });

                meshNodeSpecs.Add(new ExportedBrushMesh
                {
                    MeshIndex = meshIndex,
                    BrushUid = brush.UID,
                    BrushName = string.IsNullOrWhiteSpace(brush.TextureName) ? $"Brush_{brush.UID}" : brush.TextureName,
                    LodIndex = TryExtractLodIndex(brush.TextureName),
                    MaterialSlots = BuildMaterialSlotList(brush)
                });
            }

            if (gltfMeshes.Count == 0)
                throw new InvalidOperationException("No mesh primitives to export.");

            int inverseBindAccessor = -1;
            Quaternion[] bindGlobalRotationsRh = Array.Empty<Quaternion>();
            Vector3[] bindGlobalPositionsRh = Array.Empty<Vector3>();
            if (hasBones)
            {
                int boneCount = mesh.Bones.Count;
                bindGlobalRotationsRh = new Quaternion[boneCount];
                bindGlobalPositionsRh = new Vector3[boneCount];

                var inverseBindMats = new List<float[]>(boneCount);
                for (int i = 0; i < boneCount; i++)
                {
                    var bone = mesh.Bones[i];
                    Quaternion q = Quaternion.Normalize(bone.BaseRotation);
                    Vector3 t = bone.BaseTranslation;

                    Quaternion qRh = RfToRh(q);
                    Vector3 tRh = RfToRh(t);

                    // Bind pose in model space = inverse(inv_bind)
                    Quaternion bindRot = Quaternion.Normalize(Quaternion.Conjugate(qRh));
                    Vector3 bindPos = -Vector3.Transform(tRh, bindRot);
                    bindGlobalRotationsRh[i] = bindRot;
                    bindGlobalPositionsRh[i] = bindPos;

                    inverseBindMats.Add(BuildInvBindMatrixColumnMajor(qRh, tRh));
                }

                inverseBindAccessor = AddMat4Accessor(binData, bufferViews, accessors, inverseBindMats);
            }

            var nodes = new List<Node>();
            var sceneNodeIndices = new List<int>();

            int[] boneNodeIndices = Array.Empty<int>();
            if (hasBones)
            {
                boneNodeIndices = new int[mesh.Bones.Count];
                for (int i = 0; i < mesh.Bones.Count; i++)
                {
                    Bone b = mesh.Bones[i];
                    int parent = b.ParentIndex;
                    Quaternion localRot;
                    Vector3 localPos;
                    if (parent >= 0 && parent < mesh.Bones.Count)
                    {
                        Quaternion parentGlobalRot = bindGlobalRotationsRh[parent];
                        Vector3 parentGlobalPos = bindGlobalPositionsRh[parent];
                        Quaternion invParent = Quaternion.Normalize(Quaternion.Conjugate(parentGlobalRot));

                        localRot = Quaternion.Normalize(Quaternion.Multiply(invParent, bindGlobalRotationsRh[i]));
                        localPos = Vector3.Transform(bindGlobalPositionsRh[i] - parentGlobalPos, invParent);
                    }
                    else
                    {
                        localRot = bindGlobalRotationsRh[i];
                        localPos = bindGlobalPositionsRh[i];
                    }

                    var jointNode = new Node
                    {
                        name = BuildBoneNodeName(b.Name, i),
                        translation = new[] { localPos.X, localPos.Y, localPos.Z },
                        rotation = new[] { localRot.X, localRot.Y, localRot.Z, localRot.W },
                        scale = new[] { 1f, 1f, 1f },
                        children = new List<int>(),
                        extras = new Dictionary<string, object>
                        {
                            ["rf_type"] = "bone",
                            ["rf_bone_index"] = i
                        }
                    };

                    nodes.Add(jointNode);
                    boneNodeIndices[i] = nodes.Count - 1;
                }

                for (int i = 0; i < mesh.Bones.Count; i++)
                {
                    int parent = mesh.Bones[i].ParentIndex;
                    if (parent >= 0 && parent < mesh.Bones.Count)
                    {
                        nodes[boneNodeIndices[parent]].children!.Add(boneNodeIndices[i]);
                    }
                    else
                    {
                        sceneNodeIndices.Add(boneNodeIndices[i]);
                    }
                }
            }

            foreach (var spec in meshNodeSpecs)
            {
                var extras = new Dictionary<string, object>
                {
                    ["rf_type"] = "brush",
                    ["rf_brush_uid"] = spec.BrushUid,
                    ["rf_brush_name"] = spec.BrushName,
                    ["rf_material_slots"] = spec.MaterialSlots
                };
                if (spec.LodIndex.HasValue)
                    extras["rf_lod_index"] = spec.LodIndex.Value;

                var meshNode = new Node
                {
                    name = spec.BrushName,
                    mesh = spec.MeshIndex,
                    skin = hasBones ? 0 : null,
                    translation = new[] { 0f, 0f, 0f },
                    rotation = new[] { 0f, 0f, 0f, 1f },
                    scale = new[] { 1f, 1f, 1f },
                    children = new List<int>(),
                    extras = extras
                };

                nodes.Add(meshNode);
                sceneNodeIndices.Add(nodes.Count - 1);
            }

            foreach (var brush in mesh.Brushes)
            {
                if (brush.PropPoints == null || brush.PropPoints.Count == 0)
                    continue;

                foreach (var prop in brush.PropPoints)
                {
                    Vector3 pos = RfToRh(prop.Position);
                    Quaternion q = prop.Orientation.LengthSquared() < 1e-8f
                        ? Quaternion.Identity
                        : Quaternion.Normalize(prop.Orientation);
                    Quaternion qRh = RfToRh(q);

                    var propNode = new Node
                    {
                        name = BuildPropPointNodeName(prop.Name, brush.UID),
                        translation = new[] { pos.X, pos.Y, pos.Z },
                        rotation = new[] { qRh.X, qRh.Y, qRh.Z, qRh.W },
                        scale = new[] { 1f, 1f, 1f },
                        children = new List<int>(),
                        extras = new Dictionary<string, object>
                        {
                            ["rf_type"] = "prop_point",
                            ["rf_name"] = prop.Name ?? string.Empty,
                            ["rf_parent_bone"] = prop.ParentIndex,
                            ["rf_brush_uid"] = brush.UID
                        }
                    };

                    nodes.Add(propNode);
                    int propNodeIdx = nodes.Count - 1;

                    bool attachedToBone = hasBones && prop.ParentIndex >= 0 && prop.ParentIndex < boneNodeIndices.Length;
                    if (attachedToBone)
                        nodes[boneNodeIndices[prop.ParentIndex]].children!.Add(propNodeIdx);
                    else
                        sceneNodeIndices.Add(propNodeIdx);
                }
            }

            foreach (var sphere in mesh.CollisionSpheres)
            {
                Vector3 pos = RfToRh(sphere.Position);
                float radius = sphere.Radius <= 0f ? 0.01f : sphere.Radius;

                var sphereNode = new Node
                {
                    name = BuildCollisionSphereNodeName(sphere.Name),
                    translation = new[] { pos.X, pos.Y, pos.Z },
                    rotation = new[] { 0f, 0f, 0f, 1f },
                    scale = new[] { radius, radius, radius },
                    children = new List<int>(),
                    extras = new Dictionary<string, object>
                    {
                        ["rf_type"] = "collision_sphere",
                        ["rf_name"] = sphere.Name ?? string.Empty,
                        ["rf_radius"] = sphere.Radius,
                        ["rf_parent_bone"] = sphere.ParentIndex
                    }
                };

                nodes.Add(sphereNode);
                int sphereNodeIdx = nodes.Count - 1;

                bool attachedToBone = hasBones && sphere.ParentIndex >= 0 && sphere.ParentIndex < boneNodeIndices.Length;
                if (attachedToBone)
                    nodes[boneNodeIndices[sphere.ParentIndex]].children!.Add(sphereNodeIdx);
                else
                    sceneNodeIndices.Add(sphereNodeIdx);
            }

            List<Skin>? skins = null;
            if (hasBones)
            {
                int rootBone = mesh.Bones.FindIndex(b => b.ParentIndex < 0);
                if (rootBone < 0)
                    rootBone = 0;

                skins =
                [
                    new Skin
                    {
                        inverseBindMatrices = inverseBindAccessor,
                        joints = boneNodeIndices,
                        skeleton = boneNodeIndices[rootBone]
                    }
                ];
            }

            List<Animation>? animations = null;
            if (animation != null && hasBones)
            {
                Animation? gltfAnim = BuildAnimation(animation, animationName, mesh.Bones.Count, boneNodeIndices, binData, bufferViews, accessors);
                if (gltfAnim != null)
                    animations = [gltfAnim];
            }

            var gltf = new GltfRoot
            {
                asset = new Asset { version = "2.0", generator = "redux GltfExporter" },
                buffers = [new Buffer { uri = binName, byteLength = binData.Count }],
                bufferViews = bufferViews,
                accessors = accessors,
                meshes = gltfMeshes,
                nodes = nodes,
                skins = skins,
                animations = animations,
                materials = materials.Count > 0 ? materials : null,
                textures = textures.Count > 0 ? textures : null,
                images = images.Count > 0 ? images : null,
                samplers = samplers.Count > 0 ? samplers : null,
                scenes = [new Scene { nodes = sceneNodeIndices }],
                scene = 0
            };

            Directory.CreateDirectory(gltfDir.Length == 0 ? "." : gltfDir);
            File.WriteAllBytes(binPath, binData.ToArray());

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            string json = JsonSerializer.Serialize(gltf, opts);
            File.WriteAllText(gltfPath, json);

            Logger.Info(logSrc, "glTF export complete.");
        }

        private static List<int> BuildIndicesFromFaces(List<Face> faces)
        {
            var indices = new List<int>();
            foreach (var face in faces)
            {
                if (face.Vertices.Count < 3)
                    continue;

                for (int i = 1; i < face.Vertices.Count - 1; i++)
                {
                    indices.Add(face.Vertices[0]);
                    indices.Add(face.Vertices[i]);
                    indices.Add(face.Vertices[i + 1]);
                }
            }
            return indices;
        }

        private static List<MaterialTriangleGroup> BuildMaterialTriangleGroups(Brush brush)
        {
            var groupsByTextureSlot = new Dictionary<int, MaterialTriangleGroup>();
            bool usedFaces = false;
            int vertexCount = brush.Vertices.Count;

            if (brush.Solid?.Faces != null && brush.Solid.Faces.Count > 0)
            {
                foreach (var face in brush.Solid.Faces)
                {
                    if (face.Vertices.Count < 3)
                        continue;

                    int textureSlot = face.TextureIndex;
                    if (textureSlot < 0)
                        textureSlot = 0;

                    if (!groupsByTextureSlot.TryGetValue(textureSlot, out var group))
                    {
                        group = new MaterialTriangleGroup
                        {
                            TextureSlot = textureSlot,
                            TextureName = ResolveBrushTextureName(brush, textureSlot)
                        };
                        groupsByTextureSlot[textureSlot] = group;
                    }

                    for (int i = 1; i < face.Vertices.Count - 1; i++)
                    {
                        int i0 = face.Vertices[0];
                        int i1 = face.Vertices[i];
                        int i2 = face.Vertices[i + 1];
                        if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                            continue;
                        group.Indices.Add(i0);
                        group.Indices.Add(i1);
                        group.Indices.Add(i2);
                        usedFaces = true;
                    }
                }
            }

            if (!usedFaces)
            {
                List<int> fallbackIndices = brush.Indices.Count > 0
                    ? new List<int>(brush.Indices)
                    : BuildIndicesFromFaces(brush.Solid?.Faces ?? new List<Face>());

                fallbackIndices = FilterValidTriangles(fallbackIndices, vertexCount);
                if (fallbackIndices.Count >= 3)
                {
                    int textureSlot = 0;
                    groupsByTextureSlot[textureSlot] = new MaterialTriangleGroup
                    {
                        TextureSlot = textureSlot,
                        TextureName = ResolveBrushTextureName(brush, textureSlot),
                        Indices = fallbackIndices
                    };
                }
            }

            return groupsByTextureSlot.Values
                .Where(g => g.Indices.Count >= 3)
                .OrderBy(g => g.TextureSlot)
                .ToList();
        }

        private static List<int> FilterValidTriangles(List<int> indices, int vertexCount)
        {
            var result = new List<int>(indices.Count);
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                    continue;

                result.Add(i0);
                result.Add(i1);
                result.Add(i2);
            }

            return result;
        }

        private static CompactedBrushGeometry CompactBrushGeometry(Brush brush, List<MaterialTriangleGroup> groups, bool hasBones)
        {
            var positionsRh = new List<Vector3>();
            var uvs = new List<Vector2>();
            var joints = new List<Vector4>();
            var weights = new List<Vector4>();
            var keyToIndex = new Dictionary<VertexWeldKey, int>();

            int sourceVertexCount = brush.Vertices.Count;
            foreach (var group in groups)
            {
                for (int i = 0; i < group.Indices.Count; i++)
                {
                    int sourceIndex = group.Indices[i];
                    if (sourceIndex < 0 || sourceIndex >= sourceVertexCount)
                    {
                        group.Indices[i] = -1;
                        continue;
                    }

                    Vector3 srcPos = brush.Vertices[sourceIndex];
                    Vector2 srcUv = sourceIndex < brush.UVs.Count ? brush.UVs[sourceIndex] : Vector2.Zero;
                    Vector4 srcJoints = hasBones && brush.JointIndices != null && sourceIndex < brush.JointIndices.Count
                        ? ClampJointIndices(brush.JointIndices[sourceIndex])
                        : Vector4.Zero;
                    Vector4 srcWeights = hasBones && brush.JointWeights != null && sourceIndex < brush.JointWeights.Count
                        ? NormalizeWeights(brush.JointWeights[sourceIndex])
                        : new Vector4(1f, 0f, 0f, 0f);

                    VertexWeldKey key = BuildVertexWeldKey(srcPos, srcUv, srcJoints, srcWeights);
                    if (!keyToIndex.TryGetValue(key, out int compactIndex))
                    {
                        compactIndex = positionsRh.Count;
                        keyToIndex[key] = compactIndex;
                        positionsRh.Add(RfToRh(srcPos));
                        uvs.Add(srcUv);
                        if (hasBones)
                        {
                            joints.Add(srcJoints);
                            weights.Add(srcWeights);
                        }
                    }

                    group.Indices[i] = compactIndex;
                }

                group.Indices = FilterValidTriangles(group.Indices, positionsRh.Count);
            }

            groups.RemoveAll(g => g.Indices.Count < 3);

            return new CompactedBrushGeometry
            {
                PositionsRh = positionsRh,
                UVs = uvs,
                Joints = joints,
                Weights = weights
            };
        }

        private static string ResolveBrushTextureName(Brush brush, int textureSlot)
        {
            if (brush.Solid?.Textures != null && textureSlot >= 0 && textureSlot < brush.Solid.Textures.Count)
                return NormalizeRfTextureName(brush.Solid.Textures[textureSlot]);

            if (!string.IsNullOrWhiteSpace(brush.TextureName))
                return NormalizeRfTextureName(brush.TextureName);

            return "default.tga";
        }

        private static string NormalizeRfTextureName(string? texture)
        {
            if (string.IsNullOrWhiteSpace(texture))
                return "default.tga";

            string name = texture.Replace('\\', '/');
            int query = name.IndexOf('?');
            int fragment = name.IndexOf('#');
            if (query < 0 || (fragment >= 0 && fragment < query))
                query = fragment;
            if (query >= 0)
                name = name[..query];

            name = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(name))
                return "default.tga";

            if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                name += ".tga";

            return name;
        }

        private static int? TryExtractLodIndex(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            int marker = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return null;

            int idxStart = marker + 4;
            if (idxStart >= name.Length)
                return null;

            int idxEnd = idxStart;
            while (idxEnd < name.Length && char.IsDigit(name[idxEnd]))
                idxEnd++;

            if (idxEnd == idxStart)
                return null;

            return int.TryParse(name[idxStart..idxEnd], out int lod) ? lod : null;
        }

        private static List<string> BuildMaterialSlotList(Brush brush)
        {
            var slots = new List<string>();
            if (brush.Solid?.Textures == null)
                return slots;

            foreach (string tex in brush.Solid.Textures)
                slots.Add(NormalizeRfTextureName(tex));

            return slots;
        }

        private static VertexWeldKey BuildVertexWeldKey(Vector3 pos, Vector2 uv, Vector4 joints, Vector4 weights)
        {
            return new VertexWeldKey(
                Quantize(pos.X, 100000f),
                Quantize(pos.Y, 100000f),
                Quantize(pos.Z, 100000f),
                Quantize(uv.X, 1000000f),
                Quantize(uv.Y, 1000000f),
                Quantize(joints.X, 1f),
                Quantize(joints.Y, 1f),
                Quantize(joints.Z, 1f),
                Quantize(joints.W, 1f),
                Quantize(weights.X, 1000000f),
                Quantize(weights.Y, 1000000f),
                Quantize(weights.Z, 1000000f),
                Quantize(weights.W, 1000000f));
        }

        private static long Quantize(float value, float scale)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0;
            return (long)MathF.Round(value * scale, MidpointRounding.AwayFromZero);
        }

        private static int GetOrCreateMaterial(
            string rfTextureName,
            int brushUid,
            int textureSlot,
            List<Material> materials,
            List<TextureDef> textures,
            List<ImageDef> images,
            List<SamplerDef> samplers)
        {
            string normalizedTexture = NormalizeRfTextureName(rfTextureName);

            int samplerIndex = EnsureDefaultSampler(samplers);
            images.Add(new ImageDef
            {
                uri = ToGltfUri(normalizedTexture),
                name = Path.GetFileNameWithoutExtension(normalizedTexture)
            });

            textures.Add(new TextureDef
            {
                sampler = samplerIndex,
                source = images.Count - 1
            });

            materials.Add(new Material
            {
                // Keep material name aligned with texture filename so Blender roundtrip still resolves
                // the original RF bitmap even if custom extras are stripped.
                name = normalizedTexture,
                doubleSided = true,
                pbrMetallicRoughness = new PbrMetallicRoughness
                {
                    baseColorFactor = [1f, 1f, 1f, 1f],
                    metallicFactor = 0f,
                    roughnessFactor = 1f,
                    baseColorTexture = new TextureInfo { index = textures.Count - 1 }
                },
                extras = new Dictionary<string, object>
                {
                    ["rf_texture"] = normalizedTexture,
                    ["rf_brush_uid"] = brushUid,
                    ["rf_texture_slot"] = textureSlot
                }
            });

            return materials.Count - 1;
        }

        private static int EnsureDefaultSampler(List<SamplerDef> samplers)
        {
            if (samplers.Count > 0)
                return 0;

            samplers.Add(new SamplerDef
            {
                magFilter = 9729,
                minFilter = 9729,
                wrapS = 10497,
                wrapT = 10497
            });

            return 0;
        }

        private static string ToGltfUri(string path)
            => path.Replace('\\', '/');

        private static void FlipTriangleWinding(List<int> indices)
        {
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        private static List<Vector3> ComputeVertexNormals(List<int> indices, List<Vector3> positionsRh)
        {
            int count = positionsRh.Count;
            var normals = Enumerable.Repeat(Vector3.Zero, count).ToArray();
            var contributions = new int[count];

            for (int tri = 0; tri + 2 < indices.Count; tri += 3)
            {
                int i0 = indices[tri];
                int i1 = indices[tri + 1];
                int i2 = indices[tri + 2];

                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= count || i1 >= count || i2 >= count)
                    continue;

                Vector3 p0 = positionsRh[i0];
                Vector3 p1 = positionsRh[i1];
                Vector3 p2 = positionsRh[i2];

                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                if (cross.LengthSquared() < 1e-8f)
                    continue;

                Vector3 n = Vector3.Normalize(cross);
                normals[i0] += n;
                normals[i1] += n;
                normals[i2] += n;
                contributions[i0]++;
                contributions[i1]++;
                contributions[i2]++;
            }

            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                if (contributions[i] == 0 || normals[i].LengthSquared() < 1e-8f)
                    result.Add(new Vector3(0, 1, 0));
                else
                    result.Add(Vector3.Normalize(normals[i]));
            }

            return result;
        }

        private static Vector4 ClampJointIndices(Vector4 v)
        {
            return new Vector4(
                Math.Clamp(v.X, 0f, ushort.MaxValue),
                Math.Clamp(v.Y, 0f, ushort.MaxValue),
                Math.Clamp(v.Z, 0f, ushort.MaxValue),
                Math.Clamp(v.W, 0f, ushort.MaxValue));
        }

        private static Vector4 NormalizeWeights(Vector4 w)
        {
            float w0 = MathF.Max(0f, w.X);
            float w1 = MathF.Max(0f, w.Y);
            float w2 = MathF.Max(0f, w.Z);
            float w3 = MathF.Max(0f, w.W);
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 1e-6f)
                return new Vector4(1f, 0f, 0f, 0f);
            return new Vector4(w0 / sum, w1 / sum, w2 / sum, w3 / sum);
        }

        private static string BuildBoneNodeName(string? boneName, int boneIndex)
        {
            string baseName = string.IsNullOrWhiteSpace(boneName) ? $"bone_{boneIndex}" : boneName.Trim();
            if (TryStripBoneIndexSuffix(baseName, out string stripped))
                baseName = stripped;
            return $"{baseName}__rfbi{boneIndex}";
        }

        private static bool TryStripBoneIndexSuffix(string value, out string stripped)
        {
            stripped = value;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            const string marker = "__rfbi";
            int markerIdx = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx < 0)
                return false;

            int digitsStart = markerIdx + marker.Length;
            if (digitsStart >= value.Length)
                return false;

            for (int i = digitsStart; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                    return false;
            }

            stripped = value[..markerIdx];
            return true;
        }

        private static Vector3 RfToRh(Vector3 v) => new(-v.X, v.Y, v.Z);
        private static Quaternion RfToRh(Quaternion q) => Quaternion.Normalize(new Quaternion(-q.X, q.Y, q.Z, q.W));

        private static string BuildCollisionSphereNodeName(string? name)
            => "rf_csphere::" + (string.IsNullOrWhiteSpace(name) ? "unnamed" : name);

        private static string BuildPropPointNodeName(string? name, int brushUid)
            => $"rf_prop::uid{brushUid}::" + (string.IsNullOrWhiteSpace(name) ? "unnamed" : name);

        private static Animation? BuildAnimation(
            RfaFile source,
            string? animationName,
            int boneCount,
            int[] boneNodeIndices,
            List<byte> binData,
            List<BufferView> bufferViews,
            List<Accessor> accessors)
        {
            if (source.Bones == null || source.Bones.Count == 0)
                return null;

            var samplers = new List<AnimationSampler>();
            var channels = new List<AnimationChannel>();

            int usableBones = Math.Min(Math.Min(boneCount, boneNodeIndices.Length), source.Bones.Count);
            for (int boneIdx = 0; boneIdx < usableBones; boneIdx++)
            {
                RfaBone bone = source.Bones[boneIdx];

                if (bone.RotationKeys != null && bone.RotationKeys.Count > 0)
                {
                    var keys = bone.RotationKeys.OrderBy(k => k.Time).ToList();
                    var times = keys.Select(k => k.Time / 4800f).ToList();
                    var values = keys.Select(k => RfToRh(Quaternion.Normalize(k.Rotation))).ToList();

                    int timeAccessor = AddScalarAccessor(binData, bufferViews, accessors, times);
                    int valueAccessor = AddQuaternionAccessor(binData, bufferViews, accessors, values);

                    samplers.Add(new AnimationSampler
                    {
                        input = timeAccessor,
                        output = valueAccessor,
                        interpolation = "LINEAR"
                    });

                    channels.Add(new AnimationChannel
                    {
                        sampler = samplers.Count - 1,
                        target = new AnimationTarget
                        {
                            node = boneNodeIndices[boneIdx],
                            path = "rotation"
                        }
                    });
                }

                if (bone.TranslationKeys != null && bone.TranslationKeys.Count > 0)
                {
                    var keys = bone.TranslationKeys.OrderBy(k => k.Time).ToList();
                    var times = keys.Select(k => k.Time / 4800f).ToList();
                    var values = new List<Vector3>(keys.Count * 3);
                    for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
                    {
                        RfaTranslationKey key = keys[keyIndex];
                        Vector3 pRf = key.Translation;
                        Vector3 inHandleRf = key.InTangent;
                        Vector3 outHandleRf = key.OutTangent;

                        float prevDt = keyIndex > 0 ? MathF.Max(0f, (keys[keyIndex].Time - keys[keyIndex - 1].Time) / 4800f) : 0f;
                        float nextDt = keyIndex + 1 < keys.Count ? MathF.Max(0f, (keys[keyIndex + 1].Time - keys[keyIndex].Time) / 4800f) : 0f;

                        Vector3 inDerivRf = Vector3.Zero;
                        Vector3 outDerivRf = Vector3.Zero;

                        if (prevDt > 1e-6f)
                        {
                            inDerivRf = (pRf - inHandleRf) * (3f / prevDt);
                        }
                        else if (nextDt > 1e-6f)
                        {
                            // First key fallback: mirror outgoing slope.
                            inDerivRf = (outHandleRf - pRf) * (3f / nextDt);
                        }

                        if (nextDt > 1e-6f)
                        {
                            outDerivRf = (outHandleRf - pRf) * (3f / nextDt);
                        }
                        else if (prevDt > 1e-6f)
                        {
                            // Last key fallback: mirror incoming slope.
                            outDerivRf = (pRf - inHandleRf) * (3f / prevDt);
                        }

                        values.Add(RfToRh(inDerivRf));
                        values.Add(RfToRh(pRf));
                        values.Add(RfToRh(outDerivRf));
                    }

                    int timeAccessor = AddScalarAccessor(binData, bufferViews, accessors, times);
                    int valueAccessor = AddVec3Accessor(binData, bufferViews, accessors, values, null, includeMinMax: false);

                    samplers.Add(new AnimationSampler
                    {
                        input = timeAccessor,
                        output = valueAccessor,
                        interpolation = "CUBICSPLINE"
                    });

                    channels.Add(new AnimationChannel
                    {
                        sampler = samplers.Count - 1,
                        target = new AnimationTarget
                        {
                            node = boneNodeIndices[boneIdx],
                            path = "translation"
                        }
                    });
                }
            }

            if (channels.Count == 0)
                return null;

            RfaHeader hdr = source.Header ?? new RfaHeader();
            return new Animation
            {
                name = string.IsNullOrWhiteSpace(animationName) ? "rfa_animation" : animationName,
                samplers = samplers,
                channels = channels,
                extras = new Dictionary<string, object>
                {
                    ["rf_start_time"] = hdr.StartTime,
                    ["rf_end_time"] = hdr.EndTime,
                    ["rf_ramp_in_time"] = hdr.RampInTime,
                    ["rf_ramp_out_time"] = hdr.RampOutTime,
                    ["rf_pos_reduction"] = hdr.PosReduction,
                    ["rf_rot_reduction"] = hdr.RotReduction
                }
            };
        }

        private static float[] BuildInvBindMatrixColumnMajor(Quaternion rotation, Vector3 translation)
        {
            Quaternion q = Quaternion.Normalize(rotation);
            float x = q.X;
            float y = q.Y;
            float z = q.Z;
            float w = q.W;

            float xx = x * x;
            float yy = y * y;
            float zz = z * z;
            float xy = x * y;
            float xz = x * z;
            float yz = y * z;
            float xw = x * w;
            float yw = y * w;
            float zw = z * w;

            float r00 = 1f - 2f * (yy + zz);
            float r01 = 2f * (xy - zw);
            float r02 = 2f * (xz + yw);

            float r10 = 2f * (xy + zw);
            float r11 = 1f - 2f * (xx + zz);
            float r12 = 2f * (yz - xw);

            float r20 = 2f * (xz - yw);
            float r21 = 2f * (yz + xw);
            float r22 = 1f - 2f * (xx + yy);

            // glTF MAT4 uses column-major storage.
            return
            [
                r00, r10, r20, 0f,
                r01, r11, r21, 0f,
                r02, r12, r22, 0f,
                translation.X, translation.Y, translation.Z, 1f
            ];
        }

        private static int AddMat4Accessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<float[]> values)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var m in values)
            {
                if (m.Length != 16)
                    throw new InvalidDataException("MAT4 accessor entry must contain 16 floats.");
                for (int i = 0; i < 16; i++)
                    bw.Write(m[i]);
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), null);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "MAT4"
            });
            return accessors.Count - 1;
        }

        private static int AddQuaternionAccessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<Quaternion> values)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var q in values)
            {
                bw.Write(q.X);
                bw.Write(q.Y);
                bw.Write(q.Z);
                bw.Write(q.W);
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), null);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "VEC4"
            });
            return accessors.Count - 1;
        }

        private static int AddScalarAccessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<float> values)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (float v in values)
                bw.Write(v);

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), null);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "SCALAR",
                min = values.Count > 0 ? new[] { values.Min() } : null,
                max = values.Count > 0 ? new[] { values.Max() } : null
            });
            return accessors.Count - 1;
        }

        private static int AddVec3Accessor(
            List<byte> binData,
            List<BufferView> bufferViews,
            List<Accessor> accessors,
            List<Vector3> values,
            int? target,
            bool includeMinMax)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var v in values)
            {
                bw.Write(v.X);
                bw.Write(v.Y);
                bw.Write(v.Z);
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), target);

            float[]? min = null;
            float[]? max = null;
            if (includeMinMax && values.Count > 0)
            {
                min =
                [
                    values.Min(v => v.X),
                    values.Min(v => v.Y),
                    values.Min(v => v.Z)
                ];
                max =
                [
                    values.Max(v => v.X),
                    values.Max(v => v.Y),
                    values.Max(v => v.Z)
                ];
            }

            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "VEC3",
                min = min,
                max = max
            });
            return accessors.Count - 1;
        }

        private static int AddVec2Accessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<Vector2> values, int? target)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var v in values)
            {
                bw.Write(v.X);
                bw.Write(v.Y);
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), target);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "VEC2"
            });
            return accessors.Count - 1;
        }

        private static int AddVec4Accessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<Vector4> values, int? target)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var v in values)
            {
                bw.Write(v.X);
                bw.Write(v.Y);
                bw.Write(v.Z);
                bw.Write(v.W);
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), target);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5126,
                count = values.Count,
                type = "VEC4"
            });
            return accessors.Count - 1;
        }

        private static int AddUShortVec4Accessor(List<byte> binData, List<BufferView> bufferViews, List<Accessor> accessors, List<Vector4> values, int? target)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var v in values)
            {
                bw.Write((ushort)Math.Clamp((int)MathF.Round(v.X), 0, ushort.MaxValue));
                bw.Write((ushort)Math.Clamp((int)MathF.Round(v.Y), 0, ushort.MaxValue));
                bw.Write((ushort)Math.Clamp((int)MathF.Round(v.Z), 0, ushort.MaxValue));
                bw.Write((ushort)Math.Clamp((int)MathF.Round(v.W), 0, ushort.MaxValue));
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), target);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = 5123,
                count = values.Count,
                type = "VEC4"
            });
            return accessors.Count - 1;
        }

        private static int AddIndexAccessor(
            List<byte> binData,
            List<BufferView> bufferViews,
            List<Accessor> accessors,
            List<int> indices,
            bool useU32)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            if (useU32)
            {
                foreach (int idx in indices)
                    bw.Write((uint)Math.Max(0, idx));
            }
            else
            {
                foreach (int idx in indices)
                    bw.Write((ushort)Math.Clamp(idx, 0, ushort.MaxValue));
            }

            int view = AddBufferView(binData, bufferViews, ms.ToArray(), 34963);
            accessors.Add(new Accessor
            {
                bufferView = view,
                byteOffset = 0,
                componentType = useU32 ? 5125 : 5123,
                count = indices.Count,
                type = "SCALAR",
                min = indices.Count > 0 ? new[] { (float)indices.Min() } : null,
                max = indices.Count > 0 ? new[] { (float)indices.Max() } : null
            });
            return accessors.Count - 1;
        }

        private static int AddBufferView(List<byte> binData, List<BufferView> bufferViews, byte[] bytes, int? target)
        {
            while ((binData.Count % 4) != 0)
                binData.Add(0);

            int offset = binData.Count;
            binData.AddRange(bytes);

            bufferViews.Add(new BufferView
            {
                buffer = 0,
                byteOffset = offset,
                byteLength = bytes.Length,
                target = target
            });

            return bufferViews.Count - 1;
        }

        private class GltfRoot
        {
            public required Asset asset { get; set; }
            public required List<Buffer> buffers { get; set; }
            public required List<BufferView> bufferViews { get; set; }
            public required List<Accessor> accessors { get; set; }
            public required List<GltfMesh> meshes { get; set; }
            public required List<Node> nodes { get; set; }
            public List<Skin>? skins { get; set; }
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

        private class Buffer
        {
            public required string uri { get; set; }
            public int byteLength { get; set; }
        }

        private class BufferView
        {
            public int buffer { get; set; }
            public int byteOffset { get; set; }
            public int byteLength { get; set; }
            public int? target { get; set; }
        }

        private class Accessor
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
            public required List<MeshPrimitive> primitives { get; set; }
        }

        private class MeshPrimitive
        {
            public required Dictionary<string, int> attributes { get; set; }
            public int indices { get; set; }
            public int? material { get; set; }
            public Dictionary<string, object>? extras { get; set; }
        }

        private class Material
        {
            public string? name { get; set; }
            public bool? doubleSided { get; set; }
            public string? alphaMode { get; set; }
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
            public int? skin { get; set; }
            public float[]? translation { get; set; }
            public float[]? rotation { get; set; }
            public float[]? scale { get; set; }
            public List<int>? children { get; set; }
            public Dictionary<string, object>? extras { get; set; }
        }

        private class Skin
        {
            public int inverseBindMatrices { get; set; }
            public required int[] joints { get; set; }
            public int skeleton { get; set; }
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
            public Dictionary<string, object>? extras { get; set; }
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

        private class MaterialTriangleGroup
        {
            public int TextureSlot { get; set; }
            public string TextureName { get; set; } = "default.tga";
            public List<int> Indices { get; set; } = new();
        }

        private class CompactedBrushGeometry
        {
            public List<Vector3> PositionsRh { get; set; } = new();
            public List<Vector2> UVs { get; set; } = new();
            public List<Vector4> Joints { get; set; } = new();
            public List<Vector4> Weights { get; set; } = new();
        }

        private class ExportedBrushMesh
        {
            public int MeshIndex { get; set; }
            public int BrushUid { get; set; }
            public string BrushName { get; set; } = string.Empty;
            public int? LodIndex { get; set; }
            public List<string> MaterialSlots { get; set; } = new();
        }

        private readonly record struct VertexWeldKey(
            long Px,
            long Py,
            long Pz,
            long U,
            long V,
            long J0,
            long J1,
            long J2,
            long J3,
            long W0,
            long W1,
            long W2,
            long W3);
    }
}
