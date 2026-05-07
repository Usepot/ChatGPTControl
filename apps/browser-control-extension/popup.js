const fields = {
  relayUrl: document.getElementById("relayUrl"),
  deviceId: document.getElementById("deviceId"),
  deviceName: document.getElementById("deviceName"),
  deviceToken: document.getElementById("deviceToken"),
  autoConnect: document.getElementById("autoConnect"),
  allowUnsafeScript: document.getElementById("allowUnsafeScript"),
};

const statusBox = document.getElementById("status");
const saveButton = document.getElementById("save");
const connectButton = document.getElementById("connect");
const disconnectButton = document.getElementById("disconnect");

saveButton.addEventListener("click", async () => {
  const payload = {
    relayUrl: fields.relayUrl.value.trim(),
    deviceId: fields.deviceId.value.trim(),
    deviceName: fields.deviceName.value.trim(),
    deviceToken: fields.deviceToken.value,
    autoConnect: fields.autoConnect.checked,
    allowUnsafeScript: fields.allowUnsafeScript.checked,
  };
  const result = await send({ type: "saveConfig", config: payload });
  applyState(result);
});

connectButton.addEventListener("click", async () => {
  const result = await send({ type: "connect" });
  applyState(result);
});

disconnectButton.addEventListener("click", async () => {
  const result = await send({ type: "disconnect" });
  applyState(result);
});

chrome.runtime.onMessage.addListener((message) => {
  if (message?.type === "statusChanged") {
    renderStatus(message.status);
  }
});

void refresh();

async function refresh() {
  const result = await send({ type: "getStatus" });
  applyState(result);
}

async function send(message) {
  return await chrome.runtime.sendMessage(message);
}

function applyState(result) {
  if (result?.error) {
    renderStatus({ connected: false, state: "error", message: result.error });
    return;
  }
  if (result?.config) {
    fields.relayUrl.value = result.config.relayUrl ?? "";
    fields.deviceId.value = result.config.deviceId ?? "";
    fields.deviceName.value = result.config.deviceName ?? "";
    fields.deviceToken.placeholder = result.config.hasDeviceToken ? "Saved token hidden" : "Paste relay device token";
    if (!result.config.hasDeviceToken) {
      fields.deviceToken.value = "";
    }
    fields.autoConnect.checked = result.config.autoConnect === true;
    fields.allowUnsafeScript.checked = result.config.allowUnsafeScript === true;
  }
  renderStatus(result?.status);
}

function renderStatus(status) {
  const normalized = status ?? { connected: false, state: "unknown", message: "Unknown status" };
  const strong = statusBox.querySelector("strong");
  const span = statusBox.querySelector("span");
  strong.textContent = normalized.connected ? "Connected" : toTitle(normalized.state ?? "Disconnected");
  span.textContent = normalized.message ?? "";
}

function toTitle(value) {
  return String(value)
    .replace(/[-_]/g, " ")
    .replace(/\b\w/g, (char) => char.toUpperCase());
}
