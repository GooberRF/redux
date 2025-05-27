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
			byte[] bytes = Encoding.ASCII.GetBytes(str);
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

}
