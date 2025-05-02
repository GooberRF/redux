using System;
using System.IO;

namespace RFGConverter
{
	class Program
	{
		static void Main(string[] args)
		{
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
