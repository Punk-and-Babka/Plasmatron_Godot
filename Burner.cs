using Godot;
using System;
using static Godot.TextServer;

public partial class Burner : Node2D
{
    public enum MovementDirection { Left, Right }
    public bool IsManualPaused => _isManualPaused;

    [ExportGroup("UI Settings")]
    [Export] private HSlider _speedSlider;
    [Export] private Label _speedLabel;
    [Export] private Label _positionLabel;

    [Signal] public delegate void SequenceFinishedEventHandler();

    private float _pauseDuration = 3f;
    private float _pauseTimer;
    private bool _isPaused;
    public event Action<float> PauseUpdated;
    public event Action<float> PositionChanged;
    public event Action<float> SpeedChanged;

    private bool _isDecelerating;
    private UIController _uiController;

    public float TargetPosition { get; private set; }
    public bool IsMovingToTarget { get; private set; }
    private float _stopThreshold = 0.1f;
    public bool IsAtLeftBound => PositionXCM <= 0.1f;
    public bool IsAtRightBound => PositionXCM >= MaxPositionXCM - 0.1f;

    private bool _isAutoSequenceActive;
    private int _currentTargetIndex;
    private int _cyclesRemaining;
    private float[] _sequencePoints = new float[3];
    private bool _isManualPaused;
    private float _baseSpeed; // Базовая скорость по умолчанию
    private float _fastSpeed = 30f; // Быстрая скорость (настроить по необходимости)

    public bool IsAutoSequenceActive => _isAutoSequenceActive;
    public int CyclesRemaining => _cyclesRemaining;

    // region Основные параметры горелки
    [ExportGroup("Размеры")]
    [Export] public float RealWidthCM { get; set; } = 10f;     // Реальная ширина в сантиметрах
    [Export] public float RealHeightCM { get; set; } = 7.2f;  // Реальная высота в сантиметрах

    [ExportGroup("Внешний вид")]
    [Export] public Color BurnerColor { get; set; } = new Color(1, 0.5f, 0, 0.8f); // Основной цвет горелки

    [ExportGroup("Движение")]
    [Export] public float MaxSpeedCM { get; set; } = 10f;     // Максимальная скорость (см/сек)
    [Export(PropertyHint.Range, "0.1,5.0")]
    public float AccelerationTime = 0.25f;  // Время разгона от 0 до максимума (сек)
    [Export(PropertyHint.Range, "0.1,5.0")]
    public float DecelerationTime = 0.25f;  // Время полной остановки (сек)

    [ExportGroup("Связи")]
    [Export] private CoordinateGrid _grid;  // Ссылка на компонент координатной сетки
    // endregion

    [Export]
    public float PauseDuration
    {
        get => _pauseDuration;
        set => _pauseDuration = Mathf.Max(value, 0.1f);
    }

    // region Внутренние состояния
    private float _currentSpeedCM;     // Текущая скорость (см/сек)
    public float CurrentSpeedCM
    {
        get => _currentSpeedCM;
        private set
        {
            if (Mathf.IsEqualApprox(_currentSpeedCM, value)) return;
            _currentSpeedCM = value;
            SpeedChanged?.Invoke(value); // Инициируем событие
        }
    }
    private float _positionXCM;       // Позиция по X в сантиметрах
    private float _accelerationRate;  // Коэффициент ускорения (см/сек²)
    private float _decelerationRate;  // Коэффициент торможения (см/сек²)
                                      // endregion


    // region Свойства
    /// <summary>
    /// Текущая позиция горелки в сантиметрах с автоматическим ограничением границ
    /// </summary>
    public float PositionXCM
    {
        get => _positionXCM;
        private set
        {
            // Ограничиваем позицию в пределах доступного пространства
            value = Mathf.Clamp(value, 0f, MaxPositionXCM);

            // Обновляем только при изменении значения
            if (Mathf.IsEqualApprox(_positionXCM, value)) return;

            _positionXCM = value;
            PositionChanged?.Invoke(value); // Генерация события
            QueueRedraw(); // Требуем перерисовки при изменении позиции
        }
    }

    /// <summary>
    /// Максимально допустимая позиция (правая граница минус ширина горелки)
    /// </summary>
    private float MaxPositionXCM =>
        _grid != null ? Mathf.Max(_grid.RealWorldWidthCM - RealWidthCM, 0) : 0f;
    // endregion

