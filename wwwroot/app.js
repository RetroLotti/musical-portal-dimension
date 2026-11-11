const tagList = document.querySelector("#tagList");
const tagCount = document.querySelector("#tagCount");
const trackQueueElement = document.querySelector("#trackQueue");
const queueCount = document.querySelector("#queueCount");
const sourceSelect = document.querySelector("#sourceSelect");
const localFields = document.querySelector("#localFields");
const spotifyFields = document.querySelector("#spotifyFields");
const formMessage = document.querySelector("#formMessage");
const toast = document.querySelector("#toast");
const mediaDialog = document.querySelector("#mediaDialog");
const availableMediaList = document.querySelector("#availableMediaList");

let library = { registeredTags: [] };
let mediaFiles = [];
let uploadQueue = [];
let queueUid = null;
let editingUid = null;
let spotifyEnabled = false;
let selectedMediaPaths = new Set();
let toastTimer;

async function request(url, options = {}) {
  const response = await fetch(url, options);
  const body = response.headers.get("content-type")?.includes("application/json")
    ? await response.json()
    : null;
  if (!response.ok) throw new Error(body?.message ?? `Request failed (${response.status})`);
  return body;
}

function showToast(message) {
  toast.textContent = message;
  toast.classList.add("is-visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("is-visible"), 2800);
}

function renderTags() {
  const query = document.querySelector("#searchInput").value.trim().toLowerCase();
  const tags = (library.registeredTags ?? []).filter(tag =>
    `${tag.name ?? ""} ${tag.uid ?? ""}`.toLowerCase().includes(query));
  tagCount.textContent = String((library.registeredTags ?? []).length);
  tagList.replaceChildren();

  if (tags.length === 0) {
    const empty = document.createElement("p");
    empty.className = "empty-state";
    empty.textContent = query ? "No matching tags" : "No tags registered";
    tagList.append(empty);
    return;
  }

  for (const [index, tag] of tags.entries()) {
    const row = document.createElement("article");
    row.className = "tag-row";
    row.style.animationDelay = `${Math.min(index, 8) * 25}ms`;

    const info = document.createElement("div");
    const name = document.createElement("p");
    name.className = "tag-name";
    name.textContent = tag.name || "Unnamed tag";
    const detail = document.createElement("p");
    detail.className = "tag-detail";
    detail.textContent = `${tag.uid} · ${(tag.songs ?? []).map(song => song.name || song.path || song.spotifyId || song.type).join(", ")}`;
    info.append(name, detail);

    const kind = document.createElement("span");
    const source = tag.songs?.[0]?.type ?? "local";
    kind.className = `tag-kind${source === "spotify" ? " spotify" : ""}`;
    kind.textContent = source;
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "tag-edit";
    edit.textContent = "✎";
    edit.title = "Edit tag and playlist";
    edit.setAttribute("aria-label", `Edit ${tag.name || "tag"}`);
    edit.disabled = source === "spotify" && !spotifyEnabled;
    edit.addEventListener("click", () => editTag(tag).catch(error => showToast(error.message)));
    row.append(info, kind, edit);
    tagList.append(row);
  }
}

async function loadMedia() {
  mediaFiles = await request("/api/media");
  return mediaFiles;
}

async function refresh() {
  const [nextLibrary, status] = await Promise.all([
    request("/api/library"),
    request("/api/status")
  ]);
  library = nextLibrary;
  spotifyEnabled = status.spotifyEnabled;
  renderTags();
  const readerStatus = document.querySelector("#readerStatus");
  const readerIndicator = document.querySelector("#readerIndicator");
  readerStatus.textContent = status.readerConnected ? "Reader connected" : "Reader offline";
  readerIndicator.classList.toggle("is-connected", status.readerConnected);

  const spotifyStatus = document.querySelector("#spotifyStatus");
  spotifyStatus.textContent = status.spotifyEnabled ? "Spotify enabled" : "Local only";
  const spotifyOption = sourceSelect.querySelector('option[value="spotify"]');
  spotifyOption.hidden = !status.spotifyEnabled;
  if (!status.spotifyEnabled && sourceSelect.value === "spotify") sourceSelect.value = "local";
  updateSourceFields();
}

function updateSourceFields() {
  const isLocal = sourceSelect.value === "local";
  localFields.hidden = !isLocal;
  spotifyFields.hidden = isLocal;
  document.querySelector("#spotifyId").required = !isLocal;
}

function normalizedMediaPath(path) {
  return path.replace(/[\\/]/g, "#").toLowerCase();
}

function renderAvailableMedia() {
  const query = document.querySelector("#mediaSearch").value.trim().toLowerCase();
  const files = mediaFiles.filter(file => `${file.name} ${file.path}`.toLowerCase().includes(query));
  availableMediaList.replaceChildren();

  if (files.length === 0) {
    const empty = document.createElement("p");
    empty.className = "dialog-empty";
    empty.textContent = query ? "No matching files" : "No media files found under media/";
    availableMediaList.append(empty);
    return;
  }

  for (const file of files) {
    const label = document.createElement("label");
    label.className = "available-media-item";
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = selectedMediaPaths.has(file.path);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) selectedMediaPaths.add(file.path);
      else selectedMediaPaths.delete(file.path);
      updateSelectedMediaCount();
    });

    const copy = document.createElement("span");
    copy.className = "available-media-copy";
    const name = document.createElement("span");
    name.className = "available-media-name";
    name.textContent = file.name;
    const path = document.createElement("span");
    path.className = "available-media-path";
    path.textContent = file.path.replace(/^media#/, "").replace(/#/g, " /");
    copy.append(name, path);
    label.append(checkbox, copy);
    availableMediaList.append(label);
  }

  updateSelectedMediaCount();
}

