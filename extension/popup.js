const DEFAULT_APP_URL = "http://localhost:47321";
const $ = (id) => document.getElementById(id);

async function show() {
  const { status, appUrl } = await chrome.storage.local.get(["status", "appUrl"]);
  const url = appUrl || DEFAULT_APP_URL;
  $("url").value = url;
  $("open").href = url;
  if (status) {
    $("status").textContent = status.message;
    $("status").className = status.ok ? "ok" : "fail";
    $("when").textContent = `Last attempt: ${new Date(status.at).toLocaleString()}`;
  }
}

$("url").addEventListener("change", async () => {
  await chrome.storage.local.set({ appUrl: $("url").value.trim() || DEFAULT_APP_URL });
  show();
});

$("send").addEventListener("click", async () => {
  $("send").disabled = true;
  await chrome.runtime.sendMessage("send");
  $("send").disabled = false;
  show();
});

show();
