using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace RFGConverter
{
	public static class RfgParser
	{
		private static void SafeSkip(BinaryReader reader, int entrySize)
		{
			if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
				return;
			int count = reader.ReadInt32();
			long bytesToSkip = (long)entrySize * count;
			if (reader.BaseStream.Position + bytesToSkip > reader.BaseStream.Length)
				reader.BaseStream.Seek(0, SeekOrigin.End);
			else
				reader.BaseStream.Seek(bytesToSkip, SeekOrigin.Current);
		}
		private static void SkipLights(BinaryReader reader) => SafeSkip(reader, 100);
		private static void SkipCutsceneCameras(BinaryReader reader) => SafeSkip(reader, 84);
		private static void SkipCutscenePathNodes(BinaryReader reader) => SafeSkip(reader, 84);
		private static void SkipAmbientSounds(BinaryReader reader) => SafeSkip(reader, 48);
		private static void SkipEvents(BinaryReader reader) => SafeSkip(reader, 112);
		private static void SkipMpRespawnPoints(BinaryReader reader) => SafeSkip(reader, 80);
		private static void SkipNavPoints(BinaryReader reader) => SafeSkip(reader, 100);
		private static void SkipEntities(BinaryReader reader) => SafeSkip(reader, 220);
		private static void SkipItems(BinaryReader reader) => SafeSkip(reader, 100);
		private static void SkipClutters(BinaryReader reader) => SafeSkip(reader, 100);
		private static void SkipTriggers(BinaryReader reader) => SafeSkip(reader, 120);
		private static void SkipParticleEmitters(BinaryReader reader) => SafeSkip(reader, 180);
		private static void SkipGasRegions(BinaryReader reader) => SafeSkip(reader, 84);
		private static void SkipDecals(BinaryReader reader) => SafeSkip(reader, 80);
		private static void SkipClimbingRegions(BinaryReader reader) => SafeSkip(reader, 64);
		private static void SkipRoomEffects(BinaryReader reader) => SafeSkip(reader, 64);
		private static void SkipEaxEffects(BinaryReader reader) => SafeSkip(reader, 80);
		private static void SkipBoltEmitters(BinaryReader reader) => SafeSkip(reader, 100);
		private static void SkipTargets(BinaryReader reader) => SafeSkip(reader, 64);
		private static void SkipPushRegions(BinaryReader reader) => SafeSkip(reader, 64);
		private static void SkipGeoRegions(BinaryReader reader)
		{
			if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
				return;
			int count = reader.ReadInt32();
			long bytesToSkip = 4L + count * 4L;
			if (reader.BaseStream.Position + bytesToSkip > reader.BaseStream.Length)
				reader.BaseStream.Seek(0, SeekOrigin.End);
			else
				reader.BaseStream.Seek(bytesToSkip, SeekOrigin.Current);
		}
		public static Mesh ReadRfg(string filename)
		{
			var mesh = new Mesh();

			using (var stream = File.OpenRead(filename))
			using (var reader = new BinaryReader(stream))
			{
				// Read header
				uint magic = reader.ReadUInt32();
				if (magic != 0xD43DD00D)
					throw new Exception("Invalid RFG magic.");

				int version = reader.ReadInt32();
				int numGroups = reader.ReadInt32();

				for (int g = 0; g < numGroups; g++)
				{
					string groupName = Utils.ReadVString(reader);
					byte isMoving = reader.ReadByte();
					if (isMoving != 0)
						throw new NotSupportedException("Moving groups are not supported yet.");

					// ----- START reading brush section -----
					int numBrushes = reader.ReadInt32();
					for (int b = 0; b < numBrushes; b++)
					{
						Brush brush = ReadBrush(reader);
						mesh.Brushes.Add(brush);
					}
					// ----- END reading brush section -----

					// ----- SKIP the rest -----

					SkipGeoRegions(reader);
					SkipLights(reader);
					SkipCutsceneCameras(reader);
					SkipCutscenePathNodes(reader);
					SkipAmbientSounds(reader);
					SkipEvents(reader);
					SkipMpRespawnPoints(reader);
					SkipNavPoints(reader);
					SkipEntities(reader);
					SkipItems(reader);
					SkipClutters(reader);
					SkipTriggers(reader);
					SkipParticleEmitters(reader);
					SkipGasRegions(reader);
					SkipDecals(reader);
					SkipClimbingRegions(reader);
					SkipRoomEffects(reader);
					SkipEaxEffects(reader);
					SkipBoltEmitters(reader);
					SkipTargets(reader);
					SkipPushRegions(reader);
				}
			}

			return mesh;
		}

		private static Brush ReadBrush(BinaryReader reader)
		{
			var brush = new Brush();

			try
			{
				// UID
				if (reader.BaseStream.Position + 4 > reader.BaseStream.Length) return brush;
				brush.UID = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] UID: {brush.UID}");

				// Position
				if (reader.BaseStream.Position + 12 > reader.BaseStream.Length) return brush;
				brush.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				Console.WriteLine($"[RfgParser] Position: {brush.Position}");

				// Rotation Matrix
				if (reader.BaseStream.Position + 36 > reader.BaseStream.Length) return brush;
				// Correct reordering when reading RFG brush rotation
				float fwdX = reader.ReadSingle();
				float fwdY = reader.ReadSingle();
				float fwdZ = reader.ReadSingle();
				float rightX = reader.ReadSingle();
				float rightY = reader.ReadSingle();
				float rightZ = reader.ReadSingle();
				float upX = reader.ReadSingle();
				float upY = reader.ReadSingle();
				float upZ = reader.ReadSingle();

				// Map to standard matrix:
				// Forward = Z axis
				// Right = X axis
				// Up = Y axis

				Vector3 right = new(rightX, rightY, rightZ);
				Vector3 up = new(upX, upY, upZ);
				Vector3 forward = new(fwdX, fwdY, fwdZ);

				// Treat RFG as storing basis vectors in rows
				brush.RotationMatrix = new Matrix4x4(
					right.X, right.Y, right.Z, 0f,
					up.X, up.Y, up.Z, 0f,
					forward.X, forward.Y, forward.Z, 0f,
					0f, 0f, 0f, 1f
				);

				Console.WriteLine("[RfgParser] Rotation vectors (raw from RFG):");
				Console.WriteLine($"  Forward: ({fwdX}, {fwdY}, {fwdZ})");
				Console.WriteLine($"  Right:   ({rightX}, {rightY}, {rightZ})");
				Console.WriteLine($"  Up:      ({upX}, {upY}, {upZ})");

				Console.WriteLine("[RfgParser] Constructed rotation matrix:");
				Console.WriteLine($"  [{rightX,6:F3}, {upX,6:F3}, {-fwdX,6:F3}, 0]");
				Console.WriteLine($"  [{rightY,6:F3}, {upY,6:F3}, {-fwdY,6:F3}, 0]");
				Console.WriteLine($"  [{rightZ,6:F3}, {upZ,6:F3}, {-fwdZ,6:F3}, 0]");
				Console.WriteLine($"  [   0.000,    0.000,    0.000, 1]");

				Console.WriteLine($"[RfgParser] UID {brush.UID}: Read rotation matrix.");

				// Geometry Name (vstring)
				string geomName = Utils.ReadVString(reader);
				Console.WriteLine($"[RfgParser] Geometry name: \"{geomName}\"");

				// Skip two unknown ints (?)
				reader.ReadInt32(); // unknown1
				reader.ReadInt32(); // unknown2

				// Textures
				int numTextures = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numTextures: {numTextures}");
				var textures = new List<string>();
				for (int i = 0; i < numTextures; i++)
					textures.Add(Utils.ReadVString(reader));

				// Always create a fresh Solid for this brush
				brush.Solid = new Solid
				{
					Textures = new List<string>(textures),
					Faces = new List<Face>(),
					Vertices = new List<Vector3>()
				};

				brush.TextureName = textures.Count > 0 ? textures[0] : "missing_texture"; // fallback

				// Face Scroll Data
				int numFaceScrollData = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numFaceScrollData: {numFaceScrollData}");
				for (int i = 0; i < numFaceScrollData; i++)
					reader.BaseStream.Seek(12, SeekOrigin.Current);

				// Rooms
				int numRooms = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numRooms: {numRooms}");
				for (int i = 0; i < numRooms; i++)
				{
					reader.BaseStream.Seek(28, SeekOrigin.Current); // room AABB + flags
				}

				// Subroom Lists
				int numSubroomLists = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numSubroomLists: {numSubroomLists}");
				for (int i = 0; i < numSubroomLists; i++)
					reader.BaseStream.Seek(8, SeekOrigin.Current);

				// ⭐️ FIX: Read numPortals BEFORE vertices!
				int numPortals = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numPortals: {numPortals}");

				// Now safe to read vertices
				int numVertices = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numVertices: {numVertices}");

				var vertices = new List<Vector3>();
				for (int i = 0; i < numVertices; i++)
				{
					if (reader.BaseStream.Position + 12 > reader.BaseStream.Length)
					{
						Console.WriteLine("[RfgParser] ERROR: Unexpected end of stream while reading vertices.");
						return brush;
					}
					Vector3 vertex = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					vertices.Add(vertex);
					Console.WriteLine($"[RfgParser] Vertex {i}: {vertex}");
				}

				// Faces
				int numFaces = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numFaces: {numFaces}");
				for (int i = 0; i < numFaces; i++)
				{
					if (reader.BaseStream.Position >= reader.BaseStream.Length)
					{
						Console.WriteLine("[RfgParser] ERROR: Unexpected end of stream while reading faces.");
						return brush;
					}
					ReadFace(reader, brush, vertices);
				}


				// Surfaces
				int numSurfaces = reader.ReadInt32();
				Console.WriteLine($"[RfgParser] numSurfaces: {numSurfaces}");
				for (int i = 0; i < numSurfaces; i++)
				{
					reader.BaseStream.Seek(96, SeekOrigin.Current); // each surface is 96 bytes
				}

				// Flags, Life, State
				if (reader.BaseStream.Position + 12 <= reader.BaseStream.Length)
				{
					brush.Solid.Flags = reader.ReadUInt32(); // store flags
					reader.ReadInt32(); // life
					reader.ReadInt32(); // state
				}

			}
			catch (Exception ex)
			{
				Console.WriteLine($"[RfgParser] ERROR in ReadBrush: {ex.Message}");
			}

			return brush;
		}
		private static void ReadFace(BinaryReader reader, Brush brush, List<Vector3> vertices)
		{
			// Plane (skip normal + offset)
			for (int i = 0; i < 4; i++)
				reader.ReadSingle();

			int textureIndex = reader.ReadInt32();
			reader.ReadInt32(); // surface_index
			reader.ReadInt32(); // face_id
			reader.BaseStream.Seek(8, SeekOrigin.Current); // reserved
			reader.ReadInt32(); // portal index plus 2
			reader.ReadUInt16(); // flags
			reader.ReadUInt16(); // reserved2
			reader.ReadUInt32(); // smoothing groups
			reader.ReadInt32(); // room index

			int numFaceVertices = reader.ReadInt32();

			List<int> faceIndices = new List<int>();

			for (int i = 0; i < numFaceVertices; i++)
			{
				int vertIndex = reader.ReadInt32();
				float u = reader.ReadSingle();
				float v = reader.ReadSingle();

				if (vertIndex < vertices.Count)
				{
					int newIndex = brush.Vertices.Count;
					brush.Vertices.Add(vertices[vertIndex]);
					brush.UVs.Add(new Vector2(u, v));
					faceIndices.Add(newIndex);
				}
			}

			// Triangulate face (fan method)
			for (int i = 1; i < faceIndices.Count - 1; i++)
			{
				int i0 = faceIndices[0];
				int i1 = faceIndices[i];
				int i2 = faceIndices[i + 1];

				brush.Indices.Add(i0);
				brush.Indices.Add(i1);
				brush.Indices.Add(i2);

				brush.Solid.Faces.Add(new Face
				{
					Vertices = new List<int> { i0, i1, i2 },
					TextureIndex = textureIndex
				});
			}
		}
	}
}
