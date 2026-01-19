using Godot;
using System;

public partial class Burner : Node2D
{
    // УБРАЛИ ссылки на Label и Slider. Burner теперь чистый исполнитель.
    public bool IsManualPaused => _isManualPaused;

    [Signal] public delegate void SequenceFinishedEventHandler();

    // UIController подписывается на это, но позицию читает сам в _Process
    public event Action<float> PauseUpdated;
    public event Action<Vector2> PositionChanged;
    public event Action<float> SpeedChanged;

    private UIController _uiController;

    // ФИЗИКА
    public Vector2 TargetPosition { get; private set; }
    public bool IsMovingToTarget { get; private set; }

    // Свойство для чтения скорости из UIController
    public float CurrentSpeedScalar => _currentVelocity.Length();

    private float _stopRadius = 1.0f;
    private Vector2 MaxPositionMM => _grid != null
        ? new Vector2(_grid.RealWorldWidthMM - RealWidthMM, _grid.RealWorldHeightMM - RealHeightMM)
        : new Vector2(100, 100);

    // Внутренние переменные
    private bool _isAutoSequenceActive;
    private int _currentTargetIndex;
    private int _cyclesRemaining;
    private Vector2[] _sequencePoints = new Vector2[3];
    private bool _isManualPaused;
    private float _baseSpeed;
    private float _fastSpeed = 300f;

    public bool IsAutoSequenceActive => _isAutoSequenceActive;
    public int CyclesRemaining => _cyclesRemaining;

    [ExportGroup("Размеры (мм)")]
    [Export] public float RealWidthMM { get; set; } = 100f;
    [Export] public float RealHeightMM { get; set; } = 72f;

    [ExportGroup("Внешний вид")]
    [Export] public Color BurnerColor { get; set; } = new Color(1, 0.5f, 0, 0.8f);

    [ExportGroup("Движение")]
    [Export] public float MaxSpeedMM { get; set; } = 100f;
    [Export] public float AccelerationTime = 0.25f;
    [Export] public float DecelerationTime = 0.25f;

    [ExportGroup("Связи")]
    [Export] private CoordinateGrid _grid;

    [Export] public float PauseDuration { get; set; } = 3.0f;
    private float _pauseTimer;
    private bool _isPaused;

    private float _accelerationRate;
    private float _decelerationRate;
    private Vector2 _currentVelocity; // Вектор скорости
    private Vector2 _positionMM;

    public Vector2 PositionMM
    {
        get => _positionMM;
        private set
        {
            float x = Mathf.Clamp(value.X, 0, MaxPositionMM.X);
            float y = Mathf.Clamp(value.Y, 0, MaxPositionMM.Y);
            Vector2 clamped = new Vector2(x, y);

            if (!_positionMM.IsEqualApprox(clamped))
            {
                _positionMM = clamped;
                PositionChanged?.Invoke(_positionMM);
                QueueRedraw();
            }
        }
    }

    public override void _Ready()
    {
        AddToGroup("burners");
        if (_grid == null) _grid = GetParent().GetNodeOrNull<CoordinateGrid>("CoordinateGrid");
        _uiController = GetNodeOrNull<UIController>("../UIController");
        CalculatePhysicsRates();
    }

    public void SetMovementSpeed(float speed)
    {
        if (Mathf.IsEqualApprox(MaxSpeedMM, speed)) return;
        MaxSpeedMM = speed;
        CalculatePhysicsRates();
        SpeedChanged?.Invoke(MaxSpeedMM);
        // Не вызываем методы UIController отсюда, чтобы избежать цикличности
    }

    private void CalculatePhysicsRates()
    {
        _accelerationRate = AccelerationTime > 0 ? MaxSpeedMM / AccelerationTime : MaxSpeedMM * 10;
        _decelerationRate = DecelerationTime > 0 ? MaxSpeedMM / DecelerationTime : MaxSpeedMM * 10;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_isPaused && _isAutoSequenceActive && !_isManualPaused)
        {
            _pauseTimer -= dt;
            PauseUpdated?.Invoke(Mathf.Max(0, _pauseTimer));
            if (_pauseTimer <= 0) CompletePause();
            return;
        }

