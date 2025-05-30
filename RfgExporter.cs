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
			Utils.WriteVString(writer, "redux_group");
			writer.Write((byte)0); // is_moving = false


			// Write brushes section
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

			writer.Write(0); // 0 geo regions

			// Write lights section
			writer.Write(mesh.Lights.Count);
			foreach (var light in mesh.Lights)
			{
				// UID
				writer.Write(light.UID);
				// class name
				Utils.WriteVString(writer, light.ClassName);

				// position
				writer.Write(light.Position.X);
				writer.Write(light.Position.Y);
				writer.Write(light.Position.Z);

				// 3×3 rotation matrix
				var R = light.Rotation;
				writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
				writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
				writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

				// script name
				Utils.WriteVString(writer, light.ScriptName);

				// hidden flag
				writer.Write((byte)(light.HiddenInEditor ? 1 : 0));

				// raw bitflags
				uint rawFlags = 0;
				if (light.Dynamic) rawFlags |= 0x00000001;
				if (light.Fade) rawFlags |= 0x00000002;
				if (light.ShadowCasting) rawFlags |= 0x00000004;
				if (light.IsEnabled) rawFlags |= 0x00000008;
				rawFlags |= ((uint)light.Type & 0x3) << 4;
				rawFlags |= ((uint)light.InitialState & 0xF) << 8;
				if (light.RuntimeShadow) rawFlags |= 0x00002000;
				writer.Write(rawFlags);

				// color (RGBA bytes)
				writer.Write((byte)(light.Color.X * 255));
				writer.Write((byte)(light.Color.Y * 255));
				writer.Write((byte)(light.Color.Z * 255));
				writer.Write((byte)(light.Color.W * 255));

				// floats
				writer.Write(light.Range);
				writer.Write(light.FOV);
				writer.Write(light.FOVDropoff);
				writer.Write(light.IntensityAtMaxRange);

				// dropoff type
				writer.Write(light.DropoffType);

				// tube width
				writer.Write(light.TubeLightWidth);

				// on/off timing
				writer.Write(light.OnIntensity);
				writer.Write(light.OnTime);
				writer.Write(light.OnTimeVariation);
				writer.Write(light.OffIntensity);
				writer.Write(light.OffTime);
				writer.Write(light.OffTimeVariation);

				Logger.Debug(logSrc, $"Added light {light.UID} with range {light.Range} and intensity {light.OnIntensity}");
			}

			writer.Write(0); // 0 cutscene cameras
			writer.Write(0); // 0 cutscene path nodes
			writer.Write(0); // 0 ambient sounds

			// Write events section
			writer.Write(mesh.Events.Count);
			foreach (var ev in mesh.Events)
			{
				// UID
				writer.Write(ev.UID);

				// class name
				Utils.WriteVString(writer, ev.ClassName);

				// position
				writer.Write(ev.Position.X);
				writer.Write(ev.Position.Y);
				writer.Write(ev.Position.Z);

				// script name
				Utils.WriteVString(writer, ev.ScriptName);

				// hidden flag
				writer.Write((byte)(ev.HiddenInEditor ? 1 : 0));

				// delay
				writer.Write(ev.Delay);

				// two bools
				writer.Write((byte)(ev.Bool1 ? 1 : 0));
				writer.Write((byte)(ev.Bool2 ? 1 : 0));

				// two ints
				writer.Write(ev.Int1);
				writer.Write(ev.Int2);

				// two floats
				writer.Write(ev.Float1);
				writer.Write(ev.Float2);

				// two strings
				Utils.WriteVString(writer, ev.Str1);
				Utils.WriteVString(writer, ev.Str2);

				// links (uid_list)
				writer.Write(ev.Links.Count);
				foreach (var link in ev.Links)
					writer.Write(link);

				// optional rotation
				if (ev.HasRotation)
				{
					writer.Write(ev.Rotation.M11); writer.Write(ev.Rotation.M12); writer.Write(ev.Rotation.M13);
					writer.Write(ev.Rotation.M21); writer.Write(ev.Rotation.M22); writer.Write(ev.Rotation.M23);
					writer.Write(ev.Rotation.M31); writer.Write(ev.Rotation.M32); writer.Write(ev.Rotation.M33);
				}

				// color
				writer.Write(ev.RawColor);
			}

			// Write MP respawn points section
			writer.Write(mesh.MPRespawnPoints.Count);
			foreach (var pt in mesh.MPRespawnPoints)
			{
				// UID
				writer.Write(pt.UID);

				// Position
				writer.Write(pt.Position.X);
				writer.Write(pt.Position.Y);
				writer.Write(pt.Position.Z);

				// 3×3 rotation matrix (row‑major: M11–M13, M21–M23, M31–M33)
				var R = pt.Rotation;
				writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
				writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
				writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

				// Script name
				Utils.WriteVString(writer, pt.ScriptName);

				// Hidden flag
				writer.Write((byte)(pt.HiddenInEditor ? 1 : 0));

				// Team ID
				writer.Write(pt.TeamID);

				// Red/Blue/Bot flags
				writer.Write((byte)(pt.RedTeam ? 1 : 0));
				writer.Write((byte)(pt.BlueTeam ? 1 : 0));
				writer.Write((byte)(pt.IsBot ? 1 : 0));

				Logger.Debug(logSrc, $"Added MP spawn {pt.UID} (R?{pt.RedTeam}/B?{pt.BlueTeam})");
			}

			writer.Write(0); // 0 nav points
			writer.Write(0); // 0 entities

			// Write triggers section
			writer.Write(mesh.Items.Count);
			foreach (var it in mesh.Items)
			{
				// UID
				writer.Write(it.UID);

				if (!string.IsNullOrEmpty(Config.ReplacementItemName))
				{
					Logger.Info(logSrc, $"Swapping item class for UID {it.UID} from {it.ClassName} to {Config.ReplacementItemName}");
				}

				// pick either the original or the override
				var exportName = string.IsNullOrEmpty(Config.ReplacementItemName)
					? it.ClassName
					: Config.ReplacementItemName;

				// class name (either original or override)
				Utils.WriteVString(writer, exportName);

				// position
				writer.Write(it.Position.X);
				writer.Write(it.Position.Y);
				writer.Write(it.Position.Z);

				// 3×3 rotation matrix
				var R = it.Rotation;
				writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
				writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
				writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

				// script name
				Utils.WriteVString(writer, it.ScriptName);

				// hidden flag
				writer.Write((byte)(it.HiddenInEditor ? 1 : 0));

				// count, respawn time, team ID
				writer.Write(it.Count);
				writer.Write(it.RespawnTime);
				writer.Write(it.TeamID);
			}

			writer.Write(0); // 0 clutter

			// Write triggers section
			writer.Write(mesh.Triggers.Count);
			foreach (var trg in mesh.Triggers)
			{
				// UID
				writer.Write(trg.UID);

				// script name
				Utils.WriteVString(writer, trg.ScriptName);

				// hidden flag
				writer.Write((byte)(trg.HiddenInEditor ? 1 : 0));

				// shape
				writer.Write((int)trg.Shape);

				// resets_after & resets_times
				writer.Write(trg.ResetsAfter);
				writer.Write(trg.ResetsTimes);

				// use key required + key name
				writer.Write((byte)(trg.UseKeyRequired ? 1 : 0));
				Utils.WriteVString(writer, trg.KeyName);

				// weapon activates + activated_by
				writer.Write((byte)(trg.WeaponActivates ? 1 : 0));
				writer.Write((byte)trg.ActivatedBy);

				// npc, auto, in_vehicle
				writer.Write((byte)(trg.IsNpc ? 1 : 0));
				writer.Write((byte)(trg.IsAuto ? 1 : 0));
				writer.Write((byte)(trg.InVehicle ? 1 : 0));

				// position
				writer.Write(trg.Position.X);
				writer.Write(trg.Position.Y);
				writer.Write(trg.Position.Z);

				// sphere vs box
				if (trg.Shape == TriggerShape.Sphere)
				{
					writer.Write(trg.SphereRadius);
				}
				else
				{
					// rotation matrix
					var R = trg.Rotation;
					writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
					writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
					writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

					// box extents + one_way
					writer.Write(trg.BoxHeight);
					writer.Write(trg.BoxWidth);
					writer.Write(trg.BoxDepth);
					writer.Write((byte)(trg.OneWay ? 1 : 0));
				}

				// room/link UIDs + disabled flag
				writer.Write(trg.AirlockRoomUID);
				writer.Write(trg.AttachedToUID);
				writer.Write(trg.UseClutterUID);
				writer.Write((byte)(trg.Disabled ? 1 : 0));

				// timing
				writer.Write(trg.ButtonActiveTime);
				writer.Write(trg.InsideTime);
				
				// team
				writer.Write((int)trg.Team);

				// links
				writer.Write(trg.Links.Count);
				foreach (var uid in trg.Links)
					writer.Write(uid);
			}

			writer.Write(0); // 0 particle emitters
			writer.Write(0); // 0 gas regions
			writer.Write(0); // 0 decals
			writer.Write(0); // 0 climbing regions
			writer.Write(0); // 0 room effects
			writer.Write(0); // 0 eax effects
			writer.Write(0); // 0 bolt emitters
			writer.Write(0); // 0 targets

			// Write push regions section
			writer.Write(mesh.PushRegions.Count);
			foreach (var pr in mesh.PushRegions)
			{
				// UID
				writer.Write(pr.UID);

				// class name
				Utils.WriteVString(writer, pr.ClassName);

				// position
				writer.Write(pr.Position.X);
				writer.Write(pr.Position.Y);
				writer.Write(pr.Position.Z);

				// 3×3 rotation matrix
				var R = pr.Rotation;
				writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
				writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
				writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

				// script name
				Utils.WriteVString(writer, pr.ScriptName);

				// hidden flag
				writer.Write((byte)(pr.HiddenInEditor ? 1 : 0));

				// shape enum
				writer.Write((int)pr.Shape);

				// extents or radius
				if (pr.Shape == PushRegionShape.Sphere)
				{
					writer.Write(pr.Radius);
				}
				else
				{
					writer.Write(pr.Extents.X);
					writer.Write(pr.Extents.Y);
					writer.Write(pr.Extents.Z);
				}

				// strength
				writer.Write(pr.Strength);

				// flags (16‑bit)
				ushort rawFlags = 0;
				if (pr.JumpPad) rawFlags |= 0x40;
				if (pr.DoesntAffectPlayer) rawFlags |= 0x20;
				if (pr.Radial) rawFlags |= 0x10;
				if (pr.GrowsTowardsBoundary) rawFlags |= 0x08;
				if (pr.GrowsTowardsCenter) rawFlags |= 0x04;
				if (pr.Grounded) rawFlags |= 0x02;
				if (pr.MassIndependent) rawFlags |= 0x01;
				writer.Write(rawFlags);

				// turbulence
				writer.Write(pr.Turbulence);
			}


			//for (int i = 0; i < 22; i++)
			//	writer.Write(0);

		}
	}
}
