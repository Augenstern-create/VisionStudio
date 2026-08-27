function operatorName(descriptor) {
  return state.language === "en-US"
    ? descriptor.englishName || descriptor.type
    : descriptor.displayName;
}

function operatorDescription(descriptor) {
  return state.language === "en-US"
    ? descriptor.englishDescription || descriptor.description || descriptor.type
    : descriptor.description || descriptor.type;
}

function nodeName(node, descriptor) {
  return state.language === "en-US" &&
    (!node.name || node.name === descriptor.displayName)
    ? operatorName(descriptor)
    : node.name || operatorName(descriptor);
}

function renderPalette() {
  const query = $("globalSearch").value.trim().toLowerCase(),
    filtered = state.catalog.filter(
      (item) =>
        !query ||
        operatorName(item).toLowerCase().includes(query) ||
        item.type.toLowerCase().includes(query),
    ),
    groups = {};
  filtered.forEach((item) => (groups[item.category] ??= []).push(item));
  $("operatorCategories").replaceChildren();
  Object.entries(groups).forEach(([category, items]) => {
    const section = document.createElement("section");
    section.className = "category" + (query ? " open" : "");
    const heading = document.createElement("button");
    heading.textContent = categoryName(category);
    heading.onclick = () => section.classList.toggle("open");
    const rows = document.createElement("div");
    rows.className = "category-items";
    items.forEach((item) => {
      const row = document.createElement("button");
      row.className = "operator-row";
      row.textContent = operatorName(item);
      row.title = operatorDescription(item);
      row.onclick = () => addNode(item);
      rows.append(row);
    });
    section.append(heading, rows);
    $("operatorCategories").append(section);
  });
}

function descriptorFor(node) {
  return state.catalog.find((item) => item.type === node.type);
}

function renderWorkflow() {
  const layer = $("nodeLayer");
  layer.replaceChildren();
  const nodes = state.workflow?.nodes || [];
  $("emptyCanvas").classList.toggle("hidden", nodes.length > 0);
  for (const node of nodes) {
    const descriptor = descriptorFor(node);
    if (!descriptor) continue;
    const element = document.createElement("article");
    element.className =
      "workflow-node" + (node.id === state.selectedNodeId ? " selected" : "");
    element.dataset.nodeId = node.id;
    element.style.left = node.x + "px";
    element.style.top = node.y + "px";
    const inputs = descriptor.inputs
        .map((port) => portHtml(node, port, "input"))
        .join(""),
      outputs = descriptor.outputs
        .map((port) => portHtml(node, port, "output"))
        .join("");
    element.innerHTML =
      '<div class="node-header"><strong>' +
      esc(nodeName(node, descriptor)) +
      '</strong><span class="node-type">' +
      esc(descriptor.type) +
      '</span></div><div class="node-ports"><div class="ports-column">' +
      inputs +
      '</div><div class="ports-column">' +
      outputs +
      "</div></div>";
    element.addEventListener("click", (event) => {
      if (!event.target.closest(".port-dot") && !element.dataset.dragged)
        selectNode(node.id);
    });
    bindNodeDrag(element, node);
    layer.append(element);
  }
  requestAnimationFrame(drawEdges);
}

function portHtml(node, port, direction) {
  const label =
    state.language === "en-US" ? port.name : port.displayName || port.name;
  return (
    '<div class="port ' +
    direction +
    '"><span class="port-dot" data-node="' +
    esc(node.id) +
    '" data-port="' +
    esc(port.name) +
    '" data-direction="' +
    direction +
    '" title="' +
    esc(port.name) +
    '"></span><span>' +
    esc(label) +
    "</span></div>"
  );
}

function drawEdges() {
  const svg = $("edgeLayer");
  svg.replaceChildren();
  if (!state.workflow) return;
  for (const edge of state.workflow.connections) {
    const from = findPort(edge.fromNode, edge.fromPort, "output"),
      to = findPort(edge.toNode, edge.toPort, "input");
    if (!from || !to) continue;
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", orthogonalPath(portPoint(from), portPoint(to)));
    path.setAttribute(
      "class",
      "edge" + (sameEdge(edge, state.selectedEdge) ? " selected" : ""),
    );
    path.addEventListener("click", (event) => {
      event.stopPropagation();
      state.selectedEdge = edge;
      state.selectedNodeId = null;
      renderWorkflow();
      renderInspector();
    });
    svg.append(path);
  }
  if (state.pendingConnection?.pointer) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute(
      "d",
      orthogonalPath(
        state.pendingConnection.start,
        state.pendingConnection.pointer,
      ),
    );
    path.setAttribute("class", "edge-temp");
    svg.append(path);
  }
}

function sameEdge(a, b) {
  return (
    b &&
    a.fromNode === b.fromNode &&
    a.fromPort === b.fromPort &&
    a.toNode === b.toNode &&
    a.toPort === b.toPort
  );
}