        HandlePhysics(dt);
        // Убрали UpdateUI() - этим занимается UIController
    }

    // --- ФИЗИКА (Та самая, которая работает правильно) ---
    private void HandlePhysics(float delta)
    {
        if (_isManualPaused) return;

        Vector2 targetVelocity = Vector2.Zero;
        float currentRate = _decelerationRate;

        if (IsMovingToTarget)
        {
            Vector2 diff = TargetPosition - PositionMM;
            float dist = diff.Length();

            if (dist < _stopRadius)
            {
                PositionMM = TargetPosition;
                _currentVelocity = Vector2.Zero;
                StopAutoMovement();
                return;
            }

            float maxPermittedSpeed = Mathf.Sqrt(2 * _decelerationRate * dist);
            float targetSpeed = Mathf.Min(MaxSpeedMM, maxPermittedSpeed);

            targetVelocity = diff.Normalized() * targetSpeed;
            currentRate = targetSpeed < MaxSpeedMM ? _decelerationRate : _accelerationRate;
        }
        else if (!IsAutoSequenceActive)
        {
            float inputX = Input.GetAxis("Burner_left", "Burner_right");
            Vector2 inputDir = new Vector2(inputX, 0).Normalized();

            if (inputDir != Vector2.Zero)
            {
                targetVelocity = inputDir * MaxSpeedMM;
                currentRate = _accelerationRate;
            }
            else
            {
                targetVelocity = Vector2.Zero;
                currentRate = _decelerationRate;
            }
        }

        _currentVelocity = _currentVelocity.MoveToward(targetVelocity, currentRate * delta);
        PositionMM += _currentVelocity * delta;
    }

    // --- УПРАВЛЕНИЕ ---
    public void MoveToPosition(Vector2 target)
    {
        float x = Mathf.Clamp(target.X, 0, MaxPositionMM.X);
        float y = Mathf.Clamp(target.Y, 0, MaxPositionMM.Y);
        TargetPosition = new Vector2(x, y);

        if (!_isManualPaused)
        {
            IsMovingToTarget = true;
            _uiController?.SendCommand(TargetPosition.X > PositionMM.X ? "f" : "b");
        }
    }

    public void StopAutoMovement()
    {
        if (IsMovingToTarget)
        {
            IsMovingToTarget = false;
            _uiController?.SendCommand("s");
            if (_isAutoSequenceActive) HandleMovementCompletion();
        }
    }

    // --- АВТОМАТИЗАЦИЯ ---
    public void StartAutoSequence(Vector2[] points, int cycles)
    {
        ResetSequenceState();
        if (points.Length < 3) return;

        _sequencePoints = points;
        _cyclesRemaining = cycles;
        _baseSpeed = MaxSpeedMM;
        _isAutoSequenceActive = true;

        float distToP0 = PositionMM.DistanceTo(_sequencePoints[0]);

        if (distToP0 < _stopRadius)
        {
            GD.Print("Уже в точке 0.");
            _currentTargetIndex = 1;
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[1]);
        }
        else
        {
            _currentTargetIndex = 0;
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[0]);
        }
    }

    private void HandleMovementCompletion()
    {
        if (_isPaused) return;

        switch (_currentTargetIndex)
        {
            case 0:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_fastSpeed);
                    _currentTargetIndex = 1;
                    MoveToPosition(_sequencePoints[1]);
                }
                else
                {
                    _isAutoSequenceActive = false;
                    EmitSignal(nameof(SequenceFinished));
                }
                break;

            case 1:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_baseSpeed);
                    StartPauseAndMove(_sequencePoints[2], 2);
                }
                else
                {
                    StartPauseAndMove(_sequencePoints[0], 0);
                }
                break;

            case 2:
                _cyclesRemaining--;
                StartPauseAndMove(_sequencePoints[1], 1);
                break;
        }
    }

    private Vector2 _nextAfterPauseTarget;
    private int _nextAfterPauseIndex;

    private void StartPauseAndMove(Vector2 nextTarget, int nextIndex)
    {
        _isPaused = true;
        _pauseTimer = PauseDuration;
        _nextAfterPauseTarget = nextTarget;
        _nextAfterPauseIndex = nextIndex;
    }

    private void CompletePause()
    {
        _isPaused = false;
        if (!_isAutoSequenceActive) return;
        _currentTargetIndex = _nextAfterPauseIndex;
        MoveToPosition(_nextAfterPauseTarget);
    }

    public void ResetSequenceState()
    {
        _isAutoSequenceActive = false;
        IsMovingToTarget = false;
        _isPaused = false;
        _isManualPaused = false;
        _uiController?.SendCommand("s");
    }

    public void SetManualPause(bool state)
    {
        _isManualPaused = state;
        if (state) _uiController?.SendCommand("s");
        else if (IsMovingToTarget) MoveToPosition(TargetPosition);
    }

    public void EmergencyStop()
    {
        ResetSequenceState();
        SetMovementSpeed(100f);
    }

    // --- ОТРИСОВКА ---
    public override void _Draw()
    {
        if (_grid?.GridArea.Size == Vector2.Zero) return;

        Vector2 sizePx = new Vector2(RealWidthMM * _grid.PixelsPerMM_X, RealHeightMM * _grid.PixelsPerMM_Y);
        float x = _grid.GridArea.Position.X + (PositionMM.X * _grid.PixelsPerMM_X) - sizePx.X / 2;
        float y = _grid.GridArea.Position.Y + (_grid.RealWorldHeightMM - PositionMM.Y) * _grid.PixelsPerMM_Y - sizePx.Y;
        Vector2 pos = new Vector2(x, y);

        DrawRect(new Rect2(pos, sizePx), BurnerColor, true);
        DrawRect(new Rect2(pos, sizePx), Colors.White, false, 2);

        float flameH = sizePx.Y * 0.7f;
        Vector2[] flame = {
            pos + new Vector2(sizePx.X/2, sizePx.Y + flameH),
            pos + new Vector2(sizePx.X/4, sizePx.Y),
            pos + new Vector2(sizePx.X*0.75f, sizePx.Y)
        };
        DrawColoredPolygon(flame, new Color(1, 0, 0, 0.6f));
    }
}