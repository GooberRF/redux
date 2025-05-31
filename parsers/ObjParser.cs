using redux.utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace redux.parsers
{
    class ObjParser
    {
		private const string logSrc = "ObjParser";

		public static Mesh ReadObj(string objPath)
		{
			Logger.Info(logSrc, $"Parsing OBJ file: {objPath}");

			if (!Config.TriangulatePolygons)
			{
				Logger.Info(logSrc, $"-ngons option used, not forcing triangulation of parsed polygons");
			}

			var mesh = new Mesh();
			var positions = new List<Vector3>();
			var uvs = new List<Vector2>();
			var textureIndexByMaterial = new Dictionary<string, int>();
			string currentMaterial = "default";

			Brush? currentBrush = null;
			Dictionary<RflUtils.VertexKey, int>? currentVertexMap = null;

			string? currentObjectName = null;

			foreach (var line in File.ReadLines(objPath))
			{
				string trimmed = line.Trim();
				if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
					continue;

				string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) continue;

				switch (parts[0])
				{
					case "o":
						// Add a new brush
						if (parts.Length > 1)
						{
							string objectName = parts[1];
							// Split name on “_”
							var tokens = objectName.Split('_', StringSplitOptions.RemoveEmptyEntries);

							// Ensure we have "Brush" and a UID
							if (tokens.Length < 2 || !tokens[0].Equals("Brush", StringComparison.OrdinalIgnoreCase))
							{
								Logger.Warn(logSrc, $"Unrecognized object name format: '{objectName}'. Using sequential UID instead.");
								tokens = new[] { "Brush" }; // so we fall through to sequential assignment
							}

							// Parse UID
							int uid;
							if (tokens.Length >= 2 && int.TryParse(tokens[1], out int parsedUid))
							{
								uid = parsedUid;
							}
							else
							{
								uid = mesh.Brushes.Count + 1;
								Logger.Warn(logSrc, $"Invalid or missing UID in object name: '{objectName}'. Assigning sequential UID = {uid}.");
							}

							// Check flags
							uint flags = 0;
							if (Config.SimpleBrushNames || tokens.Length == 2)
							{
								// Simple mode is set or the name is invlaid. flags = 0
							}
							else
							{
								// "A" or "S"
								if (tokens.Length >= 3 && tokens[2].Equals("A", StringComparison.OrdinalIgnoreCase))
									flags |= (uint)SolidFlags.Air;

								// "D" or "nD"
								if (tokens.Length >= 4 && tokens[3].Equals("D", StringComparison.OrdinalIgnoreCase))
									flags |= (uint)SolidFlags.Detail;

								// "P" or "nP"
								if (tokens.Length >= 5 && tokens[4].Equals("P", StringComparison.OrdinalIgnoreCase))
									flags |= (uint)SolidFlags.Portal;

								// "ES" or "nES"
								if (tokens.Length >= 6 && tokens[5].Equals("ES", StringComparison.OrdinalIgnoreCase))
									flags |= (uint)SolidFlags.EmitsSteam;

								// "G" or "nG"
								if (tokens.Length >= 7 && tokens[6].Equals("G", StringComparison.OrdinalIgnoreCase))
									flags |= (uint)SolidFlags.Geoable;
							}

							// Create the new brush
							currentBrush = new Brush
							{
								UID = uid,
								TextureName = "missing_texture",
								RotationMatrix = Matrix4x4.Identity,
								Position = Vector3.Zero,
								Solid = new Solid
								{
									State = 3,     // default “selected”
									Life = -1,     // default
									Flags = flags  // as parsed
								}
							};
							mesh.Brushes.Add(currentBrush);

							// Reset the per‐brush maps
							currentVertexMap = new Dictionary<RflUtils.VertexKey, int>();
							textureIndexByMaterial = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

							Logger.Debug(logSrc, $"Started object '{objectName}' as Brush UID={uid}, Flags=0x{flags:X}");
						}
						break;

					case "v":
						if (parts.Length >= 4)
						{
							float x = float.Parse(parts[1], CultureInfo.InvariantCulture);
							float y = float.Parse(parts[2], CultureInfo.InvariantCulture);
							float z = float.Parse(parts[3], CultureInfo.InvariantCulture);
							positions.Add(new Vector3(-x, y, z));
						}
						break;

					case "vt":
						if (parts.Length >= 3)
						{
							float u = float.Parse(parts[1], CultureInfo.InvariantCulture);
							float v = 1.0f - float.Parse(parts[2], CultureInfo.InvariantCulture);
							uvs.Add(new Vector2(u, v));
						}
						break;

					case "usemtl":
						if (parts.Length > 1)
							currentMaterial = parts[1];
						break;

					case "f":
						if (currentBrush == null || currentVertexMap == null)
						{
							Logger.Warn(logSrc, "Encountered face data before any object was defined. Skipping.");
							continue;
						}

						// Ensure material is known
						if (!textureIndexByMaterial.TryGetValue(currentMaterial, out int texIndex))
						{
							string textureName = currentMaterial.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
								? currentMaterial
								: currentMaterial + ".tga";

							currentBrush.Solid.Textures.Add(textureName);
							texIndex = currentBrush.Solid.Textures.Count - 1;
							textureIndexByMaterial[currentMaterial] = texIndex;
						}

						var faceIndices = new List<int>();

						for (int i = 1; i < parts.Length; i++)
						{
							var vertParts = parts[i].Split('/');
							int posIdx = int.Parse(vertParts[0]) - 1;
							int uvIdx = vertParts.Length > 1 && !string.IsNullOrEmpty(vertParts[1]) ? int.Parse(vertParts[1]) - 1 : -1;

							Vector3 pos = posIdx >= 0 && posIdx < positions.Count ? positions[posIdx] : Vector3.Zero;
							Vector2 uv = uvIdx >= 0 && uvIdx < uvs.Count ? uvs[uvIdx] : Vector2.Zero;

							var key = new RflUtils.VertexKey(pos, uv);
							if (!currentVertexMap.TryGetValue(key, out int finalIdx))
							{
								finalIdx = currentBrush.Vertices.Count;
								currentBrush.Vertices.Add(pos);
								currentBrush.UVs.Add(uv);
								currentVertexMap[key] = finalIdx;
							}

							faceIndices.Add(finalIdx);
						}

						if (Config.TriangulatePolygons)
						{
							// Triangulate
							for (int i = 1; i < faceIndices.Count - 1; i++)
							{
								int i0 = faceIndices[0];
								int i1 = faceIndices[i];
								int i2 = faceIndices[i + 1];

								currentBrush.Indices.Add(i0);
								currentBrush.Indices.Add(i1);
								currentBrush.Indices.Add(i2);

								currentBrush.Solid.Faces.Add(new Face
								{
									Vertices = new List<int> { i0, i1, i2 },
									TextureIndex = texIndex
								});
							}
						}
						else
						{
							if (faceIndices.Count > 3)
							{
								Logger.Debug(logSrc, $"Parsing ngon face with {faceIndices.Count} vertices.");
							}

							// Compute face normal from first triangle of the polygon
							Vector3 normal = Vector3.UnitZ;
							if (faceIndices.Count >= 3)
							{
								Vector3 a = currentBrush.Vertices[faceIndices[0]];
								Vector3 b = currentBrush.Vertices[faceIndices[1]];
								Vector3 c = currentBrush.Vertices[faceIndices[2]];
								normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
							}

							currentBrush.Solid.Faces.Add(new Face
							{
								Vertices = faceIndices,
								TextureIndex = texIndex,
								Normal = normal
							});
						}

						break;
				}
			}

			Logger.Info(logSrc, $"Parsed OBJ with {mesh.Brushes.Count} brushes.");
			return mesh;
		}
	}
}
