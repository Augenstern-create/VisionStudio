using Emgu.CV;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args.Length == 0) return Usage();
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        switch (args[0].ToLowerInvariant())
        {
            case "new" when args.Length >= 3:
            {
                var service = new ProjectService();
                var project = await service.CreateAsync(args[1], args[2]);
                var workflow = CreateStarterWorkflow(registry);
                await service.SaveWorkflowAsync(project, workflow, createSnapshot: false);
                Console.WriteLine(project.RootPath);
                return 0;
            }
            case "validate" when args.Length >= 2:
            {
                var workflow = await LoadAsync(args[1]);
                var validation = WorkflowValidator.Validate(workflow, registry);
                if (!validation.IsValid)
                {
                    foreach (var error in validation.Errors) Console.Error.WriteLine(error);
                    return 2;
                }
                Console.WriteLine($"Valid: {validation.TopologicalOrder.Count} nodes.");
                return 0;
            }
            case "export" when args.Length >= 3:
            {
                var workflow = await LoadAsync(args[1]);
                var code = new CSharpWorkflowGenerator(registry).Generate(
                    workflow,
                    targetNodeId: args.Length >= 4 ? args[3] : null);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                await File.WriteAllTextAsync(args[2], code);
                Console.WriteLine(Path.GetFullPath(args[2]));
                return 0;
            }
            case "run" when args.Length >= 2:
            {
                var workflowPath = Path.GetFullPath(args[1]);
                var workflow = await LoadAsync(workflowPath);
                var projectRoot = Directory.GetParent(Path.GetDirectoryName(workflowPath)!)?.FullName
                    ?? throw new InvalidOperationException("Workflow must be under a project workflows directory.");
                string? targetNodeId = args.Length >= 4 ? args[3] : null;
                using var result = await new WorkflowExecutor(registry, projectRoot)
                    .ExecuteAsync(workflow, targetNodeId: targetNodeId);
                var last = targetNodeId is null
                    ? WorkflowValidator.Validate(workflow, registry).TopologicalOrder.Last()
                    : workflow.Nodes.Single(node => node.Id == targetNodeId);
                var image = result.GetOutput<Mat>(last.Id, "Image");
                if (args.Length >= 3)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                    OpenCvFileCodec.Write(args[2], image);
                }
                Console.WriteLine($"Completed {result.Timings.Count} nodes ({result.Timings.Count(x => x.FromCache)} cached) in {result.Timings.Sum(x => x.Duration.TotalMilliseconds):F2} ms.");
                return 0;
            }
            default:
                return Usage();
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("VisionStudio.Cli new <project-dir> <name>");
    Console.Error.WriteLine("VisionStudio.Cli validate <workflow.vflow>");
    Console.Error.WriteLine("VisionStudio.Cli export <workflow.vflow> <output.cs> [target-node-id]");
    Console.Error.WriteLine("VisionStudio.Cli run <workflow.vflow> [output.png] [target-node-id]");
    return 64;
}

static async Task<WorkflowDocument> LoadAsync(string path) =>
    WorkflowJson.Deserialize(await File.ReadAllTextAsync(path));

static WorkflowDocument CreateStarterWorkflow(OperatorRegistry registry)
{
    WorkflowNode Add(string type, double x)
    {
        registry.TryGetDescriptor(type, out var descriptor);
        return OpenCvOperatorCatalog.CreateNode(descriptor, x, 120);
    }
    var load = Add("Image.Load", 40);
    var gray = Add("Image.Gray", 260);
    var threshold = Add("Image.Threshold", 480);
    var save = Add("Image.Save", 700);
    return new WorkflowDocument
    {
        Nodes = { load, gray, threshold, save },
        Connections =
        {
            new(load.Id, "Image", gray.Id, "Image"),
            new(gray.Id, "Image", threshold.Id, "Image"),
            new(threshold.Id, "Image", save.Id, "Image")
        }
    };
}
