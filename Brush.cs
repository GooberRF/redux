using System.Collections.Generic;
using System.Numerics;

namespace RFGConverter
{
	public class Brush
	{
		public int UID { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 RotationMatrix { get; set; } = Matrix4x4.Identity;
		public List<Vector3> Vertices { get; set; } = new List<Vector3>();
		public List<Vector2> UVs { get; set; } = new List<Vector2>();
		public List<int> Indices { get; set; } = new List<int>();
		public string TextureName { get; set; }
		public Solid Solid { get; set; }
		public List<PropPoint> PropPoints { get; set; } = new List<PropPoint>();
	}

	public class Solid
	{
		public List<string> Textures { get; set; } = new List<string>();
		public List<Vector3> Vertices { get; set; } = new List<Vector3>();
		public List<Face> Faces { get; set; } = new List<Face>();
		public uint Flags { get; set; }
		public int Life { get; set; }
		public int State { get; set; }
	}

	public enum SolidFlags : uint
	{
		Portal =		0x00000001,
		Air =			0x00000002,
		Detail =		0x00000004,
		unk_08 =		0x00000008,
		EmitsSteam =	0x00000010,
		Geoable =		0x00000020, // RF2 only
		unk_40 =		0x00000040, // RF2 only
		unk_200 =		0x00000200  // RF2 only
	}

	public class Face
	{
		public List<int> Vertices { get; set; } = new List<int>();
		public List<Vector2> UVs { get; set; } = new();
		public int TextureIndex { get; set; }
		public ushort FaceFlags { get; set; }
		public Vector3 Normal { get; set; } = Vector3.UnitZ; // Default fallback
		public bool HasHoles => (FaceFlags & 0x80) != 0;
		public bool HasAlpha => (FaceFlags & 0x40) != 0;
		public bool FullBright => (FaceFlags & 0x20) != 0;
		public bool ScrollTexture => (FaceFlags & 0x10) != 0;
		public bool IsDetail => (FaceFlags & 0x08) != 0;
		public bool LiquidSurface => (FaceFlags & 0x04) != 0;
		public bool Mirrored => (FaceFlags & 0x02) != 0;
		public bool ShowSky => (FaceFlags & 0x01) != 0;
		public bool IsInvisible => (FaceFlags & 0x2000) != 0;

	}
	public class PropPoint
	{
		public string Name { get; set; }
		public Vector3 Position { get; set; }
		public Quaternion Rotation { get; set; }
	}
}
