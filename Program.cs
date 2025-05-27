using System;
using System.IO;

namespace RFGConverter
{
	class Program
	{
		private const string Version = "0.2.0";
		private const string logSrc = "REDUX";
		static void Main(string[] args)
		{
			if (args.Length == 1 && (args[0].Equals("-ver", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-help", StringComparison.OrdinalIgnoreCase) ||
						 args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)))
			{
				Console.WriteLine($"RED UX Toolkit by Goober - Version {Version}");
				Console.WriteLine("Usage:");
				Console.WriteLine("  redux.exe -input <file> -output <file> [options]");
				Console.WriteLine();
				Console.WriteLine("Options:");
				Console.WriteLine("  -help, -h               Show this help message.");
				Console.WriteLine("  -loglevel <level>       Set verbosity level.");
				Console.WriteLine("                          Accepts: debug (0), info (1), warn (2), error (3)");
				Console.WriteLine("                          Defaults to 'info'");
				Console.WriteLine();
				Console.WriteLine("Only available during RFL->OBJ conversion:");
				Console.WriteLine("  -portalfaces <bool>     Include portal faces. Default: false");
				Console.WriteLine("  -detailfaces <bool>     Include faces from detail brushes. Default: true");
				Console.WriteLine("  -alphafaces <bool>      Include faces with alpha textures. Default: true");
				Console.WriteLine("  -holefaces <bool>       Include faces with shoot-through alpha textures. Default: true");
				Console.WriteLine("  -liquidfaces <bool>     Include liquid surfaces. Default: false");
				Console.WriteLine("  -skyfaces <bool>        Include Show Sky faces. Default: false");
				Console.WriteLine("  -invisiblefaces <bool>  Include invisible faces. Default: false");

				return;
			}

			string inputFile = null;
			string outputFile = null;

			for (int i = 0; i < args.Length; i++)
			{
				if (args[i].Equals("-input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					inputFile = args[i + 1];
					i++;
				}
				else if (args[i].Equals("-output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					outputFile = args[i + 1];
					i++;
				}
				else if (args[i].Equals("-loglevel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					string levelStr = args[i + 1].ToLowerInvariant();
					Config.Verbosity = levelStr switch
					{
						"debug" or "0" => Config.LogLevel.Debug,
						"info" or "1" => Config.LogLevel.Info,
						"warn" or "2" => Config.LogLevel.Warn,
						"error" or "3" => Config.LogLevel.Error,
						_ => Config.LogLevel.Info
					};
					i++;
				}
				else if (i + 1 < args.Length)
				{
					string val = args[i + 1].ToLowerInvariant();
					bool IsTrue(string v) => v == "1" || v == "true";
					bool IsFalse(string v) => v == "0" || v == "false";

					if (args[i].Equals("-portalfaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludePortalFaces = IsTrue(val);
					else if (args[i].Equals("-detailfaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeDetailFaces = IsTrue(val);
					else if (args[i].Equals("-alphafaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeAlphaFaces = IsTrue(val);
					else if (args[i].Equals("-holefaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeHoleFaces = IsTrue(val);
					else if (args[i].Equals("-liquidfaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeLiquidFaces = IsTrue(val);
					else if (args[i].Equals("-skyfaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeSkyFaces = IsTrue(val);
					else if (args[i].Equals("-invisiblefaces", StringComparison.OrdinalIgnoreCase))
						Config.IncludeInvisibleFaces = IsTrue(val);
					else
						continue;

					i++; // Skip next arg (the value)
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
			else if (inputFile.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
			{
				Logger.Info(logSrc, $"Converting RFL -> OBJ: {inputFile} -> {outputFile}");
				var mesh = RflParser.ReadRfl(inputFile);
				ObjExporter.ExportObj(mesh, outputFile);
			}
			else
			{
				Logger.Error(logSrc, "Unsupported conversion requested, cannot process.");
			}
		}
	}
}
