namespace VisionStudio.Core;

public sealed record WorkflowValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<WorkflowNode> TopologicalOrder)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class WorkflowValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class WorkflowValidator
{
    public static WorkflowValidationResult Validate(WorkflowDocument workflow, OperatorRegistry registry, bool requireConnectedInputs = true)
    {
        var errors = new List<string>();
        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in workflow.Nodes)
        {
            if (!nodes.TryAdd(node.Id, node)) errors.Add($"Duplicate node id '{node.Id}'.");
            if (!registry.TryGetDescriptor(node.Type, out _)) errors.Add($"Node '{node.Id}' uses unknown operator '{node.Type}'.");
        }

        var occupiedInputs = new HashSet<(string Node, string Port)>();
        foreach (var edge in workflow.Connections)
        {
            if (!nodes.TryGetValue(edge.FromNode, out var from)) { errors.Add($"Connection source node '{edge.FromNode}' does not exist."); continue; }
            if (!nodes.TryGetValue(edge.ToNode, out var to)) { errors.Add($"Connection target node '{edge.ToNode}' does not exist."); continue; }
            if (!registry.TryGetDescriptor(from.Type, out var fromDescriptor) || !registry.TryGetDescriptor(to.Type, out var toDescriptor)) continue;
            var output = fromDescriptor.Outputs.SingleOrDefault(p => p.Name == edge.FromPort);
            var input = toDescriptor.Inputs.SingleOrDefault(p => p.Name == edge.ToPort);
            if (output is null) errors.Add($"Output port '{edge.FromNode}.{edge.FromPort}' does not exist.");
            if (input is null) errors.Add($"Input port '{edge.ToNode}.{edge.ToPort}' does not exist.");
            if (output is not null && input is not null && input.Type != NodeValueType.Any && output.Type != NodeValueType.Any && output.Type != input.Type)
                errors.Add($"Cannot connect {output.Type} to {input.Type}: '{edge.FromNode}.{edge.FromPort}' -> '{edge.ToNode}.{edge.ToPort}'.");
            if (!occupiedInputs.Add((edge.ToNode, edge.ToPort))) errors.Add($"Input '{edge.ToNode}.{edge.ToPort}' has more than one connection.");
        }

        foreach (var node in workflow.Nodes)
        {
            if (!registry.TryGetDescriptor(node.Type, out var descriptor)) continue;
            foreach (var input in descriptor.Inputs.Where(p => p.Required && requireConnectedInputs))
                if (!occupiedInputs.Contains((node.Id, input.Name))) errors.Add($"Required input '{node.Id}.{input.Name}' is not connected.");
        }

        var order = TopologicalSort(workflow, nodes, errors);
        return new WorkflowValidationResult(errors, order);
    }

    private static IReadOnlyList<WorkflowNode> TopologicalSort(WorkflowDocument workflow, Dictionary<string, WorkflowNode> nodes, List<string> errors)
    {
        var indegree = nodes.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in workflow.Connections.Where(e => nodes.ContainsKey(e.FromNode) && nodes.ContainsKey(e.ToNode)))
        {
            outgoing[edge.FromNode].Add(edge.ToNode);
            indegree[edge.ToNode]++;
        }
        var queue = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var pair in indegree.Where(x => x.Value == 0)) queue.Enqueue(pair.Key, pair.Key);
        var result = new List<WorkflowNode>();
        while (queue.TryDequeue(out var id, out _))
        {
            result.Add(nodes[id]);
            foreach (var next in outgoing[id]) if (--indegree[next] == 0) queue.Enqueue(next, next);
        }
        if (result.Count != nodes.Count) errors.Add("Workflow contains a cycle.");
        return result;
    }
}
