using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace RFGConverter
{
	public static class Config
	{
		public static bool IncludePortalFaces { get; set; } = false;
		public static bool IncludeDetailFaces { get; set; } = true;
		public static bool IncludeAlphaFaces { get; set; } = true;
		public static bool IncludeHoleFaces { get; set; } = true;
		public static bool IncludeLiquidFaces { get; set; } = false;
		public static bool IncludeSkyFaces { get; set; } = false;
		public static bool IncludeInvisibleFaces { get; set; } = false;
		public static bool ParseBrushSectionInstead { get; set; } = false;
		public static bool TriangulatePolygons { get; set; } = true;
		public static bool TranslateRF2Textures { get; set; } = false;
		public static bool SetRF2GeoableNonDetail { get; set; } = false;
		public enum LogLevel
		{
			Debug,
			Info,
			Warn,
			Error
		}

		public static LogLevel Verbosity { get; set; } = LogLevel.Info;

		public static bool ShouldLog(LogLevel level)
		{
			return level >= Verbosity;
		}
	}

	public static class Logger
	{
		public static void Log(Config.LogLevel level, string source, string message)
		{
			if (!Config.ShouldLog(level))
				return;

			string prefix = level switch
			{
				Config.LogLevel.Debug => "[DEBUG]",
				Config.LogLevel.Info => "[INFO ]",
				Config.LogLevel.Warn => "[WARN ]",
				Config.LogLevel.Error => "[ERROR]",
				_ => "[LOG  ]"
			};

			Console.WriteLine($"{prefix} [{source}] {message}");
		}

		public static void Debug(string source, string message) => Log(Config.LogLevel.Debug, source, message);
		public static void Info(string source, string message) => Log(Config.LogLevel.Info, source, message);
		public static void Warn(string source, string message) => Log(Config.LogLevel.Warn, source, message);
		public static void Error(string source, string message) => Log(Config.LogLevel.Error, source, message);
	}

	public static class Utils
	{
		public static string ReadVString(BinaryReader reader)
		{
			if (reader.BaseStream.Position + 2 > reader.BaseStream.Length)
			{
				Console.WriteLine("Warning: Tried to read string length but hit end of file.");
				return string.Empty;
			}

			ushort length = reader.ReadUInt16();

			if (length == 0)
				return string.Empty;

			if (reader.BaseStream.Position + length > reader.BaseStream.Length)
			{
				Console.WriteLine($"Warning: String length {length} exceeds remaining file size at position {reader.BaseStream.Position}.");
				return string.Empty;
			}

			byte[] data = reader.ReadBytes(length);
			return System.Text.Encoding.ASCII.GetString(data);
		}

		public static void WriteVString(BinaryWriter writer, string str)
		{
			str ??= string.Empty;
			byte[] bytes = Encoding.ASCII.GetBytes(str);

			if (bytes.Length > ushort.MaxValue)
				throw new ArgumentException("String too long for vstring format.");

			writer.Write((ushort)bytes.Length);
			writer.Write(bytes);
		}
	}

	// used for static geometry in RFL format
	public static class FaceFlags
	{
		public const ushort ShowSky = 0x01;
		public const ushort Mirrored = 0x02;
		public const ushort LiquidSurface = 0x04;
		public const ushort IsDetail = 0x08;
		public const ushort ScrollTexture = 0x10;
		public const ushort FullBright = 0x20;
		public const ushort HasAlpha = 0x40;
		public const ushort HasHoles = 0x80;
		public const ushort LightmapResolutionMask = 0x0300;
		public const ushort IsInvisible = 0x2000;
	}

	// used for static geometry in RFL format
	public class SubroomList
	{
		public int RoomIndex { get; set; }
		public int NumSubrooms { get; set; }
		public List<int> SubroomIndices { get; set; } = new();
	}

	// used for static geometry in RFL format
	public class Portal
	{
		public int RoomIndex1 { get; set; }
		public int RoomIndex2 { get; set; }
		public Vector3 Point1 { get; set; }
		public Vector3 Point2 { get; set; }
	}

	public static class ObjUtils
	{
		public static int ParseUidFromObjectName(string objectName, int fallback)
		{
			var parts = objectName.Split('_', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && parts[0].Equals("Brush", StringComparison.OrdinalIgnoreCase))
			{
				if (int.TryParse(parts[1], out int uid))
					return uid;
			}

			//Logger.Warn(logSrc, $"Could not parse UID from object name '{objectName}', using fallback UID {fallback}");
			return fallback;
		}

		public static uint ParseFlagsFromObjectName(string objectName)
		{
			uint flags = 0;
			var parts = objectName.Split('_', StringSplitOptions.RemoveEmptyEntries);

			foreach (string part in parts)
			{
				switch (part.ToLowerInvariant())
				{
					case "air": flags |= 0x02; break;
					case "solid": break;
					case "detail": flags |= 0x04; break;
					case "nodetail": break;
					case "portal": flags |= 0x01; break;
					case "noportal": break;
					case "emit": flags |= 0x10; break;
					case "noemit": break;
				}
			}

			return flags;
		}
	}

	public static class RflUtils
	{
		public readonly struct VertexKey : IEquatable<VertexKey>
		{
			public readonly Vector3 Position;
			public readonly Vector2 UV;

			public VertexKey(Vector3 pos, Vector2 uv)
			{
				Position = pos;
				UV = uv;
			}

			public bool Equals(VertexKey other) =>
				Position.Equals(other.Position) && UV.Equals(other.UV);

			public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
			public override int GetHashCode() => HashCode.Combine(Position, UV);
		}

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
		public static void SkipLights(BinaryReader reader) => SafeSkip(reader, 100);
		public static void SkipCutsceneCameras(BinaryReader reader) => SafeSkip(reader, 84);
		public static void SkipCutscenePathNodes(BinaryReader reader) => SafeSkip(reader, 84);
		public static void SkipAmbientSounds(BinaryReader reader) => SafeSkip(reader, 48);
		public static void SkipEvents(BinaryReader reader) => SafeSkip(reader, 112);
		public static void SkipMpRespawnPoints(BinaryReader reader) => SafeSkip(reader, 80);
		public static void SkipNavPoints(BinaryReader reader) => SafeSkip(reader, 100);
		public static void SkipEntities(BinaryReader reader) => SafeSkip(reader, 220);
		public static void SkipItems(BinaryReader reader) => SafeSkip(reader, 100);
		public static void SkipClutters(BinaryReader reader) => SafeSkip(reader, 100);
		public static void SkipTriggers(BinaryReader reader) => SafeSkip(reader, 120);
		public static void SkipParticleEmitters(BinaryReader reader) => SafeSkip(reader, 180);
		public static void SkipGasRegions(BinaryReader reader) => SafeSkip(reader, 84);
		public static void SkipDecals(BinaryReader reader) => SafeSkip(reader, 80);
		public static void SkipClimbingRegions(BinaryReader reader) => SafeSkip(reader, 64);
		public static void SkipRoomEffects(BinaryReader reader) => SafeSkip(reader, 64);
		public static void SkipEaxEffects(BinaryReader reader) => SafeSkip(reader, 80);
		public static void SkipBoltEmitters(BinaryReader reader) => SafeSkip(reader, 100);
		public static void SkipTargets(BinaryReader reader) => SafeSkip(reader, 64);
		public static void SkipPushRegions(BinaryReader reader) => SafeSkip(reader, 64);
		public static void SkipGeoRegions(BinaryReader reader)
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
	}

	public static class SolidFlagUtils
	{
		public static SolidFlags MakeRF1SafeFlags(SolidFlags flags)
		{
			const SolidFlags allowed =
				SolidFlags.Portal |
				SolidFlags.Air |
				SolidFlags.Detail |
				SolidFlags.EmitsSteam;

			return flags & allowed;
		}

		public static SolidFlags StripRF2Geoable(SolidFlags flags)
		{
			bool isAir = (flags & SolidFlags.Air) != 0;
			bool isPortal = (flags & SolidFlags.Portal) != 0;
			bool isDetail = (flags & SolidFlags.Detail) != 0;
			bool isGeoable = (flags & SolidFlags.Geoable) != 0;

			if (!isAir && !isPortal && isDetail && isGeoable)
			{
				flags &= ~SolidFlags.Detail;
				flags &= ~SolidFlags.Geoable;
			}

			return flags;
		}
	}

	public static class EmbeddedResourceLoader
	{
		public static string[] LoadLines(string resourceName)
		{
			var assembly = Assembly.GetExecutingAssembly();
			string fullName = assembly.GetManifestResourceNames()
				.FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

			if (fullName == null)
				throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");

			using var stream = assembly.GetManifestResourceStream(fullName);
			using var reader = new StreamReader(stream!);
			var lines = new List<string>();

			while (!reader.EndOfStream)
				lines.Add(reader.ReadLine()!);

			return lines.ToArray();
		}
	}

	public static class RF2TextureTranslator
	{
		private static Dictionary<string, string>? _translationMap;

		public static int TranslationCount => _translationMap?.Count ?? 0;

		public static void LoadRF2TextureTranslations()
		{
			var realTextures = EmbeddedResourceLoader.LoadLines("real_texture_filenames.txt").ToList();
			var translatedTextures = new HashSet<string>(
				EmbeddedResourceLoader.LoadLines("translated_texture_filenames.txt"),
				StringComparer.OrdinalIgnoreCase
			);

			string[] materialPrefixes = new[]
			{
				"rck_", "mtl_", "wtr_", "pls_", "gls_", "drt_", "woo_", "tec_", "cpt_", "mar_", "sp0_"
			};

			_translationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach (var real in realTextures)
			{
				string baseName = real;

				// Remove known material prefix
				foreach (var prefix in materialPrefixes)
				{
					if (real.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						baseName = real.Substring(prefix.Length);
						break;
					}
				}

				// Find a translated texture that ends with the base name
				string? match = translatedTextures.FirstOrDefault(t =>
					t.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));

				if (match != null)
					_translationMap[real] = match;
			}

			// Hardcoded translations
			AddManualTranslation("cpt_invisible.tga", "rck_invisible04.tga");
			AddManualTranslation("drt_invisible.tga", "rck_invisible04.tga");
			AddManualTranslation("mar_invisible.tga", "sld_invisible01.tga");
			AddManualTranslation("mtl_invisible.tga", "mtl_invisible02.tga");
			AddManualTranslation("pls_invisible.tga", "sld_invisible01.tga");
			AddManualTranslation("rck_invisible.tga", "rck_invisible04.tga");
			AddManualTranslation("woo_invisible.tga", "sld_invisible01.tga");
			AddManualTranslation("wtr_invisible.tga", "cem_invisible03.tga");
			AddManualTranslation("mtl_jpad_oct1.tga", "mtl_L15S2_lift.tga");
			AddManualTranslation("mtl_jpad_oct2.tga", "mtl_jumppad01.tga");
			AddManualTranslation("mtl_jpad_oct4.tga", "mtl_jumppad02.tga");
		}

		public static void AddManualTranslation(string original, string translated)
		{
			if (_translationMap == null)
				_translationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			_translationMap[original] = translated;
		}

		public static string TranslateRF2Texture(string textureFilename)
		{
			if (_translationMap == null)
				throw new InvalidOperationException("Translation map not loaded. Call LoadRF2TextureTranslations() first.");

			if (_translationMap.TryGetValue(textureFilename, out var translated))
				return translated;

			return textureFilename; // No translation found
		}

		public static void DebugPrintAllTranslations()
		{
			if (_translationMap == null)
			{
				Console.WriteLine("Translation map not loaded.");
				return;
			}

			foreach (var pair in _translationMap)
				Console.WriteLine($"{pair.Key} -> {pair.Value}");
		}
	}
}
