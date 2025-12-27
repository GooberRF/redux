using redux.utilities;
using System.Text;
using System.Drawing.Imaging;
using System.Drawing;

namespace redux.parsers
{
    class PegParser
    {
        private const string logSrc = "PegParser";

        public static void ExtractPeg(string pegPath)
        {
            Logger.Info(logSrc, $"Parsing PEG file: {pegPath}");
            if (!File.Exists(pegPath))
            {
                Logger.Error(logSrc, $"File not found: {pegPath}");
                return;
            }

            using var fs = File.OpenRead(pegPath);
            long fileSize = fs.Length;
            using var br = new BinaryReader(fs);

            // 1) Read and validate signature
            uint signature = br.ReadUInt32();
            if (signature != 0x564B4547) // “GEKV” little-endian
            {
                Logger.Error(logSrc, $"Invalid PEG signature: 0x{signature:X}");
                return;
            }

            // 2) Read version
            uint version = br.ReadUInt32();
            if (version != 6)
            {
                Logger.Error(logSrc, $"Unsupported PEG version: {version}");
                return;
            }

            // 3) Read header fields
            uint hdrSz = br.ReadUInt32();
            uint datSz = br.ReadUInt32();
            uint texCount = br.ReadUInt32();
            uint unknown14 = br.ReadUInt32();
            uint frameCount = br.ReadUInt32();
            uint unknown1C = br.ReadUInt32();

            // Pre-allocate texture entries
            var textures = new List<Texx>((int)texCount);
            for (int i = 0; i < texCount; i++)
                textures.Add(new Texx());

            // 4) Read each texture’s header
            for (int i = 0; i < texCount; i++)
            {
                var tx = textures[i];
                tx.Width = br.ReadUInt16();
                tx.Height = br.ReadUInt16();
                tx.Fmt = br.ReadByte();
                tx.Fmt2 = br.ReadByte();
                tx.Flgs = br.ReadByte();
                tx.FrmCnt = br.ReadByte();
                tx.AnimDelay = br.ReadByte();
                tx.MipCount = br.ReadByte();
                tx.Unk1 = br.ReadByte();
                tx.Unk2 = br.ReadByte();

                // Read fixed-length 48-byte name (null-terminated)
                byte[] nameBytes = br.ReadBytes(48);
                int nameLen = Array.IndexOf<byte>(nameBytes, 0);
                if (nameLen < 0) nameLen = 48;
                tx.Name = Encoding.ASCII.GetString(nameBytes, 0, nameLen);

                tx.Offset = br.ReadUInt32();

                // Compute size of the *previous* texture once we know this offset
                if (i > 0)
                {
                    textures[i - 1].Size = tx.Offset - textures[i - 1].Offset;
                }

                // Last texture’s size extends to EOF
                if (i == texCount - 1)
                {
                    tx.Size = (uint)fileSize - tx.Offset;
                }
            }

            // 5) Create output directory (same folder as .peg, named without extension)
            string outputDir = Path.Combine(
                Path.GetDirectoryName(pegPath) ?? "",
                Path.GetFileNameWithoutExtension(pegPath)
            );
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            // 6) Decode & save each texture
            foreach (var tx in textures)
            {
                fs.Seek(tx.Offset, SeekOrigin.Begin);
                byte[] data = br.ReadBytes((int)tx.Size);

                string originalName = tx.Name;
                string processedName = originalName;
                if (Config.TranslateRF2Textures)
                {
                    processedName = RF2TextureTranslator.TranslateRF2Texture(originalName);
                    Logger.Dev(logSrc, $"TranslateRF2Textures: \"{originalName}\" → \"{processedName}\"");
                }
                else if (Config.InsertRF2TexturePrefix)
                {
                    processedName = RF2TextureTranslator.InsertRxPrefix(originalName);
                    Logger.Dev(logSrc, $"InsertRF2TexturePrefix: \"{originalName}\" → \"{processedName}\"");
                }
                else
                {
                    // no change
                    Logger.Debug(logSrc, $"No translation/prefix: \"{originalName}\"");
                }

                string baseName = Path.GetFileNameWithoutExtension(processedName);

                Bitmap bmp = null;
                try
                {
                    switch (tx.Fmt)
                    {
                        case 3:
                            bmp = DecodeA1B5G5R5(tx, data);
                            break;
                        case 4:
                            bmp = DecodeIndexed(tx, data);
                            break;
                        case 7:
                            bmp = DecodeA8B8G8R8(tx, data);
                            break;
                        default:
                            Logger.Warn(logSrc, $"Unknown format {tx.Fmt} for texture '{processedName}'. Skipping.");
                            continue;
                    }

                    // Remove any existing extension, e.g. “.tga”
                    string baseNameTx = Path.GetFileNameWithoutExtension(processedName);

                    if (Config.ExportImageFormat == Config.ImageFormat.png)
                    {
                        string fileName = baseNameTx + ".png";
                        string outPath = Path.Combine(outputDir, fileName);
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        Logger.Info(logSrc, $"Saved texture '{processedName}' as PNG → {outPath}");
                    }
                    else // tga
                    {
                        string fileName = baseNameTx + ".tga";
                        string outPath = Path.Combine(outputDir, fileName);
                        SaveAsTga(bmp, outPath);
                        Logger.Info(logSrc, $"Saved texture '{processedName}' as TGA → {outPath}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(logSrc, $"Error decoding texture '{processedName}': {ex.Message}");
                }
                finally
                {
                    bmp?.Dispose();
                }
            }
        }

        private static Bitmap DecodeA1B5G5R5(Texx tx, byte[] input)
        {
            Logger.Dev(logSrc, $"Converting {tx.Name} using DecodeA1B5G5R5");
            int width = tx.Width;
            int height = tx.Height;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            int ipos = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    ushort wrd = BitConverter.ToUInt16(input, ipos);
                    byte a = (byte)((wrd & 0x8000) != 0 ? 255 : 0);
                    byte b = (byte)(((wrd >> 10) & 0x1F) * 255 / 31);
                    byte g = (byte)(((wrd >> 5) & 0x1F) * 255 / 31);
                    byte r = (byte)((wrd & 0x1F) * 255 / 31);

                    bmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
                    ipos += 2;
                }
            }

            return bmp;
        }

