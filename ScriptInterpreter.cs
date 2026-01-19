using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

public partial class ScriptInterpreter : Node
{
    public enum InterpreterState { Idle, Running, Paused, Error }

    [Export] private CodeEdit _scriptInput;
    [Export] private Button _runButton;
    [Export] private Button _stopButton;
    [Export] private Label _statusLabel;
    [Export] private CoordinateGrid _grid;
    [Export] public Burner TargetBurner { get; set; }

    private InterpreterState _state = InterpreterState.Idle;
    private Queue<string> _commandQueue = new Queue<string>();
    private Dictionary<string, Action<string[]>> _commandHandlers;

    // Состояние выполнения
    private float _currentDelay = 0;
    private bool _waitingForMovement = false;
    private float _movementWaitTimer = 0f;
    private const float WaitTimeout = 60f;

    // Подсветка и маппинг строк
    private int _lastHighlightedLine = -1;
    private readonly Dictionary<int, int> _executionIndexMap = new Dictionary<int, int>();
    private int _commandCounter = 0;
    private bool _movementWasPaused;

    // Переменные для циклов (Теперь Vector2!)
    private Vector2 _cyclePointA;
    private Vector2 _cyclePointB;
    private int _cycleCount;
    private int _currentCycleStep;
    private float _cyclePause;
    private bool _isLargeCycle;

    public override void _Ready()
    {
        LocateBurner();
        InitializeCommandHandlers();

        if (_scriptInput != null) _scriptInput.Editable = true;
        if (_runButton != null) _runButton.Pressed += RunScript;
        if (_stopButton != null) _stopButton.Pressed += StopScript;
    }

    private void LocateBurner()
    {
        if (TargetBurner == null) TargetBurner = GetParent().GetNodeOrNull<Burner>("Burner");
        if (TargetBurner == null) TargetBurner = GetNodeOrNull<Burner>("../Burner");

        if (TargetBurner == null)
        {
            var nodes = GetTree().GetNodesInGroup("burners");
            if (nodes.Count > 0 && nodes[0] is Burner burner) TargetBurner = burner;
        }

        if (TargetBurner == null)
        {
            GD.PrintErr("ScriptInterpreter: Burner not found!");
            if (_statusLabel != null) _statusLabel.Text = "Ошибка: Горелка не найдена";
            if (_runButton != null) _runButton.Disabled = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_state != InterpreterState.Running) return;

        // 1. ПАУЗА
        if (_currentDelay > 0)
        {
            _currentDelay -= (float)delta;
            if (_statusLabel != null) _statusLabel.Text = $"Пауза: {_currentDelay:F1} сек";

            if (_currentDelay <= 0)
            {
                _currentDelay = 0;
                OnPauseFinished();
            }
            return;
        }

        // 2. ОЖИДАНИЕ ДВИЖЕНИЯ
        if (_waitingForMovement)
        {
            _movementWaitTimer -= (float)delta;
            bool isMoving = TargetBurner != null && TargetBurner.IsMovingToTarget;

            if (!isMoving)
            {
                _waitingForMovement = false;
                _movementWaitTimer = 0f;
                OnMovementFinished();
            }
            else if (_movementWaitTimer <= 0)
            {
                GD.PrintErr("Таймаут ожидания движения!");
                _state = InterpreterState.Error;
                if (_statusLabel != null) _statusLabel.Text = "Ошибка: Таймаут движения";
                _waitingForMovement = false;
                return;
            }
            return;
        }

        // 3. СЛЕДУЮЩАЯ КОМАНДА
        ExecuteNextCommand();
    }

