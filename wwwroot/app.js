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
const spotifySearchDialog = document.querySelector("#spotifySearchDialog");
const spotifySearchResults = document.querySelector("#spotifySearchResults");
let spotifySearchController;

let library = { registeredTags: [] };
let mediaFiles = [];
let uploadQueue = [];
let spotifyQueue = [];
let queueDrag;
let queueUid = null;
let editingUid = null;
let spotifyEnabled = false;
let bluetoothDevices = [];
let bluetoothManaged = false;
let mediaMaxFileSizeMb = 512;
let selectedMediaPaths = new Set();
let toastTimer;

async function request(url, options = {}) {
  const response = await fetch(url, options);
  const body = response.headers.get("content-type")?.includes("application/json")
    ? await response.json()
    : null;
  if (!response.ok) throw new Error(body?.message ?? body?.detail ?? `Request failed (${response.status})`);
  return body;
}

function showToast(message) {
  toast.textContent = message;
  toast.classList.add("is-visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("is-visible"), 2800);
}

function audioOutputName(value) {
  if (!value || value === "headphones") return "Pi headphone jack";
  const address = value.replace(/^bluetooth:/i, "");
  return bluetoothDevices.find(device => device.address.toLowerCase() === address.toLowerCase())?.name || `Bluetooth ${address}`;
}

function renderAudioOutputOptions(selected = document.querySelector("#audioOutput").value) {
  const picker = document.querySelector("#audioOutput");
  const options = [new Option("Pi headphone jack", "headphones")];
  for (const device of bluetoothDevices.filter(item => item.paired)) {
    options.push(new Option(`${device.name}${device.connected ? " (connected)" : ""}`, `bluetooth:${device.address}`));
  }
  if (selected !== "headphones" && !options.some(option => option.value === selected))
    options.push(new Option(`${audioOutputName(selected)} (not connected)`, selected));
  picker.replaceChildren(...options);
  picker.value = selected || "headphones";
}

function renderBluetoothDevicePicker(devices) {
  const picker = document.querySelector("#bluetoothDevicePicker");
  const options = [new Option(devices.length ? "Choose a device" : "No devices found", "")];
  for (const device of devices) {
    options.push(new Option(`${device.name} - ${device.address}${device.connected ? " (connected)" : device.paired ? " (paired)" : ""}`, device.address));
  }
  picker.replaceChildren(...options);
  picker.disabled = !bluetoothManaged || devices.length === 0;
  document.querySelector("#connectBluetoothButton").disabled = !bluetoothManaged || !picker.value;
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
    detail.textContent = `${tag.uid} · ${audioOutputName(tag.audioOutput)} · ${tag.resumePlayback ? "Resume enabled · " : ""}${(tag.songs ?? []).map(song => song.name || song.path || song.spotifyId || song.type).join(", ")}`;
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
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "tag-delete";
    remove.textContent = "×";
    remove.title = "Delete registered tag";
    remove.setAttribute("aria-label", `Delete ${tag.name || "tag"}`);
    remove.addEventListener("click", () => deleteTag(tag).catch(error => showToast(error.message)));
    row.append(info, kind, edit, remove);
    tagList.append(row);
  }
}

async function deleteTag(tag) {
  if (!window.confirm(`Delete the registration for "${tag.name || tag.uid}"? Uploaded media files will be kept.`)) return;

  await request(`/api/tags/${encodeURIComponent(tag.uid)}`, { method: "DELETE" });
  if (editingUid === tag.uid) cancelEdit();
  await refresh();
  showToast("Tag registration deleted; media files kept");
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
  await loadBluetoothStatus();
  document.querySelector("#forwardUid").value = configuredControlUid("next");
  document.querySelector("#rewindUid").value = configuredControlUid("previous");
  document.querySelector("#volumeUpUid").value = configuredControlUid("volumeup");
  document.querySelector("#volumeDownUid").value = configuredControlUid("volumedown");
  document.querySelector("#shuffleUid").value = configuredControlUid("shuffle");
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

async function loadMediaSettings() {
  const settings = await request("/api/settings/media");
  mediaMaxFileSizeMb = settings.maxFileSizeMb;
  document.querySelector("#mediaMaxFileSizeMb").value = String(mediaMaxFileSizeMb);
}

async function saveMediaSettings(event) {
  event.preventDefault();
  const button = document.querySelector("#saveMediaSettingsButton");
  const message = document.querySelector("#mediaSettingsMessage");
  const input = document.querySelector("#mediaMaxFileSizeMb");
  const value = Number(input.value);
  message.textContent = "";
  message.classList.remove("error");
  if (!Number.isInteger(value) || value < 1 || value > 4096) {
    message.textContent = "Choose a whole number between 1 and 4096 MiB.";
    message.classList.add("error");
    return;
  }
  button.disabled = true;
  try {
    const settings = await request("/api/settings/media", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ maxFileSizeMb: value })
    });
    mediaMaxFileSizeMb = settings.maxFileSizeMb;
    input.value = String(mediaMaxFileSizeMb);
    message.textContent = `Upload limit saved: ${mediaMaxFileSizeMb} MiB per file`;
    document.querySelector("#uploadStatus").textContent = `Audio and M3U/M3U8 files supported. Maximum ${mediaMaxFileSizeMb} MiB each.`;
  } catch (error) {
    message.textContent = error.message;
    message.classList.add("error");
  } finally {
    button.disabled = false;
  }
}

