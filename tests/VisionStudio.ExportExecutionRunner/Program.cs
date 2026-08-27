#if HAS_GENERATED_SOURCE
if (args.Length != 1)
    throw new ArgumentException("Expected the Vision Studio project root as the only argument.");

using var result = new GeneratedVisionAlgorithm().Process(Path.GetFullPath(args[0]));
if (result.IsEmpty)
    throw new InvalidOperationException("The exported workflow returned an empty image.");

Console.WriteLine($"Exported C# executed successfully: {result.Width}x{result.Height}, {result.NumberOfChannels} channel(s).");
#else
Console.WriteLine("Pass -p:GeneratedSource=<GeneratedVisionAlgorithm.cs> to compile and execute an exported source file.");
#endif
