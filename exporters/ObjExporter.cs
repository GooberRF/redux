using redux.utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace redux.exporters
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
			Logger.Dev(logSrc, $"Added index for material {mtlPath}");

			var writtenMats = new HashSet<string>();
			int vOffset = 1, vtOffset = 1, vnOffset = 1;

			foreach (var brush in mesh.Brushes)
			{
				if (brush.Vertices.Count == 0) continue;

				bool isAir = (brush.Solid.Flags & (uint)SolidFlags.Air) != 0;
				bool isDet = (brush.Solid.Flags & (uint)SolidFlags.Detail) != 0;
				bool isPort = (brush.Solid.Flags & (uint)SolidFlags.Portal) != 0;
				bool isSteam = (brush.Solid.Flags & (uint)SolidFlags.EmitsSteam) != 0;

				var name = $"Brush_{brush.UID}_{(isAir ? "A" : "S")}_{(isDet ? "D" : "nD")}_{(isPort ? "P" : "nP")}_{(isSteam ? "ES" : "nES")}";
				obj.WriteLine($"o {name}");

				// Merge verts by position in world space only 
				var posPool = new Dictionary<
					(int x, int y, int z),
					int // vi
				>();

				var V = new List<Vector3>(); // verts in world space

				// Merge verts by quantized UVs (u, v) per vertex index (vi):
				var uvPool = new Dictionary<
					(int vi, int u, int v),
					int // vti
				>();

				var VT = new List<Vector2>(); // vert UVs

				var VN = new List<Vector3>(); // vert normals

				// Group indices per material
				var facesByMat = new Dictionary<string, List<List<(int vi, int vti, int vni)>>>();

				foreach (var face in brush.Solid.Faces)
				{
					// Compute a flat normal for the face in world space
					var w0 = Vector3.Transform(
						brush.Vertices[face.Vertices[0]],
						brush.RotationMatrix
					) + brush.Position;

					Vector3 flatNorm = Vector3.Zero;
					for (int i = 1; i + 1 < face.Vertices.Count; i++)
					{
						var w1 = Vector3.Transform(
							brush.Vertices[face.Vertices[i]],
							brush.RotationMatrix
						) + brush.Position;

						var w2 = Vector3.Transform(
							brush.Vertices[face.Vertices[i + 1]],
							brush.RotationMatrix
						) + brush.Position;

						flatNorm += Vector3.Cross(w1 - w0, w2 - w0);
					}
					flatNorm = Vector3.Normalize(flatNorm);

					// Get the texture for the face
					string tex = (face.TextureIndex >= 0 && face.TextureIndex < brush.Solid.Textures.Count)
								 ? brush.Solid.Textures[face.TextureIndex]
								 : "missing_texture.tga";
					string mat = Path.GetFileNameWithoutExtension(tex);

					if (!facesByMat.TryGetValue(mat, out var listOfFaces))
						facesByMat[mat] = listOfFaces = new List<List<(int, int, int)>>();

					// Compute the vertex indices (vi, vti, vni) for the face
					var idxTriples = new List<(int vi, int vti, int vni)>();

					for (int i = 0; i < face.Vertices.Count; i++)
					{
						// World space position of the vertex
						var p = Vector3.Transform(
							brush.Vertices[face.Vertices[i]],
							brush.RotationMatrix
						) + brush.Position;

						// Quantize world space position to integer by 0.001
						var pKey = (
							x: (int)Math.Round(p.X * 1000f),
							y: (int)Math.Round(p.Y * 1000f),
							z: (int)Math.Round(p.Z * 1000f)
						);

						// If this is a new position, assign a new vi
						if (!posPool.TryGetValue(pKey, out int vi))
						{
							vi = V.Count;
							posPool[pKey] = vi;
							V.Add(p);
						}

						// UV of the vertex
						var uv = (i < face.UVs.Count)
								 ? face.UVs[i]
								 : Vector2.Zero;

						// Quantize UV to integer by 0.001
						var uvKey = (
							u: (int)Math.Round(uv.X * 1000f),
							v: (int)Math.Round(uv.Y * 1000f)
						);

						// Make key for the uvPool (vi, quantized_u, quantized_v)
						var fullUVKey = (vi: vi, u: uvKey.u, v: uvKey.v);

						// If this is a new UV for this vertex, assign a new vti
						if (!uvPool.TryGetValue(fullUVKey, out int vti))
						{
							vti = VT.Count;
							uvPool[fullUVKey] = vti;
							VT.Add(uv);
							VN.Add(flatNorm); // store this face’s normal for (vi, vti)
						}

						idxTriples.Add((vi, vti, vti));
						// Use the same index for vni so that vn[vti] is the flat normal
					}

					// Triganulate if needed before adding the face
					if (Config.TriangulatePolygons && idxTriples.Count > 3)
					{
						for (int i = 1; i + 1 < idxTriples.Count; i++)
							listOfFaces.Add(new List<(int, int, int)> {
								idxTriples[0],
								idxTriples[i],
								idxTriples[i + 1]
							});
					}
					else
					{
						listOfFaces.Add(idxTriples);
					}
				}

				// Write unique positions (V)
				// OBJ format uses right-handed coordinate system, so flip X to match RF (left-handed)
				foreach (var p in V)
					obj.WriteLine(
						$"v {(-p.X).ToString(CultureInfo.InvariantCulture)} " +
						$"{p.Y.ToString(CultureInfo.InvariantCulture)} " +
						$"{p.Z.ToString(CultureInfo.InvariantCulture)}"
					);

				// Write unique UVs (VT)
				foreach (var uv in VT)
					obj.WriteLine(
						$"vt {uv.X.ToString(CultureInfo.InvariantCulture)} " +
						$"{(1 - uv.Y).ToString(CultureInfo.InvariantCulture)}"
					);

				// Write unique normals (VN)
				foreach (var n in VN)
					obj.WriteLine(
						$"vn {n.X.ToString(CultureInfo.InvariantCulture)} " +
						$"{n.Y.ToString(CultureInfo.InvariantCulture)} " +
						$"{n.Z.ToString(CultureInfo.InvariantCulture)}"
					);

				// Write faces grouped by material
				// Use pooled indices and global offsets
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
						face.Reverse();  // correct winding so face normals point outward
						var faceTokens = face
							.Select(t => $"{t.vi + vOffset}/{t.vti + vtOffset}/{t.vni + vnOffset}")
							.ToArray();
						obj.WriteLine("f " + string.Join(" ", faceTokens));
					}
				}

				// Increment global vertex offsets before parsing next brush
				vOffset += V.Count;
				vtOffset += VT.Count;
				vnOffset += VN.Count;

				Logger.Dev(logSrc, $"Wrote brush {brush.UID}");
			}

			Logger.Info(logSrc, $"{objPath} and {mtlPath} written successfully.");
		}
	}
}
