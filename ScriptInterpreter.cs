using Godot;
using System;
using System.Collections.Generic;
using System.Globalization; // Для InvariantCulture
using System.Linq;
using System.Text.RegularExpressions;

public partial class ScriptInterpreter : Node
{
    public enum InterpreterState { Idle, Running, Paused, Error }

    [Export] private CodeEdit _scriptInput; // Заменил TextEdit на CodeEdit (в 4.x удобнее, но TextEdit тоже ок)
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

    // Переменные для больших циклов
    private float _cyclePointA;
    private float _cyclePointB;
    private int _cycleCount;
    private int _currentCycleStep; // Шаг внутри цикла (0, 1, 2...)
    private float _cyclePause;
    private bool _isLargeCycle;

    public override void _Ready()
    {
        LocateBurner();
        InitializeCommandHandlers();

        if (_scriptInput != null)
            _scriptInput.Editable = true;

        if (_runButton != null) _runButton.Pressed += RunScript;
        if (_stopButton != null) _stopButton.Pressed += StopScript;
    }

    private void LocateBurner()
    {
        // Попытка найти горелку разными способами
        if (TargetBurner == null) TargetBurner = GetParent().GetNodeOrNull<Burner>("Burner");
        if (TargetBurner == null) TargetBurner = GetNodeOrNull<Burner>("../Burner");
        if (TargetBurner == null) TargetBurner = GetTree().Root.GetNodeOrNull<Burner>("Main/Burner");

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

        // 1. ОБРАБОТКА ПАУЗЫ
        if (_currentDelay > 0)
        {
            _currentDelay -= (float)delta;
            if (_statusLabel != null)
                _statusLabel.Text = $"Пауза: {_currentDelay:F1} сек";

            if (_currentDelay <= 0)
            {
                _currentDelay = 0;
                OnPauseFinished(); // Пауза закончилась, решаем что делать дальше
            }
            else
            {
                return; // Ждем окончания паузы
            }
        }

        // 2. ОБРАБОТКА ОЖИДАНИЯ ДВИЖЕНИЯ
        if (_waitingForMovement)
        {
            _movementWaitTimer -= (float)delta;

            // Проверяем статус горелки
            bool isMoving = TargetBurner != null && TargetBurner.IsMovingToTarget;

            if (!isMoving)
            {
                // Движение физически завершилось
                _waitingForMovement = false;
                _movementWaitTimer = 0f;
                OnMovementFinished(); // Движение закончилось, решаем что делать дальше
            }
            else if (_movementWaitTimer <= 0)
            {
                GD.PrintErr("Таймаут ожидания движения!");
                _state = InterpreterState.Error;
                if (_statusLabel != null) _statusLabel.Text = "Ошибка: Таймаут движения";
                _waitingForMovement = false;
                return;
            }
            else
            {
                return; // Ждем окончания движения
            }
        }

        // 3. ВЫПОЛНЕНИЕ СЛЕДУЮЩЕЙ КОМАНДЫ
        // Если мы не ждем паузу и не ждем движения — берем следующую команду
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
            GD.Print("Очередь команд пуста. Скрипт завершен.");
            return;
        }

        // Получаем команду, но не удаляем, пока не узнаем индекс строки (для подсветки)
        int currentCommandIndex = _commandCounter - _commandQueue.Count;
        string commandLine = _commandQueue.Dequeue();

        // Подсветка
        if (_executionIndexMap.TryGetValue(currentCommandIndex, out int originalLineIndex))
        {
            HighlightCurrentLine(originalLineIndex);
        }

        // Парсинг
        // Разбиваем по скобкам и запятым. 
        // ВАЖНО: Разделитель запятая используется для аргументов. Дроби должны быть через точку.
        var parts = Regex.Split(commandLine, @"\(|\)|,")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (parts.Length == 0) return; // Пустая строка

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

    // --- ЛОГИКА БОЛЬШИХ ЦИКЛОВ ---

