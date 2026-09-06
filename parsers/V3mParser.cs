using redux.parsers.parser_utils;
using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static redux.utilities.Utils;

namespace redux.parsers
{
    // Parser for V3m (static) and V3c (skeletal) mesh files
    public static class V3mParser
    {
        private const string logSrc = "V3mParser";

        private const int V3M_SIGNATURE = 0x52463344;    // 'RF3D'
        private const int V3C_SIGNATURE = 0x5246434D;    // 'RFCM'
        private const int V3D_VERSION = 0x40000;

        private const int SECTION_END = 0x00000000;
        private const int SECTION_SUBMESH = 0x5355424D; // 'SUBM'
        private const int SECTION_CSPHERE = 0x43535048; // 'CSPH'
        private const int SECTION_BONES = 0x424F4E45; // 'BONE'

        public static Mesh ReadV3mAsRflMesh(string path)
        {
            Logger.Debug(logSrc, $"Opening V3M/V3C file: \"{path}\"");
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            // Header
            int signature = reader.ReadInt32();
            Logger.Debug(logSrc, $"Signature read: 0x{signature:X8}");
            if (signature != V3M_SIGNATURE && signature != V3C_SIGNATURE)
                throw new InvalidDataException($"Not a V3M/V3C file (sig=0x{signature:X8})");

            int version = reader.ReadInt32();
            Logger.Debug(logSrc, $"V3D version: 0x{version:X8}");
            if (version != V3D_VERSION)
                throw new InvalidDataException($"Unsupported V3D version 0x{version:X8}");

            int numSubmeshes = reader.ReadInt32();
            int numAllVerts = reader.ReadInt32(); // always 0
            int numAllTriangles = reader.ReadInt32(); // always 0
            int unknown0 = reader.ReadInt32(); // zero (normals)
            int numAllMaterials = reader.ReadInt32();
            int unknown1 = reader.ReadInt32(); // zero (lod meshes)
            int unknown2 = reader.ReadInt32(); // zero (dumb chunks)
            int numColSpheres = reader.ReadInt32();

            Logger.Debug(logSrc, $"Header → numSubmeshes={numSubmeshes}, numAllMaterials={numAllMaterials}, numColSpheres={numColSpheres}");

            var mesh = new Mesh();
            int submeshOrdinal = 0;

            // Section parser
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                long sectionHeaderPos = reader.BaseStream.Position;
                int secType = reader.ReadInt32();
                int secSize = reader.ReadInt32();
                long secBodyStart = reader.BaseStream.Position;

                Logger.Debug(logSrc, $"Section @0x{sectionHeaderPos:X}: Type=0x{secType:X8}, Size={secSize}");

                if (secType == SECTION_END)
                {
                    Logger.Debug(logSrc, "Encountered SECTION_END; breaking out of section loop.");
                    break;
                }
                else if (secType == SECTION_SUBMESH)
                {
                    Logger.Debug(logSrc, "Found SUBMESH section; parsing submesh → multiple LOD-Brushes");
                    var lodBrushes = ParseSubmeshAsBrushes(reader, submeshOrdinal++);
                    foreach (var b in lodBrushes)
                    {
                        b.UID = mesh.Brushes.Count; // sequential UIDs for LOD level brushes
                        mesh.Brushes.Add(b);
                    }
                }
                else if (secType == SECTION_CSPHERE)
                {
                    Logger.Debug(logSrc, "Found CSPHERE section; parsing.");
                    var cs = ParseCsphere(reader, secSize);
                    mesh.CollisionSpheres.Add(cs);
                }
                else if (secType == SECTION_BONES)
                {
                    Logger.Debug(logSrc, "Found BONES section; parsing skeleton.");
                    ParseBones(reader, mesh, secSize);
                    ComputeBoneWorldPositions(mesh);
                    NormalizeBonePositions(mesh);
                }
                else
                {
                    // skip any other chunk
                    Logger.Debug(logSrc, $"Unknown/ignored section 0x{secType:X8}, skipping its {secSize} bytes.");
                    reader.BaseStream.Seek(secBodyStart + secSize, SeekOrigin.Begin);
                }
            }