    private void ExecuteNextCommand()
    {
        if (_state == InterpreterState.Paused) return;

        if (_commandQueue.Count == 0)
        {
            _state = InterpreterState.Idle;
            if (_statusLabel != null) _statusLabel.Text = "Скрипт завершен";
            ToggleButtons(false);
            ClearHighlighting();
            return;
        }

        int currentCommandIndex = _commandCounter - _commandQueue.Count;
        string commandLine = _commandQueue.Dequeue();

        if (_executionIndexMap.TryGetValue(currentCommandIndex, out int originalLineIndex))
        {
            HighlightCurrentLine(originalLineIndex);
        }

        // Парсинг аргументов
        var parts = Regex.Split(commandLine, @"\(|\)|,")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (parts.Length == 0) return;

        var command = parts[0].ToUpperInvariant();
        var args = parts.Skip(1).ToArray();

        if (_commandHandlers.ContainsKey(command))
        {
            try
            {
                _commandHandlers[command](args);
            }
            catch (Exception ex)
            {
                _state = InterpreterState.Error;
                if (_statusLabel != null) _statusLabel.Text = $"Ошибка: {ex.Message}";
                GD.PrintErr($"Ошибка команды '{command}': {ex}");
            }
        }
        else
        {
            GD.PrintErr($"Неизвестная команда: {command}");
        }
    }

    // --- ЛОГИКА ЦИКЛОВ ---