    // region Жизненный цикл
    public override void _Ready()
    {
        AddToGroup("burners");
        // Автоматически находим сетку если не установлена вручную
        if (_grid == null)
            _grid = GetParent().GetNode<CoordinateGrid>("CoordinateGrid");

        // Подписываемся на обновления сетки
        _grid.GridUpdated += () => QueueRedraw();

        // Устанавливаем порядок отрисовки
        ZIndex = 3;

        _uiController = GetNode<UIController>("../UIController");
        // Рассчитываем коэффициенты движения
        // Ускорение: изменение скорости за секунду = максимальная скорость / время разгона
        _accelerationRate = MaxSpeedCM / AccelerationTime;

        // Торможение: аналогичный расчет для замедления
        _decelerationRate = MaxSpeedCM / DecelerationTime;

        if (_positionLabel == null)
        {
            _positionLabel = GetNode<Label>("../CanvasLayer/PositionLabel");
        }

        if (_speedSlider != null)
        {
            ConnectSlider();
        }
    }

    private float _updateTimer;
    public override void _Process(double delta)
    {
        // Главный цикл обработки движения
        HandleMovement((float)delta);

        _updateTimer += (float)delta;
        if (_updateTimer > 0.1f) // Обновление каждые 0.1 секунды
        {

            _updateTimer = 0;

            //// Отладочный вывод текущей скорости
            //GD.Print($"Speed: {_currentSpeedCM:N1}");
        }


    }
    // endregion

    private float _savedSpeedBeforePause;
    private float _savedTargetBeforePause;
    private bool _wasMovingBeforePause;

    public void SetManualPause(bool state)
    {
        if (_isManualPaused == state) return;

        _isManualPaused = state;

        if (_isManualPaused)
        {
            // Сохраняем состояние перед паузой
            _savedSpeedBeforePause = MaxSpeedCM;
            _savedTargetBeforePause = TargetPosition;
            _wasMovingBeforePause = IsMovingToTarget;

            // Останавливаем движение
            SendStopCommand();
            GD.Print("Ручная пауза активирована");
        }
        else
        {
            // Восстанавливаем состояние только если двигались
            if (_wasMovingBeforePause)
            {
                // Возвращаем оригинальную скорость
                MaxSpeedCM = _savedSpeedBeforePause;

                // Возобновляем движение к сохранённой цели
                MoveToPosition(_savedTargetBeforePause);
            }
            GD.Print("Ручная пауза деактивирована");
        }
    }

    public void ResetSequenceState()
    {
        _isAutoSequenceActive = false;
        _isPaused = false;
        _isDecelerating = false;
        IsMovingToTarget = false;
        _currentTargetIndex = -1;
        _pauseTimer = 0;
        _isManualPaused = false;

        // Останавливаем все возможные движения
        SendStopCommand();

        GD.Print("Полный сброс состояния последовательности");
    }

