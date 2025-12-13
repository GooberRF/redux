using redux.utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace redux.parsers
{
	class ObjParser
	{
		private const string logSrc = "ObjParser";

		public static Mesh ReadObj(string objPath)
		{
			Logger.Info(logSrc, $"Parsing OBJ file: {objPath}");

			if (!Config.TriangulatePolygons)
				Logger.Info(logSrc, $"-ngons option used, not forcing triangulation");

			var mesh = new Mesh();
			var positions = new List<Vector3>();
			var uvs = new List<Vector2>();
			var normals = new List<Vector3>();
			var textureIndexByMaterial = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			string currentMaterial = "default";

			Brush currentBrush = null!;
			var currentVertexMap = new Dictionary<RflUtils.VertexKey, int>();

			foreach (var line in File.ReadLines(objPath))
			{
				var trimmed = line.Trim();
				if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
					continue;

				var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				switch (parts[0])
				{
					case "o" when parts.Length > 1:
						currentBrush = CreateNewBrush(parts[1], mesh);
						textureIndexByMaterial.Clear();
						currentVertexMap.Clear();
						break;

					case "v" when parts.Length >= 4:
						positions.Add(ParseVec3(parts[1], parts[2], parts[3], invertX: true));
						break;

					case "vt" when parts.Length >= 3:
						uvs.Add(ParseVec2(parts[1], parts[2], flipV: true));
						break;

					case "vn" when parts.Length >= 4:
						normals.Add(ParseVec3(parts[1], parts[2], parts[3], invertX: true));
						break;

					case "usemtl" when parts.Length > 1:
						currentMaterial = parts[1];
						currentBrush.TextureName = currentMaterial;
						break;

					case "f" when currentBrush != null:
						ParseFace(parts, positions, uvs, normals, currentBrush,
								  textureIndexByMaterial, currentVertexMap);
						break;
				}
			}

			Logger.Info(logSrc, $"Parsed OBJ → {mesh.Brushes.Count} brushes.");
			return mesh;
		}

		static Brush CreateNewBrush(string objectName, Mesh mesh)
		{
			int uid = ObjUtils.ParseUidFromObjectName(objectName, mesh.Brushes.Count + 1);
			var flags = ObjUtils.ParseFlagsFromObjectName(objectName);
			var brush = new Brush
			{
				UID = uid,
				Position = Vector3.Zero,
				RotationMatrix = Matrix4x4.Identity,
				TextureName = "missing_texture",
				Solid = new Solid { State = 3, Life = -1, Flags = flags }
			};
			mesh.Brushes.Add(brush);
			Logger.Debug(logSrc, $"Created Brush {objectName} UID={uid} Flags=0x{flags:X}");
			return brush;
		}

		static void ParseFace(string[] parts,
							  List<Vector3> positions,
							  List<Vector2> uvs,
							  List<Vector3> normals,
							  Brush brush,
							  Dictionary<string, int> texMap,
							  Dictionary<RflUtils.VertexKey, int> vmap)
		{
			if (!texMap.TryGetValue(brush.TextureName, out int texIndex))
			{
				string tex = brush.TextureName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
							 ? brush.TextureName
							 : brush.TextureName + ".tga";
				brush.Solid.Textures.Add(tex);
				texIndex = brush.Solid.Textures.Count - 1;
				texMap[brush.TextureName] = texIndex;
			}

			var faceIdxs = new List<int>();
			for (int i = 1; i < parts.Length; i++)
			{
				var vp = parts[i].Split('/');
				int pi = int.Parse(vp[0], CultureInfo.InvariantCulture) - 1;
				int ui = vp.Length > 1 && vp[1] != "" ? int.Parse(vp[1], CultureInfo.InvariantCulture) - 1 : -1;
				int ni = vp.Length > 2 && vp[2] != "" ? int.Parse(vp[2], CultureInfo.InvariantCulture) - 1 : -1;

				var pos = pi >= 0 && pi < positions.Count ? positions[pi] : Vector3.Zero;
				var uv = ui >= 0 && ui < uvs.Count ? uvs[ui] : Vector2.Zero;
				var norm = ni >= 0 && ni < normals.Count ? normals[ni] : Vector3.Zero;

				var key = new RflUtils.VertexKey(pos, uv, norm);
				if (!vmap.TryGetValue(key, out int idx))
				{
					idx = brush.Vertices.Count;
					brush.Vertices.Add(pos);
					brush.UVs.Add(uv);
					brush.Normals.Add(norm);
					vmap[key] = idx;
				}
				faceIdxs.Add(idx);
			}

			// build faces
			if (Config.TriangulatePolygons)
			{
				for (int i = 1; i < faceIdxs.Count - 1; i++)
					AddFace(brush, faceIdxs[0], faceIdxs[i], faceIdxs[i + 1], texIndex);
			}
			else
				AddFace(brush, faceIdxs, texIndex);
		}

		static void AddFace(Brush brush, int i0, int i1, int i2, int texIndex)
		{
			brush.Indices.AddRange(new[] { i0, i1, i2 });
			var face = new Face { TextureIndex = texIndex };

			face.Vertices.AddRange(new[] { i0, i1, i2 });
			face.UVs.AddRange(new[] { brush.UVs[i0], brush.UVs[i1], brush.UVs[i2] });

			var normal = face.Normal = Vector3.Normalize(Vector3.Cross(
				brush.Vertices[i1] - brush.Vertices[i0],
				brush.Vertices[i2] - brush.Vertices[i0]
			));

			// flip if requested
			//if (Config.FlipNormals)
				//normal = -normal;

			face.Normal = normal;

			brush.Solid.Faces.Add(face);
		}

		static void AddFace(Brush brush, List<int> idxs, int texIndex)
		{
			for (int i = 1; i < idxs.Count - 1; i++)
				AddFace(brush, idxs[0], idxs[i], idxs[i + 1], texIndex);
		}

		static Vector3 ParseVec3(string sx, string sy, string sz, bool invertX = false)
		{
			float x = float.Parse(sx, CultureInfo.InvariantCulture);
			float y = float.Parse(sy, CultureInfo.InvariantCulture);
			float z = float.Parse(sz, CultureInfo.InvariantCulture);
			return invertX ? new Vector3(-x, y, z) : new Vector3(x, y, z);
		}

		static Vector2 ParseVec2(string su, string sv, bool flipV = false)
		{
			var u = float.Parse(su, CultureInfo.InvariantCulture);
			var v = float.Parse(sv, CultureInfo.InvariantCulture);
			return flipV ? new Vector2(u, 1 - v) : new Vector2(u, v);
		}
	}
}