    private void OnMovementFinished()
    {
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            string pauseCmd = $"PAUSE({_cyclePause.ToString(CultureInfo.InvariantCulture)})";
            PushPriorityCommand(pauseCmd);
        }
    }

    private void OnPauseFinished()
    {
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            _currentCycleStep++;
            int totalSteps = _cycleCount * 2;

            if (_currentCycleStep >= totalSteps)
            {
                GD.Print($"Большой цикл завершен ({_cycleCount} итераций)");
                _isLargeCycle = false;
                return;
            }

            // Выбираем цель (вектор)
            Vector2 target = (_currentCycleStep % 2 != 0) ? _cyclePointB : _cyclePointA;

            // Формируем команду GO с двумя аргументами (X, Y)
            string moveCmd = $"GO({target.X.ToString(CultureInfo.InvariantCulture)}, {target.Y.ToString(CultureInfo.InvariantCulture)})";
            PushPriorityCommand(moveCmd);
        }
    }

    private void PushPriorityCommand(string cmd)
    {
        var newQueue = new Queue<string>();
        newQueue.Enqueue(cmd);
        foreach (var c in _commandQueue) newQueue.Enqueue(c);
        _commandQueue = newQueue;
        _commandCounter++;
    }

    // --- ОБРАБОТЧИКИ КОМАНД ---

    private void InitializeCommandHandlers()
    {
        _commandHandlers = new Dictionary<string, Action<string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPEED"] = args => HandleSpeed(args),
            ["GO"] = args => HandleGo(args),
            ["CYCLE"] = args => HandleCycle(args),
            ["PAUSE"] = args => HandlePause(args),
            ["START"] = args => { },
            ["END"] = args => HandleEnd()
        };
    }

    private void HandleSpeed(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента скорости");
        float speed = ParseFloat(args[0]);
        TargetBurner?.SetMovementSpeed(speed);
        if (_statusLabel != null) _statusLabel.Text = $"Скорость: {speed} мм/с";
    }

    private void HandleGo(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента позиции");

        float x = ParseFloat(args[0]);
        float y = 0; // По умолчанию Y = 0

        // Если есть второй аргумент, читаем Y
        if (args.Length > 1) y = ParseFloat(args[1]);

        // ИЗМЕНЕНИЕ: Передаем Vector2
        TargetBurner?.MoveToPosition(new Vector2(x, y));

        _waitingForMovement = true;
        _movementWaitTimer = WaitTimeout;
        if (_statusLabel != null) _statusLabel.Text = $"Движение к {x:F0}:{y:F0} мм...";
    }

    private void HandlePause(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента времени");
        float seconds = ParseFloat(args[0]);
        _currentDelay = seconds;
        if (_statusLabel != null) _statusLabel.Text = $"Пауза: {seconds} сек";
    }

    private void HandleCycle(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Формат: CYCLE(PosA, PosB, Count, [Pause])");

        // Читаем X координаты для цикла (Y пока считаем 0 для простоты линейного цикла)
        // В будущем можно расширить до CYCLE(Ax, Ay, Bx, By...)
        float xA = ParseFloat(args[0]);
        float xB = ParseFloat(args[1]);
        int count = (int)ParseFloat(args[2]);
        float pause = 0.5f;
        if (args.Length >= 4) pause = ParseFloat(args[3]);

        if (count <= 0) return;

        // Создаем векторы
        Vector2 vecA = new Vector2(xA, 0);
        Vector2 vecB = new Vector2(xB, 0);

        UpdateGridPoints(vecA, vecB);

        _cyclePointA = vecA;
        _cyclePointB = vecB;
        _cycleCount = count;
        _cyclePause = pause;
        _isLargeCycle = true;
        _currentCycleStep = 0;

        GD.Print($"Старт цикла: {vecA} <-> {vecB}, {count} раз");

        // Запуск первого шага
        string startCmd = $"GO({vecA.X.ToString(CultureInfo.InvariantCulture)}, {vecA.Y.ToString(CultureInfo.InvariantCulture)})";
        PushPriorityCommand(startCmd);
    }

    private void HandleEnd()
    {
        _commandQueue.Clear();
        _isLargeCycle = false;
        _state = InterpreterState.Idle;
        if (_statusLabel != null) _statusLabel.Text = "Выполнено";
        ToggleButtons(false);
        ClearHighlighting();
    }

    // --- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ---

    private float ParseFloat(string input)
    {
        input = input.Replace(',', '.');
        if (float.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
            return result;
        throw new FormatException($"Неверный формат числа: '{input}'");
    }

    private void RunScript()
    {
        if (_state == InterpreterState.Running) return;
        if (_scriptInput == null) return;

        ClearHighlighting();
        ParseScriptText(_scriptInput.Text);

        _state = InterpreterState.Running;
        ToggleButtons(true);
        if (_statusLabel != null) _statusLabel.Text = "Запуск...";
    }

    private void StopScript()
    {
        if (_state == InterpreterState.Running)
        {
            _state = InterpreterState.Paused;
            if (_statusLabel != null) _statusLabel.Text = "Пауза (Польз.)";
            if (TargetBurner != null && !TargetBurner.IsManualPaused)
            {
                TargetBurner.SetManualPause(true);
                _movementWasPaused = true;
            }
        }
        else if (_state == InterpreterState.Paused)
        {
            _state = InterpreterState.Running;
            if (_statusLabel != null) _statusLabel.Text = "Продолжение...";
            if (TargetBurner != null && _movementWasPaused)
            {
                TargetBurner.SetManualPause(false);
                _movementWasPaused = false;
            }
        }
    }

    private void ParseScriptText(string script)
    {
        _commandQueue.Clear();
        _executionIndexMap.Clear();
        _commandCounter = 0;
        _isLargeCycle = false;

        var lines = script.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#"))
                continue;

            _executionIndexMap[_commandCounter] = i;
            _commandQueue.Enqueue(line);
            _commandCounter++;
        }
    }

    private void UpdateGridPoints(Vector2 a, Vector2 b)
    {
        if (_grid == null) return;
        // Передаем массив векторов
        Vector2[] pts = { a, b, Vector2.Zero };
        Color[] cols = { Colors.Yellow, Colors.Orange, Colors.Transparent };
        _grid.UpdatePoints(pts, cols);
    }

    private void ToggleButtons(bool isRunning)
    {
        if (_runButton != null) _runButton.Disabled = isRunning;
        if (_stopButton != null) _stopButton.Disabled = !isRunning;
    }

    private void HighlightCurrentLine(int lineIndex)
    {
        if (_scriptInput == null) return;
        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));

        if (lineIndex >= 0 && lineIndex < _scriptInput.GetLineCount())
        {
            _scriptInput.SetLineBackgroundColor(lineIndex, new Color(0.2f, 0.6f, 1f, 0.25f));
            _scriptInput.SetLineAsCenterVisible(lineIndex);
            _lastHighlightedLine = lineIndex;
        }
    }

    private void ClearHighlighting()
    {
        if (_scriptInput == null) return;
        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));
        _lastHighlightedLine = -1;
    }
}