using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace redux.utilities
{
    public static class Config
    {
        public static bool IncludePortalFaces { get; set; } = true;
        public static bool IncludeDetailFaces { get; set; } = true;
        public static bool IncludeAlphaFaces { get; set; } = true;
        public static bool IncludeHoleFaces { get; set; } = true;
        public static bool IncludeLiquidFaces { get; set; } = false;
        public static bool IncludeSkyFaces { get; set; } = true;
        public static bool IncludeInvisibleFaces { get; set; } = true;
        public static bool ParseBrushSectionInstead { get; set; } = false;
        public static bool TriangulatePolygons { get; set; } = true;
        public static bool SimpleBrushNames { get; set; } = false;
        public static bool TranslateRF2Textures { get; set; } = false;
        public static bool InsertRF2TexturePrefix { get; set; } = false;
        public static bool SetRF2GeoableNonDetail { get; set; } = false;
        public static bool DumpLightmaps { get; set; } = false;
        public static float RF2LightScale { get; set; } = 1.0f;
        public static bool FlipNormals { get; set; } = false;
        public static string ReplacementItemName { get; set; } = "";
        public static string CoronaClutterName { get; set; } = "";
        public enum LogLevel
        {
            Debug,
            Dev,
            Info,
            Warn,
            Error
        }

        public static ImageFormat ExportImageFormat { get; set; } = ImageFormat.png;

        public enum ImageFormat
        {
            png,
            tga
        }

        public static LogLevel Verbosity { get; set; } = LogLevel.Info;

        public static bool ShouldLog(LogLevel level)
        {
            return level >= Verbosity;
        }

        public enum MirrorAxis {
            None,
            X,
            Y,
            Z
        }
        public static MirrorAxis GeoMirror { get; set; } = MirrorAxis.None;
    }

    public static class MirrorUtils
    {
        public static Vector3 Apply(Vector3 v, Config.MirrorAxis axis) => axis switch
        {
            Config.MirrorAxis.X => new Vector3(-v.X, v.Y, v.Z),
            Config.MirrorAxis.Y => new Vector3(v.X, -v.Y, v.Z),
            Config.MirrorAxis.Z => new Vector3(v.X, v.Y, -v.Z),
            _ => v
        };

        // Mirror a 3x3 basis embedded in Matrix4x4 by flipping axis components
        public static Matrix4x4 Apply(Matrix4x4 m, Config.MirrorAxis axis)
        {
            // Flip each basis vector’s axis component. (We don’t touch translation; call Apply() on positions separately.)
            var m11 = m.M11; var m12 = m.M12; var m13 = m.M13;
            var m21 = m.M21; var m22 = m.M22; var m23 = m.M23;
            var m31 = m.M31; var m32 = m.M32; var m33 = m.M33;

            switch (axis)
            {
                case Config.MirrorAxis.X:
                    m11 = -m11; m12 = -m12; m13 = -m13;
                    break;
                case Config.MirrorAxis.Y:
                    m21 = -m21; m22 = -m22; m23 = -m23;
                    break;
                case Config.MirrorAxis.Z:
                    m31 = -m31; m32 = -m32; m33 = -m33;
                    break;
            }

            var r = m; // copy all
            r.M11 = m11; r.M12 = m12; r.M13 = m13;
            r.M21 = m21; r.M22 = m22; r.M23 = m23;
            r.M31 = m31; r.M32 = m32; r.M33 = m33;
            return r;
        }
    }


    public static class Logger
    {
        public static void Log(Config.LogLevel level, string source, string message)
        {
            if (!Config.ShouldLog(level))
                return;

            string prefix = level switch
            {
                Config.LogLevel.Debug => "[DEBUG]",
                Config.LogLevel.Info => "[INFO ]",
                Config.LogLevel.Warn => "[WARN ]",
                Config.LogLevel.Error => "[ERROR]",
                _ => "[LOG  ]"
            };

            Console.WriteLine($"{prefix} [{source}] {message}");
        }

        public static void Debug(string source, string message) => Log(Config.LogLevel.Debug, source, message);
        public static void Dev(string source, string message) => Log(Config.LogLevel.Dev, source, message);
        public static void Info(string source, string message) => Log(Config.LogLevel.Info, source, message);
        public static void Warn(string source, string message) => Log(Config.LogLevel.Warn, source, message);
        public static void Error(string source, string message) => Log(Config.LogLevel.Error, source, message);
    }

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

            if (length == 0 || length == 0xFFFF)
                return string.Empty;

            if (reader.BaseStream.Position + length > reader.BaseStream.Length)
            {
                Console.WriteLine($"Warning: String length {length} exceeds remaining file size at position {reader.BaseStream.Position}.");
                return string.Empty;
            }

            byte[] data = reader.ReadBytes(length);
            return Encoding.ASCII.GetString(data);
        }
        public static string ReadVStringPlain(BinaryReader r)
        {
            byte len = r.ReadByte();
            if (len == 0) return "";
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }
        public static string ReadVStringFixedLength(BinaryReader reader, long sectionEnd)
        {
            // read the 2‑byte length prefix
            if (reader.BaseStream.Position + 2 > reader.BaseStream.Length)
                return string.Empty;
            ushort length = reader.ReadUInt16();

            // 0 or 0xFFFF → no string data, just skip nothing
            if (length == 0 || length == 0xFFFF)
                return string.Empty;

            // figure out how many bytes *we can* safely skip
            long remainingInSection = sectionEnd - reader.BaseStream.Position;
            int toSkip = (int)Math.Min(length, Math.Max(0, remainingInSection));

            // actually consume exactly that many bytes
            byte[] data = reader.ReadBytes(toSkip);

            return Encoding.ASCII.GetString(data);
        }


        public static void WriteVString(BinaryWriter writer, string str)
        {
            str ??= string.Empty;
            byte[] bytes = Encoding.ASCII.GetBytes(str);

            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException("String too long for vstring format.");

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        public static string ReadFixedAscii(BinaryReader r, int length)
        {
            var bytes = r.ReadBytes(length);
            int end = Array.IndexOf<byte>(bytes, 0);
            if (end < 0) end = length;
            return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
        }

        public static string ReadZeroTerminatedAscii(BinaryReader r)
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                byte b = r.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        public enum EditorViewType : int
        {
            FreeLook = 0,
            TopDown = 1,
            SideView = 2
        }
    }

    // used for static geometry in RFL format
    public static class FaceFlags
    {
        public const ushort ShowSky = 0x01;
        public const ushort Mirrored = 0x02;
        public const ushort LiquidSurface = 0x04;
        public const ushort IsDetail = 0x08;
        public const ushort ScrollTexture = 0x10;
        public const ushort FullBright = 0x20;
        public const ushort HasAlpha = 0x40;
        public const ushort HasHoles = 0x80;
        public const ushort LightmapResolutionMask = 0x0300;
        public const ushort IsInvisible = 0x2000;
    }

    // used for static geometry in RFL format
    public class SubroomList
    {
        public int RoomIndex { get; set; }
        public int NumSubrooms { get; set; }
        public List<int> SubroomIndices { get; set; } = new();
    }

    // used for static geometry in RFL format
    public class Portal
    {
        public int RoomIndex1 { get; set; }
        public int RoomIndex2 { get; set; }
        public Vector3 Point1 { get; set; }
        public Vector3 Point2 { get; set; }
    }

    public static class ObjUtils
    {
        public static int ParseUidFromObjectName(string objectName, int fallback)
        {
            var parts = objectName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("Brush", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(parts[1], out int uid))
                    return uid;
            }

            //Logger.Warn(logSrc, $"Could not parse UID from object name '{objectName}', using fallback UID {fallback}");
            return fallback;
        }

        public static uint ParseFlagsFromObjectName(string objectName)
        {
            uint flags = 0;
            var parts = objectName.Split('_', StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "air": flags |= 0x02; break;
                    case "solid": break;
                    case "detail": flags |= 0x04; break;
                    case "nodetail": break;
                    case "portal": flags |= 0x01; break;
                    case "noportal": break;
                    case "emit": flags |= 0x10; break;
                    case "noemit": break;
                }
            }

            return flags;
        }
    }

    public static class RflUtils
    {
        public static bool IsRF2 (int rfl_version)
        {
            // 295
            return rfl_version == 0x127;
        }
        public static bool IsRF1(int rfl_version)
        {
            // >= 300 || <= 180
            return rfl_version >= 0x12C || rfl_version <= 0xC8;
        }
        public readonly struct VertexKey : IEquatable<VertexKey>
        {
            public readonly Vector3 Position;
            public readonly Vector2 UV;
            public readonly Vector3 Normal;

            public VertexKey(Vector3 pos, Vector2 uv, Vector3 norm)
            {
                Position = pos; UV = uv; Normal = norm;
            }

            public bool Equals(VertexKey o) =>
              Position.Equals(o.Position)
           && UV.Equals(o.UV)
           && Normal.Equals(o.Normal);

            public override int GetHashCode() =>
              HashCode.Combine(Position, UV, Normal);
        }

        private static void SafeSkip(BinaryReader reader, int entrySize)
        {
            if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
                return;
            int count = reader.ReadInt32();
            long bytesToSkip = (long)entrySize * count;
            if (reader.BaseStream.Position + bytesToSkip > reader.BaseStream.Length)
                reader.BaseStream.Seek(0, SeekOrigin.End);
            else
                reader.BaseStream.Seek(bytesToSkip, SeekOrigin.Current);
        }
        public static void SkipLights(BinaryReader reader) => SafeSkip(reader, 100);
        public static void SkipCutsceneCameras(BinaryReader reader) => SafeSkip(reader, 84);
        public static void SkipCutscenePathNodes(BinaryReader reader) => SafeSkip(reader, 84);
        public static void SkipAmbientSounds(BinaryReader reader) => SafeSkip(reader, 48);
        public static void SkipEvents(BinaryReader reader) => SafeSkip(reader, 112);
        public static void SkipMpRespawnPoints(BinaryReader reader) => SafeSkip(reader, 80);
        public static void SkipNavPoints(BinaryReader reader) => SafeSkip(reader, 100);
        public static void SkipEntities(BinaryReader reader) => SafeSkip(reader, 220);
        public static void SkipItems(BinaryReader reader) => SafeSkip(reader, 100);
        public static void SkipClutters(BinaryReader reader) => SafeSkip(reader, 100);
        public static void SkipTriggers(BinaryReader reader) => SafeSkip(reader, 120);
        public static void SkipParticleEmitters(BinaryReader reader) => SafeSkip(reader, 180);
        public static void SkipGasRegions(BinaryReader reader) => SafeSkip(reader, 84);
        public static void SkipDecals(BinaryReader reader) => SafeSkip(reader, 80);
        public static void SkipClimbingRegions(BinaryReader reader) => SafeSkip(reader, 64);
        public static void SkipRoomEffects(BinaryReader reader) => SafeSkip(reader, 64);
        public static void SkipEaxEffects(BinaryReader reader) => SafeSkip(reader, 80);
        public static void SkipBoltEmitters(BinaryReader reader) => SafeSkip(reader, 100);
        public static void SkipTargets(BinaryReader reader) => SafeSkip(reader, 64);
        public static void SkipPushRegions(BinaryReader reader) => SafeSkip(reader, 64);
        public static void SkipGeoRegions(BinaryReader reader)
        {
            if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
                return;
            int count = reader.ReadInt32();
            long bytesToSkip = 4L + count * 4L;
            if (reader.BaseStream.Position + bytesToSkip > reader.BaseStream.Length)
                reader.BaseStream.Seek(0, SeekOrigin.End);
            else
                reader.BaseStream.Seek(bytesToSkip, SeekOrigin.Current);
        }

        public static int FindNextValidUID(Mesh mesh)
        {
            // collect every existing UID
            var used = new HashSet<int>();
            used.UnionWith(mesh.Brushes.Select(b => b.UID));
            used.UnionWith(mesh.Movers.Select(b => b.UID));
            used.UnionWith(mesh.Lights.Select(l => l.UID));
            used.UnionWith(mesh.RoomEffects.Select(r => r.UID));
            used.UnionWith(mesh.MPRespawnPoints.Select(p => p.UID));
            used.UnionWith(mesh.Events.Select(e => e.UID));
            used.UnionWith(mesh.PushRegions.Select(p => p.UID));
            used.UnionWith(mesh.Triggers.Select(t => t.UID));
            used.UnionWith(mesh.Items.Select(i => i.UID));
            used.UnionWith(mesh.ClimbingRegions.Select(c => c.UID));
            used.UnionWith(mesh.ParticleEmitters.Select(pe => pe.UID));
            used.UnionWith(mesh.NavPoints.Select(n => n.UID));
            used.UnionWith(mesh.Decals.Select(d => d.UID));
            used.UnionWith(mesh.Clutters.Select(c => c.UID));
            used.UnionWith(mesh.Coronas.Select(c => c.UID));

            // scan upward from 1 until we find a free UID
            int candidate = 1;
            while (used.Contains(candidate))
                candidate++;

            return candidate;
        }
    }

    public static class SolidFlagUtils
    {
        public static SolidFlags MakeRF1SafeFlags(SolidFlags flags)
        {
            const SolidFlags allowed =
                SolidFlags.Portal |
                SolidFlags.Air |
                SolidFlags.Detail |
                SolidFlags.EmitsSteam;

            return flags & allowed;
        }

        public static SolidFlags StripRF2Geoable(SolidFlags flags)
        {
            bool isAir = (flags & SolidFlags.Air) != 0;
            bool isPortal = (flags & SolidFlags.Portal) != 0;
            bool isDetail = (flags & SolidFlags.Detail) != 0;
            bool isGeoable = (flags & SolidFlags.Geoable) != 0;

            if (!isAir && !isPortal && isDetail && isGeoable)
            {
                flags &= ~SolidFlags.Detail;
                flags &= ~SolidFlags.Geoable;
            }

            return flags;
        }
    }

    public static class EmbeddedResourceLoader
    {
        public static string[] LoadLines(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string fullName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

            if (fullName == null)
                throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");

            using var stream = assembly.GetManifestResourceStream(fullName);
            using var reader = new StreamReader(stream!);
            var lines = new List<string>();

            while (!reader.EndOfStream)
                lines.Add(reader.ReadLine()!);

            return lines.ToArray();
        }
    }

    public static class RF2TextureTranslator
    {
        public static string InsertRxPrefix(string textureFilename)
        {
            // 1) Extract just the filename and extension
            string fileName = Path.GetFileName(textureFilename) ?? textureFilename;
            string extension = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            // 2) Check if there is at least one underscore in the "nameWithoutExt"
            int underscoreIndex = nameWithoutExt.IndexOf('_');
            string originalPrefix = "";
            string remainder;

            if (underscoreIndex > 0)
            {
                // include the underscore itself in the prefix
                originalPrefix = nameWithoutExt.Substring(0, underscoreIndex + 1);
                remainder = nameWithoutExt.Substring(underscoreIndex + 1);
            }
            else
            {
                // no prefix detected
                originalPrefix = "";
                remainder = nameWithoutExt;
            }

            // 3) Normalize comparisons to lowercase for prefix‐replacement logic,
            //    but keep the “shape” of the final prefix in lowercase (texture names
            //    are typically lowercase anyway).
            string lowerPrefix = originalPrefix.ToLowerInvariant();

            string finalPrefix = originalPrefix;
            switch (lowerPrefix)
            {
                case "drt_":
                    finalPrefix = "rck_";
                    break;
                case "woo_":
                case "cpt_":
                case "mar_":
                case "sp0_":
                    finalPrefix = "sld_";
                    break;
                case "tec_":
                    finalPrefix = "mtl_";
                    break;
                    // any other prefix stays as‐is (including if originalPrefix was empty)
            }

            string withRx;
            if (!string.IsNullOrEmpty(finalPrefix))
            {
                withRx = finalPrefix + "rx_" + remainder;
            }
            else
            {
                withRx = "rx_" + remainder;
            }

            return withRx + extension;
        }

        private static Dictionary<string, string>? _translationMap;

        public static int TranslationCount => _translationMap?.Count ?? 0;

        public static void LoadRF2TextureTranslations()
        {
            var realTextures = EmbeddedResourceLoader.LoadLines("real_texture_filenames.txt").ToList();
            var translatedTextures = new HashSet<string>(
                EmbeddedResourceLoader.LoadLines("translated_texture_filenames.txt"),
                StringComparer.OrdinalIgnoreCase
            );

            string[] materialPrefixes = new[]
            {
                "rck_", "mtl_", "wtr_", "pls_", "gls_", "drt_", "woo_", "tec_", "cpt_", "mar_", "sp0_"
            };

            _translationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var real in realTextures)
            {
                string baseName = real;

                // Remove known material prefix
                foreach (var prefix in materialPrefixes)
                {
                    if (real.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = real.Substring(prefix.Length);
                        break;
                    }
                }

                // Find a translated texture that ends with the base name
                string? match = translatedTextures.FirstOrDefault(t =>
                    t.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    _translationMap[real] = match;
            }

            // Hardcoded translations
            AddManualTranslation("cpt_invisible.tga", "rck_invisible04.tga");
            AddManualTranslation("drt_invisible.tga", "rck_invisible04.tga");
            AddManualTranslation("mar_invisible.tga", "sld_invisible01.tga");
            AddManualTranslation("mtl_invisible.tga", "mtl_invisible02.tga");
            AddManualTranslation("pls_invisible.tga", "sld_invisible01.tga");
            AddManualTranslation("rck_invisible.tga", "rck_invisible04.tga");
            AddManualTranslation("woo_invisible.tga", "sld_invisible01.tga");
            AddManualTranslation("wtr_invisible.tga", "cem_invisible03.tga");
            AddManualTranslation("mtl_jpad_oct1.tga", "mtl_L15S2_lift.tga");
            AddManualTranslation("mtl_jpad_oct2.tga", "mtl_jumppad01.tga");
            AddManualTranslation("mtl_jpad_oct4.tga", "mtl_jumppad02.tga");
            AddManualTranslation("cpt_012red01.tga", "sld_grf2012red01a.tga");
            AddManualTranslation("mtl_122_grate2.tga", "mtl_grf2122_grate.tga");
        }

        public static void AddManualTranslation(string original, string translated)
        {
            if (_translationMap == null)
                _translationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _translationMap[original] = translated;
        }

        public static string TranslateRF2Texture(string textureFilename)
        {
            if (_translationMap == null)
                throw new InvalidOperationException("Translation map not loaded. Call LoadRF2TextureTranslations() first.");

            if (_translationMap.TryGetValue(textureFilename, out var translated))
                return translated;

            return textureFilename; // No translation found
        }

        public static void DebugPrintAllTranslations()
        {
            if (_translationMap == null)
            {
                Console.WriteLine("Translation map not loaded.");
                return;
            }

            foreach (var pair in _translationMap)
                Console.WriteLine($"{pair.Key} -> {pair.Value}");
        }
    }
}
