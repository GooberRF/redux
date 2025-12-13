using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using redux.utilities;

namespace redux.parsers
{
	public static class RfmParser
	{
		private const uint RFC_MAGIC = 0x87128712;
		private const uint RFC_VERSION = 0x114;

		public static Mesh ReadRfm(string path)
		{
			Logger.Info(nameof(RfmParser), $"Loading RFM/RFC file: {path}");
			using var fs = File.OpenRead(path);
			using var reader = new BinaryReader(fs);

			// 1) signature & version
			uint sig = reader.ReadUInt32();
			Logger.Dev(nameof(RfmParser), $"Signature=0x{sig:X8}");
			if (sig != RFC_MAGIC)
				throw new InvalidDataException($"Not an RFM/RFC file (sig=0x{sig:X8})");

			uint version = reader.ReadUInt32();
			Logger.Dev(nameof(RfmParser), $"Version=0x{version:X}");
			if (version != RFC_VERSION)
				throw new InvalidDataException($"Unsupported RFC version 0x{version:X}");

			// 2) skeletal flag
			byte hasBones = reader.ReadByte();
			Logger.Dev(nameof(RfmParser), $"HasBones={(hasBones != 0)}");

			var mesh = new Mesh();

			// 3) read Lst1
			int n1 = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"Lst1.Count={n1}");
			for (int i = 0; i < n1; i++)
			{
				string name = Utils.ReadZeroTerminatedAscii(reader);
				int fld18 = reader.ReadInt32();
				var pos1 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				float fld28 = reader.ReadSingle();
				Logger.Dev(nameof(RfmParser), $"Lst1[{i}] Name={name}, fld18={fld18}, Pos={pos1}, fld28={fld28}");
			}

			// 4) read Lst2
			int n2 = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"Lst2.Count={n2}");
			for (int i = 0; i < n2; i++)
			{
				string name = Utils.ReadZeroTerminatedAscii(reader);
				int fld40 = reader.ReadInt32();
				var pos2 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				var f50 = (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				Logger.Dev(nameof(RfmParser), $"Lst2[{i}] Name={name}, fld40={fld40}, Pos={pos2}, F50={f50}");
			}

			// 5) bones
			int boneCount = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"BoneCount={boneCount}");
			if (hasBones != 0 && boneCount > 0)
			{
				for (int i = 0; i < boneCount; i++)
				{
					string boneName = Utils.ReadZeroTerminatedAscii(reader);
					uint parent = reader.ReadUInt32();
					for (int m = 0; m < 9; m++) reader.ReadSingle();
					var translation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					Logger.Dev(nameof(RfmParser), $"Bone[{i}] Name={boneName}, Parent={parent}, Translation={translation}");
					mesh.Bones.Add(new Bone { Name = boneName, BaseRotation = Quaternion.Identity, BaseTranslation = translation, ParentIndex = (int)parent });
				}
				V3mParser.ComputeBoneWorldPositions(mesh);
				V3mParser.NormalizeBonePositions(mesh);
			}

