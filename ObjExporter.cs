using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RFGConverter
{
	public static class ObjExporter
	{
		private const string logSrc = "ObjExporter";

		public static void ExportObj(Mesh mesh, string objPath)
		{
			var objDir = Path.GetDirectoryName(objPath)!;
			var baseName = Path.GetFileNameWithoutExtension(objPath);
			var mtlName = baseName + ".mtl";
			var mtlPath = Path.Combine(objDir, mtlName);

			Logger.Info(logSrc, $"Writing OBJ to {objPath}");
			Logger.Info(logSrc, $"Writing MTL to {mtlPath}");

			using var obj = new StreamWriter(objPath);
			using var mtl = new StreamWriter(mtlPath);

			obj.WriteLine($"mtllib {mtlName}");

			var writtenMats = new HashSet<string>();
			int vOffset = 1, vtOffset = 1, vnOffset = 1;

			foreach (var brush in mesh.Brushes)
			{
				if (brush.Vertices.Count == 0) continue;

				bool isAir = (brush.Solid.Flags & (uint)SolidFlags.Air) != 0;
				bool isDet = (brush.Solid.Flags & (uint)SolidFlags.Detail) != 0;
				bool isPort = (brush.Solid.Flags & (uint)SolidFlags.Portal) != 0;
				bool isSteam = (brush.Solid.Flags & (uint)SolidFlags.EmitsSteam) != 0;

				var name = $"Brush_{brush.UID}_{(isAir ? "air" : "solid")}_{(isDet ? "detail" : "nodetail")}_{(isPort ? "portal" : "noportal")}_{(isSteam ? "emit" : "noemit")}";
				obj.WriteLine($"o {name}");

				// shared‐pool: pos+uv+normal → (vi, vti, vni)
				var pool = new Dictionary<(int x, int y, int z, float u, float v, float nx, float ny, float nz), (int vi, int vti, int vni)>();
				var V = new List<Vector3>();
				var VT = new List<Vector2>();
				var VN = new List<Vector3>();

				// collect per-face index lists
				var facesByMat = new Dictionary<string, List<List<(int vi, int vti, int vni)>>>();

				foreach (var face in brush.Solid.Faces)
				{
					// compute flat normal in world space
					var w0 = Vector3.Transform(brush.Vertices[face.Vertices[0]], brush.RotationMatrix) + brush.Position;
					Vector3 norm = Vector3.Zero;
					for (int i = 1; i + 1 < face.Vertices.Count; i++)
					{
						var w1 = Vector3.Transform(brush.Vertices[face.Vertices[i]], brush.RotationMatrix) + brush.Position;
						var w2 = Vector3.Transform(brush.Vertices[face.Vertices[i + 1]], brush.RotationMatrix) + brush.Position;
						norm += Vector3.Cross(w1 - w0, w2 - w0);
					}
					norm = Vector3.Normalize(norm);

					// pick texture name with safe lookup
					string tex = face.TextureIndex >= 0 && face.TextureIndex < brush.Solid.Textures.Count
								 ? brush.Solid.Textures[face.TextureIndex]
								 : "missing_texture.tga";
					string mat = Path.GetFileNameWithoutExtension(tex);

					if (!facesByMat.TryGetValue(mat, out var list))
						facesByMat[mat] = list = new List<List<(int, int, int)>>();

					// build one face's index triple list
					var idxTriples = new List<(int vi, int vti, int vni)>();
					for (int i = 0; i < face.Vertices.Count; i++)
					{
						// world position
						var p = Vector3.Transform(brush.Vertices[face.Vertices[i]], brush.RotationMatrix) + brush.Position;
						// uv
						var uv = (i < face.UVs.Count) ? face.UVs[i] : Vector2.Zero;

						// quantize
						var key = (
							(int)(p.X * 1000), (int)(p.Y * 1000), (int)(p.Z * 1000),
							uv.X, uv.Y,
							norm.X, norm.Y, norm.Z
						);

						if (!pool.TryGetValue(key, out var trip))
						{
							trip.vi = V.Count;
							trip.vti = VT.Count;
							trip.vni = VN.Count;
							pool[key] = trip;
							V.Add(p);
							VT.Add(uv);
							VN.Add(norm);
						}

						idxTriples.Add(trip);
					}

					// optional triangulate
					if (Config.TriangulatePolygons && idxTriples.Count > 3)
					{
						for (int i = 1; i + 1 < idxTriples.Count; i++)
							list.Add(new List<(int, int, int)> { idxTriples[0], idxTriples[i], idxTriples[i + 1] });
					}
					else
					{
						list.Add(idxTriples);
					}
				}

				// emit all pooled vertices
				foreach (var p in V) obj.WriteLine($"v {-p.X} {p.Y} {p.Z}");
				foreach (var uv in VT) obj.WriteLine($"vt {uv.X} {1 - uv.Y}");
				foreach (var n in VN) obj.WriteLine($"vn {n.X} {n.Y} {n.Z}");

				// emit faces grouped by material
				foreach (var kv in facesByMat)
				{
					var mat = kv.Key;
					if (writtenMats.Add(mat))
					{
						mtl.WriteLine($"newmtl {mat}");
						mtl.WriteLine("Ka 1.000 1.000 1.000");
						mtl.WriteLine("Kd 1.000 1.000 1.000");
						mtl.WriteLine("Ks 0.000 0.000 0.000");
						mtl.WriteLine("d 1.0");
						mtl.WriteLine("illum 1");
						mtl.WriteLine($"map_Kd {mat}.tga");
						mtl.WriteLine();
					}

					obj.WriteLine($"usemtl {mat}");
					foreach (var face in kv.Value)
					{
						var faceTokens = face
							.Select(t => $"{t.vi + vOffset}/{t.vti + vtOffset}/{t.vni + vnOffset}")
							.ToArray();
						obj.WriteLine("f " + string.Join(" ", faceTokens));
					}
				}

				// bump our global offsets
				vOffset += V.Count;
				vtOffset += VT.Count;
				vnOffset += VN.Count;
			}

			Logger.Info(logSrc, "OBJ export complete.");
		}
	}
}
