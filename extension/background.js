// Sends the instagram.com "sessionid" cookie to the local Following app:
// on install/startup, whenever the cookie changes (e.g. you logged in again), and hourly as a safety net.
// The app ignores repeats, so the hourly send costs nothing and recovers if the app was down.

const DEFAULT_APP_URL = "http://localhost:47321";
const INSTAGRAM = "https://www.instagram.com";

async function appUrl() {
  const { appUrl } = await chrome.storage.local.get("appUrl");
  return (appUrl || DEFAULT_APP_URL).replace(/\/+$/, "");
}

async function setStatus(ok, message) {
  await chrome.storage.local.set({ status: { ok, message, at: new Date().toISOString() } });
  await chrome.action.setBadgeText({ text: ok ? "" : "!" });
  await chrome.action.setBadgeBackgroundColor({ color: "#cf222e" });
}

async function send() {
  const cookie = await chrome.cookies.get({ url: INSTAGRAM, name: "sessionid" });
  if (!cookie) {
    await setStatus(false, "Not logged in to instagram.com in this browser.");
    return;
  }
  const url = await appUrl();
  try {
    const res = await fetch(`${url}/api/session`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: cookie.value }),
    });
    const body = await res.json().catch(() => ({}));
    await setStatus(res.ok, res.ok ? body.message : body.error || `The app answered HTTP ${res.status}.`);
  } catch {
    await setStatus(false, `Can't reach the app at ${url}. Is Docker running?`);
  }
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create("sync", { periodInMinutes: 60 });
  send();
});
chrome.runtime.onStartup.addListener(send);
chrome.alarms.onAlarm.addListener(send);

chrome.cookies.onChanged.addListener(({ cookie, removed }) => {
  if (!removed && cookie.name === "sessionid" && cookie.domain.endsWith("instagram.com")) send();
});

// "Send now" from the popup.
chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message === "send") {
    send().then(() => reply());
    return true; // reply asynchronously
  }
});