			// 6) objects & LOD layout
			int objNum = reader.ReadInt32();
			int lodCount = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"ObjNum={objNum}, LODCount={lodCount}");

			var lodHeap = new List<RfmLod>(objNum * lodCount);
			var objList = new List<RfmObject>(objNum);
			for (int i = 0; i < objNum; i++) objList.Add(new RfmObject());
			for (int i = 0; i < objNum * lodCount; i++) lodHeap.Add(new RfmLod());

			// 7) per-object floats
			int heapIdx = 0;
			for (int i = 0; i < objNum; i++)
			{
				int fltCount = reader.ReadInt32(); Logger.Dev(nameof(RfmParser), $"Obj[{i}].fltCount={fltCount}");
				for (int j = 0; j < fltCount; j++, heapIdx++)
				{
					objList[i].Lods.Add(lodHeap[heapIdx]);
					float f = reader.ReadSingle(); objList[i].Flts.Add(f);
					Logger.Dev(nameof(RfmParser), $" Obj[{i}].LOD[{j}].flt={f}");
				}
			}

			// 8) LOD names & IDs
			foreach (var obj in objList)
			{
				foreach (var lod in obj.Lods)
				{
					lod.Name1 = Utils.ReadZeroTerminatedAscii(reader);
					lod.Name2 = Utils.ReadZeroTerminatedAscii(reader);
					lod.Dword1 = reader.ReadUInt32(); lod.Dword2 = reader.ReadUInt32();
					Logger.Dev(nameof(RfmParser), $"LOD Name1={lod.Name1},Name2={lod.Name2},D1={lod.Dword1},D2={lod.Dword2}");
				}
			}

			// 9) parse each LOD’s geometry
			foreach (var lod in lodHeap) ParseRfmLod(reader, lod, mesh);

			// 10) flatten first-LOD
			int brushUid = 0;
			foreach (var obj in objList)
			{
				var lod = obj.Lods[0];
				foreach (var geom in lod.Geoms)
				{
					var brush = new Brush
					{
						UID = brushUid++,
						Position = lod.Pos,
						Solid = new Solid()
					};

					// **IMPORTANT**: pull in the texture names
					brush.Solid.Textures = new List<string>(lod.Textures);

					// copy verts
					brush.Vertices = geom.SimpleVertices
									  .Select(v => v + lod.Pos)
									  .ToList();

					// copy faces
					brush.Solid.Faces = geom.Triangles
						.Select(tr => new Face
						{
							TextureIndex = tr.Item1.VtxID >= 0 ? geom.TexID : tr.Item1.VtxID,
							Vertices = new List<int> { tr.Item1.VtxID, tr.Item2.VtxID, tr.Item3.VtxID },
							UVs = new List<Vector2> {
					new Vector2(tr.Item1.u, tr.Item1.v),
					new Vector2(tr.Item2.u, tr.Item2.v),
					new Vector2(tr.Item3.u, tr.Item3.v)
							}
						}).ToList();

					mesh.Brushes.Add(brush);
				}
			}

			return mesh;
		}

		private static void ParseRfmLod(BinaryReader reader, RfmLod lod, Mesh mesh)
		{
			Logger.Dev(nameof(RfmParser), $"  → Parsing LOD “{lod.Name1}”");

			// — textures —
			int texCount = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"    TexCount = {texCount}");
			for (int i = 0; i < texCount; i++)
			{
				string t = Utils.ReadZeroTerminatedAscii(reader);
				Logger.Dev(nameof(RfmParser), $"    Texture[{i}] = {t}");
				lod.Textures.Add(t);
			}

			// — skip the “t42” records —
			uint t42Count = reader.ReadUInt32();
			Logger.Dev(nameof(RfmParser), $"    t42Count = {t42Count}");
			for (uint i = 0; i < t42Count; i++)
			{
				Logger.Dev(nameof(RfmParser), $"      skipping t42[{i}]");
				// two dwords
				reader.ReadUInt32();
				reader.ReadUInt32();
				// Bt1 (we don't need to store it)
				reader.ReadByte();
				// skip the next 4 bytes
				reader.ReadByte();
				reader.ReadByte();
				reader.ReadByte();
				reader.ReadByte();
				// then skip one more dword
				reader.ReadUInt32();


				// two small “optional” blocks
				for (int k = 0; k < 2; k++)
				{
					byte present = reader.ReadByte();
					if (present != 0)
					{
						reader.ReadUInt32();
						// Python does readNstr here => 2-byte length + that many bytes
						Utils.ReadVString(reader);
						reader.ReadUInt32();
						reader.ReadSingle();
						reader.ReadUInt32();
					}
				}

				// skip the “throw‐away” count
				reader.ReadUInt32();
				// now read the real count of floats
				uint arr0Count = reader.ReadUInt32();
				for (uint k = 0; k < arr0Count; k++)
					reader.ReadSingle();

				// now the three bounding floats
				reader.ReadSingle();
				reader.ReadSingle();
				reader.ReadSingle();


				// one V-string
				Utils.ReadVString(reader);

				// another dword + float array
				reader.ReadUInt32();
				uint arr1 = reader.ReadUInt32();
				for (uint k = 0; k < arr1; k++)
					reader.ReadSingle();

				// one float
				reader.ReadSingle();

				// one more float array
				uint arr2 = reader.ReadUInt32();
				for (uint k = 0; k < arr2; k++)
					reader.ReadSingle();
			}

			// — now the real geometry —
			lod.NumGeom = reader.ReadInt32();
			Logger.Dev(nameof(RfmParser), $"    NumGeom = {lod.NumGeom}");
			for (int g = 0; g < lod.NumGeom; g++)
			{
				Logger.Dev(nameof(RfmParser), $"    Geom[{g}]:");
				var geom = new RfmGeom();

				// simple‐mesh branch (no bones)
				int vertCount = reader.ReadInt32();
				Logger.Dev(nameof(RfmParser), $"      VertCount = {vertCount}");
				for (int v = 0; v < vertCount; v++)
				{
					var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					var n = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					geom.SimpleVertices.Add(p);
					geom.Normals.Add(n);
				}

				int triCount = reader.ReadInt32();
				Logger.Dev(nameof(RfmParser), $"      TriCount = {triCount}");
				for (int t = 0; t < triCount; t++)
				{
					var f0 = new RfmFaceVtx(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
					var f1 = new RfmFaceVtx(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
					var f2 = new RfmFaceVtx(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
					geom.Triangles.Add((f0, f1, f2));
				}

				geom.TexID = reader.ReadInt32();
				geom.Unk = reader.ReadInt32();
				geom.Fl1 = reader.ReadSingle();
				Logger.Dev(nameof(RfmParser),
					$"      TexID={geom.TexID}, Unk={geom.Unk}, Fl1={geom.Fl1}");

				// if this mesh is actually skinned, skip its 4‐byte flags
				if (mesh.Bones.Count > 0)
					reader.ReadBytes(4);

				lod.Geoms.Add(geom);
			}

			// — finally read the LOD’s position + bounds —
			lod.Pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Logger.Dev(nameof(RfmParser), $"    LOD.Pos = {lod.Pos}");

			// skip the “fl1 + bounds” floats
			reader.ReadSingle();                        // fl1
			reader.ReadSingle(); reader.ReadSingle();   // min bound
			reader.ReadSingle();
			reader.ReadSingle(); reader.ReadSingle();   // max bound
			reader.ReadSingle();
		}


		// helper types
		private class RfmObject { public List<RfmLod> Lods = new(); public List<float> Flts = new(); }
		private class RfmLod { public string Name1, Name2; public uint Dword1, Dword2; public List<string> Textures = new(); public int NumGeom; public List<RfmGeom> Geoms = new(); public Vector3 Pos; public List<RfmVertex> SkinnedVertices = new(); }
		private class RfmGeom { public List<Vector3> SimpleVertices = new(); public List<Vector3> Normals = new(); public List<(RfmFaceVtx, RfmFaceVtx, RfmFaceVtx)> Triangles = new(); public int TexID, Unk; public float Fl1; }
		private struct RfmFaceVtx { public int VtxID; public float u, v; public RfmFaceVtx(int id, float u, float v) { VtxID = id; this.u = u; this.v = v; } };
		private class RfmVertex { public Vector3 Pos, Normal; public ushort Sh1; public List<RfmVtxWeight> Weights = new(); }
		private struct RfmVtxWeight { public int BoneID; public float Weight; }
	}
}
