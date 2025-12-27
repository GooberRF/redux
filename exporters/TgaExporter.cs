using redux.utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace redux.exporters
{
    public static class TgaExporter
    {
        private const string logSrc = "TgaExporter";
        public static void Write24BitTga(string outputPath, Lightmap lm)
        {
            Logger.Dev(logSrc, $"Starting TGA export: \"{outputPath}\" (Dimensions: {lm.Width}×{lm.Height})");

            using var fs = File.OpenWrite(outputPath);
            using var bw = new BinaryWriter(fs);

            // 1) Header: 18 bytes
            Logger.Dev(logSrc, "Writing TGA header...");
            bw.Write((byte)0);            // 0: no ID field
            bw.Write((byte)0);            // 1: no color map
            bw.Write((byte)2);            // 2: uncompressed true‐color image
            bw.Write((short)0);           // 3–4: color map origin
            bw.Write((short)0);           // 5–6: color map length
            bw.Write((byte)0);            // 7: color map depth
            bw.Write((short)0);           // 8–9: x‐origin (low, high)
            bw.Write((short)0);           // 10–11: y‐origin
            bw.Write((short)lm.Width);    // 12–13: width
            bw.Write((short)lm.Height);   // 14–15: height
            bw.Write((byte)24);           // 16: pixel depth (24 bits)
            bw.Write((byte)0);            // 17: image descriptor (no alpha, origin bottom‐left)
            Logger.Dev(logSrc, "Header written.");

            // 2) Pixel data: bottom row first, BGR order
            int w = lm.Width, h = lm.Height;
            byte[] data = lm.PixelData; // assumed RGB row-major from top‐left
            Logger.Dev(logSrc, $"Writing pixel data ({w * h * 3} bytes total)…");

            for (int row = h - 1; row >= 0; row--)
            {
                int rowStart = row * (w * 3);
                for (int col = 0; col < w; col++)
                {
                    int pixelIndex = rowStart + col * 3;
                    byte r = data[pixelIndex + 0];
                    byte g = data[pixelIndex + 1];
                    byte b = data[pixelIndex + 2];
                    // Write in BGR order
                    bw.Write(b);
                    bw.Write(g);
                    bw.Write(r);
                }
            }
            Logger.Dev(logSrc, "Pixel data written.");

            // 3) No footer/trailer needed for a simple TGA
            bw.Flush();
            Logger.Dev(logSrc, $"Finished TGA export: \"{outputPath}\"");
        }
    }
}
