async function showResult(result) {
  const preview = $("preview"),
    summary = $("outputSummary");
  summary.replaceChildren();
  if (state.previewUrl) {
    URL.revokeObjectURL(state.previewUrl);
    state.previewUrl = null;
  }
  const imageOutput = result.outputs?.find((output) => output.artifactPath);
  if (imageOutput) {
    const blob = await apiBlob(
      "/api/projects/" +
        state.projectId +
        "/files?path=" +
        encodeURIComponent(imageOutput.artifactPath),
    );
    state.previewUrl = URL.createObjectURL(blob);
    const img = document.createElement("img");
    img.src = state.previewUrl;
    img.alt = imageOutput.port;
    preview.replaceChildren(img);
  } else
    preview.innerHTML =
      '<div class="preview-empty"><span>◇</span><small>' +
      esc(text("noOutput")) +
      "</small></div>";
  (result.outputs || []).forEach((output) => {
    const row = document.createElement("div");
    row.textContent = output.port + ": " + (output.summary || output.type);
    summary.append(row);
  });
}

function renderInspector() {
  const root = $("properties");
  root.replaceChildren();
  if (state.selectedEdge) {
    const title = document.createElement("h3");
    title.textContent =
      state.selectedEdge.fromPort + " → " + state.selectedEdge.toPort;
    const actions = document.createElement("div");
    actions.className = "property-actions";
    const remove = document.createElement("button");
    remove.className = "danger";
    remove.textContent = text("delete");
    remove.onclick = deleteSelection;
    actions.append(remove);
    root.append(title, actions);
    return;
  }
  const node = state.workflow?.nodes.find(
    (item) => item.id === state.selectedNodeId,
  );
  if (!node) {
    root.innerHTML =
      '<p class="description">' + esc(text("selectNode")) + "</p>";
    return;
  }
  const descriptor = descriptorFor(node),
    title = document.createElement("h3");
  title.textContent = nodeName(node, descriptor);
  const apiName = document.createElement("div");
  apiName.className = "interface-name";
  apiName.textContent = text("interface") + ": " + descriptor.type;
  const description = document.createElement("p");
  description.className = "description";
  description.textContent = operatorDescription(descriptor);
  root.append(title, apiName, description);
  descriptor.parameters.forEach((parameter) =>
    root.append(parameterEditor(node, parameter)),
  );
  const actions = document.createElement("div");
  actions.className = "property-actions";
  if (node.type === "Image.Load") {
    const upload = document.createElement("button");
    upload.textContent = text("upload");
    upload.onclick = () => $("imageUpload").click();
    actions.append(upload);
  }
  const execute = document.createElement("button");
  execute.textContent = text("execute");
  execute.onclick = () => executeNode(node.id);
  const remove = document.createElement("button");
  remove.className = "danger";
  remove.textContent = text("delete");
  remove.onclick = deleteSelection;
  actions.append(execute, remove);
  root.append(actions);
}

function parameterEditor(node, parameter) {
  const label = document.createElement("label");
  label.className = "property-field";
  const title = document.createElement("span");
  title.textContent =
    state.language === "en-US"
      ? parameter.name
      : parameter.displayName || parameter.name;
  let input;
  if (parameter.kind === "True" || parameter.kind === "False") {
    input = document.createElement("input");
    input.type = "checkbox";
    input.checked = Boolean(node.parameters[parameter.name]);
    input.onchange = () => setParameter(node, parameter, input.checked);
  } else {
    input = document.createElement("input");
    input.type = parameter.kind === "Number" ? "number" : "text";
    input.value =
      node.parameters[parameter.name] ?? parameter.defaultValue ?? "";
    if (parameter.minimum != null) input.min = parameter.minimum;
    if (parameter.maximum != null) input.max = parameter.maximum;
    input.onchange = () =>
      setParameter(
        node,
        parameter,
        parameter.kind === "Number" ? Number(input.value) : input.value,
      );
  }
  label.append(title, input);
  if (parameter.description && state.language !== "en-US") {
    const small = document.createElement("small");
    small.textContent = parameter.description;
    label.append(small);
  }
  return label;
}

async function setParameter(node, parameter, value) {
  await mutate(
    [
      {
        kind: "setParameter",
        nodeId: node.id,
        parameter: parameter.name,
        value,
      },
    ],
    node.id,
    null,
    state.language === "en-US"
      ? "Change " + parameter.name
      : "修改" + (parameter.displayName || parameter.name),
  );
  await executeNode(node.id);
}
