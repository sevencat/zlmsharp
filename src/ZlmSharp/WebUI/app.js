// zlmsharp 内置管理页面：零构建，直接调用 ZLM 兼容 API。
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const state = { secret: localStorage.getItem("zlmsharp.secret") || "", player: null };

  const log = (message, data) => {
    const line = `[${new Date().toLocaleTimeString()}] ${message}` +
      (data === undefined ? "" : `\n${JSON.stringify(data, null, 2)}`);
    $("log").textContent = `${line}\n\n${$("log").textContent}`.slice(0, 20000);
  };

  const request = async (name, params = {}) => {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== null && `${value}`.length > 0) query.set(key, value);
    }
    if (state.secret) query.set("secret", state.secret);

    const response = await fetch(`/index/api/${name}?${query}`, { cache: "no-store" });
    const text = await response.text();
    let body;
    try {
      body = JSON.parse(text);
    } catch {
      throw new Error(`${name} 返回非 JSON（HTTP ${response.status}）：${text.slice(0, 120)}`);
    }

    if (body.code !== 0 && body.code !== undefined) {
      throw new Error(`${name} 失败：${body.msg || body.code}`);
    }

    return body;
  };

  const call = async (name, params) => {
    try {
      const body = await request(name, params);
      log(`${name} ok`, body.data ?? body);
      return body.data;
    } catch (error) {
      log(`${name} 失败：${error.message}`);
      throw error;
    }
  };

  const bytes = (value) => (value > 1024 * 1024 ? `${(value / 1048576).toFixed(1)} MB` : `${Math.round(value / 1024)} KB`);

  const renderStats = (media) => {
    const streams = media || [];
    const readers = streams.reduce((sum, item) => sum + (item.readerCount || 0), 0);
    const drops = streams.reduce((sum, item) => sum + (item.dropFrames || 0), 0);
    const cards = [
      ["在线流", streams.length],
      ["读者", readers],
      ["总丢帧", drops],
      ["API", "v" + ($("version").dataset.version || "-")],
    ];

    $("stats").innerHTML = cards
      .map(([label, value]) => `<div class="card"><div class="value">${value}</div><div class="label">${label}</div></div>`)
      .join("");
  };

  const streamName = (item) => `${item.app}/${item.stream}`;

  const renderStreams = (media) => {
    const tbody = $("streams");
    if (!media || media.length === 0) {
      tbody.innerHTML = `<tr><td colspan="7" class="muted">暂无在线流</td></tr>`;
      $("streamHint").textContent = "";
      return;
    }

    tbody.innerHTML = media.map((item) => `
      <tr>
        <td title="${item.vhost}/${item.app}/${item.stream}">${streamName(item)}</td>
        <td>${item.videoCodec || "-"}${item.audioCodec ? ` / ${item.audioCodec}` : ""}</td>
        <td>${item.videoWidth ? `${item.videoWidth}x${item.videoHeight}` : "-"}${item.videoFps ? ` @${Math.round(item.videoFps)}` : ""}</td>
        <td>${item.readerCount ?? 0}</td>
        <td><span class="badge ${item.originType === "rtmp" ? "ok" : ""}">${item.originType || "-"}</span></td>
        <td>${item.dropFrames ?? 0}</td>
        <td class="actions">
          <button data-act="play" data-app="${item.app}" data-stream="${item.stream}">播放</button>
          <button data-act="record" data-app="${item.app}" data-stream="${item.stream}">录像</button>
          <button data-act="stopRecord" data-app="${item.app}" data-stream="${item.stream}">停录</button>
          <button data-act="overlay" data-app="${item.app}" data-stream="${item.stream}">YOLO 叠加</button>
          <button data-act="close" data-app="${item.app}" data-stream="${item.stream}" class="danger">关闭</button>
        </td>
      </tr>`).join("");

    $("streamHint").textContent = `共 ${media.length} 路`;
  };

  const refresh = async () => {
    try {
      const version = await request("version");
      $("version").textContent = `v${version.data.buildVersion} · ${version.data.branchName}/${version.data.commitHash}`;
      $("version").dataset.version = version.data.buildVersion;

      const media = await call("getMediaList", {});
      renderStats(media);
      renderStreams(media);
    } catch (error) {
      $("version").textContent = `连接失败：${error.message}`;
    }
  };

  const play = (app, stream) => {
    $("playerPanel").hidden = false;
    $("playerTitle").textContent = `${app}/${stream}`;
    const url = `/${app}/${stream}.live.flv`;

    if (state.player) {
      state.player.destroy();
      state.player = null;
    }

    if (window.mpegts && window.mpegts.isSupported()) {
      state.player = window.mpegts.createPlayer({ type: "flv", url, isLive: true });
      state.player.attachMediaElement($("player"));
      state.player.load();
      state.player.play().catch(() => {});
      $("playerHint").textContent = `HTTP-FLV：${url}`;
    } else {
      $("playerHint").textContent = `缺少 mpegts.js（离线环境）：${url}`;
    }
  };

  const loadSessions = async () => {
    const data = await call("getSessionList", {});
    const rows = (data || []).map((item) => `
      <tr>
        <td>${item.id}</td>
        <td>${item.type || "-"}</td>
        <td>${item.app ? `${item.app}/${item.stream}` : "-"}</td>
        <td>${item.ip || "-"}:${item.port || 0}</td>
        <td><button data-act="kick" data-id="${item.id}" class="danger">踢出</button></td>
      </tr>`);
    $("sessions").innerHTML = rows.join("") || `<tr><td colspan="5" class="muted">没有会话</td></tr>`;
  };

  const loadRecords = async () => {
    const app = $("recordApp").value.trim();
    const stream = $("recordStream").value.trim();
    if (!app || !stream) return;

    const data = await call("getMp4RecordFile", { app, stream });
    const roots = (data && data.paths) || [];
    $("records").innerHTML = roots.length
      ? roots.map((item) => `<li>${item}</li>`).join("")
      : `<li class="muted">没有录像</li>`;
  };

  document.addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-act]");
    if (!button) return;

    const { act, app, stream, id } = button.dataset;
    try {
      if (act === "play") play(app, stream);
      if (act === "record") await call("startRecord", { app, stream, type: 5 });
      if (act === "stopRecord") await call("stopRecord", { app, stream });
      if (act === "overlay") await call("startYoloOverlay", { app, stream });
      if (act === "close") await call("close_streams", { app, stream });
      if (act === "kick") await call("kick_session", { id });
      if (act === "deleteRecord") await call("deleteRecordFile", { app, stream, path: button.dataset.path });

      if (act === "close" || act === "kick") await refresh();
    } catch {
      /* 错误已写进日志 */
    }
  });

  $("saveSecret").onclick = () => {
    state.secret = $("secret").value.trim();
    localStorage.setItem("zlmsharp.secret", state.secret);
    log("secret 已保存");
    refresh();
  };

  $("refresh").onclick = refresh;
  $("loadSessions").onclick = () => loadSessions().catch(() => {});
  $("loadRecords").onclick = () => loadRecords().catch(() => {});

  $("addProxy").onclick = async () => {
    const app = $("proxyApp").value.trim();
    const stream = $("proxyStream").value.trim();
    const url = $("proxyUrl").value.trim();
    if (!app || !stream || !url) return;
    await call("addStreamProxy", { app, stream, url });
    await refresh();
  };

  $("addPush").onclick = async () => {
    const app = $("pushApp").value.trim();
    const stream = $("pushStream").value.trim();
    const url = $("pushUrl").value.trim();
    if (!app || !stream || !url) return;
    await call("startPush", { app, stream, url });
  };

  $("loadYolo").onclick = async () => {
    const stream = $("aiStream").value.trim();
    const data = await call("getYoloResult", { stream });
    $("aiOutput").textContent = JSON.stringify(data, null, 2);
  };

  $("loadAnalysis").onclick = async () => {
    const stream = $("aiStream").value.trim();
    const data = await call("analyzeResult", { stream });
    $("aiOutput").textContent = JSON.stringify(data, null, 2);
  };

  $("closePlayer").onclick = () => {
    if (state.player) {
      state.player.destroy();
      state.player = null;
    }
    $("playerPanel").hidden = true;
  };

  $("clearLog").onclick = () => {
    $("log").textContent = "";
  };

  if (state.secret) $("secret").value = state.secret;
  refresh();
  setInterval(refresh, 5000);
})();
