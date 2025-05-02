using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace RFGConverter
{
	public static class ObjExporter
	{
		public static void ExportObj(Mesh mesh, string objPath)
		{
			string objDir = Path.GetDirectoryName(objPath)!;
			string objName = Path.GetFileNameWithoutExtension(objPath);
			string mtlFileName = objName + ".mtl";
			string mtlPath = Path.Combine(objDir, mtlFileName);

			using StreamWriter objWriter = new StreamWriter(objPath);
			using StreamWriter mtlWriter = new StreamWriter(mtlPath);

			objWriter.WriteLine("mtllib " + mtlFileName);

			int globalVertexOffset = 1;
			HashSet<string> writtenMaterials = new HashSet<string>();

			foreach (var brush in mesh.Brushes)
			{
				if (brush.Vertices.Count == 0 || brush.Indices.Count == 0)
					continue;

				bool isAir = (brush.Solid.Flags & 0x02) != 0;
				bool isDetail = (brush.Solid.Flags & 0x04) != 0;
				bool isPortal = (brush.Solid.Flags & 0x01) != 0;
				bool emitsSteam = (brush.Solid.Flags & 0x10) != 0;

				string typeStr = isAir ? "air" : "solid";
				string detailStr = isDetail ? "detail" : "nodetail";
				string portalStr = isPortal ? "portal" : "noportal";
				string steamStr = emitsSteam ? "emit" : "noemit";

				string brushName = $"Brush_{brush.UID}_{typeStr}_{detailStr}_{portalStr}_{steamStr}";
				objWriter.WriteLine($"o {brushName}");

				List<Vector3> transformedVerts = new();
				foreach (var v in brush.Vertices)
					transformedVerts.Add(Vector3.Transform(v, brush.RotationMatrix) + brush.Position);

				foreach (var v in transformedVerts)
					objWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", -v.X, v.Y, v.Z));

				foreach (var uv in brush.UVs)
					objWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "vt {0} {1}", uv.X, 1.0f - uv.Y));

				// Group faces by TextureIndex
				var materialGroups = new Dictionary<int, List<Face>>();
				foreach (var face in brush.Solid.Faces)
				{
					if (!materialGroups.ContainsKey(face.TextureIndex))
						materialGroups[face.TextureIndex] = new List<Face>();
					materialGroups[face.TextureIndex].Add(face);
				}

				foreach (var kvp in materialGroups)
				{
					int textureIndex = kvp.Key;
					string textureName = (textureIndex >= 0 && textureIndex < brush.Solid.Textures.Count)
						? brush.Solid.Textures[textureIndex]
						: "missing_texture";
					string materialName = Path.GetFileNameWithoutExtension(textureName);

					// Write .mtl entry
					if (writtenMaterials.Add(materialName))
					{
						mtlWriter.WriteLine($"newmtl {materialName}");
						mtlWriter.WriteLine("Ka 1.000 1.000 1.000");
						mtlWriter.WriteLine("Kd 1.000 1.000 1.000");
						mtlWriter.WriteLine("Ks 0.000 0.000 0.000");
						mtlWriter.WriteLine("d 1.0");
						mtlWriter.WriteLine("illum 1");
						mtlWriter.WriteLine($"map_Kd {materialName}.tga");
						mtlWriter.WriteLine();
					}

					objWriter.WriteLine($"usemtl {materialName}");

					foreach (var face in kvp.Value)
					{
						if (face.Vertices.Count < 3) continue;

						for (int i = 1; i < face.Vertices.Count - 1; i++)
						{
							int i0 = face.Vertices[0] + globalVertexOffset;
							int i1 = face.Vertices[i] + globalVertexOffset;
							int i2 = face.Vertices[i + 1] + globalVertexOffset;
							objWriter.WriteLine($"f {i0}/{i0} {i2}/{i2} {i1}/{i1}");
						}
					}
				}

				globalVertexOffset += transformedVerts.Count;
			}
		}
	}
}