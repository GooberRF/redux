using System;
using System.IO;

namespace RFGConverter
{
	class Program
	{
		private const string Version = "0.0.1";
		static void Main(string[] args)
		{
			if (args.Length == 1 && (args[0] == "-ver" || args[0] == "--version" || args[0] == "-v" || args[0] == "-help" || args[0] == "-h"))
			{
				Console.WriteLine($"RED UX Toolkit by Goober - Version {Version}");
				Console.WriteLine("Usage: redux.exe -input <file> -output <file>");
				return;
			}

			string inputFile = null;
			string outputFile = null;

			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "-input" && i + 1 < args.Length)
					inputFile = args[i + 1];
				if (args[i] == "-output" && i + 1 < args.Length)
					outputFile = args[i + 1];
			}

			if (string.IsNullOrEmpty(inputFile) || string.IsNullOrEmpty(outputFile))
			{
				Console.WriteLine("Usage: redux.exe -input <file> -output <file>");
				return;
			}

			if (inputFile.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase) && outputFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"Converting RFG -> OBJ: {inputFile} -> {outputFile}");
				var mesh = RfgParser.ReadRfg(inputFile);
				ObjExporter.ExportObj(mesh, outputFile);
			}
			else
			{
				Console.WriteLine("Unsupported conversion requested, cannot process.");
			}
		}
	}
}
