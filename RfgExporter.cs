using RFGConverter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace redux
{
	public static class RfgExporter
	{
		private const string logSrc = "RfgExporter";

		public static void ExportRfg(Mesh mesh, string outputPath)
		{
			using var stream = File.Create(outputPath);
			using var writer = new BinaryWriter(stream);

			// Header
			writer.Write(0xD43DD00D);			// Magic for RFG files
			writer.Write(0x0000012C);			// Version 300 (0x12C)
			writer.Write(1);					// Number of groups
			Utils.WriteVString(writer, "box");	// group name
			writer.Write((byte)0);				// is_moving = 0 (static group)

			// Brushes
			writer.Write(mesh.Brushes.Count);
			foreach (var brush in mesh.Brushes)
			{
				var flagDescriptions = new List<string>();
				uint flags = brush.Solid.Flags;
				if ((flags & 0x01) != 0) flagDescriptions.Add("portal");
				if ((flags & 0x02) != 0) flagDescriptions.Add("air");
				if ((flags & 0x04) != 0) flagDescriptions.Add("detail");
				if ((flags & 0x08) != 0) flagDescriptions.Add("unk_08");
				if ((flags & 0x10) != 0) flagDescriptions.Add("steam?");
				if ((flags & 0x20) != 0) flagDescriptions.Add("geoable");
				if ((flags & 0x40) != 0) flagDescriptions.Add("unk_40");
				if ((flags & 0x200) != 0) flagDescriptions.Add("unk_200");
				string flagsSummary = flagDescriptions.Count > 0 ? string.Join(", ", flagDescriptions) : "none";
				Logger.Debug(logSrc, $"Writing brush {brush.UID}, with life {brush.Solid.Life}, state {brush.Solid.State}, and flags 0x{flags:X8} ({flagsSummary})");

				// UID
				writer.Write(brush.UID);

				// Position
				writer.Write(brush.Position.X);
				writer.Write(brush.Position.Y);
				writer.Write(brush.Position.Z);

				// Orientation
				Vector3 fwd = new(brush.RotationMatrix.M31, brush.RotationMatrix.M32, brush.RotationMatrix.M33);
				Vector3 right = new(brush.RotationMatrix.M11, brush.RotationMatrix.M12, brush.RotationMatrix.M13);
				Vector3 up = new(brush.RotationMatrix.M21, brush.RotationMatrix.M22, brush.RotationMatrix.M23);
				writer.Write(fwd.X); writer.Write(fwd.Y); writer.Write(fwd.Z);
				writer.Write(right.X); writer.Write(right.Y); writer.Write(right.Z);
				writer.Write(up.X); writer.Write(up.Y); writer.Write(up.Z);

				// Solid info
				writer.Write(0); // unknown1
				writer.Write(0); // unknown2
				Utils.WriteVString(writer, ""); // unknown3

				Logger.Debug(logSrc, $"Brush {brush.UID} textures: {string.Join(", ", brush.Solid.Textures)}");
				writer.Write(brush.Solid.Textures.Count);
				foreach (var tex in brush.Solid.Textures)
					Utils.WriteVString(writer, tex);

				writer.Write(0); // num_face_scrolls
				writer.Write(0); // num_rooms
				writer.Write(0); // num_subroom_lists
				writer.Write(0); // num_portals

				// Flattened vertex and UV data
				var vertices = new List<Vector3>();
				var uvs = new List<Vector2>();
				var remappedFaces = new List<Face>();

				foreach (var face in brush.Solid.Faces)
				{
					var newFace = new Face
					{
						TextureIndex = face.TextureIndex,
						Vertices = new List<int>()
					};

					foreach (var vi in face.Vertices)
					{
						vertices.Add(brush.Vertices[vi]);
						uvs.Add(brush.UVs[vi]);
						newFace.Vertices.Add(vertices.Count - 1);
					}

					remappedFaces.Add(newFace);
				}

				// Vertices
				writer.Write(vertices.Count);
				foreach (var v in vertices)
				{
					writer.Write(v.X);
					writer.Write(v.Y);
					writer.Write(v.Z);
				}

				// Faces
				writer.Write(remappedFaces.Count);
				foreach (var face in remappedFaces)
				{
					Vector3 p0 = vertices[face.Vertices[0]];
					Vector3 p1 = vertices[face.Vertices[1]];
					Vector3 p2 = vertices[face.Vertices[2]];
					Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
					float dist = -Vector3.Dot(normal, p0);

					writer.Write(normal.X);
					writer.Write(normal.Y);
					writer.Write(normal.Z);
					writer.Write(dist);

					writer.Write(face.TextureIndex);
					writer.Write(-1); // surface_index
					writer.Write(-1); // face_id
					writer.Write(-1); // reserved
					writer.Write(-1); // reserved
					writer.Write(0);  // portal_index
					writer.Write((ushort)256); // face flags (256 = solid)
					writer.Write((ushort)0); // reserved2
					writer.Write(0); // smoothing groups
					writer.Write(-1); // room index

					writer.Write(face.Vertices.Count);
					foreach (var vi in face.Vertices)
					{
						writer.Write(vi);
						writer.Write(uvs[vi].X);
						writer.Write(uvs[vi].Y);
					}
				}

				writer.Write(0); // Surfaces

				// Brush footer
				writer.Write(brush.Solid.Flags);
				writer.Write(brush.Solid.Life);
				writer.Write(brush.Solid.State);
				//writer.Write(-1); // life
				//writer.Write(3);  // state (0 = deselected, 2 = locked, 3 = selected)
			}

			// Write zeroes for other sections
			for (int i = 0; i < 22; i++)
				writer.Write(0);
		}
	}
}
