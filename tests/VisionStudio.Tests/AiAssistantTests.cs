using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Ai.LlamaCpp;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class AiAssistantTests
{
    [Fact]
    public void RawOperatorSearch_FindsAvailableCrackCapabilities()
    {
        var matches = new OperatorSearchService().Search(
            OpenCvOperatorCatalog.CreateRegistry().Descriptors,
            "我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度",
            8);

        Assert.Contains(matches, match => match.Descriptor.Type == "Defect.CrackMeasure");
    }

    [Fact]
    public async Task AgentPrompt_ContainsEveryRuntimeOperator_WithPortsAndParameters()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Complete Catalog Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes = { new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" } }
        };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new CapturingProvider("""
            {"summary":"转换为灰度图","targetNodeId":"gray","actions":[
              {"type":"add_node","nodeId":"gray","operatorId":"Image.Gray"},
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"gray","toPort":"Image"}
            ]}
            """);

        await new AiWorkflowAgent(session, provider).ProcessAsync(new("把输入图片转换为灰度图", "load"));

        Assert.NotNull(provider.LastRequest);
        var request = provider.LastRequest!;
        Assert.Contains("complete runtime catalog", request.SystemPrompt, StringComparison.Ordinal);
        Assert.True(request.Messages.Last().Content.Length < 64_000, "The complete operator prompt must remain compact enough for local model contexts.");
        using var context = JsonDocument.Parse(request.Messages.Last().Content);
        var root = context.RootElement;
        var promptOperators = root.GetProperty("operators").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var expectedIds = registry.Descriptors.Select(descriptor => descriptor.Type).Order(StringComparer.Ordinal).ToArray();
        var actualIds = promptOperators.Select(item => item.Split('=')[0]).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedIds.Length, root.GetProperty("operatorCount").GetInt32());
        Assert.Equal(expectedIds, actualIds);
        var recommended = root.GetProperty("recommendedOperators").EnumerateArray().ToArray();
        Assert.Contains(recommended, item => item.GetProperty("id").GetString() == "Image.Gray");
        Assert.All(recommended, item =>
        {
            Assert.True(item.TryGetProperty("inputs", out _));
            Assert.True(item.TryGetProperty("outputs", out _));
            Assert.True(item.TryGetProperty("parameters", out _));
        });
        Assert.All(promptOperators, item => Assert.Matches(@"^[^=]+=.+$", item));
    }

    [Fact]
    public void PlanParser_AcceptsStrictPlanAndRejectsUntrustedAction()
    {
        var plan = AiWorkflowAgent.ParsePlan("""
            {"summary":"测量裂纹","targetNodeId":"crack","actions":[
              {"type":"add_node","nodeId":"crack","operatorId":"Defect.CrackMeasure"},
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"crack","toPort":"Image"}
            ]}
            """);

        Assert.Equal("crack", plan.TargetNodeId);
        Assert.Throws<InvalidDataException>(() => AiWorkflowAgent.ParsePlan(
            "{\"summary\":\"x\",\"actions\":[]}"));
    }

    [Fact]
    public void PlanParser_WrapsSinglePipelineActionReturnedBySmallModel()
    {
        var plan = AiWorkflowAgent.ParsePlan("""
            {"type":"add_pipeline","fromNode":"load_2","operatorIds":["Image.Gray","Image.GaussianBlur"],"nodeIds":["gray_2","blur_2"]}
            """);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("add_pipeline", action.Type);
        Assert.Equal("load_2", action.FromNode);
        Assert.Equal("blur_2", plan.TargetNodeId);
    }

    [Fact]
    public void PlanParser_NormalizesDependencyOrderFromSmallModels()
    {
        var plan = AiWorkflowAgent.ParsePlan("""
            {"actions":[
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"crack","toPort":"Image"},
              {"type":"add_node","nodeId":"crack","operatorId":"Defect.CrackMeasure"}
            ]}
            """);

        Assert.Equal(["add_node", "connect_nodes"], plan.Actions.Select(action => action.Type));
        Assert.Equal("crack", plan.TargetNodeId);
    }

    [Fact]
    public void PlanParser_SeparatesOperatorIdsFromNodeInstanceIds()
    {
        var plan = AiWorkflowAgent.ParsePlan("""
            {"targetNodeId":"Contour.PathMetrics","actions":[
              {"type":"add_node","nodeId":"Contour.PathMetrics","operatorId":"Contour.PathMetrics"},
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"Contour.PathMetrics","toPort":"Image"}
            ]}
            """);

        var added = Assert.Single(plan.Actions, action => action.Type == "add_node");
        Assert.NotEqual("Contour.PathMetrics", added.NodeId);
        Assert.Equal(added.NodeId, plan.Actions.Single(action => action.Type == "connect_nodes").ToNode);
        Assert.Equal(added.NodeId, plan.TargetNodeId);
    }

    [Fact]
    public async Task LlamaProvider_UsesOpenAiCompatibleLoopbackEndpointWithoutBundledRuntime()
    {
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health" => new(HttpStatusCode.OK),
            "/v1/chat/completions" => new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"ok\\\",\\\"actions\\\":[{\\\"type\\\":\\\"add_node\\\",\\\"nodeId\\\":\\\"x\\\",\\\"operatorId\\\":\\\"Defect.CrackMeasure\\\"}]}\"}}]}", Encoding.UTF8, "application/json")
            },
            _ => new(HttpStatusCode.NotFound)
        });
        var profile = new AiModelProfile("test", "Test", "missing.gguf", null, "missing-server.exe");
        await using var provider = new LlamaServerProvider(profile, handler);

        var response = await provider.SendAsync(new("system", [new("user", "measure")]));

        Assert.Contains("Defect.CrackMeasure", response.Content);
        Assert.False(response.VisionUsed);
        Assert.False(provider.SupportsVision);
    }

    [Fact]
    public async Task Agent_ComposesInspectableCrackPipelineAndExecutesProvidedImage()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Crack Test");
        var input = Path.Combine(project.RootPath, "images", "input", "1.png");
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), input, overwrite: true);
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var workflow = new WorkflowDocument { Nodes = { load } };
        await projects.SaveWorkflowAsync(project, workflow);
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("""
            {"summary":"使用通用 OpenCV 阶段分割细长暗缺陷并统计数量和长度","targetNodeId":"ai_metrics","actions":[
              {"type":"add_pipeline","fromNode":"load","operatorIds":["Image.Gray","Image.GaussianBlur","Morphology.BlackHat","Threshold.Otsu","Morphology.Close","Blob.Contours","Contour.PathMetrics"],"nodeIds":["ai_gray","ai_blur","ai_blackhat","ai_otsu","ai_close","ai_contours","ai_metrics"]},
              {"type":"set_parameter","nodeId":"ai_blackhat","parameter":"kernelSize","value":15},
              {"type":"set_parameter","nodeId":"ai_close","parameter":"kernelSize","value":5},
              {"type":"set_parameter","nodeId":"ai_metrics","parameter":"minArea","value":30},
              {"type":"set_parameter","nodeId":"ai_metrics","parameter":"minLength","value":50}
            ]}
            """);

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度",
            "load",
            input,
            AiAssistantMode.Automatic));

        Assert.True(response.Execution?.Success, response.Execution?.Error);
        Assert.Contains(response.Execution!.Outputs, output => output.Port == "Count");
        Assert.Contains(response.Execution.Outputs, output => output.Port == "PixelLength");
        Assert.DoesNotContain(response.Plan!.Actions, action => action.Type == "add_pipeline");
        var finalWorkflow = (await session.GetSnapshotAsync()).Workflow;
        Assert.DoesNotContain(finalWorkflow.Nodes, node => node.Type == "Defect.CrackMeasure");
        Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Threshold.Otsu");
        Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Blob.Contours");
        Assert.Contains(finalWorkflow.Nodes, node => node.Type == "Contour.PathMetrics");
        var orderedPipeline = new[] { "Image.Gray", "Image.GaussianBlur", "Morphology.BlackHat", "Threshold.Otsu", "Morphology.Close", "Blob.Contours", "Contour.PathMetrics" }
            .Select(type => finalWorkflow.Nodes.Single(node => node.Type == type)).ToList();
        Assert.All(orderedPipeline.Zip(orderedPipeline.Skip(1)), pair => Assert.True(pair.First.X < pair.Second.X));
    }

    [Fact]
    public async Task Agent_DoesNotApplyLastInvalidPlanAfterAllRetriesFail()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Invalid Plan Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/missing.png");
        var workflow = new WorkflowDocument { Nodes = { load } };
        await projects.SaveWorkflowAsync(project, workflow);
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("""
            {"summary":"残缺方案","targetNodeId":"missing","actions":[
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"missing","toPort":"Image"}
            ]}
            """);
        var before = await session.GetSnapshotAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度", "load", Mode: AiAssistantMode.Automatic)));

        var after = await session.GetSnapshotAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Single(after.Workflow.Nodes);
        Assert.Empty(after.Workflow.Connections);
    }

    [Fact]
    public async Task Agent_RepeatedPipelineFromSecondImage_GeneratesUniqueNodeIds()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Repeated Pipeline Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes =
            {
                new() { Id = "load_1", Type = "Image.Load", Name = "加载图像 1" },
                new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" },
                new() { Id = "blur", Type = "Image.GaussianBlur", Name = "高斯模糊" },
                new() { Id = "blackhat", Type = "Morphology.BlackHat", Name = "黑帽" },
                new() { Id = "otsu", Type = "Threshold.Otsu", Name = "大津阈值" },
                new() { Id = "close", Type = "Morphology.Close", Name = "闭运算" },
                new() { Id = "contours", Type = "Blob.Contours", Name = "查找轮廓" },
                new() { Id = "metrics", Type = "Contour.PathMetrics", Name = "路径统计" },
                new() { Id = "load_2", Type = "Image.Load", Name = "加载图像 2" }
            }
        };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("""
            {"type":"add_pipeline","fromNode":"load_1","operatorIds":["Image.Gray","Image.GaussianBlur","Morphology.BlackHat","Threshold.Otsu","Morphology.Close","Blob.Contours","Contour.PathMetrics"],"nodeIds":["gray","blur","blackhat","otsu","close","contours","metrics"]}
            """);

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "[IMG-02] 加载图像，我需要测量裂纹数量与像素长度", "load_2"));

        Assert.NotNull(response.Plan);
        var addedIds = response.Plan!.Actions.Where(action => action.Type == "add_node").Select(action => action.NodeId!).ToArray();
        Assert.Equal(["gray_2", "blur_2", "blackhat_2", "otsu_2", "close_2", "contours_2", "metrics_2"], addedIds);
        Assert.Equal("metrics_2", response.Plan.TargetNodeId);
        Assert.Contains(response.Plan.Actions, action => action.Type == "connect_nodes" && action.FromNode == "load_2" && action.ToNode == "gray_2");
        Assert.Contains("节点与参数：", response.Message);
        Assert.Contains("gray_2", response.Message);
        Assert.Contains("kernelSize=", response.Message);
        Assert.Contains("连接：", response.Message);
    }

    [Fact]
    public async Task Agent_InvalidPlanError_IncludesReasonAndCopyableFullResponse()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Detailed Error Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        var marker = "DETAIL_AFTER_500_CHARACTERS";
        var responseJson = JsonSerializer.Serialize(new
        {
            summary = new string('x', 700) + marker,
            targetNodeId = "missing",
            actions = new[] { new { type = "connect_nodes", fromNode = "load", fromPort = "Image", toNode = "missing", toPort = "Image" } }
        });
        await using var provider = new FixedProvider(responseJson);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "测量裂纹数量与长度", "load")));

        Assert.Contains("Validation error:", exception.Message);
        Assert.Contains(marker, exception.Message);
        Assert.Contains("```json", exception.Message);
    }

    [Theory]
    [InlineData("{\"summary\":\"缺少输入\",\"targetNodeId\":\"gray\",\"actions\":[{\"type\":\"add_node\",\"nodeId\":\"gray\",\"operatorId\":\"Image.Gray\"}]}")]
    [InlineData("{\"summary\":\"重复输入\",\"targetNodeId\":\"gray\",\"actions\":[{\"type\":\"add_node\",\"nodeId\":\"gray\",\"operatorId\":\"Image.Gray\"},{\"type\":\"connect_nodes\",\"fromNode\":\"load\",\"fromPort\":\"Image\",\"toNode\":\"gray\",\"toPort\":\"Image\"},{\"type\":\"connect_nodes\",\"fromNode\":\"load\",\"fromPort\":\"Image\",\"toNode\":\"gray\",\"toPort\":\"Image\"}]}")]
    public async Task Agent_RejectsMissingOrMultiplyConnectedRequiredInputs(string invalidPlan)
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Connection Validation");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/missing.png");
        var workflow = new WorkflowDocument { Nodes = { load } };
        await projects.SaveWorkflowAsync(project, workflow);
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider(invalidPlan);

        await Assert.ThrowsAsync<InvalidDataException>(() => new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "把加载的图片转换为灰度图", "load", Mode: AiAssistantMode.Automatic)));

        var after = await session.GetSnapshotAsync();
        Assert.Equal(0, after.Revision);
        Assert.Single(after.Workflow.Nodes);
        Assert.Empty(after.Workflow.Connections);
    }

    [Fact]
    public async Task ProfileStore_RoundTripsExternalPathsWithoutCopyingPayload()
    {
        using var temp = new TemporaryDirectory();
        var store = new AiModelProfileStore(Path.Combine(temp.Path, "local.json"), Path.Combine(temp.Path, "remote.json"), secretProtector: new TestSecretProtector());
        var profile = new AiModelProfile("qwen", "Qwen", "E:\\models\\model.gguf", null, "D:\\Programs\\llama\\llama-server.exe");
        await store.SaveAsync([profile]);

        Assert.Equal(profile, Assert.Single(await store.LoadAsync()));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);
    }

    [Fact]
    public void DefaultAiConfiguration_IsSoftwareLevelNotProjectLevel()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config", "ai-local-models.json"), AiSoftwarePaths.LocalProfilesFile);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config", "ai-remote-models.json"), AiSoftwarePaths.RemoteProfilesFile);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config", "ai-contexts.json"), AiSoftwarePaths.ContextsFile);
        Assert.DoesNotContain(".project", AiSoftwarePaths.LocalProfilesFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileStore_SeparatesLocalAndRemoteAndEncryptsRemoteApiKey()
    {
        using var temp = new TemporaryDirectory();
        var localPath = Path.Combine(temp.Path, "ai-local-models.json");
        var remotePath = Path.Combine(temp.Path, "ai-remote-models.json");
        var store = new AiModelProfileStore(localPath, remotePath, secretProtector: new TestSecretProtector());
        await store.SaveAsync([
            new("local", "Local", "model.gguf", null, "llama-server", Source: AiModelSource.Local),
            new("remote", "Remote", "", null, "", Endpoint: "https://example.test/v1", Source: AiModelSource.Remote, ModelId: "vision", ApiKey: "top-secret")
        ]);

        var localJson = await File.ReadAllTextAsync(localPath);
        var remoteJson = await File.ReadAllTextAsync(remotePath);
        Assert.Contains("model.gguf", localJson);
        Assert.DoesNotContain("Remote", localJson);
        Assert.Contains("enc:", remoteJson);
        Assert.DoesNotContain("\"apiKey\"", remoteJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", remoteJson);
        Assert.DoesNotContain("example.test", remoteJson);
        Assert.DoesNotContain("vision", remoteJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("top-secret", Assert.Single((await store.LoadRemoteAsync())).ApiKey);
    }

    [Fact]
    public async Task RemoteModel_VirtualAddSaveReloadAndSecondModelPreserveEncryptedFields()
    {
        using var temp = new TemporaryDirectory();
        var localPath = Path.Combine(temp.Path, "ai-local-models.json");
        var remotePath = Path.Combine(temp.Path, "ai-remote-models.json");
        var store = new AiModelProfileStore(localPath, remotePath, secretProtector: new TestSecretProtector());
        var first = new AiModelProfile(
            "virtual-1", "虚拟 OpenAI 测试模型", "", null, "",
            Endpoint: "http://127.0.0.1:65534/v1", Source: AiModelSource.Remote,
            ModelId: "virtual-vision-model", ApiKey: "vs-test-key-not-real", SupportsImageInput: true);

        await store.SaveAsync([first]);
        var reopened = Assert.Single(await store.LoadRemoteAsync());

        Assert.Equal(first.DisplayName, reopened.DisplayName);
        Assert.Equal(first.ModelId, reopened.ModelId);
        Assert.Equal(first.Endpoint, reopened.Endpoint);
        Assert.Equal(first.ApiKey, reopened.ApiKey);
        Assert.True(reopened.SupportsImageInput);

        var second = first with { Id = "virtual-2", DisplayName = "第二个虚拟模型", ModelId = "virtual-text-model", ApiKey = null, SupportsImageInput = false };
        await store.SaveAsync([reopened, second]);
        var twiceReopened = await store.LoadRemoteAsync();
        Assert.Equal(2, twiceReopened.Count);
        Assert.Equal("virtual-vision-model", twiceReopened.Single(profile => profile.Id == "virtual-1").ModelId);
        Assert.Equal("virtual-text-model", twiceReopened.Single(profile => profile.Id == "virtual-2").ModelId);

        var raw = await File.ReadAllTextAsync(remotePath);
        foreach (var plaintext in new[] { "虚拟 OpenAI 测试模型", "virtual-vision-model", "127.0.0.1", "vs-test-key-not-real", "第二个虚拟模型" })
            Assert.DoesNotContain(plaintext, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultSecretProtector_RoundTripsWithoutPlaintext()
    {
        var protector = SecretProtector.CreateDefault();
        var protectedValue = protector.Protect("vision-secret-value");

        Assert.DoesNotContain("vision-secret-value", protectedValue);
        Assert.Equal("vision-secret-value", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void ImageReferences_ResolveNumberNameAndSelectedNode()
    {
        var first = new WorkflowNode { Id = "a", Type = "Image.Load", Name = "零件正面" };
        first.SetParameter("path", "images/input/front.png");
        var second = new WorkflowNode { Id = "b", Type = "Image.Load", Name = "零件背面" };
        second.SetParameter("path", "images/input/back.png");
        var workflow = new WorkflowDocument { Nodes = { first, second } };

        Assert.Equal("IMG-01", AiImageReferenceCatalog.Resolve(workflow, "分析 IMG-01")?.Reference);
        Assert.Equal("b", AiImageReferenceCatalog.Resolve(workflow, "分析 [IMG-02] 加载图像", "a")?.NodeId);
        Assert.Equal("b", AiImageReferenceCatalog.Resolve(workflow, "检查零件背面")?.NodeId);
        Assert.Equal("b", AiImageReferenceCatalog.Resolve(workflow, "分析选中图片", "b")?.NodeId);
        Assert.Equal("a", AiImageReferenceCatalog.Resolve(workflow, "处理图片1")?.NodeId);
    }

    [Fact]
    public void ImageReferences_ExplicitSecondReference_WinsWhenLoadNodeNamesAreDuplicated()
    {
        var first = new WorkflowNode { Id = "load_1", Type = "Image.Load", Name = "加载图像" };
        var second = new WorkflowNode { Id = "load_2", Type = "Image.Load", Name = "加载图像" };
        var workflow = new WorkflowDocument { Nodes = { first, second } };

        var resolved = AiImageReferenceCatalog.Resolve(
            workflow, "[IMG-02] 加载图像，我需要测量裂纹数量与像素长度", selectedNodeId: "load_1");

        Assert.NotNull(resolved);
        Assert.Equal("IMG-02", resolved.Reference);
        Assert.Equal("load_2", resolved.NodeId);
        Assert.Null(AiImageReferenceCatalog.Resolve(workflow, "分析加载图像"));
    }

    [Fact]
    public void LocalRuntimeManager_DetectsInstalledRuntimeWithoutManualConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var executable = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        File.WriteAllText(executable, "test");
        var manager = new LocalAiRuntimeManager(temp.Path);

        Assert.Equal(Path.GetFullPath(executable), manager.FindLlamaServer());
    }

    [Fact]
    public async Task DefaultPluginRoots_FindProviderBesideTestHost()
    {
        var roots = AiModelProfileStore.DefaultPluginRoots();
        var directories = new AiPluginLoader().DiscoverDirectories(roots);
        var manifests = await Task.WhenAll(directories.Select(directory =>
            File.ReadAllTextAsync(Path.Combine(directory, "plugin.json"))));

        Assert.Contains(roots, root => Path.GetFullPath(root) == Path.GetFullPath(AppContext.BaseDirectory));
        Assert.Contains(manifests, manifest => manifest.Contains("visionstudio.ai.llamacpp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalRuntimeManager_DownloadsAndConfiguresMissingWindowsRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        byte[] archive;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("llama-b1/llama-server.exe");
                await using var stream = entry.Open();
                await stream.WriteAsync("runtime"u8.ToArray());
            }
            archive = buffer.ToArray();
        }
        var handler = new StubHttpHandler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"assets\":[{\"name\":\"llama-b1-bin-win-cpu-x64.zip\",\"browser_download_url\":\"https://download.test/llama.zip\"}]}]", Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var manager = new LocalAiRuntimeManager(temp.Path, new HttpClient(handler));

        var installed = await manager.DownloadLatestRuntimeAsync();

        Assert.True(File.Exists(installed));
        Assert.EndsWith("llama-server.exe", installed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextStore_KeepsIndependentModelAndProjectContexts()
    {
        using var temp = new TemporaryDirectory();
        var store = new AiConversationContextStore(Path.Combine(temp.Path, "contexts.json"));
        var contexts = new Dictionary<string, IReadOnlyList<AiChatMessage>>
        {
            ["local::project-a"] = [new("user", "local question")],
            ["remote::project-a"] = [new("user", "remote question")],
            ["local::project-b"] = [new("user", "other project")]
        };
        await store.SaveAsync(contexts);

        var restored = await store.LoadAsync();

        Assert.Equal(3, restored.Count);
        Assert.Equal("remote question", restored["remote::project-a"][0].Content);
    }

    [Fact]
    public async Task ContextStore_PreservesUserThenErrorAsDistinctTurns()
    {
        using var temp = new TemporaryDirectory();
        var store = new AiConversationContextStore(Path.Combine(temp.Path, "contexts.json"));
        IReadOnlyDictionary<string, IReadOnlyList<AiChatMessage>> contexts = new Dictionary<string, IReadOnlyList<AiChatMessage>>
        {
            ["model::project"] =
            [
                new("user", "[IMG-02] 测量裂纹数量与长度"),
                new("assistant", "AI 错误：计划不完整")
            ]
        };

        await store.SaveAsync(contexts);
        var loaded = await store.LoadAsync();

        Assert.Collection(loaded["model::project"],
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Contains("IMG-02", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.StartsWith("AI 错误", message.Content);
            });
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_UsesStandardEndpointModelAndBearerToken()
    {
        Uri? requestedUri = null;
        string? authorization = null;
        var handler = new StubHttpHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"ok\\\",\\\"actions\\\":[{\\\"type\\\":\\\"add_node\\\",\\\"nodeId\\\":\\\"x\\\",\\\"operatorId\\\":\\\"Defect.CrackMeasure\\\"}]}\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        var profile = new AiModelProfile(
            "remote", "Remote", "", null, "", Endpoint: "https://example.test/v1", Source: AiModelSource.Remote,
            ModelId: "vision-model", ApiKey: "secret-token", SupportsImageInput: false);
        await using var provider = new OpenAiCompatibleProvider(profile, handler);

        var result = await provider.SendAsync(new("system", [new("user", "plan")]));

        Assert.Equal("https://example.test/v1/chat/completions", requestedUri?.ToString());
        Assert.Equal("Bearer secret-token", authorization);
        Assert.Contains("summary", result.Content);
    }

    [Fact]
    public async Task StandardCatalog_CreatesInstallDirectoryModelProfile()
    {
        using var temp = new TemporaryDirectory();
        var standardPath = Path.Combine(temp.Path, "standard-models.json");
        await File.WriteAllTextAsync(standardPath, "[{\"id\":\"standard\",\"displayName\":\"Standard\",\"modelFileName\":\"model.gguf\",\"mmprojFileName\":null,\"modelId\":\"standard-model\",\"downloadUrl\":\"https://example.test/model\",\"modelSizeBytes\":1,\"modelSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"license\":\"Apache-2.0\",\"sourceUrl\":\"https://example.test/source\"}]");
        var store = new AiModelProfileStore(Path.Combine(temp.Path, "local.json"), Path.Combine(temp.Path, "remote.json"), standardPath, new TestSecretProtector());

        var definition = Assert.Single(await store.LoadStandardModelsAsync());
        var profile = AiModelProfileStore.CreateStandardProfile(definition, "llama-server.exe");

        Assert.Equal(AiModelSource.StandardLocal, profile.Source);
        Assert.StartsWith(AiSoftwarePaths.ModelsDirectory, profile.ModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://example.test/model", profile.DownloadUrl);
    }

    [Fact]
    public async Task StandardCatalog_RepositoryDefinitionHasPinnedVerifiedDownloads()
    {
        var path = FindRepositoryFile("src", "VisionStudio.App", "config", "standard-models.json");
        var store = new AiModelProfileStore(
            standardModelsPath: path,
            secretProtector: new TestSecretProtector());

        var definition = Assert.Single(await store.LoadStandardModelsAsync());

        Assert.StartsWith("https://", definition.DownloadUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", definition.MmprojDownloadUrl, StringComparison.Ordinal);
        Assert.Equal(64, definition.ModelSha256?.Length);
        Assert.Equal(64, definition.MmprojSha256?.Length);
        Assert.True(definition.ModelSizeBytes > 0);
        Assert.True(definition.MmprojSizeBytes > 0);
        Assert.Equal("Apache-2.0", definition.License);
    }

    [Fact]
    public async Task LocalRuntimeManager_DownloadsAndVerifiesStandardModelFiles()
    {
        using var temp = new TemporaryDirectory();
        var modelBytes = "verified-model"u8.ToArray();
        var projectionBytes = "verified-mmproj"u8.ToArray();
        var server = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        await File.WriteAllTextAsync(server, "runtime");
        var definition = new StandardAiModelDefinition(
            "verified", "Verified", "model.gguf", "mmproj.gguf", "verified-model",
            "https://download.test/model", "https://download.test/mmproj", "test",
            modelBytes.Length, Sha256(modelBytes), projectionBytes.Length, Sha256(projectionBytes),
            "Apache-2.0", "https://download.test/source");
        var profile = new AiModelProfile(
            "verified", "Verified", Path.Combine(temp.Path, "model.gguf"),
            Path.Combine(temp.Path, "mmproj.gguf"), server,
            Source: AiModelSource.StandardLocal, StandardModelId: definition.Id,
            SupportsImageInput: true);
        var handler = new StubHttpHandler(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                request.RequestUri!.AbsolutePath.EndsWith("mmproj", StringComparison.Ordinal)
                    ? projectionBytes
                    : modelBytes)
        });
        var manager = new LocalAiRuntimeManager(temp.Path, new HttpClient(handler));

        var prepared = await manager.PrepareProfileAsync(profile, definition);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(prepared.ModelPath));
        Assert.Equal(projectionBytes, await File.ReadAllBytesAsync(prepared.MmprojPath!));
        Assert.Equal(Path.GetFullPath(server), prepared.LlamaServerPath);
    }

    [Fact]
    public async Task LocalRuntimeManager_RejectsAndRemovesCorruptStandardModelDownload()
    {
        using var temp = new TemporaryDirectory();
        var bytes = "corrupt"u8.ToArray();
        var modelPath = Path.Combine(temp.Path, "model.gguf");
        var definition = new StandardAiModelDefinition(
            "corrupt", "Corrupt", "model.gguf", null, "corrupt-model",
            "https://download.test/model", Description: "test",
            ModelSizeBytes: bytes.Length,
            ModelSha256: new string('0', 64),
            License: "Apache-2.0",
            SourceUrl: "https://download.test/source");
        var profile = new AiModelProfile(
            "corrupt", "Corrupt", modelPath, null, "unused-server",
            Source: AiModelSource.StandardLocal, StandardModelId: definition.Id);
        var manager = new LocalAiRuntimeManager(
            temp.Path,
            new HttpClient(new StubHttpHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            })));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.PrepareProfileAsync(profile, definition));

        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(modelPath + ".download"));
    }

    [Fact]
    public async Task PluginLoader_LoadsConnectorThroughSharedContractAndUnloadsIt()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "Plugin Loader");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument();
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        var loader = new AiPluginLoader();

        await using var loaded = await loader.LoadAsync(AppContext.BaseDirectory, new(session, "2.0.0", project.RootPath));

        Assert.Equal("visionstudio.ai.llamacpp", loaded.Manifest.Id);
        Assert.Equal(AiContract.Version, loaded.Manifest.SchemaVersion);
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

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FixedProvider(string content) : IAiProvider
    {
        public string ProviderId => "test";
        public AiModelProfile Profile { get; } = new("test", "Test", "external.gguf", null, "external-server");
        public bool SupportsVision => false;
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingProvider(string content) : IAiProvider
    {
        public string ProviderId => "capturing-test";
        public AiModelProfile Profile { get; } = new("capturing-test", "Capturing Test", "external.gguf", null, "external-server");
        public bool SupportsVision => false;
        public AiProviderRequest? LastRequest { get; private set; }
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
