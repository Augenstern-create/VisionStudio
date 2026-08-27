async function loadProjects() {
  const projects = await api("/api/projects");
  $("projectList").replaceChildren();
  if (!projects.length) {
    $("projectList").innerHTML =
      '<div class="empty-list">' + esc(text("noProjects")) + "</div>";
    return;
  }
  projects.forEach((project) => {
    const button = document.createElement("button");
    button.className = "project-row";
    button.innerHTML =
      "<span><strong>" +
      esc(project.name) +
      "</strong><small>" +
      esc(new Date(project.modifiedAt).toLocaleString()) +
      "</small></span><span>" +
      esc(text("open")) +
      " ›</span>";
    button.onclick = () => openProject(project.id);
    $("projectList").append(button);
  });
}

async function createProject(name) {
  const project = await api("/api/projects", {
    method: "POST",
    body: JSON.stringify({ name }),
  });
  notify(text("created"));
  await loadProjects();
  await openProject(project.id);
}

async function openProject(id) {
  setStatus("Loading…");
  state.projectId = id;
  const [snapshot, catalog] = await Promise.all([
    api("/api/projects/" + id + "/snapshot"),
    api("/api/projects/" + id + "/catalog"),
  ]);
  state.revision = snapshot.revision;
  state.workflow = snapshot.workflow;
  state.catalog = catalog;
  state.selectedNodeId = null;
  state.selectedEdge = null;
  state.panX = 80;
  state.panY = 60;
  applyPan();
  $("projectBadge").textContent = snapshot.projectName;
  $("statusProject").textContent = snapshot.projectName;
  $("workflowTitle").textContent = snapshot.workflow.name;
  showEditor();
  renderPalette();
  renderWorkflow();
  renderInspector();
  refreshOperations();
  setStatus(text("ready"));
}

async function uploadImage() {
  const file = $("imageUpload").files[0],
    node = state.workflow?.nodes.find(
      (item) => item.id === state.selectedNodeId,
    );
  if (!file || !node) return;
  const form = new FormData();
  form.append("file", file, file.name);
  try {
    const uploaded = await api("/api/projects/" + state.projectId + "/images", {
        method: "POST",
        body: form,
      }),
      parameter = descriptorFor(node).parameters.find(
        (item) => item.name === "path",
      );
    await setParameter(node, parameter, uploaded.path);
    notify(text("uploadDone"));
  } finally {
    $("imageUpload").value = "";
  }
}

async function exportCode() {
  if (!state.projectId) return;
  try {
    const target = state.selectedNodeId
        ? "?targetNodeId=" + encodeURIComponent(state.selectedNodeId)
        : "",
      result = await api(
        "/api/projects/" + state.projectId + "/export" + target,
        { method: "POST" },
      ),
      blob = await apiBlob(result.downloadUrl),
      url = URL.createObjectURL(blob),
      anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = "GeneratedVisionAlgorithm.cs";
    anchor.click();
    URL.revokeObjectURL(url);
    notify(text("exported"));
  } catch (error) {
    notify(error.message, true);
  }
}

async function refreshOperations() {
  if (!state.projectId) return;
  try {
    const operations = await api(
      "/api/projects/" + state.projectId + "/operations",
    );
    $("operationsList").replaceChildren();
    operations
      .slice()
      .reverse()
      .forEach((item) => {
        const row = document.createElement("div");
        row.className = "operation-row";
        row.innerHTML =
          esc(operationText(item.action)) +
          "<small>" +
          esc(operationText(item.detail || "")) +
          " · " +
          esc(new Date(item.timestamp).toLocaleString()) +
          "</small>";
        $("operationsList").append(row);
      });
  } catch {}
}