            Logger.Debug(logSrc, $"Finished reading V3M/V3C file. Total brushes parsed: {mesh.Brushes.Count}");

            return mesh;
        }

        private static List<Brush> ParseSubmeshAsBrushes(BinaryReader reader, int submeshIndex)
        {
            Logger.Debug(logSrc, "Entering ParseSubmeshAsBrushes(...)");
            var brushes = new List<Brush>();

            string submeshName = ReadFixedAscii(reader, 24);
            string parentName = ReadFixedAscii(reader, 24);
            Logger.Debug(logSrc, $"Submesh name = \"{submeshName}\", parent = \"{parentName}\"");

            int submeshVersion = reader.ReadInt32(); // typically “7”
            int numLods = reader.ReadInt32();
            Logger.Debug(logSrc, $"Submesh version={submeshVersion}, numLods={numLods}");

            float[] lodDistances = new float[numLods];
            for (int i = 0; i < numLods; i++)
            {
                lodDistances[i] = reader.ReadSingle();
                Logger.Debug(logSrc, $"  lodDistance[{i}] = {lodDistances[i]}");
            }

            Vector3 offset = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float radius = reader.ReadSingle();
            Vector3 bboxMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 bboxMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Logger.Debug(logSrc, $"Offset={offset}, Radius={radius}, AABB_min={bboxMin}, AABB_max={bboxMax}");

            var allLods = new LodMesh[numLods];
            for (int lodIdx = 0; lodIdx < numLods; lodIdx++)
            {
                Logger.Debug(logSrc, $"Parsing LOD {lodIdx + 1}/{numLods}");
                var lod = ParseLodMesh(reader);
                allLods[lodIdx] = lod;
            }

            int numMaterials = reader.ReadInt32();
            Logger.Debug(logSrc, $"numMaterials = {numMaterials}");

            // Collect diffuse_map_name for each material
            var submeshMaterials = new List<string>(numMaterials);
            var submeshMaterialProps = new List<V3mMaterialProps>(numMaterials);
            for (int mi = 0; mi < numMaterials; mi++)
            {
                string diffuse = ReadFixedAscii(reader, 32).TrimEnd();
                float emissive = reader.ReadSingle();
                float specular = reader.ReadSingle();
                float glossiness = reader.ReadSingle();
                float reflection = reader.ReadSingle();
                string reflMap = ReadFixedAscii(reader, 32).TrimEnd();
                uint matFlags = reader.ReadUInt32();

                Logger.Debug(logSrc,
                    $"  Material[{mi}]: diffuse=\"{diffuse}\", emissive={emissive}, specular={specular}, glossiness={glossiness}, reflection={reflection}, reflMap=\"{reflMap}\", flags=0x{matFlags:X8}");

                submeshMaterials.Add(diffuse);
                submeshMaterialProps.Add(new V3mMaterialProps
                {
                    Emissive = emissive,
                    Specular = specular,
                    Glossiness = glossiness,
                    Reflection = reflection,
                    ReflectionMap = reflMap,
                    Flags = matFlags
                });
            }

            int numUnknown1 = reader.ReadInt32();
            Logger.Debug(logSrc, $"numUnknown1 = {numUnknown1}, skipping {numUnknown1 * 28} bytes");
            reader.BaseStream.Seek(numUnknown1 * 28, SeekOrigin.Current);

            // One brush per LOD
            for (int lodIdx = 0; lodIdx < numLods; lodIdx++)
            {
                var lod = allLods[lodIdx];
                var brush = new Brush();
                brush.Solid = new Solid();

                brush.TextureName = $"{submeshName}_LOD{lodIdx}";
                brush.LodDistance = lodIdx < lodDistances.Length ? lodDistances[lodIdx] : (float?)null;
                brush.SubmeshParent = parentName;
                brush.SubmeshIndex = submeshIndex;
                brush.LodFlags = lod.Flags;
                brush.SubmeshOffset = offset;
                if (lod.Textures != null && lod.Textures.Length > 0)
                {
                    brush.LodTextures = lod.Textures
                        .Select(t => new V3mLodTexture { Slot = t.Id, Name = t.Filename ?? string.Empty })
                        .ToList();
                }
                brush.Vertices = new List<Vector3>();
                brush.UVs = new List<Vector2>();
                brush.Indices = new List<int>();
                brush.PropPoints = new List<redux.utilities.PropPoint>();

                // Prop points are stored in MODEL space, like collision spheres: RF's static-mesh prop
                // transform copies the position straight from the file without adding the submesh
                // offset. Only vertices and triangle planes are offset-relative.
                foreach (var pp in lod.PropPoints)
                {
                    brush.PropPoints.Add(new redux.utilities.PropPoint
                    {
                        Name = pp.Name,
                        Position = pp.Position,
                        Orientation = pp.Orientation,
                        ParentIndex = pp.ParentIndex
                    });
                }

                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var faces = new List<Face>();
                var indices = new List<int>();
                var jointIndices = new List<Vector4>();
                var jointWeights = new List<Vector4>();

                for (int ci = 0; ci < lod.NumChunks; ci++)
                {
                    var info = lod.ChunkInfos[ci];
                    var chunkData = lod.Chunks[ci];
                    int baseIndex = positions.Count;

                    Logger.Debug(logSrc,
                        $"  BUILDING Brush for LOD#{lodIdx}, Chunk[{ci}]: Pos={chunkData.Positions.Length}, UVs={chunkData.UVs.Length}, Triangles={chunkData.Triangles.Length}");

                    // Vertices are stored in submesh-local space; apply the submesh offset to place them in model space.
                    foreach (var pos in chunkData.Positions)
                        positions.Add(pos + offset);

                    // Keep the authored per-vertex normals so exporters don't have to fall back to
                    // flat face normals (which would split every shared vertex on re-export).
                    for (int vi = 0; vi < chunkData.Positions.Length; vi++)
                        normals.Add(vi < chunkData.Normals.Length ? chunkData.Normals[vi] : Vector3.Zero);

                    for (int vi = 0; vi < chunkData.Positions.Length; vi++)
                        uvs.Add(vi < chunkData.UVs.Length ? chunkData.UVs[vi] : Vector2.Zero);

                    for (int vi = 0; vi < chunkData.Positions.Length; vi++)
                    {
                        if (chunkData.BoneLinks != null && chunkData.BoneLinks.Length > 0)
                        {
                            var link = chunkData.BoneLinks[vi];
                            // byte[4] each for Weights and Bones
                            float w0 = link.Weights[0] / 255f;
                            float w1 = link.Weights[1] / 255f;
                            float w2 = link.Weights[2] / 255f;
                            float w3 = link.Weights[3] / 255f;
                            float sum = w0 + w1 + w2 + w3;
                            if (sum > 1e-6f)
                            {
                                w0 /= sum;
                                w1 /= sum;
                                w2 /= sum;
                                w3 /= sum;
                            }
                            else
                            {
                                w0 = 1f;
                                w1 = 0f;
                                w2 = 0f;
                                w3 = 0f;
                            }
                            jointWeights.Add(new Vector4(w0, w1, w2, w3));

                            float j0 = (link.Bones[0] == 0xFF || link.Weights[0] == 0) ? 0 : link.Bones[0];
                            float j1 = (link.Bones[1] == 0xFF || link.Weights[1] == 0) ? 0 : link.Bones[1];
                            float j2 = (link.Bones[2] == 0xFF || link.Weights[2] == 0) ? 0 : link.Bones[2];
                            float j3 = (link.Bones[3] == 0xFF || link.Weights[3] == 0) ? 0 : link.Bones[3];
                            jointIndices.Add(new Vector4(j0, j1, j2, j3));
                        }
                        else
                        {
                            // No bone‐link info → put a “zero” influence on bone 0
                            jointWeights.Add(new Vector4(1, 0, 0, 0));
                            jointIndices.Add(new Vector4(0, 0, 0, 0));
                        }
                    }

                    // Only the first info.NumFaces triangles are valid; faces_alloc can over-allocate (trailing entries are unused buffer slack).
                    int numValidFaces = Math.Min(info.NumFaces, chunkData.Triangles.Length);
                    for (int f = 0; f < numValidFaces; f++)
                    {
                        var tri = chunkData.Triangles[f];
                        int i0 = baseIndex + tri.I0;
                        int i1 = baseIndex + tri.I1;
                        int i2 = baseIndex + tri.I2;
                        ushort ff = tri.Flags;
                        int textureIdxRaw = lod.ChunkHeaders[ci];
                        int textureIdx = ResolveChunkTextureIndex(textureIdxRaw, lod, submeshMaterials);

                        var face = new Face
                        {
                            TextureIndex = textureIdx,
                            RenderFlags = info.RenderFlags,
                            Vertices = new List<int> { i0, i1, i2 },
                            UVs = new List<Vector2>
                            {
                                uvs[baseIndex + tri.I0],
                                uvs[baseIndex + tri.I1],
                                uvs[baseIndex + tri.I2]
                            },
                            FaceFlags = ff
                        };

                        faces.Add(face);
                        indices.Add(i0);
                        indices.Add(i1);
                        indices.Add(i2);
                    }
                }

                brush.Vertices = positions;
                brush.Normals = normals;
                brush.UVs = uvs;
                brush.Indices = indices;
                brush.Solid.Faces = faces;
                brush.JointIndices = jointIndices;
                brush.JointWeights = jointWeights;

                // Names are kept verbatim (extensions included: .vbm animated textures must survive).
                // The LOD texture reference table travels separately on brush.LodTextures.
                foreach (var matName in submeshMaterials)
                    brush.Solid.Textures.Add(matName);

                if (submeshMaterialProps.Count > 0)
                {
                    brush.Solid.MaterialProps = submeshMaterialProps
                        .Select(p => new V3mMaterialProps
                        {
                            Emissive = p.Emissive,
                            Specular = p.Specular,
                            Glossiness = p.Glossiness,
                            Reflection = p.Reflection,
                            ReflectionMap = p.ReflectionMap,
                            Flags = p.Flags
                        })
                        .ToList();
                }

                brushes.Add(brush);
                Logger.Debug(logSrc,
                    $"  → Created Brush “{brush.TextureName}”: Vertices={positions.Count}, Faces={faces.Count}, Materials={submeshMaterials.Count}");
            }

            Logger.Debug(logSrc, "Exiting ParseSubmeshAsBrushes(...)");
            return brushes;
        }

