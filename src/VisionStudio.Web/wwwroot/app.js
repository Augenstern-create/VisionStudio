"use strict";

const state = {
  token: sessionStorage.getItem("visionstudio-token") || "",
  projectId: null,
  revision: 0,
  workflow: null,
  catalog: [],
  selectedNodeId: null,
  selectedEdge: null,
  panX: 80,
  panY: 60,
  language: localStorage.getItem("visionstudio-language") || "zh-CN",
  theme: localStorage.getItem("visionstudio-theme") || "dark",
  previewUrl: null,
  pendingConnection: null,
};
const $ = (id) => document.getElementById(id);
const t = {
  "zh-CN": {
    authHint: "输入服务器访问令牌",
    token: "访问令牌",
    login: "进入工作台",
    file: "文件",
    quick: "快捷操作",
    view: "视图",
    help: "帮助",
    search: "搜索算子",
    welcome: "视觉算法工作室",
    welcomeHint: "在服务器上创建或打开一个项目",
    newProject: "新建项目",
    newProjectHint: "创建完整项目目录与工作流",
    refresh: "刷新项目",
    refreshHint: "重新读取服务器项目目录",
    serverProjects: "服务器项目",
    operators: "算子",
    undo: "撤销",
    layout: "自动排版",
    export: "导出 C#",
    resultAndProperties: "结果与属性",
    emptyCanvas: "从左侧添加算子",
    emptyCanvasHint: "拖动端口连接，点击算子查看结果",
    selectNode: "选择算子查看结果",
    operations: "操作记录",
    ready: "就绪",
    projectName: "项目名称",
    cancel: "取消",
    create: "创建",
    aboutText: "服务器端 OpenCV 可视化工作流编辑器",
    close: "关闭",
    noProjects: "暂无项目",
    open: "打开",
    interface: "接口",
    execute: "执行",
    delete: "删除",
    upload: "选择图片",
    running: "正在执行…",
    saved: "已保存",
    created: "项目已创建",
    exported: "C# 已导出",
    uploadDone: "图片已上传",
    noOutput: "没有可显示的输出",
    confirmDelete: "确定删除这个算子吗？",
  },
  "en-US": {
    authHint: "Enter the server access token",
    token: "Access token",
    login: "Open Workbench",
    file: "File",
    quick: "Quick Actions",
    view: "View",
    help: "Help",
    search: "Search operators",
    welcome: "Visual Algorithm Studio",
    welcomeHint: "Create or open a project on this server",
    newProject: "New Project",
    newProjectHint: "Create the project folders and workflow",
    refresh: "Refresh",
    refreshHint: "Reload the server project directory",
    serverProjects: "Server Projects",
    operators: "Operators",
    undo: "Undo",
    layout: "Auto Layout",
    export: "Export C#",
    resultAndProperties: "Result & Properties",
    emptyCanvas: "Add an operator from the left",
    emptyCanvasHint: "Drag ports to connect; click a node to preview",
    selectNode: "Select a node to view its result",
    operations: "Operations",
    ready: "Ready",
    projectName: "Project name",
    cancel: "Cancel",
    create: "Create",
    aboutText: "Server-side visual OpenCV workflow editor",
    close: "Close",
    noProjects: "No projects",
    open: "Open",
    interface: "Interface",
    execute: "Execute",
    delete: "Delete",
    upload: "Choose image",
    running: "Running…",
    saved: "Saved",
    created: "Project created",
    exported: "C# exported",
    uploadDone: "Image uploaded",
    noOutput: "No displayable output",
    confirmDelete: "Delete this node?",
  },
};
const text = (key) => t[state.language][key] || key;
const esc = (value) =>
  String(value ?? "").replace(
    /[&<>'"]/g,
    (ch) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[
        ch
      ],
  );
const categoryNames = {
  输入: "Input",
  输出: "Output",
  图像: "Image",
  基础处理: "Basic Processing",
  滤波: "Filtering",
  增强: "Enhancement",
  阈值: "Thresholding",
  二值处理: "Binary Processing",
  分割: "Segmentation",
  形态学: "Morphology",
  边缘: "Edges and Gradients",
  Blob: "Region Analysis",
  轮廓: "Contours",
  几何: "Geometry",
  定位: "Localization",
  "直线/圆": "Lines and Circles",
  变换: "Transforms",
  图像运算: "Image Arithmetic",
  统计: "Statistics",
  测量: "Measurement",
  标定: "Calibration",
  逻辑: "Logic",
  集合: "Collections",
  显示: "Drawing",
  AI: "AI",
};
const englishOperations = {
  添加: "Add",
  修改: "Change",
  移动算子: "Move operator",
  连接算子: "Connect operators",
  删除连接: "Delete connection",
  删除算子: "Delete operator",
  自动排版: "Auto layout",
  "Web 撤销": "Web undo",
  撤销上一项工作流修改: "Undo the previous workflow change",
};
const categoryName = (value) =>
  state.language === "en-US" ? categoryNames[value] || value : value;
const safeMessage = (value) =>
  state.language === "en-US" && /[\u3400-\u9fff]/u.test(String(value || ""))
    ? "The operation could not be completed."
    : String(value || "");
const operationText = (value) =>
  state.language === "en-US"
    ? Object.entries(englishOperations).reduce(
        (result, [source, target]) => result.replaceAll(source, target),
        String(value || ""),
      )
    : String(value || "");

document.addEventListener("DOMContentLoaded", initialize);
async function initialize() {
  document.body.dataset.theme = state.theme;
  applyLanguage();
  bindStaticEvents();
  applyPan();
  if (state.token) {
    try {
      await loadProjects();
      showApp();
    } catch (error) {
      if (error.status === 401) showAuth();
      else notify(error.message, true);
    }
  } else showAuth();
}
function bindStaticEvents() {
  $("authForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    state.token = $("tokenInput").value.trim();
    sessionStorage.setItem("visionstudio-token", state.token);
    try {
      await loadProjects();
      showApp();
    } catch (error) {
      $("authError").textContent = error.message;
      state.token = "";
      sessionStorage.removeItem("visionstudio-token");
    }
  });
  $("newProjectButton").onclick = () => {
    $("projectNameInput").value = "";
    $("newProjectDialog").showModal();
    setTimeout(() => $("projectNameInput").focus(), 0);
  };
  $("refreshProjectsButton").onclick = loadProjects;
  $("newProjectForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    await createProject($("projectNameInput").value);
    $("newProjectDialog").close();
  });
  $("homeButton").onclick = showProjects;
  $("undoButton").onclick = undo;
  $("layoutButton").onclick = autoLayout;
  $("exportButton").onclick = exportCode;
  $("fileMenuButton").onclick = () =>
    state.projectId ? exportCode() : $("newProjectDialog").showModal();
  $("editMenuButton").onclick = undo;
  $("viewMenuButton").onclick = cycleTheme;
  $("helpButton").onclick = () => $("aboutDialog").showModal();
  $("languageButton").onclick = toggleLanguage;
  $("themeButton").onclick = cycleTheme;
  $("globalSearch").addEventListener("input", renderPalette);
  $("paletteToggle").onclick = () => $("palette").classList.add("open");
  $("inspectorToggle").onclick = () => $("inspector").classList.add("open");
  document
    .querySelectorAll("[data-close]")
    .forEach(
      (button) =>
        (button.onclick = () =>
          $(button.dataset.close).classList.remove("open")),
    );
  $("imageUpload").addEventListener("change", uploadImage);
  bindCanvas();
  document.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "z") {
      event.preventDefault();
      undo();
    } else if (event.key === "Delete" && !isEditing()) {
      deleteSelection();
    }
  });
}
function isEditing() {
  return ["INPUT", "TEXTAREA", "SELECT"].includes(
    document.activeElement?.tagName,
  );
}
function showAuth() {
  $("authView").classList.remove("hidden");
  $("app").classList.add("hidden");
  $("tokenInput").focus();
}
function showApp() {
  $("authView").classList.add("hidden");
  $("app").classList.remove("hidden");
}
function showProjects() {
  state.projectId = null;
  state.workflow = null;
  state.selectedNodeId = null;
  $("projectView").classList.remove("hidden");
  $("editorView").classList.add("hidden");
  $("projectBadge").textContent = "";
  loadProjects();
}
function showEditor() {
  $("projectView").classList.add("hidden");
  $("editorView").classList.remove("hidden");
}
function applyLanguage() {
  document.documentElement.lang = state.language;
  document
    .querySelectorAll("[data-i18n]")
    .forEach((el) => (el.textContent = text(el.dataset.i18n)));
  document
    .querySelectorAll("[data-i18n-placeholder]")
    .forEach((el) => (el.placeholder = text(el.dataset.i18nPlaceholder)));
  $("languageButton").textContent =
    state.language === "zh-CN" ? "中 / EN" : "EN / 中";
  if (state.workflow) {
    renderPalette();
    renderWorkflow();
    renderInspector();
  }
}
function toggleLanguage() {
  state.language = state.language === "zh-CN" ? "en-US" : "zh-CN";
  localStorage.setItem("visionstudio-language", state.language);
  applyLanguage();
}
function cycleTheme() {
  const themes = ["dark", "light", "gradient"];
  state.theme = themes[(themes.indexOf(state.theme) + 1) % themes.length];
  document.body.dataset.theme = state.theme;
  localStorage.setItem("visionstudio-theme", state.theme);
  requestAnimationFrame(drawEdges);
}

function setStatus(message) {
  $("statusText").textContent = message;
  $("statusRevision").textContent = "Revision " + state.revision;
}
let toastTimer;
function notify(message, error = false) {
  const toast = $("toast");
  toast.textContent = message;
  toast.style.borderColor = error ? "var(--danger)" : "var(--border)";
  toast.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("show"), 3200);
}
