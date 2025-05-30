using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace redux
{
	public static class RfgParser
	{
		public static Mesh ReadRfg(string filename)
		{
			var mesh = new Mesh();

			using (var stream = File.OpenRead(filename))
			using (var reader = new BinaryReader(stream))
			{
				// Read header
				uint magic = reader.ReadUInt32();
				if (magic != 0xD43DD00D)
					throw new Exception("Invalid RFG magic.");

				int version = reader.ReadInt32();
				int numGroups = reader.ReadInt32();

				for (int g = 0; g < numGroups; g++)
				{
					string groupName = Utils.ReadVString(reader);
					byte isMoving = reader.ReadByte();
					if (isMoving != 0)
						throw new NotSupportedException("Moving groups are not currently supported.");

					// ----- START reading brush section -----
					int numBrushes = reader.ReadInt32();
					for (int b = 0; b < numBrushes; b++)
					{
						Brush brush = RflBrushParser.ReadRF1Brush(reader, version);
						mesh.Brushes.Add(brush);
					}
					// ----- END reading brush section -----

					// ----- SKIP the rest -----

					RflUtils.SkipGeoRegions(reader);
					RflUtils.SkipLights(reader);
					RflUtils.SkipCutsceneCameras(reader);
					RflUtils.SkipCutscenePathNodes(reader);
					RflUtils.SkipAmbientSounds(reader);
					RflUtils.SkipEvents(reader);
					RflUtils.SkipMpRespawnPoints(reader);
					RflUtils.SkipNavPoints(reader);
					RflUtils.SkipEntities(reader);
					RflUtils.SkipItems(reader);
					RflUtils.SkipClutters(reader);
					RflUtils.SkipTriggers(reader);
					RflUtils.SkipParticleEmitters(reader);
					RflUtils.SkipGasRegions(reader);
					RflUtils.SkipDecals(reader);
					RflUtils.SkipClimbingRegions(reader);
					RflUtils.SkipRoomEffects(reader);
					RflUtils.SkipEaxEffects(reader);
					RflUtils.SkipBoltEmitters(reader);
					RflUtils.SkipTargets(reader);
					RflUtils.SkipPushRegions(reader);
				}
			}

			return mesh;
		}
	}
}
