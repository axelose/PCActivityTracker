using System.Windows.Input;

namespace PCActivityTracker.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _errorHandler;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, Action<Exception>? errorHandler = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _errorHandler = errorHandler;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)
    {
        try { _execute(parameter); }
        catch (Exception exception)
        {
            if (_errorHandler is not null) _errorHandler(exception);
            else PCActivityTracker.Services.DiagnosticLogService.Error("UI.Command", exception);
        }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Action<Exception>? _errorHandler;
    private bool _running;

    public AsyncRelayCommand(Func<object?, Task> execute, Action<Exception>? errorHandler = null)
    {
        _execute = execute;
        _errorHandler = errorHandler;
    }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(parameter); }
        catch (Exception exception) { (_errorHandler ?? DefaultErrorHandler)(exception); }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void DefaultErrorHandler(Exception exception) =>
        PCActivityTracker.Services.DiagnosticLogService.Error("UI.Command", exception);
}
