using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using static System.Collections.Specialized.BitVector32;
using static System.Net.Mime.MediaTypeNames;

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
				Logger.Info(logSrc, $"Red Faction 2 RFL detected, using RF2 parsing");
				if (Config.TranslateRF2Textures)
				{
					RF2TextureTranslator.LoadRF2TextureTranslations();
					Logger.Info(logSrc, $"Loaded {RF2TextureTranslator.TranslationCount} RF2 texture filename translation definitions");
				}
			}
			else if (isAlpine)
			{
				Logger.Info(logSrc, $"Red Faction 1 (Alpine Faction) RFL detected, using RF1 parsing");
			}
			else if (isRF1)
			{
				Logger.Info(logSrc, $"Red Faction 1 RFL detected, using RF1 parsing");
			}

			if (Config.ParseBrushSectionInstead)
			{
				Logger.Info(logSrc, $"-brushes option used, parsing brush data instead of static geometry");
			}

			if (!Config.TriangulatePolygons)
			{
				Logger.Info(logSrc, $"-ngons option used, not forcing triangulation of parsed polygons");
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

					if (sectionType == 0x100 && !Config.ParseBrushSectionInstead) // Static Geometry section
					{
						Logger.Debug(logSrc, "Found static geometry section (0x100). Parsing...");
						Brush brush = isRF2
							? RflStaticGeometryParser.ParseStaticGeometryFromRF2Rfl(reader, sectionEnd)
							: RflStaticGeometryParser.ParseStaticGeometryFromRF1Rfl(reader, sectionEnd, version);
						reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
						mesh.Brushes.Add(brush);
					}
					else if (sectionType == 0x02000000 && Config.ParseBrushSectionInstead) // Brush section
					{
						Logger.Debug(logSrc, "Found brush geometry section (0x02000000). Parsing...");
						if (isRF2)
						{
							RflBrushParser.ParseBrushesFromRF2Rfl(reader, sectionEnd, mesh);
						}
						else
						{
							RflBrushParser.ParseBrushesFromRF1Rfl(reader, sectionEnd, mesh, version);
						}
					}
					else if (sectionType == 0x0)
					{
						// once we've reached section type 0 we can stop reading further sections
						// rf2 rfls have a section 0 at the end for some reason. rf1 rfls do not
						break;
					}
					else
					{
						reader.BaseStream.Seek(sectionSize, SeekOrigin.Current); // Skip unknown section
					}
				}

			return mesh;
		}
	}
	public static class RflStaticGeometryParser
	{
		private const string logSrc = "RflGeomParser";
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

		public static Brush ParseStaticGeometryFromRF1Rfl(BinaryReader reader, long sectionEnd, int rflVersion)
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
		private const string logSrc = "RflBrushParser";
		public static void ParseBrushesFromRF1Rfl(BinaryReader reader, long sectionEnd, Mesh mesh, int rfl_version)
		{
			int numBrushes = reader.ReadInt32();
			for (int i = 0; i < numBrushes; i++)
			{
				Logger.Debug(logSrc, $"Reading brush {i}/{numBrushes}");
				Brush brush = ReadRF1Brush(reader, rfl_version);
				mesh.Brushes.Add(brush);
			}

			Logger.Info(logSrc, $"Parsed {numBrushes} brushes from RF1 RFL file.");

			// Ensure we're positioned correctly at the end of the section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
		}

		public static void ParseBrushesFromRF2Rfl(BinaryReader reader, long sectionEnd, Mesh mesh)
		{
			int numBrushes = reader.ReadInt32();
			for (int i = 0; i < numBrushes; i++)
			{
				Logger.Debug(logSrc, $"Reading brush {i}/{numBrushes}");
				Brush brush = ReadRF2Brush(reader);
				mesh.Brushes.Add(brush);
			}

			Logger.Info(logSrc, $"Parsed {numBrushes} brushes from RF2 RFL file.");

			// Ensure we're positioned correctly at the end of the section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
		}

		public static Brush ReadRF2Brush(BinaryReader reader)
		{
			var brush = new Brush();
			var solid = new Solid();
			brush.Solid = solid;

			// Read brush metadata
			brush.UID = reader.ReadInt32();
			Logger.Debug(logSrc, $"UID: {brush.UID}");

			brush.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Logger.Debug(logSrc, $"Position: {brush.Position}");

			// Read rotation matrix
			Vector3 fwd = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Vector3 right = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Vector3 up = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

			brush.RotationMatrix = new Matrix4x4(
				right.X, right.Y, right.Z, 0,
				up.X, up.Y, up.Z, 0,
				fwd.X, fwd.Y, fwd.Z, 0,
				0, 0, 0, 1
			);

			string name = Utils.ReadVString(reader); // geometry name
			Logger.Debug(logSrc, $"Geometry name: \"{name}\"");

			reader.ReadUInt32(); // unknown field

			// Read textures
			int numTextures = reader.ReadInt32();
			Logger.Debug(logSrc, $"numTextures: {numTextures}");
			for (int i = 0; i < numTextures; i++)
			{
				string tex = Utils.ReadVString(reader);

				if (Config.TranslateRF2Textures)
				{
					string translatedTex = RF2TextureTranslator.TranslateRF2Texture(tex);
					Logger.Debug(logSrc, $"Texture {i}: \"{tex}\" → \"{translatedTex}\"");
					solid.Textures.Add(translatedTex);
				}
				else
				{
					Logger.Debug(logSrc, $"Texture {i}: \"{tex}\"");
					solid.Textures.Add(tex);
				}
			}

			// Skip room data
			int numRooms = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRooms: {numRooms}");

			for (int i = 0; i < numRooms; i++)
			{
				reader.BaseStream.Seek(64, SeekOrigin.Current);
				Utils.ReadVString(reader);
				reader.BaseStream.Seek(32, SeekOrigin.Current);
			}
			int numSubroomLinks = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSubroomLinks: {numSubroomLinks} @ 0x{reader.BaseStream.Position - 4:X}");

			for (int i = 0; i < numSubroomLinks; i++)
				reader.BaseStream.Seek(8 + 4 * reader.ReadInt32(), SeekOrigin.Current);
			int numURoomLinks = reader.ReadInt32();
			Logger.Debug(logSrc, $"numURoomLinks: {numURoomLinks}");

			reader.BaseStream.Seek(numURoomLinks * 8, SeekOrigin.Current);
			int numPortals = reader.ReadInt32();
			Logger.Debug(logSrc, $"numPortals: {numPortals}");

			reader.BaseStream.Seek(numPortals * 32, SeekOrigin.Current);

			// Read raw vertices
			int numRawVerts = reader.ReadInt32();
			var rawVerts = new List<Vector3>(numRawVerts);
			for (int i = 0; i < numRawVerts; i++)
				rawVerts.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));

			// Deduplicated position store
			var positionMap = new Dictionary<(int, int, int), int>();
			var uniqueVerts = new List<Vector3>();
			var indices = new List<int>();
			var uvs = new List<Vector2>();

			int numFaces = reader.ReadInt32();
			for (int i = 0; i < numFaces; i++)
			{
				reader.BaseStream.Seek(16, SeekOrigin.Current); // normal + dist
				int textureIndex = reader.ReadInt32();
				reader.BaseStream.Seek(12, SeekOrigin.Current); // A, B, C
				uint faceFlags = reader.ReadUInt32();
				reader.ReadUInt32(); // smoothing groups

				if ((faceFlags & 0x8000) != 0)
				{
					reader.ReadSingle();
					float f = reader.ReadSingle();
				}

				reader.BaseStream.Seek(3, SeekOrigin.Current);
				float extra = reader.ReadSingle();

				reader.ReadUInt32(); // roomIndex
				int vertCount = reader.ReadInt32();

				var faceIndices = new List<int>();
				var faceUVs = new List<Vector2>();

				for (int j = 0; j < vertCount; j++)
				{
					uint vid = reader.ReadUInt32();
					float u = reader.ReadSingle();
					float v = reader.ReadSingle();
					reader.BaseStream.Seek(4, SeekOrigin.Current); // RGBA

					if (vid >= rawVerts.Count) continue;

					Vector3 pos = rawVerts[(int)vid];

					// Quantized position key for deduplication
					var key = ((int)(pos.X * 1000), (int)(pos.Y * 1000), (int)(pos.Z * 1000));
					if (!positionMap.TryGetValue(key, out int posIndex))
					{
						posIndex = uniqueVerts.Count;
						uniqueVerts.Add(pos);
						positionMap[key] = posIndex;
					}

					faceIndices.Add(posIndex);
					faceUVs.Add(new Vector2(u, v));
					uvs.Add(new Vector2(u, v));         // Per-face corner UV
					indices.Add(posIndex);              // Index into shared Vertices
				}

				// Add face with deduped position indices
				solid.Faces.Add(new Face
				{
					TextureIndex = textureIndex,
					Vertices = faceIndices,
					UVs = faceUVs
				});

			}

			brush.Vertices = uniqueVerts;
			brush.Indices = indices;
			brush.UVs = uvs;

			// Optional final metadata
			if (reader.BaseStream.Position + 12 <= reader.BaseStream.Length)
			{
				solid.Flags = reader.ReadUInt32();
				solid.Life = reader.ReadInt32();
				solid.State = reader.ReadInt32();
			}

			return brush;
		}


		public static Brush ReadRF1Brush(BinaryReader reader, int rfl_version)
		{
			var brush = new Brush();
			var solid = new Solid();
			brush.Solid = solid;

			brush.UID = reader.ReadInt32();
			brush.Position = new Vector3(
				reader.ReadSingle(),
				reader.ReadSingle(),
				reader.ReadSingle()
			);

			float fwdX = reader.ReadSingle();
			float fwdY = reader.ReadSingle();
			float fwdZ = reader.ReadSingle();
			float rightX = reader.ReadSingle();
			float rightY = reader.ReadSingle();
			float rightZ = reader.ReadSingle();
			float upX = reader.ReadSingle();
			float upY = reader.ReadSingle();
			float upZ = reader.ReadSingle();
			brush.RotationMatrix = new Matrix4x4(
				rightX, rightY, rightZ, 0f,
				upX, upY, upZ, 0f,
				fwdX, fwdY, fwdZ, 0f,
				0f, 0f, 0f, 1f
			);

			// Modifiability fields (RF1 0xC8+)
			if (rfl_version >= 0xC8)
			{
				reader.ReadUInt32();
				reader.ReadUInt32();
			}

			// Geometry name
			string geomName = Utils.ReadVString(reader);
			Logger.Debug(logSrc, $"Geometry name: \"{geomName}\"");

			// Old modifiability (pre‑0xC8)
			if (rfl_version < 0xC8)
				reader.ReadUInt32();

			// Read textures
			int numTextures = reader.ReadInt32();
			Logger.Debug(logSrc, $"numTextures: {numTextures}");
			var textures = new List<string>();
			for (int i = 0; i < numTextures; i++)
			{
				string tex = Utils.ReadVString(reader);
				Logger.Debug(logSrc, $"Texture {i}: \"{tex}\"");
				textures.Add(tex);
			}
			solid.Textures = textures;

			// Face Scroll Data (new format)
			if (rfl_version >= 0xB4)
			{
				int numFaceScrollData = reader.ReadInt32();
				Logger.Debug(logSrc, $"numFaceScrollData: {numFaceScrollData}");
				for (int i = 0; i < numFaceScrollData; i++)
				{
					reader.BaseStream.Seek(12, SeekOrigin.Current);
				}
			}

			int numRooms = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRooms: {numRooms}");
			for (int i = 0; i < numRooms; i++)
			{
				reader.BaseStream.Seek(28, SeekOrigin.Current); // room AABB + flags
			}

			// Subroom Lists
			int numSubroomLists = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSubroomLists: {numSubroomLists}");
			for (int i = 0; i < numSubroomLists; i++)
				reader.BaseStream.Seek(8, SeekOrigin.Current);

			// numPortals
			int numPortals = reader.ReadInt32();
			Logger.Debug(logSrc, $"numPortals: {numPortals}");

			// Read raw vertex positions
			int numRawVerts = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRawVerts: {numRawVerts}");
			var rawVerts = new List<Vector3>(numRawVerts);
			for (int i = 0; i < numRawVerts; i++)
				rawVerts.Add(new Vector3(
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle()
				));

			// Prepare shared pool for (pos + UV) → single index
			var vertMap = new Dictionary<(int x, int y, int z, float u, float v), int>();
			var sharedVerts = new List<Vector3>();
			var sharedUVs = new List<Vector2>();

			// Read faces, build shared pools & per-face indices/UVs
			int numFaces = reader.ReadInt32();
			Logger.Debug(logSrc, $"numFaces: {numFaces}");
			for (int f = 0; f < numFaces; f++)
			{
				// Plane (skip normal + offset)
				for (int i = 0; i < 4; i++)
					reader.ReadSingle();

				int textureIndex = reader.ReadInt32();
				reader.ReadInt32(); // surface_index
				reader.ReadInt32(); // face_id
									//reader.BaseStream.Seek(8, SeekOrigin.Current); // reserved
				int reserved1 = reader.ReadInt32();
				int reserved2 = reader.ReadInt32();
				int portal_index = reader.ReadInt32(); // portal index plus 2
				uint face_flags = reader.ReadUInt16(); // flags
				uint reserved3 = reader.ReadUInt16();
				uint smoothing_groups = reader.ReadUInt32(); // smoothing groups
				int room_index = reader.ReadInt32(); // room index (-1 for movers)
				Logger.Debug(logSrc, $"room_index: {room_index}, face_flags: {face_flags}, portal_index: {portal_index}, smoothing groups: {smoothing_groups}, reserved1: {reserved1}, reserved2: {reserved2}, reserved3: {reserved3}");

				int vertCount = reader.ReadInt32();
				Logger.Debug(logSrc, $"vertCount: {vertCount}");

				var faceIndices = new List<int>(vertCount);
				var faceUVs = new List<Vector2>(vertCount);

				for (int i = 0; i < vertCount; i++)
				{
					int rawIdx = reader.ReadInt32();
					float u = reader.ReadSingle();
					float v = reader.ReadSingle();

					if (rawIdx < 0 || rawIdx >= rawVerts.Count)
						continue;

					var pos = rawVerts[rawIdx];
					// quantize to ints to avoid float‐noise collisions
					var key = (
						(int)(pos.X * 1000),
						(int)(pos.Y * 1000),
						(int)(pos.Z * 1000),
						u, v
					);

					if (!vertMap.TryGetValue(key, out int sharedIdx))
					{
						sharedIdx = sharedVerts.Count;
						sharedVerts.Add(pos);
						sharedUVs.Add(new Vector2(u, v));
						vertMap[key] = sharedIdx;
					}

					faceIndices.Add(sharedIdx);
					faceUVs.Add(new Vector2(u, v));
				}

				// Triangulate ngons
				if (Config.TriangulatePolygons && faceIndices.Count > 3)
				{
					Logger.Debug(logSrc, $"Triangulating ngon face with {faceIndices.Count} vertices on brush {brush.UID}.");
					for (int j = 1; j < faceIndices.Count - 1; j++)
					{
						solid.Faces.Add(new Face
						{
							TextureIndex = textureIndex,
							Vertices = new List<int> {
						faceIndices[0], faceIndices[j], faceIndices[j+1]
					},
							UVs = new List<Vector2> {
						faceUVs[0], faceUVs[j], faceUVs[j+1]
					}
						});
					}
				}
				else
				{
					solid.Faces.Add(new Face
					{
						TextureIndex = textureIndex,
						Vertices = faceIndices,
						UVs = faceUVs
					});
				}
			}

			// Ship shared pools back onto the brush
			brush.Vertices = sharedVerts;
			brush.UVs = sharedUVs;

			// Surfaces
			int numSurfaces = reader.ReadInt32();
			for (int i = 0; i < numSurfaces; i++)
			{
				reader.BaseStream.Seek(96, SeekOrigin.Current); // each surface is 96 bytes
			}

			// Face Scroll Data (old format)
			if (rfl_version <= 0xB4)
			{
				int numFaceScrollDataOld = reader.ReadInt32();
				Logger.Debug(logSrc, $"numFaceScrollDataOld: {numFaceScrollDataOld}");
				for (int i = 0; i < numFaceScrollDataOld; i++)
				{
					reader.BaseStream.Seek(12, SeekOrigin.Current);
				}
			}

			solid.Flags = reader.ReadUInt32();
			solid.Life = reader.ReadInt32();
			solid.State = reader.ReadInt32();

			Logger.Debug(logSrc, $"Parsed brush {brush.UID} with {brush.Vertices.Count} verticies, {brush.UVs.Count} faces, flags {brush.Solid.Flags}, life {brush.Solid.Life}, and state {brush.Solid.State}");

			return brush;
		}
		private static void ReadRF1Face(BinaryReader reader, Brush brush, List<Vector3> vertices)
		{
			// Plane (skip normal + offset)
			for (int i = 0; i < 4; i++)
				reader.ReadSingle();

			int textureIndex = reader.ReadInt32();
			reader.ReadInt32(); // surface_index
			reader.ReadInt32(); // face_id
			//reader.BaseStream.Seek(8, SeekOrigin.Current); // reserved
			int reserved1 = reader.ReadInt32();
			int reserved2 = reader.ReadInt32();
			int portal_index = reader.ReadInt32(); // portal index plus 2
			uint face_flags = reader.ReadUInt16(); // flags
			uint reserved3 = reader.ReadUInt16();
			uint smoothing_groups = reader.ReadUInt32(); // smoothing groups
			int room_index = reader.ReadInt32(); // room index (-1 for movers)
			Logger.Debug(logSrc, $"room_index: {room_index}, face_flags: {face_flags}, portal_index: {portal_index}, smoothing groups: {smoothing_groups}, reserved1: {reserved1}, reserved2: {reserved2}, reserved3: {reserved3}");

			int numFaceVertices = reader.ReadInt32();

			Logger.Debug(logSrc, $"numFaceVertices: {numFaceVertices}");

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

			if (Config.TriangulatePolygons)
			{
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
			else
			{
				if (faceIndices.Count > 3)
				{
					Logger.Debug(logSrc, $"Parsing ngon face with {faceIndices.Count} vertices.");
				}

				brush.Solid.Faces.Add(new Face
				{
					Vertices = faceIndices,
					TextureIndex = textureIndex
				});
			}
		}
	}
}