function updateSelectedMediaCount() {
  document.querySelector("#selectedMediaCount").textContent = `${selectedMediaPaths.size} selected`;
}

async function openMediaDialog() {
  const uid = normalizedUid(document.querySelector("#uidInput").value);
  if (!uid) {
    showToast("Enter or scan the tag UID first.");
    return;
  }

  try {
    await loadMedia();
    selectedMediaPaths = new Set();
    document.querySelector("#mediaSearch").value = "";
    renderAvailableMedia();
    mediaDialog.showModal();
  } catch (error) {
    showToast(error.message);
  }
}

function addSelectedMedia() {
  const uid = normalizedUid(document.querySelector("#uidInput").value);
  if (!uid) {
    showToast("Enter or scan the tag UID first.");
    mediaDialog.close();
    return;
  }

  if (queueUid && queueUid !== uid) uploadQueue = [];
  queueUid = uid;
  const existingPaths = new Set(uploadQueue.map(file => normalizedMediaPath(file.path)));
  for (const file of mediaFiles.filter(item => selectedMediaPaths.has(item.path))) {
    if (!existingPaths.has(normalizedMediaPath(file.path))) uploadQueue.push(file);
  }

  renderQueue();
  mediaDialog.close();
}

async function editTag(tag) {
  const songs = tag.songs ?? [];
  const source = songs[0]?.type ?? "local";
  if (source === "spotify" && !spotifyEnabled) {
    showToast("Enable Spotify in config.json to edit Spotify tags.");
    return;
  }

  if (source === "local") await loadMedia();
  editingUid = tag.uid;
  queueUid = tag.uid;
  document.querySelector("#uidInput").value = tag.uid;
  document.querySelector("#uidInput").readOnly = true;
  document.querySelector("#nameInput").value = tag.name ?? "";
  sourceSelect.value = source;
  document.querySelector("#spotifyType").value = songs[0]?.spotifyType ?? "track";
  document.querySelector("#spotifyId").value = songs[0]?.spotifyId ?? "";

  if (source === "local") {
    uploadQueue = songs.filter(song => song.path).map(song => {
      const file = mediaFiles.find(item => normalizedMediaPath(item.path) === normalizedMediaPath(song.path));
      if (file) return file;

      const name = song.path.split(/[\\/#]/).at(-1);
      return {
        name: name || song.name || "Unknown media",
        path: song.path,
        extension: `.${(name || "").split(".").at(-1)}`,
        size: 0
      };
    });
  } else {
    uploadQueue = [];
  }

  formMessage.textContent = "";
  formMessage.classList.remove("error");
  document.querySelector("#editorTitle").textContent = "Edit tag";
  document.querySelector("#saveLabel").textContent = "Save changes";
  document.querySelector("#cancelEditButton").hidden = false;
  document.querySelector("#uploadStatus").textContent = source === "local"
    ? "Add or browse files, then arrange playback order."
    : "";
  updateSourceFields();
  renderQueue();
  document.querySelector("#editorTitle").scrollIntoView({ behavior: "smooth", block: "center" });
}

function cancelEdit() {
  editingUid = null;
  queueUid = null;
  uploadQueue = [];
  document.querySelector("#tagForm").reset();
  document.querySelector("#uidInput").readOnly = false;
  document.querySelector("#editorTitle").textContent = "Register a tag";
  document.querySelector("#saveLabel").textContent = "Add tag";
  document.querySelector("#cancelEditButton").hidden = true;
  document.querySelector("#formMessage").textContent = "";
  document.querySelector("#formMessage").classList.remove("error");
  document.querySelector("#uploadStatus").textContent = "Scan or enter the UID first. Audio and M3U files supported.";
  updateSourceFields();
  renderQueue();
}

function normalizedUid(value) {
  const compact = value.replace(/[-:\s]/g, "").toUpperCase();
  if (compact.length < 2 || compact.length % 2 !== 0 || !/^[0-9A-F]+$/.test(compact)) return null;
  return compact.match(/.{2}/g).join("-");
}

function fileSize(size) {
  return size < 1024 * 1024
    ? `${Math.max(1, Math.round(size / 1024))} KB`
    : `${(size / (1024 * 1024)).toFixed(1)} MB`;
}

function renderQueue() {
  trackQueueElement.replaceChildren();
  queueCount.textContent = `${uploadQueue.length} track${uploadQueue.length === 1 ? "" : "s"}`;

  if (uploadQueue.length === 0) {
    const empty = document.createElement("li");
    empty.className = "queue-empty";
    empty.textContent = "Add one or more files. Their order here is the playback order.";
    trackQueueElement.append(empty);
    return;
  }

  for (const [index, file] of uploadQueue.entries()) {
    const item = document.createElement("li");
    item.className = "track-item";

    const position = document.createElement("span");
    position.className = "track-position";
    position.textContent = String(index + 1).padStart(2, "0");

    const copy = document.createElement("div");
    copy.className = "track-copy";
    const title = document.createElement("p");
    title.className = "track-title";
    title.textContent = file.name;
    const meta = document.createElement("p");
    meta.className = "track-meta";
    meta.textContent = `${file.extension.replace(".", "").toUpperCase()} · ${fileSize(file.size)}`;
    copy.append(title, meta);

    const actions = document.createElement("div");
    actions.className = "track-actions";
    actions.append(
      queueAction("↑", "Move up", index, -1, index === 0),
      queueAction("↓", "Move down", index, 1, index === uploadQueue.length - 1),
      queueAction("×", "Remove", index, 0, false, true)
    );
    item.append(position, copy, actions);
    trackQueueElement.append(item);
  }
}

function queueAction(symbol, label, index, offset, disabled, remove = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `track-action${remove ? " remove" : ""}`;
  button.textContent = symbol;
  button.title = label;
  button.setAttribute("aria-label", label);
  button.disabled = disabled;
  button.addEventListener("click", () => {
    if (remove) uploadQueue.splice(index, 1);
    else [uploadQueue[index], uploadQueue[index + offset]] = [uploadQueue[index + offset], uploadQueue[index]];
    renderQueue();
  });
  return button;
}

async function captureUid() {
  const button = document.querySelector("#captureButton");
  button.disabled = true;
  button.textContent = "Hold tag near reader";
  try {
    await request("/api/reader/capture", { method: "POST" });
    for (let attempt = 0; attempt < 90; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 350));
      const state = await request("/api/reader/capture");
      if (state.capturedUid) {
        const uidInput = document.querySelector("#uidInput");
        uidInput.value = state.capturedUid;
        uidInput.dispatchEvent(new Event("input", { bubbles: true }));
        showToast(`Captured ${state.capturedUid}`);
        return;
      }
      if (!state.isCapturing) break;
    }
    showToast("No tag was read. Enter its UID manually.");
  } catch (error) {
    showToast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "Scan tag";
  }
}

