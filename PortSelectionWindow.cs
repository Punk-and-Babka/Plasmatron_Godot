using Godot;
using System;
using System.IO.Ports;
using System.Linq;

/// <summary>
/// Окно выбора COM-порта и параметров подключения
/// </summary>
public partial class PortSelectionWindow : Window
{
    // region Экспортируемые элементы UI
    [Export] private OptionButton _portComboBox;
    [Export] private OptionButton _baudRateComboBox;
    [Export] private CheckButton _mockPortCheckButton;
    // endregion

    // region Публичные свойства
    public string SelectedPort { get; private set; }
    public int SelectedBaudRate { get; private set; }
    public bool UseMockPort => _mockPortCheckButton.ButtonPressed;
    // endregion

    [Signal] public delegate void ConnectionConfirmedEventHandler();

    public override void _Ready()
    {
        InitializePortList();
        InitializeBaudRates();
    }

    /// <summary>
    /// Инициализация списка доступных COM-портов
    /// </summary>
    private void InitializePortList()
    {
        var ports = SerialPort.GetPortNames()
            .OrderBy(p => p)
            .ToArray();

        foreach (var port in ports)
        {
            _portComboBox.AddItem(port);
        }

        if (ports.Length == 0)
        {
            GD.Print("COM-порты не обнаружены");
        }
    }

    /// <summary>
    /// Инициализация списка стандартных скоростей передачи
    /// </summary>
    private void InitializeBaudRates()
    {
        int[] baudRates = { 9600, 19200, 38400, 57600, 115200 };
        foreach (var rate in baudRates)
        {
            _baudRateComboBox.AddItem(rate.ToString());
        }
        _baudRateComboBox.Selected = 0;
    }

    /// <summary>
    /// Обработчик нажатия кнопки подключения
    /// </summary>
    private void OnConnectButtonPressed()
    {
        if (ValidateConnectionParameters())
        {
            EmitSignal(SignalName.ConnectionConfirmed);
            Hide();
        }
    }

    /// <summary>
    /// Проверка корректности выбранных параметров подключения
    /// </summary>
    private bool ValidateConnectionParameters()
    {
        if (_mockPortCheckButton.ButtonPressed)
        {
            SelectedPort = "MOCK";
            SelectedBaudRate = 9600; // Значение по умолчанию
            return true;
        }

        if (_portComboBox.Selected == -1)
        {
            ShowError("Выберите COM-порт");
            return false;
        }

        if (!int.TryParse(_baudRateComboBox.GetItemText(_baudRateComboBox.Selected), out int baud))
        {
            ShowError("Некорректная скорость подключения");
            return false;
        }

        SelectedPort = _portComboBox.GetItemText(_portComboBox.Selected);
        SelectedBaudRate = baud;
        return true;
    }

    /// <summary>
    /// Отображение ошибки в консоли
    /// </summary>
    private void ShowError(string message)
    {
        GD.PrintErr($"Ошибка подключения: {message}");
    }
}