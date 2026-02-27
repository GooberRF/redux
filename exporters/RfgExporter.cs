using redux.utilities;
using static redux.utilities.MirrorUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace redux.exporters
{
    public static class RfgExporter
    {
        private const string logSrc = "RfgExporter";

        // Map RF "world axis" for positions/geometry so it matches rotation mirroring.
        // If you're seeing X behave like Z for positions, swap X<->Z here.
        private static Config.MirrorAxis AxisForPosGeo(Config.MirrorAxis a) =>
            a switch
            {
                Config.MirrorAxis.X => Config.MirrorAxis.Z,
                Config.MirrorAxis.Z => Config.MirrorAxis.X,
                _ => a
            };


        // Mirror a position about a plane at coordinate c on the chosen axis
        private static Vector3 MirrorPosAboutPivot(Vector3 v, Config.MirrorAxis axis, float c)
        {
            return axis switch
            {
                Config.MirrorAxis.X => new Vector3(2 * c - v.X, v.Y, v.Z),
                Config.MirrorAxis.Y => new Vector3(v.X, 2 * c - v.Y, v.Z),
                Config.MirrorAxis.Z => new Vector3(v.X, v.Y, 2 * c - v.Z),
                _ => v
            };
        }

        /// Mirror a local orientation across a world plane through the origin,
        /// using RF axis convention: row1=Right(X), row2=Up(Y), row3=Forward(Z).
        ///  - axis = X → mirror across the YZ plane (X → -X)
        ///  - axis = Y → mirror across the XZ plane (Y → -Y)
        ///  - axis = Z → mirror across the XY plane (Z → -Z)
        private static Matrix4x4 MirrorRotationAboutOrigin(Matrix4x4 R, Config.MirrorAxis axis)
        {
            if (axis == Config.MirrorAxis.None) return R;

            // Extract RF row-basis (Right, Up, Forward) in world coords
            Vector3 right = new(R.M11, R.M12, R.M13); // X
            Vector3 up = new(R.M21, R.M22, R.M23); // Y
            Vector3 forward = new(R.M31, R.M32, R.M33); // Z

            // World reflection A = diag(sx, sy, sz)
            float sx = 1f, sy = 1f, sz = 1f;
            switch (axis)
            {
                case Config.MirrorAxis.X: sx = -1f; break; // mirror about YZ plane
                case Config.MirrorAxis.Y: sy = -1f; break; // mirror about XZ plane
                case Config.MirrorAxis.Z: sz = -1f; break; // mirror about XY plane
            }

            // Apply A on the LEFT (scale world components of each row)
            right = new Vector3(sx * right.X, sy * right.Y, sz * right.Z);
            up = new Vector3(sx * up.X, sy * up.Y, sz * up.Z);
            forward = new Vector3(sx * forward.X, sy * forward.Y, sz * forward.Z);

            // Rebuild a right-handed orthonormal basis for RF (X=right, Y=up, Z=forward)
            // Start by normalizing the mirrored "forward" & "up" directions
            forward = Vector3.Normalize(forward);
            up = Vector3.Normalize(up);

            // Right-handed rule in RF:  right = up × forward
            Vector3 newRight = Vector3.Normalize(Vector3.Cross(up, forward));

            // Re-orthogonalize up to avoid drift: up = forward × right
            Vector3 newUp = Vector3.Normalize(Vector3.Cross(forward, newRight));

            // (Optional) tiny numerical guard to ensure right-handedness
            // if (Vector3.Dot(Vector3.Cross(newUp, forward), newRight) < 0f) newRight = -newRight;

            // Write rows back
            var outR = R;
            outR.M11 = newRight.X; outR.M12 = newRight.Y; outR.M13 = newRight.Z; // Right
            outR.M21 = newUp.X; outR.M22 = newUp.Y; outR.M23 = newUp.Z;    // Up
            outR.M31 = forward.X; outR.M32 = forward.Y; outR.M33 = forward.Z;  // Forward
            return outR;
        }

        public static void ExportRfg(Mesh mesh, string outputPath)
        {
            Logger.Info(logSrc, $"Writing RFG to {outputPath}");
            
            using var stream = File.Create(outputPath);
            using var writer = new BinaryWriter(stream);

            writer.Write(0xD43DD00D); // Magic
            writer.Write(0x0000012C); // Version
            //writer.Write(1); // Num groups

            int totalGroups = 1 + mesh.Groups.Count;
            writer.Write(totalGroups);

            //Utils.WriteVString(writer, "static_group");
            //writer.Write((byte)0); // is_moving = false

            var movingBrushUIDs = new HashSet<int>(
                mesh.Groups.SelectMany(g => g.Brushes)
            );
            var staticBrushes = mesh.Brushes
                .Where(b => !movingBrushUIDs.Contains(b.UID))
                .ToList();

            // write the “static_group” header
            Utils.WriteVString(writer, "static_group");
            writer.Write((byte)0); // is_moving = false

            // Fold coronas into clutter section if a clutter replacement name is specified (since RF1 doesn't have corona objects like RF2)
            HandleCoronaClutterReplacements(mesh);

            // write only the filtered brushes
            WriteBrushesSection(writer, staticBrushes);

            // Mirror settings for the static group
            bool mirrorActive = Config.GeoMirror != Config.MirrorAxis.None;
            var posAxis = AxisForPosGeo(Config.GeoMirror);
            // Mirror about world origin
            float staticGroupPivot = 0f;
            if (mirrorActive)
                Logger.Dev(logSrc, $"Static group mirroring about world origin on axis {Config.GeoMirror}");

            writer.Write(0); // 0 geo regions

            // Write lights section
            Logger.Dev(logSrc, $"Writing lights: count={mesh.Lights.Count}");
            writer.Write(mesh.Lights.Count);
            foreach (var light in mesh.Lights)
            {
                Logger.Dev(logSrc, $"  → light UID={light.UID}, range={light.Range}, intensity={light.OnIntensity}");

                // UID
                writer.Write(light.UID);
                // class name
                Utils.WriteVString(writer, light.ClassName);

                // position
                var lightPos = mirrorActive ? MirrorPosAboutPivot(light.Position, posAxis, 0f) : light.Position;
                writer.Write(lightPos.X); writer.Write(lightPos.Y); writer.Write(lightPos.Z);

                // 3×3 rotation matrix
                var R = mirrorActive ? MirrorRotationAboutOrigin(light.Rotation, Config.GeoMirror) : light.Rotation;
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
            }

            writer.Write(0); // 0 cutscene cameras
            writer.Write(0); // 0 cutscene path nodes
            writer.Write(0); // 0 ambient sounds

            // Write events section
            Logger.Dev(logSrc, $"Writing events: count={mesh.Events.Count}");
            writer.Write(mesh.Events.Count);
            foreach (var ev in mesh.Events)
            {
                Logger.Dev(logSrc, $"  → event UID={ev.UID}, type={ev.ClassName}");

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
            Logger.Dev(logSrc, $"Writing MP respawn points: count={mesh.MPRespawnPoints.Count}");
            writer.Write(mesh.MPRespawnPoints.Count);
            foreach (var pt in mesh.MPRespawnPoints)
            {
                Logger.Dev(logSrc, $"  → MPSpawn UID={pt.UID}, Red={pt.RedTeam}, Blue={pt.BlueTeam}, Bot={pt.IsBot}");

                // UID
                writer.Write(pt.UID);

                // Position
                var ptPos = mirrorActive ? MirrorPosAboutPivot(pt.Position, posAxis, 0f) : pt.Position;
                writer.Write(ptPos.X); writer.Write(ptPos.Y); writer.Write(ptPos.Z);


                // 3×3 rotation matrix (row‑major: M11–M13, M21–M23, M31–M33)
                var R = mirrorActive ? MirrorRotationAboutOrigin(pt.Rotation, Config.GeoMirror) : pt.Rotation;
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
            }

            // --- nav points section ---
            Logger.Dev(logSrc, $"Writing nav points: count={mesh.NavPoints.Count}");
            writer.Write(mesh.NavPoints.Count);
            for (int i = 0; i < mesh.NavPoints.Count; i++)
            {
                var np = mesh.NavPoints[i];

                // log
                Logger.Dev(logSrc,
                    $"  → NavPoint[{i}] " +
                    $"UID={np.UID}, HiddenInEditor={(np.HiddenInEditor ? 1 : 0)}, " +
                    $"Height={np.Height:F3}, Pos=({np.Position.X:F3},{np.Position.Y:F3},{np.Position.Z:F3}), " +
                    $"Radius={np.Radius:F3}, Type={(int)np.Type}, Directional={(np.Directional ? 1 : 0)}, " +
                    $"Cover={(np.Cover ? 1 : 0)}, Hide={(np.Hide ? 1 : 0)}, Crunch={(np.Crunch ? 1 : 0)}, " +
                    $"PauseTime={np.PauseTime:F3}, Links=[{string.Join(",", np.LinkIndices)}]"
                );

                // UID
                writer.Write(np.UID);

                // hidden_in_editor (u1)
                writer.Write((byte)(np.HiddenInEditor ? 1 : 0));

                // height (f4)
                writer.Write(np.Height);

                // position (vec3)
                writer.Write(np.Position.X);
                writer.Write(np.Position.Y);
                writer.Write(np.Position.Z);

                // radius (f4)
                writer.Write(np.Radius);

                // type (s4)
                writer.Write((int)np.Type);

                // directional (u1)
                writer.Write((byte)(np.Directional ? 1 : 0));

                // if directional, 3×3 rotation matrix (forward, right, up)
                if (np.Directional && np.Rotation.HasValue)
                {
                    var R = np.Rotation.Value;
                    // forward = third row
                    writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);
                    // right   = first row
                    writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
                    // up      = second row
                    writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
                }

                // … after writing directional and optional rot …

                // cover, hide, crunch (three u1s)
                writer.Write((byte)(np.Cover ? 1 : 0));
                writer.Write((byte)(np.Hide ? 1 : 0));
                writer.Write((byte)(np.Crunch ? 1 : 0));

                // pause_time (f4)
                writer.Write(np.PauseTime);

                // now the uid_list
                writer.Write(np.LinkIndices.Count);
                foreach (var link in np.LinkIndices)
                    writer.Write(link);

            }

            writer.Write(0); // 0 entities

            // Write items section
            Logger.Dev(logSrc, $"Writing items: count={mesh.Items.Count}");
            writer.Write(mesh.Items.Count);
            foreach (var it in mesh.Items)
            {
                Logger.Dev(logSrc, $"  → item UID={it.UID}, class={it.ClassName}");

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
                var itPos = mirrorActive ? MirrorPosAboutPivot(it.Position, posAxis, 0f) : it.Position;
                writer.Write(itPos.X); writer.Write(itPos.Y); writer.Write(itPos.Z);


                // 3×3 rotation matrix
                var R = mirrorActive ? MirrorRotationAboutOrigin(it.Rotation, Config.GeoMirror) : it.Rotation;
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

            // --- Clutters Section ---
            Logger.Dev(logSrc, $"Writing clutters: count={mesh.Clutters.Count}");
            writer.Write(mesh.Clutters.Count);
            foreach (var c in mesh.Clutters)
            {
                // UID
                writer.Write(c.UID);

                // class_name
                Utils.WriteVString(writer, c.ClassName);

                var cPos = mirrorActive ? MirrorPosAboutPivot(c.Position, posAxis, 0f) : c.Position;
                writer.Write(cPos.X);
                writer.Write(cPos.Y);
                writer.Write(cPos.Z);

                // rotation (3×3 matrix row-major: M11–M13, M21–M23, M31–M33)
                var R = mirrorActive
                    ? MirrorRotationAboutOrigin(c.Rotation, Config.GeoMirror)
                    : c.Rotation;

                writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
                writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
                writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);

                // script_name
                Utils.WriteVString(writer, c.ScriptName);

                // hidden_in_editor flag
                writer.Write((byte)(c.HiddenInEditor ? 1 : 0));

                // the “unknown” int
                //writer.Write(c.Unknown);
                writer.Write(0);

                // skin
                Utils.WriteVString(writer, c.Skin);

                // links (uid_list)
                writer.Write(c.Links.Count);
                foreach (var link in c.Links)
                    writer.Write(link);
            }


            // Write triggers section
            Logger.Dev(logSrc, $"Writing triggers: count={mesh.Triggers.Count}");
            writer.Write(mesh.Triggers.Count);
            foreach (var trg in mesh.Triggers)
            {
                Logger.Dev(logSrc, $"  → trigger UID={trg.UID}, shape={trg.Shape}");

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

                // position (mirror about world origin if active)
                var trgPos = mirrorActive ? MirrorPosAboutPivot(trg.Position, posAxis, 0f) : trg.Position;
                writer.Write(trgPos.X);
                writer.Write(trgPos.Y);
                writer.Write(trgPos.Z);

                // sphere vs box
                if (trg.Shape == TriggerShape.Sphere)
                {
                    writer.Write(trg.SphereRadius);
                }
                else
                {
                    // rotation matrix (mirror in world if active)
                    var R = mirrorActive
                        ? MirrorRotationAboutOrigin(trg.Rotation, Config.GeoMirror)
                        : trg.Rotation;

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


            //writer.Write(0); // 0 decals

            // Write decals section
            Logger.Dev(logSrc, $"Writing decals: count={mesh.Decals.Count}");
            writer.Write(mesh.Decals.Count);
            foreach (var dc in mesh.Decals)
            {
                Logger.Dev(logSrc, $"  → decal UID={dc.UID}, texture={dc.Texture}, scale={dc.Scale}");

                // UID
                writer.Write(dc.UID);

                // class name (“Decal”)
                Utils.WriteVString(writer, dc.ClassName);

                // position
                var dcPos = mirrorActive ? MirrorPosAboutPivot(dc.Position, posAxis, 0f) : dc.Position;
                writer.Write(dcPos.X);
                writer.Write(dcPos.Y);
                writer.Write(dcPos.Z);

                // 3×3 rotation (forward, right, up)
                var R = mirrorActive
                    ? MirrorRotationAboutOrigin(dc.Rotation, Config.GeoMirror)
                    : dc.Rotation;

                // forward = third row
                writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);
                // right   = first row
                writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
                // up      = second row
                writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);


                /*
                // position
                writer.Write(dc.Position.X);
                writer.Write(dc.Position.Y);
                writer.Write(dc.Position.Z);

                // 3×3 rotation (forward, right, up)
                var R = dc.Rotation;
                // forward = third row
                writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);
                // right   = first row
                writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
                // up      = second row
                writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);
                */
                // script name
                Utils.WriteVString(writer, dc.ScriptName);

                // hidden flag
                writer.Write((byte)(dc.HiddenInEditor ? 1 : 0));

                // extents (vec3)
                writer.Write(dc.Extents.X);
                writer.Write(dc.Extents.Y);
                writer.Write(dc.Extents.Z);

                // texture
                Utils.WriteVString(writer, dc.Texture);

                // alpha (s4)
                writer.Write(dc.Alpha);

                // self_illuminated (u1)
                writer.Write((byte)(dc.SelfIlluminated ? 1 : 0));

                // tiling (s4 enum)
                writer.Write((int)dc.Tiling);

                // scale (f4)
                writer.Write(dc.Scale);
            }

            // Write climbing regions section
            Logger.Dev(logSrc, $"Writing climbing regions: count={mesh.ClimbingRegions.Count}");
            writer.Write(mesh.ClimbingRegions.Count);
            foreach (var cr in mesh.ClimbingRegions)
            {
                Logger.Dev(logSrc, $"  → climb UID={cr.UID}, type={cr.Type}, extents={cr.Extents}");

                // UID
                writer.Write(cr.UID);

                // class name
                Utils.WriteVString(writer, cr.ClassName);

                // position
                var crPos = mirrorActive ? MirrorPosAboutPivot(cr.Position, posAxis, 0f) : cr.Position;
                writer.Write(crPos.X);
                writer.Write(crPos.Y);
                writer.Write(crPos.Z);

                // 3×3 rotation (forward, right, up)
                var R = mirrorActive
                    ? MirrorRotationAboutOrigin(cr.Rotation, Config.GeoMirror)
                    : cr.Rotation;

                // forward = third row
                writer.Write(R.M31); writer.Write(R.M32); writer.Write(R.M33);
                // right   = first row
                writer.Write(R.M11); writer.Write(R.M12); writer.Write(R.M13);
                // up      = second row
                writer.Write(R.M21); writer.Write(R.M22); writer.Write(R.M23);


                // script name
                Utils.WriteVString(writer, cr.ScriptName);

                // hidden flag
                writer.Write((byte)(cr.HiddenInEditor ? 1 : 0));

                // region type
                writer.Write(cr.Type);

                // extents
                writer.Write(cr.Extents.X);
                writer.Write(cr.Extents.Y);
                writer.Write(cr.Extents.Z);
            }

            writer.Write(0); // 0 room effects
            writer.Write(0); // 0 eax effects
            writer.Write(0); // 0 bolt emitters
            writer.Write(0); // 0 targets

            // Write push regions section
            Logger.Dev(logSrc, $"Writing push regions: count={mesh.PushRegions.Count}");
            writer.Write(mesh.PushRegions.Count);
            foreach (var pr in mesh.PushRegions)
            {
                Logger.Dev(logSrc, $"  → push UID={pr.UID}, strength={pr.Strength}");

                // UID
                writer.Write(pr.UID);

                // class name
                Utils.WriteVString(writer, pr.ClassName);

                // position
                var prPos = mirrorActive ? MirrorPosAboutPivot(pr.Position, posAxis, 0f) : pr.Position;
                writer.Write(prPos.X); writer.Write(prPos.Y); writer.Write(prPos.Z);


                // 3×3 rotation matrix
                var R = mirrorActive ? MirrorRotationAboutOrigin(pr.Rotation, Config.GeoMirror) : pr.Rotation;
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
            //  writer.Write(0);

            // --- MOVING GROUPS ---
            foreach (var grp in mesh.Groups)
            {
                // a) group_name
                Utils.WriteVString(writer, grp.Name);

                // b) is_moving
                writer.Write((byte)1);

                // c) moving_data
                var md = grp.MovingData;
                // keyframes
                writer.Write(md.Keyframes.Count);
                foreach (var kf in md.Keyframes)
                {
                    writer.Write(kf.UID);
                    writer.Write(kf.Pos.X); writer.Write(kf.Pos.Y); writer.Write(kf.Pos.Z);
                    // 3×3 rotation
                    writer.Write(kf.Rot.M11); writer.Write(kf.Rot.M12); writer.Write(kf.Rot.M13);
                    writer.Write(kf.Rot.M21); writer.Write(kf.Rot.M22); writer.Write(kf.Rot.M23);
                    writer.Write(kf.Rot.M31); writer.Write(kf.Rot.M32); writer.Write(kf.Rot.M33);
                    Utils.WriteVString(writer, kf.ScriptName);
                    writer.Write((byte)(kf.HiddenInEditor ? 1 : 0));
                    writer.Write(kf.PauseTime);
                    writer.Write(kf.DepartTravelTime);
                    writer.Write(kf.ReturnTravelTime);
                    writer.Write(kf.AccelTime);
                    writer.Write(kf.DecelTime);
                    writer.Write(kf.EventUID);
                    writer.Write(kf.ItemUID1);
                    writer.Write(kf.ItemUID2);
                    writer.Write(kf.DegreesAboutAxis);
                }
                // member transforms
                writer.Write(md.MemberTransforms.Count);
                foreach (var mt in md.MemberTransforms)
                {
                    writer.Write(mt.UID);
                    writer.Write(mt.Pos.X); writer.Write(mt.Pos.Y); writer.Write(mt.Pos.Z);
                    writer.Write(mt.Rot.M11); writer.Write(mt.Rot.M12); writer.Write(mt.Rot.M13);
                    writer.Write(mt.Rot.M21); writer.Write(mt.Rot.M22); writer.Write(mt.Rot.M23);
                    writer.Write(mt.Rot.M31); writer.Write(mt.Rot.M32); writer.Write(mt.Rot.M33);
                }
                // flags & other scalar fields
                writer.Write((byte)(md.IsDoor ? 1 : 0));
                writer.Write((byte)(md.RotateInPlace ? 1 : 0));
                writer.Write((byte)(md.StartsBackwards ? 1 : 0));
                writer.Write((byte)(md.UseTravelTimeAsSpeed ? 1 : 0));
                writer.Write((byte)(md.ForceOrient ? 1 : 0));
                writer.Write((byte)(md.NoPlayerCollide ? 1 : 0));
                writer.Write(md.MovementType);
                writer.Write(md.StartingKeyframe);
                Utils.WriteVString(writer, md.StartSound);
                writer.Write(md.StartVol);
                Utils.WriteVString(writer, md.LoopingSound);
                writer.Write(md.LoopingVol);
                Utils.WriteVString(writer, md.StopSound);
                writer.Write(md.StopVol);
                Utils.WriteVString(writer, md.CloseSound);
                writer.Write(md.CloseVol);

                // d) brushes (only those in this group)
                var groupBrushes = mesh.Brushes
                    .FindAll(b => grp.Brushes.Contains(b.UID));
                WriteBrushesSection(writer, groupBrushes);

                // e–end) all other sections empty
                WriteEmptySection(writer); // geo_regions
                WriteEmptySection(writer); // lights
                WriteEmptySection(writer); // cutscene_cameras
                WriteEmptySection(writer); // cutscene_path_nodes
                WriteEmptySection(writer); // ambient_sounds
                WriteEmptySection(writer); // events
                WriteEmptySection(writer); // mp_respawn_points
                writer.Write(0);          // num_nav_points
                WriteEmptySection(writer); // entities
                WriteEmptySection(writer); // items
                WriteEmptySection(writer); // clutters
                WriteEmptySection(writer); // triggers
                WriteEmptySection(writer); // particle_emitters
                WriteEmptySection(writer); // gas_regions
                WriteEmptySection(writer); // decals
                WriteEmptySection(writer); // climbing_regions
                WriteEmptySection(writer); // room_effects
                WriteEmptySection(writer); // eax_effects
                WriteEmptySection(writer); // bolt_emitters
                WriteEmptySection(writer); // targets
                WriteEmptySection(writer); // push_regions
            }

            Logger.Info(logSrc, "RFG export complete.");

        }

        private static void WriteEmptySection(BinaryWriter writer)
        {
            writer.Write(0);
        }

        /// <summary>
        /// Writes a brushes section to the RFG, using exactly the supplied brushes list.
        /// </summary>
        private static void WriteBrushesSection(BinaryWriter writer, List<Brush> brushes)
        {
            Logger.Dev(logSrc, $"Writing brushes section: count={brushes.Count}");
            writer.Write(brushes.Count);

            bool mirrorActive = Config.GeoMirror != Config.MirrorAxis.None;
            var posAxis = AxisForPosGeo(Config.GeoMirror);

            // Mirror about world origin (no per-group pivot)
            float pivot = 0f;
            // Build A using the mapped axis
            Vector3 A_r1 = new(1, 0, 0), A_r2 = new(0, 1, 0), A_r3 = new(0, 0, 1);
            if (mirrorActive)
            {
                Logger.Dev(logSrc, $"Brush mirroring about world origin on axis {Config.GeoMirror}");
                switch (posAxis)
                {
                    case Config.MirrorAxis.X: A_r1.X = -1; break;
                    case Config.MirrorAxis.Y: A_r2.Y = -1; break;
                    case Config.MirrorAxis.Z: A_r3.Z = -1; break;
                }
            }

            // --- Local math helpers (3x3 only) ---
            static void Mat3_FromRows(Matrix4x4 m, out Vector3 r1, out Vector3 r2, out Vector3 r3)
            {   // rows in your brush.RotationMatrix are M11..M13, etc.
                r1 = new Vector3(m.M11, m.M12, m.M13);
                r2 = new Vector3(m.M21, m.M22, m.M23);
                r3 = new Vector3(m.M31, m.M32, m.M33);
            }

            static Vector3 Mat3MulVec3(in Vector3 r1, in Vector3 r2, in Vector3 r3, Vector3 v)
            {   // rows * column
                return new Vector3(
                    r1.X * v.X + r1.Y * v.Y + r1.Z * v.Z,
                    r2.X * v.X + r2.Y * v.Y + r2.Z * v.Z,
                    r3.X * v.X + r3.Y * v.Y + r3.Z * v.Z
                );
            }

            static void Mat3Transpose(in Vector3 r1, in Vector3 r2, in Vector3 r3, out Vector3 c1, out Vector3 c2, out Vector3 c3)
            {   // columns of R are rows of R^T
                c1 = new Vector3(r1.X, r2.X, r3.X);
                c2 = new Vector3(r1.Y, r2.Y, r3.Y);
                c3 = new Vector3(r1.Z, r2.Z, r3.Z);
            }

            static void Mat3Mul(in Vector3 A_r1, in Vector3 A_r2, in Vector3 A_r3,
                                in Vector3 B_r1, in Vector3 B_r2, in Vector3 B_r3,
                                out Vector3 C_r1, out Vector3 C_r2, out Vector3 C_r3)
            {   // C = A * B (row-major)
                // columns of B
                Vector3 B_c1 = new Vector3(B_r1.X, B_r2.X, B_r3.X);
                Vector3 B_c2 = new Vector3(B_r1.Y, B_r2.Y, B_r3.Y);
                Vector3 B_c3 = new Vector3(B_r1.Z, B_r2.Z, B_r3.Z);

                C_r1 = new Vector3(
                    Vector3.Dot(A_r1, B_c1),
                    Vector3.Dot(A_r1, B_c2),
                    Vector3.Dot(A_r1, B_c3)
                );
                C_r2 = new Vector3(
                    Vector3.Dot(A_r2, B_c1),
                    Vector3.Dot(A_r2, B_c2),
                    Vector3.Dot(A_r2, B_c3)
                );
                C_r3 = new Vector3(
                    Vector3.Dot(A_r3, B_c1),
                    Vector3.Dot(A_r3, B_c2),
                    Vector3.Dot(A_r3, B_c3)
                );
            }

            foreach (var brush in brushes)
            {
                // --- Log ---
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
                var flagsSummary = flagDescriptions.Count > 0 ? string.Join(", ", flagDescriptions) : "none";
                Logger.Dev(logSrc,
                    $"  → brush UID={brush.UID}, verts={brush.Vertices.Count}, faces={brush.Solid.Faces.Count}, " +
                    $"life={brush.Solid.Life}, flags=0x{flags:X8} ({flagsSummary})");

                writer.Write(brush.UID);

                // Position: mirror WHOLE-GROUP about pivot (affine mirror in world)
                var posOut = mirrorActive ? MirrorPosAboutPivot(brush.Position, posAxis, pivot) : brush.Position;
                writer.Write(posOut.X); writer.Write(posOut.Y); writer.Write(posOut.Z);


                // Orientation: KEEP original (det +1). Do NOT mirror basis here.
                var R = brush.RotationMatrix;
                // Rows of R
                Mat3_FromRows(R, out var R_r1, out var R_r2, out var R_r3);

                // Emit F/R/U from original basis (unchanged)
                var fwd = new Vector3(R.M31, R.M32, R.M33);
                var right = new Vector3(R.M11, R.M12, R.M13);
                var up = new Vector3(R.M21, R.M22, R.M23);
                writer.Write(fwd.X); writer.Write(fwd.Y); writer.Write(fwd.Z);
                writer.Write(right.X); writer.Write(right.Y); writer.Write(right.Z);
                writer.Write(up.X); writer.Write(up.Y); writer.Write(up.Z);

                // --- Reserved / Empty ---
                writer.Write(0);
                writer.Write(0);
                Utils.WriteVString(writer, "");

                // --- Textures ---
                writer.Write(brush.Solid.Textures.Count);
                Logger.Debug(logSrc, $"Brush {brush.UID} textures: {string.Join(", ", brush.Solid.Textures)}");
                foreach (var tex in brush.Solid.Textures)
                    Utils.WriteVString(writer, tex);

                var scrollFaceDict = new Dictionary<int, Face>();
                foreach (var face in brush.Solid.Faces)
                {
                    if ((Math.Abs(face.ScrollU) > 0.0001f || Math.Abs(face.ScrollV) > 0.0001f) && !scrollFaceDict.ContainsKey(face.FaceId))
                        scrollFaceDict[face.FaceId] = face;
                }
                writer.Write(scrollFaceDict.Count);
                foreach (var kvp in scrollFaceDict)
                {
                    var face = kvp.Value;
                    Logger.Dev(logSrc, $"  scroll: faceId={face.FaceId}, U={face.ScrollU}, V={face.ScrollV}");
                    writer.Write(face.FaceId);
                    writer.Write(face.ScrollU);
                    writer.Write(face.ScrollV);
                }
                writer.Write(0); // numRooms
                writer.Write(0); // numSubroomLinks
                writer.Write(0); // numPortals

                // --- Bake world-axis mirror into LOCAL vertices: v' = R^T * A * R * v ---
                // Build R^T
                Mat3Transpose(R_r1, R_r2, R_r3, out var RT_c1, out var RT_c2, out var RT_c3); // columns of R are rows of RT

                // Compute Mlocal = RT * A * R
                // First: B = A * R
                Mat3Mul(A_r1, A_r2, A_r3, R_r1, R_r2, R_r3, out var B_r1, out var B_r2, out var B_r3);
                // Then: M = RT * B   (note: RT rows == columns of R -> use RT_c* as rows)
                Mat3Mul(RT_c1, RT_c2, RT_c3, B_r1, B_r2, B_r3, out var M_r1, out var M_r2, out var M_r3);

                var exportVerts = new List<Vector3>();
                var vertMap = new Dictionary<(int, int, int), int>();
                var remappedFaces = new List<Face>();

                foreach (var face in brush.Solid.Faces)
                {
                    var newFace = new Face
                    {
                        TextureIndex = face.TextureIndex,
                        FaceFlags = face.FaceFlags,
                        FaceId = face.FaceId,
                        //ScrollU = face.ScrollU,
                        //ScrollV = face.ScrollV,
                        SmoothingGroups = face.SmoothingGroups,
                        Vertices = new List<int>(),
                        UVs = new List<Vector2>()
                    };

                    for (int i = 0; i < face.Vertices.Count; i++)
                    {
                        int posIndex = face.Vertices[i];

                        // Original local vertex
                        var v = brush.Vertices[posIndex];

                        // Mirror in local space via conjugation (if active)
                        var vOut = mirrorActive ? Mat3MulVec3(M_r1, M_r2, M_r3, v) : v;

                        // Gracefully handle missing UVs
                        var uv = i < face.UVs.Count ? face.UVs[i] : Vector2.Zero;

                        // Dedup
                        var key = ((int)(vOut.X * 1000), (int)(vOut.Y * 1000), (int)(vOut.Z * 1000));
                        if (!vertMap.TryGetValue(key, out int vertIdx))
                        {
                            vertIdx = exportVerts.Count;
                            exportVerts.Add(vOut);
                            vertMap[key] = vertIdx;
                        }

                        newFace.Vertices.Add(vertIdx);
                        newFace.UVs.Add(uv);
                    }

                    if (newFace.Vertices.Count > 3)
                        Logger.Debug(logSrc, $"Exported an ngon face with {newFace.Vertices.Count} vertices on brush {brush.UID}.");

                    remappedFaces.Add(newFace);
                }

                // Vertex list
                writer.Write(exportVerts.Count);
                foreach (var v in exportVerts)
                {
                    writer.Write(v.X);
                    writer.Write(v.Y);
                    writer.Write(v.Z);
                }

                // Faces
                writer.Write(remappedFaces.Count);
                foreach (var face in remappedFaces)
                {
                    // Prepare local copy
                    var idxs = face.Vertices.ToArray();
                    var uvs = face.UVs.ToArray();

                    // Reflection flips handedness → reverse winding to keep outward normals
                    if ((Config.FlipNormals || mirrorActive) && idxs.Length > 2)
                    {
                        Array.Reverse(idxs, 1, idxs.Length - 1);
                        Array.Reverse(uvs, 1, uvs.Length - 1);
                    }

                    // Plane from (possibly reversed) winding in local space
                    var origin = exportVerts[idxs[0]];
                    var normal = Vector3.Zero;
                    for (int i = 1; i < idxs.Length - 1; i++)
                        normal += Vector3.Cross(
                            exportVerts[idxs[i]] - origin,
                            exportVerts[idxs[i + 1]] - origin
                        );
                    normal = Vector3.Normalize(normal);
                    var dist = -Vector3.Dot(normal, origin);

                    writer.Write(normal.X);
                    writer.Write(normal.Y);
                    writer.Write(normal.Z);
                    writer.Write(dist);

                    writer.Write(face.TextureIndex);

                    writer.Write(-1);                       // surface_index
                    writer.Write(face.FaceId);              // face_id
                    writer.Write(-1);                       // unk12
                    writer.Write(-1);                       // reserved1
                    writer.Write(-1);                       // portal_index
                    writer.Write(face.FaceFlags);           // face_flags (ushort)
                    writer.Write((ushort)0);                // reserved2
                    writer.Write(face.SmoothingGroups);     // smoothing_groups
                    writer.Write(-1);                       // room_index

                    writer.Write(idxs.Length);
                    for (int i = 0; i < idxs.Length; i++)
                    {
                        writer.Write(idxs[i]);
                        writer.Write(uvs[i].X);
                        writer.Write(uvs[i].Y);
                    }
                }

                Logger.Dev(logSrc, $"Brush {brush.UID} has {exportVerts.Count} vertices, {remappedFaces.Count} faces");

                // --- End of brush ---
                writer.Write(0);

                // --- Strip unsupported flags if needed ---
                var exportFlags = brush.Solid.Flags;
                bool wasGeoable = false;
                if (Config.SetRF2GeoableNonDetail)
                {
                    var before = (SolidFlags)exportFlags;
                    var after = SolidFlagUtils.StripRF2Geoable(before);
                    if ((before & SolidFlags.Detail) != 0 && (after & SolidFlags.Detail) == 0)
                    {
                        Logger.Debug(logSrc, $"Removing detail flag from RF2 geoable brush {brush.UID}.");
                        wasGeoable = true;
                    }
                    exportFlags = (uint)after;
                }
                exportFlags = (uint)SolidFlagUtils.MakeRF1SafeFlags((SolidFlags)exportFlags);

                writer.Write(exportFlags);
                writer.Write(wasGeoable ? -1 : brush.Solid.Life);
                writer.Write(brush.Solid.State);
            }
        }

        public static void HandleCoronaClutterReplacements(Mesh mesh)
        {
            // 1) collect Corona UIDs
            var coronaClutterUIDs = new List<int>();
            if (!string.IsNullOrEmpty(Config.CoronaClutterName))
            {
                // 2) for each corona, add a Clutter
                foreach (var c in mesh.Coronas)
                {
                    coronaClutterUIDs.Add(c.UID);

                    mesh.Clutters.Add(new Clutter
                    {
                        UID = c.UID,
                        ClassName = Config.CoronaClutterName,
                        ScriptName = c.ScriptName,
                        Position = c.Position,
                        Rotation = Matrix4x4.Identity,
                        HiddenInEditor = false,
                        Skin = "",
                        Links = new List<int>()
                    });
                }

                // 4) create the Unhide event (links → all new clutters)
                int unhideEventUID = RflUtils.FindNextValidUID(mesh);
                mesh.Events.Add(new RflEvent
                {
                    UID = unhideEventUID,
                    ClassName = "Unhide",
                    ScriptName = "Unhide",
                    Position = Vector3.Zero + new Vector3(0, 3, 0),
                    HiddenInEditor = false,
                    Delay = 0f,
                    Bool1 = false,
                    Bool2 = false,
                    Int1 = 0,
                    Int2 = 0,
                    Float1 = 0f,
                    Float2 = 0f,
                    Str1 = "",
                    Str2 = "",
                    Links = new List<int>(coronaClutterUIDs),
                    RawColor = 0xFF00FFFF
                });

                // 5) create the Route_Node event (links → Unhide)
                int routeNodeEventUID = RflUtils.FindNextValidUID(mesh);
                mesh.Events.Add(new RflEvent
                {
                    UID = routeNodeEventUID,
                    ClassName = "Route_Node",
                    ScriptName = "Route_Node",
                    Position = Vector3.Zero + new Vector3(0, 2, 0),
                    HiddenInEditor = false,
                    Delay = 0f,
                    Bool1 = false,
                    Bool2 = false,
                    Int1 = 2, // invert
                    Int2 = 0,
                    Float1 = 0f,
                    Float2 = 0f,
                    Str1 = "",
                    Str2 = "",
                    Links = new List<int> { unhideEventUID },
                    RawColor = 0xFF00FFFF
                });

                // 7) create the auto‐trigger (links → Route_Node)
                int TriggerUID = RflUtils.FindNextValidUID(mesh);
                mesh.Triggers.Add(new Trigger
                {
                    UID = TriggerUID,
                    ScriptName = "Redux Trigger Auto",
                    HiddenInEditor = false,
                    Shape = TriggerShape.Sphere,
                    ResetsAfter = 0f,
                    ResetsTimes = -1,
                    UseKeyRequired = false,
                    KeyName = "",
                    WeaponActivates = false,
                    ActivatedBy = TriggerActivatedBy.AllObjects,
                    IsNpc = false,
                    IsAuto = true,
                    InVehicle = false,
                    Position = Vector3.Zero + new Vector3(0, 1, 0),
                    SphereRadius = 1f,
                    AirlockRoomUID = -1,
                    AttachedToUID = -1,
                    UseClutterUID = -1,
                    Disabled = false,
                    ButtonActiveTime = 0f,
                    InsideTime = 0f,
                    Team = TriggerTeam.None,
                    Links = new List<int> { routeNodeEventUID }
                });
            }
        }
    }
}
