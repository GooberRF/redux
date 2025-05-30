using redux;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RFGConverter
{
	public static class V3mExporter
	{
		private const string logSrc = "V3mExporter";
		private const int V3M_SIGNATURE = 0x52463344; // 'RF3D'
		private const int V3D_VERSION = 0x40000;
		private const int V3D_SECTION_SUBMESH = 0x5355424D; // 'SUBM'
		private const int V3D_SECTION_END = 0x00000000;
		private const int V3D_LOD_TRIANGLE_PLANES = 0x20;

		public static void ExportV3m(Mesh mesh, string outputPath)
		{
			Logger.Dev(logSrc, $"ExportV3m: '{outputPath}', submesh count={mesh.Brushes.Count}");
			using var writer = new BinaryWriter(File.Create(outputPath));

			// File header
			writer.Write(V3M_SIGNATURE);
			writer.Write(V3D_VERSION);
			writer.Write(mesh.Brushes.Count);
			writer.Write(0); writer.Write(0); writer.Write(0); // reset fields
			int totalMaterials = mesh.Brushes.Sum(b => b.Solid.Textures.Count);
			writer.Write(totalMaterials);
			writer.Write(0); writer.Write(0); writer.Write(0);

			// Submeshes
			foreach (var brush in mesh.Brushes)
				WriteSubmesh(brush, writer);

			// End section
			writer.Write(V3D_SECTION_END);
			writer.Write(0);
			Logger.Dev(logSrc, "ExportV3m complete");
		}

		private static void WriteSubmesh(Brush brush, BinaryWriter writer)
		{
			Logger.Dev(logSrc, $"-- Submesh begin Brush {brush.UID}");
			// Section header
			writer.Write(V3D_SECTION_SUBMESH);
			writer.Write(0);

			// Submesh header
			WriteFixedString(writer, $"Brush_{brush.UID}", 24);
			WriteFixedString(writer, "", 24);
			writer.Write(7); writer.Write(1); writer.Write(0f);

			// Geometry per material
			var perMat = GatherGeometry(brush);

			// Bounding
			var allPts = perMat.Values.SelectMany(c => c.Positions).ToList();
			var aabbMin = new Vector3(float.MaxValue);
			var aabbMax = new Vector3(float.MinValue);
			foreach (var p in allPts)
			{
				aabbMin = Vector3.Min(aabbMin, p);
				aabbMax = Vector3.Max(aabbMax, p);
			}
			float radius = allPts.Max(p => p.Length());
			WriteVec3(writer, Vector3.Zero);
			writer.Write(radius);
			WriteVec3(writer, aabbMin);
			WriteVec3(writer, aabbMax);

			// LOD mesh header
			writer.Write((uint)V3D_LOD_TRIANGLE_PLANES);
			int totalVerts = allPts.Count;
			int numBatches = perMat.Count;
			writer.Write(totalVerts);
			writer.Write((ushort)numBatches);

			// Build raw data block
			using var ms = new MemoryStream();
			using var dw = new BinaryWriter(ms);
			void Align(int a) { long pad = (a - (dw.BaseStream.Position % a)) % a; if (pad > 0) dw.Write(new byte[pad]); }

			// Batch headers
			int texIdx = 0;
			foreach (var _ in perMat)
			{
				dw.Write(new byte[0x20]); dw.Write(texIdx++); dw.Write(new byte[0x14]);
			}
			Align(0x10);

			// Batch data
			foreach (var chunk in perMat.Values)
			{
				// positions
				foreach (var v in chunk.Positions) { 
					dw.Write(v.X); dw.Write(v.Y); dw.Write(v.Z);
				}
				Align(0x10);

				// normals
				foreach (var n in chunk.Normals) { 
					dw.Write(n.X); dw.Write(n.Y); dw.Write(n.Z);
				}
				Align(0x10);

				// uvs (flip Y)
				Logger.Dev(logSrc, $"Writing UVs: count={chunk.UVs.Count}");
				for (int i = 0; i < chunk.UVs.Count; i++)
				{
					var uv = chunk.UVs[i];
					float u = uv.X;
					float v = 1f - uv.Y;
					dw.Write(u);
					dw.Write(v);
					if (i < 3)
						Logger.Dev(logSrc, $"UV[{i}] = ({u:0.###}, {v:0.###})");
				}
				Align(0x10);


				// triangles
				foreach (var (i0, i1, i2, flags) in chunk.Triangles)
				{
					dw.Write((ushort)i0); dw.Write((ushort)i1); dw.Write((ushort)i2); dw.Write((ushort)flags);
				}
				Align(0x10);

				// planes
				foreach (var (n, d) in chunk.Planes)
				{
					dw.Write(n.X); dw.Write(n.Y); dw.Write(n.Z); dw.Write(d);
				}
				Align(0x10);

				// same_pos offsets (zeros)
				dw.Write(new byte[chunk.Positions.Count * sizeof(short)]);
				Align(0x10);
			}

			// Write data block
			writer.Write((int)ms.Length);
			writer.Write(ms.ToArray());
			writer.Write(-1); // unknown1

			// batch_info entries
			foreach (var chunk in perMat.Values)
			{
				int nv = chunk.Positions.Count;
				int nf = chunk.Triangles.Count;
				writer.Write((ushort)nv);
				writer.Write((ushort)nf);
				writer.Write((ushort)(nv * 12));  // positions_size
				writer.Write((ushort)(nf * 8));   // indices_size
				writer.Write((ushort)(nv * 2));   // same_pos_vertex_offsets_size
				writer.Write((ushort)0);          // bone_links_size
				writer.Write((ushort)(nv * 8));   // tex_coords_size
				writer.Write((uint)5344321);            // render_flags
			}

			// prop points
			writer.Write(0);

			// LOD textures
			writer.Write((uint)perMat.Count);
			byte ti = 0;
			foreach (var mat in perMat.Keys)
			{
				writer.Write(ti++);
				WriteZeroTerminatedString(writer, mat + ".tga");
			}

			// Submesh materials
			writer.Write(brush.Solid.Textures.Count);
			foreach (var tex in brush.Solid.Textures)
			{
				WriteFixedString(writer, Path.GetFileName(tex), 32);
				writer.Write(0f); // emissive
				writer.Write(0f); writer.Write(0f); writer.Write(0f); // unused
				WriteFixedString(writer, "", 32);
				writer.Write((uint)1); // flags: bit0
			}

			// unknown1
			writer.Write(1);
			WriteFixedString(writer, $"Brush_{brush.UID}", 24);
			writer.Write(0f);
		}

		private static Dictionary<string, Chunk> GatherGeometry(Brush brush)
		{
			var perMat = new Dictionary<string, Chunk>();
			foreach (var face in brush.Solid.Faces)
			{
				var idx = face.Vertices;
				int tris = idx.Count > 3 ? idx.Count - 2 : 1;
				for (int i = 0; i < tris; i++)
				{
					// World positions
					Vector3 p0 = Transform(brush, idx[0]);
					Vector3 p1 = Transform(brush, idx[i + 1]);
					Vector3 p2 = Transform(brush, idx[i + 2]);

					// Texture UV from global brush.UVs
					Vector2 uv0 = idx[0] < brush.UVs.Count ? brush.UVs[idx[0]] : Vector2.Zero;
					Vector2 uv1 = idx[i + 1] < brush.UVs.Count ? brush.UVs[idx[i + 1]] : Vector2.Zero;
					Vector2 uv2 = idx[i + 2] < brush.UVs.Count ? brush.UVs[idx[i + 2]] : Vector2.Zero;

					// Compute normal & plane
					var n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
					float d = -Vector3.Dot(n, p0);

					// Material name
					string mat = Path.GetFileNameWithoutExtension(
						face.TextureIndex >= 0 && face.TextureIndex < brush.Solid.Textures.Count
						? brush.Solid.Textures[face.TextureIndex]
						: string.Empty
					);

					if (!perMat.TryGetValue(mat, out var chunk))
						perMat[mat] = chunk = new Chunk();

					// Add or reuse vertex
					int v0 = chunk.AddVertex(p0, n, uv0);
					int v1 = chunk.AddVertex(p1, n, uv1);
					int v2 = chunk.AddVertex(p2, n, uv2);

					// Record triangle and plane
					chunk.Triangles.Add((v0, v1, v2, 0));
					chunk.Planes.Add((n, d));
				}
			}
			Logger.Dev(logSrc, $"Gathered geometry: {perMat.Count} materials, total vertices = {perMat.Values.Sum(c => c.Positions.Count)}");
			return perMat;
		}

		private static Vector3 Transform(Brush b, int vi)
			=> Vector3.Transform(b.Vertices[vi], b.RotationMatrix) + b.Position;

		private static void WriteFixedString(BinaryWriter w, string s, int len)
		{
			var bs = System.Text.Encoding.ASCII.GetBytes(s);
			int count = Math.Min(bs.Length, len - 1);
			w.Write(bs, 0, count);
			for (int i = count; i < len; i++) w.Write((byte)0);
		}

		private static void WriteZeroTerminatedString(BinaryWriter w, string s)
		{
			var bs = System.Text.Encoding.ASCII.GetBytes(s);
			w.Write(bs);
			w.Write((byte)0);
		}

		private static void WriteVec3(BinaryWriter w, Vector3 v)
		{
			w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
		}

		private class Chunk
		{
			public readonly List<Vector3> Positions = new();
			public readonly List<Vector3> Normals = new();
			public readonly List<Vector2> UVs = new();
			public readonly List<(int, int, int, uint)> Triangles = new();
			public readonly List<(Vector3, float)> Planes = new();
			private readonly Dictionary<(Vector3, Vector3, Vector2), int> map = new();
			public int AddVertex(Vector3 p, Vector3 n, Vector2 uv)
			{
				var key = (p, n, uv);
				if (map.TryGetValue(key, out var idx)) return idx;
				idx = Positions.Count;
				Positions.Add(p);
				Normals.Add(n);
				UVs.Add(uv);
				map[key] = idx;
				return idx;
			}
		}
	}
}
