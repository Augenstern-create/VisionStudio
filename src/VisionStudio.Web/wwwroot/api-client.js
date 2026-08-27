async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("X-VisionStudio-Token", state.token);
  if (options.body && !(options.body instanceof FormData))
    headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers });
  if (response.status === 401) {
    showAuth();
    const error = new Error("访问令牌无效 / Invalid access token");
    error.status = 401;
    throw error;
  }
  if (!response.ok) {
    let problem = {};
    try {
      problem = await response.json();
    } catch {}
    const error = new Error(
      safeMessage(problem.error || "HTTP " + response.status),
    );
    error.status = response.status;
    error.details = problem.details;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

async function apiBlob(path) {
  const response = await fetch(path, {
    headers: { "X-VisionStudio-Token": state.token },
  });
  if (!response.ok) throw new Error("HTTP " + response.status);
  return response.blob();
}
