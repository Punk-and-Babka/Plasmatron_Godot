using Godot;
using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;

public partial class SerialManager : Node
{
    // Сигналы для UI и других систем
    [Signal] public delegate void ConnectionChangedEventHandler(bool isConnected);
    [Signal] public delegate void DataReceivedEventHandler(string data);
    [Signal] public delegate void ErrorOccurredEventHandler(string message);

    private SerialPort _serialPort;
    private bool _isRunning;
    private Thread _writeThread;

    // Потокобезопасная очередь команд
    private ConcurrentQueue<string> _commandQueue = new ConcurrentQueue<string>();

    public new bool IsConnected => _serialPort != null && _serialPort.IsOpen;

    public override void _Ready()
    {
        // Запускаем поток отправки данных, чтобы не тормозить UI
        _isRunning = true;
        _writeThread = new Thread(WriteLoop) { IsBackground = true };
        _writeThread.Start();
    }

    public void Connect(string portName, int baudRate)
    {
        if (IsConnected) Close();

        try
        {
            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _serialPort.ReadTimeout = 500;
            _serialPort.WriteTimeout = 500;
            _serialPort.DataReceived += OnSerialDataReceived;
            _serialPort.Open();

            EmitSignal(SignalName.ConnectionChanged, true);
            GD.Print($"[SerialManager] Connected to {portName}");
        }
        catch (Exception ex)
        {
            EmitSignal(SignalName.ErrorOccurred, ex.Message);
            Close();
        }
    }

    public void Close()
    {
        if (_serialPort != null)
        {
            if (_serialPort.IsOpen)
            {
                // Отписываемся и закрываем аккуратно
                _serialPort.DataReceived -= OnSerialDataReceived;
                try { _serialPort.Close(); } catch { }
            }
            _serialPort.Dispose();
            _serialPort = null;
        }
        EmitSignal(SignalName.ConnectionChanged, false);
    }

    // Метод для отправки команд из ЛЮБОГО места программы
    public void SendCommand(string command)
    {
        _commandQueue.Enqueue(command);
    }

    // Бесконечный цикл в отдельном потоке
    private void WriteLoop()
    {
        while (_isRunning)
        {
            if (IsConnected && _commandQueue.TryDequeue(out string cmd))
            {
                try
                {
                    _serialPort.WriteLine(cmd);
                    // Небольшая задержка, чтобы не зафлудить Ардуино, если нужно
                    // Thread.Sleep(10); 
                }
                catch (Exception ex)
                {
                    // Маршалинг ошибки в главный поток Godot
                    CallDeferred(nameof(EmitError), $"Write Error: {ex.Message}");
                }
            }
            else
            {
                Thread.Sleep(10); // Спать, если очереди нет, чтобы не грузить CPU
            }
        }
    }

    // Обработка входящих данных
    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            // Читаем всё, что есть
            string indata = _serialPort.ReadLine();
            // Маршалим данные в главный поток Godot
            CallDeferred(nameof(EmitData), indata.Trim());
        }
        catch (Exception) { /* Игнор таймаутов при чтении */ }
    }

    private void EmitData(string data) => EmitSignal(SignalName.DataReceived, data);
    private void EmitError(string msg) => EmitSignal(SignalName.ErrorOccurred, msg);

    public override void _ExitTree()
    {
        _isRunning = false;
        Close();
    }
}