    private void OnMovementFinished()
    {
        // Если мы в режиме "Большой цикл", то после движения всегда идет пауза
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            // Добавляем команду паузы в начало очереди (приоритетно)
            string pauseCmd = $"PAUSE({_cyclePause.ToString(CultureInfo.InvariantCulture)})";
            PushPriorityCommand(pauseCmd);
        }
    }

    private void OnPauseFinished()
    {
        // Если мы в режиме "Большой цикл", то после паузы идет следующее движение
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            _currentCycleStep++;
            // Цикл состоит из 2 шагов движения (А и Б). Всего шагов = кол-во циклов * 2.
            int totalSteps = _cycleCount * 2;

            if (_currentCycleStep >= totalSteps)
            {
                GD.Print($"Большой цикл завершен ({_cycleCount} итераций)");
                _isLargeCycle = false; // Выходим из режима цикла
                // Дальше интерпретатор пойдет по обычной очереди, если там что-то есть
                return;
            }

            // Определяем, куда ехать
            // Четные шаги (0, 2...) -> Едем в точку А (начало)
            // Нечетные шаги (1, 3...) -> Едем в точку Б (конец)
            // Но логика HandleCycle начинает движение с А.
            // Значит:
            // Шаг 0 (старт HandleCycle) -> Едем в А (уже выполнено)
            // Пауза.
            // Вызов OnPauseFinished. _currentCycleStep стал 1.
            // Шаг 1 -> Едем в Б.

            float target = (_currentCycleStep % 2 != 0) ? _cyclePointB : _cyclePointA;
            string moveCmd = $"GO({target.ToString(CultureInfo.InvariantCulture)})";

            PushPriorityCommand(moveCmd);
        }
    }

    // Метод добавления команды "без очереди" (для циклов)
    private void PushPriorityCommand(string cmd)
    {
        // В C# Queue нет PushFront.
        // Чтобы вставить вперед, создаем новую очередь.
        // Это не очень эффективно для гигантских очередей, но для скрипта на 100 строк — ок.
        var newQueue = new Queue<string>();
        newQueue.Enqueue(cmd); // Сначала новая команда

        foreach (var c in _commandQueue)
            newQueue.Enqueue(c); // Потом старые

        _commandQueue = newQueue;

        // Корректируем счетчик, чтобы не сломать подсветку
        // (Команды цикла не имеют привязки к строкам редактора)
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
            ["START"] = args => GD.Print("Start marker passed"),
            ["END"] = args => HandleEnd()
        };
    }

    private void HandleSpeed(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента скорости");
        float speed = ParseFloat(args[0]);

        TargetBurner?.SetMovementSpeed(speed);
        if (_statusLabel != null) _statusLabel.Text = $"Скорость: {speed} см/с";
    }

    private void HandleGo(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента позиции");
        float pos = ParseFloat(args[0]);

        TargetBurner?.MoveToPosition(pos);

        // Включаем режим ожидания
        _waitingForMovement = true;
        _movementWaitTimer = WaitTimeout;
        if (_statusLabel != null) _statusLabel.Text = $"Движение к {pos} см...";
    }

    private void HandlePause(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Нет аргумента времени");
        float seconds = ParseFloat(args[0]);

        // Просто выставляем переменную. Timer создавать НЕ НУЖНО.
        // _Process сам отсчитает время.
        _currentDelay = seconds;
        if (_statusLabel != null) _statusLabel.Text = $"Пауза: {seconds} сек";
    }

    private void HandleCycle(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Формат: CYCLE(PosA, PosB, Count, [Pause])");

        float posA = ParseFloat(args[0]);
        float posB = ParseFloat(args[1]);
        int count = (int)ParseFloat(args[2]); // ParseFloat безопаснее int.Parse
        float pause = 0.5f;
        if (args.Length >= 4) pause = ParseFloat(args[3]);

        if (count <= 0) return;

        UpdateGridPoints(posA, posB);

        // Инициализируем "Большой цикл"
        _cyclePointA = posA;
        _cyclePointB = posB;
        _cycleCount = count;
        _cyclePause = pause;
        _isLargeCycle = true;
        _currentCycleStep = 0; // Начинаем с 0

        GD.Print($"Старт цикла: {posA} <-> {posB}, {count} раз");

        // Генерируем ПЕРВУЮ команду движения. 
        // Остальные (Пауза -> Движение Б -> Пауза...) будут добавлены автоматически в _Process.
        PushPriorityCommand($"GO({posA.ToString(CultureInfo.InvariantCulture)})");
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

    // Безопасный парсинг дробных чисел (10.5 и 10)
    private float ParseFloat(string input)
    {
        // Заменяем запятую на точку на случай, если пользователь ошибся, 
        // НО только если это не сломает аргументы (здесь мы парсим уже отдельный аргумент)
        input = input.Replace(',', '.');

        if (float.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
        {
            return result;
        }
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

            // Если горелка ехала, ставим на паузу
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

            // Снимаем с паузы
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
            // Игнорируем пустые строки и комментарии
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#"))
                continue;

            _executionIndexMap[_commandCounter] = i; // Запоминаем номер строки для подсветки
            _commandQueue.Enqueue(line);
            _commandCounter++;
        }
    }

    private void UpdateGridPoints(float a, float b)
    {
        if (_grid == null) return;
        float[] pts = { a, b, -1 };
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

        // Используем встроенные методы CodeEdit/TextEdit, если это TextEdit, API немного другое
        // Для универсальности используем простой цвет фона, если поддерживается

        // Очистка старой подсветки (грубый метод для TextEdit - перерисовка)
        // В Godot 4 CodeEdit имеет спец методы set_line_background_color

        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));

        if (lineIndex >= 0 && lineIndex < _scriptInput.GetLineCount())
        {
            _scriptInput.SetLineBackgroundColor(lineIndex, new Color(0.2f, 0.6f, 1f, 0.25f));
            _scriptInput.SetLineAsCenterVisible(lineIndex); // Прокрутка к строке
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