function configuredControlUid(action) {
  const aliases = { forward: "next", rewind: "previous", volumneup: "volumeup", volumnedown: "volumedown" };
  return Object.entries(library.controlTags ?? {}).find(([, value]) => (aliases[value.toLowerCase()] ?? value.toLowerCase()) === action)?.[0] ?? "";
}

function updateSourceFields() {
  const isLocal = sourceSelect.value === "local";
  localFields.hidden = !isLocal;
  spotifyFields.hidden = isLocal;
  document.querySelector("#spotifyId").required = !isLocal && spotifyQueue.length === 0;
  renderQueue();
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
  document.querySelector("#spotifySelectionMessage").textContent = "";
  const songs = tag.songs ?? [];
  const source = songs[0]?.type ?? "local";
  if (source === "spotify" && !spotifyEnabled) {
    showToast("Enable Spotify in Spotify settings to edit Spotify tags.");
    return;
  }

  if (source === "local") await loadMedia();
  editingUid = tag.uid;
  queueUid = tag.uid;
  document.querySelector("#uidInput").value = tag.uid;
  document.querySelector("#uidInput").readOnly = true;
  document.querySelector("#nameInput").value = tag.name ?? "";
  document.querySelector("#resumePlayback").checked = Boolean(tag.resumePlayback);
  renderAudioOutputOptions(tag.audioOutput ?? "headphones");
  sourceSelect.value = source;
  document.querySelector("#spotifyType").value = songs[0]?.spotifyType ?? "track";
  document.querySelector("#spotifyId").value = "";
  spotifyQueue = source === "spotify" ? songs.map(song => ({
    name: song.name || song.spotifyId,
    spotifyType: song.spotifyType,
    spotifyId: song.spotifyId
  })) : [];

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
  document.querySelector("#spotifySelectionMessage").textContent = "";
  editingUid = null;
  queueUid = null;
  uploadQueue = [];
  spotifyQueue = [];
  document.querySelector("#tagForm").reset();
  renderAudioOutputOptions("headphones");
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
  queueDrag?.cancel();
  const queue = sourceSelect.value === "spotify" ? spotifyQueue : uploadQueue;
  const isSpotify = sourceSelect.value === "spotify";
  trackQueueElement.replaceChildren();
  queueCount.textContent = `${queue.length} ${isSpotify && queue.some(item => item.spotifyType !== "track") ? "item" : "track"}${queue.length === 1 ? "" : "s"}`;
  document.querySelector("#queueMessage").textContent = "";

  if (queue.length === 0) {
    const empty = document.createElement("li");
    empty.className = "queue-empty";
    empty.textContent = isSpotify ? "No Spotify items added" : "Add one or more files. Their order here is the playback order.";
    trackQueueElement.append(empty);
    return;
  }

  for (const [index, file] of queue.entries()) {
    const item = document.createElement("li");
    item.className = "track-item";
    item.dataset.index = String(index);
    const handle = document.createElement("button");
    handle.type = "button";
    handle.className = "track-drag-handle";
    handle.textContent = "⠿";
    handle.title = `Drag to reorder ${file.name}`;
    handle.setAttribute("aria-label", `Drag to reorder ${file.name}`);
    handle.disabled = queue.length < 2;
    enableQueueDrag(handle, item, index, queue);

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
    meta.textContent = isSpotify ? `Spotify ${file.spotifyType}${file.subtitle ? ` · ${file.subtitle}` : ""}`
      : `${file.extension.replace(".", "").toUpperCase()} · ${fileSize(file.size)}`;
    copy.append(title, meta);

    const actions = document.createElement("div");
    actions.className = "track-actions";
    actions.append(
      queueAction("↑", "Move up", queue, index, -1, index === 0),
      queueAction("↓", "Move down", queue, index, 1, index === queue.length - 1),
      queueAction("×", "Remove", queue, index, 0, false, true)
    );
    item.append(handle, position, copy, actions);
    trackQueueElement.append(item);
  }
}

function moveQueueItem(queue, from, to) {
  if (from === to || to < 0 || to >= queue.length) return;
  const [moved] = queue.splice(from, 1);
  queue.splice(to, 0, moved);
  renderQueue();
  document.querySelector("#queueMessage").textContent = `${moved.name} moved to position ${to + 1}`;
  trackQueueElement.querySelector(`[data-index="${to}"] .track-drag-handle`)?.focus({ preventScroll: true });
}

