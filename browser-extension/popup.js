const status = document.querySelector("#status");
const paused = document.querySelector("#paused");

async function refresh() {
  const state = await chrome.storage.local.get(["bridgeStatus", "bridgeStatusAt", "bridgePaused"]);
  paused.checked = Boolean(state.bridgePaused);
  const at = state.bridgeStatusAt ? new Date(state.bridgeStatusAt).toLocaleTimeString() : "";
  status.textContent = `${state.bridgeStatus || "まだ接続していません。"}${at ? `\n最終確認 ${at}` : ""}`;
}

paused.addEventListener("change", async () => {
  await chrome.storage.local.set({ bridgePaused: paused.checked });
  const result = await chrome.runtime.sendMessage({ type: "publish-active-tab" });
  status.textContent = result?.message || "状態を更新しました。";
});

document.querySelector("#retry").addEventListener("click", async () => {
  status.textContent = "接続確認中…";
  const result = await chrome.runtime.sendMessage({ type: "publish-active-tab" });
  status.textContent = result?.message || "接続状態を確認できませんでした。";
});

document.querySelector("#options").addEventListener("click", () => chrome.runtime.openOptionsPage());
refresh();