async function uploadSelectedFiles(event) {
  const files = [...event.target.files];
  if (!files.length) return;
  const uid = normalizedUid(document.querySelector("#uidInput").value);
  const status = document.querySelector("#uploadStatus");
  if (!uid) {
    status.textContent = "Enter or scan a valid UID before adding files.";
    event.target.value = "";
    return;
  }

  if (queueUid && queueUid !== uid) uploadQueue = [];
  queueUid = uid;
  status.textContent = `Uploading 0/${files.length}`;
  try {
    for (const [index, file] of files.entries()) {
      const formData = new FormData();
      formData.append("file", file);
      formData.append("uid", uid);
      const uploaded = await request("/api/media", { method: "POST", body: formData });
      uploadQueue.push(uploaded);
      renderQueue();
      status.textContent = `Uploading ${index + 1}/${files.length}`;
    }
    status.textContent = `${files.length} file${files.length === 1 ? "" : "s"} uploaded`;
    showToast(`${files.length} file${files.length === 1 ? "" : "s"} added to queue`);
  } catch (error) {
    status.textContent = "Upload failed";
    showToast(error.message);
  } finally {
    event.target.value = "";
  }
}

async function createTag(event) {
  event.preventDefault();
  formMessage.textContent = "";
  formMessage.classList.remove("error");
  const source = sourceSelect.value;
  const payload = {
    uid: document.querySelector("#uidInput").value,
    name: document.querySelector("#nameInput").value,
    type: source,
    paths: source === "local" ? uploadQueue.map(file => file.path) : [],
    spotifyType: source === "spotify" ? document.querySelector("#spotifyType").value : null,
    spotifyId: source === "spotify" ? document.querySelector("#spotifyId").value : null
  };

  if (source === "local" && uploadQueue.length === 0) {
    formMessage.textContent = "Add at least one audio or playlist file.";
    formMessage.classList.add("error");
    return;
  }

  const submit = document.querySelector("#saveButton");
  submit.disabled = true;
  try {
    const url = editingUid ? `/api/tags/${encodeURIComponent(editingUid)}` : "/api/tags";
    await request(url, {
      method: editingUid ? "PUT" : "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
    await refresh();
    const wasEditing = Boolean(editingUid);
    cancelEdit();
    formMessage.textContent = wasEditing ? "Tag and playlist updated" : "Tag saved to music.json";
    showToast(wasEditing ? "Tag updated" : "Tag registered");
  } catch (error) {
    formMessage.textContent = error.message;
    formMessage.classList.add("error");
  } finally {
    submit.disabled = false;
  }
}

sourceSelect.addEventListener("change", updateSourceFields);
document.querySelector("#searchInput").addEventListener("input", renderTags);
document.querySelector("#captureButton").addEventListener("click", captureUid);
document.querySelector("#mediaUpload").addEventListener("change", uploadSelectedFiles);
document.querySelector("#existingMediaButton").addEventListener("click", openMediaDialog);
document.querySelector("#mediaSearch").addEventListener("input", renderAvailableMedia);
document.querySelector("#addSelectedMedia").addEventListener("click", addSelectedMedia);
document.querySelector("#closeMediaDialog").addEventListener("click", () => mediaDialog.close());
document.querySelector("#cancelEditButton").addEventListener("click", cancelEdit);
document.querySelector("#uidInput").addEventListener("input", event => {
  const uid = normalizedUid(event.target.value);
  if (uploadQueue.length && uid !== queueUid) {
    uploadQueue = [];
    queueUid = null;
    renderQueue();
    document.querySelector("#uploadStatus").textContent = "UID changed. Add files again for this tag.";
  }
});
document.querySelector("#tagForm").addEventListener("submit", createTag);
document.querySelector("#refreshButton").addEventListener("click", () => refresh().catch(error => showToast(error.message)));

refresh().catch(error => showToast(error.message));
renderQueue();