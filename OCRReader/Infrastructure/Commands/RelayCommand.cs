using System;
using System.Windows.Input;

namespace OCRReader.Infrastructure.Commands
{
    /// <summary>
    /// Generic RelayCommand implementation for MVVM command binding
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Func<bool>? _canExecute;
        private readonly Action _execute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter)
        {
            _execute();
        }
    }

    /// <summary>
    /// Generic RelayCommand for commands with parameters
    /// </summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Func<T, bool>? _canExecute;
        private readonly Action<T> _execute;

        public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke((T)parameter!) ?? true;
        }

        public void Execute(object? parameter)
        {
            _execute((T)parameter!);
        }
    }
}
