using Godot;
using System;
using System.Net.Sockets;
using System.Threading;

// Наследуемся от TextureRect, так как скрипт висит прямо на нем
public partial class CameraClient : TextureRect
{
    [Export] public string ServerAddress { get; set; } = "127.0.0.1";
    [Export] public int ServerPort { get; set; } = 8080;

    // Если true - подключается сразу при старте программы
    [Export] public bool AutoConnect { get; set; } = true;

    private Label _statusLabel;
    private TcpClient _tcpClient;
    private Thread _receiverThread;

    private volatile bool _isRunning = false;
    private DateTime _lastReconnectAttempt = DateTime.MinValue;

    public override void _Ready()
    {
        // Ищем лейбл внутри себя (если вы его добавили)
        _statusLabel = GetNodeOrNull<Label>("StatusLabel");

        if (AutoConnect)
        {
            StartCamera();
        }
    }

    // Публичные методы, чтобы UIController мог управлять камерой (вкл/выкл)
    public void StartCamera()
    {
        if (_isRunning) return;

        _isRunning = true;
        _receiverThread = new Thread(ReceiveLoop);
        _receiverThread.IsBackground = true;
        _receiverThread.Start();

        UpdateStatus("Подключение...");
    }

    // Свойство, чтобы UI знал, включены мы или нет
    public bool IsActive => _isRunning;

    // Метод-переключатель
    public void Toggle()
    {
        if (_isRunning) StopCamera();
        else StartCamera();
    }
    public void StopCamera()
    {
        if (!_isRunning) return;

        _isRunning = false;

        try { _tcpClient?.Close(); } catch { }
        _tcpClient = null;

        // Важно: вызываем обновление UI
        CallDeferred(nameof(OnCameraStoppedUI));
    }

    // Специальный метод для обновления UI при остановке
    private void OnCameraStoppedUI()
    {
        // 1. Убираем картинку
        this.Texture = null;

        // 2. Показываем текст
        if (_statusLabel != null)
        {
            _statusLabel.Text = "Камера выключена";
            _statusLabel.Visible = true;

            // На всякий случай поднимаем лейбл наверх, если он перекрыт
            _statusLabel.MoveToFront();
        }
    }

    private void ReceiveLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (_tcpClient == null || !_tcpClient.Connected)
                {
                    ConnectAndStream();
                }
            }
            catch (Exception ex)
            {
                if (!_isRunning) return;

                GD.PrintErr($"Camera Error: {ex.Message}");
                CallDeferred(nameof(UpdateStatus), "Ошибка связи");
            }

            // Если соединение разорвалось само (а не мы выключили), ждем перед реконнектом
            if (_isRunning) Thread.Sleep(2000);
        }
    }

    private void ConnectAndStream()
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(ServerAddress, ServerPort);
        _tcpClient.ReceiveTimeout = 3000; // Таймаут на случай зависания

        GD.Print("Camera: Connected");
        CallDeferred(nameof(UpdateStatus), ""); // Скрываем текст ошибки

        using (NetworkStream stream = _tcpClient.GetStream())
        {
            byte[] lengthPrefix = new byte[4];

            while (_isRunning && _tcpClient.Connected)
            {
                // 1. Читаем размер кадра
                if (!ReadBytesFull(stream, lengthPrefix)) break;
                int length = BitConverter.ToInt32(lengthPrefix, 0);
                if (length <= 0) continue;

                // 2. Читаем кадр
                byte[] imageData = new byte[length];
                if (!ReadBytesFull(stream, imageData)) break;

                // 3. Конвертируем
                Image image = new Image();
                Error error = image.LoadJpgFromBuffer(imageData);

                if (error == Error.Ok)
                {
                    ImageTexture texture = ImageTexture.CreateFromImage(image);
                    CallDeferred(nameof(ApplyTexture), texture);
                }
            }
        }
    }

    private bool ReadBytesFull(NetworkStream stream, byte[] buffer)
    {
        int offset = 0;
        int remaining = buffer.Length;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, offset, remaining);
            if (read == 0) return false;
            offset += read;
            remaining -= read;
        }
        return true;
    }

    private void ApplyTexture(Texture2D texture)
    {
        this.Texture = texture;

        // Скрываем лейбл, когда пришел кадр
        if (_statusLabel != null)
        {
            _statusLabel.Visible = false;
        }
    }

    // Этот метод можно оставить для внутренних нужд или удалить, 
    // так как мы используем OnCameraStoppedUI
    private void ClearTexture()
    {
        this.Texture = null;
    }

    private void UpdateStatus(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
        }
    }

    public override void _ExitTree()
    {
        StopCamera();
    }
}