function findPort(node, port, direction) {
  return document.querySelector(
    '.port-dot[data-node="' +
      CSS.escape(node) +
      '"][data-port="' +
      CSS.escape(port) +
      '"][data-direction="' +
      direction +
      '"]',
  );
}

function portPoint(element) {
  const rect = element.getBoundingClientRect(),
    world = $("canvasWorld").getBoundingClientRect();
  return {
    x: rect.left + rect.width / 2 - world.left,
    y: rect.top + rect.height / 2 - world.top,
  };
}

function orthogonalPath(a, b) {
  const mid = (a.x + b.x) / 2,
    dir = b.y >= a.y ? 1 : -1,
    r = Math.min(14, Math.abs(b.y - a.y) / 2);
  return (
    "M " +
    a.x +
    " " +
    a.y +
    " H " +
    (mid - r) +
    " Q " +
    mid +
    " " +
    a.y +
    " " +
    mid +
    " " +
    (a.y + dir * r) +
    " V " +
    (b.y - dir * r) +
    " Q " +
    mid +
    " " +
    b.y +
    " " +
    (mid + r) +
    " " +
    b.y +
    " H " +
    b.x
  );
}

function applyPan() {
  $("canvasWorld").style.transform =
    "translate(" + state.panX + "px," + state.panY + "px)";
  $("canvasViewport").style.backgroundPosition =
    state.panX + "px " + state.panY + "px";
}

function bindCanvas() {
  const viewport = $("canvasViewport");
  let pan = null;
  viewport.addEventListener("pointerdown", (event) => {
    const output = event.target.closest?.('.port-dot[data-direction="output"]');
    if (output) {
      event.stopPropagation();
      state.pendingConnection = {
        fromNode: output.dataset.node,
        fromPort: output.dataset.port,
        start: portPoint(output),
        pointer: portPoint(output),
      };
      drawEdges();
      return;
    }
    if (
      event.target === viewport ||
      event.target === $("canvasWorld") ||
      event.target === $("edgeLayer")
    ) {
      state.selectedEdge = null;
      renderInspector();
      pan = {
        x: event.clientX,
        y: event.clientY,
        startX: state.panX,
        startY: state.panY,
      };
      viewport.classList.add("panning");
      viewport.setPointerCapture(event.pointerId);
    }
  });
  viewport.addEventListener("pointermove", (event) => {
    if (pan) {
      state.panX = pan.startX + event.clientX - pan.x;
      state.panY = pan.startY + event.clientY - pan.y;
      applyPan();
    }
    if (state.pendingConnection) {
      const world = $("canvasWorld").getBoundingClientRect();
      state.pendingConnection.pointer = {
        x: event.clientX - world.left,
        y: event.clientY - world.top,
      };
      drawEdges();
    }
  });
  viewport.addEventListener("pointerup", async (event) => {
    if (pan) {
      pan = null;
      viewport.classList.remove("panning");
    }
    if (state.pendingConnection) {
      const input = document
          .elementFromPoint(event.clientX, event.clientY)
          ?.closest?.('.port-dot[data-direction="input"]'),
        pending = state.pendingConnection;
      state.pendingConnection = null;
      drawEdges();
      if (input)
        await connect(
          pending.fromNode,
          pending.fromPort,
          input.dataset.node,
          input.dataset.port,
        );
    }
  });
  window.addEventListener("resize", drawEdges);
}

function bindNodeDrag(element, node) {
  const header = element.querySelector(".node-header");
  let drag = null;
  header.addEventListener("pointerdown", (event) => {
    event.stopPropagation();
    drag = {
      x: event.clientX,
      y: event.clientY,
      startX: node.x,
      startY: node.y,
      moved: false,
    };
    header.setPointerCapture(event.pointerId);
  });
  header.addEventListener("pointermove", (event) => {
    if (!drag) return;
    const dx = event.clientX - drag.x,
      dy = event.clientY - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 3) drag.moved = true;
    element.style.left = Math.round((drag.startX + dx) / 12) * 12 + "px";
    element.style.top = Math.round((drag.startY + dy) / 12) * 12 + "px";
    drawEdges();
  });
  header.addEventListener("pointerup", async () => {
    if (!drag) return;
    element.dataset.dragged = drag.moved ? "1" : "";
    const moved = drag.moved,
      x = parseFloat(element.style.left),
      y = parseFloat(element.style.top);
    drag = null;
    if (moved)
      await mutate(
        [{ kind: "moveNode", nodeId: node.id, x, y }],
        null,
        null,
        state.language === "en-US" ? "Move operator" : "移动算子",
      );
    setTimeout(() => delete element.dataset.dragged, 0);
  });
}