function enableQueueDrag(handle, item, index, queue) {
  handle.addEventListener("pointerdown", event => {
    if (event.button !== 0 || queue.length < 2) return;
    event.preventDefault();
    queueDrag?.cancel();
    handle.setPointerCapture(event.pointerId);
    const pointerId = event.pointerId;
    const startY = event.clientY;
    let pointerX = event.clientX;
    let pointerY = event.clientY;
    let insertionIndex = null;
    let moved = false;
    let animation;
    const clearMarkers = () => {
      for (const row of trackQueueElement.children) row.classList.remove("drop-before", "drop-after");
    };
    const markTarget = () => {
      clearMarkers();
      insertionIndex = null;
      const bounds = trackQueueElement.getBoundingClientRect();
      if (!moved || pointerX < bounds.left || pointerX > bounds.right || pointerY < bounds.top || pointerY > bounds.bottom) return;
      const rows = [...trackQueueElement.children];
      const row = rows.find(candidate => {
        const bounds = candidate.getBoundingClientRect();
        return pointerY < bounds.top + bounds.height / 2;
      });
      insertionIndex = row ? Number(row.dataset.index) : queue.length;
      (row ?? rows.at(-1))?.classList.add(row ? "drop-before" : "drop-after");
    };
    const frame = () => {
      if (!queueDrag) return;
      const bounds = trackQueueElement.getBoundingClientRect();
      if (moved && pointerX >= bounds.left && pointerX <= bounds.right && pointerY >= bounds.top && pointerY <= bounds.bottom) {
        if (pointerY < bounds.top + 30) trackQueueElement.scrollTop -= 6;
        else if (pointerY > bounds.bottom - 30) trackQueueElement.scrollTop += 6;
        markTarget();
      }
      animation = requestAnimationFrame(frame);
    };
    const move = next => {
      if (next.pointerId !== pointerId) return;
      pointerX = next.clientX;
      pointerY = next.clientY;
      moved ||= Math.abs(pointerY - startY) > 5;
      markTarget();
    };
    const finish = commit => {
      queueDrag = null;
      cancelAnimationFrame(animation);
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", drop);
      handle.removeEventListener("pointercancel", cancel);
      handle.removeEventListener("lostpointercapture", cancel);
      document.removeEventListener("keydown", escape);
      if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
      clearMarkers();
      item.classList.remove("is-dragging");
      trackQueueElement.classList.remove("is-dragging");
      if (commit && moved && insertionIndex !== null) {
        const target = insertionIndex > index ? insertionIndex - 1 : insertionIndex;
        moveQueueItem(queue, index, target);
      }
    };
    const drop = next => {
      if (next.pointerId !== pointerId) return;
      move(next);
      finish(true);
    };
    const cancel = () => finish(false);
    const escape = next => { if (next.key === "Escape") finish(false); };
    queueDrag = { cancel };
    item.classList.add("is-dragging");
    trackQueueElement.classList.add("is-dragging");
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", drop);
    handle.addEventListener("pointercancel", cancel);
    handle.addEventListener("lostpointercapture", cancel);
    document.addEventListener("keydown", escape);
    animation = requestAnimationFrame(frame);
  });
}

function queueAction(symbol, label, queue, index, offset, disabled, remove = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `track-action${remove ? " remove" : ""}`;
  button.textContent = symbol;
  button.title = label;
  button.setAttribute("aria-label", label);
  button.disabled = disabled;
  button.addEventListener("click", () => {
    if (remove) {
      queue.splice(index, 1);
      document.querySelector("#spotifyId").required = sourceSelect.value === "spotify" && spotifyQueue.length === 0;
      renderQueue();
    } else moveQueueItem(queue, index, index + offset);
  });
  return button;
}

async function captureUid(input, button) {
  const originalLabel = button.textContent;
  button.disabled = true;
  button.textContent = "Hold tag near reader";
  try {
    await request("/api/reader/capture", { method: "POST" });
    for (let attempt = 0; attempt < 90; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 350));
      const state = await request("/api/reader/capture");
      if (state.capturedUid) {
        input.value = state.capturedUid;
        input.dispatchEvent(new Event("input", { bubbles: true }));
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
    button.textContent = originalLabel;
  }
}

