const tokenInput = document.querySelector("#token");
const status = document.querySelector("#status");
document.querySelector("#extension-id").textContent = chrome.runtime.id;

chrome.storage.local.get("bridgeToken").then(({ bridgeToken = "" }) => {
  tokenInput.value = bridgeToken;
});

document.querySelector("#save").addEventListener("click", async () => {
  const bridgeToken = tokenInput.value.trim();
  if (!bridgeToken) {
    status.textContent = "トークンを入力してください。";
    return;
  }
  await chrome.storage.local.set({ bridgeToken });
  try {
    const result = await chrome.runtime.sendMessage({ type: "publish-active-tab" });
    status.textContent = result?.message || "保存しましたが、接続状態を確認できませんでした。";
  } catch {
    status.textContent = "保存しました。PC Activity Tracker を起動してから再確認してください。";
  }
});