        private static Bitmap DecodeA8B8G8R8(Texx tx, byte[] input)
        {
            Logger.Dev(logSrc, $"Converting {tx.Name} using DecodeA8B8G8R8");
            int width = tx.Width;
            int height = tx.Height;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            int ipos = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = input[ipos++];
                    byte g = input[ipos++];
                    byte b = input[ipos++];
                    byte rawA = input[ipos++];
                    // Stretch 0…128 → 0…255:
                    byte a = ScaleAlpha7to8(rawA);

                    bmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
                }
            }

            return bmp;
        }
        private static Bitmap DecodeIndexed(Texx tx, byte[] input)
        {
            Logger.Dev(logSrc, $"Converting {tx.Name} using DecodeIndexed");
            int width = tx.Width;
            int height = tx.Height;

            // Build 256-entry palette:
            var palette = new Color[256];
            int ipos = 0;
            for (int i = 0; i < 256; i++)
            {
                if (tx.Fmt2 == 1)
                {
                    // A1B5G5R5: one‐bit alpha → clamp 0 or 255
                    ushort wrd = (ushort)(input[ipos++] | (input[ipos++] << 8));
                    byte a = (byte)((wrd & 0x8000) != 0 ? 255 : 0);
                    byte b = (byte)(((wrd >> 10) & 0x1F) * 255 / 31);
                    byte g = (byte)(((wrd >> 5) & 0x1F) * 255 / 31);
                    byte r = (byte)((wrd & 0x1F) * 255 / 31);
                    palette[i] = Color.FromArgb(a, r, g, b);
                }
                else if (tx.Fmt2 == 2)
                {
                    // A8B8G8R8 in the palette: rawA ∈ [0..128],
                    // so scale 0..128→0..255
                    byte r = input[ipos++];
                    byte g = input[ipos++];
                    byte b = input[ipos++];
                    byte rawA = input[ipos++];
                    byte a = ScaleAlpha7to8(rawA);

                    palette[i] = Color.FromArgb(a, r, g, b);
                }
                else
                {
                    palette[i] = Color.FromArgb(0, 0, 0, 0);
                }
            }

            // Now decode each pixel’s index:
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte idx = input[ipos++];
                    int sw = (idx & 0xE7) | ((idx >> 1) & 0x08) | ((idx << 1) & 0x10);
                    Color c = palette[sw];
                    bmp.SetPixel(x, y, c);
                }
            }

            return bmp;
        }
        
        private static void SaveAsTga(Bitmap bmp, string outPath)
        {
            int width = bmp.Width;
            int height = bmp.Height;

            // 1) Determine if we actually need alpha.
            bool needsAlpha = false;
            for (int y = 0; y < height && !needsAlpha; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.A < 255)
                    {
                        needsAlpha = true;
                        break;
                    }
                }
            }

            // 2) Write TGA header.
            using var fs = File.Create(outPath);
            using var bw = new BinaryWriter(fs);

            byte imageType = 2; // uncompressed true-color
            byte pixelDepth = (byte)(needsAlpha ? 32 : 24);
            byte descriptor = (byte)(needsAlpha ? 0x08 : 0x00);
            // 0x08 = 8 bits of alpha; origin bottom-left

            // --- 18-byte header ---
            bw.Write((byte)0);          // ID length
            bw.Write((byte)0);          // Color map type
            bw.Write(imageType);        // Image type = 2

            // Color map spec (unused for true-color)
            bw.Write((ushort)0);        // first entry index
            bw.Write((ushort)0);        // color map length
            bw.Write((byte)0);          // color map entry size

            // Image spec
            bw.Write((ushort)0);        // x‐origin
            bw.Write((ushort)0);        // y‐origin
            bw.Write((ushort)width);    // width
            bw.Write((ushort)height);   // height
            bw.Write(pixelDepth);       // pixel depth (24 or 32 bits)
            bw.Write(descriptor);       // image descriptor

            // 3) Write pixel data bottom→top, left→right
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    // TGA expects B, G, R, [A]
                    bw.Write(c.B);
                    bw.Write(c.G);
                    bw.Write(c.R);
                    if (needsAlpha)
                        bw.Write(c.A);
                }
            }
            // no footer needed for uncompressed
        }
        private static byte ScaleAlpha7to8(byte rawA)
        {
            // rawA [0..128], map 0→0 and 128→255
            return (byte)(rawA * 255 / 128);
        }

        private class Texx
        {
            public string Name = "";
            public int Width;
            public int Height;
            public byte Fmt;
            public byte Fmt2;
            public byte Flgs;
            public byte FrmCnt;
            public byte AnimDelay;
            public byte MipCount;
            public byte Unk1;
            public byte Unk2;
            public uint Offset;
            public uint Size;
        }
    }
}
