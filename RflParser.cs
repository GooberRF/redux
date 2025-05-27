using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using static System.Collections.Specialized.BitVector32;

namespace RFGConverter
{
	public static class RflParser
	{
		private const string logSrc = "RflParser";
		public static Mesh ReadRfl(string filePath)
		{
			var mesh = new Mesh();

			using var stream = File.OpenRead(filePath);
			using var reader = new BinaryReader(stream);

			// Validate magic number
			uint magic = reader.ReadUInt32();
			if (magic != 0xD4BADA55)
				throw new Exception("Invalid RFL magic.");

			// Read header
			int version = reader.ReadInt32();
			uint timestamp = reader.ReadUInt32();
			int playerStartOffset = reader.ReadInt32();
			int levelInfoOffset = reader.ReadInt32();
			int numSections = reader.ReadInt32();
			int sectionsSize = reader.ReadInt32();

			// What version of RFL is this?
			bool isAlpine = version >= 0x12C;
			bool isRF1 = version <= 0xC8 || isAlpine;	// <= 201 or >= 300
			bool isRF2 = version == 0x127;						// 295

			string levelName = Utils.ReadVString(reader);
			string modName = "";
			if (version >= 0xB2 && !isRF2)
				modName = Utils.ReadVString(reader);

			Logger.Info(logSrc, $"Parsing {levelName}...");
			if (modName.Length > 0)
				Logger.Info(logSrc, $"Required mod: {modName}");
			Logger.Info(logSrc, $"RFL version: {version}");
			if (isRF2)
			{
				Logger.Info(logSrc, $"Red Faction 2 RFL detected, using RF2 geometry parsing");
			}
			else if (isAlpine)
			{
				Logger.Info(logSrc, $"Red Faction 1 (Alpine Faction) RFL detected, using RF1 geometry parsing");
			}
			else if (isRF1)
			{
				Logger.Info(logSrc, $"Red Faction 1 RFL detected, using RF1 geometry parsing");
			}

				// Read sections
				for (int i = 0; i < numSections; i++)
				{
					long sectionHeaderPos = reader.BaseStream.Position;

					if (reader.BaseStream.Position + 8 > reader.BaseStream.Length)
					{
						Logger.Warn(logSrc, $"Reached EOF unexpectedly while reading section header {i}.");
						break;
					}

					int sectionType = reader.ReadInt32();
					int sectionSize = reader.ReadInt32();
					long sectionStart = reader.BaseStream.Position;
					long sectionEnd = sectionStart + sectionSize;

					Logger.Debug(logSrc, $"Section {i}: Type 0x{sectionType:X}, Size {sectionSize} at 0x{sectionHeaderPos:X}");

					if (sectionEnd > reader.BaseStream.Length)
					{
						Logger.Warn(logSrc, $"Section {i} exceeds file length. Skipping.");
						break;
					}

					if (sectionType == 0x100) // Static Geometry
					{
						Logger.Debug(logSrc, "Found static geometry section (0x100). Parsing...");
						Brush brush = isRF2
							? ParseStaticGeometryFromRF2Rfl(reader, sectionEnd)
							: ParseStaticGeometryFromRfl(reader, sectionEnd, version);
						reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
						mesh.Brushes.Add(brush);
					}
				/*else if (sectionType == 0x02000000) // wip
				{
					Logger.Debug(logSrc, "Found brush geometry section (0x02000000). Parsing...");
					RflBrushParser.ParseBrushesFromRfl(reader, sectionEnd, mesh);
				}*/
				else if (sectionType == 0x0)
					{
						// once we've reached section type 0 we can stop reading further sections
						// rf2 rfls have a section 0 at the end for some reason. rf1 files do not
						break;
					}
					else
					{
						reader.BaseStream.Seek(sectionSize, SeekOrigin.Current); // Skip unknown section
					}
				}

			return mesh;
		}
		public static Brush ParseStaticGeometryFromRF2Rfl(BinaryReader reader, long sectionEnd)
		{
			var brush = new Brush();
			brush.Solid = new Solid();
			var solid = brush.Solid;

			string name = Utils.ReadVString(reader);
			Logger.Debug(logSrc, $"Geometry name: \"{name}\"");

			reader.ReadUInt32(); // unknown field

			int numTextures = reader.ReadInt32();
			Logger.Debug(logSrc, $"numTextures: {numTextures}");
			for (int i = 0; i < numTextures; i++)
			{
				string tex = Utils.ReadVString(reader);
				Logger.Debug(logSrc, $"Texture {i}: \"{tex}\"");
				solid.Textures.Add(tex);
			}

			// Read room data
			int numRooms = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRooms: {numRooms}");
			for (int i = 0; i < numRooms; i++)
			{
				int type = reader.ReadInt32();

				// Bounding box: Vector3 min, Vector3 max
				Vector3 aabbMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				Vector3 aabbMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

				int unk1 = reader.ReadInt32();
				float unk2 = reader.ReadSingle();
				string unusedName = Utils.ReadVString(reader);

				float unk3 = reader.ReadSingle();
				float unk4 = reader.ReadSingle();
				float unk5 = reader.ReadSingle();

				int unk6 = reader.ReadInt32();
				float unk7 = reader.ReadSingle();
				int unk8 = reader.ReadInt32();

				// pre-284 rfls have a conditional check here, but we don't have any of those so don't worry about it

				float unk9 = reader.ReadSingle();
				float unk10 = reader.ReadSingle();
				float unk11 = reader.ReadSingle();
				float unk12 = reader.ReadSingle();
			}

			// Subrooms
			int numSubroomLinks = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSubroomLinks: {numSubroomLinks} @ 0x{reader.BaseStream.Position - 4:X}");

			for (int i = 0; i < numSubroomLinks; i++)
			{
				int roomID = reader.ReadInt32();
				int subroomCount = reader.ReadInt32();

				for (int j = 0; j < subroomCount; j++)
					reader.ReadInt32();
			}

			// I'm not quite sure what these are, but we can read and skip them
			int numURoomLinks = reader.ReadInt32();
			Logger.Debug(logSrc, $"numURoomLinks: {numURoomLinks}");

			for (int i = 0; i < numURoomLinks; i++)
			{
				reader.ReadInt32(); // roomID
				reader.ReadInt32(); // room2ID
			}

			// Portals
			int numPortals = reader.ReadInt32();
			Logger.Debug(logSrc, $"numPortals: {numPortals}");
			reader.BaseStream.Seek(numPortals * 32, SeekOrigin.Current); // Skip past portal data

			// Vertices
			int numVertices = reader.ReadInt32();
			Logger.Debug(logSrc, $"numVertices: {numVertices}");
			var rawVerts = new List<Vector3>();
			for (int i = 0; i < numVertices; i++)
			{
				float x = reader.ReadSingle();
				float y = reader.ReadSingle();
				float z = reader.ReadSingle();
				rawVerts.Add(new Vector3(x, y, z));
			}

			int numFaces = reader.ReadInt32();
			Logger.Debug(logSrc, $"numFaces: {numFaces}");

			List<Vector3> finalVerts = new();
			List<Vector2> finalUVs = new();
			List<int> indices = new();

			for (int i = 0; i < numFaces; i++)
			{
				long start = reader.BaseStream.Position;
				Logger.Debug(logSrc, $"Reading face {i} at 0x{start:X}");

				Vector3 normal = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				float dist = reader.ReadSingle();

				int textureIndex = reader.ReadInt32(); // TexID
				reader.ReadUInt32(); // skip A
				reader.ReadUInt32(); // skip B
				reader.ReadUInt32(); // skip C

				uint faceFlags = reader.ReadUInt32();
				reader.ReadUInt32(); // skip smoothing groups

				uint roomIndex;
				uint vertCount;

				// If flag 0x8000 set (not sure what it means), we need to read additional data here and set some additional flags
				if ((faceFlags & 0x8000) != 0)
				{
					reader.ReadSingle(); // skip a float
					float tmp = reader.ReadSingle();
					if (Math.Abs(tmp - 1.0f) < 0.001f)
						faceFlags |= 0x100000;
					else if (Math.Abs(tmp - 1.35f) < 0.001f)
						faceFlags |= 0x200000;
					else if (Math.Abs(tmp - 1.5f) < 0.001f)
						faceFlags |= 0x400000;
					else
						faceFlags |= 0x800000;
				}

				// Pre-295 rfls only checked these sometimes, but we're always 295
				reader.ReadByte();
				reader.ReadByte();
				reader.ReadByte();

				float extraFloat = reader.ReadSingle(); // affects 0x4000000 - again, not sure what it means
				if (Math.Abs(extraFloat) > 0.0001f)
					faceFlags |= 0x4000000;

				// Now room index and vert count
				roomIndex = reader.ReadUInt32();
				vertCount = reader.ReadUInt32();

				Logger.Debug(logSrc, $"Face {i} has {vertCount} vertices");

				List<int> localIndices = new();

				for (int j = 0; j < vertCount; j++)
				{
					uint rawVid = reader.ReadUInt32(); // Vertex index
					float u = reader.ReadSingle();
					float v = reader.ReadSingle();

					byte r = reader.ReadByte();
					byte g = reader.ReadByte();
					byte b = reader.ReadByte();
					byte a = reader.ReadByte();

					if (rawVid >= rawVerts.Count)
					{
						Logger.Warn(logSrc, $"Invalid vertex index {rawVid} on face {i}");
						break;
					}

					Vector3 pos = rawVerts[(int)rawVid];
					Vector2 uv = new(u, v);

					int newIdx = finalVerts.Count;
					finalVerts.Add(pos);
					finalUVs.Add(uv);
					localIndices.Add(newIdx);
				}

				// Face visibility filtering
				bool isInvisible = (faceFlags & 0x2000) != 0;
				bool isHole = (faceFlags & 0x0008) != 0;
				bool isAlpha = (faceFlags & 0x0040) != 0;
				bool isDetail = (faceFlags & 0x0010) != 0;

				if (!Config.IncludeInvisibleFaces && isInvisible) continue;
				if (!Config.IncludeHoleFaces && isHole) continue;
				if (!Config.IncludeAlphaFaces && isAlpha) continue;
				if (!Config.IncludeDetailFaces && isDetail) continue;

				// Triangulate
				for (int j = 1; j < localIndices.Count - 1; j++)
				{
					int i0 = localIndices[0];
					int i1 = localIndices[j];
					int i2 = localIndices[j + 1];

					indices.Add(i0);
					indices.Add(i1);
					indices.Add(i2);

					solid.Faces.Add(new Face
					{
						Vertices = new List<int> { i0, i1, i2 },
						TextureIndex = textureIndex
					});
				}

				long end = reader.BaseStream.Position;
				Logger.Debug(logSrc, $"Face {i} size: {end - start} bytes");
			}

			brush.Vertices = finalVerts;
			brush.UVs = finalUVs;
			brush.Indices = indices;

			Logger.Info(logSrc, $"Parsed RF2 RFL static geometry with {brush.Vertices.Count} verts and {solid.Faces.Count} faces");
			return brush;
		}

