using System.Windows.Input;

namespace Spikit.ViewModels;

// ICommand minimalista para bindings de botones desde XAML. Sin overengineering —
// si después necesitamos CommandParameter o async, lo extendemos.
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

// Variante parametrizada. Usada por SettingsViewModel.NavigateToCommand para que el sidebar
// pueda hacer `Command="{Binding NavigateToCommand}" CommandParameter="Provider"` en lugar de
// declarar 8 comandos separados (uno por sección). El parámetro llega desde XAML como string
// (nombre del enum) o como el valor enum directo según cómo se exprese el CommandParameter.
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Coerce(parameter)) ?? true;
    public void Execute(object? parameter) => _execute(Coerce(parameter));

    private static T? Coerce(object? parameter)
    {
        if (parameter is null) return default;
        if (parameter is T typed) return typed;

        // Si el binding pasa un string (CommandParameter="Provider") y T es enum, lo
        // parseamos. Cualquier otro convert se delega a Convert.ChangeType.
        var targetType = typeof(T);
        if (targetType.IsEnum && parameter is string asString)
        {
            return (T)Enum.Parse(targetType, asString, ignoreCase: true);
        }

        return (T)Convert.ChangeType(parameter, Nullable.GetUnderlyingType(targetType) ?? targetType);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
