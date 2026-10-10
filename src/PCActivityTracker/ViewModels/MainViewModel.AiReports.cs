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
    private async Task LoadAiReportAsync()
    {
        if (!_uiActive || !IsAiReports) return;
        var type = IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily;
        var (start, scheduledEnd) = GetAiRange();
        var now = DateTimeOffset.Now;
        var end = start <= now && now < scheduledEnd ? now : scheduledEnd;
        var snapshot = await Task.Run(() => _aiReportService.GetSnapshot(type, start, end));
        ApplyAiSnapshot(snapshot);
        if (OllamaModels.Count == 0 && OllamaConnectionStatus == "未確認")
            await RefreshOllamaAsync();
    }

    private async Task RefreshOllamaAsync()
    {
        try
        {
            OllamaConnectionStatus = "ローカルOllamaへ接続確認中…";
            _ = OllamaClient.ParseLocalEndpoint(OllamaEndpoint);
            var status = await _aiReportService.GetOllamaStatusAsync(OllamaEndpoint, CancellationToken.None);
            Replace(OllamaModels, status.Models.Select(model => model.Name));
            if (string.IsNullOrWhiteSpace(OllamaModel) ||
                !OllamaModels.Contains(OllamaModel, StringComparer.OrdinalIgnoreCase))
                OllamaModel = OllamaModels.FirstOrDefault() ?? string.Empty;
            var runtime = status.RunningModels.Count == 0
                ? "GPUメモリ上のモデルなし"
                : $"稼働中 {string.Join(", ", status.RunningModels.Select(model => model.Name))}";
            OllamaConnectionStatus = $"Ollama {status.Version} / モデル {status.Models.Count}件 / {runtime}";
        }
        catch (Exception exception)
        {
            OllamaConnectionStatus = $"接続できません: {exception.Message}";
            DiagnosticLogService.Error("AI.OllamaStatus", exception);
        }
    }

    private async Task GenerateAiReportAsync()
    {
        if (IsAiGenerating || _isDeletingData) return;
        if (string.IsNullOrWhiteSpace(OllamaModel))
        {
            AiReportStatus = "モデルを選択してください。先に「接続確認」を押すと一覧を取得できます。";
            return;
        }

        try
        {
            _ = OllamaClient.ParseLocalEndpoint(OllamaEndpoint);
        }
        catch (Exception exception)
        {
            AiReportStatus = $"Ollama接続先が不正です: {exception.Message}";
            return;
        }

        var safetySettings = BuildAiSafetySettings();
        var safety = _gameSafetyService.Evaluate(safetySettings);
        if (safety.IsGameForeground)
        {
            AiReportStatus = $"{safety.ProcessName} がForegroundのため、ゲームセーフモードが生成を停止しました。";
            return;
        }

        var type = IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily;
        var selectedDate = SelectedDate;
        var (start, scheduledEnd) = GetAiRange();
        var now = DateTimeOffset.Now;
        var end = start <= now && now < scheduledEnd ? now : scheduledEnd;
        var cancellation = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiGenerationCancellation?.Dispose();
        _aiGenerationCancellation = cancellation;
        _aiGenerationStopped = stopped;
        _gameCancellationProcess = null;
        IsAiGenerating = true;
        var retryPolicy = UnloadOllamaAfterGeneration || !RequireIdleOllama
            ? "品質不適合時は最大1回再生成" : "品質不適合時の自動再生成はOFF";
        AiReportStatus = RequireIdleOllama
            ? $"Ollamaの空き状態を確認し、ローカルGPUで生成しています（{retryPolicy}）…"
            : $"GPU実行枠を確保し、ローカルOllamaで生成しています（{retryPolicy}）…";
        var gameMonitor = MonitorGameAndCancelAsync(safetySettings, cancellation);
        try
        {
            var result = await _aiReportService.GenerateAsync(type, start, end, OllamaEndpoint,
                OllamaModel, UnloadOllamaAfterGeneration, RequireIdleOllama,
                OllamaTimeoutMinutes, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsAiReports || SelectedDate != selectedDate || type != (IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily))
                return;
            await LoadAiReportAsync();
            var memory = result.ModelUnloaded ? "生成後にGPUからモデルを解放" : "モデルを5分間保持";
            var retry = result.Attempts > 1 ? "・自動再生成で修正" : string.Empty;
            AiReportStatus = $"生成完了・品質検査OK{retry}（{result.Duration:mm\\:ss}、入力 {result.PromptTokens} / 出力 {result.OutputTokens} tokens、{memory}）";
        }
        catch (OperationCanceledException)
        {
            AiReportStatus = _gameCancellationProcess is null
                ? "生成をキャンセルしました。"
                : $"{_gameCancellationProcess} がForegroundになったため、ゲームセーフモードが生成を中止しました。";
        }
        catch (OllamaBusyException exception)
        {
            AiReportStatus = $"Ollamaは使用中です（{string.Join(", ", exception.Models.Select(model => model.Name))}）。完了または解放後に再実行してください。";
        }
        catch (AiReportQualityException exception)
        {
            AiReportStatus = exception.Message + " 保存はしていません。";
            DiagnosticLogService.Error("AI.QualityValidation", exception);
        }
        catch (Exception exception)
        {
            AiReportStatus = $"生成エラー: {exception.Message}";
            DiagnosticLogService.Error("AI.GenerateReport", exception);
        }
        finally
        {
            cancellation.Cancel();
            try { await gameMonitor; } catch (OperationCanceledException) { }
            if (ReferenceEquals(_aiGenerationCancellation, cancellation))
            {
                _aiGenerationCancellation = null;
                IsAiGenerating = false;
            }
            if (ReferenceEquals(_aiGenerationStopped, stopped)) _aiGenerationStopped = null;
            stopped.TrySetResult();
            cancellation.Dispose();
        }
    }

    private async Task MonitorGameAndCancelAsync(AppSettings settings, CancellationTokenSource generation)
    {
        try
        {
            var processName = await _gameSafetyService.MonitorAsync(settings, generation.Token);
            if (string.IsNullOrWhiteSpace(processName)) return;
            _gameCancellationProcess = processName;
            generation.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyAiSnapshot(AiReportSnapshot snapshot)
    {
        var report = snapshot.Report;
        HasAiReport = report is not null;
        AiReportContent = report?.Content ?? string.Empty;
        IsAiReportStale = snapshot.IsStale;
        AiReportMetadata = report is null
            ? $"{AiPeriodLabel} の{AiReportTypeLabel}はまだ生成されていません"
            : $"{report.GeneratedAt.LocalDateTime:yyyy/M/d HH:mm}生成 ・ {report.ModelName} ・ ローカル保存";
        AiFreshnessStatus = report is null
            ? "未生成"
            : snapshot.IsStale
                ? "生成後に新しいActivityがあります。再生成すると最新データを反映できます。"
                : "生成後のActivity変更はありません";
        var analysis = snapshot.Analysis;
        AiEvidenceSummary = $"PC {FormatDuration(analysis.Report.TotalPcSeconds)} ・ Idle {FormatDuration(analysis.Report.IdleSeconds)} ・ 集中 {FormatDuration(analysis.Focus.TotalSeconds)} ・ 25分以上の集中 {analysis.Focus.BlockCount}回";
        Replace(AiEvidenceApps, ToRanking(analysis.Report.Apps.Take(5).ToList()));
        Replace(AiEvidenceSites, ToRanking(analysis.Report.Sites.Take(5).ToList()));
        Replace(AiEvidenceCategories, ToRanking(analysis.Report.Categories.Take(5).ToList()));
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetAiRange()
    {
        var day = DateTime.SpecifyKind(SelectedDate.Date, DateTimeKind.Local);
        if (!IsWeeklyAiReport) return (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)));
        var monday = DateTime.SpecifyKind(StartOfWeek(day), DateTimeKind.Local);
        return (new DateTimeOffset(monday), new DateTimeOffset(monday.AddDays(7)));
    }

    private AppSettings BuildAiSafetySettings() => new()
    {
        GameSafeModeEnabled = GameSafeModeEnabled,
        GameProcessNames = GameProcessNames
    };

}
