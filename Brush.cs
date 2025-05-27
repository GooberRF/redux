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
	}

	public class Face
	{
		public List<int> Vertices { get; set; } = new List<int>();
		public int TextureIndex { get; set; }
		public ushort FaceFlags { get; set; }
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
