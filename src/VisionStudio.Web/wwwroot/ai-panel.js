"use strict";

// Web AI assistant: same agent and read-only tools as the desktop shell, streamed over SSE.
(function () {
  const els = {};
  let abortController = null;
  let modelsLoaded = false;

  function ensureRefs() {
    els.panel = $("aiPanel");
    els.toggle = $("aiToggle");
    els.close = $("aiClose");
    els.model = $("aiModel");
    els.messages = $("aiMessages");
    els.prompt = $("aiPrompt");
    els.send = $("aiSend");
    els.stop = $("aiStop");
    return Boolean(els.panel && els.toggle);
  }

  function appendMessage(role, text) {
    const card = document.createElement("div");
    card.className = "ai-message " + role;
    const meta = document.createElement("span");
    meta.className = "ai-meta";
    meta.textContent = role === "user" ? "你" : role === "error" ? "错误" : "AI 助手";
    const reasoning = document.createElement("span");
    reasoning.className = "ai-reasoning";
    reasoning.hidden = true;
    const body = document.createElement("span");
    body.className = "ai-body";
    body.textContent = text || "";
    card.append(meta, reasoning, body);
    els.messages.append(card);
    els.messages.scrollTop = els.messages.scrollHeight;
    return { card, body, reasoning };
  }

  async function loadModels() {
    if (modelsLoaded) return;
    try {
      const models = await api("/api/ai/models");
      els.model.innerHTML = "";
      if (!models.length) {
        const option = document.createElement("option");
        option.textContent = "服务器未配置模型";
        option.value = "";
        els.model.append(option);
        appendMessage("error", "服务器还没有配置远程模型：请在服务器上写入 ~/.config/visionstudio/ai-remote-models.json（与桌面端“文件 → 设置”相同的加密格式）。");
        return;
      }
      for (const model of models) {
        const option = document.createElement("option");
        option.value = model.id;
        option.textContent = `${model.displayName} · ${model.effectiveModelId}`;
        els.model.append(option);
      }
      modelsLoaded = true;
    } catch (error) {
      appendMessage("error", error.message);
    }
  }

  async function send() {
    const text = els.prompt.value.trim();
    if (!text) return;
    if (!state.projectId) {
      appendMessage("error", "请先打开一个项目。");
      return;
    }
    if (!els.model.value) {
      await loadModels();
      if (!els.model.value) return;
    }

    appendMessage("user", text);
    els.prompt.value = "";
    const target = appendMessage("assistant", "");
    setBusy(true);
    abortController = new AbortController();

    try {
      const response = await fetch(`/api/projects/${state.projectId}/ai/chat`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-VisionStudio-Token": state.token,
        },
        body: JSON.stringify({
          modelId: els.model.value,
          text,
          selectedNodeId: state.selectedNodeId || null,
        }),
        signal: abortController.signal,
      });

      if (response.status === 401) {
        showAuth();
        throw new Error("访问令牌无效 / Invalid access token");
      }
      if (!response.ok || !response.body) {
        let detail = "";
        try {
          detail = (await response.json()).error || "";
        } catch {}
        throw new Error(detail || "HTTP " + response.status);
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let boundary = buffer.indexOf("\n\n");
        while (boundary >= 0) {
          const chunk = buffer.slice(0, boundary).trim();
          buffer = buffer.slice(boundary + 2);
          boundary = buffer.indexOf("\n\n");
          if (!chunk.startsWith("data:")) continue;
          let event;
          try {
            event = JSON.parse(chunk.slice(5).trim());
          } catch {
            continue;
          }
          handleEvent(event, target);
        }
      }
    } catch (error) {
      if (error.name === "AbortError") {
        target.body.textContent += "\n\n（已停止）";
      } else {
        target.card.className = "ai-message error";
        target.body.textContent = error.message;
      }
    } finally {
      abortController = null;
      setBusy(false);
    }
  }

  function handleEvent(event, target) {
    switch (event.kind) {
      case "reasoning":
        target.reasoning.hidden = false;
        target.reasoning.textContent = (target.reasoning.textContent + event.text).slice(-900);
        break;
      case "tool":
        target.reasoning.hidden = false;
        target.reasoning.textContent += `\n[执行节点 ${event.text}]`;
        break;
      case "content":
        target.body.textContent += event.text;
        break;
      case "plan":
        target.body.textContent += `\n\n建议改动：${event.text}（可在桌面端“应用改动”）`;
        break;
      case "error":
        target.card.className = "ai-message error";
        target.body.textContent = event.text;
        break;
      default:
        break;
    }

    els.messages.scrollTop = els.messages.scrollHeight;
  }

  function setBusy(busy) {
    els.send.disabled = busy;
    els.stop.classList.toggle("hidden", !busy);
  }

  function togglePanel(show) {
    const visible = show === undefined ? els.panel.classList.contains("hidden") : show;
    els.panel.classList.toggle("hidden", !visible);
    if (visible) {
      loadModels();
      els.prompt.focus();
    }
  }

  function wire() {
    if (!ensureRefs()) return;
    els.toggle.addEventListener("click", () => togglePanel());
    els.close.addEventListener("click", () => togglePanel(false));
    els.send.addEventListener("click", send);
    els.stop.addEventListener("click", () => abortController && abortController.abort());
    els.prompt.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        send();
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", wire);
  } else {
    wire();
  }
})();
