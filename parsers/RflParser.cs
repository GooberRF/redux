using redux.exporters;
using redux.parsers.parser_utils;
using redux.utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static redux.utilities.Utils;
using static System.Collections.Specialized.BitVector32;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace redux.parsers
{
    // root rfl parser
    public static class RflParser
    {
        private const string logSrc = "RflParser";
        public static Mesh ReadRfl(string filePath)
        {
            var mesh = new Mesh();

            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            // Validate magic number
            uint magic = reader.ReadUInt32();
            if (magic != 0xD4BADA55)
                throw new Exception("Invalid RFL file: wrong magic.");

            // Read header
            int version = reader.ReadInt32();
            uint timestamp = reader.ReadUInt32();
            int playerStartOffset = reader.ReadInt32();
            int levelInfoOffset = reader.ReadInt32();
            int numSections = reader.ReadInt32();
            int sectionsSize = reader.ReadInt32();

            // What version of RFL is this?
            bool isAlpine = version >= 0x12C;
            bool isRF1 = version <= 0xC8 || isAlpine;   // <= 201 or >= 300
            bool isRF2 = version == 0x127;                      // 295

            string levelName = ReadVString(reader);
            string modName = "";
            if (version >= 0xB2 && !isRF2)
                modName = ReadVString(reader);

            Logger.Info(logSrc, $"Parsing {levelName}...");
            if (modName.Length > 0)
                Logger.Info(logSrc, $"Required mod: {modName}");
            Logger.Info(logSrc, $"RFL version: {version}");
            if (isRF2)
            {
                Logger.Info(logSrc, $"Red Faction 2 RFL detected, using RF2 parsing");
                if (Config.TranslateRF2Textures)
                {
                    RF2TextureTranslator.LoadRF2TextureTranslations();
                    Logger.Info(logSrc, $"Loaded {RF2TextureTranslator.TranslationCount} RF2 texture filename translation definitions");
                }
            }
            else if (isAlpine)
            {
                Logger.Info(logSrc, $"Red Faction 1 (Alpine Faction) RFL detected, using RF1 parsing");
            }
            else if (isRF1)
            {
                Logger.Info(logSrc, $"Red Faction 1 RFL detected, using RF1 parsing");
            }

            if (Config.ParseBrushSectionInstead)
            {
                Logger.Info(logSrc, $"-brushes option used, parsing brush data instead of static geometry");
            }

            if (!Config.TriangulatePolygons)
            {
                Logger.Info(logSrc, $"-ngons option used, not forcing triangulation of parsed polygons");
            }

            // Read sections
            Vector3 rf2MedianBaked = Vector3.Zero;
            LevelProperties levelProperties = null;
            int coronaDerivedLightCount = 0;
            for (int i = 0; i < numSections; i++)
            {
                long sectionHeaderPos = reader.BaseStream.Position;

                if (reader.BaseStream.Position + 8 > reader.BaseStream.Length)
                {
                    Logger.Warn(logSrc, $"Reached EOF unexpectedly while reading section header {i}.");
                    break;
                }

                int sectionType = reader.ReadInt32();
                int sectionSize = reader.ReadInt32();
                long sectionStart = reader.BaseStream.Position;
                long sectionEnd = sectionStart + sectionSize;

                Logger.Info(logSrc, $"Section {i}: Type 0x{sectionType:X}, Size {sectionSize} at 0x{sectionHeaderPos:X}");

                if (sectionEnd > reader.BaseStream.Length)
                {
                    Logger.Warn(logSrc, $"Section {i} exceeds file length. Skipping.");
                    break;
                }

                if (sectionType == 0x100 && !Config.ParseBrushSectionInstead) // static geometry
                {
                    Logger.Debug(logSrc, $"Found static geometry section (0x100) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    Brush brush = RFGeometryParser.ReadStaticGeometry(reader, version);
                    mesh.Brushes.Add(brush);
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);

                }
                else if (sectionType == 0x02000000 && Config.ParseBrushSectionInstead) // brushes
                {
                    Logger.Debug(logSrc, $"Found brush geometry section (0x02000000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    RflBrushParser.ParseBrushesFromRfl(reader, sectionEnd, mesh, version);
                }
                else if (sectionType == 0x00000300) // lights (present in both RF1 and RF2 RFL files)
                {
                    Logger.Debug(logSrc, $"Found lights section (0x300) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Lights.AddRange(RflLightParser.ParseLightsFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00000500 && isRF2) // ambient sounds (RF2 - despite address proximity to lights, confirmed as sound emitters via FUN_00487ab0 → FUN_0055a100)
                {
                    Logger.Debug(logSrc, $"Found RF2 ambient_sounds section (0x500) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    // Not parsed - these are sound emitter objects, not visual lights
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x0900) // level_properties
                {
                    Logger.Debug(logSrc, $"Found level_properties section (0x0900) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    levelProperties = RflLevelPropertiesParser.ParseLevelPropertiesFromRfl(reader, sectionEnd, version);
                    mesh.AmbientColor = levelProperties.AmbientColor;
                    mesh.LightmapMultiplier = levelProperties.LightmapMultiplier;
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin); // shouldn't be needed, but for safety since RF2 rfls have some unknowns
                }
                else if (sectionType == 0x01000000) // level_info
                {
                    Logger.Debug(logSrc, $"Found level_info section (0x01000000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    RflLevelInfoParser.ParseLevelInfoFromRfl(reader, sectionEnd, version);
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin); // shouldn't be needed, but for safety since RF2 rfls have some unknowns
                }
                else if (sectionType == 0x00000700) // mp_respawn_points
                {
                    Logger.Debug(logSrc, $"Found mp_respawn_points section (0x700) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.MPRespawnPoints.AddRange(RflMpRespawnPointParser.ParseMpRespawnPoints(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00000a00) // particle_emitters (RF1 format, also present in some RF2 RFLs; RF2 runtime uses 0x7677)
                {
                    Logger.Debug(logSrc, $"Found particle_emitters section (0x0A00) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.ParticleEmitters.AddRange(RflParticleEmitterParser.ParseParticleEmittersFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x7677 && isRF2) // particle_emitters (RF2 only - RF1 uses 0x0A00)
                {
                    // RF2 particle emitters use a simpler class-based format (not compatible with RF1 parser)
                    Logger.Info(logSrc, $"Found RF2 particle_emitters section (0x7677) at 0x{sectionHeaderPos:X8}, size={sectionSize} (not yet implemented for RF2)");
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00000600) // events
                {
                    Logger.Warn(logSrc, $"Found events section (0x600) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    if (RflUtils.IsRF2(version))
                    {
                        Logger.Info(logSrc, "events section support for RF2 RFLs is a work-in-progress. Parser output may have errors.");
                        mesh.Events.AddRange(RflEventParser.ParseEventsRF2(reader, sectionEnd, version));
                    }
                    else
                    {
                        mesh.Events.AddRange(RflEventParser.ParseEvents(reader, sectionEnd, version));
                    }
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00001100) // push_regions
                {
                    Logger.Debug(logSrc, $"Found push_regions section (0x1100) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.PushRegions.AddRange(RflPushRegionParser.ParsePushRegionsFromRfl(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00060000) // triggers
                {
                    Logger.Debug(logSrc, $"Found triggers section (0x00060000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    if (isRF2)
                    {
                        Logger.Info(logSrc, "trigger section support for RF2 RFLs is a work-in-progress. Parser output may have errors.");
                    }
                    mesh.Triggers.AddRange(RflTriggerParser.ParseTriggersFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00040000) // items
                {
                    Logger.Debug(logSrc, $"Found items section (0x00040000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Items.AddRange(RflItemParser.ParseItemsFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00000d00) // climbing regions
                {
                    Logger.Debug(logSrc, $"Found climbing_regions section (0x0D00) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.ClimbingRegions.AddRange(RflClimbingRegionParser.ParseClimbingRegionsFromRfl(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x7680 && isRF2) // climbing/push regions (RF2 only - RF1 uses 0x0D00)
                {
                    // RF2 climbing/push regions use a different format (not compatible with RF1 parser)
                    Logger.Info(logSrc, $"Found RF2 climbing_regions section (0x7680) at 0x{sectionHeaderPos:X8}, size={sectionSize} (not yet implemented for RF2)");
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00010000) // waypoint lists
                {
                    Logger.Debug(logSrc, $"Found waypoint_lists section (0x00010000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.WaypointLists.AddRange(RflWaypointListParser.ParseWaypointListsFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00020000) // nav points
                {
                    Logger.Debug(logSrc, $"Found nav_points section (0x00010000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    if (isRF2 && 1 == 0) // temp disabled
                    {
                        Logger.Info(logSrc, "nav_points section support for RF2 RFLs is a work-in-progress. Parser output may have errors.");
                        mesh.NavPoints.AddRange(RflNavPointParser.ParseNavPointsFromRfl(reader, sectionEnd, version));
                    }

                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00001000) // decals
                {
                    Logger.Debug(logSrc, $"Found decals section (0x1000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Decals.AddRange(RflDecalParser.ParseDecalsFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00000f00 && isRF2) // decals (RF2 only - RF1 uses 0x1000)
                {
                    // RF2 decals at 0x0F00 use a simpler class-based format (just common header)
                    Logger.Debug(logSrc, $"Found RF2 decals section (0x0F00) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Decals.AddRange(RflDecalParser.ParseDecalsFromRflRF2(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00001200)  // lightmaps (RF1 only)
                {
                    Logger.Debug(logSrc, $"Found lightmaps section (0x1200) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Lightmaps.AddRange(RflLightmapParser.ParseLightmapsFromRfl(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);

                    if (Config.DumpLightmaps)
                    {
                        Logger.Debug(logSrc, "Dumping lightmaps...");
                        for (int ilm = 0; ilm < mesh.Lightmaps.Count; ilm++)
                        {
                            var lm = mesh.Lightmaps[ilm];
                            string outputPath = $"lightmap_{ilm}.tga";
                            TgaExporter.Write24BitTga(outputPath, lm);
                        }
                    }
                }
                else if (sectionType == 0x00002000) // movers
                {
                    Logger.Debug(logSrc, $"Found movers section (0x00002000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Movers.AddRange(RflMoverParser.ParseMoversFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00003000) // moving_groups
                {
                    Logger.Debug(logSrc, $"Found moving_groups section (0x00003000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Groups.AddRange(RflGroupParser.ParseGroupsFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x00050000) // clutters
                {
                    Logger.Debug(logSrc, $"Found clutters section (0x00050000) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Clutters.AddRange(RflClutterParser.ParseCluttersFromRfl(reader, sectionEnd, version));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x7678)  // coronas / glares chunk?
                {
                    Logger.Info(logSrc, $"Found coronas section (0x7678) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    mesh.Coronas.AddRange(RflCoronaParser.ParseCoronasFromRfl(reader, sectionEnd));
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x7779 && isRF2)  // RF2 lightmap geometry metadata
                {
                    Logger.Debug(logSrc, $"Found RF2 lightmap geometry section (0x7779) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }
                else if (sectionType == 0x7900 && isRF2)  // RF2 lightmap vertex colors
                {
                    Logger.Debug(logSrc, $"Found RF2 lightmap vertex colors section (0x7900) at 0x{sectionHeaderPos:X8}, size={sectionSize}");
                    var rf2LightmapData = RflRF2LightmapParser.ParseRF2LightmapsFromRfl(reader, sectionEnd);
                    if (rf2LightmapData != null)
                    {
                        var convertedLightmaps = RflRF2LightmapParser.ConvertToLightmaps(rf2LightmapData);
                        mesh.Lightmaps.AddRange(convertedLightmaps);
                        rf2MedianBaked = RflRF2LightmapParser.ComputeMedianBakedColor(rf2LightmapData);
                        Logger.Info(logSrc, $"Parsed {convertedLightmaps.Count} RF2 lightmaps from vertex color data (median baked RGB: {rf2MedianBaked.X},{rf2MedianBaked.Y},{rf2MedianBaked.Z})");

                        if (Config.DumpLightmaps)
                        {
                            Logger.Debug(logSrc, "Dumping RF2 lightmaps...");
                            int startIdx = mesh.Lightmaps.Count - convertedLightmaps.Count;
                            for (int ilm = 0; ilm < convertedLightmaps.Count; ilm++)
                            {
                                string outputPath = $"lightmap_{startIdx + ilm}.tga";
                                TgaExporter.Write24BitTga(outputPath, convertedLightmaps[ilm]);
                            }
                        }
                    }
                    reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
                }

                else if (sectionType == 0x0) // end section
                {
                    // once we've reached section type 0 we can stop reading further sections
                    // rf2 rfls have a section 0 at the end for some reason. rf1 rfls do not
                    break;
                }
                else
                {
                    reader.BaseStream.Seek(sectionSize, SeekOrigin.Current); // Skip unknown section
                }

                // RF2 chunk IDs (confirmed via rf2.exe):
                // Phase 1 (pre-geometry): 0x0100=geometry, 0x0900=level_properties, 0x7001=textures,
                //   0x7002=lightmaps, 0x7003=V3D models, 0x7004=VFX effects, 0x7005=sounds
                // Phase 2: 0x0200=skipped, 0x0400=spawn points, 0x0500=ambient sounds, 0x0600=events,
                //   0x0700=MP respawns, 0x0C00=editor-only (skipped), 0x0D00=cutscene cameras,
                //   0x0E00=ambient sounds, 0x0F00=decals (RF2 format), 0x1000=decals (RF1 format), 0x1100=kill regions,
                //   0x2000=movers, 0x3000=entity groups, 0x4000=cutscene paths,
                //   0x7677=particle emitters, 0x7678=glares/coronas, 0x7679=random item spawners,
                //   0x7680=climbing/push regions, 0x7681=clutter, 0x7777=keyframed movers,
                //   0x7779=lightmap geometry, 0x7900=lightmap vertex colors, 0x7901=fast geometry flag,
                //   0x10000=portals, 0x20000=nav points v2, 0x30000=entities/NPCs,
                //   0x40000=items, 0x50000=clutters, 0x60000=triggers, 0x70000=player start

            }

            // Convert RF2 coronas to RF1 point lights (RED2 bakes coronas as light sources)
            if (isRF2 && mesh.Coronas.Count > 0)
            {
                // Find max UID across all objects to assign corona-derived light UIDs above everything
                int maxUID = 0;
                foreach (var b in mesh.Brushes) maxUID = Math.Max(maxUID, b.UID);
                foreach (var l in mesh.Lights) maxUID = Math.Max(maxUID, l.UID);
                foreach (var d in mesh.Decals) maxUID = Math.Max(maxUID, d.UID);
                foreach (var e in mesh.Events) maxUID = Math.Max(maxUID, e.UID);
                foreach (var t in mesh.Triggers) maxUID = Math.Max(maxUID, t.UID);
                foreach (var it in mesh.Items) maxUID = Math.Max(maxUID, it.UID);
                foreach (var c in mesh.Clutters) maxUID = Math.Max(maxUID, c.UID);
                foreach (var np in mesh.NavPoints) maxUID = Math.Max(maxUID, np.UID);
                foreach (var pe in mesh.ParticleEmitters) maxUID = Math.Max(maxUID, pe.UID);
                foreach (var pr in mesh.PushRegions) maxUID = Math.Max(maxUID, pr.UID);
                foreach (var cr in mesh.ClimbingRegions) maxUID = Math.Max(maxUID, cr.UID);
                foreach (var co in mesh.Coronas) maxUID = Math.Max(maxUID, co.UID);
                foreach (var mv in mesh.Movers) maxUID = Math.Max(maxUID, mv.UID);
                int coronaNextUID = maxUID + 1;

                // Derive per-corona light range from the map's regular lights.
                // Base range = median regular light range (already ×3 scaled) halved.
                // Per-corona range = baseRange × sqrt(Intensity), since in inverse-square
                // attenuation the effective illumination distance scales as sqrt(I).
                // This gives bright coronas (I=1.0) the full base range, while dim accent
                // coronas (I=0.03) get ~17% of it.
                float coronaBaseRange = 15.0f; // fallback if no regular lights
                if (mesh.Lights.Count > 0)
                {
                    var ranges = mesh.Lights.Select(l => l.Range).OrderBy(r => r).ToList();
                    float median = ranges[ranges.Count / 2];
                    coronaBaseRange = median / 2.0f;
                }
                Logger.Info(logSrc, $"Corona base range: {coronaBaseRange:F1}m (median/2 of {mesh.Lights.Count} regular lights)");

                int coronaLightCount = 0;
                int coronaSkipCount = 0;
                foreach (var corona in mesh.Coronas)
                {
                    // Skip black coronas — they contribute no light
                    byte cr = (byte)(corona.Color.X * 255f);
                    byte cg = (byte)(corona.Color.Y * 255f);
                    byte cb = (byte)(corona.Color.Z * 255f);
                    if (cr == 0 && cg == 0 && cb == 0)
                    {
                        coronaSkipCount++;
                        continue;
                    }

                    var light = new Light();
                    light.UID = coronaNextUID++;
                    light.ClassName = "Light";
                    light.Position = corona.Position;
                    light.ScriptName = corona.ScriptName;
                    light.HiddenInEditor = false;
                    light.Dynamic = false;
                    light.Fade = false;
                    light.ShadowCasting = true;
                    light.IsEnabled = true;
                    light.InitialState = (LightState)2; // matches RED's default bake-eligible state
                    light.Color = new Vector4(corona.Color.X, corona.Color.Y, corona.Color.Z, 1.0f);
                    light.Range = coronaBaseRange * (float)Math.Sqrt(Math.Max(corona.Intensity, 0.01f));
                    light.IntensityAtMaxRange = 0;
                    light.DropoffType = 0;
                    light.TubeLightWidth = 4.0f;
                    light.OnIntensity = corona.Intensity * Config.RF2LightScale;
                    light.OnTime = 1.0f;
                    light.OnTimeVariation = 0;
                    light.OffIntensity = 0;
                    light.OffTime = 1.0f;
                    light.OffTimeVariation = 0;

                    // Narrow-cone coronas become spotlights. Surveyed all RF2 maps:
                    // genuine spotlights ≤44° (l01s1=30, l05s1=40, l02s1=44),
                    // visual/omni coronas ≥70° (dmc13=70, ctfo=75-85, dmpc04=90, others=360).
                    // Threshold 60° sits cleanly in the 44-70° gap.
                    Vector3 coneDir = new Vector3(corona.Orientation.M31, corona.Orientation.M32, corona.Orientation.M33);
                    bool isDirectional = corona.ConeAngle < 60f && coneDir.LengthSquared() > 0.001f;
                    if (isDirectional)
                    {
                        light.Type = LightType.Spot;
                        light.FOV = corona.ConeAngle / 2.0f;
                        light.FOVDropoff = corona.ConeAngle / 2.0f;

                        // Use orientation matrix directly (normalized rows)
                        Vector3 forward = Vector3.Normalize(coneDir);
                        Vector3 right = Vector3.Normalize(new Vector3(corona.Orientation.M11, corona.Orientation.M12, corona.Orientation.M13));
                        Vector3 up = Vector3.Normalize(new Vector3(corona.Orientation.M21, corona.Orientation.M22, corona.Orientation.M23));
                        light.Rotation = new Matrix4x4(
                            right.X, right.Y, right.Z, 0,
                            up.X, up.Y, up.Z, 0,
                            forward.X, forward.Y, forward.Z, 0,
                            0, 0, 0, 1
                        );
                        Logger.Dev(logSrc, $"Corona UID {corona.UID} → spotlight dir=({forward.X:F2},{forward.Y:F2},{forward.Z:F2}) cone={corona.ConeAngle}°");
                    }
                    else
                    {
                        light.Type = LightType.Point;
                        light.Rotation = Matrix4x4.Identity;
                        light.FOV = 15.0f;
                        light.FOVDropoff = 15.0f;
                    }

                    mesh.Lights.Add(light);
                    coronaLightCount++;
                }
                coronaDerivedLightCount = coronaLightCount;
                int spotCount = mesh.Coronas.Count(c => c.ConeAngle < 60f && new Vector3(c.Orientation.M31, c.Orientation.M32, c.Orientation.M33).LengthSquared() > 0.001f && (byte)(c.Color.X * 255f) + (byte)(c.Color.Y * 255f) + (byte)(c.Color.Z * 255f) > 0);
                // Compute range stats for the converted corona lights
                var coronaLights = mesh.Lights.Skip(mesh.Lights.Count - coronaLightCount).ToList();
                float minR = coronaLights.Count > 0 ? coronaLights.Min(l => l.Range) : 0;
                float maxR = coronaLights.Count > 0 ? coronaLights.Max(l => l.Range) : 0;
                Logger.Warn(logSrc, $"Converted {coronaLightCount} RF2 coronas to lights ({spotCount} spot, {coronaLightCount - spotCount} point, {coronaSkipCount} black skipped, range={minR:F1}-{maxR:F1}m, UIDs {maxUID + 1}-{coronaNextUID - 1})");
            }

            // Apply RF2 lightmap multiplier to all light intensities (post-loop since section order varies)
            if (isRF2 && mesh.LightmapMultiplier != 1.0f)
            {
                Logger.Warn(logSrc, $"Applying RF2 lightmap multiplier ({mesh.LightmapMultiplier}) to {mesh.Lights.Count} lights");
                foreach (var light in mesh.Lights)
                {
                    light.OnIntensity *= mesh.LightmapMultiplier;
                    light.OffIntensity *= mesh.LightmapMultiplier;
                }
            }

            if (isRF2)
            {
                int rawR = (int)mesh.AmbientColor.X;
                int rawG = (int)mesh.AmbientColor.Y;
                int rawB = (int)mesh.AmbientColor.Z;

                // Compute recommended ambient from baked vertex color data.
                // median_baked × 1.5 estimates the ambient needed in RF1 to produce
                // similar overall brightness (accounts for RED2's ~0.2 ambient factor
                // and inverse-square attenuation vs RED's ~0.5 factor and linear falloff).
                // Take the max with raw RF2 ambient to preserve high-ambient maps.
                int recR = Math.Min(255, Math.Max(rawR, (int)Math.Round(rf2MedianBaked.X * 1.5f)));
                int recG = Math.Min(255, Math.Max(rawG, (int)Math.Round(rf2MedianBaked.Y * 1.5f)));
                int recB = Math.Min(255, Math.Max(rawB, (int)Math.Round(rf2MedianBaked.Z * 1.5f)));

                int originalLightCount = mesh.Lights.Count - coronaDerivedLightCount;
                Logger.Warn(logSrc, $"RF2 light conversion summary: {originalLightCount} lights + {coronaDerivedLightCount} corona-derived lights, rf2lightscale={Config.RF2LightScale}, lmMult={mesh.LightmapMultiplier}, effective={Config.RF2LightScale * mesh.LightmapMultiplier}x");
                Logger.Warn(logSrc, $"RF2 ambient (raw): R={rawR} G={rawG} B={rawB}");
                Logger.Warn(logSrc, $"RF2 baked median: R={rf2MedianBaked.X} G={rf2MedianBaked.Y} B={rf2MedianBaked.Z}");
                Logger.Warn(logSrc, $"Recommended RF1 ambient: R={recR} G={recG} B={recB} (set in RED level properties)");

                // Fog settings
                if (levelProperties != null)
                {
                    Logger.Warn(logSrc, $"RF2 fog: color=({levelProperties.FogR},{levelProperties.FogG},{levelProperties.FogB}) near={levelProperties.FogNear} far={levelProperties.FogFar}");
                }

                // Geoable brush UIDs
                var geoableUIDs = new List<int>();
                foreach (var brush in mesh.Brushes)
                {
                    if (brush.Solid != null && (brush.Solid.Flags & (uint)SolidFlags.Geoable) != 0)
                        geoableUIDs.Add(brush.UID);
                }
                if (geoableUIDs.Count > 0)
                    Logger.Warn(logSrc, $"RF2 geoable brush UIDs ({geoableUIDs.Count}): {string.Join(", ", geoableUIDs)}");
                else
                    Logger.Warn(logSrc, "RF2 geoable brush UIDs: none");
            }

            return mesh;
        }
    }

    static class RflCoronaParser
    {
        private const string logSrc = "RflCoronaParser";

        public static List<Corona> ParseCoronasFromRfl(BinaryReader reader, long sectionEnd)
        {
            int count = reader.ReadInt32();
            Logger.Info(logSrc, $"Corona count = {count}");
            var list = new List<Corona>(count);

            for (int i = 0; i < count; i++)
            {
                var c = new Corona();
                long startPos = reader.BaseStream.Position;

                c.UID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Corona[{i}] UID = {c.UID}");

                c.Name = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Corona[{i}] Name = \"{c.Name}\"");

                float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                c.Position = new Vector3(px, py, pz);
                Logger.Dev(logSrc, $"Corona[{i}] Pos = ({px:F3}, {py:F3}, {pz:F3})");

                // Rotation matrix: 9 floats (36 bytes) per rf2.exe FUN_005289f0
                // Stream order: row3(right), row1(forward), row2(up)
                float[] rf = new float[9];
                for (int j = 0; j < 9; j++) rf[j] = reader.ReadSingle();
                // Map to RF1 convention: Row1=right, Row2=up, Row3=forward(cone dir)
                c.Orientation = new Matrix4x4(
                    rf[0], rf[1], rf[2], 0,  // Row1 = right (stream row3)
                    rf[6], rf[7], rf[8], 0,  // Row2 = up (stream row2)
                    rf[3], rf[4], rf[5], 0,  // Row3 = forward (stream row1, cone direction)
                    0, 0, 0, 1
                );
                Vector3 fwd = new Vector3(rf[3], rf[4], rf[5]);
                Logger.Dev(logSrc, $"Corona[{i}] Direction = ({fwd.X:F3}, {fwd.Y:F3}, {fwd.Z:F3})");

                c.ScriptName = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Corona[{i}] Script Name = \"{c.ScriptName}\"");

                // Version-gated fields per rf2.exe FUN_00528b60/FUN_00528950
                reader.ReadByte(); // bool (v>=0)
                byte r = reader.ReadByte(), g = reader.ReadByte(),
                     b = reader.ReadByte(), a = reader.ReadByte(); // RGBA color (v>=0)
                c.Color = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
                Logger.Dev(logSrc, $"Corona[{i}] Color = ({r},{g},{b},{a})");
                reader.ReadByte(); // bool (v>=0x103)
                reader.ReadByte(); // bool (v>=0x120)
                reader.ReadByte(); // bool (v>=0x121)
                reader.ReadByte(); // bool (v>=0x124)
                reader.ReadByte(); // bool (v>=0xe8)
                

                c.CoronaBitmap = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Corona[{i}] CoronaBitmap = \"{c.CoronaBitmap}\"");

                c.ConeAngle = reader.ReadSingle();
                Logger.Dev(logSrc, $"Corona[{i}] ConeAngle = {c.ConeAngle}");
                c.Intensity = reader.ReadSingle();
                Logger.Dev(logSrc, $"Corona[{i}] Intensity = {c.Intensity}");
                c.RadiusDistance = reader.ReadSingle();
                Logger.Dev(logSrc, $"Corona[{i}] RadiusDistance = {c.RadiusDistance}");
                c.RadiusScale = reader.ReadSingle();
                Logger.Dev(logSrc, $"Corona[{i}] RadiusScale = {c.RadiusScale}");
                c.DiminishDistance = reader.ReadSingle();
                Logger.Dev(logSrc, $"Corona[{i}] DiminishDistance = {c.DiminishDistance}");

                c.VolumetricBitmap = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Corona[{i}] VolumetricBitmap = \"{c.VolumetricBitmap}\"");

                if (!string.IsNullOrEmpty(c.VolumetricBitmap))
                {
                    c.VolumetricHeight = reader.ReadSingle();
                    Logger.Dev(logSrc, $"Corona[{i}] VolumetricHeight = {c.VolumetricHeight}");
                    c.VolumetricLength = reader.ReadSingle();
                    Logger.Dev(logSrc, $"Corona[{i}] VolumetricLength = {c.VolumetricLength}");

                    // unknown value
                    reader.ReadSingle();
                }

                // unknown fields
                reader.ReadByte();
                reader.ReadByte();
                reader.ReadByte();
                reader.ReadByte();
                reader.ReadByte();

                list.Add(c);

                Logger.Dev(logSrc, $"-- bytes consumed for Corona[{i}]: {reader.BaseStream.Position - startPos}\n");
            }

            return list;
        }
    }
    static class RflClutterParser
    {
        private const string logSrc = "RflClutterParser";

        public static List<Clutter> ParseCluttersFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numClutters = reader.ReadInt32();
            Logger.Debug(logSrc, $"Reading {numClutters} clutter(s)…");

            var clutters = new List<Clutter>(numClutters);
            for (int i = 0; i < numClutters; i++)
            {
                var c = new Clutter();

                c.UID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Clutter[{i}] UID = {c.UID}");

                c.ClassName = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Clutter[{i}] ClassName = \"{c.ClassName}\"");

                float px = reader.ReadSingle(),
                      py = reader.ReadSingle(),
                      pz = reader.ReadSingle();
                c.Position = new Vector3(px, py, pz);
                Logger.Dev(logSrc, $"Clutter[{i}] Pos = ({px:F2},{py:F2},{pz:F2})");

                float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                c.Rotation = new Matrix4x4(
                    m00, m01, m02, 0f,
                    m10, m11, m12, 0f,
                    m20, m21, m22, 0f,
                    0f, 0f, 0f, 1f
                );
                Logger.Dev(logSrc, $"Clutter[{i}] Rotation matrix read");

                c.ScriptName = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Clutter[{i}] ScriptName = \"{c.ScriptName}\"");

                c.HiddenInEditor = reader.ReadByte() != 0;
                Logger.Dev(logSrc, $"Clutter[{i}] HiddenInEditor = {c.HiddenInEditor}");

                // RF1 has an unknown int here that isn't loaded by the game, not present in RF2
                if (RflUtils.IsRF1(rfl_version))
                {
                    int unk = reader.ReadInt32();
                    Logger.Dev(logSrc, $"Clutter[{i}] Unknown field = {unk}");
                }               

                c.Skin = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Clutter[{i}] Skin = \"{c.Skin}\"");

                int numLinks = reader.ReadInt32();
                c.Links = new List<int>(numLinks);
                for (int j = 0; j < numLinks; j++)
                {
                    int link = reader.ReadInt32();
                    c.Links.Add(link);
                }
                Logger.Dev(logSrc, $"Clutter[{i}] Links = [{string.Join(",", c.Links)}]");

                clutters.Add(c);
            }

            Logger.Debug(logSrc, "Finished parsing clutters");
            return clutters;
        }
    }

    static class RflMoverParser
    {
        private const string logSrc = "RflMoverParser";

        public static List<Brush> ParseMoversFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numMovers = reader.ReadInt32();
            Logger.Debug(logSrc, $"Reading {numMovers} mover(s)...");
            Logger.Dev(logSrc, $"→ Section size: {sectionEnd - reader.BaseStream.Position} bytes");

            var movers = new List<Brush>(numMovers);
            for (int i = 0; i < numMovers; i++)
            {
                long startPos = reader.BaseStream.Position;
                var moverBrush = RFGeometryParser.ReadBrush(reader, rfl_version);
                if (moverBrush != null)
                {
                    Logger.Dev(logSrc,
                        $"Mover[{i}] UID={moverBrush.UID}, " +
                        $"Vertices={moverBrush.Vertices.Count}, " +
                        $"Faces={moverBrush.Solid.Faces.Count}, " +
                        $"BytesConsumed={reader.BaseStream.Position - startPos}"
                    );
                    movers.Add(moverBrush);
                }
                else
                {
                    Logger.Dev(logSrc, $"Mover[{i}] returned null brush, skipping");
                }
            }

            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            Logger.Dev(logSrc, $"Finished parsing movers; reader at 0x{reader.BaseStream.Position:X}");
            return movers;
        }
    }

    static class RflGroupParser
    {
        private const string logSrc = "RflGroupParser";

        public static List<Group> ParseGroupsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numGroups = reader.ReadInt32();
            Logger.Debug(logSrc, $"Reading {numGroups} group(s)...");
            Logger.Dev(logSrc, $"→ Section size: {sectionEnd - reader.BaseStream.Position} bytes");

            var groups = new List<Group>(numGroups);
            for (int i = 0; i < numGroups; i++)
            {
                long groupStart = reader.BaseStream.Position;
                var grp = new Group();

                grp.Name = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"Group[{i}] Name=\"{grp.Name}\"");

                byte unknown = reader.ReadByte();
                Logger.Info(logSrc, $"Group[{i}] UnknownByte=0x{unknown:X2}");

                if (RflUtils.IsRF2(rfl_version))
                {
                    //reader.ReadInt32();
                    var unk1 = reader.ReadByte() != 0;
                    var unk2 = reader.ReadByte() != 0;
                    var unk3 = reader.ReadByte() != 0;
                    var unk4 = reader.ReadByte() != 0;
                    Logger.Info(logSrc, $"Group[{i}] unk1={unk1}, unk2={unk2}, unk3={unk3}, unk4={unk4}");
                }

                grp.IsMoving = reader.ReadByte() != 0;
                Logger.Info(logSrc, $"Group[{i}] IsMoving={grp.IsMoving}");             

                if (grp.IsMoving)
                {                   grp.MovingData = new MovingGroupData();
                    int numKeyframes = reader.ReadInt32();
                    Logger.Dev(logSrc, $"Group[{i}] KeyframesCount={numKeyframes}");

                    for (int k = 0; k < numKeyframes; k++)
                    {
                        var kf = new Keyframe
                        {
                            UID = reader.ReadInt32(),
                            Pos = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                            Rot = new System.Numerics.Matrix4x4(
                                                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                                    0f, 0f, 0f, 1f),
                            ScriptName = Utils.ReadVString(reader),
                            HiddenInEditor = reader.ReadByte() != 0,
                            PauseTime = reader.ReadSingle(),
                            DepartTravelTime = reader.ReadSingle(),
                            ReturnTravelTime = reader.ReadSingle(),
                            AccelTime = reader.ReadSingle(),
                            DecelTime = reader.ReadSingle(),
                            EventUID = reader.ReadInt32(),
                            ItemUID1 = reader.ReadInt32(),
                            ItemUID2 = reader.ReadInt32(),
                            DegreesAboutAxis = reader.ReadSingle()
                        };
                        Logger.Dev(logSrc,
                            $"  Keyframe[{k}] UID={kf.UID}, Pos=({kf.Pos.X:F2},{kf.Pos.Y:F2},{kf.Pos.Z:F2}), " +
                            $"PauseTime={kf.PauseTime:F2}"
                        );
                        grp.MovingData.Keyframes.Add(kf);
                    }

                    int numMemberTransforms = reader.ReadInt32();
                    Logger.Dev(logSrc, $"Group[{i}] MemberTransformsCount={numMemberTransforms}");
                    for (int m = 0; m < numMemberTransforms; m++)
                    {
                        var mt = new MovingGroupMemberTransform
                        {
                            UID = reader.ReadInt32(),
                            Pos = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                            Rot = new System.Numerics.Matrix4x4(
                                      reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                      reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                      reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0f,
                                      0f, 0f, 0f, 1f)
                        };
                        Logger.Dev(logSrc,
                            $"  MemberTransform[{m}] UID={mt.UID}, Pos=({mt.Pos.X:F2},{mt.Pos.Y:F2},{mt.Pos.Z:F2})"
                        );
                        grp.MovingData.MemberTransforms.Add(mt);
                    }

                    // additional flags
                    grp.MovingData.IsDoor = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  IsDoor={grp.MovingData.IsDoor}");
                    grp.MovingData.RotateInPlace = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  RotateInPlace={grp.MovingData.RotateInPlace}");
                    grp.MovingData.StartsBackwards = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  StartsBackwards={grp.MovingData.StartsBackwards}");
                    grp.MovingData.UseTravelTimeAsSpeed = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  UseTravelTimeAsSpeed={grp.MovingData.UseTravelTimeAsSpeed}");
                    grp.MovingData.ForceOrient = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  ForceOrient={grp.MovingData.ForceOrient}");
                    grp.MovingData.NoPlayerCollide = reader.ReadByte() != 0; Logger.Dev(logSrc, $"  NoPlayerCollide={grp.MovingData.NoPlayerCollide}");

                    // unk RF2 specific field
                    if (RflUtils.IsRF2(rfl_version))
                    {
                        reader.ReadByte();
                    }
                    
                    grp.MovingData.MovementType = reader.ReadInt32(); Logger.Dev(logSrc, $"  MovementType={grp.MovingData.MovementType}");
                    grp.MovingData.StartingKeyframe = reader.ReadInt32(); Logger.Dev(logSrc, $"  StartingKeyframe={grp.MovingData.StartingKeyframe}");
                    grp.MovingData.StartSound = Utils.ReadVString(reader); Logger.Dev(logSrc, $"  StartSound=\"{grp.MovingData.StartSound}\"");
                    grp.MovingData.StartVol = reader.ReadSingle(); Logger.Dev(logSrc, $"  StartVol={grp.MovingData.StartVol}");
                    grp.MovingData.LoopingSound = Utils.ReadVString(reader); Logger.Dev(logSrc, $"  LoopingSound=\"{grp.MovingData.LoopingSound}\"");
                    grp.MovingData.LoopingVol = reader.ReadSingle(); Logger.Dev(logSrc, $"  LoopingVol={grp.MovingData.LoopingVol}");
                    grp.MovingData.StopSound = Utils.ReadVString(reader); Logger.Dev(logSrc, $"  StopSound=\"{grp.MovingData.StopSound}\"");
                    grp.MovingData.StopVol = reader.ReadSingle(); Logger.Dev(logSrc, $"  StopVol={grp.MovingData.StopVol}");
                    grp.MovingData.CloseSound = Utils.ReadVString(reader); Logger.Dev(logSrc, $"  CloseSound=\"{grp.MovingData.CloseSound}\"");
                    grp.MovingData.CloseVol = reader.ReadSingle(); Logger.Dev(logSrc, $"  CloseVol={grp.MovingData.CloseVol}");

                    
                }

                // unk RF2 specific field
                if (RflUtils.IsRF2(rfl_version))
                {
                    var unk1 = reader.ReadInt32();
                    Logger.Dev(logSrc, $"Group[{i}] unk1={unk1}");

                }

                grp.Objects = ReadUidList(reader);
                Logger.Info(logSrc, $"Group[{i}] ObjectsCount={grp.Objects.Count}");

                grp.Brushes = ReadUidList(reader);
                Logger.Info(logSrc, $"Group[{i}] BrushesCount={grp.Brushes.Count}");

                // unk RF2 specific fields
                if ( RflUtils.IsRF2(rfl_version))
                {
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt32();
                }

                groups.Add(grp);
                Logger.Dev(logSrc,
                    $"Parsed Group[{i}] \"{grp.Name}\" @ bytes {reader.BaseStream.Position - groupStart}"
                );
            }

            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            Logger.Dev(logSrc, $"Finished parsing groups; reader at 0x{reader.BaseStream.Position:X}");
            return groups;
        }

        private static List<int> ReadUidList(BinaryReader reader)
        {
            int n = reader.ReadInt32();
            Logger.Info(logSrc, $"Count: {n}");
            var list = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                int uid = reader.ReadInt32();
                list.Add(uid);
                Logger.Info(logSrc, $"Element[{i}]: {uid}");
            }
            return list;
        }
    }
    static class RflDecalParser
    {
        private const string logSrc = "RflDecalParser";

        public static List<Decal> ParseDecalsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numDecals = reader.ReadInt32();
            Logger.Info(logSrc, $"Found {numDecals} decal(s).");

            var result = new List<Decal>(numDecals);
            for (int i = 0; i < numDecals; i++)
            {
                var d = new Decal();

                d.UID = reader.ReadInt32();

                d.ClassName = Utils.ReadVString(reader);

                float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                d.Position = new Vector3(px, py, pz);

                float fx = reader.ReadSingle(), fy = reader.ReadSingle(), fz = reader.ReadSingle();
                float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
                float ux = reader.ReadSingle(), uy = reader.ReadSingle(), uz = reader.ReadSingle();
                // embed mat3 into a 4×4 matrix (row‐major as usual):
                d.Rotation = new Matrix4x4(
                    rx, ry, rz, 0f,
                    ux, uy, uz, 0f,
                    fx, fy, fz, 0f,
                    0f, 0f, 0f, 1f
                );

                d.ScriptName = Utils.ReadVString(reader);

                d.HiddenInEditor = reader.ReadByte() != 0;

                float ex = reader.ReadSingle(), ey = reader.ReadSingle(), ez = reader.ReadSingle();
                d.Extents = new Vector3(ex, ey, ez);

                var tex = Utils.ReadVString(reader);

                if (RflUtils.IsRF2(rfl_version) && Config.TranslateRF2Textures)
                {
                    string translatedTex = RF2TextureTranslator.TranslateRF2Texture(tex);
                    Logger.Dev(logSrc, $"Texture {i}: \"{tex}\" → \"{translatedTex}\"");
                    d.Texture = translatedTex;
                }
                else if (RflUtils.IsRF2(rfl_version) && Config.InsertRF2TexturePrefix)
                {
                    string translatedTex = RF2TextureTranslator.InsertRxPrefix(tex);
                    Logger.Dev(logSrc, $"Texture {i}: \"{tex}\" → \"{translatedTex}\"");
                    d.Texture = translatedTex;
                }
                else
                {
                    Logger.Dev(logSrc, $"Texture {i}: \"{tex}\"");
                    d.Texture = tex;
                }

                d.Alpha = reader.ReadInt32();

                d.SelfIlluminated = reader.ReadByte() != 0;

                d.Tiling = reader.ReadInt32();

                d.Scale = reader.ReadSingle();

                // Unknown fields in RF2 RFLs - uncertain if data types are correct
                // One of these is "glow" (like in particle emitters), unsure which
                if (RflUtils.IsRF2(rfl_version))
                {
                    var unk1 = reader.ReadSingle();
                    var unk2 = reader.ReadInt32();
                    var unk3 = reader.ReadInt32();
                    Logger.Dev(logSrc, $"RF2 specific fields - Decal[{i}] unk1={unk1}, unk2={unk2}, unk3={unk3}");
                }

                Logger.Dev(logSrc,
                    $"Decal[{i}] UID={d.UID}, ClassName=\"{d.ClassName}\", Pos=({px},{py},{pz}), " +
                    $"Rot=[{fx},{fy},{fz} ; {rx},{ry},{rz} ; {ux},{uy},{uz}], " +
                    $"Script=\"{d.ScriptName}\", Hidden={d.HiddenInEditor}, Extents=({ex},{ey},{ez}), " +
                    $"Texture=\"{d.Texture}\", Alpha={d.Alpha}, SelfIlluminated={d.SelfIlluminated}, " +
                    $"Tiling={d.Tiling}, Scale={d.Scale}"
                );

                result.Add(d);
            }

            return result;
        }

        // RF2 decals (chunk 0x0F00) use a simpler class-based format.
        // Properties like texture, extents, alpha are defined externally by class name.
        public static List<Decal> ParseDecalsFromRflRF2(BinaryReader reader, long sectionEnd)
        {
            int numDecals = reader.ReadInt32();
            Logger.Info(logSrc, $"Found {numDecals} RF2 decal(s).");

            var result = new List<Decal>(numDecals);
            for (int i = 0; i < numDecals; i++)
            {
                var d = new Decal();

                d.UID = reader.ReadInt32();
                d.ClassName = Utils.ReadVString(reader);

                float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                d.Position = new Vector3(px, py, pz);

                float fx = reader.ReadSingle(), fy = reader.ReadSingle(), fz = reader.ReadSingle();
                float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
                float ux = reader.ReadSingle(), uy = reader.ReadSingle(), uz = reader.ReadSingle();
                d.Rotation = new Matrix4x4(
                    rx, ry, rz, 0f,
                    ux, uy, uz, 0f,
                    fx, fy, fz, 0f,
                    0f, 0f, 0f, 1f
                );

                d.ScriptName = Utils.ReadVString(reader);
                d.HiddenInEditor = reader.ReadByte() != 0;

                Logger.Dev(logSrc,
                    $"RF2 Decal[{i}] UID={d.UID}, ClassName=\"{d.ClassName}\", " +
                    $"Pos=({px:F2},{py:F2},{pz:F2}), Script=\"{d.ScriptName}\", Hidden={d.HiddenInEditor}");

                result.Add(d);
            }

            return result;
        }
    }

    static class RflWaypointListParser
    {
        private const string logSrc = "RflWaypointListParser";

        public static List<WaypointList> ParseWaypointListsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numLists = reader.ReadInt32();
            Logger.Dev(logSrc, $"Found {numLists} waypoint_list(s).");

            var result = new List<WaypointList>(numLists);
            for (int i = 0; i < numLists; i++)
            {
                var list = new WaypointList();

                list.Name = Utils.ReadVString(reader);
                Logger.Dev(logSrc, $"WaypointList[{i}].Name = \"{list.Name}\"");

                int countWp = reader.ReadInt32();
                Logger.Dev(logSrc, $"WaypointList[{i}].Count = {countWp}");

                for (int w = 0; w < countWp; w++)
                {
                    int idx = reader.ReadInt32();
                    list.WaypointIndices.Add(idx);
                    Logger.Dev(logSrc, $"WaypointList[{i}].WaypointIndices[{w}] = {idx}");
                }

                result.Add(list);
            }

            return result;
        }
    }

    static class RflNavPointParser
    {
        private const string logSrc = "RflNavPointParser";

        public static List<NavPoint> ParseNavPointsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int numPoints = reader.ReadInt32();
            Logger.Info(logSrc, $"Found {numPoints} nav_point(s).");

            var tempPoints = new List<NavPoint>(numPoints);

            for (int i = 0; i < numPoints; i++)
            {
                var np = new NavPoint();

                np.UID = reader.ReadInt32();
                np.HiddenInEditor = reader.ReadByte() != 0;
                np.Height = reader.ReadSingle();

                float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                np.Position = new Vector3(px, py, pz);

                np.Radius = reader.ReadSingle();

                np.Type = RflUtils.IsRF1(rfl_version) ? (NavPointType)reader.ReadInt32() : (NavPointType)reader.ReadByte();

                // only used in rf2 parsing
                byte unk1 = 0;
                byte unk2 = 0;

                if (RflUtils.IsRF2(rfl_version))
                {
                    unk1 = reader.ReadByte();
                    unk2 = reader.ReadByte();
                }

                np.Directional = reader.ReadByte() != 0;

                //reader.ReadByte();

                // only used in rf2 parsing
                float unk3 = 0.0f;

                if (RflUtils.IsRF2(rfl_version))
                {
                    unk3 = reader.ReadSingle();
                }

                if (np.Directional)
                {
                    float fx = reader.ReadSingle(), fy = reader.ReadSingle(), fz = reader.ReadSingle();
                    float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
                    float ux = reader.ReadSingle(), uy = reader.ReadSingle(), uz = reader.ReadSingle();

                    np.Rotation = new Matrix4x4(
                        rx, ry, rz, 0f,
                        ux, uy, uz, 0f,
                        fx, fy, fz, 0f,
                        0f, 0f, 0f, 1f
                    );
                }

                // only used in rf2 parsing
                int unk4 = 0;

                if (RflUtils.IsRF1(rfl_version))
                {
                    np.Cover = reader.ReadByte() != 0;
                    np.Hide = reader.ReadByte() != 0;
                    np.Crunch = reader.ReadByte() != 0;
                }
                else
                {
                    unk4 = reader.ReadInt32();
                }
                
                np.PauseTime = reader.ReadSingle();

                // only used in rf2 parsing
                float unk5 = 0.0f;

                if (RflUtils.IsRF2(rfl_version))
                {
                    // not convinced this is actually pause time in rf2
                    if (np.PauseTime > 0)
                    {
                        unk5 = reader.ReadSingle();
                    }
                }
                else
                {
                    int numLinks = reader.ReadInt32();
                    for (int L = 0; L < numLinks; L++)
                    {
                        int linkIdx = reader.ReadInt32();
                        np.LinkIndices.Add(linkIdx);
                    }
                }

                Logger.Dev(logSrc,
                        $"NavPoint[{i}] UID={np.UID}, HiddenInEditor={np.HiddenInEditor}, Height={np.Height}, " +
                        $"Position=({px},{py},{pz}), Radius={np.Radius}, Type={np.Type}, Directional={np.Directional}, " +
                        $"Cover={np.Cover}, Hide={np.Hide}, Crunch={np.Crunch}, PauseTime={np.PauseTime}, " +
                        $"InitialLinks=[{string.Join(",", np.LinkIndices)}]" +
                        $"  unk1={unk1}, unk2={unk2}, unk3={unk3}, unk4={unk4}, unk5={unk5}"
                    );

                tempPoints.Add(np);
            }

            // not exported in rfg anyway
            /*
            // Read numPoints nav_point_connections arrays
            for (int i = 0; i < numPoints; i++)
            {
                byte numIdx = reader.ReadByte();
                var connections = new List<int>(numIdx);
                for (int c = 0; c < numIdx; c++)
                {
                    int idx = reader.ReadInt32();
                    connections.Add(idx);
                }

                tempPoints[i].LinkIndices.AddRange(connections);
                Logger.Dev(logSrc,
                    $"NavPointConnections[{i}].Count = {numIdx}; Connections=[{string.Join(",", connections)}]"
                );
            }
            */
            return tempPoints;
        }
    }

    public static class RflLightmapParser
    {
        private const string logSrc = "RflLightmapParser";

        public static List<Lightmap> ParseLightmapsFromRfl(BinaryReader reader, long sectionEnd)
        {
            var lightmaps = new List<Lightmap>();

            int numLightmaps = reader.ReadInt32();
            Logger.Dev(logSrc, $"Found {numLightmaps} lightmap(s).");

            for (int i = 0; i < numLightmaps; i++)
            {
                var lm = new Lightmap();

                // Read width and height
                lm.Width = reader.ReadInt32();
                lm.Height = reader.ReadInt32();
                Logger.Dev(logSrc, $"Lightmap[{i}] dimensions: {lm.Width}×{lm.Height}");

                // Compute total byte count (24 bpp → 3 bytes per pixel)
                int pixelCount = lm.Width * lm.Height;
                int byteCount = pixelCount * 3;

                // Sanity check
                long bytesRemainingInSection = sectionEnd - reader.BaseStream.Position;
                if (bytesRemainingInSection < byteCount)
                {
                    Logger.Warn(logSrc, $"Section ended prematurely while reading lightmap[{i}]: " +
                                 $"needed {byteCount} bytes but only {bytesRemainingInSection} remain. Clamping to available.");
                    byteCount = (int)bytesRemainingInSection;
                }

                // Read raw RGB bytes
                lm.PixelData = reader.ReadBytes(byteCount);

                // Store and log
                Logger.Dev(logSrc, $"Read {lm.PixelData.Length} bytes of lightmap[{i}].");
                lightmaps.Add(lm);
            }

            return lightmaps;
        }
    }

    public static class RflRF2LightmapParser
    {
        private const string logSrc = "RflRF2LightmapParser";

        private static byte ClampToByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
            {
                bytes.Add(b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public static RF2LightmapData ParseRF2LightmapsFromRfl(BinaryReader reader, long sectionEnd)
        {
            var data = new RF2LightmapData();

            data.Version = reader.ReadInt32();
            if (data.Version != 5)
            {
                Logger.Warn(logSrc, $"Unsupported RF2 lightmap version {data.Version} (expected 5), skipping.");
                return null;
            }

            int numTextureNames = reader.ReadInt32();
            int numFaceGroups = reader.ReadInt32();
            Logger.Dev(logSrc, $"Version={data.Version}, textureNames={numTextureNames}, faceGroups={numFaceGroups}");

            // Texture name table
            for (int i = 0; i < numTextureNames; i++)
            {
                data.TextureNames.Add(ReadNullTerminatedString(reader));
            }

            // Light probes
            int numLightProbes = reader.ReadInt32();
            for (int i = 0; i < numLightProbes; i++)
            {
                float a = reader.ReadSingle();
                float b = reader.ReadSingle();
                data.LightProbes.Add((a, b));
            }

            // Read all face group lightmap IDs first (pre-loop)
            var lightmapIds = new int[numFaceGroups];
            for (int i = 0; i < numFaceGroups; i++)
            {
                lightmapIds[i] = reader.ReadInt32();
            }

            // Parse each face group
            for (int g = 0; g < numFaceGroups; g++)
            {
                var group = new RF2LightmapFaceGroup();
                group.LightmapId = lightmapIds[g];

                int numEntries = reader.ReadInt32();
                Logger.Dev(logSrc, $"FaceGroup[{g}] id={group.LightmapId}, entries={numEntries}");

                for (int e = 0; e < numEntries; e++)
                {
                    var entry = new RF2LightmapEntry();

                    entry.FaceVertexCount = reader.ReadInt32();
                    entry.TriangleVertexCount = reader.ReadInt32();
                    entry.TextureIndex = reader.ReadInt32();
                    entry.Unknown1 = reader.ReadInt32();
                    entry.Unknown2 = reader.ReadInt32();

                    // Vertex positions
                    for (int v = 0; v < entry.FaceVertexCount; v++)
                    {
                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        float z = reader.ReadSingle();
                        entry.VertexPositions.Add(new Vector3(x, y, z));
                    }

                    // AABB min/max (2 × Vec3, read and discard)
                    reader.BaseStream.Seek(24, SeekOrigin.Current);

                    // Triangle vertex data
                    for (int v = 0; v < entry.TriangleVertexCount; v++)
                    {
                        var vert = new RF2LightmapVertex();

                        // 3 indices
                        vert.Index0 = reader.ReadInt32();
                        vert.Index1 = reader.ReadInt32();
                        vert.Index2 = reader.ReadInt32();

                        // 9 color values as int32, clamped to [0,255]
                        // 3 per-vertex RGB colors in vertex-major order:
                        // v0_R, v0_G, v0_B, v1_R, v1_G, v1_B, v2_R, v2_G, v2_B
                        vert.V0_R = ClampToByte(reader.ReadInt32());
                        vert.V0_G = ClampToByte(reader.ReadInt32());
                        vert.V0_B = ClampToByte(reader.ReadInt32());
                        vert.V1_R = ClampToByte(reader.ReadInt32());
                        vert.V1_G = ClampToByte(reader.ReadInt32());
                        vert.V1_B = ClampToByte(reader.ReadInt32());
                        vert.V2_R = ClampToByte(reader.ReadInt32());
                        vert.V2_G = ClampToByte(reader.ReadInt32());
                        vert.V2_B = ClampToByte(reader.ReadInt32());

                        // 6 interleaved floats: per-vertex UV coordinates
                        // File order: u0, v0, u1, v1, u2, v2
                        float u0 = reader.ReadSingle();
                        float v0 = reader.ReadSingle();
                        float u1 = reader.ReadSingle();
                        float v1 = reader.ReadSingle();
                        float u2 = reader.ReadSingle();
                        float v2 = reader.ReadSingle();
                        vert.UCoords = new Vector3(u0, u1, u2);
                        vert.VCoords = new Vector3(v0, v1, v2);

                        // Alpha as int32, clamped
                        vert.Alpha = ClampToByte(reader.ReadInt32());

                        // Trailing Vec3 (discard)
                        reader.BaseStream.Seek(12, SeekOrigin.Current);

                        entry.TriangleVertices.Add(vert);
                    }

                    group.Entries.Add(entry);
                }

                data.FaceGroups.Add(group);
            }

            Logger.Dev(logSrc, $"Finished parsing RF2 lightmaps: {data.FaceGroups.Count} face groups");
            return data;
        }

        public static List<Lightmap> ConvertToLightmaps(RF2LightmapData data)
        {
            var lightmaps = new List<Lightmap>();

            foreach (var group in data.FaceGroups)
            {
                foreach (var entry in group.Entries)
                {
                    if (entry.TriangleVertexCount == 0)
                        continue;

                    // Each triangle entry has 3 per-vertex baked colors.
                    // Produce 3 pixels per triangle (one per vertex).
                    int count = entry.TriangleVertexCount;
                    int totalPixels = count * 3; // 3 vertices per triangle entry
                    int width = (int)Math.Ceiling(Math.Sqrt(totalPixels));
                    int height = (int)Math.Ceiling((double)totalPixels / width);

                    var lm = new Lightmap();
                    lm.Width = width;
                    lm.Height = height;
                    lm.PixelData = new byte[width * height * 3]; // zero-initialized (black padding)

                    for (int v = 0; v < count; v++)
                    {
                        var vert = entry.TriangleVertices[v];
                        // Vertex 0
                        int offset = (v * 3) * 3;
                        lm.PixelData[offset + 0] = vert.V0_R;
                        lm.PixelData[offset + 1] = vert.V0_G;
                        lm.PixelData[offset + 2] = vert.V0_B;
                        // Vertex 1
                        offset = (v * 3 + 1) * 3;
                        lm.PixelData[offset + 0] = vert.V1_R;
                        lm.PixelData[offset + 1] = vert.V1_G;
                        lm.PixelData[offset + 2] = vert.V1_B;
                        // Vertex 2
                        offset = (v * 3 + 2) * 3;
                        lm.PixelData[offset + 0] = vert.V2_R;
                        lm.PixelData[offset + 1] = vert.V2_G;
                        lm.PixelData[offset + 2] = vert.V2_B;
                    }

                    lightmaps.Add(lm);
                }
            }

            Logger.Dev(logSrc, $"Converted {lightmaps.Count} RF2 lightmap entries to bitmap lightmaps");
            return lightmaps;
        }

        public static Vector3 ComputeMedianBakedColor(RF2LightmapData data)
        {
            var reds = new List<byte>();
            var greens = new List<byte>();
            var blues = new List<byte>();

            foreach (var group in data.FaceGroups)
            {
                foreach (var entry in group.Entries)
                {
                    foreach (var vert in entry.TriangleVertices)
                    {
                        reds.Add(vert.V0_R); greens.Add(vert.V0_G); blues.Add(vert.V0_B);
                        reds.Add(vert.V1_R); greens.Add(vert.V1_G); blues.Add(vert.V1_B);
                        reds.Add(vert.V2_R); greens.Add(vert.V2_G); blues.Add(vert.V2_B);
                    }
                }
            }

            if (reds.Count == 0) return Vector3.Zero;

            reds.Sort();
            greens.Sort();
            blues.Sort();

            int mid = reds.Count / 2;
            return new Vector3(reds[mid], greens[mid], blues[mid]);
        }
    }

    public static class RflParticleEmitterParser
    {
        private const string logSrc = "RflParticleEmitterParser";

        public static List<ParticleEmitter> ParseParticleEmittersFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            var emitters = new List<ParticleEmitter>();

            int count = reader.ReadInt32();
            Logger.Warn(logSrc, $"Particle emitters count: {count}");
            for (int i = 0; i < count; i++)
            {
                var e = new ParticleEmitter();

                e.UID = reader.ReadInt32();
                Logger.Warn(logSrc, $"Emitter[{i}].UID = {e.UID}");

                e.ClassName = ReadVString(reader);
                Logger.Warn(logSrc, $"Emitter[{i}].ClassName = \"{e.ClassName}\"");

                // Position (vec3)
                e.Position = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                );
                Logger.Warn(logSrc, $"Emitter[{i}].Position = {e.Position}");

                // Rotation (mat3)
                float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                e.Rotation = new Matrix4x4(
                    m00, m01, m02, 0f,
                    m10, m11, m12, 0f,
                    m20, m21, m22, 0f,
                    0f, 0f, 0f, 1f
                );
                Logger.Warn(logSrc, $"Emitter[{i}].Rotation = [ [{m00}, {m01}, {m02}], [{m10}, {m11}, {m12}], [{m20}, {m21}, {m22}] ]");

                e.ScriptName = ReadVString(reader);
                Logger.Warn(logSrc, $"Emitter[{i}].ScriptName = \"{e.ScriptName}\"");

                e.HiddenInEditor = reader.ReadByte() != 0;
                Logger.Warn(logSrc, $"Emitter[{i}].HiddenInEditor = {e.HiddenInEditor}");

                e.Shape = (ParticleEmitterShape)reader.ReadInt32();
                Logger.Warn(logSrc, $"Emitter[{i}].Shape = {e.Shape}");

                e.SphereRadius = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].SphereRadius = {e.SphereRadius}");
                e.PlaneWidth = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].PlaneWidth = {e.PlaneWidth}");
                e.PlaneDepth = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].PlaneDepth = {e.PlaneDepth}");

                e.Texture = ReadVString(reader);
                Logger.Warn(logSrc, $"Emitter[{i}].Texture = \"{e.Texture}\"");

                e.SpawnDelay = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].SpawnDelay = {e.SpawnDelay}");
                e.SpawnRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].SpawnRandomize = {e.SpawnRandomize}");

                e.Velocity = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].Velocity = {e.Velocity}");
                e.VelocityRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].VelocityRandomize = {e.VelocityRandomize}");

                e.Acceleration = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].Acceleration = {e.Acceleration}");

                e.Decay = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].Decay = {e.Decay}");
                e.DecayRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].DecayRandomize = {e.DecayRandomize}");

                e.ParticleRadius = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].ParticleRadius = {e.ParticleRadius}");
                e.ParticleRadiusRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].ParticleRadiusRandomize = {e.ParticleRadiusRandomize}");

                e.GrowthRate = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].GrowthRate = {e.GrowthRate}");
                e.GravityMultiplier = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].GravityMultiplier = {e.GravityMultiplier}");
                e.RandomDirection = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].RandomDirection = {e.RandomDirection}");

                // Colors (RGBA)
                e.ParticleColor = new Vector4(
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f
                );
                Logger.Warn(logSrc, $"Emitter[{i}].ParticleColor = {e.ParticleColor}");
                e.FadeToColor = new Vector4(
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f
                );
                Logger.Warn(logSrc, $"Emitter[{i}].FadeToColor = {e.FadeToColor}");

                // Flags
                e.EmitterFlags = reader.ReadUInt32();
                Logger.Warn(logSrc, $"Emitter[{i}].EmitterFlags = 0x{e.EmitterFlags:X8}");
                e.ParticleFlags = reader.ReadUInt16();
                Logger.Warn(logSrc, $"Emitter[{i}].ParticleFlags = 0x{e.ParticleFlags:X4}");

                // Four 4-bit values packed into a UInt16
                ushort nibblePack = reader.ReadUInt16();
                e.Stickiness = (byte)(nibblePack >> 12 & 0xF);
                e.Bounciness = (byte)(nibblePack >> 8 & 0xF);
                e.PushEffect = (byte)(nibblePack >> 4 & 0xF);
                e.Swirliness = (byte)(nibblePack & 0xF);
                Logger.Warn(logSrc, $"Emitter[{i}].Stickiness={e.Stickiness}, Bounciness={e.Bounciness}, PushEffect={e.PushEffect}, Swirliness={e.Swirliness}");

                e.InitiallyOn = reader.ReadByte() != 0;
                Logger.Warn(logSrc, $"Emitter[{i}].InitiallyOn = {e.InitiallyOn}");
                e.TimeOn = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].TimeOn = {e.TimeOn}");
                e.TimeOnRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].TimeOnRandomize = {e.TimeOnRandomize}");

                e.TimeOff = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].TimeOff = {e.TimeOff}");
                e.TimeOffRandomize = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].TimeOffRandomize = {e.TimeOffRandomize}");

                e.ActiveDistance = reader.ReadSingle();
                Logger.Warn(logSrc, $"Emitter[{i}].ActiveDistance = {e.ActiveDistance}");

                emitters.Add(e);
            }

            // Skip any padding or unknown data
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            return emitters;
        }
    }

    public static class RflLevelInfoParser
    {
        private const string logSrc = "RflLevelInfoParser";

        public static void ParseLevelInfoFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int unknown = reader.ReadInt32();
            Logger.Warn(logSrc, $"Unk value: {unknown}");

            string levelName = ReadVString(reader);
            Logger.Warn(logSrc, $"Level name: \"{levelName}\"");

            string author = ReadVString(reader);
            Logger.Warn(logSrc, $"Level author: \"{author}\"");

            string date = ReadVString(reader);
            Logger.Warn(logSrc, $"Save date/time: \"{date}\"");

            byte hasMovers = reader.ReadByte();
            Logger.Warn(logSrc, $"Has movers: {hasMovers}");

            byte multiplayer = reader.ReadByte();
            Logger.Warn(logSrc, $"Multiplayer level: {multiplayer}");

            // Editor view layouts
            for (int i = 0; i < 4; i++)
            {
                long cfgStart = reader.BaseStream.Position;
                int viewType = reader.ReadInt32();
                Logger.Debug(logSrc, $"editor_view_config[{i}].view_type = {viewType}");

                if (viewType == (int)EditorViewType.FreeLook)
                {
                    // pos_3d (vec3)
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    Logger.Debug(logSrc, $"editor_view_config[{i}].pos_3d = ({x}, {y}, {z})");
                }
                else
                {
                    // pos_2d (4 × f4)
                    float[] pos2d = new float[4];
                    for (int j = 0; j < 4; j++)
                        pos2d[j] = reader.ReadSingle();
                    Logger.Debug(logSrc, $"editor_view_config[{i}].pos_2d = [{string.Join(", ", pos2d)}]");
                }

                // rot (mat3)
                float[,] m = new float[3, 3];
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                        m[row, col] = reader.ReadSingle();
                Logger.Debug(logSrc,
                    $"editor_view_config[{i}].rot = [\n" +
                    $"  [{m[0, 0]}, {m[0, 1]}, {m[0, 2]}],\n" +
                    $"  [{m[1, 0]}, {m[1, 1]}, {m[1, 2]}],\n" +
                    $"  [{m[2, 0]}, {m[2, 1]}, {m[2, 2]}]\n" +
                    $"]");

                long cfgSize = reader.BaseStream.Position - cfgStart;
                Logger.Debug(logSrc, $"editor_view_config[{i}] size = {cfgSize} bytes");
            }

            // prints garbage data in this section at the end of RF2 sections, but not RF1
            /*int extraIndex = 0;
            while (reader.BaseStream.Position < sectionEnd)
            {
                var b = reader.ReadByte();
                Logger.Warn(logSrc, $"level_info extra byte[{extraIndex}] = 0x{b:X2}");
                extraIndex++;
            }*/
        }
    }

    public static class RflClimbingRegionParser
    {
        private const string logSrc = "RflClimbingRegionParser";

        public static List<ClimbingRegion> ParseClimbingRegionsFromRfl(BinaryReader reader, long sectionEnd)
        {
            var regions = new List<ClimbingRegion>();

            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} climbing regions…");

            for (int i = 0; i < count; i++)
            {
                var cr = new ClimbingRegion();

                cr.UID = reader.ReadInt32();
                cr.ClassName = ReadVString(reader);
                cr.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                // mat3 comes in forward, right, up order → embed into Matrix4x4
                var f = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var r = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var u = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                cr.Rotation = new Matrix4x4(
                    r.X, r.Y, r.Z, 0f,
                    u.X, u.Y, u.Z, 0f,
                    f.X, f.Y, f.Z, 0f,
                    0f, 0f, 0f, 1f);

                cr.ScriptName = ReadVString(reader);
                cr.HiddenInEditor = reader.ReadByte() != 0;
                cr.Type = reader.ReadInt32();
                cr.Extents = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                Logger.Dev(logSrc,
                    $"ClimbingRegion[{i}] UID={cr.UID}, Class=\"{cr.ClassName}\", Pos={cr.Position}, " +
                    $"Rot=\n{cr.Rotation}, Script=\"{cr.ScriptName}\", Hidden={cr.HiddenInEditor}, " +
                    $"Type={cr.Type}, Extents={cr.Extents}");

                regions.Add(cr);
            }

            // ensure we position at the end no matter what
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            return regions;
        }
    }

    public static class RflItemParser
    {
        private const string logSrc = "RflItemParser";

        public static List<RflItem> ParseItemsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            var items = new List<RflItem>();
            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} items…");

            for (int i = 0; i < count; i++)
            {
                var it = new RflItem();

                it.UID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Item[{i}] UID = {it.UID}");

                it.ClassName = ReadVString(reader);
                Logger.Dev(logSrc, $"Item[{i}] ClassName = \"{it.ClassName}\"");

                it.Position = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                );
                Logger.Dev(logSrc, $"Item[{i}] Position = {it.Position}");

                // 3×3 rotation matrix → Matrix4x4
                var m00 = reader.ReadSingle(); var m01 = reader.ReadSingle(); var m02 = reader.ReadSingle();
                var m10 = reader.ReadSingle(); var m11 = reader.ReadSingle(); var m12 = reader.ReadSingle();
                var m20 = reader.ReadSingle(); var m21 = reader.ReadSingle(); var m22 = reader.ReadSingle();
                it.Rotation = new Matrix4x4(
                    m00, m01, m02, 0,
                    m10, m11, m12, 0,
                    m20, m21, m22, 0,
                    0, 0, 0, 1
                );
                Logger.Dev(logSrc, $"Item[{i}] Rotation = {it.Rotation}");

                it.ScriptName = ReadVString(reader);
                Logger.Dev(logSrc, $"Item[{i}] ScriptName = \"{it.ScriptName}\"");

                it.HiddenInEditor = reader.ReadByte() != 0;
                Logger.Dev(logSrc, $"Item[{i}] HiddenInEditor = {it.HiddenInEditor}");

                it.Count = reader.ReadInt32();
                Logger.Dev(logSrc, $"Item[{i}] Count = {it.Count}");

                it.RespawnTime = reader.ReadInt32();
                Logger.Dev(logSrc, $"Item[{i}] RespawnTime = {it.RespawnTime}");

                it.TeamID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Item[{i}] TeamID = {it.TeamID}");

                // unknown bytes, found only in RF2 rfls
                // 255, 255, 255, 255, 0, 0 in every instance I have found
                if (rfl_version == 0x127)
                {
                    var unk1 = reader.ReadByte();
                    var unk2 = reader.ReadByte();
                    var unk3 = reader.ReadByte();
                    var unk4 = reader.ReadByte();
                    var unk5 = reader.ReadByte();
                    var unk6 = reader.ReadByte();
                    Logger.Dev(logSrc, $"unk RF2-specific bytes {unk1}, {unk2}, {unk3}, {unk4}, {unk5}, {unk6}");
                }

                items.Add(it);
            }

            return items;
        }
    }
    public static class RflTriggerParser
    {
        private const string logSrc = "RflTriggerParser";

        public static List<Trigger> ParseTriggersFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            var list = new List<Trigger>();
            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} triggers…");

            for (int i = 0; i < count; i++)
            {
                var t = new Trigger();

                t.UID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] UID = {t.UID}");

                t.ScriptName = ReadVString(reader);
                Logger.Dev(logSrc, $"Trigger[{i}] ScriptName = \"{t.ScriptName}\"");

                t.HiddenInEditor = reader.ReadByte() != 0;
                Logger.Dev(logSrc, $"Trigger[{i}] HiddenInEditor = {t.HiddenInEditor}");

                t.Shape = (TriggerShape)reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] Shape = {t.Shape}");

                t.ResetsAfter = reader.ReadSingle();
                Logger.Dev(logSrc, $"Trigger[{i}] ResetsAfter = {t.ResetsAfter:F2}");

                t.ResetsTimes = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] ResetsTimes = {t.ResetsTimes}");

                t.UseKeyRequired = reader.ReadByte() != 0;
                Logger.Dev(logSrc, $"Trigger[{i}] UseKeyRequired = {t.UseKeyRequired}");

                if (RflUtils.IsRF1(rfl_version))
                {
                    t.KeyName = ReadVString(reader);
                }
                else
                {
                    t.KeyName = ReadVStringPlain(reader); // rf2 uses a plain vstring for key name
                }
                Logger.Dev(logSrc, $"Trigger[{i}] KeyName = \"{t.KeyName}\"");

                if (RflUtils.IsRF1(rfl_version))
                {
                    t.WeaponActivates = reader.ReadByte() != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] WeaponActivates = {t.WeaponActivates}");
                }

                t.ActivatedBy = (TriggerActivatedBy)reader.ReadByte();
                Logger.Dev(logSrc, $"Trigger[{i}] ActivatedBy = {t.ActivatedBy}");

                if (RflUtils.IsRF1(rfl_version))
                {
                    t.IsNpc = reader.ReadByte() != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] IsNpc = {t.IsNpc}");

                    t.IsAuto = reader.ReadByte() != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] IsAuto = {t.IsAuto}");

                    t.InVehicle = reader.ReadByte() != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] InVehicle = {t.InVehicle}");
                }

                // position
                float px = reader.ReadSingle();
                float py = reader.ReadSingle();
                float pz = reader.ReadSingle();
                t.Position = new Vector3(px, py, pz);
                Logger.Dev(logSrc, $"Trigger[{i}] Position = ({px:F2}, {py:F2}, {pz:F2})");

                if (t.Shape == TriggerShape.Sphere)
                {
                    t.SphereRadius = reader.ReadSingle();
                    Logger.Dev(logSrc, $"Trigger[{i}] SphereRadius = {t.SphereRadius:F2}");
                }
                else
                {
                    // rotation
                    float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                    float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                    float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                    t.Rotation = new Matrix4x4(
                        m00, m01, m02, 0,
                        m10, m11, m12, 0,
                        m20, m21, m22, 0,
                        0, 0, 0, 1
                    );
                    Logger.Dev(logSrc, $"Trigger[{i}] Rotation = [ [{m00:F2},{m01:F2},{m02:F2}], [{m10:F2},{m11:F2},{m12:F2}], [{m20:F2},{m21:F2},{m22:F2}] ]");

                    t.BoxHeight = reader.ReadSingle();
                    t.BoxWidth = reader.ReadSingle();
                    t.BoxDepth = reader.ReadSingle();
                    Logger.Dev(logSrc, $"Trigger[{i}] BoxExtents = H:{t.BoxHeight:F2}, W:{t.BoxWidth:F2}, D:{t.BoxDepth:F2}");

                    if (RflUtils.IsRF1(rfl_version))
                    {
                        t.OneWay = reader.ReadByte() != 0;
                        Logger.Dev(logSrc, $"Trigger[{i}] OneWay = {t.OneWay}");
                    }
                }

                t.AirlockRoomUID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] AirlockRoomUID = {t.AirlockRoomUID}");

                t.AttachedToUID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] AttachedToUID = {t.AttachedToUID}");

                t.UseClutterUID = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] UseClutterUID = {t.UseClutterUID}");

                t.Disabled = reader.ReadByte() != 0;
                Logger.Dev(logSrc, $"Trigger[{i}] Disabled = {t.Disabled}");

                t.ButtonActiveTime = reader.ReadSingle();
                Logger.Dev(logSrc, $"Trigger[{i}] ButtonActiveTime = {t.ButtonActiveTime:F2}");

                if (RflUtils.IsRF1(rfl_version))
                {
                    t.InsideTime = reader.ReadSingle();
                    Logger.Dev(logSrc, $"Trigger[{i}] InsideTime = {t.InsideTime:F2}");
                }
                else
                {
                    reader.ReadSingle();
                    // This value is super high in RF2, so isn't InsideTime. May not even be a float. Reading it is needed for alignment
                }

                // RF2 omits a number of fields that are in RF1 triggers and appears to consolidate them into flags and flags2
                // Many of these are unknown. Some are likely flags present in RF1 and some new in RF2
                if (RflUtils.IsRF2(rfl_version))
                {
                    reader.ReadByte();
                    ushort flags = reader.ReadUInt16();
                    Logger.Dev(logSrc, $"Trigger[{i}] with Script Name {t.ScriptName} Flags = 0x{flags:X4}");
                }

                if (rfl_version >= 0xB1 && !(RflUtils.IsRF2(rfl_version)))
                {
                    t.Team = (TriggerTeam)reader.ReadInt32();
                    Logger.Dev(logSrc, $"Trigger[{i}] Team = {t.Team}");
                }
                else if (RflUtils.IsRF2(rfl_version))
                {
                    uint flags2 = reader.ReadUInt32();
                    //t.StartActive = (flags & 0x1) != 0;
                    //t.OneShot = (flags & 0x2) != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] with Script Name {t.ScriptName} Flags2 = 0x{flags2:X8}");

                    // bit 0x0008 = IsAuto in RF2
                    t.IsAuto = (flags2 & 0x0008) != 0;
                    Logger.Dev(logSrc, $"Trigger[{i}] with Script Name {t.ScriptName} IsAuto = {t.IsAuto}");

                    // unknown flags in RF2 triggers
                    var F2unk1 = (flags2 & 0x0001) != 0;
                    var F2unk2 = (flags2 & 0x0002) != 0;
                    var F2unk3 = (flags2 & 0x0010) != 0;
                    var F2unk4 = (flags2 & 0x0020) != 0;
                    var F2unk5 = (flags2 & 0x0040) != 0;
                    var F2unk6 = (flags2 & 0x0080) != 0;
                    var F2unk7 = (flags2 & 0x0100) != 0;

                    Logger.Dev(logSrc, $"Trigger[{i}] with Script Name {t.ScriptName} unk1 {F2unk1}, unk2 {F2unk2}, unk3 {F2unk3}, unk4 {F2unk4}, unk5 {F2unk5}, unk6 {F2unk6}, unk7 {F2unk7}");
                }

                int numLinks = reader.ReadInt32();
                Logger.Dev(logSrc, $"Trigger[{i}] LinksCount = {numLinks}");
                t.Links = new List<int>(numLinks);
                for (int j = 0; j < numLinks; j++)
                {
                    int link = reader.ReadInt32();
                    t.Links.Add(link);
                    Logger.Dev(logSrc, $"Trigger[{i}] Link[{j}] = {link}");
                }

                list.Add(t);
            }

            return list;
        }
    }

    public static class RflPushRegionParser
    {
        private const string logSrc = "RflPushRegionParser";

        public static List<PushRegion> ParsePushRegionsFromRfl(BinaryReader reader, long sectionEnd)
        {
            var regions = new List<PushRegion>();
            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} push regions…");

            for (int i = 0; i < count; i++)
            {
                var pr = new PushRegion();

                pr.UID = reader.ReadInt32();
                pr.ClassName = ReadVString(reader);
                pr.Position = new Vector3(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()
                );

                // rotation matrix
                float r00 = reader.ReadSingle(), r01 = reader.ReadSingle(), r02 = reader.ReadSingle();
                float r10 = reader.ReadSingle(), r11 = reader.ReadSingle(), r12 = reader.ReadSingle();
                float r20 = reader.ReadSingle(), r21 = reader.ReadSingle(), r22 = reader.ReadSingle();
                pr.Rotation = new Matrix4x4(
                    r00, r01, r02, 0,
                    r10, r11, r12, 0,
                    r20, r21, r22, 0,
                    0, 0, 0, 1
                );

                pr.ScriptName = ReadVString(reader);
                pr.HiddenInEditor = reader.ReadByte() != 0;

                pr.Shape = (PushRegionShape)reader.ReadInt32();
                if (pr.Shape == PushRegionShape.Sphere)
                {
                    pr.Radius = reader.ReadSingle();
                }
                else
                {
                    // extents = Vector3
                    pr.Extents = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );
                }

                pr.Strength = reader.ReadSingle();

                // flags bitfield
                uint rawFlags = reader.ReadUInt16(); // 16 bits
                pr.JumpPad = (rawFlags & 0x40) != 0;
                pr.DoesntAffectPlayer = (rawFlags & 0x20) != 0;
                pr.Radial = (rawFlags & 0x10) != 0;
                pr.GrowsTowardsBoundary = (rawFlags & 0x08) != 0;
                pr.GrowsTowardsCenter = (rawFlags & 0x04) != 0;
                pr.Grounded = (rawFlags & 0x02) != 0;
                pr.MassIndependent = (rawFlags & 0x01) != 0;

                pr.Turbulence = reader.ReadUInt16();

                // log out everything
                Logger.Debug(logSrc,
                    $"PushRegion[{i}] UID={pr.UID}, Class=\"{pr.ClassName}\", Pos={pr.Position}, " +
                    $"Script=\"{pr.ScriptName}\", Hidden={pr.HiddenInEditor}, Shape={pr.Shape}, " +
                    (pr.Shape == PushRegionShape.Sphere
                        ? $"Radius={pr.Radius}"
                        : $"Extents={pr.Extents}"
                    ) +
                    $", Strength={pr.Strength}, Flags=0x{rawFlags:X4} (JumpPad={pr.JumpPad}, " +
                    $"NoPlayer={pr.DoesntAffectPlayer}, Radial={pr.Radial}, " +
                    $"BoundaryGrow={pr.GrowsTowardsBoundary}, CenterGrow={pr.GrowsTowardsCenter}, " +
                    $"Grounded={pr.Grounded}, MassIndep={pr.MassIndependent}), Turbulence={pr.Turbulence}"
                );

                regions.Add(pr);
            }

            return regions;
        }
    }

    public static class RflEventParser
    {
        private const string logSrc = "RflEventParser";

        public static List<RflEvent> ParseEventsRF2(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            int count = reader.ReadInt32();
            Logger.Warn(logSrc, $"RF2 event count = {count}");

            var events = new List<RflEvent>(count);

            for (int i = 0; i < count; i++)
            {
                long startPos = reader.BaseStream.Position;
                var ev = new RflEvent();

                // UID
                ev.UID = reader.ReadInt32();
                Logger.Warn(logSrc, $"Event[{i}] UID             = {ev.UID}");

                // class name
                ev.ClassName = ReadVString(reader);
                Logger.Warn(logSrc, $"Event[{i}] ClassName       = \"{ev.ClassName}\"");

                // position
                float px = reader.ReadSingle();
                float py = reader.ReadSingle();
                float pz = reader.ReadSingle();
                ev.Position = new Vector3(px, py, pz);
                Logger.Warn(logSrc, $"Event[{i}] Position = ({px:F2}, {py:F2}, {pz:F2})");

                // script name
                ev.ScriptName = ReadVString(reader);
                Logger.Warn(logSrc, $"Event[{i}] ScriptName      = \"{ev.ScriptName}\"");

                // hidden in editor
                ev.HiddenInEditor = reader.ReadByte() != 0;
                Logger.Warn(logSrc, $"Event[{i}] HiddenInEditor = {ev.HiddenInEditor}");

                // delay
                ev.Delay = reader.ReadSingle();
                Logger.Warn(logSrc, $"Event[{i}] Delay           = {ev.Delay}");

                // 8 4-byte fields:
                var unk0 = reader.ReadInt32(); // -1, 0, or 1 everywhere that I've seen, unsure of significance
                var unk1 = reader.ReadInt32(); // typically 0, unsure of significance
                var mb1 = reader.ReadByte() != 0; // top candidate for bool1
                var mb2 = reader.ReadByte() != 0; // top candidate for bool2
                var unk2 = reader.ReadInt32(); // confident this is int1
                var unk3 = reader.ReadInt32(); // confident this is int2
                var unk4 = reader.ReadInt32(); // int maybe? was -1 on a Delay
                var unk5 = reader.ReadInt32(); // int maybe? was -1 on a Delay
                var unk6 = reader.ReadSingle(); // confident this is float1
                var unk7 = reader.ReadSingle(); // top candidate for float2
                var unk8 = reader.ReadInt32(); // 00 00 00 00 everywhere that I've seen

                // str1
                ev.Str1 = ReadVString(reader); // confident of this
                Logger.Warn(logSrc, $"Event[{i}] Str1             = \"{ev.Str1}\"");

                // str2
                ev.Str2 = ReadVString(reader); // confident of this
                Logger.Warn(logSrc, $"Event[{i}] Str2             = \"{ev.Str2}\"");

                //reader.ReadByte();
                //reader.ReadByte();
                var unk9 = ReadVString(reader); // maybe a third str? 00 00 everywhere that I've seen

                ev.Bool1 = mb1;
                ev.Bool2 = mb2;
                ev.Int1 = unk2;
                ev.Int2 = unk3;
                ev.Float1 = unk6;
                ev.Float2 = unk7;

                Logger.Warn(logSrc, $"Event[{i}] Bool1            = {ev.Bool1}");
                Logger.Warn(logSrc, $"Event[{i}] Bool2            = {ev.Bool2}");
                Logger.Warn(logSrc, $"Event[{i}] Int1             = {ev.Int1}");
                Logger.Warn(logSrc, $"Event[{i}] Int2             = {ev.Int2}");
                Logger.Warn(logSrc, $"Event[{i}] Float1           = {ev.Float1}");
                Logger.Warn(logSrc, $"Event[{i}] Float2           = {ev.Float2}");

                Logger.Warn(logSrc, $"Event[{i}] unk0 {unk0}, unk1 {unk1}, mb1 {mb1}, mb2 {mb2}, unk2 {unk2}, unk3 {unk3}, unk4 {unk4}, unk5 {unk5}, unk6 {unk6}, unk7 {unk7}, unk8 {unk8}, unk9 {unk9}");

                // Links
                int numLinks = reader.ReadInt32();
                Logger.Warn(logSrc, $"Event[{i}] Links count     = {numLinks}");

                ev.Links = new List<int>(numLinks);
                for (int j = 0; j < numLinks; j++)
                {
                    int link = reader.ReadInt32();
                    ev.Links.Add(link);
                    Logger.Warn(logSrc, $"Event[{i}] Link[{j}]        = {link}");
                }

                if (ev.ClassName == "Play_Explosion" || ev.ClassName == "Teleport")
                {
                    ev.HasRotation = true; // used by exporter
                    float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                    float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                    float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                    ev.Rotation = new Matrix4x4(
                        m00, m01, m02, 0,
                        m10, m11, m12, 0,
                        m20, m21, m22, 0,
                          0, 0, 0, 1
                    );
                }

                // color in editor
                ev.RawColor = reader.ReadUInt32();
                Logger.Warn(logSrc, $"Event[{i}] RawColor        = 0x{ev.RawColor:X8}");

                // add to events list
                events.Add(ev);

                // sanity check
                long consumed = reader.BaseStream.Position - startPos;
                Logger.Warn(logSrc, $"Event[{i}] bytes consumed  = {consumed}");
            }

            // fast-forward to end of section
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            return events;
        }

        public static List<RflEvent> ParseEvents(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            if (rfl_version == 0x127) // 295
            {
                Logger.Warn(logSrc, "RFL version 0x127 (295) is not yet supported for event parsing. Skipping events.");

                int count2 = reader.ReadInt32();
                Logger.Warn(logSrc, $"Reading {count2} events…");

                for (int i = 0; i < count2; i++)
                {
                    int extraIndex = 0;
                    while (reader.BaseStream.Position < sectionEnd)
                    {
                        var b = reader.ReadByte();
                        //Logger.Warn(logSrc, $"event byte[{extraIndex}] = 0x{b:X2}");
                        extraIndex++;
                    }
                }

                return new List<RflEvent>();
            }

            var events = new List<RflEvent>();
            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} events…");

            for (int i = 0; i < count; i++)
            {
                var ev = new RflEvent();

                ev.UID = reader.ReadInt32();
                ev.ClassName = ReadVString(reader);
                ev.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                ev.ScriptName = ReadVString(reader);
                ev.HiddenInEditor = reader.ReadByte() != 0;
                ev.Delay = reader.ReadSingle();
                ev.Bool1 = reader.ReadByte() != 0;
                ev.Bool2 = reader.ReadByte() != 0;
                ev.Int1 = reader.ReadInt32();
                ev.Int2 = reader.ReadInt32();
                ev.Float1 = reader.ReadSingle();
                ev.Float2 = reader.ReadSingle();
                ev.Str1 = ReadVString(reader);
                ev.Str2 = ReadVString(reader);

                // links (uid_list)             
                int numLinks = reader.ReadInt32();
                if (numLinks < 0 || reader.BaseStream.Position + numLinks * 4L > sectionEnd)
                {
                    Logger.Warn(logSrc, $"Suspicious link count {numLinks} in event[{i}] – clamping to 0");
                    numLinks = 0;
                }
                ev.Links = new List<int>(numLinks);
                for (int j = 0; j < numLinks; j++)
                    ev.Links.Add(reader.ReadInt32());

                if (rfl_version >= 0x91 && (ev.ClassName == "Teleport" || ev.ClassName == "Play_Vclip" || ev.ClassName == "Teleport_Player")
                || rfl_version >= 0x98 && ev.ClassName == "Alarm"
                || rfl_version >= 0x12C && (ev.ClassName == "AF_Teleport_Player" || ev.ClassName == "Clone_Entity")
                || rfl_version >= 0x12D && ev.ClassName == "Anchor_Marker_Orient")
                {
                    ev.HasRotation = true; // used by exporter
                    float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                    float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                    float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                    ev.Rotation = new Matrix4x4(
                        m00, m01, m02, 0,
                        m10, m11, m12, 0,
                        m20, m21, m22, 0,
                          0, 0, 0, 1
                    );
                }

                if (rfl_version >= 0xB0)
                {
                    //reader.BaseStream.Seek(4, SeekOrigin.Current); // color
                    ev.RawColor = reader.ReadUInt32();
                    Logger.Dev(logSrc, $"Event[{i}] RawColor = 0x{ev.RawColor:X8}");
                }

                Logger.Dev(logSrc,
                    $"Event[{i}] UID={ev.UID}, Class=\"{ev.ClassName}\", Pos={ev.Position}, " +
                    $"Script=\"{ev.ScriptName}\", Hidden={ev.HiddenInEditor}, Delay={ev.Delay}, " +
                    $"Bool1={ev.Bool1}, Bool2={ev.Bool2}, Int1={ev.Int1}, Int2={ev.Int2}, " +
                    $"Float1={ev.Float1}, Float2={ev.Float2}, Str1=\"{ev.Str1}\", Str2=\"{ev.Str2}\", " +
                    $"Links=[{string.Join(", ", ev.Links)}]"
                );

                if (ev != null)
                    events.Add(ev);
            }

            // skip to end of this section
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            return events;
        }
    }

    public static class RflMpRespawnPointParser
    {
        private const string logSrc = "RflMpRespawnPointParser";

        public static List<MpRespawnPoint> ParseMpRespawnPoints(BinaryReader reader, long sectionEnd)
        {
            var list = new List<MpRespawnPoint>();
            int count = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {count} MP respawn points…");

            for (int i = 0; i < count; i++)
            {
                var pt = new MpRespawnPoint();
                pt.UID = reader.ReadInt32();
                pt.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                // 3×3 rotation matrix → embed into Matrix4x4
                float m00 = reader.ReadSingle(), m01 = reader.ReadSingle(), m02 = reader.ReadSingle();
                float m10 = reader.ReadSingle(), m11 = reader.ReadSingle(), m12 = reader.ReadSingle();
                float m20 = reader.ReadSingle(), m21 = reader.ReadSingle(), m22 = reader.ReadSingle();
                pt.Rotation = new Matrix4x4(
                    m00, m01, m02, 0,
                    m10, m11, m12, 0,
                    m20, m21, m22, 0,
                    0, 0, 0, 1
                );

                pt.ScriptName = ReadVString(reader);
                pt.HiddenInEditor = reader.ReadByte() != 0;
                pt.TeamID = reader.ReadInt32();
                pt.RedTeam = reader.ReadByte() != 0;
                pt.BlueTeam = reader.ReadByte() != 0;
                pt.IsBot = reader.ReadByte() != 0;

                // print everything
                Logger.Dev(logSrc,
                    $"MP Respawn[{i}] UID={pt.UID}, Pos={pt.Position}, Team={pt.TeamID} (R?{pt.RedTeam}/B?{pt.BlueTeam}), " +
                    $"Bot={pt.IsBot}, Hidden={pt.HiddenInEditor}, Script=\"{pt.ScriptName}\""
                );

                list.Add(pt);
            }

            // ensure we skip to end of section
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
            return list;
        }
    }


    public class LevelProperties
    {
        public Vector4 AmbientColor { get; set; }
        public float LightmapMultiplier { get; set; } = 1.0f;
        public byte FogR { get; set; }
        public byte FogG { get; set; }
        public byte FogB { get; set; }
        public float FogNear { get; set; }
        public float FogFar { get; set; }
    }

    public static class RflLevelPropertiesParser
    {
        private const string logSrc = "RflLevelPropertiesParser";

        public static LevelProperties ParseLevelPropertiesFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            // geomod_texture
            string geomodTexture = ReadVString(reader);

            // hardness
            int hardness = reader.ReadInt32();

            // ambient_color (RGBA)
            byte ambR = reader.ReadByte();
            byte ambG = reader.ReadByte();
            byte ambB = reader.ReadByte();
            byte ambA = reader.ReadByte();

            // directional_ambient_light
            byte dirAmbient = reader.ReadByte();

            // fog_color
            byte fogR = reader.ReadByte();
            byte fogG = reader.ReadByte();
            byte fogB = reader.ReadByte();
            byte fogA = reader.ReadByte();

            // fog near/far planes
            float fogNear = reader.ReadSingle();
            float fogFar = reader.ReadSingle();
            Logger.Warn(logSrc, "Level properties:");
            Logger.Warn(logSrc, $"  GeoMod texture: \"{geomodTexture}\"");
            Logger.Warn(logSrc, $"  GeoMod hardness: {hardness}");
            Logger.Warn(logSrc, $"  Ambient light colour: R={ambR} G={ambG} B={ambB} A={ambA}");
            Logger.Warn(logSrc, $"  Directional ambient light: {(dirAmbient != 0 ? "yes" : "no")}");
            Logger.Warn(logSrc, $"  Distance fog colour: R={fogR} G={fogG} B={fogB} A={fogA}");
            Logger.Warn(logSrc, $"  Distance fog near clip plane: {fogNear}");
            Logger.Warn(logSrc, $"  Distance fog far clip plane:  {fogFar}");

            float lightmapMultiplier = 1.0f;

            if (rfl_version == 0x127) // rf2
            {
                // Verified against rf2.exe FUN_0048a370:
                // Field order: RGBA, float, float, float(v>0x10F), float(v>0x101), RGBA+int32(v>0x10D), float(v>0x11E)
                // For RF2 v295, all version-conditional fields are present.
                // rf2.exe discards most of these at runtime (the baked vertex colors already include their effects),
                // but RED2 uses them during lightmap baking. Field names inferred from data analysis across 101 RF2 RFLs.

                // RED2 baking sun/directional light (not used by rf2.exe at runtime)
                byte sunR = reader.ReadByte(), sunG = reader.ReadByte(), sunB = reader.ReadByte(), sunA = reader.ReadByte();
                float sunYaw = reader.ReadSingle();
                float sunPitch = reader.ReadSingle();
                float sunIntensity = reader.ReadSingle(); // 0.0-1.0
                float sunSpreadAngle = reader.ReadSingle(); // nearly always 89.9

                // Hardlight overlay (fullscreen subtractive color quad) - the ONLY RF2-specific field used by rf2.exe
                // Rendered via D3DBLENDOP_REVSUBTRACT in FUN_00431a50: output = framebuffer - (RGB * intensity/128)
                byte hardlightR = reader.ReadByte(), hardlightG = reader.ReadByte(), hardlightB = reader.ReadByte(), hardlightA = reader.ReadByte();
                int hardlightIntensity = reader.ReadInt32();

                // RED2 lightmap brightness multiplier - scales all baked lighting (0.4-2.5 range, 1.0 = default)
                lightmapMultiplier = reader.ReadSingle();

                Logger.Info(logSrc, "RF2 level properties (RED2 baking parameters):");
                Logger.Info(logSrc, $"  Sun light: RGB=({sunR},{sunG},{sunB}) yaw={sunYaw} pitch={sunPitch} intensity={sunIntensity} spread={sunSpreadAngle}");
                Logger.Info(logSrc, $"  Hardlight overlay: RGB=({hardlightR},{hardlightG},{hardlightB}) intensity={hardlightIntensity} (subtractive)");
                Logger.Info(logSrc, $"  Lightmap multiplier: {lightmapMultiplier}");
            }

            // read any remaining data until sectionEnd
            while (reader.BaseStream.Position < sectionEnd)
            {
                reader.ReadSingle();
            }

            return new LevelProperties
            {
                AmbientColor = new Vector4(ambR, ambG, ambB, ambA),
                LightmapMultiplier = lightmapMultiplier,
                FogR = fogR,
                FogG = fogG,
                FogB = fogB,
                FogNear = fogNear,
                FogFar = fogFar
            };
        }
    }


    static class RflLightParser
    {
        private const string logSrc = "RflLightParser";

        public static List<Light> ParseLightsFromRfl(BinaryReader reader, long sectionEnd, int rfl_version)
        {
            var lights = new List<Light>();
            int numLights = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {numLights} lights…");

            for (int i = 0; i < numLights; i++)
            {
                long lightStart = reader.BaseStream.Position;
                var light = new Light();

                light.UID = reader.ReadInt32();
                light.ClassName = ReadVString(reader);
                light.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                // 3×3 rotation matrix → embed into Matrix4x4
                var m00 = reader.ReadSingle(); var m01 = reader.ReadSingle(); var m02 = reader.ReadSingle();
                var m10 = reader.ReadSingle(); var m11 = reader.ReadSingle(); var m12 = reader.ReadSingle();
                var m20 = reader.ReadSingle(); var m21 = reader.ReadSingle(); var m22 = reader.ReadSingle();
                light.Rotation = new Matrix4x4(
                    m00, m01, m02, 0,
                    m10, m11, m12, 0,
                    m20, m21, m22, 0,
                    0, 0, 0, 1
                );

                light.ScriptName = ReadVString(reader);
                light.HiddenInEditor = reader.ReadByte() != 0;

                long rawFlagsOffset = reader.BaseStream.Position - lightStart;
                uint rawFlags = reader.ReadUInt32();
                light.Dynamic = (rawFlags & 0x1) != 0;
                light.Fade = (rawFlags & 0x2) != 0;
                light.ShadowCasting = (rawFlags & 0x4) != 0;
                light.IsEnabled = (rawFlags & 0x8) != 0;
                // Light type enum is the same for RF1 and RF2: 0=Undefined, 1=Point, 2=Spot(AABB), 3=Tube(OBB)
                // (confirmed via rf2.exe FUN_00485da0 point-in-light tests and FUN_00431a50 debug drawing)
                // The 0x300 chunk format is identical between RF1 and RF2.
                int typeBits = (int)((rawFlags >> 4) & 0x3);
                light.Type = (LightType)typeBits;
                light.InitialState = (LightState)(rawFlags >> 8 & 0xF);
                light.RuntimeShadow = (rawFlags & 0x2000) != 0;
                Logger.Dev(logSrc,
                    $"Light[{i}] flags=0x{rawFlags:X8} typeBits={(rawFlags >> 4) & 0x3} type={light.Type} " +
                    $"dyn={light.Dynamic} fade={light.Fade} shadow={light.ShadowCasting} enabled={light.IsEnabled} " +
                    $"init={(int)light.InitialState} runtimeShadow={light.RuntimeShadow}");

                // RGBA
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                byte a = reader.ReadByte();
                light.Color = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);

                light.Range = reader.ReadSingle();
                light.FOV = reader.ReadSingle();
                light.FOVDropoff = reader.ReadSingle();
                light.IntensityAtMaxRange = reader.ReadSingle();
                light.DropoffType = reader.ReadInt32();
                light.TubeLightWidth = reader.ReadSingle();

                if (rfl_version != 0x127) // 295
                {
                    light.OnIntensity = reader.ReadSingle();
                }
                else
                {
                    // RF2 uses inverse-square attenuation: 1/(1+(d/r)^2)
                    // RF1 uses linear attenuation: max(0, 1 - d/R)
                    // Range conversion: RF2's r is the half-intensity distance;
                    // multiplying by 3 extends RF1's linear cutoff to where RF2 drops to ~10%,
                    // giving the closest curve match (RMS-optimal k≈2-3, total energy match at k=3).
                    // With range×3, intensity scale ~1.0 closely tracks RF2's attenuation curve
                    // (vs old scale=3.0 which overcompensated near-field for zero far-field).
                    // lm_mult is applied separately (post-parse) for per-map intensity tuning.
                    // Configurable via -rf2lightscale (default 1.0).
                    float I0 = reader.ReadSingle();
                    light.OnIntensity = I0 * Config.RF2LightScale;
                    light.Range *= 3.0f; // inverse-square → linear range conversion

                    Logger.Dev(logSrc, $"Scaled RF2 light {light.UID}: raw_int={I0}, scale={Config.RF2LightScale}, result_int={light.OnIntensity}, range={light.Range/3:F1}→{light.Range:F1}");
                }
                light.OnTime = reader.ReadSingle();
                light.OnTimeVariation = reader.ReadSingle();
                light.OffIntensity = reader.ReadSingle();
                light.OffTime = reader.ReadSingle();
                light.OffTimeVariation = reader.ReadSingle();

                lights.Add(light);

                long lightEnd = reader.BaseStream.Position;
                Logger.Dev(logSrc,
                    $"Light[{i}] offset=0x{lightStart:X8}-0x{lightEnd:X8} " +
                    $"len={lightEnd - lightStart} bytes rfl=0x{rfl_version:X} " +
                    $"uid={light.UID} class={light.ClassName} script={light.ScriptName} hidden={light.HiddenInEditor} " +
                    $"pos={light.Position} rot=({m00},{m01},{m02};{m10},{m11},{m12};{m20},{m21},{m22}) " +
                    $"color={light.Color} range={light.Range} fov={light.FOV} fovDropoff={light.FOVDropoff} " +
                    $"intMaxRange={light.IntensityAtMaxRange} dropoff={light.DropoffType} tubeWidth={light.TubeLightWidth} " +
                    $"onIntensity={light.OnIntensity} onTime={light.OnTime} onVar={light.OnTimeVariation} " +
                    $"offIntensity={light.OffIntensity} offTime={light.OffTime} offVar={light.OffTimeVariation}");
                int lightLength = (int)(lightEnd - lightStart);
                int dumpLength = Math.Min(lightLength, 64);
                if (dumpLength > 0)
                {
                    var stream = reader.BaseStream;
                    long savedPos = stream.Position;
                    stream.Seek(lightStart, SeekOrigin.Begin);
                    byte[] rawBytes = reader.ReadBytes(dumpLength);
                    stream.Seek(savedPos, SeekOrigin.Begin);
                    string rawHex = BitConverter.ToString(rawBytes).Replace("-", " ");
                    Logger.Dev(logSrc,
                        $"Light[{i}] raw[0..{dumpLength - 1}] ({dumpLength} bytes of {lightLength}): {rawHex}");
                }

                // (we leave reader at end of this light record)
                Logger.Dev(logSrc,
                    $"Loaded Light[{i}] UID={light.UID}, class={light.ClassName}, pos={light.Position}, " +
                    $"flags=0x{rawFlags:X8}, color={light.Color}, range={light.Range}, int={light.OnIntensity}, intmr={light.IntensityAtMaxRange}"
                );
            }

            return lights;
        }

        // RF2 lights (chunk 0x0500) use a much simpler format than RF1.
        // Light properties (color, range, intensity, dropoff, etc.) are defined
        // externally by class name; the RFL only stores position and a few params.
        public static List<Light> ParseLightsFromRflRF2(BinaryReader reader, long sectionEnd)
        {
            var lights = new List<Light>();
            int numLights = reader.ReadInt32();
            Logger.Dev(logSrc, $"Reading {numLights} RF2 lights…");

            for (int i = 0; i < numLights; i++)
            {
                long lightStart = reader.BaseStream.Position;
                var light = new Light();

                light.UID = reader.ReadInt32();

                // RF2 light format: UID, position, bool, class name, 3 floats, 1 int
                float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                light.Position = new Vector3(px, py, pz);

                bool unkFlag = reader.ReadByte() != 0;

                light.ClassName = ReadVString(reader);

                float param1 = reader.ReadSingle();
                float param2 = reader.ReadSingle();
                float param3 = reader.ReadSingle();
                int param4 = reader.ReadInt32();

                // RF2 lights define most properties externally via class name.
                // The inline params are passed to the light class lookup system.
                // We store what we can and set reasonable defaults for the rest.
                light.Range = param1;
                light.OnIntensity = param2;
                light.IsEnabled = true;
                light.Type = LightType.Point;
                light.Color = new Vector4(1f, 1f, 1f, 1f);

                lights.Add(light);

                Logger.Dev(logSrc,
                    $"RF2 Light[{i}] UID={light.UID}, class={light.ClassName}, " +
                    $"pos=({px:F2},{py:F2},{pz:F2}), unkFlag={unkFlag}, " +
                    $"param1={param1}, param2={param2}, param3={param3}, param4={param4}");
            }

            return lights;
        }
    }

    public static class RflBrushParser
    {
        private const string logSrc = "RflBrushParser";
        public static void ParseBrushesFromRfl(BinaryReader reader, long sectionEnd, Mesh mesh, int rfl_version)
        {
            int numBrushes = reader.ReadInt32();
            for (int i = 0; i < numBrushes; i++)
            {
                Logger.Debug(logSrc, $"Reading brush {i}/{numBrushes}");
                Brush brush = RFGeometryParser.ReadBrush(reader, rfl_version);
                if (brush != null) // for safety, only add valid brushes
                    mesh.Brushes.Add(brush);
            }

            Logger.Info(logSrc, $"Parsed {numBrushes} brushes from RF1 RFL file.");

            // Ensure we're positioned correctly at the end of the section
            reader.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
        }
    }

}
