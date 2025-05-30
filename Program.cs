using redux.exporters;
using redux.parsers;
using redux.utilities;
using System;
using System.IO;

namespace redux
{
	class Program
	{
		private const string Version = "0.2.2";
		private const string logSrc = "REDUX";
		static void Main(string[] args)
		{
			if (args.Length == 1 && (args[0].Equals("-ver", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-help", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)))
			{
				Console.WriteLine();
				Console.WriteLine($"=======================================================");
				Console.WriteLine($"=======================================================");
				Console.WriteLine($"====== RED UX Toolkit by Goober - Version {Version} =======");
				Console.WriteLine($"=======================================================");
				Console.WriteLine($"=======================================================");
				Console.WriteLine();
				Console.WriteLine("Usage:");
				Console.WriteLine("  redux.exe -input <file> -output <file> [options]");
				Console.WriteLine();
				Console.WriteLine("  <bool> options can be set with true|false. If specified with no value provided, they are treated as true.");
				Console.WriteLine();
				Console.WriteLine("Supported input formats:");
				Console.WriteLine("  RF Group (.rfg)         Brushes from non-moving groups");
				Console.WriteLine("  RF1/RF2 Level (.rfl)    Static geometry or brushes from non-moving groups");
				Console.WriteLine("  Wavefront OBJ (.obj)    Geometry objects");
				Console.WriteLine();
				Console.WriteLine("Supported output formats:");
				Console.WriteLine("  RF Group (.rfg)         Brushes with flags (air, portal, detail, etc.)");
				Console.WriteLine("  RF Static Mesh (.v3m)   Mesh with submeshes for each brush");
				Console.WriteLine("  Wavefront OBJ (.obj)    Geometry objects with flags in object names");
				Console.WriteLine();
				Console.WriteLine("Options:");
				Console.WriteLine("  -help, -ver, -h, -v     Show this help message.");
				Console.WriteLine("  -loglevel <level>       Set log message verbosity. Accepts: debug (0), dev (1), info (2), warn (3), error (4). Default: info");
				Console.WriteLine("  -swapitem <class>       If set, overwrite all item classes in exported RFGs with the specified item class.");
				Console.WriteLine("  -ngons <bool>           Allow n-sided polygons. If false, triangulate all polygons. Default: false");
				Console.WriteLine("  -textranslate <bool>    Enable RF2 → RF1 texture name translation. If false, keep original RF2 filenames. Default: false");
				Console.WriteLine("  -brushes <bool>         Export brush data from RFL. If false, exports static geometry. Default: false");
				Console.WriteLine("  -geonodetail <bool>     Remove detail flag from all geoable brushes. Only applies for brushes from RF2 RFLs. Default: false");
				Console.WriteLine("  -portalfaces <bool>     Include portal faces. Only applies for static geometry. Default: false");
				Console.WriteLine("  -detailfaces <bool>     Include faces from detail brushes. Only applies for static geometry. Default: true");
				Console.WriteLine("  -alphafaces <bool>      Include faces with alpha textures. Only applies for static geometry. Default: true");
				Console.WriteLine("  -holefaces <bool>       Include faces with shoot-through alpha textures. Only applies for static geometry. Default: true");
				Console.WriteLine("  -liquidfaces <bool>     Include liquid surfaces. Only applies for static geometry. Default: false");
				Console.WriteLine("  -skyfaces <bool>        Include Show Sky faces. Only applies for static geometry. Default: false");
				Console.WriteLine("  -invisiblefaces <bool>  Include invisible faces. Only applies for static geometry. Default: false");

				return;
			}

			string inputFile = null;
			string outputFile = null;

			for (int i = 0; i < args.Length; i++)
			{
				if (args[i].Equals("-input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					inputFile = args[++i];
				}
				else if (args[i].Equals("-output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					outputFile = args[++i];
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
				else if (args[i].Equals("-swapitem", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					Config.ReplacementItemName = args[++i];
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
					else if (IsBoolArg("-textranslate"))
					{
						if (TryGetBoolArg(i, out var val)) i++;
						Config.TranslateRF2Textures = val;
					}
					else if (IsBoolArg("-ngons"))
					{
						if (TryGetBoolArg(i, out var val)) i++;
						Config.TriangulatePolygons = !val;
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

			if (string.IsNullOrEmpty(inputFile) || string.IsNullOrEmpty(outputFile))
			{
				Logger.Error(logSrc, "Usage: redux.exe -input <file> -output <file>");
				return;
			}

			if (inputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFG -> OBJ: {inputFile} -> {outputFile}");
				var mesh = RfgParser.ReadRfg(inputFile);
				ObjExporter.ExportObj(mesh, outputFile);
			}
			else if (inputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFG -> V3M: {inputFile} -> {outputFile}");
				var mesh = RfgParser.ReadRfg(inputFile);
				V3mExporter.ExportV3m(mesh, outputFile);
			}
			else if (inputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFG -> RFG: {inputFile} -> {outputFile}");
				var mesh = RfgParser.ReadRfg(inputFile);
				RfgExporter.ExportRfg(mesh, outputFile);
			}
			else if (inputFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting OBJ -> RFG: {inputFile} -> {outputFile}");
				var mesh = ObjParser.ReadObj(inputFile);
				RfgExporter.ExportRfg(mesh, outputFile);
			}
			else if (inputFile.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFL -> OBJ: {inputFile} -> {outputFile}");
				var mesh = RflParser.ReadRfl(inputFile);
				ObjExporter.ExportObj(mesh, outputFile);
			}
			else if (inputFile.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFL -> RFG: {inputFile} -> {outputFile}");
				var mesh = RflParser.ReadRfl(inputFile);
				RfgExporter.ExportRfg(mesh, outputFile);
			}
			else
			{
				Logger.Error(logSrc, "Unsupported conversion requested, cannot process.");
			}
		}
	}
}
