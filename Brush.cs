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
	}
}
