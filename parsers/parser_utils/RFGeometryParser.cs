﻿using redux.utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using static redux.utilities.Utils;

namespace redux.parsers.parser_utils
{
	class RFGeometryParser
	{
		private const string logSrc = "RFGeometryParser";
		public static Brush ReadStaticGeometry(BinaryReader reader, int rfl_version)
		{
			var brush = new Brush();
			var (vertices, uvs, faces, indices, solid) = RFGeometryParser.ReadGeometryBody(reader, rfl_version, true);

			brush.Vertices = vertices;          // your per‐vertex positions
			brush.UVs = uvs;                    // your per‐corner UVs
			brush.Indices = indices;            // the index buffer
			brush.Solid = solid;                // contains solid.Textures and solid.Faces

			Logger.Dev(logSrc, $"Parsed static geometry {brush.UID} with {brush.Vertices.Count} verticies, {brush.UVs.Count} faces, flags {brush.Solid.Flags}, life {brush.Solid.Life}, and state {brush.Solid.State}");

			return brush;
		}
		public static Brush ReadBrush(BinaryReader reader, int rfl_version)
		{
			var brush = new Brush();

			// Brush UID
			brush.UID = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading brush {brush.UID}");

			// TBD - unsure on this approach. It fixes an error parsing RF2 l06s2.rfl but I'm not convinced this is the right way to do it
			if (brush.UID == 1)
			{
				reader.BaseStream.Seek(-4, SeekOrigin.Current);
				Logger.Dev(logSrc, "Detected either invalid brush or world static geometry inside brush section, skipping");
				return null;
			}

			// Brush position
			brush.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Logger.Debug(logSrc, $"Brush {brush.UID} position: {brush.Position}");

			// Brush rotation matrix
			Vector3 fwd = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Vector3 right = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			Vector3 up = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			brush.RotationMatrix = new Matrix4x4(
				right.X, right.Y, right.Z, 0,
				up.X, up.Y, up.Z, 0,
				fwd.X, fwd.Y, fwd.Z, 0,
				0, 0, 0, 1
			);

			var (vertices, uvs, faces, indices, solid) = RFGeometryParser.ReadGeometryBody(reader, rfl_version, false);

			brush.Vertices = vertices;          // your per‐vertex positions
			brush.UVs = uvs;                    // your per‐corner UVs
			brush.Indices = indices;            // the index buffer
			brush.Solid = solid;                // contains solid.Textures and solid.Faces

			if (reader.BaseStream.Position + 12 <= reader.BaseStream.Length)
			{
				brush.Solid.Flags = reader.ReadUInt32();
				brush.Solid.Life = reader.ReadInt32();
				brush.Solid.State = reader.ReadInt32();
			}

			Logger.Dev(logSrc, $"Parsed brush {brush.UID} with {brush.Vertices.Count} verticies, {brush.UVs.Count} faces, flags {brush.Solid.Flags}, life {brush.Solid.Life}, and state {brush.Solid.State}");

			return brush;
		}
		public static (List<Vector3> vertices, List<Vector2> uvs, List<Face> faces, List<int> indices, Solid solid) ReadGeometryBody(BinaryReader reader, int rfl_version, bool static_geo)
		{
			var solid = new Solid(); // Solid to hold parsed geometry data

			bool isRF1 = RflUtils.IsRF1(rfl_version);
			bool isRF2 = RflUtils.IsRF2(rfl_version);

			Logger.Dev(logSrc, $"RF1 {isRF1}, RF2 {isRF2}");

			// Skip unknown uint + modifiability (RF1 only)
			if (isRF1 && rfl_version >= 0xC8)
				reader.BaseStream.Seek(8, SeekOrigin.Current);

			string name = ReadVString(reader); // geo name is typically blank

			// Skip unknown data (RF2), old modifiability (RF1 before 0xC8)
			if (isRF2 || (isRF1 && rfl_version < 0xC8))
				reader.ReadUInt32();

			// Textures
			int numTextures = reader.ReadInt32();
			Logger.Debug(logSrc, $"numTextures: {numTextures}");
			for (int i = 0; i < numTextures; i++)
			{
				string tex = ReadVString(reader);

				if (isRF2 && Config.TranslateRF2Textures)
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
			
			// Skip face scroll data (RF1 only)
			if (isRF1 && rfl_version >= 0xB4)
			{
				int numFaceScrollData = reader.ReadInt32();
				Logger.Debug(logSrc, $"numFaceScrollData: {numFaceScrollData}");
				for (int i = 0; i < numFaceScrollData; i++)
					reader.BaseStream.Seek(12, SeekOrigin.Current);
			}
			else if (isRF1 && rfl_version < 0xB4)
			{
				int numUnkData = reader.ReadInt32();
				Logger.Debug(logSrc, $"numUnkData: {numUnkData}");
				for (int i = 0; i < numUnkData; i++)
					reader.BaseStream.Seek(0x29, SeekOrigin.Current);
			}

			// Skip room data (static geometry only)
			int numRooms = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRooms: {numRooms}");
			for (int i = 0; i < numRooms; i++)
			{
				if (isRF2)
				{
					int type = reader.ReadInt32();
					Vector3 aabbMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					Vector3 aabbMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
					int unk1 = reader.ReadInt32();
					float unk2 = reader.ReadSingle();
					string eax_name = ReadVString(reader);
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
				else
				{
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
					if (rfl_version >= 0xB4)
					{
						string eaxEffect = ReadVString(reader);
					}

					// Read liquid_properties
					if (isLiquidRoom == 1)
					{
						float depth = reader.ReadSingle();
						byte r = reader.ReadByte();
						byte g = reader.ReadByte();
						byte b = reader.ReadByte();
						byte a = reader.ReadByte();
						string surfaceTexture = ReadVString(reader);
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
				}
			}

			// Skip subroom data
			int numSubroomLinks = reader.ReadInt32();
			Logger.Debug(logSrc, $"numSubroomLinks: {numSubroomLinks}");
			for (int i = 0; i < numSubroomLinks; i++)
			{
				int roomID = reader.ReadInt32();
				int subroomCount = reader.ReadInt32();
				for (int j = 0; j < subroomCount; j++)
					reader.ReadInt32();
			}

			// Skip uroom data (RF2 only)
			if (isRF2) { 
				int numURoomLinks = reader.ReadInt32();
				Logger.Debug(logSrc, $"numURoomLinks: {numURoomLinks}");
				reader.BaseStream.Seek(numURoomLinks * 8, SeekOrigin.Current);
			}

			// Skip portal data
			int numPortals = reader.ReadInt32();
			Logger.Debug(logSrc, $"numPortals: {numPortals}");
			reader.BaseStream.Seek(numPortals * 32, SeekOrigin.Current);

			// Read raw vertices
			int numRawVerts = reader.ReadInt32();
			Logger.Debug(logSrc, $"numRawVerts: {numRawVerts}");
			var rawVerts = new List<Vector3>(numRawVerts);
			for (int i = 0; i < numRawVerts; i++) 
			{
				float x = reader.ReadSingle();
				float y = reader.ReadSingle();
				float z = reader.ReadSingle();
				rawVerts.Add(new Vector3(x, y, z));
			}

			// Deduplicated vertex position store - merges verticies on the same brush and avoids tearing
			var positionMap = new Dictionary<(int, int, int), int>();
			var uniqueVerts = new List<Vector3>();
			var indices = new List<int>();
			var uvs = new List<Vector2>();

			// Read raw faces
			int numFaces = reader.ReadInt32();
			Logger.Debug(logSrc, $"numFaces: {numFaces}");
			for (int i = 0; i < numFaces; i++)
			{
				long faceStart = reader.BaseStream.Position;
				Logger.Debug(logSrc, $"Reading face {i} at 0x{faceStart:X}");

				reader.BaseStream.Seek(16, SeekOrigin.Current);		// skip plane normal and dist
				int textureIndex = reader.ReadInt32();				// texture index
				reader.BaseStream.Seek(12, SeekOrigin.Current);     // unused data surface_index, face_id, unk

				if (isRF1)
				{
					reader.ReadUInt32();							// reserved1 (RF1 only)
					int portalIndex = reader.ReadInt32();			// portal_index (RF1 only)
					uint faceFlagsRF1 = reader.ReadUInt16();		// RF1 faceFlags is a 16‐bit value
					reader.ReadUInt16();							// reserved2 (RF1 only)
					uint smoothingGroups = reader.ReadUInt32();
					int roomIndex = reader.ReadInt32();
					uint faceFlags32 = faceFlagsRF1;                // redux stores flags in a 32-bit structure to keep consistent with RF2
					int vertCount = reader.ReadInt32();

					Logger.Debug(logSrc, $"RF1 face[{i}]: texture={textureIndex}, faceFlags=0x{faceFlagsRF1:X}  vertCount={vertCount}");

					var faceVerts = new List<int>(vertCount);
					var faceUVs = new List<Vector2>(vertCount);

					// set flag bools
					bool isInvisible = (faceFlags32 & 0x2000) != 0;
					bool isFullbright = (faceFlags32 & 0x20) != 0;
					bool isHole = (faceFlags32 & 0x80) != 0;
					bool isAlpha = (faceFlags32 & 0x40) != 0;
					bool isDetail = (faceFlags32 & 0x0010) != 0;
					bool isLiquid = (faceFlags32 & 0x04) != 0;
					bool isPortal = (faceFlags32 & 0x1) != 0;
					bool isSky = (faceFlags32 & 0x01) != 0;

					for (int vi = 0; vi < vertCount; vi++)
					{
						int rawIdx = reader.ReadInt32();      // index into rawVerts
						float u = reader.ReadSingle();
						float v = reader.ReadSingle();
						
						// lmap coords are only present when face is NOT fullbright or invisible
						if (static_geo && !isFullbright && !isInvisible)
						{
							float lmapu = reader.ReadSingle();
							float lmapv = reader.ReadSingle();
						}
						

						if (rawIdx < 0 || rawIdx >= rawVerts.Count)
							continue;

						Vector3 pos = rawVerts[rawIdx];
						var key = ((int)(pos.X * 1000), (int)(pos.Y * 1000), (int)(pos.Z * 1000));
						if (!positionMap.TryGetValue(key, out int xi))
						{
							xi = uniqueVerts.Count;
							uniqueVerts.Add(pos);
							positionMap[key] = xi;
						}

						faceVerts.Add(xi);
						faceUVs.Add(new Vector2(u, v));
						uvs.Add(new Vector2(u, v));
						indices.Add(xi);
					}

					// Face visibility filtering
					if (!Config.IncludeInvisibleFaces && isInvisible) continue;
					if (!Config.IncludeHoleFaces && isHole) continue;
					if (!Config.IncludeAlphaFaces && isAlpha) continue;
					if (!Config.IncludeDetailFaces && isDetail) continue;
					if (!Config.IncludeLiquidFaces && isLiquid) continue;
					if (!Config.IncludePortalFaces && isPortal) continue;
					if (!Config.IncludeSkyFaces && isSky) continue;
					
					// Triangulate or add ngon faces
					if (Config.TriangulatePolygons && faceVerts.Count > 3)
					{
						Logger.Debug(logSrc, $"Triangulating RF1 ngon (count={faceVerts.Count})");
						for (int k = 1; k < faceVerts.Count - 1; k++)
						{
							solid.Faces.Add(new Face
							{
								TextureIndex = textureIndex,
								Vertices = new List<int> { faceVerts[0], faceVerts[k], faceVerts[k + 1] },
								UVs = new List<Vector2> { faceUVs[0], faceUVs[k], faceUVs[k + 1] },
								FaceFlags = (ushort)faceFlagsRF1
							});
						}
					}
					else
					{
						solid.Faces.Add(new Face
						{
							TextureIndex = textureIndex,
							Vertices = faceVerts,
							UVs = faceUVs,
							FaceFlags = (ushort)faceFlagsRF1
						});
					}

				}
				else
				{
					uint faceFlagsRF2 = reader.ReadUInt32();
					uint smoothingGroups = reader.ReadUInt32();

					if ((faceFlagsRF2 & 0x8000) != 0)
					{
						reader.ReadSingle();												// skip a float
						float tmp = reader.ReadSingle();
						if (Math.Abs(tmp - 1.0f) < 0.001f) faceFlagsRF2 |= 0x100000;
						else if (Math.Abs(tmp - 1.35f) < 0.001f) faceFlagsRF2 |= 0x200000;
						else if (Math.Abs(tmp - 1.5f) < 0.001f) faceFlagsRF2 |= 0x400000;
						else faceFlagsRF2 |= 0x800000;
					}

					// ¯\_(ツ)_/¯
					if (rfl_version >= 0x127)
					{
						reader.BaseStream.Seek(3, SeekOrigin.Current);						// skip 3 bytes
						float flagDecider = reader.ReadSingle();
						if (Math.Abs(flagDecider) > 0.0001f)
						{
							faceFlagsRF2 |= 0x4000000;
						}
					}

					reader.ReadUInt32();                                                    // room index

					int vertCount = reader.ReadInt32();

					Logger.Debug(logSrc, $"RF2 face[{i}]: texture={textureIndex}, faceFlags=0x{faceFlagsRF2:X}, vertCount={vertCount}");

					var faceVerts = new List<int>(vertCount);
					var faceUVs = new List<Vector2>(vertCount);

					bool isInvisible = (faceFlagsRF2 & 0x2000) != 0;
					bool isFullbright = (faceFlagsRF2 & 0x20) != 0;
					bool isHole = (faceFlagsRF2 & 0x80) != 0;
					bool isAlpha = (faceFlagsRF2 & 0x40) != 0;
					bool isDetail = (faceFlagsRF2 & 0x0010) != 0;
					bool isLiquid = (faceFlagsRF2 & 0x04) != 0;
					bool isPortal = (faceFlagsRF2 & 0x1) != 0;
					bool isSky = (faceFlagsRF2 & 0x01) != 0;
					bool isMirrored = (faceFlagsRF2 & 0x02) != 0;

					for (int vi = 0; vi < vertCount; vi++)
					{
						uint rawIdx = reader.ReadUInt32();
						float u = reader.ReadSingle();
						float v = reader.ReadSingle();
						reader.BaseStream.Seek(4, SeekOrigin.Current);                      // vertex colour (RGBA)						

						if (rawIdx >= rawVerts.Count)
							continue;

						Vector3 pos = rawVerts[(int)rawIdx];
						var key = ((int)(pos.X * 1000), (int)(pos.Y * 1000), (int)(pos.Z * 1000));
						if (!positionMap.TryGetValue(key, out int xi))
						{
							xi = uniqueVerts.Count;
							uniqueVerts.Add(pos);
							positionMap[key] = xi;
						}

						faceVerts.Add(xi);
						faceUVs.Add(new Vector2(u, v));
						uvs.Add(new Vector2(u, v));
						indices.Add(xi);
					}

					// Face visibility filtering
					if (!Config.IncludeInvisibleFaces && isInvisible) continue;
					if (!Config.IncludeHoleFaces && isHole) continue;
					if (!Config.IncludeAlphaFaces && isAlpha) continue;
					if (!Config.IncludeDetailFaces && isDetail) continue;
					if (!Config.IncludeLiquidFaces && isLiquid) continue;
					if (!Config.IncludePortalFaces && isPortal) continue;
					if (!Config.IncludeSkyFaces && isSky) continue;

					// Triangulate or add polygon faces for RF2
					if (Config.TriangulatePolygons && faceVerts.Count > 3)
					{
						Logger.Debug(logSrc, $"Triangulating RF2 ngon (count={faceVerts.Count})");
						for (int k = 1; k < faceVerts.Count - 1; k++)
						{
							solid.Faces.Add(new Face
							{
								TextureIndex = textureIndex,
								Vertices = new List<int> { faceVerts[0], faceVerts[k], faceVerts[k + 1] },
								UVs = new List<Vector2> { faceUVs[0], faceUVs[k], faceUVs[k + 1] },
								FaceFlags = (ushort)(faceFlagsRF2 & 0xFFFF)	// cast to ushort for storage
							});
						}
					}
					else
					{
						solid.Faces.Add(new Face
						{
							TextureIndex = textureIndex,
							Vertices = faceVerts,
							UVs = faceUVs,
							FaceFlags = (ushort)(faceFlagsRF2 & 0xFFFF)
						});
					}
				}

				long faceEnd = reader.BaseStream.Position;
				Logger.Debug(logSrc, $"Face {i} consumed {(faceEnd - faceStart)} bytes");
			}

			// Skip surface data (RF1 only)
			if (isRF1)
			{
				int numSurfaces = reader.ReadInt32();
				Logger.Debug(logSrc, $"numSurfaces: {numSurfaces}");
				for (int i = 0; i < numSurfaces; i++)
					reader.BaseStream.Seek(96, SeekOrigin.Current);
			}

			// Skip old face scroll data (RF1 only)
			if (isRF1 && rfl_version <= 0xB4)
			{
				int numOldFaceScrollData = reader.ReadInt32();
				Logger.Debug(logSrc, $"numOldFaceScrollData: {numOldFaceScrollData}");
				for (int i = 0; i < numOldFaceScrollData; i++)
					reader.BaseStream.Seek(12, SeekOrigin.Current);
			}

			return (uniqueVerts, uvs, solid.Faces, indices, solid);
		}
	}
}