        private static int ResolveChunkTextureIndex(int textureIdxRaw, LodMesh lod, List<string> submeshMaterials)
        {
            if (submeshMaterials.Count == 0)
                return 0;

            // Most files use chunk texture index as an index into LOD texture refs.
            int slotFromDirectRef = ResolveTextureRefToMaterialSlot(textureIdxRaw, lod, submeshMaterials);
            if (slotFromDirectRef >= 0)
                return slotFromDirectRef;

            // Some tools appear to store 1-based chunk texture indices.
            int slotFromOneBasedRef = ResolveTextureRefToMaterialSlot(textureIdxRaw - 1, lod, submeshMaterials);
            if (slotFromOneBasedRef >= 0)
                return slotFromOneBasedRef;

            // Fallbacks when chunk header points straight at material slots.
            if (textureIdxRaw >= 0 && textureIdxRaw < submeshMaterials.Count)
                return textureIdxRaw;
            if (textureIdxRaw > 0 && (textureIdxRaw - 1) < submeshMaterials.Count)
                return textureIdxRaw - 1;

            Logger.Warn(logSrc, $"Unresolved chunk texture index {textureIdxRaw}; defaulting to material slot 0.");
            return 0;
        }

        private static int ResolveTextureRefToMaterialSlot(int textureRefIndex, LodMesh lod, List<string> submeshMaterials)
        {
            if (lod.Textures == null || textureRefIndex < 0 || textureRefIndex >= lod.Textures.Length)
                return -1;

            LodTexture texRef = lod.Textures[textureRefIndex];

            // In many files this is the direct 0-based material slot index.
            int id = texRef.Id;
            if (id >= 0 && id < submeshMaterials.Count)
                return id;

            // Some content appears to use 1-based IDs.
            if (id > 0 && (id - 1) < submeshMaterials.Count)
                return id - 1;

            // As a final fallback, match by texture filename.
            int byName = FindMaterialSlotByTextureName(texRef.Filename, submeshMaterials);
            return byName;
        }