    // Вызывать этот метод перед каждым новым запуском последовательности
    public void StartAutoSequence(float[] points, int cycles)
    {
        ResetSequenceState(); // Важно: полный сброс перед запуском

        if (points.Length != 3) return;

        _baseSpeed = MaxSpeedCM;
        _sequencePoints = points;
        _cyclesRemaining = cycles;
        _currentTargetIndex = 0;
        _isAutoSequenceActive = true;

        GD.Print($"Запуск последовательности. Циклов: {cycles}");

        if (Mathf.IsEqualApprox(PositionXCM, _sequencePoints[0]))
        {
            // Если уже в точке 0, сразу переходим к обработке
            GD.Print("Уже в точке 0. Переходим к точке 1.");
            _currentTargetIndex = 1;
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[1]);
        }
        else
        {
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[0]);
        }
    }

    // Метод для изменения скорости
    public void SetMovementSpeed(float speed)
    {
        if (Mathf.IsEqualApprox(MaxSpeedCM, speed)) return;

        MaxSpeedCM = speed;
        _accelerationRate = MaxSpeedCM / AccelerationTime;
        _decelerationRate = MaxSpeedCM / DecelerationTime;

        // Обновляем UI и отправляем команду
        SpeedChanged?.Invoke(MaxSpeedCM);
        _uiController?.SendSpeedCommand(MaxSpeedCM);
        // Обновление слайдера
        _uiController?.UpdateSpeedSlider(MaxSpeedCM);
    }
    public void StopAutoSequence()
    {
        _isAutoSequenceActive = false;
        SetMovementSpeed(_baseSpeed); // Восстановление скорости
        StopAutoMovement();
    }

    public void HandleMovementCompletion()
    {
        if (!_isAutoSequenceActive || !IsAtTargetPosition() || _isPaused)
            return;

        switch (_currentTargetIndex)
        {
            case 0:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_fastSpeed);
                    MoveToPosition(_sequencePoints[1]);
                    _currentTargetIndex = 1;
                }
                else
                {
                    // Удаляем завершение последовательности здесь
                    GD.Print("Финишная точка достигнута");
                    _currentTargetIndex = -1; // Новый статус

                    if (!_isPaused) // Если не ожидается пауза
                    {
                        _isAutoSequenceActive = false;
                        EmitSignal(nameof(SequenceFinished));
                    }

                    StartPauseBeforeMovement(_sequencePoints[0], -1); // Специальный индекс
                }
                break;

            case 1:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_baseSpeed);
                    StartPauseBeforeMovement(_sequencePoints[2], 2);
                }
                else
                {
                    // Устанавливаем флаг завершения после точки 0
                    StartPauseBeforeMovement(_sequencePoints[0], 0);
                }
                break;

            case 2:
                StartPauseBeforeMovement(_sequencePoints[1], 1);
                _cyclesRemaining = Math.Max(0, _cyclesRemaining - 1);
                GD.Print($"Осталось циклов: {_cyclesRemaining}");
                break;
        }
    }

    private void StartPauseBeforeMovement(float target, int nextIndex)
    {
        // Отменяем предыдущие паузы
        CancelCurrentPause();

        _isAutoSequenceActive = true;
        SetMovementSpeed(nextIndex == 0 ? _fastSpeed : _baseSpeed);

        _isPaused = true;
        _pauseTimer = PauseDuration;

        GD.Print($"Начало паузы перед движением к точке {nextIndex}");
        AsyncPause(() =>
        {
            if (!_isAutoSequenceActive)
            {
                GD.Print("Пауза отменена: последовательность прервана");
                return;
            }

            if (nextIndex == -1)
            {
                _isAutoSequenceActive = false;
                EmitSignal(nameof(SequenceFinished)); // СИГНАЛ ЗДЕСЬ
                GD.Print("Последовательность завершена");
                return;
            }

            GD.Print($"Пауза завершена. Движение к цели: {target}");
            MoveToPosition(target);
            _currentTargetIndex = nextIndex;
        });
    }

    private void CancelCurrentPause()
    {
        _isPaused = false;
        _pauseTimer = 0;
        // Здесь должна быть логика отмены асинхронной паузы
    }

    private async void AsyncPause(Action callback)
    {
        try
        {
            while (_pauseTimer > 0 && _isPaused)
            {
                await ToSignal(GetTree().CreateTimer(1.0), "timeout");
                _pauseTimer -= 1f;

                // Явно вызываем обновление через делегат
                CallDeferred(nameof(UpdatePauseDelegate), _pauseTimer);
                GD.Print($"Пауза: {_pauseTimer} сек");
            }

            if (_isAutoSequenceActive)
            {
                _isPaused = false;
                callback?.Invoke();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Ошибка в AsyncPause: {ex}");
        }
    }

    private void UpdatePauseDelegate(float timer)
    {
        PauseUpdated?.Invoke(timer);
    }
    // Вспомогательная функция проверки позиции
    private bool IsAtTargetPosition()
    {
        return Mathf.Abs(PositionXCM - TargetPosition) <= _stopThreshold;
    }

    /// <summary>
    /// Отправка команды движения
    /// </summary>
    private void SendMovementCommand(MovementDirection direction)
    {
        if (!CanMoveInDirection(direction))
        {
            GD.Print("Движение заблокировано: достигнута граница");
            return;
        }

        if (_uiController == null) return;

        string command = direction == MovementDirection.Right ? "f" : "b";
        _uiController.SendCommand(command);
    }
    /// <summary>
    /// Отправка команды остановки
    /// </summary>
    private void SendStopCommand()
    {
        if (_uiController == null) return;

        _uiController.SendCommand("s");
    }

    /// <summary>
    /// Остановка автоматического движения
    /// </summary>
    public void StopAutoMovement()
    {
        if (IsMovingToTarget)
        {
            GD.Print("Остановка автоматического движения");
            IsMovingToTarget = false;
            _isDecelerating = false;
            SendStopCommand();

            // Всегда уведомляем о завершении движения
            HandleMovementCompletion();
        }
    }
    /// <summary>
    /// Запуск движения к целевой позиции
    /// </summary>
    public void MoveToPosition(float target)
    {
        // Сохраняем цель даже при паузе
        TargetPosition = Mathf.Clamp(target, 0, MaxPositionXCM);

        if (!_isManualPaused)
        {
            IsMovingToTarget = true;
            _isDecelerating = false;

            GD.Print($"Новая цель: {TargetPosition:N1} см");
            var direction = TargetPosition > PositionXCM ?
                MovementDirection.Right :
                MovementDirection.Left;
            SendMovementCommand(direction);
        }
    }

    public void EmergencyStop()
    {
        _isManualPaused = false;
        _isPaused = false;

        if (_isAutoSequenceActive)
        {
            _isAutoSequenceActive = false;
            EmitSignal(nameof(SequenceFinished));
        }

        StopAutoMovement();
        ResetToBaseSpeed();
    }

    public void ResetToBaseSpeed()
    {
        if (!Mathf.IsEqualApprox(MaxSpeedCM, _baseSpeed))
        {
            MaxSpeedCM = _baseSpeed;
            // Обновляем коэффициенты движения
            _accelerationRate = MaxSpeedCM / AccelerationTime;
            _decelerationRate = MaxSpeedCM / DecelerationTime;
            _uiController?.SendSpeedCommand(MaxSpeedCM);
        }
    }

    /// <summary>
    /// Логика автоматического движения к цели
    /// </summary>
    private void HandleAutoMovement(float delta)
    {
        if (_isManualPaused) return;

        float distance = TargetPosition - PositionXCM;
        float direction = Mathf.Sign(distance);

        // Рассчитываем дистанцию замедления
        float decelerationDistance = CalculateDecelerationDistance();

        // Определяем, нужно ли начинать замедление
        bool shouldDecelerate = Mathf.Abs(distance) <= decelerationDistance;

        if (shouldDecelerate && !_isDecelerating)
        {
            _isDecelerating = true;
            SendStopCommand();
        }

        if (_isDecelerating)
        {
            CurrentSpeedCM = Mathf.MoveToward(
                CurrentSpeedCM,
                0f,
                _decelerationRate * delta
            );
        }
        else
        {
            // Разгон до максимальной скорости
            CurrentSpeedCM = Mathf.MoveToward(
                CurrentSpeedCM,
                MaxSpeedCM * direction,
                _accelerationRate * delta
            );
        }

        // Обновление позиции
        PositionXCM += CurrentSpeedCM * delta;

        // Коррекция позиции при приближении к цели
        if (_isDecelerating && Mathf.Abs(CurrentSpeedCM) < _stopThreshold)
        {
            PositionXCM = TargetPosition;
            StopAutoMovement();
        }
    }


    /// <summary>
    /// Вычисляет дистанцию, необходимую для замедления
    /// </summary>
    private float CalculateDecelerationDistance()
    {
        // Рассчитываем расстояние, необходимое для остановки
        // с текущей скорости при заданном замедлении
        float currentSpeed = Mathf.Abs(CurrentSpeedCM);
        return (currentSpeed * currentSpeed) / (2 * _decelerationRate);
    }

    // region Логика движения
    /// <summary>
    /// Обработка ввода и обновление позиции
    /// </summary>
    /// <param name="delta">Время в секундах с последнего кадра</param>

    private MovementDirection _currentDirection;
    private bool _isMoving;
    private void HandleMovement(float delta)
    {
        if (IsMovingToTarget)
        {
            HandleAutoMovement(delta);
            return;
        }

        float input = Input.GetActionStrength("Burner_right")
                    - Input.GetActionStrength("Burner_left");

        // Блокировка ввода у границ
        if (IsAtRightBound && input > 0)
        {
            input = 0;
            GD.Print("Достигнута правая граница!");
        }
        else if (IsAtLeftBound && input < 0)
        {
            input = 0;
            GD.Print("Достигнута левая граница!");
        }

        if (!Mathf.IsZeroApprox(input))
        {
            var newDirection = input > 0 ? MovementDirection.Right : MovementDirection.Left;

            if (newDirection != _currentDirection || !_isMoving)
            {
                // Проверка перед отправкой команды
                if (CanMoveInDirection(newDirection))
                {
                    _currentDirection = newDirection;
                    _isMoving = true;
                    MovementStarted?.Invoke(_currentDirection);
                }
                else
                {
                    input = 0;
                }
            }
        }
        else
        {
            if (_isMoving)
            {
                _isMoving = false;
                MovementStopped?.Invoke();
            }
        }

        if (_isMoving)
        {
            Accelerate(input, delta);
        }
        else
        {
            Decelerate(delta);
        }

        PositionXCM += CurrentSpeedCM * delta;
    }

    // Новый метод проверки возможности движения
    private bool CanMoveInDirection(MovementDirection direction)
    {
        return direction switch
        {
            MovementDirection.Left => !IsAtLeftBound,
            MovementDirection.Right => !IsAtRightBound,
            _ => true
        };
    }

    public event Action MovementStopped;
    public event Action<MovementDirection> MovementStarted;
    /// <summary>
    /// Ускорение в направлении ввода
    /// </summary>
    private void Accelerate(float input, float delta)
    {
        // Целевая скорость с учетом направления
        float targetSpeed = input * MaxSpeedCM;

        // Плавное изменение скорости с постоянным ускорением
        CurrentSpeedCM = Mathf.MoveToward(
            _currentSpeedCM,
            targetSpeed,
            _accelerationRate * delta
        );
    }

    /// <summary>
    /// Замедление до полной остановки
    /// </summary>
    private void Decelerate(float delta)
    {
        // Плавное снижение скорости с постоянным замедлением
        CurrentSpeedCM = Mathf.MoveToward(
            _currentSpeedCM,
            0f,
            _decelerationRate * delta
        );
    }
    // endregion

    // region Отрисовка
    public override void _Draw()
    {
        if (_grid?.GridArea.Size == Vector2.Zero) return;

        // 1. Рассчитываем размеры горелки в пикселях
        Vector2 burnerSize = new Vector2(
            RealWidthCM * _grid.PixelsPerCM_X,
            RealHeightCM * _grid.PixelsPerCM_Y
        );

        // 2. Корректируем позицию: PositionXCM - центр, burnerPos.X - левый край
        Vector2 burnerPos = new Vector2(
            _grid.GridArea.Position.X + (PositionXCM * _grid.PixelsPerCM_X) - burnerSize.X / 2,
            _grid.GridArea.Position.Y + _grid.GridArea.Size.Y - burnerSize.Y
        );

        // 3. Отрисовка горелки
        DrawRect(new Rect2(burnerPos, burnerSize), BurnerColor, true);
        DrawRect(new Rect2(burnerPos, burnerSize), Colors.White, false, 2);


        // Генерация точек пламени
        float flameHeight = burnerSize.Y * 0.7f;
        Vector2[] flamePoints = {
            burnerPos + new Vector2(burnerSize.X/2, -flameHeight/2),  // Верхняя центральная точка
            burnerPos + new Vector2(burnerSize.X/4, -flameHeight),     // Левая верхняя
            burnerPos + new Vector2(burnerSize.X*0.75f, -flameHeight)  // Правая верхняя
        };

        // Отрисовка треугольного пламени
        DrawColoredPolygon(flamePoints, new Color(1, 0, 0, 0.6f));
    }
    // endregion


    private void ConnectSlider()
    {
        _speedSlider.ValueChanged += OnSpeedChanged;
        UpdateSpeedDisplay(MaxSpeedCM);
    }

    private void OnSpeedChanged(double value)
    {
        MaxSpeedCM = (float)value;
        // Обновляем коэффициенты ускорения/торможения
        _accelerationRate = MaxSpeedCM / AccelerationTime;
        _decelerationRate = MaxSpeedCM / DecelerationTime;
        UpdateSpeedDisplay(MaxSpeedCM);
    }

    private void UpdateSpeedDisplay(float speed)
    {
        if (_speedLabel != null)
        {
            _speedLabel.Text = $"Скорость: {speed:N0} см/сек";
        }
    }

}