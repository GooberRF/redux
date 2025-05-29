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

			Logger.Info(logSrc, $"Writing RFG to {outputPath}");

			writer.Write(0xD43DD00D); // Magic
			writer.Write(0x0000012C); // Version
			writer.Write(1); // Num groups
			Utils.WriteVString(writer, "box");
			writer.Write((byte)0); // is_moving = false

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

				writer.Write(brush.UID);
				writer.Write(brush.Position.X);
				writer.Write(brush.Position.Y);
				writer.Write(brush.Position.Z);

				var fwd = new Vector3(brush.RotationMatrix.M31, brush.RotationMatrix.M32, brush.RotationMatrix.M33);
				var right = new Vector3(brush.RotationMatrix.M11, brush.RotationMatrix.M12, brush.RotationMatrix.M13);
				var up = new Vector3(brush.RotationMatrix.M21, brush.RotationMatrix.M22, brush.RotationMatrix.M23);
				writer.Write(fwd.X); writer.Write(fwd.Y); writer.Write(fwd.Z);
				writer.Write(right.X); writer.Write(right.Y); writer.Write(right.Z);
				writer.Write(up.X); writer.Write(up.Y); writer.Write(up.Z);

				writer.Write(0); writer.Write(0); Utils.WriteVString(writer, "");
				writer.Write(brush.Solid.Textures.Count);

				Logger.Debug(logSrc, $"Brush {brush.UID} textures: {string.Join(", ", brush.Solid.Textures)}");

				foreach (var tex in brush.Solid.Textures)
					Utils.WriteVString(writer, tex);
				writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

				var exportVerts = new List<Vector3>();
				var vertMap = new Dictionary<(int, int, int), int>();
				var remappedFaces = new List<Face>();

				foreach (var face in brush.Solid.Faces)
				{
					var newFace = new Face
					{
						TextureIndex = face.TextureIndex,
						Vertices = new List<int>(),
						UVs = new List<Vector2>()
					};

					for (int i = 0; i < face.Vertices.Count; i++)
					{
						var posIndex = face.Vertices[i];
						var pos = brush.Vertices[posIndex];

						// Gracefully handle missing UVs (if corrupted input)
						var uv = (i < face.UVs.Count) ? face.UVs[i] : Vector2.Zero;

						var key = ((int)(pos.X * 1000), (int)(pos.Y * 1000), (int)(pos.Z * 1000));
						if (!vertMap.TryGetValue(key, out int vertIdx))
						{
							vertIdx = exportVerts.Count;
							exportVerts.Add(pos);
							vertMap[key] = vertIdx;
						}


						newFace.Vertices.Add(vertIdx);
						newFace.UVs.Add(uv);
					}

					if (newFace.Vertices.Count > 3)
					{
						Logger.Debug(logSrc, $"Exported an ngon face with {newFace.Vertices.Count} vertices on brush {brush.UID}.");
					}

					remappedFaces.Add(newFace);
				}

				writer.Write(exportVerts.Count);
				foreach (var v in exportVerts) {
					writer.Write(v.X);
					writer.Write(v.Y);
					writer.Write(v.Z);
				}

				writer.Write(remappedFaces.Count);
				foreach (var face in remappedFaces)
				{
					Vector3 normal = Vector3.Zero;
					var origin = exportVerts[face.Vertices[0]];
					for (int i = 1; i < face.Vertices.Count - 1; i++)
						normal += Vector3.Cross(
							exportVerts[face.Vertices[i]] - origin,
							exportVerts[face.Vertices[i + 1]] - origin
						);
					normal = Vector3.Normalize(normal);
					float dist = -Vector3.Dot(normal, origin);

					writer.Write(normal.X); writer.Write(normal.Y); writer.Write(normal.Z); writer.Write(dist);
					writer.Write(face.TextureIndex); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
					writer.Write(0); writer.Write((ushort)256); writer.Write((ushort)0); writer.Write(0); writer.Write(-1);
					writer.Write(face.Vertices.Count);
					for (int i = 0; i < face.Vertices.Count; i++)
					{
						int vi = face.Vertices[i];
						var uv = face.UVs[i];
						writer.Write(vi);
						writer.Write(uv.X);
						writer.Write(uv.Y);
					}
				}

				Logger.Debug(logSrc, $"Brush {brush.UID} has {exportVerts.Count} vertices, {remappedFaces.Count} faces");

				writer.Write(0);

				uint exportFlags = brush.Solid.Flags;

				bool RF2GeoableFlag = false;

				// If desired, make RF2 geoable brushes non-detail so they can be geoed in RF1
				if (Config.SetRF2GeoableNonDetail)
				{
					var before = (SolidFlags)exportFlags;
					var after = SolidFlagUtils.StripRF2Geoable(before);

					if ((before & SolidFlags.Detail) != 0 && (after & SolidFlags.Detail) == 0)
					{
						Logger.Debug(logSrc, $"Removing detail flag from RF2 geoable brush {brush.UID}.");
						RF2GeoableFlag = true;
					}

					exportFlags = (uint)after;
				}

				// Only include flags RF1 supports
				exportFlags = (uint)SolidFlagUtils.MakeRF1SafeFlags((SolidFlags)exportFlags);
				writer.Write(exportFlags);

				// RF1 doesn't support the RF2 geoable flag. In RF1, any brush with life > -1 breaks like glass.
				// So any brush with that flag needs its life set to -1 during the conversion
				writer.Write(RF2GeoableFlag ? -1 : brush.Solid.Life);

				writer.Write(brush.Solid.State); // state (0 = deselected, 2 = locked, 3 = selected)
			}

			for (int i = 0; i < 22; i++) writer.Write(0);
		}
	}
}