        private static int FindMaterialSlotByTextureName(string? textureName, List<string> submeshMaterials)
        {
            string wanted = NormalizeTextureName(textureName);
            for (int i = 0; i < submeshMaterials.Count; i++)
            {
                string candidate = NormalizeTextureName(submeshMaterials[i]);
                if (string.Equals(wanted, candidate, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string NormalizeTextureName(string? textureName)
        {
            if (string.IsNullOrWhiteSpace(textureName))
                return string.Empty;

            string file = Path.GetFileName(textureName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(file))
                file = textureName;

            string noExt = Path.GetFileNameWithoutExtension(file);
            return noExt ?? string.Empty;
        }

        private static LodMesh ParseLodMesh(BinaryReader reader)
        {
            Logger.Debug(logSrc, "  -> Entering ParseLodMesh(...)");

            var lod = new LodMesh();
            lod.Flags = reader.ReadUInt32();
            lod.NumVertices = reader.ReadInt32();
            lod.NumChunks = reader.ReadUInt16();
            int dataBlockSize = reader.ReadInt32();

            Logger.Debug(logSrc, $"    Flags=0x{lod.Flags:X8}, NumVertices={lod.NumVertices}, NumChunks={lod.NumChunks}, DataBlockSize={dataBlockSize}");

            lod.DataBlock = reader.ReadBytes(dataBlockSize);
            lod.Unknown1 = reader.ReadInt32();
            Logger.Debug(logSrc, $"    Unknown1 = {lod.Unknown1}");

            lod.ChunkInfos = new ChunkInfo[lod.NumChunks];
            for (int i = 0; i < lod.NumChunks; i++)
            {
                var ci = new ChunkInfo
                {
                    NumVertices = reader.ReadUInt16(),
                    NumFaces = reader.ReadUInt16(),
                    VecsAlloc = reader.ReadUInt16(),
                    FacesAlloc = reader.ReadUInt16(),
                    SamePosVertexOffsetsAlloc = reader.ReadUInt16(),
                    WiAlloc = reader.ReadUInt16(),
                    UvsAlloc = reader.ReadUInt16(),
                    RenderFlags = reader.ReadUInt32()
                };
                lod.ChunkInfos[i] = ci;
                Logger.Debug(logSrc, $"    ChunkInfo[{i}]: NumVertices={ci.NumVertices}, NumFaces={ci.NumFaces}, VecsAlloc={ci.VecsAlloc}, FacesAlloc={ci.FacesAlloc}, SamePosOffsetAlloc={ci.SamePosVertexOffsetsAlloc}, WiAlloc={ci.WiAlloc}, UvsAlloc={ci.UvsAlloc}, RenderFlags=0x{ci.RenderFlags:X8}");
            }

            lod.NumPropPoints = reader.ReadInt32();
            lod.NumTextures = reader.ReadInt32();
            Logger.Debug(logSrc, $"    NumPropPoints={lod.NumPropPoints}, NumTextures={lod.NumTextures}");

            lod.Textures = new LodTexture[lod.NumTextures];
            for (int t = 0; t < lod.NumTextures; t++)
            {
                var lt = new LodTexture
                {
                    Id = reader.ReadByte(),
                    Filename = ReadZeroTerminatedAscii(reader)
                };
                lod.Textures[t] = lt;
                Logger.Debug(logSrc, $"    LodTexture[{t}]: Id={lt.Id}, Filename=\"{lt.Filename}\"");
            }

            UnpackLodDataBlock(lod);

            Logger.Debug(logSrc, "  <- Exiting ParseLodMesh(...)");
            return lod;
        }

        private static void UnpackLodDataBlock(LodMesh lod)
        {
            Logger.Debug(logSrc, "    -> Entering UnpackLodDataBlock(...)");
            using var ms = new MemoryStream(lod.DataBlock);
            using var r = new BinaryReader(ms);

            lod.ChunkHeaders = new int[lod.NumChunks];
            for (int ci = 0; ci < lod.NumChunks; ci++)
            {
                r.ReadBytes(0x20);
                int texIdx = r.ReadInt32();
                r.ReadBytes(0x14);
                lod.ChunkHeaders[ci] = texIdx;
                Logger.Debug(logSrc, $"      ChunkHeader[{ci}] → textureIdx={texIdx}");
            }

            // align to 0x10
            long pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
            if (pad > 0)
            {
                Logger.Debug(logSrc, $"      Skipping {pad} bytes of padding after chunk headers.");
                r.ReadBytes((int)pad);
            }

            lod.Chunks = new ChunkData[lod.NumChunks];
            for (int ci = 0; ci < lod.NumChunks; ci++)
            {
                var info = lod.ChunkInfos[ci];
                var cd = new ChunkData();

                Logger.Debug(logSrc, $"      → Unpacking ChunkData[{ci}]: VecsAlloc={info.VecsAlloc}, UvsAlloc={info.UvsAlloc}, FacesAlloc={info.FacesAlloc}, SamePosOffsetAlloc={info.SamePosVertexOffsetsAlloc}, WiAlloc={info.WiAlloc}");

                // positions — only num_vertices entries are real; the alloc size can be padded/over-allocated,
                // so read the valid element count but advance the stream by the full alloc.
                int numPos = Math.Min(info.NumVertices, Math.Max(0, info.VecsAlloc / 12));
                long blockStart = ms.Position;
                cd.Positions = new Vector3[numPos];
                for (int i = 0; i < numPos; i++)
                {
                    cd.Positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                ms.Position = blockStart + info.VecsAlloc;
                pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                if (pad > 0) r.ReadBytes((int)pad);
                Logger.Debug(logSrc, $"        Read {numPos} positions, skipped {pad} bytes padding.");

                // normals (same element count and alloc size as positions)
                blockStart = ms.Position;
                cd.Normals = new Vector3[numPos];
                for (int i = 0; i < numPos; i++)
                {
                    cd.Normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                ms.Position = blockStart + info.VecsAlloc;
                pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                if (pad > 0) r.ReadBytes((int)pad);
                Logger.Debug(logSrc, $"        Read {numPos} normals, skipped {pad} bytes padding.");

                // uvs
                int numUvs = Math.Min(info.NumVertices, Math.Max(0, info.UvsAlloc / 8));
                blockStart = ms.Position;
                cd.UVs = new Vector2[numUvs];
                for (int i = 0; i < numUvs; i++)
                {
                    cd.UVs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                }
                ms.Position = blockStart + info.UvsAlloc;
                pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                if (pad > 0) r.ReadBytes((int)pad);
                Logger.Debug(logSrc, $"        Read {numUvs} UVs, skipped {pad} bytes padding.");

                // faces
                int numFaces = info.FacesAlloc / 8;
                cd.Triangles = new Triangle[numFaces];
                for (int i = 0; i < numFaces; i++)
                {
                    cd.Triangles[i].I0 = r.ReadUInt16();
                    cd.Triangles[i].I1 = r.ReadUInt16();
                    cd.Triangles[i].I2 = r.ReadUInt16();
                    cd.Triangles[i].Flags = r.ReadUInt16();
                }
                pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                if (pad > 0) r.ReadBytes((int)pad);
                Logger.Debug(logSrc, $"        Read {numFaces} triangles, skipped {pad} bytes padding.");

                // optional planes — count is num_faces (NOT faces_alloc/8); the face buffer can be over-allocated
                if ((lod.Flags & (uint)LodFlags.TrianglePlanes) != 0)
                {
                    int numPlanes = info.NumFaces;
                    cd.Planes = new RFPlane[numPlanes];
                    for (int i = 0; i < numPlanes; i++)
                    {
                        cd.Planes[i].Normal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        cd.Planes[i].Dist = r.ReadSingle();
                    }
                    pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                    if (pad > 0) r.ReadBytes((int)pad);
                    Logger.Debug(logSrc, $"        Read {numPlanes} planes, skipped {pad} bytes padding.");
                }
                else
                {
                    cd.Planes = Array.Empty<RFPlane>();
                }

                // same_pos_vertex_offsets — only num_vertices entries are real; the rest of the
                // allocation is slack RED reserves to round the vertex count up to a multiple of 4.
                int numOffsets = Math.Min(info.NumVertices, Math.Max(0, info.SamePosVertexOffsetsAlloc / 2));
                blockStart = ms.Position;
                cd.SamePosVertexOffsets = new short[numOffsets];
                for (int i = 0; i < numOffsets; i++)
                    cd.SamePosVertexOffsets[i] = r.ReadInt16();
                ms.Position = blockStart + info.SamePosVertexOffsetsAlloc;
                pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                if (pad > 0) r.ReadBytes((int)pad);
                Logger.Debug(logSrc, $"        Read {numOffsets} same_pos offsets, skipped {pad} bytes padding.");

                // optional bone_links
                if (info.WiAlloc > 0)
                {
                    int numWeights = info.WiAlloc / 8;
                    cd.BoneLinks = new VertexBoneLink[numWeights];
                    for (int i = 0; i < numWeights; i++)
                    {
                        var vbl = new VertexBoneLink
                        {
                            Weights = new byte[4],
                            Bones = new byte[4]
                        };
                        for (int w = 0; w < 4; w++)
                            vbl.Weights[w] = r.ReadByte();
                        for (int b = 0; b < 4; b++)
                            vbl.Bones[b] = r.ReadByte();
                        cd.BoneLinks[i] = vbl;
                    }
                    pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                    if (pad > 0) r.ReadBytes((int)pad);
                }
                else
                {
                    cd.BoneLinks = Array.Empty<VertexBoneLink>();
                }

                // optional orig_map
                if ((lod.Flags & (uint)LodFlags.OrigMap) != 0)
                {
                    int numOrig = lod.NumVertices;
                    cd.OrigMap = new short[numOrig];
                    for (int i = 0; i < numOrig; i++)
                        cd.OrigMap[i] = r.ReadInt16();
                    pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
                    if (pad > 0) r.ReadBytes((int)pad);
                    Logger.Debug(logSrc, $"        Read {numOrig} orig_map entries, skipped {pad} bytes padding.");
                }
                else
                {
                    cd.OrigMap = Array.Empty<short>();
                    Logger.Debug(logSrc, $"        No orig_map (OrigMap flag not set).");
                }

                lod.Chunks[ci] = cd;
            }

            if (lod.NumPropPoints > 0)
            {
                lod.PropPoints = new PropPoint[lod.NumPropPoints];
                for (int p = 0; p < lod.NumPropPoints; p++)
                {
                    var pp = new PropPoint
                    {
                        Name = ReadFixedAscii(r, 0x44), // 68 bytes, strz
                        Orientation = new Quaternion(
                            r.ReadSingle(),
                            r.ReadSingle(),
                            r.ReadSingle(),
                            r.ReadSingle()),
                        Position = new Vector3(
                            r.ReadSingle(),
                            r.ReadSingle(),
                            r.ReadSingle()),
                        ParentIndex = r.ReadInt32()
                    };
                    lod.PropPoints[p] = pp;
                    Logger.Debug(logSrc, $"        PropPoint[{p}]: Name=\"{pp.Name}\", Position={pp.Position}, ParentIndex={pp.ParentIndex}");
                }
            }
            else
            {
                lod.PropPoints = Array.Empty<PropPoint>();
                Logger.Debug(logSrc, $"        No prop_points in data_block (NumPropPoints=0).");
            }

            Logger.Debug(logSrc, "    <- Exiting UnpackLodDataBlock(...)");
        }

        private static CollisionSphere ParseCsphere(BinaryReader reader, int secSize)
        {
            var cs = new CollisionSphere();
            cs.Name = ReadFixedAscii(reader, 24);
            cs.ParentIndex = reader.ReadInt32();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            cs.Position = new Vector3(x, y, z);
            cs.Radius = reader.ReadSingle();

            // Advance if there is leftover padding
            long bytesRead = 24 + 4 + 12 + 4; // 44 bytes total
            long toSkip = secSize - bytesRead;
            if (toSkip > 0)
                reader.BaseStream.Seek(toSkip, SeekOrigin.Current);

            return cs;
        }

        private static void ParseBones(BinaryReader reader, Mesh mesh, int secSize)
        {
            long startPos = reader.BaseStream.Position;
            int numBones = reader.ReadInt32();
            Logger.Debug(logSrc, $"  → numBones = {numBones}");

            for (int i = 0; i < numBones; i++)
            {
                string boneName = ReadFixedAscii(reader, 24);

                float qx = reader.ReadSingle();
                float qy = reader.ReadSingle();
                float qz = reader.ReadSingle();
                float qw = reader.ReadSingle();
                var baseRot = new Quaternion(qx, qy, qz, qw);
                baseRot = Quaternion.Normalize(baseRot);

                float tx = reader.ReadSingle();
                float ty = reader.ReadSingle();
                float tz = reader.ReadSingle();
                var baseTrans = new Vector3(tx, ty, tz);

                int parentIndex = reader.ReadInt32();

                var bone = new Bone
                {
                    Name = boneName,
                    BaseRotation = baseRot,
                    BaseTranslation = baseTrans,
                    ParentIndex = parentIndex
                };
                mesh.Bones.Add(bone);

                Logger.Debug(logSrc,
                    $"    Bone[{i}]: Name=\"{boneName}\", Parent={parentIndex}, Rot=({qx},{qy},{qz},{qw}), Trans=({tx},{ty},{tz})");
            }

            // Now compute and normalize RH‐corrected positions:
            ComputeBoneWorldPositions(mesh);
            NormalizeBonePositions(mesh);

            long consumed = reader.BaseStream.Position - startPos;
            long toSkip = secSize - consumed;
            if (toSkip > 0)
                reader.BaseStream.Seek(toSkip, SeekOrigin.Current);
        }

        public static void ComputeBoneWorldPositions(Mesh mesh)
        {
            int count = mesh.Bones.Count;
            mesh.BoneWorldPositions = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                Bone b = mesh.Bones[i];

                // 1) Convert RF quaternion → RH quaternion by flipping X
                // flip handedness
                Quaternion q_rf = b.BaseRotation;
                var q_rh = new Quaternion(
                    -q_rf.X,   // flip X
                     q_rf.Y,
                     q_rf.Z,
                     q_rf.W
                );
                q_rh = Quaternion.Normalize(q_rh);

                // 2) Convert RF translation → RH translation by flipping X
                Vector3 t_rf = b.BaseTranslation;
                var t_rh = new Vector3(
                    -t_rf.X,   // flip X
                     t_rf.Y,
                     t_rf.Z
                );

                // 3) Compute bone‐origin in world (RH) = -( R_rh^{-1} * t_rh )
                //    Since R_rh is normalized, R_rh^{-1} == Quaternion.Conjugate(R_rh)
                Quaternion invRot = Quaternion.Conjugate(q_rh);
                Vector3 rotated = Vector3.Transform(t_rh, invRot);
                Vector3 worldOrig = -rotated;

                mesh.BoneWorldPositions[i] = worldOrig;
            }
        }

        public static void NormalizeBonePositions(Mesh mesh)
        {
            // Find the root bone (ParentIndex < 0)
            int rootIndex = -1;
            for (int i = 0; i < mesh.Bones.Count; i++)
            {
                if (mesh.Bones[i].ParentIndex < 0)
                {
                    rootIndex = i;
                    break;
                }
            }

            if (rootIndex < 0)
            {
                Logger.Warn(logSrc, "No root bone found; skipping normalization.");
                return;
            }

            Vector3 rootPos = mesh.BoneWorldPositions[rootIndex];
            int count = mesh.Bones.Count;
            mesh.BoneModelPositions = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                mesh.BoneModelPositions[i] = mesh.BoneWorldPositions[i] - rootPos;
            }
        }
    }
}
