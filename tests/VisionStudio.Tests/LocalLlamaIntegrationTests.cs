using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;
using System.Security.Cryptography;

namespace VisionStudio.Tests;

public sealed class LocalLlamaIntegrationTests
{
    [Fact]
    [Trait("Dimension", "LocalAi")]
    public async Task InstalledQwenModel_PlansAndRunsComposedCrackWorkflow()
    {
        if (Environment.GetEnvironmentVariable("VISIONSTUDIO_RUN_LOCAL_AI") != "1") return;
        var model = RequireEnvironment("VISIONSTUDIO_AI_MODEL");
        var server = RequireEnvironment("VISIONSTUDIO_LLAMA_SERVER");
        var mmprojSetting = Environment.GetEnvironmentVariable("VISIONSTUDIO_AI_MMPROJ");
        var mmproj = string.IsNullOrWhiteSpace(mmprojSetting) ? null : RequireEnvironment("VISIONSTUDIO_AI_MMPROJ");
        var fixtureRoot = Environment.GetEnvironmentVariable("VISIONSTUDIO_AI_TEST_PROJECT");
        var fixtureImage = string.IsNullOrWhiteSpace(fixtureRoot)
            ? FindRepositoryFile("tests", "image", "1.png")
            : Path.Combine(fixtureRoot, "images", "input", "1.png");
        if (!File.Exists(fixtureImage)) throw new FileNotFoundException("AI test project input image does not exist.", fixtureImage);
        var fixtureHashes = string.IsNullOrWhiteSpace(fixtureRoot) ? null : HashProjectFixture(fixtureRoot);
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "Local AI Integration");
        var input = Path.Combine(project.RootPath, "images", "input", "1.png");
        File.Copy(fixtureImage, input, overwrite: true);
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var workflow = new WorkflowDocument { Nodes = { load } };
        await projects.SaveWorkflowAsync(project, workflow);
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        var profile = new AiModelProfile(
            "qwen-local", "Qwen2.5-VL-3B", model, mmproj, "", ContextSize: 8192, MaximumOutputTokens: 2048,
            Source: AiModelSource.Local, ModelId: "qwen2.5-vl-3b-instruct");
        var profileStore = new AiModelProfileStore(
            Path.Combine(temp.Path, "config", "ai-local-models.json"),
            Path.Combine(temp.Path, "config", "ai-remote-models.json"));
        await profileStore.SaveAsync([profile]);
        var configuredProfile = Assert.Single(await profileStore.LoadAsync());
        var previousServerPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH");
        Environment.SetEnvironmentVariable("LLAMA_SERVER_PATH", server);
        try
        {
            await using var provider = await new AiProviderFactory().CreateAsync(configuredProfile, session, Path.Combine(temp.Path, "config"), "2.0.0");
            var trackingProvider = new ResponseTrackingProvider(provider.Provider);
            var response = await new AiWorkflowAgent(session, trackingProvider).ProcessAsync(new(
                "我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度", "load", input, AiAssistantMode.Automatic));

            Assert.True(response.Execution?.Success, response.Execution?.Error ?? response.Message);
            if (mmproj is not null) Assert.True(trackingProvider.LastResponse?.VisionUsed, "The multimodal model did not consume the supplied test image.");
            Assert.Contains(response.Execution!.Outputs, output => output.Port == "Count");
            Assert.Contains(response.Execution.Outputs, output => output.Port == "PixelLength");
            var finalWorkflow = (await session.GetSnapshotAsync()).Workflow;
            Assert.DoesNotContain(finalWorkflow.Nodes, node => node.Type == "Defect.CrackMeasure");
            Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Threshold.Otsu");
            Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Blob.Contours");
            Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Contour.PathMetrics");
            Console.WriteLine("AI plan operators: " + string.Join(" -> ", finalWorkflow.Nodes.Select(node => node.Type)));
            Console.WriteLine("AI vision input used: " + (trackingProvider.LastResponse?.VisionUsed ?? false));
            Console.WriteLine("AI execution outputs: " + string.Join("; ", response.Execution.Outputs.Select(output => $"{output.Port}={output.Summary}")));
            if (fixtureHashes is not null)
            {
                var afterHashes = HashProjectFixture(fixtureRoot!);
                Assert.Equal(fixtureHashes.Count, afterHashes.Count);
                foreach (var pair in fixtureHashes) Assert.Equal(pair.Value, afterHashes[pair.Key]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMA_SERVER_PATH", previousServerPath);
        }
    }

    private static IReadOnlyDictionary<string, string> HashProjectFixture(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".vstudio", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".vflow", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) throw new FileNotFoundException($"{name} does not point to a file.", value);
        return value;
    }

    private sealed class ResponseTrackingProvider(IAiProvider inner) : IAiProvider
    {
        public string ProviderId => inner.ProviderId;
        public AiModelProfile Profile => inner.Profile;
        public bool SupportsVision => inner.SupportsVision;
        public AiProviderResponse? LastResponse { get; private set; }
        public async Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
        {
            LastResponse = await inner.SendAsync(request, cancellationToken);
            return LastResponse;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
