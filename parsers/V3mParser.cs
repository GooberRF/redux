﻿using redux.parsers.parser_utils;
using redux.utilities;
using System;
using System.Collections.Generic;
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

		private const int V3M_SIGNATURE = 0x52463344;	// 'RF3D'
		private const int V3C_SIGNATURE = 0x5246434D;	// 'RFCM'
		private const int V3D_VERSION = 0x40000;

		private const int SECTION_END = 0x00000000;
		private const int SECTION_SUBMESH = 0x5355424D; // 'SUBM'
		private const int SECTION_CSPHERE = 0x43535048; // 'CSPH'
		private const int SECTION_BONES = 0x424F4E45;	// 'BONE'

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
					Logger.Debug(logSrc, "Found SUBMESH section; parsing submesh → multiple LOD‑Brushes");
					var lodBrushes = ParseSubmeshAsBrushes(reader);
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

		private static List<Brush> ParseSubmeshAsBrushes(BinaryReader reader)
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
				brush.Vertices = new List<Vector3>();
				brush.UVs = new List<Vector2>();
				brush.Indices = new List<int>();
				brush.PropPoints = new List<redux.utilities.PropPoint>();

				// Copy prop points
				foreach (var pp in lod.PropPoints)
				{
					brush.PropPoints.Add(new redux.utilities.PropPoint
					{
						Name = pp.Name,
						Position = pp.Position,
						Orientation = pp.Orientation
					});
				}

				var positions = new List<Vector3>();
				var uvs = new List<Vector2>();
				var faces = new List<Face>();
				var indices = new List<int>();

				for (int ci = 0; ci < lod.NumChunks; ci++)
				{
					var info = lod.ChunkInfos[ci];
					var chunkData = lod.Chunks[ci];
					int baseIndex = positions.Count;

					Logger.Debug(logSrc,
						$"  BUILDING Brush for LOD#{lodIdx}, Chunk[{ci}]: Pos={chunkData.Positions.Length}, UVs={chunkData.UVs.Length}, Triangles={chunkData.Triangles.Length}");

					foreach (var pos in chunkData.Positions)
						positions.Add(pos);

					foreach (var uv in chunkData.UVs)
						uvs.Add(uv);

					for (int f = 0; f < chunkData.Triangles.Length; f++)
					{
						var tri = chunkData.Triangles[f];
						int i0 = baseIndex + tri.I0;
						int i1 = baseIndex + tri.I1;
						int i2 = baseIndex + tri.I2;
						ushort ff = tri.Flags;

						int textureIdx = lod.ChunkHeaders[ci];

						var face = new Face
						{
							TextureIndex = textureIdx,
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
				brush.UVs = uvs;
				brush.Indices = indices;
				brush.Solid.Faces = faces;

				foreach (var matName in submeshMaterials)
					brush.Solid.Textures.Add(matName);

				brushes.Add(brush);
				Logger.Debug(logSrc,
					$"  → Created Brush “{brush.TextureName}”: Vertices={positions.Count}, Faces={faces.Count}, Materials={submeshMaterials.Count}");
			}

			Logger.Debug(logSrc, "Exiting ParseSubmeshAsBrushes(...)");
			return brushes;
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
				lod.ChunkHeaders[ci] = (texIdx);
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
				
				// positions
				int numPos = info.VecsAlloc / 12;
				cd.Positions = new Vector3[numPos];
				for (int i = 0; i < numPos; i++)
				{
					cd.Positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				}
				pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
				if (pad > 0) r.ReadBytes((int)pad);
				Logger.Debug(logSrc, $"        Read {numPos} positions, skipped {pad} bytes padding.");

				// normals
				cd.Normals = new Vector3[numPos];
				for (int i = 0; i < numPos; i++)
				{
					cd.Normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				}
				pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
				if (pad > 0) r.ReadBytes((int)pad);
				Logger.Debug(logSrc, $"        Read {numPos} normals, skipped {pad} bytes padding.");

				// uvs
				int numUvs = info.UvsAlloc / 8;
				cd.UVs = new Vector2[numUvs];
				for (int i = 0; i < numUvs; i++)
				{
					cd.UVs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
				}
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

				// optional planes
				if ((lod.Flags & (uint)LodFlags.TrianglePlanes) != 0)
				{
					cd.Planes = new RFPlane[numFaces];
					for (int i = 0; i < numFaces; i++)
					{
						cd.Planes[i].Normal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						cd.Planes[i].Dist = r.ReadSingle();
					}
					pad = (0x10L - (ms.Position % 0x10L)) % 0x10L;
					if (pad > 0) r.ReadBytes((int)pad);
					Logger.Debug(logSrc, $"        Read {numFaces} planes, skipped {pad} bytes padding.");
				}
				else
				{
					cd.Planes = Array.Empty<RFPlane>();
				}

				// same_pos_vertex_offsets
				int numOffsets = info.SamePosVertexOffsetsAlloc / 2;
				cd.SamePosVertexOffsets = new short[numOffsets];
				for (int i = 0; i < numOffsets; i++)
					cd.SamePosVertexOffsets[i] = r.ReadInt16();
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
					Logger.Debug(logSrc, $"        Read {numWeights} VertexBoneLinks, skipped {pad} bytes padding.");
				}
				else
				{
					cd.BoneLinks = Array.Empty<VertexBoneLink>();
					Logger.Debug(logSrc, $"        No bone links (WiAlloc=0).");
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
			Logger.Debug(logSrc, $"  → num_bones = {numBones}");

			for (int i = 0; i < numBones; i++)
			{
				string boneName = ReadFixedAscii(reader, 24);

				float qx = reader.ReadSingle();
				float qy = reader.ReadSingle();
				float qz = reader.ReadSingle();
				float qw = reader.ReadSingle();
				var baseRot = new Quaternion(qx, qy, qz, qw);

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
				  $"    Bone[{i}]: Name=\"{boneName}\", Parent={parentIndex}, " +
				  $"Rot=({qx},{qy},{qz},{qw}), Trans=({tx},{ty},{tz})");
			}

			// Advance past any padding
			long bytesConsumed = reader.BaseStream.Position - startPos;
			long toSkip = secSize - bytesConsumed;
			if (toSkip > 0)
			{
				reader.BaseStream.Seek(toSkip, SeekOrigin.Current);
			}
		}
	}
}
