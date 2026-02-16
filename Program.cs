using redux.exporters;
using redux.parsers;
using redux.utilities;
using System;
using System.IO;

namespace redux
{
    class Program
    {
        private const string Version = "0.2.6";
        private const string logSrc = "REDUX";
        static void Main(string[] args)
        {
            if (args.Length == 1 && (
                    args[0].Equals("-ver", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("-help", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)))
            {
                ShowHelp();
                return;
            }

            string inputFile = null;
            string outFormatArg = null;
            string skeletonFile = null;
            string animationFile = null;
            string animationName = null;

            // Parse arguments:
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].Equals("-input", StringComparison.OrdinalIgnoreCase) ||
                     args[i].Equals("-i", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < args.Length)
                {
                    inputFile = args[++i];
                }
                else if ((args[i].Equals("-outformat", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals("-out", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals("-format", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals("-output", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < args.Length)
                {
                    outFormatArg = args[++i];
                }
                else if (args[i].Equals("-loglevel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string levelStr = args[++i].ToLowerInvariant();
                    Config.Verbosity = levelStr switch
                    {
                        "debug" or "0" => Config.LogLevel.Debug,
                        "dev" or "1" => Config.LogLevel.Dev,
                        "info" or "2" => Config.LogLevel.Info,
                        "warn" or "3" => Config.LogLevel.Warn,
                        "error" or "4" => Config.LogLevel.Error,
                        _ => Config.LogLevel.Info
                    };
                }
                else if (args[i].Equals("-geomirror", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string axis = args[++i].Trim().ToUpperInvariant();
                    Config.GeoMirror = axis switch
                    {
                        "X" => Config.MirrorAxis.X,
                        "Y" => Config.MirrorAxis.Y,
                        "Z" => Config.MirrorAxis.Z,
                        _ => Config.MirrorAxis.None
                    };
                    if (Config.GeoMirror == Config.MirrorAxis.None)
                        Logger.Warn(logSrc, $"Unknown axis '{axis}' for -geomirror. Valid: X, Y, Z.");
                }
                else if (args[i].Equals("-itemswap", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    Config.ReplacementItemName = args[++i];
                }
                else if (args[i].Equals("-coronaswap", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    Config.CoronaClutterName = args[++i];
                }
                else if ((args[i].Equals("-skeleton", StringComparison.OrdinalIgnoreCase) ||
                          args[i].Equals("-skel", StringComparison.OrdinalIgnoreCase)) &&
                         i + 1 < args.Length)
                {
                    skeletonFile = args[++i];
                }
                else if ((args[i].Equals("-anim", StringComparison.OrdinalIgnoreCase) ||
                          args[i].Equals("-animation", StringComparison.OrdinalIgnoreCase)) &&
                         i + 1 < args.Length)
                {
                    animationFile = args[++i];
                }
                else if (args[i].Equals("-animname", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    animationName = args[++i];
                }
                else
                {
                    bool IsBoolArg(string name) =>
                        args[i].Equals(name, StringComparison.OrdinalIgnoreCase);

                    bool TryGetBoolArg(int index, out bool value)
                    {
                        if (index + 1 < args.Length)
                        {
                            string val = args[index + 1].ToLowerInvariant();
                            if (val == "1" || val == "true") { value = true; return true; }
                            if (val == "0" || val == "false") { value = false; return true; }
                        }
                        value = true; // default to true if no explicit value
                        return false;
                    }

                    if (IsBoolArg("-brushes"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.ParseBrushSectionInstead = val;
                    }
                    else if (IsBoolArg("-simplenames"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.SimpleBrushNames = val;
                    }
                    else if (IsBoolArg("-textranslate"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.TranslateRF2Textures = val;
                    }
                    else if (IsBoolArg("-texprefix"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.InsertRF2TexturePrefix = val;
                    }
                    else if (IsBoolArg("-ngons"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.TriangulatePolygons = !val;
                    }
                    else if (IsBoolArg("-flipnormals"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.FlipNormals = !val;
                    }
                    else if (IsBoolArg("-geonodetail"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.SetRF2GeoableNonDetail = val;
                    }
                    else if (IsBoolArg("-portalfaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludePortalFaces = val;
                    }
                    else if (IsBoolArg("-detailfaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeDetailFaces = val;
                    }
                    else if (IsBoolArg("-alphafaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeAlphaFaces = val;
                    }
                    else if (IsBoolArg("-holefaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeHoleFaces = val;
                    }
                    else if (IsBoolArg("-liquidfaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeLiquidFaces = val;
                    }
                    else if (IsBoolArg("-skyfaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeSkyFaces = val;
                    }
                    else if (IsBoolArg("-invisiblefaces"))
                    {
                        if (TryGetBoolArg(i, out var val)) i++;
                        Config.IncludeInvisibleFaces = val;
                    }
                }
            }

            if (string.IsNullOrEmpty(inputFile) || string.IsNullOrEmpty(outFormatArg))
            {
                Logger.Error(logSrc, "Usage: redux.exe -input <file> -outformat <fmt> [options]");
                return;
            }

            if (!File.Exists(inputFile))
            {
                Logger.Error(logSrc, $"Input file not found: {inputFile}");
                return;
            }

            // Normalize extensions/formats
            string inExt = Path.GetExtension(inputFile).ToLowerInvariant();
            if (inExt.Length == 0)
            {
                Logger.Error(logSrc, "Input file has no extension; cannot determine format.");
                return;
            }

            // Build a canonical “desired extension” (with leading dot) from outFormatArg:
            outFormatArg = outFormatArg.ToLowerInvariant();
            string desiredExt;
            bool extractTextures = false; // used for tga/png

            switch (outFormatArg)
            {
                case "peg":
                    desiredExt = ".peg";
                    break;
                case "rfg":
                    desiredExt = ".rfg";
                    break;
                case "v3m":
                    desiredExt = ".v3m";
                    break;
                case "v3c":
                    desiredExt = ".v3c";
                    break;
                case "obj":
                    desiredExt = ".obj";
                    break;
                case "gltf":
                    desiredExt = ".gltf";
                    break;
                case "rfa":
                    desiredExt = ".rfa";
                    break;
                case "tga":
                    Config.ExportImageFormat = Config.ImageFormat.tga;
                    extractTextures = true;
                    desiredExt = outFormatArg == "png" ? ".png" : ".tga";
                    break;
                case "png":
                    extractTextures = true;
                    desiredExt = outFormatArg == "png" ? ".png" : ".tga";
                    break;
                default:
                    Logger.Error(logSrc, $"Unknown outformat “{outFormatArg}”. Valid: peg, rfg, v3m, v3c, obj, gltf, rfa, tga, png.");
                    return;
            }

            // Construct the output filename by replacing the input’s extension with desiredExt
            string inputFolder = Path.GetDirectoryName(inputFile) ?? "";
            string inputBase = Path.GetFileNameWithoutExtension(inputFile);
            string outputFile = Path.Combine(inputFolder, inputBase + desiredExt);

            Logger.Info(logSrc, $"Converting {inExt} → {desiredExt}: {inputFile} → {outputFile}");

            // Dispatch based on (input extension, desired format)
            switch (inExt)
            {
                case ".peg":
                    if (extractTextures)
                    {
                        PegParser.ExtractPeg(inputFile);
                    }
                    else
                    {
                        if (desiredExt is ".rfg" or ".v3m" or ".obj")
                        {
                            Logger.Error(logSrc, $".peg → {desiredExt} is not a valid conversion.");
                        }
                        else
                        {
                            Logger.Warn(logSrc, "Input and output formats are both PEG; nothing to do.");
                        }
                    }
                    break;

                case ".rfg":
                    if (desiredExt == ".rfg")
                    {
                        var mesh = RfgParser.ReadRfg(inputFile);
                        RfgExporter.ExportRfg(mesh, outputFile);
                    }
                    else if (desiredExt == ".obj")
                    {
                        var mesh = RfgParser.ReadRfg(inputFile);
                        ObjExporter.ExportObj(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3m")
                    {
                        var mesh = RfgParser.ReadRfg(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3c")
                    {
                        var mesh = RfgParser.ReadRfg(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile, forceCharacterMesh: true);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        var mesh = RfgParser.ReadRfg(inputFile);
                        RfaFile anim = null;
                        if (!string.IsNullOrWhiteSpace(animationFile))
                            anim = RfaParser.ReadRfa(animationFile);
                        GltfExporter.ExportGltf(mesh, outputFile, anim, animationName ?? Path.GetFileNameWithoutExtension(animationFile));
                    }
                    else
                    {
                        Logger.Error(logSrc, $".rfg → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".v3c":
                case ".v3m":
                    if (desiredExt == ".v3m")
                    {
                        var mesh = V3mParser.ReadV3mAsRflMesh(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3c")
                    {
                        var mesh = V3mParser.ReadV3mAsRflMesh(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile, forceCharacterMesh: true);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        var mesh = V3mParser.ReadV3mAsRflMesh(inputFile);
                        RfaFile anim = null;
                        if (!string.IsNullOrWhiteSpace(animationFile))
                            anim = RfaParser.ReadRfa(animationFile);
                        GltfExporter.ExportGltf(mesh, outputFile, anim, animationName ?? Path.GetFileNameWithoutExtension(animationFile));
                    }
                    else if (desiredExt == ".obj")
                    {
                        var mesh = V3mParser.ReadV3mAsRflMesh(inputFile);
                        ObjExporter.ExportObj(mesh, outputFile);
                    }
                    else if (desiredExt == ".rfg")
                    {
                        var mesh = V3mParser.ReadV3mAsRflMesh(inputFile);
                        RfgExporter.ExportRfg(mesh, outputFile);
                    }
                    else
                    {
                        Logger.Error(logSrc, $".v3m → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".rfc":
                case ".rfm":
                    if (desiredExt == ".v3m")
                    {
                        var mesh = RfmParser.ReadRfm(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3c")
                    {
                        var mesh = RfmParser.ReadRfm(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile, forceCharacterMesh: true);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        var mesh = RfmParser.ReadRfm(inputFile);
                        RfaFile anim = null;
                        if (!string.IsNullOrWhiteSpace(animationFile))
                            anim = RfaParser.ReadRfa(animationFile);
                        GltfExporter.ExportGltf(mesh, outputFile, anim, animationName ?? Path.GetFileNameWithoutExtension(animationFile));
                    }
                    else if (desiredExt == ".obj")
                    {
                        var mesh = RfmParser.ReadRfm(inputFile);
                        ObjExporter.ExportObj(mesh, outputFile);
                    }
                    else if (desiredExt == ".rfg")
                    {
                        var mesh = RfmParser.ReadRfm(inputFile);
                        RfgExporter.ExportRfg(mesh, outputFile);
                    }
                    else
                    {
                        Logger.Error(logSrc, $".rfm → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".obj":
                    if (desiredExt == ".obj")
                    {
                        var mesh = ObjParser.ReadObj(inputFile);
                        ObjExporter.ExportObj(mesh, outputFile);
                    }
                    else if (desiredExt == ".rfg")
                    {
                        var mesh = ObjParser.ReadObj(inputFile);
                        RfgExporter.ExportRfg(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3m")
                    {
                        var mesh = ObjParser.ReadObj(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3c")
                    {
                        var mesh = ObjParser.ReadObj(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile, forceCharacterMesh: true);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        var mesh = ObjParser.ReadObj(inputFile);
                        GltfExporter.ExportGltf(mesh, outputFile, null, null);
                    }
                    else
                    {
                        Logger.Error(logSrc, $".obj → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".rfl":
                    if (desiredExt == ".obj")
                    {
                        var mesh = RflParser.ReadRfl(inputFile);
                        ObjExporter.ExportObj(mesh, outputFile);
                    }
                    else if (desiredExt == ".rfg")
                    {
                        var mesh = RflParser.ReadRfl(inputFile);
                        RfgExporter.ExportRfg(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3m")
                    {
                        var mesh = RflParser.ReadRfl(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile);
                    }
                    else if (desiredExt == ".v3c")
                    {
                        var mesh = RflParser.ReadRfl(inputFile);
                        V3mExporter.ExportV3m(mesh, outputFile, forceCharacterMesh: true);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        var mesh = RflParser.ReadRfl(inputFile);
                        GltfExporter.ExportGltf(mesh, outputFile, null, null);
                    }
                    else
                    {
                        Logger.Error(logSrc, $".rfl → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".rfa":
                    if (desiredExt == ".rfa")
                    {
                        var anim = RfaParser.ReadRfa(inputFile);
                        RfaExporter.ExportRfa(anim, outputFile);
                    }
                    else if (desiredExt == ".gltf")
                    {
                        if (string.IsNullOrWhiteSpace(skeletonFile) || !File.Exists(skeletonFile))
                        {
                            Logger.Error(logSrc, "Converting .rfa to .gltf requires -skeleton <v3c/v3m path>.");
                            break;
                        }

                        var mesh = V3mParser.ReadV3mAsRflMesh(skeletonFile);
                        var anim = RfaParser.ReadRfa(inputFile);
                        GltfExporter.ExportGltf(mesh, outputFile, anim, animationName ?? Path.GetFileNameWithoutExtension(inputFile));
                    }
                    else
                    {
                        Logger.Error(logSrc, $".rfa → {desiredExt} is not a valid conversion.");
                    }
                    break;

                case ".gltf":
                    {
                        var imported = GltfParser.ReadGltf(inputFile);
                        if (desiredExt == ".v3m")
                        {
                            V3mExporter.ExportV3m(imported.Mesh, outputFile);
                        }
                        else if (desiredExt == ".v3c")
                        {
                            V3mExporter.ExportV3m(imported.Mesh, outputFile, forceCharacterMesh: true);
                        }
                        else if (desiredExt == ".obj")
                        {
                            ObjExporter.ExportObj(imported.Mesh, outputFile);
                        }
                        else if (desiredExt == ".rfg")
                        {
                            RfgExporter.ExportRfg(imported.Mesh, outputFile);
                        }
                        else if (desiredExt == ".rfa")
                        {
                            if (imported.Animation == null)
                            {
                                Logger.Error(logSrc, "No animation track found in glTF; cannot export .rfa.");
                            }
                            else
                            {
                                RfaExporter.ExportRfa(imported.Animation, outputFile);
                            }
                        }
                        else if (desiredExt == ".gltf")
                        {
                            GltfExporter.ExportGltf(imported.Mesh, outputFile, imported.Animation, animationName ?? Path.GetFileNameWithoutExtension(inputFile));
                        }
                        else
                        {
                            Logger.Error(logSrc, $".gltf → {desiredExt} is not a valid conversion.");
                        }
                        break;
                    }

                default:
                    Logger.Error(logSrc, $"Unsupported input format “{inExt}.”");
                    break;
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine($"=======================================================");
            Console.WriteLine($"=======================================================");
            Console.WriteLine($"====== RED UX Toolkit by Goober - Version {Version} =======");
            Console.WriteLine($"=======================================================");
            Console.WriteLine($"=======================================================");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  redux.exe -input <file> -outformat <fmt> [options]");
            Console.WriteLine();
            Console.WriteLine("Supported input formats:");
            Console.WriteLine("  .rfl    (RF1/RF2 level)");
            Console.WriteLine("  .rfg    (RF group)");
            Console.WriteLine("  .v3m    (RF static mesh)");
            Console.WriteLine("  .v3c    (RF character mesh)");
            Console.WriteLine("  .obj    (Wavefront OBJ)");
            Console.WriteLine("  .gltf   (glTF 2.0)");
            Console.WriteLine("  .peg    (RF2 texture packfile)");
            Console.WriteLine("  .rfa    (RF animation)");
            Console.WriteLine();
            Console.WriteLine("Supported output formats (specify with -outformat):");
            Console.WriteLine("  rfg     (RF group)");
            Console.WriteLine("  v3m     (RF static mesh)");
            Console.WriteLine("  v3c     (RF character mesh)");
            Console.WriteLine("  obj     (Wavefront OBJ)");
            Console.WriteLine("  gltf    (glTF 2.0)");
            Console.WriteLine("  rfa     (RF animation)");
            Console.WriteLine("  tga     (extract textures from a .peg into .tga)");
            Console.WriteLine("  png     (extract textures from a .peg into .png)");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  redux.exe -input dm02.rfl -outformat obj");
            Console.WriteLine("  redux.exe -input dmc11.peg -outformat png");
            Console.WriteLine();
            Console.WriteLine("Other options (boolean flags):");
            Console.WriteLine("  -loglevel <debug|dev|info|warn|error> - Set logging verbosity level (default info)");
            Console.WriteLine("  -swapitem <class> - Replace all items in exported .rfg with the specified class");
            Console.WriteLine("  -skeleton <file> - Skeleton mesh (.v3c/.v3m) when converting .rfa to .gltf");
            Console.WriteLine("  -anim <file> - Embed .rfa animation when exporting .gltf");
            Console.WriteLine("  -animname <name> - Override embedded animation clip name");
            Console.WriteLine("  -brushes <true|false> - Export brush data from .rfl instead of static geometry (default false)");
            Console.WriteLine("  -ngons <true|false> - Allow n-sided polygons in output (default false)");
            Console.WriteLine("  -flipnormals <true|false> - Flip face normals during conversion (default false)");
            Console.WriteLine("  -geomirror <X|Y|Z> - Mirror geometry across the given global axis (RFG export only)");
            Console.WriteLine("  -simplenames <true|false> - Use simple Brush_UID names in exports (default false)");
            Console.WriteLine("  -textranslate <true|false> - RF2 -> RF1 texture name translation (default false)");
            Console.WriteLine("  -geonodetail <true|false> - Remove detail flag from RF2 geoable brushes (default false)");
            Console.WriteLine("  -portalfaces <true|false> - Include portal faces (default true)");
            Console.WriteLine("  -detailfaces <true|false> - Include detail faces (default true)");
            Console.WriteLine("  -alphafaces <true|false> - Include faces with alpha textures (default true)");
            Console.WriteLine("  -holefaces <true|false> - Include faces with shoot-through alpha textures (default true)");
            Console.WriteLine("  -liquidfaces <true|false> - Include liquid faces (default false)");
            Console.WriteLine("  -skyfaces <true|false> - Include show sky faces (default true)");
            Console.WriteLine("  -invisiblefaces <true|false> - Include invisible faces (default true)");
            Console.WriteLine("  -ver / -help - Print version/usage/help information");
            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine("  – <bool> defaults to true if no explicit value is given.");
            Console.WriteLine("  - <class> references class names from in the corresponding .tbl file. Use quotation marks if it has spaces.");
            Console.WriteLine("  – If you supply “-input dmc11.peg -outformat png,” textures will be extracted as .png files in a folder named “dmc11/”.");
        }
    }
}