		public static Brush ParseStaticGeometryFromRfl(BinaryReader reader, long sectionEnd, int rflVersion)
		{
			var brush = new Brush();
			brush.Solid = new Solid();
			var solid = brush.Solid;

			// Correct static geometry read order
			uint unknown1 = reader.ReadUInt32();
			uint modifiabilityFlags = reader.ReadUInt32();
			string name = Utils.ReadVString(reader);
			Logger.Debug(logSrc, $"Geometry name: \"{name}\"");

			int numTextures = reader.ReadInt32();
			Logger.Debug(logSrc, $"numTextures: {numTextures}");
			for (int i = 0; i < numTextures; i++)
			{
				string tex = Utils.ReadVString(reader);
				Logger.Debug(logSrc, $"Texture {i}: \"{tex}\"");
				solid.Textures.Add(tex);
			}

			int numFaceScrollData = reader.ReadInt32();
			Logger.Debug(logSrc, $"numFaceScrollData: {numFaceScrollData}");
			reader.BaseStream.Seek(numFaceScrollData * 12, SeekOrigin.Current);

			int numRooms = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRooms: {numRooms}");
			for (int i = 0; i < numRooms; i++)
			{
				long roomStart = reader.BaseStream.Position;

				int id = reader.ReadInt32();
				Vector3 aabbMin = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				Vector3 aabbMax = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				byte isSkyroom = reader.ReadByte();
				byte isCold = reader.ReadByte();
				byte isOutside = reader.ReadByte();
				byte isAirlock = reader.ReadByte();
				byte isLiquidRoom = reader.ReadByte();
				byte hasAmbientLight = reader.ReadByte();
				byte isSubroom = reader.ReadByte();
				byte hasAlpha = reader.ReadByte();
				float life = reader.ReadSingle();

				// Read eax_effect if version >= 0xB4
				if (rflVersion >= 0xB4)
				{
					string eaxEffect = Utils.ReadVString(reader);
				}

				// Read liquid_properties
				if (isLiquidRoom == 1)
				{
					float depth = reader.ReadSingle();
					byte r = reader.ReadByte();
					byte g = reader.ReadByte();
					byte b = reader.ReadByte();
					byte a = reader.ReadByte();
					string surfaceTexture = Utils.ReadVString(reader);
					float visibility = reader.ReadSingle();
					int liquidType = reader.ReadInt32();
					int liquidAlpha = reader.ReadInt32();
					byte containsPlankton = reader.ReadByte();
					int ppmU = reader.ReadInt32();
					int ppmV = reader.ReadInt32();
					float angle = reader.ReadSingle();
					int waveform = reader.ReadInt32();
					float scrollU = reader.ReadSingle();
					float scrollV = reader.ReadSingle();
				}

				// Read ambient_color
				if (hasAmbientLight == 1)
				{
					byte r = reader.ReadByte();
					byte g = reader.ReadByte();
					byte b = reader.ReadByte();
					byte a = reader.ReadByte();
				}

				long roomEnd = reader.BaseStream.Position;
				Logger.Debug(logSrc, $"Room {i}: read {roomEnd - roomStart} bytes @ 0x{roomStart:X} → 0x{roomEnd:X}");
			}
			Logger.Debug(logSrc, $"Pos after parsing all rooms: 0x{reader.BaseStream.Position:X}");

			int numSubroomLists = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSubroomLists: {numSubroomLists}");
			List<SubroomList> subroomLists = new();

			for (int i = 0; i < numSubroomLists; i++)
			{
				SubroomList list = new SubroomList();
				list.RoomIndex = reader.ReadInt32();
				list.NumSubrooms = reader.ReadInt32();

				for (int j = 0; j < list.NumSubrooms; j++)
				{
					list.SubroomIndices.Add(reader.ReadInt32());
				}

				subroomLists.Add(list);
			}

			int numPortals = reader.ReadInt32();
			Logger.Debug(logSrc, $"numPortals: {numPortals}");
			List<Portal> portals = new();

			for (int i = 0; i < numPortals; i++)
			{
				Portal portal = new Portal();
				portal.RoomIndex1 = reader.ReadInt32();
				portal.RoomIndex2 = reader.ReadInt32();
				portal.Point1 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				portal.Point2 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				portals.Add(portal);
			}

			int numVertices = reader.ReadInt32();
			Logger.Debug(logSrc, $"numVertices: {numVertices}");

			List<Vector3> vertices = new();
			for (int i = 0; i < numVertices; i++)
			{
				float x = reader.ReadSingle();
				float y = reader.ReadSingle();
				float z = reader.ReadSingle();
				vertices.Add(new Vector3(x, y, z));
			}
			brush.Vertices = vertices;

			int numFaces = reader.ReadInt32();
			Logger.Debug(logSrc, $"numFaces: {numFaces}");

			List<Vector3> finalVerts = new();
			List<Vector2> finalUVs = new();
			List<int> indices = new();
			Logger.Debug(logSrc, $"Include portal faces? {Config.IncludePortalFaces}");
			Logger.Debug(logSrc, $"Include faces from detail brushes? {Config.IncludeDetailFaces}");
			Logger.Debug(logSrc, $"Include faces with alpha textures? {Config.IncludeAlphaFaces}");
			Logger.Debug(logSrc, $"Include faces with shoot-through alpha textures? {Config.IncludeHoleFaces}");
			Logger.Debug(logSrc, $"Include liquid surfaces? {Config.IncludeLiquidFaces}");
			Logger.Debug(logSrc, $"Include Show Sky faces? {Config.IncludeSkyFaces}");
			Logger.Debug(logSrc, $"Include invisible faces? {Config.IncludeInvisibleFaces}");

			for (int i = 0; i < numFaces; i++)
			{
				Vector3 normal = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				float dist = reader.ReadSingle();

				int textureIndex = reader.ReadInt32();
				int surfaceIndex = reader.ReadInt32();
				int faceId = reader.ReadInt32();
				reader.ReadInt32(); // reserved1[0]
				reader.ReadInt32(); // reserved1[1]
				int portalIndexPlus2 = reader.ReadInt32();
				ushort faceFlags = reader.ReadUInt16();
				reader.ReadUInt16(); // reserved2
				reader.ReadUInt32(); // smoothing_groups
				reader.ReadInt32(); // room_index

				bool isPortalFace = portalIndexPlus2 >= 2;
				bool isDetailFace = (faceFlags & FaceFlags.IsDetail) != 0;
				bool isAlphaFace = (faceFlags & FaceFlags.HasAlpha) != 0;
				bool isHoleFace = (faceFlags & FaceFlags.HasHoles) != 0;
				bool isLiquidFace = (faceFlags & FaceFlags.LiquidSurface) != 0;
				bool isSkyFace = (faceFlags & FaceFlags.ShowSky) != 0;
				bool isInvisibleFace = (faceFlags & FaceFlags.IsInvisible) != 0;

				int vertCount = reader.ReadInt32();

				List<int> localIndices = new();

				for (int j = 0; j < vertCount; j++)
				{
					int vertIdx = reader.ReadInt32();
					float u = reader.ReadSingle();
					float v = reader.ReadSingle();

					if (vertIdx < 0 || vertIdx >= brush.Vertices.Count)
					{
						Logger.Warn(logSrc, $"WARNING: vertIdx {vertIdx} out of range");
						continue;
					}

					Vector3 pos = brush.Vertices[vertIdx];
					Vector2 uv = new Vector2(u, v);

					if (surfaceIndex != -1)
					{
						reader.ReadSingle(); // lightmap u
						reader.ReadSingle(); // lightmap v
					}

					int newIdx = finalVerts.Count;
					finalVerts.Add(pos);
					finalUVs.Add(uv);
					localIndices.Add(newIdx);
				}

				for (int j = 1; j < localIndices.Count - 1; j++)
				{
					int i0 = localIndices[0];
					int i1 = localIndices[j];
					int i2 = localIndices[j + 1];

					indices.Add(i0);
					indices.Add(i1);
					indices.Add(i2);

					// omit potentially unwanted faces
					if (!Config.IncludePortalFaces && isPortalFace)
						break;
					if (!Config.IncludeDetailFaces && isDetailFace)
						break;
					if (!Config.IncludeAlphaFaces && isAlphaFace)
						break;
					if (!Config.IncludeHoleFaces && isHoleFace)
						break;
					if (!Config.IncludeLiquidFaces && isLiquidFace)
						break;
					if (!Config.IncludeSkyFaces && isSkyFace)
						break;
					if (!Config.IncludeInvisibleFaces && isInvisibleFace)
						break;

					solid.Faces.Add(new Face
					{
						Vertices = new List<int> { i0, i1, i2 },
						TextureIndex = textureIndex
					});
				}
			}

			brush.Vertices = finalVerts;
			brush.UVs = finalUVs;
			brush.Indices = indices;

			int numSurfaces = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSurfaces: {numSurfaces}");
			reader.BaseStream.Seek(84 * numSurfaces, SeekOrigin.Current);

			Logger.Info(logSrc, $"Parsed RF1 RFL static geometry with {brush.Vertices.Count} verts and {solid.Faces.Count} faces.");
			return brush;
		}
	}
	public static class RflBrushParser
	{
		public static void ParseBrushesFromRfl(BinaryReader reader, long sectionEnd, Mesh mesh)
		{
			// Each brush is parsed exactly like in RFG
			int numBrushes = reader.ReadInt32();
			for (int i = 0; i < numBrushes; i++)
			{
				Brush brush = RfgParser.ReadBrush(reader);
				mesh.Brushes.Add(brush);
			}

			// Ensure we're positioned correctly at the end of the section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
		}
	}
}
