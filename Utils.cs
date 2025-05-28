using System.IO;
using System.Numerics;
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
		public static bool TriangulatePolygons { get; set; } = false;
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
}
