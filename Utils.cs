using System.IO;
using System.Text;

namespace RFGConverter
{
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
}