async function saveControlTags() {
  const inputs = ["forwardUid", "rewindUid", "volumeUpUid", "volumeDownUid", "shuffleUid"];
  const payload = {};
  const message = document.querySelector("#controlMessage");
  const button = document.querySelector("#saveControlsButton");
  message.textContent = "";
  for (const id of inputs) {
    const value = document.getElementById(id).value.trim();
    const normalized = value ? normalizedUid(value) : "";
    if (normalized === null) {
      message.textContent = "Enter valid hexadecimal tag UIDs.";
      message.classList.add("error");
      return;
    }
    payload[id] = normalized;
  }
  const assignedUids = Object.values(payload).filter(Boolean);
  if (new Set(assignedUids).size !== assignedUids.length) {
    message.textContent = "Each playback control must use a different tag.";
    message.classList.add("error");
    return;
  }

  button.disabled = true;
  try {
    await request("/api/control-tags", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
    message.classList.remove("error");
    message.textContent = "Controls saved";
    await refresh();
    showToast("Playback control tags saved");
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

async function uploadSelectedFiles(event) {
  const selectedFiles = [...event.target.files];
  if (!selectedFiles.length) return;
  const files = selectedFiles.filter(file => file.size <= mediaMaxFileSizeMb * 1024 * 1024);
  const rejectedFiles = selectedFiles.filter(file => file.size > mediaMaxFileSizeMb * 1024 * 1024);
  const uid = normalizedUid(document.querySelector("#uidInput").value);
  const status = document.querySelector("#uploadStatus");
  const rejectedNames = rejectedFiles.slice(0, 3).map(file => file.name).join(", ");
  const rejectedMore = rejectedFiles.length > 3 ? ` and ${rejectedFiles.length - 3} more` : "";
  const rejectedMessage = rejectedFiles.length
    ? `Skipped ${rejectedFiles.length} over-limit file${rejectedFiles.length === 1 ? "" : "s"}: ${rejectedNames}${rejectedMore}`
    : "";
  if (!files.length) {
    status.textContent = `No files added. ${rejectedMessage}. Maximum file size is ${mediaMaxFileSizeMb} MiB.`;
    showToast(rejectedMessage);
    event.target.value = "";
    return;
  }
  if (!uid) {
    status.textContent = `Enter or scan a valid UID before adding files.${rejectedMessage ? ` ${rejectedMessage}.` : ""}`;
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
    status.textContent = `${files.length} file${files.length === 1 ? "" : "s"} uploaded${rejectedMessage ? `; ${rejectedMessage.toLowerCase()}` : ""}`;
    showToast(`${files.length} file${files.length === 1 ? "" : "s"} added to queue${rejectedMessage ? `. ${rejectedMessage}` : ""}`);
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
  if (source === "spotify" && document.querySelector("#spotifyId").value.trim() && !addManualSpotifyItem()) return;
  const payload = {
    uid: document.querySelector("#uidInput").value,
    name: document.querySelector("#nameInput").value,
    type: source,
    paths: source === "local" ? uploadQueue.map(file => file.path) : [],
    spotifyType: source === "spotify" ? document.querySelector("#spotifyType").value : null,
    spotifyId: null,
    spotifyItems: source === "spotify" ? spotifyQueue.map(item => ({ name: item.name, spotifyType: item.spotifyType, spotifyId: item.spotifyId })) : null,
    resumePlayback: document.querySelector("#resumePlayback").checked,
    audioOutput: document.querySelector("#audioOutput").value
  };

  if (source === "local" && uploadQueue.length === 0) {
    formMessage.textContent = "Add at least one audio or playlist file.";
    formMessage.classList.add("error");
    return;
  }
  if (source === "spotify" && spotifyQueue.length === 0) {
    formMessage.textContent = "Add at least one Spotify track, album, or playlist.";
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

let spotifyDevices = [];
let receiverManaged = false;

async function loadReceiverStatus() {
  const status = await request("/api/spotify/receiver");
  receiverManaged = status.managed;
  document.querySelector("#receiverSettings").disabled = !receiverManaged;
  const message = document.querySelector("#receiverStatus");
  message.classList.toggle("error", Boolean(status.error) && receiverManaged);
  if (!receiverManaged) {
    message.textContent = status.error;
    return;
  }
  const name = status.name ? ` - ${status.name}` : "";
  const audio = status.audioDevice ? ` - ${status.audioDevice}` : "";
  const login = !status.apiKeyConfigured ? "Soloist API key required." : status.loggedIn === true
    ? "Spotify Connect session authenticated." : status.loggedIn === false ? "Pairing required: select this Pi once in Spotify on the same LAN."
    : "Spotify Connect session status unavailable.";
  const update = status.update ? ` Update: ${status.update.state}${status.update.version ? ` (${status.update.version})` : ""}.${status.update.error ? ` ${status.update.error}` : ""}` : "";
  message.textContent = status.error ?? `Soloist: ${status.activeState}${name}${audio}. ${login}${update}`;
}

async function loadBluetoothStatus() {
  const status = await request("/api/audio/bluetooth");
  bluetoothManaged = status.managed;
  bluetoothDevices = status.devices ?? [];
  renderBluetoothDevicePicker(bluetoothDevices);
  renderAudioOutputOptions();
  document.querySelector("#bluetoothMessage").textContent = status.message ?? "";
  document.querySelector("#scanBluetoothButton").disabled = !bluetoothManaged || status.state === "running";
}

async function runBluetoothOperation(operation) {
  const scanButton = document.querySelector("#scanBluetoothButton");
  const connectButton = document.querySelector("#connectBluetoothButton");
  const message = document.querySelector("#bluetoothMessage");
  scanButton.disabled = true;
  connectButton.disabled = true;
  message.classList.remove("error");
  message.textContent = operation === "scan" ? "Scanning for nearby devices..." : "Pairing and connecting...";
  try {
    const address = document.querySelector("#bluetoothDevicePicker").value;
    const result = await request(`/api/audio/bluetooth/${operation}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      ...(operation === "connect" ? { body: JSON.stringify({ address }) } : {})
    });
    for (let attempt = 0; attempt < 55; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 1000));
      const status = await request("/api/audio/bluetooth");
      if (status.requestId !== result.requestId) continue;
      bluetoothDevices = status.devices ?? [];
      renderBluetoothDevicePicker(bluetoothDevices);
      renderAudioOutputOptions();
      if (status.state === "error") throw new Error(status.message || "Bluetooth setup failed.");
      if (status.state === "complete") {
        message.textContent = status.message || "Bluetooth setup complete.";
        if (bluetoothDevices.length) {
          const preferred = operation === "connect" ? address : bluetoothDevices[0].address;
          document.querySelector("#bluetoothDevicePicker").value = bluetoothDevices.some(device => device.address === preferred)
            ? preferred : bluetoothDevices[0].address;
          connectButton.disabled = !bluetoothManaged || !document.querySelector("#bluetoothDevicePicker").value;
        }
        return;
      }
    }
    throw new Error("Bluetooth setup is taking longer than expected. Refresh status before trying again.");
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    scanButton.disabled = !bluetoothManaged;
    connectButton.disabled = !bluetoothManaged || !document.querySelector("#bluetoothDevicePicker").value;
  }
}

document.querySelector("#scanBluetoothButton").addEventListener("click", () => runBluetoothOperation("scan"));
document.querySelector("#connectBluetoothButton").addEventListener("click", () => runBluetoothOperation("connect"));
document.querySelector("#bluetoothDevicePicker").addEventListener("change", event => {
  document.querySelector("#connectBluetoothButton").disabled = !bluetoothManaged || !event.target.value;
});

document.querySelector("#refreshReceiverStatus").addEventListener("click", async event => {
  const button = event.currentTarget;
  button.disabled = true;
  try {
    await loadReceiverStatus();
  } catch (error) {
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
});

async function loadSpotifyDevices() {
  const button = document.querySelector("#loadSpotifyDevicesButton");
  const picker = document.querySelector("#spotifyAvailableDevices");
  const label = document.querySelector("#spotifyDevicesLabel");
  const message = document.querySelector("#spotifyDevicesMessage");
  button.disabled = true;
  picker.disabled = true;
  picker.hidden = true;
  label.hidden = true;
  picker.replaceChildren();
  spotifyDevices = [];
  message.classList.remove("error");
  message.textContent = "Finding Spotify devices...";
  try {
    spotifyDevices = await request("/api/spotify/devices");
    if (!spotifyDevices.length) {
      message.textContent = "No devices visible to Spotify. Check that the Pi's Spotify Connect receiver is running and signed in.";
      return;
    }
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "Choose a device";
    picker.append(placeholder);
    for (const [index, device] of spotifyDevices.entries()) {
      const option = document.createElement("option");
      option.value = String(index);
      option.textContent = `${device.name} - ${device.type}${device.isActive ? " (Active)" : ""}${device.isRestricted ? " (Restricted)" : ""}`;
      option.disabled = device.isRestricted;
      picker.append(option);
    }
    const configuredName = document.querySelector("#spotifyDeviceName").value;
    const configuredIndex = spotifyDevices.findIndex(device => device.name === configuredName);
    picker.value = configuredIndex >= 0 ? String(configuredIndex) : "";
    picker.hidden = false;
    label.hidden = false;
    picker.disabled = false;
    message.textContent = `${spotifyDevices.length} device${spotifyDevices.length === 1 ? "" : "s"} available`;
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

document.querySelector("#loadSpotifyDevicesButton").addEventListener("click", loadSpotifyDevices);
document.querySelector("#spotifyAvailableDevices").addEventListener("change", event => {
  if (event.target.value === "") return;
  const device = spotifyDevices[Number(event.target.value)];
  if (!device || device.isRestricted) return;
  document.querySelector("#spotifyDeviceName").value = device.name;
  document.querySelector("#spotifyDevicesMessage").textContent = "Device selected (not saved)";
});
document.querySelector("#spotifyDeviceName").addEventListener("input", () => {
  document.querySelector("#spotifyAvailableDevices").value = "";
});

async function loadSpotifySettings() {
  const settings = await request("/api/settings/spotify");
  document.querySelector("#spotifyEnable").checked = settings.enable;
  document.querySelector("#spotifyDeviceName").value = settings.spotifyDeviceName ?? "";
  document.querySelector("#spotifyClientId").value = settings.clientId ?? "";
  document.querySelector("#spotifyClientSecret").value = "";
  document.querySelector("#spotifyClientSecret").placeholder = settings.hasClientSecret ? "Saved (unchanged)" : "Not set";
  document.querySelector("#spotifyClearSecret").checked = false;
  document.querySelector("#receiverEnabled").checked = settings.receiver?.enabled ?? true;
  document.querySelector("#receiverAudioDevice").value = settings.receiver?.audioDevice ?? "plughw:CARD=Headphones,DEV=0";
  document.querySelector("#soloistApiKey").value = "";
  document.querySelector("#soloistApiKey").placeholder = settings.receiver?.hasSoloistApiKey ? "Saved (unchanged)" : "Not set";
  document.querySelector("#clearSoloistApiKey").checked = false;
  await loadReceiverStatus();
  await loadSpotifyAuthorization();
}

function renderSpotifyAuthorization(status) {
  document.querySelector("#spotifyAuthorizationStatus").textContent = status.pending
    ? "Authorization pending" : status.authorized ? "Connected" : "Not connected";
  document.querySelector("#refreshSpotifyTokenButton").disabled = !status.authorized;
  if (!status.pending) {
    document.querySelector("#spotifyCallbackForm").hidden = true;
    document.querySelector("#spotifyCallbackUrl").value = "";
    document.querySelector("#spotifyAuthorizationLink").removeAttribute("href");
  }
}

async function loadSpotifyAuthorization() {
  const status = await request("/api/spotify/auth");
  renderSpotifyAuthorization(status);
  return status;
}

async function connectSpotify() {
  const button = document.querySelector("#connectSpotifyButton");
  const message = document.querySelector("#spotifyAuthorizationMessage");
  button.disabled = true;
  message.textContent = "";
  message.classList.remove("error");
  try {
    const authorization = await request("/api/spotify/auth/start", { method: "POST" });
    document.querySelector("#spotifyAuthorizationLink").href = authorization.authorizationUrl;
    document.querySelector("#spotifyRedirectUri").value = authorization.redirectUri;
    document.querySelector("#spotifyCallbackUrl").value = "";
    document.querySelector("#spotifyCallbackForm").hidden = false;
    await loadSpotifyAuthorization();
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

async function completeSpotifyConnection(event) {
  event.preventDefault();
  const button = document.querySelector("#completeSpotifyButton");
  const message = document.querySelector("#spotifyAuthorizationMessage");
  const input = document.querySelector("#spotifyCallbackUrl");
  const callbackUrl = input.value.trim();
  input.value = "";
  button.disabled = true;
  message.textContent = "";
  message.classList.remove("error");
  try {
    const status = await request("/api/spotify/auth/complete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ callbackUrl })
    });
    renderSpotifyAuthorization(status);
    message.textContent = "Spotify connected";
    showToast("Spotify connected");
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

async function refreshSpotifyAccess() {
  const button = document.querySelector("#refreshSpotifyTokenButton");
  const message = document.querySelector("#spotifyAuthorizationMessage");
  button.disabled = true;
  message.textContent = "";
  message.classList.remove("error");
  try {
    const status = await request("/api/spotify/auth/refresh", { method: "POST" });
    renderSpotifyAuthorization(status);
    message.textContent = "Spotify access renewed";
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    await loadSpotifyAuthorization().catch(() => { button.disabled = false; });
  }
}

document.querySelector("#connectSpotifyButton").addEventListener("click", connectSpotify);
document.querySelector("#spotifyCallbackForm").addEventListener("submit", completeSpotifyConnection);
document.querySelector("#refreshSpotifyTokenButton").addEventListener("click", refreshSpotifyAccess);

async function saveSpotifySettings(event) {
  event.preventDefault();
  const button = document.querySelector("#saveSpotifyButton");
  const message = document.querySelector("#spotifySettingsMessage");
  button.disabled = true;
  message.textContent = "";
  message.classList.remove("error");
  try {
    await request("/api/settings/spotify", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        enable: document.querySelector("#spotifyEnable").checked,
        spotifyDeviceName: document.querySelector("#spotifyDeviceName").value,
        clientId: document.querySelector("#spotifyClientId").value,
        clientSecret: document.querySelector("#spotifyClearSecret").checked
          ? "" : document.querySelector("#spotifyClientSecret").value || null,
        receiver: receiverManaged ? {
          enabled: document.querySelector("#receiverEnabled").checked,
          audioDevice: document.querySelector("#receiverAudioDevice").value.trim(),
          soloistApiKey: document.querySelector("#clearSoloistApiKey").checked
            ? "" : document.querySelector("#soloistApiKey").value.trim() || null
        } : null
      })
    });
    if (editingUid && sourceSelect.value === "spotify" && !document.querySelector("#spotifyEnable").checked) cancelEdit();
    await Promise.all([loadSpotifySettings(), refresh()]);
    message.textContent = receiverManaged ? "Spotify settings saved; receiver changes apply in up to 30 seconds." : "Spotify settings saved";
    showToast("Spotify settings saved");
  } catch (error) {
    message.classList.add("error");
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function clearSpotifySearch() {
  spotifySearchController?.abort();
  spotifySearchController = null;
  spotifySearchResults.replaceChildren();
  spotifySearchResults.setAttribute("aria-busy", "false");
  document.querySelector("#spotifySearchButton").disabled = false;
  document.querySelector("#spotifySearchStatus").textContent = "";
}

function openSpotifySearch() {
  if (!spotifyEnabled) {
    showToast("Enable Spotify in Spotify settings first.");
    return;
  }
  clearSpotifySearch();
  document.querySelector("#spotifySearchType").value = document.querySelector("#spotifyType").value;
  spotifySearchDialog.showModal();
  document.querySelector("#spotifySearchQuery").focus();
}

function selectSpotifyResult(result) {
  if (!addSpotifyItem({ name: result.name, spotifyType: result.type, spotifyId: result.id, subtitle: result.subtitle })) return;
  document.querySelector("#spotifyType").value = result.type;
  const name = document.querySelector("#nameInput");
  if (!name.value.trim()) name.value = result.name.slice(0, name.maxLength);
  document.querySelector("#spotifySelectionMessage").textContent = `Added: ${result.name}${result.subtitle ? ` - ${result.subtitle}` : ""}`;
  if (result.type === "track") {
    document.querySelector("#spotifySearchStatus").textContent = `Added ${result.name}. ${spotifyQueue.length} track${spotifyQueue.length === 1 ? "" : "s"} in queue`;
    const query = document.querySelector("#spotifySearchQuery");
    query.focus();
    query.select();
  } else spotifySearchDialog.close();
}

function addSpotifyItem(item) {
  if (spotifyQueue.length && (item.spotifyType !== "track" || spotifyQueue.some(entry => entry.spotifyType !== "track"))) {
    if (!window.confirm("Albums and playlists must be used on their own. Replace the current Spotify selection?")) return false;
    spotifyQueue = [];
  }
  if (spotifyQueue.length >= 100) {
    showToast("A tag can contain up to 100 Spotify tracks.");
    return false;
  }
  spotifyQueue.push(item);
  document.querySelector("#spotifyId").value = "";
  document.querySelector("#spotifyId").required = false;
  queueUid = normalizedUid(document.querySelector("#uidInput").value);
  renderQueue();
  document.querySelector("#queueMessage").textContent = `Added ${item.name}`;
  return true;
}

function addManualSpotifyItem() {
  const spotifyId = document.querySelector("#spotifyId").value.trim();
  if (!spotifyId) {
    showToast("Enter a Spotify ID first.");
    return false;
  }
  return addSpotifyItem({ name: spotifyId, spotifyType: document.querySelector("#spotifyType").value, spotifyId });
}

document.querySelector("#addSpotifyItemButton").addEventListener("click", addManualSpotifyItem);

function renderSpotifyResults(results) {
  spotifySearchResults.replaceChildren();
  for (const result of results) {
    const row = document.createElement("article");
    row.className = "spotify-result-row";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "spotify-result";
    button.setAttribute("aria-label", `Select ${result.name}${result.subtitle ? ` by ${result.subtitle}` : ""}`);
    button.addEventListener("click", () => selectSpotifyResult(result));
    const artwork = document.createElement("span");
    artwork.className = "spotify-artwork";
    artwork.setAttribute("aria-hidden", "true");
    artwork.textContent = "♪";
    if (result.imageUrl?.startsWith("https://")) {
      const image = document.createElement("img");
      image.src = result.imageUrl;
      image.alt = "";
      image.addEventListener("error", () => { artwork.textContent = "♪"; });
      artwork.replaceChildren(image);
    }
    const copy = document.createElement("span");
    copy.className = "spotify-result-copy";
    const title = document.createElement("span");
    title.className = "spotify-result-title";
    title.textContent = result.name;
    const detail = document.createElement("span");
    detail.className = "spotify-result-detail";
    detail.textContent = `${result.type}${result.subtitle ? ` - ${result.subtitle}` : ""}`;
    copy.append(title, detail);
    button.append(artwork, copy);
    const link = document.createElement("a");
    link.className = "spotify-result-link";
    link.href = `https://open.spotify.com/${encodeURIComponent(result.type)}/${encodeURIComponent(result.id)}`;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    link.textContent = "Spotify ↗";
    link.setAttribute("aria-label", `Open ${result.name} in Spotify`);
    row.append(button, link);
    spotifySearchResults.append(row);
  }
}

async function searchSpotify(event) {
  event.preventDefault();
  clearSpotifySearch();
  const query = document.querySelector("#spotifySearchQuery").value.trim();
  const type = document.querySelector("#spotifySearchType").value;
  const status = document.querySelector("#spotifySearchStatus");
  status.classList.remove("error");
  if (!query) {
    status.textContent = "Enter a search term.";
    return;
  }
  const controller = new AbortController();
  spotifySearchController = controller;
  document.querySelector("#spotifySearchButton").disabled = true;
  spotifySearchResults.setAttribute("aria-busy", "true");
  status.textContent = "Searching Spotify...";
  try {
    const parameters = new URLSearchParams({ q: query, type });
    const results = await request(`/api/spotify/search?${parameters}`, { signal: controller.signal });
    if (spotifySearchController !== controller) return;
    renderSpotifyResults(results);
    status.textContent = results.length ? `${results.length} result${results.length === 1 ? "" : "s"}` : "No matches found.";
  } catch (error) {
    if (controller.signal.aborted || spotifySearchController !== controller) return;
    status.classList.add("error");
    status.textContent = error.message;
  } finally {
    if (spotifySearchController === controller) {
      spotifySearchResults.setAttribute("aria-busy", "false");
      document.querySelector("#spotifySearchButton").disabled = false;
    }
  }
}

document.querySelector("#openSpotifySearch").addEventListener("click", openSpotifySearch);
document.querySelector("#closeSpotifySearch").addEventListener("click", () => spotifySearchDialog.close());
spotifySearchDialog.addEventListener("close", clearSpotifySearch);
spotifySearchDialog.addEventListener("keydown", event => {
  if (event.key !== "Escape") return;
  event.preventDefault();
  spotifySearchDialog.close();
});
document.querySelector("#spotifySearchForm").addEventListener("submit", searchSpotify);
document.querySelector("#spotifySearchQuery").addEventListener("input", clearSpotifySearch);
document.querySelector("#spotifySearchType").addEventListener("change", clearSpotifySearch);
for (const selector of ["#spotifyId", "#spotifyType"]) {
  document.querySelector(selector).addEventListener("input", () => {
    document.querySelector("#spotifySelectionMessage").textContent = "";
  });
}

const sectionTabs = [...document.querySelectorAll('.section-tabs [role="tab"]')];

function selectSectionTab(selectedTab) {
  for (const tab of sectionTabs) {
    const selected = tab === selectedTab;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    document.getElementById(tab.getAttribute("aria-controls")).hidden = !selected;
  }
}

for (const [index, tab] of sectionTabs.entries()) {
  tab.addEventListener("click", () => selectSectionTab(tab));
  tab.addEventListener("keydown", event => {
    let nextIndex;
    switch (event.key) {
      case "ArrowRight": nextIndex = (index + 1) % sectionTabs.length; break;
      case "ArrowLeft": nextIndex = (index - 1 + sectionTabs.length) % sectionTabs.length; break;
      case "Home": nextIndex = 0; break;
      case "End": nextIndex = sectionTabs.length - 1; break;
      default: return;
    }
    event.preventDefault();
    selectSectionTab(sectionTabs[nextIndex]);
    sectionTabs[nextIndex].focus();
  });
}

document.querySelector("#spotifySettingsForm").addEventListener("submit", saveSpotifySettings);
document.querySelector("#mediaSettingsForm").addEventListener("submit", saveMediaSettings);
sourceSelect.addEventListener("change", updateSourceFields);
document.querySelector("#searchInput").addEventListener("input", renderTags);
document.querySelector("#captureButton").addEventListener("click", event => captureUid(document.querySelector("#uidInput"), event.currentTarget));
document.querySelector("#captureForwardButton").addEventListener("click", event => captureUid(document.querySelector("#forwardUid"), event.currentTarget));
document.querySelector("#captureRewindButton").addEventListener("click", event => captureUid(document.querySelector("#rewindUid"), event.currentTarget));
document.querySelector("#captureVolumeUpButton").addEventListener("click", event => captureUid(document.querySelector("#volumeUpUid"), event.currentTarget));
document.querySelector("#captureVolumeDownButton").addEventListener("click", event => captureUid(document.querySelector("#volumeDownUid"), event.currentTarget));
document.querySelector("#captureShuffleButton").addEventListener("click", event => captureUid(document.querySelector("#shuffleUid"), event.currentTarget));
document.querySelector("#saveControlsButton").addEventListener("click", saveControlTags);
document.querySelector("#mediaUpload").addEventListener("change", uploadSelectedFiles);
document.querySelector("#existingMediaButton").addEventListener("click", openMediaDialog);
document.querySelector("#mediaSearch").addEventListener("input", renderAvailableMedia);
document.querySelector("#addSelectedMedia").addEventListener("click", addSelectedMedia);
document.querySelector("#closeMediaDialog").addEventListener("click", () => mediaDialog.close());
document.querySelector("#cancelEditButton").addEventListener("click", cancelEdit);
document.querySelector("#uidInput").addEventListener("input", event => {
  const uid = normalizedUid(event.target.value);
  if ((uploadQueue.length || spotifyQueue.length) && queueUid && uid !== queueUid) {
    uploadQueue = [];
    spotifyQueue = [];
    queueUid = null;
    document.querySelector("#spotifySelectionMessage").textContent = "";
    updateSourceFields();
    document.querySelector("#uploadStatus").textContent = "UID changed. Add files again for this tag.";
  }
  if ((uploadQueue.length || spotifyQueue.length) && !queueUid) queueUid = uid;
});
document.querySelector("#tagForm").addEventListener("submit", createTag);
document.querySelector("#refreshButton").addEventListener("click", () => refresh().catch(error => showToast(error.message)));

refresh().catch(error => showToast(error.message));
loadMediaSettings().catch(error => showToast(error.message));
loadSpotifySettings().catch(error => showToast(error.message));
renderQueue();