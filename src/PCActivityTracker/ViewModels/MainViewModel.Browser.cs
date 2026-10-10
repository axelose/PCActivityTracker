using System.Collections.ObjectModel;
using System.Windows.Threading;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace PCActivityTracker.ViewModels;

// Screen-specific behavior; the public binding surface remains compatible.
public sealed partial class MainViewModel
{
    public void ResetBrowserPairing()
    {
        _nativeMessagingRegistration.Unregister();
        BrowserBridgeToken = _settingsService.ResetBrowserPairing();
        BrowserPairingStatus = "未ペアリング（新しいトークンを拡張機能へ設定してください）";
        StatusMessage = "ブラウザ連携トークンを再発行し、登録済み拡張IDを解除しました";
    }

    public void RegisterBrowserExtension()
    {
        _nativeMessagingRegistration.Register(BrowserExtensionId);
        BrowserExtensionId = string.Empty;
        ApplyBridgeDiagnostics(_bridgeDiagnosticsProvider());
        StatusMessage = "Chrome／EdgeのNative Messaging連携を登録しました。ブラウザを再起動してください";
    }

    private void ApplyBridgeDiagnostics(BrowserBridgeDiagnostics diagnostics)
    {
        BridgeStatus = diagnostics.ListenerStatus;
        BrowserConnectionState = diagnostics.ConnectionState;
        BrowserLastReceived = diagnostics.LastReceivedAt?.LocalDateTime.ToString("M/d HH:mm:ss") ?? "未受信";
        BrowserLastActivity = string.Join(" / ", new[] { diagnostics.LastBrowser, diagnostics.LastDomain, diagnostics.LastCreator }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(BrowserLastActivity)) BrowserLastActivity = "—";
        _browserCreatorStatus = diagnostics.LastReceivedAt is null
            ? "配信者・チャンネル情報は未受信です"
            : !string.IsNullOrWhiteSpace(diagnostics.LastCreator) ? "配信者・チャンネル取得済み"
            : diagnostics.LastDomain is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "twitch.tv" or "www.twitch.tv" or "m.twitch.tv"
                ? "配信者・チャンネル: 未取得または該当なし（取得不能は利用ゼロを意味しません）"
                : "配信者・チャンネル取得の対象外です";
        Raise(nameof(BrowserCreatorStatus));
        BrowserExtensionVersion = diagnostics.ExtensionVersion ?? "未受信";
        BrowserExtensionStatus = diagnostics.ExtensionVersion is null
            ? $"推奨バージョン {diagnostics.ExpectedExtensionVersion}"
            : diagnostics.IsExpectedExtension
                ? "拡張機能は最新です"
                : $"再読み込みが必要です（推奨 {diagnostics.ExpectedExtensionVersion}）";
        BrowserRequestCounts = $"正常 {diagnostics.AcceptedCount} / 拒否 {diagnostics.RejectedCount} / 処理中 {diagnostics.ActiveClients} ・ {diagnostics.LastResult}";
        var paired = _settingsService.Current.BrowserBridgeAllowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries);
        BrowserPairingStatus = paired.Length == 0
            ? "未ペアリング"
            : $"登録済み拡張 {paired.Length}件: {string.Join(", ", paired.Select(origin => origin.Replace("chrome-extension://", string.Empty)))}";
    }

}