async function addNode(descriptor) {
  const viewport = $("canvasViewport").getBoundingClientRect(),
    index = state.workflow.nodes.length,
    x =
      Math.round((viewport.width * 0.28 - state.panX + (index % 4) * 34) / 12) *
      12,
    y =
      Math.round(
        (viewport.height * 0.18 - state.panY + (index % 6) * 52) / 12,
      ) * 12,
    id = "node-" + crypto.randomUUID();
  await mutate(
    [{ kind: "addNode", nodeId: id, operatorType: descriptor.type, x, y }],
    id,
    null,
    (state.language === "en-US" ? "Add " : "添加") + operatorName(descriptor),
  );
  selectNode(id, false);
}

async function connect(fromNode, fromPort, toNode, toPort) {
  await mutate(
    [{ kind: "connect", fromNode, fromPort, toNode, toPort }],
    toNode,
    null,
    state.language === "en-US" ? "Connect operators" : "连接算子",
  );
}

async function mutate(
  edits,
  selectNodeId = state.selectedNodeId,
  executeNodeId = null,
  summary = "Web workflow update",
) {
  try {
    const result = await api(
      "/api/projects/" + state.projectId + "/mutations",
      {
        method: "POST",
        body: JSON.stringify({
          requestId: "web-" + crypto.randomUUID(),
          expectedRevision: state.revision,
          edits,
          summary,
          selectNodeId,
          executeNodeId,
        }),
      },
    );
    state.revision = result.revision;
    state.workflow = result.workflow;
    state.selectedNodeId = result.selectedNodeId || selectNodeId;
    state.selectedEdge = null;
    renderWorkflow();
    renderInspector();
    refreshOperations();
    setStatus(text("saved"));
    return result;
  } catch (error) {
    if (error.status === 409) await reloadSnapshot();
    notify(error.message, true);
    throw error;
  }
}

async function reloadSnapshot() {
  const snapshot = await api("/api/projects/" + state.projectId + "/snapshot");
  state.revision = snapshot.revision;
  state.workflow = snapshot.workflow;
  renderWorkflow();
  renderInspector();
}

async function selectNode(id, execute = true) {
  state.selectedNodeId = id;
  state.selectedEdge = null;
  renderWorkflow();
  renderInspector();
  if (execute) await executeNode(id);
}

async function executeNode(id) {
  setStatus(text("running"));
  try {
    const result = await api("/api/projects/" + state.projectId + "/execute", {
      method: "POST",
      body: JSON.stringify({
        requestId: "web-exec-" + crypto.randomUUID(),
        targetNodeId: id,
      }),
    });
    await showResult(result);
    setStatus(result.success ? text("ready") : result.error || "Error");
  } catch (error) {
    setStatus(error.message);
    notify(error.message, true);
  }
}

async function deleteSelection() {
  if (state.selectedEdge) {
    const edge = state.selectedEdge;
    await mutate(
      [{ kind: "disconnect", ...edge }],
      null,
      null,
      state.language === "en-US" ? "Delete connection" : "删除连接",
    );
    return;
  }
  if (!state.selectedNodeId) return;
  if (!confirm(text("confirmDelete"))) return;
  const id = state.selectedNodeId;
  state.selectedNodeId = null;
  await mutate(
    [{ kind: "removeNode", nodeId: id }],
    null,
    null,
    state.language === "en-US" ? "Delete operator" : "删除算子",
  );
}

async function undo() {
  if (!state.projectId) return;
  try {
    const snapshot = await api("/api/projects/" + state.projectId + "/undo", {
      method: "POST",
    });
    state.revision = snapshot.revision;
    state.workflow = snapshot.workflow;
    state.selectedNodeId = null;
    renderWorkflow();
    renderInspector();
    refreshOperations();
    notify(text("undo"));
  } catch (error) {
    notify(error.message, true);
  }
}

async function autoLayout() {
  if (!state.workflow?.nodes.length) return;
  const indegree = Object.fromEntries(
      state.workflow.nodes.map((node) => [node.id, 0]),
    ),
    outgoing = Object.fromEntries(
      state.workflow.nodes.map((node) => [node.id, []]),
    );
  state.workflow.connections.forEach((edge) => {
    indegree[edge.toNode]++;
    outgoing[edge.fromNode].push(edge.toNode);
  });
  const queue = state.workflow.nodes
      .filter((node) => indegree[node.id] === 0)
      .map((node) => node.id),
    levels = {};
  queue.forEach((id) => (levels[id] = 0));
  while (queue.length) {
    const id = queue.shift();
    for (const next of outgoing[id]) {
      levels[next] = Math.max(levels[next] || 0, levels[id] + 1);
      if (--indegree[next] === 0) queue.push(next);
    }
  }
  const rows = {},
    edits = state.workflow.nodes.map((node) => {
      const level = levels[node.id] || 0;
      rows[level] = (rows[level] || 0) + 1;
      return {
        kind: "moveNode",
        nodeId: node.id,
        x: 60 + level * 250,
        y: 60 + (rows[level] - 1) * 140,
      };
    });
  await mutate(
    edits,
    state.selectedNodeId,
    null,
    state.language === "en-US" ? "Auto layout" : "自动排版",
  );
}
