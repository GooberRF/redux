using redux.parsers.parser_utils;
using redux.utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using static redux.utilities.Utils;
using static System.Collections.Specialized.BitVector32;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace redux.parsers
{
	// root rfl parser
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
				throw new Exception("Invalid RFL file: wrong magic.");

			// Read header
			int version = reader.ReadInt32();
			uint timestamp = reader.ReadUInt32();
			int playerStartOffset = reader.ReadInt32();
			int levelInfoOffset = reader.ReadInt32();
			int numSections = reader.ReadInt32();
			int sectionsSize = reader.ReadInt32();

			// What version of RFL is this?
			bool isAlpine = version >= 0x12C;
			bool isRF1 = version <= 0xC8 || isAlpine;   // <= 201 or >= 300
			bool isRF2 = version == 0x127;                      // 295

			string levelName = ReadVString(reader);
			string modName = "";
			if (version >= 0xB2 && !isRF2)
				modName = ReadVString(reader);

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

				Logger.Info(logSrc, $"Section {i}: Type 0x{sectionType:X}, Size {sectionSize} at 0x{sectionHeaderPos:X}");

				if (sectionEnd > reader.BaseStream.Length)
				{
					Logger.Warn(logSrc, $"Section {i} exceeds file length. Skipping.");
					break;
				}

				if (sectionType == 0x100 && !Config.ParseBrushSectionInstead) // static geometry
				{
					Logger.Debug(logSrc, "Found static geometry section (0x100). Parsing...");
					Brush brush = RFGeometryParser.ReadStaticGeometry(reader, version);
					mesh.Brushes.Add(brush);
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
					
				}
				else if (sectionType == 0x02000000 && Config.ParseBrushSectionInstead) // brushes
				{
					Logger.Debug(logSrc, "Found brush geometry section (0x02000000). Parsing...");
					RflBrushParser.ParseBrushesFromRfl(reader, sectionEnd, mesh, version);
				}
				else if (sectionType == 0x00000300) // lights 
				{
					Logger.Debug(logSrc, "Found lights section (0x00000300). Parsing...");
					mesh.Lights.AddRange(RflLightParser.ParseLightsFromRfl(reader, sectionEnd, version));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);

				}
				else if (sectionType == 0x0900) // level_properties
				{
					Logger.Debug(logSrc, "Found level_properties section (0x0900). Parsing...");
					RflLevelPropertiesParser.ParseLevelPropertiesFromRfl(reader, sectionEnd, version);
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin); // shouldn't be needed, but for safety since RF2 rfls have some unknowns
				}
				else if (sectionType == 0x01000000) // level_info
				{
					Logger.Debug(logSrc, "Found level_info section (0x01000000). Parsing...");
					RflLevelInfoParser.ParseLevelInfoFromRfl(reader, sectionEnd, version);
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin); // shouldn't be needed, but for safety since RF2 rfls have some unknowns
				}
				else if (sectionType == 0x00000700) // mp_respawn_points
				{
					Logger.Debug(logSrc, "Found mp_respawn_points section (0x700). Parsing...");
					mesh.MPRespawnPoints.AddRange(RflMpRespawnPointParser.ParseMpRespawnPoints(reader, sectionEnd));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00000a00) // particle_emitters
				{
					Logger.Debug(logSrc, "Found particle_emitters section (0x00000a00). Parsing...");
					mesh.ParticleEmitters.AddRange(RflParticleEmitterParser.ParseParticleEmittersFromRfl(reader, sectionEnd, version));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00000600) // events
				{
					Logger.Debug(logSrc, "Found events section (0x600). Parsing...");
					mesh.Events.AddRange(RflEventParser.ParseEvents(reader, sectionEnd, version));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00001100) // push_regions
				{
					Logger.Debug(logSrc, "Found push_regions section (0x00001100). Parsing...");
					mesh.PushRegions.AddRange(RflPushRegionParser.ParsePushRegionsFromRfl(reader, sectionEnd));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00060000) // triggers
				{
					Logger.Debug(logSrc, "Found triggers section (0x00060000). Parsing...");
					mesh.Triggers.AddRange(RflTriggerParser.ParseTriggersFromRfl(reader, sectionEnd, version));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00040000) // items
				{
					Logger.Debug(logSrc, "Found items section (0x00040000). Parsing...");
					mesh.Items.AddRange(RflItemParser.ParseItemsFromRfl(reader, sectionEnd, version));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x00000d00) // climbing regions
				{
					Logger.Debug(logSrc, "Found climbing_regions section (0x00000d00). Parsing...");
					mesh.ClimbingRegions.AddRange(RflClimbingRegionParser.ParseClimbingRegionsFromRfl(reader, sectionEnd));
					reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
				}
				else if (sectionType == 0x0) // end section
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

	public static class RflParticleEmitterParser
	{
		private const string logSrc = "RflParticleEmitterParser";

		public static List<ParticleEmitter> ParseParticleEmittersFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			var emitters = new List<ParticleEmitter>();

			int count = reader.ReadInt32();
			Logger.Warn(logSrc, $"Particle emitters count: {count}");
			for (int i = 0; i < count; i++)
			{
				var e = new ParticleEmitter();

				e.UID = reader.ReadInt32();
				Logger.Warn(logSrc, $"Emitter[{i}].UID = {e.UID}");

				e.ClassName = ReadVString(reader);
				Logger.Warn(logSrc, $"Emitter[{i}].ClassName = \"{e.ClassName}\"");

				// Position (vec3)
				e.Position = new Vector3(
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle()
				);
				Logger.Warn(logSrc, $"Emitter[{i}].Position = {e.Position}");

				// Rotation (mat3)
				float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
				float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
				float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
				e.Rotation = new Matrix4x4(
					m00, m01, m02, 0f,
					m10, m11, m12, 0f,
					m20, m21, m22, 0f,
					0f, 0f, 0f, 1f
				);
				Logger.Warn(logSrc, $"Emitter[{i}].Rotation = [ [{m00}, {m01}, {m02}], [{m10}, {m11}, {m12}], [{m20}, {m21}, {m22}] ]");

				e.ScriptName = ReadVString(reader);
				Logger.Warn(logSrc, $"Emitter[{i}].ScriptName = \"{e.ScriptName}\"");

				e.HiddenInEditor = reader.ReadByte() != 0;
				Logger.Warn(logSrc, $"Emitter[{i}].HiddenInEditor = {e.HiddenInEditor}");

				e.Shape = (ParticleEmitterShape)reader.ReadInt32();
				Logger.Warn(logSrc, $"Emitter[{i}].Shape = {e.Shape}");

				e.SphereRadius = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].SphereRadius = {e.SphereRadius}");
				e.PlaneWidth = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].PlaneWidth = {e.PlaneWidth}");
				e.PlaneDepth = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].PlaneDepth = {e.PlaneDepth}");

				e.Texture = ReadVString(reader);
				Logger.Warn(logSrc, $"Emitter[{i}].Texture = \"{e.Texture}\"");

				e.SpawnDelay = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].SpawnDelay = {e.SpawnDelay}");
				e.SpawnRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].SpawnRandomize = {e.SpawnRandomize}");

				e.Velocity = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].Velocity = {e.Velocity}");
				e.VelocityRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].VelocityRandomize = {e.VelocityRandomize}");

				e.Acceleration = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].Acceleration = {e.Acceleration}");

				e.Decay = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].Decay = {e.Decay}");
				e.DecayRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].DecayRandomize = {e.DecayRandomize}");

				e.ParticleRadius = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].ParticleRadius = {e.ParticleRadius}");
				e.ParticleRadiusRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].ParticleRadiusRandomize = {e.ParticleRadiusRandomize}");

				e.GrowthRate = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].GrowthRate = {e.GrowthRate}");
				e.GravityMultiplier = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].GravityMultiplier = {e.GravityMultiplier}");
				e.RandomDirection = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].RandomDirection = {e.RandomDirection}");

				// Colors (RGBA)
				e.ParticleColor = new Vector4(
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f
				);
				Logger.Warn(logSrc, $"Emitter[{i}].ParticleColor = {e.ParticleColor}");
				e.FadeToColor = new Vector4(
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f,
					reader.ReadByte() / 255f
				);
				Logger.Warn(logSrc, $"Emitter[{i}].FadeToColor = {e.FadeToColor}");

				// Flags
				e.EmitterFlags = reader.ReadUInt32();
				Logger.Warn(logSrc, $"Emitter[{i}].EmitterFlags = 0x{e.EmitterFlags:X8}");
				e.ParticleFlags = reader.ReadUInt16();
				Logger.Warn(logSrc, $"Emitter[{i}].ParticleFlags = 0x{e.ParticleFlags:X4}");

				// Four 4-bit values packed into a UInt16
				ushort nibblePack = reader.ReadUInt16();
				e.Stickiness = (byte)(nibblePack >> 12 & 0xF);
				e.Bounciness = (byte)(nibblePack >> 8 & 0xF);
				e.PushEffect = (byte)(nibblePack >> 4 & 0xF);
				e.Swirliness = (byte)(nibblePack & 0xF);
				Logger.Warn(logSrc, $"Emitter[{i}].Stickiness={e.Stickiness}, Bounciness={e.Bounciness}, PushEffect={e.PushEffect}, Swirliness={e.Swirliness}");

				e.InitiallyOn = reader.ReadByte() != 0;
				Logger.Warn(logSrc, $"Emitter[{i}].InitiallyOn = {e.InitiallyOn}");
				e.TimeOn = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].TimeOn = {e.TimeOn}");
				e.TimeOnRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].TimeOnRandomize = {e.TimeOnRandomize}");

				e.TimeOff = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].TimeOff = {e.TimeOff}");
				e.TimeOffRandomize = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].TimeOffRandomize = {e.TimeOffRandomize}");

				e.ActiveDistance = reader.ReadSingle();
				Logger.Warn(logSrc, $"Emitter[{i}].ActiveDistance = {e.ActiveDistance}");

				emitters.Add(e);
			}

			// Skip any padding or unknown data
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
			return emitters;
		}
	}

	public static class RflLevelInfoParser
	{
		private const string logSrc = "RflLevelInfoParser";

		public static void ParseLevelInfoFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			int unknown = reader.ReadInt32();
			Logger.Info(logSrc, $"Unk value: {unknown}");

			string levelName = ReadVString(reader);
			Logger.Info(logSrc, $"Level name: \"{levelName}\"");

			string author = ReadVString(reader);
			Logger.Info(logSrc, $"Level author: \"{author}\"");

			string date = ReadVString(reader);
			Logger.Info(logSrc, $"Save date/time: \"{date}\"");

			byte hasMovers = reader.ReadByte();
			Logger.Info(logSrc, $"Has movers: {hasMovers}");

			byte multiplayer = reader.ReadByte();
			Logger.Info(logSrc, $"Multiplayer level: {multiplayer}");

			// Editor view layouts
			for (int i = 0; i < 4; i++)
			{
				long cfgStart = reader.BaseStream.Position;
				int viewType = reader.ReadInt32();
				Logger.Debug(logSrc, $"editor_view_config[{i}].view_type = {viewType}");

				if (viewType == (int)EditorViewType.FreeLook)
				{
					// pos_3d (vec3)
					float x = reader.ReadSingle();
					float y = reader.ReadSingle();
					float z = reader.ReadSingle();
					Logger.Debug(logSrc, $"editor_view_config[{i}].pos_3d = ({x}, {y}, {z})");
				}
				else
				{
					// pos_2d (4 × f4)
					float[] pos2d = new float[4];
					for (int j = 0; j < 4; j++)
						pos2d[j] = reader.ReadSingle();
					Logger.Debug(logSrc, $"editor_view_config[{i}].pos_2d = [{string.Join(", ", pos2d)}]");
				}

				// rot (mat3)
				float[,] m = new float[3, 3];
				for (int row = 0; row < 3; row++)
					for (int col = 0; col < 3; col++)
						m[row, col] = reader.ReadSingle();
				Logger.Debug(logSrc,
					$"editor_view_config[{i}].rot = [\n" +
					$"  [{m[0, 0]}, {m[0, 1]}, {m[0, 2]}],\n" +
					$"  [{m[1, 0]}, {m[1, 1]}, {m[1, 2]}],\n" +
					$"  [{m[2, 0]}, {m[2, 1]}, {m[2, 2]}]\n" +
					$"]");

				long cfgSize = reader.BaseStream.Position - cfgStart;
				Logger.Debug(logSrc, $"editor_view_config[{i}] size = {cfgSize} bytes");
			}

			// prints garbage data in this section at the end of RF2 sections, but not RF1
			/*int extraIndex = 0;
			while (reader.BaseStream.Position < sectionEnd)
			{
				var b = reader.ReadByte();
				Logger.Warn(logSrc, $"level_info extra byte[{extraIndex}] = 0x{b:X2}");
				extraIndex++;
			}*/
		}
	}

	public static class RflClimbingRegionParser
	{
		private const string logSrc = "RflClimbingRegionParser";

		public static List<ClimbingRegion> ParseClimbingRegionsFromRfl(BinaryReader reader, long sectionEnd)
		{
			var regions = new List<ClimbingRegion>();

			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} climbing regions…");

			for (int i = 0; i < count; i++)
			{
				var cr = new ClimbingRegion();

				cr.UID = reader.ReadInt32();
				cr.ClassName = ReadVString(reader);
				cr.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

				// mat3 comes in forward, right, up order → embed into Matrix4x4
				var f = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				var r = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				var u = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				cr.Rotation = new Matrix4x4(
					r.X, r.Y, r.Z, 0f,
					u.X, u.Y, u.Z, 0f,
					f.X, f.Y, f.Z, 0f,
					0f, 0f, 0f, 1f);

				cr.ScriptName = ReadVString(reader);
				cr.HiddenInEditor = reader.ReadByte() != 0;
				cr.Type = reader.ReadInt32();
				cr.Extents = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

				Logger.Dev(logSrc,
					$"ClimbingRegion[{i}] UID={cr.UID}, Class=\"{cr.ClassName}\", Pos={cr.Position}, " +
					$"Rot=\n{cr.Rotation}, Script=\"{cr.ScriptName}\", Hidden={cr.HiddenInEditor}, " +
					$"Type={cr.Type}, Extents={cr.Extents}");

				regions.Add(cr);
			}

			// ensure we position at the end no matter what
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
			return regions;
		}
	}

	public static class RflItemParser
	{
		private const string logSrc = "RflItemParser";

		public static List<RflItem> ParseItemsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			var items = new List<RflItem>();
			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} items…");

			for (int i = 0; i < count; i++)
			{
				var it = new RflItem();

				it.UID = reader.ReadInt32();
				Logger.Dev(logSrc, $"Item[{i}] UID = {it.UID}");

				it.ClassName = ReadVString(reader);
				Logger.Dev(logSrc, $"Item[{i}] ClassName = \"{it.ClassName}\"");

				it.Position = new Vector3(
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle()
				);
				Logger.Dev(logSrc, $"Item[{i}] Position = {it.Position}");

				// 3×3 rotation matrix → Matrix4x4
				var m00 = reader.ReadSingle(); var m01 = reader.ReadSingle(); var m02 = reader.ReadSingle();
				var m10 = reader.ReadSingle(); var m11 = reader.ReadSingle(); var m12 = reader.ReadSingle();
				var m20 = reader.ReadSingle(); var m21 = reader.ReadSingle(); var m22 = reader.ReadSingle();
				it.Rotation = new Matrix4x4(
					m00, m01, m02, 0,
					m10, m11, m12, 0,
					m20, m21, m22, 0,
					0, 0, 0, 1
				);
				Logger.Dev(logSrc, $"Item[{i}] Rotation = {it.Rotation}");

				it.ScriptName = ReadVString(reader);
				Logger.Dev(logSrc, $"Item[{i}] ScriptName = \"{it.ScriptName}\"");

				it.HiddenInEditor = reader.ReadByte() != 0;
				Logger.Dev(logSrc, $"Item[{i}] HiddenInEditor = {it.HiddenInEditor}");

				it.Count = reader.ReadInt32();
				Logger.Dev(logSrc, $"Item[{i}] Count = {it.Count}");

				it.RespawnTime = reader.ReadInt32();
				Logger.Dev(logSrc, $"Item[{i}] RespawnTime = {it.RespawnTime}");

				it.TeamID = reader.ReadInt32();
				Logger.Dev(logSrc, $"Item[{i}] TeamID = {it.TeamID}");

				// unknown bytes, found only in RF2 rfls
				// 255, 255, 255, 255, 0, 0 in every instance I have found
				if (rfl_version == 0x127)
				{
					var unk1 = reader.ReadByte();
					var unk2 = reader.ReadByte();
					var unk3 = reader.ReadByte();
					var unk4 = reader.ReadByte();
					var unk5 = reader.ReadByte();
					var unk6 = reader.ReadByte();
					Logger.Dev(logSrc, $"unk RF2-specific bytes {unk1}, {unk2}, {unk3}, {unk4}, {unk5}, {unk6}");
				}

				items.Add(it);
			}

			return items;
		}
	}
	public static class RflTriggerParser
	{
		private const string logSrc = "RflTriggerParser";

		public static List<Trigger> ParseTriggersFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			if (rfl_version == 0x127) // 295
			{
				Logger.Warn(logSrc, "RFL version 0x127 (295) is not yet supported for trigger parsing. Skipping triggers.");
				return new List<Trigger>();
			}

			var list = new List<Trigger>();
			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} triggers…");

			for (int i = 0; i < count; i++)
			{
				var t = new Trigger();
				t.UID = reader.ReadInt32();
				t.ScriptName = ReadVString(reader);
				t.HiddenInEditor = reader.ReadByte() != 0;
				t.Shape = (TriggerShape)reader.ReadInt32();
				t.ResetsAfter = reader.ReadSingle();
				t.ResetsTimes = reader.ReadInt32();
				t.UseKeyRequired = reader.ReadByte() != 0;
				t.KeyName = ReadVString(reader);
				t.WeaponActivates = reader.ReadByte() != 0;
				t.ActivatedBy = (TriggerActivatedBy)reader.ReadByte();
				t.IsNpc = reader.ReadByte() != 0;
				t.IsAuto = reader.ReadByte() != 0;
				t.InVehicle = reader.ReadByte() != 0;

				// position
				t.Position = new Vector3(
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle()
				);

				if (t.Shape == TriggerShape.Sphere)
				{
					t.SphereRadius = reader.ReadSingle();
				}
				else
				{
					// oriented/axis‐aligned box
					// rotation
					float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
					float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
					float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
					t.Rotation = new Matrix4x4(
						m00, m01, m02, 0,
						m10, m11, m12, 0,
						m20, m21, m22, 0,
						0, 0, 0, 1
					);

					// box extents
					t.BoxHeight = reader.ReadSingle();
					t.BoxWidth = reader.ReadSingle();
					t.BoxDepth = reader.ReadSingle();

					t.OneWay = reader.ReadByte() != 0;
				}

				t.AirlockRoomUID = reader.ReadInt32();
				t.AttachedToUID = reader.ReadInt32();
				t.UseClutterUID = reader.ReadInt32();
				t.Disabled = reader.ReadByte() != 0;

				t.ButtonActiveTime = reader.ReadSingle();
				t.InsideTime = reader.ReadSingle();

				if (rfl_version >= 0xB1)
					t.Team = (TriggerTeam)reader.ReadInt32();

				// links
				int numLinks = reader.ReadInt32();
				t.Links = new List<int>(numLinks);
				for (int j = 0; j < numLinks; j++)
					t.Links.Add(reader.ReadInt32());

				Logger.Debug(logSrc,
					$"Trigger[{i}] UID={t.UID}, Script=\"{t.ScriptName}\", Hidden={t.HiddenInEditor}, " +
					$"Shape={t.Shape}, ResetsAfter={t.ResetsAfter}, ResetsTimes={t.ResetsTimes}, " +
					$"UseKey={t.UseKeyRequired}, Key=\"{t.KeyName}\", WeaponActivates={t.WeaponActivates}, " +
					$"ActivatedBy={t.ActivatedBy}, IsNpc={t.IsNpc}, IsAuto={t.IsAuto}, InVehicle={t.InVehicle}, " +
					$"Pos={t.Position}, " +
					(t.Shape == TriggerShape.Sphere
						? $"Radius={t.SphereRadius}"
						: $"Rot={t.Rotation}, HxWxD=({t.BoxHeight},{t.BoxWidth},{t.BoxDepth}), OneWay={t.OneWay}"
					) +
					$", AirlockUID={t.AirlockRoomUID}, AttachedUID={t.AttachedToUID}, UseClutterUID={t.UseClutterUID}, " +
					$"Disabled={t.Disabled}, BtnTime={t.ButtonActiveTime}, InsideTime={t.InsideTime}, Team={t.Team}, " +
					$"Links=[{string.Join(",", t.Links)}]"
				);

				list.Add(t);
			}

			return list;
		}
	}

	public static class RflPushRegionParser
	{
		private const string logSrc = "RflPushRegionParser";

		public static List<PushRegion> ParsePushRegionsFromRfl(BinaryReader reader, long sectionEnd)
		{
			var regions = new List<PushRegion>();
			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} push regions…");

			for (int i = 0; i < count; i++)
			{
				var pr = new PushRegion();

				pr.UID = reader.ReadInt32();
				pr.ClassName = ReadVString(reader);
				pr.Position = new Vector3(
					reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()
				);

				// rotation matrix
				float r00 = reader.ReadSingle(), r01 = reader.ReadSingle(), r02 = reader.ReadSingle();
				float r10 = reader.ReadSingle(), r11 = reader.ReadSingle(), r12 = reader.ReadSingle();
				float r20 = reader.ReadSingle(), r21 = reader.ReadSingle(), r22 = reader.ReadSingle();
				pr.Rotation = new Matrix4x4(
					r00, r01, r02, 0,
					r10, r11, r12, 0,
					r20, r21, r22, 0,
					0, 0, 0, 1
				);

				pr.ScriptName = ReadVString(reader);
				pr.HiddenInEditor = reader.ReadByte() != 0;

				pr.Shape = (PushRegionShape)reader.ReadInt32();
				if (pr.Shape == PushRegionShape.Sphere)
				{
					pr.Radius = reader.ReadSingle();
				}
				else
				{
					// extents = Vector3
					pr.Extents = new Vector3(
						reader.ReadSingle(),
						reader.ReadSingle(),
						reader.ReadSingle()
					);
				}

				pr.Strength = reader.ReadSingle();

				// flags bitfield
				uint rawFlags = reader.ReadUInt16(); // 16 bits
				pr.JumpPad = (rawFlags & 0x40) != 0;
				pr.DoesntAffectPlayer = (rawFlags & 0x20) != 0;
				pr.Radial = (rawFlags & 0x10) != 0;
				pr.GrowsTowardsBoundary = (rawFlags & 0x08) != 0;
				pr.GrowsTowardsCenter = (rawFlags & 0x04) != 0;
				pr.Grounded = (rawFlags & 0x02) != 0;
				pr.MassIndependent = (rawFlags & 0x01) != 0;

				pr.Turbulence = reader.ReadUInt16();

				// log out everything
				Logger.Debug(logSrc,
					$"PushRegion[{i}] UID={pr.UID}, Class=\"{pr.ClassName}\", Pos={pr.Position}, " +
					$"Script=\"{pr.ScriptName}\", Hidden={pr.HiddenInEditor}, Shape={pr.Shape}, " +
					(pr.Shape == PushRegionShape.Sphere
						? $"Radius={pr.Radius}"
						: $"Extents={pr.Extents}"
					) +
					$", Strength={pr.Strength}, Flags=0x{rawFlags:X4} (JumpPad={pr.JumpPad}, " +
					$"NoPlayer={pr.DoesntAffectPlayer}, Radial={pr.Radial}, " +
					$"BoundaryGrow={pr.GrowsTowardsBoundary}, CenterGrow={pr.GrowsTowardsCenter}, " +
					$"Grounded={pr.Grounded}, MassIndep={pr.MassIndependent}), Turbulence={pr.Turbulence}"
				);

				regions.Add(pr);
			}

			return regions;
		}
	}

	public static class RflEventParser
	{
		private const string logSrc = "RflEventParser";

		public static List<RflEvent> ParseEvents(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			if (rfl_version == 0x127) // 295
			{
				Logger.Warn(logSrc, "RFL version 0x127 (295) is not yet supported for event parsing. Skipping events.");

				int count2 = reader.ReadInt32();
				Logger.Dev(logSrc, $"Reading {count2} events…");

				for (int i = 0; i < count2; i++)
				{
					int extraIndex = 0;
					while (reader.BaseStream.Position < sectionEnd)
					{
						var b = reader.ReadByte();
						//Logger.Warn(logSrc, $"event byte[{extraIndex}] = 0x{b:X2}");
						extraIndex++;
					}
				}

				return new List<RflEvent>();
			}

			var events = new List<RflEvent>();
			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} events…");

			for (int i = 0; i < count; i++)
			{
				var ev = new RflEvent();

				ev.UID = reader.ReadInt32();
				ev.ClassName = ReadVString(reader);
				ev.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				ev.ScriptName = ReadVString(reader);
				ev.HiddenInEditor = reader.ReadByte() != 0;
				ev.Delay = reader.ReadSingle();
				ev.Bool1 = reader.ReadByte() != 0;
				ev.Bool2 = reader.ReadByte() != 0;
				ev.Int1 = reader.ReadInt32();
				ev.Int2 = reader.ReadInt32();
				ev.Float1 = reader.ReadSingle();
				ev.Float2 = reader.ReadSingle();
				ev.Str1 = ReadVString(reader);
				ev.Str2 = ReadVString(reader);

				// links (uid_list)				
				int numLinks = reader.ReadInt32();
				if (numLinks < 0 || reader.BaseStream.Position + numLinks * 4L > sectionEnd)
				{
					Logger.Warn(logSrc, $"Suspicious link count {numLinks} in event[{i}] – clamping to 0");
					numLinks = 0;
				}
				ev.Links = new List<int>(numLinks);
				for (int j = 0; j < numLinks; j++)
					ev.Links.Add(reader.ReadInt32());

				if (rfl_version >= 0x91 && (ev.ClassName == "Teleport" || ev.ClassName == "Play_Vclip" || ev.ClassName == "Teleport_Player")
				|| rfl_version >= 0x98 && ev.ClassName == "Alarm"
				|| rfl_version >= 0x12C && (ev.ClassName == "AF_Teleport_Player" || ev.ClassName == "Clone_Entity")
				|| rfl_version >= 0x12D && ev.ClassName == "Anchor_Marker_Orient")
				{
					ev.HasRotation = true; // used by exporter
					float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
					float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
					float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
					ev.Rotation = new Matrix4x4(
						m00, m01, m02, 0,
						m10, m11, m12, 0,
						m20, m21, m22, 0,
						  0, 0, 0, 1
					);
				}

				if (rfl_version >= 0xB0)
				{
					//reader.BaseStream.Seek(4, SeekOrigin.Current); // color
					ev.RawColor = reader.ReadUInt32();
					Logger.Dev(logSrc, $"Event[{i}] RawColor = 0x{ev.RawColor:X8}");
				}

				Logger.Dev(logSrc,
					$"Event[{i}] UID={ev.UID}, Class=\"{ev.ClassName}\", Pos={ev.Position}, " +
					$"Script=\"{ev.ScriptName}\", Hidden={ev.HiddenInEditor}, Delay={ev.Delay}, " +
					$"Bool1={ev.Bool1}, Bool2={ev.Bool2}, Int1={ev.Int1}, Int2={ev.Int2}, " +
					$"Float1={ev.Float1}, Float2={ev.Float2}, Str1=\"{ev.Str1}\", Str2=\"{ev.Str2}\", " +
					$"Links=[{string.Join(", ", ev.Links)}]"
				);

				if (ev != null)
					events.Add(ev);
			}

			// skip to end of this section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
			return events;
		}
	}

	public static class RflMpRespawnPointParser
	{
		private const string logSrc = "RflMpRespawnPointParser";

		public static List<MpRespawnPoint> ParseMpRespawnPoints(BinaryReader reader, long sectionEnd)
		{
			var list = new List<MpRespawnPoint>();
			int count = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {count} MP respawn points…");

			for (int i = 0; i < count; i++)
			{
				var pt = new MpRespawnPoint();
				pt.UID = reader.ReadInt32();
				pt.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

				// 3×3 rotation matrix → embed into Matrix4x4
				float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
				float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
				float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
				pt.Rotation = new Matrix4x4(
					m00, m01, m02, 0,
					m10, m11, m12, 0,
					m20, m21, m22, 0,
					0, 0, 0, 1
				);

				pt.ScriptName = ReadVString(reader);
				pt.HiddenInEditor = reader.ReadByte() != 0;
				pt.TeamID = reader.ReadInt32();
				pt.RedTeam = reader.ReadByte() != 0;
				pt.BlueTeam = reader.ReadByte() != 0;
				pt.IsBot = reader.ReadByte() != 0;

				// print everything
				Logger.Dev(logSrc,
					$"MP Respawn[{i}] UID={pt.UID}, Pos={pt.Position}, Team={pt.TeamID} (R?{pt.RedTeam}/B?{pt.BlueTeam}), " +
					$"Bot={pt.IsBot}, Hidden={pt.HiddenInEditor}, Script=\"{pt.ScriptName}\""
				);

				list.Add(pt);
			}

			// ensure we skip to end of section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
			return list;
		}
	}


	public static class RflLevelPropertiesParser
	{
		private const string logSrc = "RflLevelPropertiesParser";

		public static void ParseLevelPropertiesFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			// geomod_texture
			string geomodTexture = ReadVString(reader);

			// hardness
			int hardness = reader.ReadInt32();

			// ambient_color (RGBA)
			byte ambR = reader.ReadByte();
			byte ambG = reader.ReadByte();
			byte ambB = reader.ReadByte();
			byte ambA = reader.ReadByte();

			// directional_ambient_light
			byte dirAmbient = reader.ReadByte();

			// fog_color
			byte fogR = reader.ReadByte();
			byte fogG = reader.ReadByte();
			byte fogB = reader.ReadByte();
			byte fogA = reader.ReadByte();

			// fog near/far planes
			float fogNear = reader.ReadSingle();
			float fogFar = reader.ReadSingle();
			Logger.Info(logSrc, "Level properties:");
			Logger.Info(logSrc, $"  GeoMod texture: \"{geomodTexture}\"");
			Logger.Info(logSrc, $"  GeoMod hardness: {hardness}");
			Logger.Info(logSrc, $"  Ambient light colour: R={ambR} G={ambG} B={ambB} A={ambA}");
			Logger.Info(logSrc, $"  Directional ambient light: {(dirAmbient != 0 ? "yes" : "no")}");
			Logger.Info(logSrc, $"  Distance fog colour: R={fogR} G={fogG} B={fogB} A={fogA}");
			Logger.Info(logSrc, $"  Distance fog near clip plane: {fogNear}");
			Logger.Info(logSrc, $"  Distance fog far clip plane:  {fogFar}");

			if (rfl_version == 0x127) // rf2
			{
				uint unk1 = reader.ReadUInt32();
				uint unk2 = reader.ReadUInt32();
				float unk3 = reader.ReadSingle();
				float unk4 = reader.ReadSingle();
				float unk5 = reader.ReadSingle();
				byte unk1R = reader.ReadByte(), unk1G = reader.ReadByte(), unk1B = reader.ReadByte(), unk1A = reader.ReadByte();
				byte unk2R = reader.ReadByte(), unk2G = reader.ReadByte(), unk2B = reader.ReadByte(), unk2A = reader.ReadByte();
				float unk6 = reader.ReadSingle();

				Logger.Info(logSrc, "Guesses at unknown RF2 level properties fields:");
				Logger.Info(logSrc, $"  Unk1 (flags?): 0x{unk1:X8}");
				Logger.Info(logSrc, $"  Unk2 (flags?): 0x{unk2:X8}");
				Logger.Info(logSrc, $"  maybeViewDistance: {unk3}");
				Logger.Info(logSrc, $"  maybeAmbientScale: {unk4}");
				Logger.Info(logSrc, $"  maybeSunAngleDeg: {unk5}");
				Logger.Info(logSrc, $"  maybeSkyColorRGBA: [{unk1R},{unk1G},{unk1B},{unk1A}]");
				Logger.Info(logSrc, $"  maybeSkyColorAltRGBA: [{unk2R},{unk2G},{unk2B},{unk2A}]");
				Logger.Info(logSrc, $"  maybeExposure: {unk6}");
			}

			// now read everything until we hit sectionEnd
			int extraIndex = 0;
			while (reader.BaseStream.Position < sectionEnd)
			{
				var b = reader.ReadSingle();
				Logger.Warn(logSrc, $"level_properties extra byte[{extraIndex}] = 0x{b:X2}");
				extraIndex++;
			}

		}
	}


	static class RflLightParser
	{
		private const string logSrc = "RflLightParser";

		public static List<Light> ParseLightsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
		{
			var lights = new List<Light>();
			int numLights = reader.ReadInt32();
			Logger.Dev(logSrc, $"Reading {numLights} lights…");

			for (int i = 0; i < numLights; i++)
			{
				var light = new Light();

				light.UID = reader.ReadInt32();
				light.ClassName = ReadVString(reader);
				light.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

				// 3×3 rotation matrix → embed into Matrix4x4
				var m00 = reader.ReadSingle(); var m01 = reader.ReadSingle(); var m02 = reader.ReadSingle();
				var m10 = reader.ReadSingle(); var m11 = reader.ReadSingle(); var m12 = reader.ReadSingle();
				var m20 = reader.ReadSingle(); var m21 = reader.ReadSingle(); var m22 = reader.ReadSingle();
				light.Rotation = new Matrix4x4(
					m00, m01, m02, 0,
					m10, m11, m12, 0,
					m20, m21, m22, 0,
					0, 0, 0, 1
				);

				light.ScriptName = ReadVString(reader);
				light.HiddenInEditor = reader.ReadByte() != 0;

				uint rawFlags = reader.ReadUInt32();
				light.Dynamic = (rawFlags & 0x1) != 0;
				light.Fade = (rawFlags & 0x2) != 0;
				light.ShadowCasting = (rawFlags & 0x4) != 0;
				light.IsEnabled = (rawFlags & 0x8) != 0;
				light.Type = (LightType)(rawFlags >> 4 & 0x3);
				light.InitialState = (LightState)(rawFlags >> 8 & 0xF);
				light.RuntimeShadow = (rawFlags & 0x2000) != 0;

				// RGBA
				byte r = reader.ReadByte();
				byte g = reader.ReadByte();
				byte b = reader.ReadByte();
				byte a = reader.ReadByte();
				light.Color = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);

				light.Range = reader.ReadSingle();
				light.FOV = reader.ReadSingle();
				light.FOVDropoff = reader.ReadSingle();
				light.IntensityAtMaxRange = reader.ReadSingle();
				light.DropoffType = reader.ReadInt32();
				light.TubeLightWidth = reader.ReadSingle();

				if (1 == 1 || rfl_version != 0x127) // 295
				{
					light.OnIntensity = reader.ReadSingle();
				}
				else
				{
					float I0 = reader.ReadSingle();     // the raw RF2 OnIntensity
					light.Range *= 2;
					float R = light.Range;
					switch (light.DropoffType)
					{
						case 0:
							// linear:        I(d) = I0 * (1 – d/R)
							// so I0_file = I_linear_at_d=0 / R → our engine wants I(d=0)=I0*R
							light.OnIntensity = I0 * R;
							break;

						case 1:
							// squared:       I(d) = I0 * (1 – d/R)^2
							// invert so total “power” matches at d=0 → scale by R^2
							light.OnIntensity = I0 * R * R;
							break;

						case 2:
							// cosine:        I(d) = I0 * cos(d/R * (π/2))
							// invert so at d=0 you get I0 * 1 → no extra scaling, but 
							// if your target engine expects I(d=R)=0, you leave it unmodified
							light.OnIntensity = I0;
							break;

						case 3:
							// sqrt:          I(d) = I0 * sqrt(1 – d/R)
							// invert so I(d=0) = I0 * 1 → must scale by √R 
							light.OnIntensity = I0 * MathF.Sqrt(R);
							break;

						default:
							// unknown: treat as linear
							light.OnIntensity = I0 * R;
							break;
					}
					Logger.Dev(logSrc, $"Converted intensity for RF2 light {light.UID}. DropoffType={light.DropoffType}, Range={R}, RawIntensity={I0}, NewIntensity={light.OnIntensity}");
				}
				light.OnTime = reader.ReadSingle();
				light.OnTimeVariation = reader.ReadSingle();
				light.OffIntensity = reader.ReadSingle();
				light.OffTime = reader.ReadSingle();
				light.OffTimeVariation = reader.ReadSingle();

				lights.Add(light);

				// (we leave reader at end of this light record)
				Logger.Dev(logSrc,
					$"Loaded Light[{i}] UID={light.UID}, class={light.ClassName}, pos={light.Position}, " +
					$"flags=0x{rawFlags:X8}, color={light.Color}, range={light.Range}, int={light.OnIntensity}, intmr={light.IntensityAtMaxRange}"
				);
			}

			return lights;
		}
	}

	public static class RflBrushParser
	{
		private const string logSrc = "RflBrushParser";
		public static void ParseBrushesFromRfl(BinaryReader reader, long sectionEnd, Mesh mesh, int rfl_version)
		{
			int numBrushes = reader.ReadInt32();
			for (int i = 0; i < numBrushes; i++)
			{
				Logger.Debug(logSrc, $"Reading brush {i}/{numBrushes}");
				Brush brush = RFGeometryParser.ReadBrush(reader, rfl_version);
				if (brush != null) // for safety, only add valid brushes
					mesh.Brushes.Add(brush);
			}

			Logger.Info(logSrc, $"Parsed {numBrushes} brushes from RF1 RFL file.");

			// Ensure we're positioned correctly at the end of the section
			reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
		}
	}
}
