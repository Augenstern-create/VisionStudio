using System.Net;
using System.Text;
using System.Text.Json;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
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
    public async Task AssistantPrompt_ContainsEveryRuntimeOperator_AndAllowsOptionalProposal()
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
        Assert.Contains("workflow assistant", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("review card", request.SystemPrompt, StringComparison.Ordinal);
        Assert.False(request.RequireJson, "The assistant answers in prose; a plan block is optional.");
        Assert.True(request.Messages.Last().Content.Length < 64_000, "The operator context must stay compact.");
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
    public void ProposalSplit_ReturnsProseAndFencedPlanSeparately()
    {
        var (prose, proposal) = AiWorkflowAgent.SplitProposal("""
            先做灰度化再高斯模糊，这样黑帽能突出细长暗缺陷。

            ```json
            {"summary":"灰度 + 模糊","actions":[{"type":"add_node","nodeId":"gray","operatorId":"Image.Gray"}]}
            ```
            """);

        Assert.Contains("黑帽", prose);
        Assert.DoesNotContain("```", prose);
        Assert.NotNull(proposal);
        Assert.Contains("\"add_node\"", proposal);
    }

    [Fact]
    public void ProposalSplit_TextOnlyAnswerProducesNoPlan()
    {
        var (prose, proposal) = AiWorkflowAgent.SplitProposal("这个结果里的噪点主要来自阈值过低，可以先试 Otsu。");

        Assert.Equal("这个结果里的噪点主要来自阈值过低，可以先试 Otsu。", prose);
        Assert.Null(proposal);
    }

    [Fact]
    public void ProposalSplit_ToleratesBarePlanObject()
    {
        var (prose, proposal) = AiWorkflowAgent.SplitProposal(
            "{\"summary\":\"x\",\"actions\":[{\"type\":\"add_node\",\"nodeId\":\"g\",\"operatorId\":\"Image.Gray\"}]}");

        Assert.Equal("", prose);
        Assert.NotNull(proposal);
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
    public async Task Pipeline_CanCarryPerNodeParametersInOneAction()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Pipeline Params");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("""
            {"type":"add_pipeline","fromNode":"load",
             "operatorIds":["Image.Gray","Image.GaussianBlur"],
             "nodeIds":["g","b"],
             "parameters":{"b":{"kernelSize":5,"sigma":1.5}}}
            """);

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new("加灰度再模糊", "load"));

        Assert.NotNull(response.Plan);
        Assert.Contains(response.Plan!.Actions, action =>
            action.Type == "set_parameter" && action.NodeId == "b" && action.Parameter == "kernelSize");
        Assert.Contains(response.Plan.Actions, action =>
            action.Type == "set_parameter" && action.NodeId == "b" && action.Parameter == "sigma");
        Assert.DoesNotContain(response.Plan.Actions, action => action.Type == "add_pipeline");
    }

    [Fact]
    public async Task Assistant_InsertsNodeIntoAnExistingEdge()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Insert Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" }, new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" } },
            Connections = { new WorkflowConnection("load", "Image", "gray", "Image") }
        };
        await projects.SaveWorkflowAsync(project, workflow);
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("""
            {"summary":"在加载与灰度之间插入模糊","targetNodeId":"blur","actions":[
              {"type":"disconnect_nodes","fromNode":"load","fromPort":"Image","toNode":"gray","toPort":"Image"},
              {"type":"add_node","nodeId":"blur","operatorId":"Image.GaussianBlur"},
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"blur","toPort":"Image"},
              {"type":"connect_nodes","fromNode":"blur","fromPort":"Image","toNode":"gray","toPort":"Image"},
              {"type":"set_parameter","nodeId":"blur","parameter":"kernelSize","value":5}
            ]}
            """);

        var agent = new AiWorkflowAgent(session, provider);
        var proposal = await agent.ProcessAsync(new("在加载和灰度之间加一个高斯模糊", "load"));
        Assert.NotNull(proposal.Plan);

        var revision = (await session.GetSnapshotAsync()).Revision;
        await agent.ApplyPlanAsync(proposal.Plan!, revision);

        var updated = (await session.GetSnapshotAsync()).Workflow;
        Assert.Equal(3, updated.Nodes.Count);
        Assert.Equal(2, updated.Connections.Count);
        Assert.Contains(updated.Connections, c => c.FromNode == "load" && c.ToNode == "blur");
        Assert.Contains(updated.Connections, c => c.FromNode == "blur" && c.ToNode == "gray");
        Assert.Equal(5, updated.Nodes.Single(node => node.Id == "blur").GetParameter("kernelSize", 0));
    }

    [Fact]
    public void PlanParser_AllowsFortyEightActionsButNoMore()
    {
        static string Plan(int actions) => "{\"summary\":\"x\",\"actions\":[" +
            string.Join(',', Enumerable.Range(0, actions).Select(index =>
                $"{{\"type\":\"add_node\",\"nodeId\":\"n{index}\",\"operatorId\":\"Image.Gray\"}}")) + "]}";

        var plan = AiWorkflowAgent.ParsePlan(Plan(AiWorkflowAgent.MaximumActions));
        Assert.Equal(AiWorkflowAgent.MaximumActions, plan.Actions.Count);
        Assert.Throws<InvalidDataException>(() => AiWorkflowAgent.ParsePlan(Plan(AiWorkflowAgent.MaximumActions + 1)));
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
    public async Task Assistant_ContextCarriesNodeParametersAndSelection()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Context Params");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var blur = new WorkflowNode { Id = "blur", Type = "Image.GaussianBlur", Name = "高斯模糊" };
        blur.SetParameter("kernelSize", 5);
        blur.SetParameter("sigma", 1.5);
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" }, blur } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new CapturingProvider("好的。");

        await new AiWorkflowAgent(session, provider).ProcessAsync(new("这个模糊够强吗", "blur"));

        using var context = JsonDocument.Parse(provider.LastRequest!.Messages[^1].Content);
        var blurNode = context.RootElement.GetProperty("graph").GetProperty("nodes")
            .EnumerateArray().Single(node => node.GetProperty("id").GetString() == "blur");
        Assert.Equal(5, blurNode.GetProperty("parameters").GetProperty("kernelSize").GetInt32());
        Assert.Equal(1.5, blurNode.GetProperty("parameters").GetProperty("sigma").GetDouble(), 3);
        var selected = context.RootElement.GetProperty("graph").GetProperty("selectedNode");
        Assert.Equal("blur", selected.GetProperty("id").GetString());
        Assert.Equal(5, selected.GetProperty("parameters").GetProperty("kernelSize").GetInt32());
    }

    [Fact]
    public async Task Assistant_RequestsExecutionThenUsesTheResult()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Tool Loop");
        string input = Path.Combine(project.RootPath, "images", "input");
        Directory.CreateDirectory(input);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(input, "1.png"));
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" } },
            Connections = { new WorkflowConnection("load", "Image", "gray", "Image") }
        };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new ScriptedProvider(
            "```json\n{\"tool\":\"execute_node\",\"nodeId\":\"gray\"}\n```",
            "灰度图是 430x287 单通道，结果正常。");
        var executed = new List<string>();

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(
            new("灰度化跑出来是什么样", "gray"), toolProgress: new Collector<string>(executed.Add));

        Assert.Contains("430x287", response.Message);
        Assert.Equal(["gray"], executed);
        Assert.Equal(2, provider.Requests.Count);
        string dump = string.Join(" || ", provider.Requests[1].Messages.Select(message => message.Content[..Math.Min(90, message.Content.Length)]));
        Assert.True(provider.Requests[1].Messages.Any(message => message.Content.Contains("[tool result]", StringComparison.Ordinal)), dump);
        Assert.True(provider.Requests[1].Messages.Any(message => message.Content.Contains("Image = 430x287", StringComparison.Ordinal)), dump);
    }

    [Fact]
    public async Task Assistant_ParsesSweepRequestsAndFeedsTheTableBack()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Sweep Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new ScriptedProvider(
            "```json\n{\"tool\":\"sweep_parameter\",\"nodeId\":\"blur\",\"parameter\":\"kernelSize\",\"from\":3,\"to\":9,\"step\":2,\"targetNodeId\":\"gray\"}\n```",
            "扫描完成：kernelSize 从 3 到 9 对输出没有影响。");
        var tools = new RecordingTools();
        var executed = new List<string>();

        var response = await new AiWorkflowAgent(session, provider, tools).ProcessAsync(
            new("扫一下 kernelSize 的影响", "blur"), toolProgress: new Collector<string>(executed.Add));

        var sweep = Assert.Single(tools.Sweeps);
        Assert.Equal("blur", sweep.NodeId);
        Assert.Equal("kernelSize", sweep.Parameter);
        Assert.Equal(["3", "5", "7", "9"], sweep.Values);
        Assert.Equal("gray", sweep.TargetNodeId);
        Assert.Contains("扫描完成", response.Message);
        Assert.Contains(provider.Requests[1].Messages, message => message.Content.Contains("[tool result] sweep blur.kernelSize", StringComparison.Ordinal));
        Assert.Contains(executed, item => item.StartsWith("sweep:blur.kernelSize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assistant_RejectsSweepsWithTooManyValues()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Sweep Budget");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new ScriptedProvider(
            "```json\n{\"tool\":\"sweep_parameter\",\"nodeId\":\"blur\",\"parameter\":\"kernelSize\",\"from\":1,\"to\":99,\"step\":1}\n```",
            "好的。");
        var tools = new RecordingTools();

        await new AiWorkflowAgent(session, provider, tools).ProcessAsync(new("扫一遍", "blur"));

        Assert.Empty(tools.Sweeps);
        Assert.Contains(provider.Requests[1].Messages, message => message.Content.Contains("limited to 12 values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assistant_StopsRequestingExecutionAfterTheBudget()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Tool Budget");
        string input = Path.Combine(project.RootPath, "images", "input");
        Directory.CreateDirectory(input);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(input, "1.png"));
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" } },
            Connections = { new WorkflowConnection("load", "Image", "gray", "Image") }
        };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        var toolCall = "```json\n{\"tool\":\"execute_node\",\"nodeId\":\"gray\"}\n```";
        await using var provider = new ScriptedProvider(toolCall, toolCall, toolCall, toolCall, toolCall, toolCall, toolCall);
        var executed = new List<string>();

        await new AiWorkflowAgent(session, provider).ProcessAsync(
            new("一直跑", "gray"), toolProgress: new Collector<string>(executed.Add));

        Assert.Equal(3, executed.Count);
        Assert.Contains(provider.Requests[^1].Messages, message => message.Content.Contains("[tool limit]", StringComparison.Ordinal));
    }

    [Fact]
    public void AnnotateUnknownOperators_FlagsIdsOutsideTheCatalog()
    {
        IReadOnlyList<OperatorDescriptor> catalog = [.. OpenCvOperatorCatalog.CreateRegistry().Descriptors];

        string flagged = AiWorkflowAgent.AnnotateUnknownOperators("先用 Image.Gray，再 Draw.Magic 和 Contour.Hull2", catalog);
        Assert.Contains("Draw.Magic", flagged);
        Assert.Contains("Contour.Hull2", flagged);
        Assert.Contains("不在当前算子目录中", flagged);

        string clean = AiWorkflowAgent.AnnotateUnknownOperators("先用 Image.Gray，再用 Draw.Contour 和 Measure.Circularity", catalog);
        Assert.Equal("先用 Image.Gray，再用 Draw.Contour 和 Measure.Circularity", clean);
    }

    [Fact]
    public async Task Assistant_DoesNotReplayErrorOrExecutionTurnsToTheModel()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Kinds Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new CapturingProvider("好的。");
        var conversation = new List<AiChatMessage>
        {
            new("user", "第一个问题", AiChatMessageKinds.Chat),
            new("assistant", "第一个回答", AiChatMessageKinds.Chat),
            new("assistant", "AI 错误：OpenAI-compatible endpoint returned 404", AiChatMessageKinds.Error),
            new("assistant", "Contours: VisionContours { Items = ... }；Count: 4", AiChatMessageKinds.Execution)
        };

        await new AiWorkflowAgent(session, provider).ProcessAsync(new("第二个问题", "load", Conversation: conversation));

        var sent = provider.LastRequest!.Messages;
        Assert.DoesNotContain(sent, message => message.Content.Contains("AI 错误", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, message => message.Content.Contains("VisionContours", StringComparison.Ordinal));
        Assert.Contains(sent, message => message.Content == "第一个问题");
        Assert.Contains(sent, message => message.Content == "第一个回答");
    }

    [Fact]
    public async Task Assistant_TextOnlyAnswer_ReturnsConversationWithoutPlan()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Conversation Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FixedProvider("黑帽运算适合提取细长暗缺陷；先灰度化，再把 kernelSize 调到 15 左右。");

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new("黑帽算子适合做什么？", "load"));

        Assert.Contains("黑帽", response.Message);
        Assert.Null(response.Plan);
        var after = await session.GetSnapshotAsync();
        Assert.Empty(after.Workflow.Connections);
    }

    [Fact]
    public async Task Assistant_ProposesPlanButLeavesWorkflowUntouchedUntilApplied()
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
            裂纹可以用“灰度 → 平滑 → 黑帽 → Otsu → 闭运算 → 轮廓 → 路径统计”这套通用流程来量。

            ```json
            {"summary":"使用通用 OpenCV 阶段分割细长暗缺陷并统计数量和长度","targetNodeId":"ai_metrics","actions":[
              {"type":"add_pipeline","fromNode":"load","operatorIds":["Image.Gray","Image.GaussianBlur","Morphology.BlackHat","Threshold.Otsu","Morphology.Close","Blob.Contours","Contour.PathMetrics"],"nodeIds":["ai_gray","ai_blur","ai_blackhat","ai_otsu","ai_close","ai_contours","ai_metrics"]},
              {"type":"set_parameter","nodeId":"ai_blackhat","parameter":"kernelSize","value":15},
              {"type":"set_parameter","nodeId":"ai_close","parameter":"kernelSize","value":5},
              {"type":"set_parameter","nodeId":"ai_metrics","parameter":"minArea","value":30},
              {"type":"set_parameter","nodeId":"ai_metrics","parameter":"minLength","value":50}
            ]}
            ```
            """);

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度", "load", input));

        Assert.Contains("黑帽", response.Message);
        Assert.DoesNotContain("```", response.Message);
        Assert.NotNull(response.Plan);
        var untouched = await session.GetSnapshotAsync();
        Assert.Single(untouched.Workflow.Nodes);
        Assert.Empty(untouched.Workflow.Connections);

        var applied = await new AiWorkflowAgent(session, provider).ApplyPlanAsync(response.Plan!, untouched.Revision);

        Assert.True(applied.Execution?.Success, applied.Execution?.Error);
        Assert.Contains(applied.Execution!.Outputs, output => output.Port == "Count");
        Assert.Contains(applied.Execution.Outputs, output => output.Port == "PixelLength");
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
    public async Task Assistant_DropsInvalidProposalAndReportsTheReason()
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
            先接一个不存在的节点试试。

            ```json
            {"summary":"残缺方案","targetNodeId":"missing","actions":[
              {"type":"connect_nodes","fromNode":"load","fromPort":"Image","toNode":"missing","toPort":"Image"}
            ]}
            ```
            """);
        var before = await session.GetSnapshotAsync();

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new(
            "把加载的图片转换为灰度图", "load"));

        Assert.Null(response.Plan);
        Assert.Contains("未通过校验", response.Message);
        var after = await session.GetSnapshotAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Single(after.Workflow.Nodes);
        Assert.Empty(after.Workflow.Connections);
    }

    [Theory]
    [InlineData("{\"summary\":\"缺少输入\",\"targetNodeId\":\"gray\",\"actions\":[{\"type\":\"add_node\",\"nodeId\":\"gray\",\"operatorId\":\"Image.Gray\"}]}")]
    [InlineData("{\"summary\":\"重复输入\",\"targetNodeId\":\"gray\",\"actions\":[{\"type\":\"add_node\",\"nodeId\":\"gray\",\"operatorId\":\"Image.Gray\"},{\"type\":\"connect_nodes\",\"fromNode\":\"load\",\"fromPort\":\"Image\",\"toNode\":\"gray\",\"toPort\":\"Image\"},{\"type\":\"connect_nodes\",\"fromNode\":\"load\",\"fromPort\":\"Image\",\"toNode\":\"gray\",\"toPort\":\"Image\"}]}")]
    public async Task Assistant_RejectsMissingOrMultiplyConnectedRequiredInputs(string invalidPlan)
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

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new("把加载的图片转换为灰度图", "load"));

        Assert.Null(response.Plan);
        var after = await session.GetSnapshotAsync();
        Assert.Equal(0, after.Revision);
        Assert.Single(after.Workflow.Nodes);
        Assert.Empty(after.Workflow.Connections);
    }

    [Fact]
    public async Task Assistant_RepeatedPipelineFromSecondImage_GeneratesUniqueNodeIds()
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
    }

    [Fact]
    public async Task ProfileStore_RoundTripsRemoteProfileWithEncryptedApiKey()
    {
        using var temp = new TemporaryDirectory();
        var store = new AiModelProfileStore(Path.Combine(temp.Path, "remote.json"), secretProtector: new TestSecretProtector());
        var profile = new AiModelProfile("qwen", "Qwen", "https://example.test/v1", "qwen-vl-max", "top-secret", true);

        await store.SaveAsync([profile]);
        var restored = Assert.Single(await store.LoadAsync());

        Assert.Equal(profile, restored);
        string raw = await File.ReadAllTextAsync(Path.Combine(temp.Path, "remote.json"));
        Assert.Contains("enc:", raw);
        Assert.DoesNotContain("top-secret", raw);
        Assert.DoesNotContain("example.test", raw);
    }

    [Fact]
    public void DefaultAiConfiguration_IsSoftwareLevelNotProjectLevel()
    {
        Assert.Equal(Path.Combine(AiSoftwarePaths.ConfigDirectory, "ai-remote-models.json"), AiSoftwarePaths.RemoteProfilesFile);
        Assert.Equal(Path.Combine(AiSoftwarePaths.ConfigDirectory, "ai-contexts.json"), AiSoftwarePaths.ContextsFile);
        Assert.DoesNotContain(".project", AiSoftwarePaths.RemoteProfilesFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".project", AiSoftwarePaths.ContextsFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteModel_VirtualAddSaveReloadAndSecondModelPreserveEncryptedFields()
    {
        using var temp = new TemporaryDirectory();
        var remotePath = Path.Combine(temp.Path, "ai-remote-models.json");
        var store = new AiModelProfileStore(remotePath, secretProtector: new TestSecretProtector());
        var first = new AiModelProfile(
            "virtual-1", "虚拟 OpenAI 测试模型", "http://127.0.0.1:65534/v1",
            "virtual-vision-model", "vs-test-key-not-real", SupportsImageInput: true);

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
    public async Task ContextStore_KeepsIndependentModelAndProjectContexts()
    {
        using var temp = new TemporaryDirectory();
        var store = new AiConversationContextStore(Path.Combine(temp.Path, "contexts.json"));
        var contexts = new Dictionary<string, IReadOnlyList<AiChatMessage>>
        {
            ["model-a::project-a"] = [new("user", "first question")],
            ["remote::project-a"] = [new("user", "remote question")],
            ["model-a::project-b"] = [new("user", "other project")]
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
                new("assistant", "AI 错误：连接失败")
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
            "remote", "Remote", "https://example.test/v1", "vision-model", "secret-token");
        await using var provider = new OpenAiCompatibleProvider(profile, handler);

        var result = await provider.SendAsync(new("system", [new("user", "plan")]));

        Assert.Equal("https://example.test/v1/chat/completions", requestedUri?.ToString());
        Assert.Equal("Bearer secret-token", authorization);
        Assert.Contains("summary", result.Content);
    }

    [Theory]
    [InlineData("https://host/v1", "https://host/v1/chat/completions")]
    [InlineData("https://host/v1/", "https://host/v1/chat/completions")]
    [InlineData("  https://host/v1  ", "https://host/v1/chat/completions")]
    [InlineData("https://host", "https://host/v1/chat/completions")]
    [InlineData("https://host/", "https://host/v1/chat/completions")]
    [InlineData("https://host/v1/chat/completions", "https://host/v1/chat/completions")]
    [InlineData("https://host/openai/v1", "https://host/openai/v1/chat/completions")]
    public void ChatUri_AcceptsEveryBaseUrlShape(string endpoint, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.ResolveChatCompletionsUri(endpoint).ToString());
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_FallsBackToPathWithoutV1On404()
    {
        var calls = new List<string>();
        var handler = new StubHttpHandler(request =>
        {
            calls.Add(request.RequestUri!.ToString());
            return request.RequestUri!.AbsolutePath == "/v1/chat/completions"
                ? new(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"降级路径可用\"}}]}",
                        Encoding.UTF8, "application/json")
                };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://host/v1", "model"), handler);

        var result = await provider.SendAsync(new("system", [new("user", "hi")]));

        Assert.Equal("降级路径可用", result.Content);
        Assert.Equal(["https://host/v1/chat/completions", "https://host/chat/completions"], calls);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_ReportsPathAndHintOn404()
    {
        var handler = new StubHttpHandler(_ => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent("404 page not found")
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://host/v1/chat/completions", "model"), handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SendAsync(new("system", [new("user", "hi")])));

        Assert.Contains("404", exception.Message);
        Assert.Contains("https://host/v1/chat/completions", exception.Message);
        Assert.Contains("Base URL", exception.Message);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_ReadsArrayContentParts()
    {
        var handler = new StubHttpHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"第一段。\"},{\"type\":\"text\",\"text\":\"第二段。\"}]}}]}",
                Encoding.UTF8, "application/json")
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "vision-model"), handler);

        var result = await provider.SendAsync(new("system", [new("user", "hi")]));

        Assert.Equal("第一段。第二段。", result.Content);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_RetriesWithLargerBudgetWhenAnswerIsCutOff()
    {
        var budgets = new List<int>();
        var handler = new StubHttpHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            budgets.Add(document.RootElement.GetProperty("max_tokens").GetInt32());
            return budgets.Count == 1
                ? new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"\",\"reasoning_content\":\"想得太久了\"}}]}",
                        Encoding.UTF8, "application/json")
                }
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"重试后的回答\"}}]}",
                        Encoding.UTF8, "application/json")
                };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "reasoner", MaximumOutputTokens: 1024), handler);

        var result = await provider.SendAsync(new("system", [new("user", "搭个流程")]));

        Assert.Equal("重试后的回答", result.Content);
        Assert.Equal([1024, 4096], budgets);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_ExplainsEmptyContentFromReasoningModel()
    {
        var handler = new StubHttpHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"\",\"reasoning_content\":\"让我先想一想……\"}}],\"usage\":{\"completion_tokens\":1024}}",
                Encoding.UTF8, "application/json")
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "reasoner"), handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.SendAsync(new("system", [new("user", "hi")])));

        Assert.Contains("finish_reason=length", exception.Message);
        Assert.Contains("reasoning_content", exception.Message);
        Assert.Contains("最大输出 token", exception.Message);
        Assert.Contains("Raw response:", exception.Message);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_StreamsReasoningThenAnswer()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先想一想。\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"答案是 42。\"}}]}\n\n" +
                "data: [DONE]\n\n", Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://host/v1", "model"), handler);
        var deltas = new List<AiStreamDelta>();

        var result = await provider.SendStreamingAsync(
            new AiProviderRequest("system", [new("user", "hi")]), deltas.Add);

        Assert.Equal("答案是 42。", result.Content);
        Assert.Collection(deltas,
            item =>
            {
                Assert.True(item.IsReasoning);
                Assert.Equal("先想一想。", item.Text);
            },
            item =>
            {
                Assert.False(item.IsReasoning);
                Assert.Equal("答案是 42。", item.Text);
            });
    }

    [Fact]
    public async Task Assistant_ReportsStreamDeltasWhileAnswering()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Streaming Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new StreamingProvider("这是流式回答。");
        var deltas = new List<AiStreamDelta>();

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(
            new("这个流程能做什么？", "load"), new Collector<AiStreamDelta>(deltas.Add));

        Assert.Equal("这是流式回答。", response.Message);
        Assert.Contains(deltas, delta => delta.IsReasoning);
        Assert.Contains(deltas, delta => !delta.IsReasoning);
    }

    [Fact]
    public async Task Assistant_FallsBackToBufferedCallWhenStreamingFails()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Streaming Fallback");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        await using var provider = new FailingStreamingProvider("缓冲回答。");

        var response = await new AiWorkflowAgent(session, provider).ProcessAsync(new("继续", "load"));

        Assert.Equal("缓冲回答。", response.Message);
        Assert.True(provider.UsedFallback);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_SendsReasoningEffortAndMaxTokens()
    {
        string? payload = null;
        var handler = new StubHttpHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().Result;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "reasoner",
                MaximumOutputTokens: 4096, MaxInputTokens: 64000, ReasoningEffort: AiReasoningEffort.High), handler);

        await provider.SendAsync(new("system", [new("user", "hi")]));

        using var body = JsonDocument.Parse(payload!);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_MapsOffEffortToThinkingDisabled()
    {
        string? payload = null;
        var handler = new StubHttpHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().Result;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "reasoner",
                ReasoningEffort: AiReasoningEffort.Off), handler);

        await provider.SendAsync(new("system", [new("user", "hi")]));

        using var body = JsonDocument.Parse(payload!);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_RetriesWithoutReasoningWhenGatewayRejectsIt()
    {
        var payloads = new List<string>();
        var handler = new StubHttpHandler(request =>
        {
            payloads.Add(request.Content!.ReadAsStringAsync().Result);
            return payloads.Count == 1
                ? new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"the request parameters were rejected\"}}", Encoding.UTF8, "application/json")
                }
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"降级成功\"}}]}",
                        Encoding.UTF8, "application/json")
                };
        });
        await using var provider = new OpenAiCompatibleProvider(
            new AiModelProfile("remote", "Remote", "https://example.test/v1", "reasoner",
                ReasoningEffort: AiReasoningEffort.Max), handler);

        var result = await provider.SendAsync(new("system", [new("user", "hi")]));

        Assert.Equal("降级成功", result.Content);
        Assert.Equal(2, payloads.Count);
        using var first = JsonDocument.Parse(payloads[0]);
        using var second = JsonDocument.Parse(payloads[1]);
        Assert.Equal("max", first.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(second.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Assistant_TrimsHistoryToConfiguredInputBudget()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Budget Test");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        var history = Enumerable.Range(0, 20)
            .Select(index => new AiChatMessage(index % 2 == 0 ? "user" : "assistant", new string('字', 2000)))
            .ToList();
        history.Add(new AiChatMessage("user", "这是最新的一条问题"));
        var profile = new AiModelProfile("test", "Test", "https://example.test/v1", "test-model",
            MaximumOutputTokens: 1024, MaxInputTokens: 8000);
        await using var provider = new CapturingProvider("好的。", profile);

        await new AiWorkflowAgent(session, provider).ProcessAsync(new("继续", "load", Conversation: history));

        var sent = provider.LastRequest!.Messages;
        Assert.True(sent.Count < history.Count, $"expected trimmed history, got {sent.Count} of {history.Count}");
        Assert.Contains("这是最新的一条问题", sent[^2].Content + sent[^1].Content);
    }

    [Fact]
    public async Task ProviderFactory_RequiresRemoteEndpoint()    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "AI Provider Factory");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var session = new WorkflowAutomationSession(project, new WorkflowDocument(), projects, registry, new OpenCvWorkflowAutomationRunner(registry));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiProviderFactory().CreateAsync(
            new AiModelProfile("empty", "Empty", ""), session, project.RootPath, "2.0.0"));
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

    private sealed class FixedProvider(string content) : IAiProvider
    {
        public string ProviderId => "test";
        public AiModelProfile Profile { get; } = new("test", "Test", "https://example.test/v1", "test-model");
        public bool SupportsVision => false;
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingProvider(string content, AiModelProfile? profile = null) : IAiProvider
    {
        public string ProviderId => "capturing-test";
        public AiModelProfile Profile { get; } = profile ?? new("capturing-test", "Capturing Test", "https://example.test/v1", "test-model");
        public bool SupportsVision => false;
        public AiProviderRequest? LastRequest { get; private set; }
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records tool calls so the agent's parsing can be asserted.</summary>
    private sealed class RecordingTools : IAiWorkflowTools
    {
        public List<AiParameterSweep> Sweeps { get; } = [];
        public Task<string> ExecuteNodeAsync(string nodeId, CancellationToken cancellationToken = default) =>
            Task.FromResult("ok in 10 ms\nImage = 430x287, 1 channel(s)");
        public Task<string> SweepParameterAsync(AiParameterSweep sweep, CancellationToken cancellationToken = default)
        {
            Sweeps.Add(sweep);
            return Task.FromResult($"swept {sweep.NodeId}.{sweep.Parameter}" + Environment.NewLine + "kernelSize=3 -> 10 ms, Image=430x287x1");
        }
    }

    /// <summary>Returns queued replies in order and records every request it received.</summary>
    private sealed class ScriptedProvider(params string[] replies) : IAiProvider
    {
        public string ProviderId => "scripted";
        public AiModelProfile Profile { get; } = new("scripted", "Scripted", "https://host/v1", "model");
        public bool SupportsVision => false;
        public List<AiProviderRequest> Requests { get; } = [];
        private int _index;
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            string reply = _index < replies.Length ? replies[_index] : replies[^1];
            _index++;
            return Task.FromResult(new AiProviderResponse(reply, "Scripted", false, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamingProvider(string content) : IAiProvider, IAiStreamingProvider
    {
        public string ProviderId => "streaming-test";
        public AiModelProfile Profile { get; } = new("streaming-test", "Streaming Test", "https://host/v1", "model");
        public bool SupportsVision => false;
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        public Task<AiProviderResponse> SendStreamingAsync(AiProviderRequest request, Action<AiStreamDelta> onDelta, CancellationToken cancellationToken = default)
        {
            onDelta(new AiStreamDelta(true, "先看流程。"));
            onDelta(new AiStreamDelta(false, content));
            return Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingStreamingProvider(string content) : IAiProvider, IAiStreamingProvider
    {
        public string ProviderId => "failing-stream";
        public AiModelProfile Profile { get; } = new("failing-stream", "Failing Stream", "https://host/v1", "model");
        public bool SupportsVision => false;
        public bool UsedFallback { get; private set; }
        public Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
        {
            UsedFallback = true;
            return Task.FromResult(new AiProviderResponse(content, "Test", false, TimeSpan.Zero));
        }
        public Task<AiProviderResponse> SendStreamingAsync(AiProviderRequest request, Action<AiStreamDelta> onDelta, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("streaming not supported by this endpoint");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]));
    }

    private sealed class Collector<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
